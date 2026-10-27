using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace ExitBoost.Core.Routing;

/// <summary>Удалённый UDP endpoint, сопоставленный WinDivert с PID процесса.</summary>
public sealed record UdpFlowInfo(int ProcessId, string RemoteAddress, int LocalPort, int RemotePort, DateTime SeenUtc);
public sealed record UdpFlowHealth(int ProcessId, string RemoteAddress, int LocalPort, int RemotePort,
    int OutboundPackets, int InboundPackets, DateTime FirstOutboundUtc, DateTime LastOutboundUtc);

/// <summary>
/// Слушает WinDivert FLOW-события. FLOW сообщает 5-tuple UDP-соединения и PID,
/// не перехватывая и не изменяя пакеты. Записи хранятся только в памяти.
/// </summary>
public sealed class WinDivertFlowMonitor : IDisposable
{
    private const int FlowLayer = 2;
    private const int NetworkLayer = 0;
    private const int FlowEstablished = 1;
    private const int FlowDeleted = 2;
    private const int UdpProtocol = 17;
    private const ulong SniffAndReceiveOnly = 0x0001 | 0x0004;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private readonly string _engineDirectory;
    private readonly ConcurrentDictionary<ulong, UdpFlowInfo> _flows = new();
    private readonly ConcurrentDictionary<string, UdpFlowInfo> _flowsByTuple = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UdpPacketHistory> _packetHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private IntPtr _library;
    private IntPtr _handle;
    private IntPtr _networkHandle;
    private OpenDelegate? _open;
    private ReceiveDelegate? _receive;
    private CloseDelegate? _close;
    private ShutdownDelegate? _shutdown;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Task? _networkWorker;
    private bool _disposed;

    public Action<string>? Log { get; set; }

    public bool IsListening
    {
        get { lock (_sync) return IsHandleValid(_handle) && _worker is { IsCompleted: false }; }
    }

    /// <summary>True when the passive UDP packet observer is active for request/reply analysis.</summary>
    public bool CanAnalyzeUdpReplies
    {
        get { lock (_sync) return IsHandleValid(_networkHandle) && _networkWorker is { IsCompleted: false }; }
    }

    public WinDivertFlowMonitor(string engineDirectory) => _engineDirectory = engineDirectory;

    /// <summary>Открывает FLOW listener один раз; повторный вызов безопасен.</summary>
    public Task<bool> StartAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsHandleValid(_handle) && _worker is { IsCompleted: false }) return Task.FromResult(true);
            if (IsHandleValid(_handle))
            {
                try { _close?.Invoke(_handle); } catch { }
                _handle = IntPtr.Zero;
                ReleaseLibrary();
            }

            string dll = Path.Combine(_engineDirectory, "WinDivert.dll");
            if (!File.Exists(dll))
            {
                Log?.Invoke("UDP-монитор: WinDivert не установлен; обновите zapret, чтобы включить обнаружение UDP-серверов");
                return Task.FromResult(false);
            }

            try
            {
                _library = NativeLibrary.Load(dll);
                _open = Load<OpenDelegate>("WinDivertOpen");
                _receive = Load<ReceiveDelegate>("WinDivertRecv");
                _close = Load<CloseDelegate>("WinDivertClose");
                _shutdown = Load<ShutdownDelegate>("WinDivertShutdown");

                // FLOW требует одновременно SNIFF и RECV_ONLY. События нельзя
                // блокировать, а сетевые пакеты этот handle вообще не получает.
                _handle = _open("true", FlowLayer, 0, SniffAndReceiveOnly);
                if (!IsHandleValid(_handle))
                {
                    int error = Marshal.GetLastPInvokeError();
                    _handle = IntPtr.Zero;
                    ReleaseLibrary();
                    Log?.Invoke($"UDP-монитор не запустился (WinDivert, код {error}). Проверьте права администратора и совместимость драйвера.");
                    return Task.FromResult(false);
                }

                _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _worker = Task.Run(() => ReceiveLoop(_stop.Token));
                _networkHandle = _open("udp", NetworkLayer, 0, SniffAndReceiveOnly);
                if (IsHandleValid(_networkHandle))
                    _networkWorker = Task.Run(() => ReceiveNetworkLoop(_stop.Token));
                else
                    Log?.Invoke($"UDP-монитор: не удалось запустить пассивный анализ ответов (WinDivert, код {Marshal.GetLastPInvokeError()}); авто-fallback UDP недоступен");
                Log?.Invoke("Монитор игр запущен: сопоставление UDP-потоков с PID и пассивный учёт пакетов запрос/ответ (содержимое не читается)");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _handle = IntPtr.Zero;
                ReleaseLibrary();
                Log?.Invoke("UDP-монитор не запустился: " + ex.Message);
                return Task.FromResult(false);
            }
        }
    }

    /// <summary>Активные UDP endpoints выбранного процесса, свежие за последние 10 минут.</summary>
    public IReadOnlyList<UdpFlowInfo> GetUdpFlows(int processId)
    {
        PruneExpired();
        return _flows.Values
            .Where(f => f.ProcessId == processId && DateTime.UtcNow - f.SeenUtc <= Retention)
            .GroupBy(f => (f.RemoteAddress, f.LocalPort, f.RemotePort))
            .Select(g => g.OrderByDescending(f => f.SeenUtc).First())
            .OrderByDescending(f => f.SeenUtc)
            .ToList();
    }

    /// <summary>
    /// UDP-потоки выбранного процесса с повторными исходящими пакетами и без
    /// единого ответа в окне наблюдения. Это эвристика недоступности, не анализ payload.
    /// </summary>
    public IReadOnlyList<UdpFlowHealth> GetUnansweredUdpFlows(int processId, TimeSpan? window = null)
    {
        if (!CanAnalyzeUdpReplies) return [];
        DateTime now = DateTime.UtcNow;
        TimeSpan interval = window ?? TimeSpan.FromSeconds(12);
        var result = new List<UdpFlowHealth>();
        foreach (var flow in GetUdpFlows(processId))
        {
            string key = TupleKey(flow.RemoteAddress, flow.LocalPort, flow.RemotePort);
            if (!_packetHistory.TryGetValue(key, out var history)) continue;
            var snapshot = history.Snapshot(now - interval);
            if (snapshot.OutboundPackets >= 5 && snapshot.InboundPackets == 0
                && snapshot.FirstOutboundUtc != default
                && now - snapshot.FirstOutboundUtc >= TimeSpan.FromSeconds(8))
                result.Add(new UdpFlowHealth(processId, flow.RemoteAddress, flow.LocalPort, flow.RemotePort,
                    snapshot.OutboundPackets, snapshot.InboundPackets, snapshot.FirstOutboundUtc, snapshot.LastOutboundUtc));
        }
        return result;
    }

    private void ReceiveLoop(CancellationToken ct)
    {
        var address = Marshal.AllocHGlobal(Marshal.SizeOf<DivertAddress>());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IntPtr handle;
                ReceiveDelegate? receive;
                lock (_sync) { handle = _handle; receive = _receive; }
                if (!IsHandleValid(handle) || receive is null) return;

                if (!receive(handle, IntPtr.Zero, 0, out _, address))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (!ct.IsCancellationRequested && error is not 0 and not 232 and not 995)
                        Log?.Invoke($"UDP-монитор остановил чтение (WinDivert, код {error})");
                    return;
                }

                var evt = Marshal.PtrToStructure<DivertAddress>(address);
                int eventType = (int)((evt.LayerEventFlags >> 8) & 0xff);
                var flow = evt.Flow;
                if (flow.Protocol != UdpProtocol || flow.ProcessId == 0) continue;

                if (eventType == FlowDeleted)
                {
                    if (_flows.TryRemove(flow.EndpointId, out var removed))
                    {
                        string key = TupleKey(removed.RemoteAddress, removed.LocalPort, removed.RemotePort);
                        _flowsByTuple.TryRemove(key, out _);
                        _packetHistory.TryRemove(key, out _);
                    }
                    continue;
                }
                if (eventType != FlowEstablished || flow.RemotePort == 0) continue;

                bool ipv6 = (evt.LayerEventFlags & (1u << 20)) != 0;
                string remote = FormatAddress(flow.RemoteAddr0, flow.RemoteAddr1, flow.RemoteAddr2, flow.RemoteAddr3, ipv6);
                if (string.IsNullOrWhiteSpace(remote) || remote == "::" || remote == "0.0.0.0") continue;

                var info = new UdpFlowInfo((int)flow.ProcessId, remote, flow.LocalPort, flow.RemotePort, DateTime.UtcNow);
                _flows[flow.EndpointId] = info;
                string tupleKey = TupleKey(remote, flow.LocalPort, flow.RemotePort);
                _flowsByTuple[tupleKey] = info;
                _packetHistory.GetOrAdd(tupleKey, _ => new UdpPacketHistory());
                if (_flows.Count > 4096) PruneExpired();
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Log?.Invoke("UDP-монитор: " + ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(address);
        }
    }

    private void ReceiveNetworkLoop(CancellationToken ct)
    {
        const int maxPacketSize = 65_535;
        var packet = Marshal.AllocHGlobal(maxPacketSize);
        var address = Marshal.AllocHGlobal(Marshal.SizeOf<DivertAddress>());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IntPtr handle;
                ReceiveDelegate? receive;
                lock (_sync) { handle = _networkHandle; receive = _receive; }
                if (!IsHandleValid(handle) || receive is null) return;

                if (!receive(handle, packet, maxPacketSize, out uint receivedLength, address))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (!ct.IsCancellationRequested && error is not 0 and not 232 and not 995)
                        Log?.Invoke($"UDP-анализатор остановил чтение пакетов (WinDivert, код {error})");
                    return;
                }

                var evt = Marshal.PtrToStructure<DivertAddress>(address);
                bool outbound = (evt.LayerEventFlags & (1u << 17)) != 0;
                if (!TryParseUdpPacket(packet, receivedLength, outbound, out var observation)) continue;

                string key = outbound
                    ? TupleKey(observation.RemoteAddress, observation.LocalPort, observation.RemotePort)
                    : TupleKey(observation.RemoteAddress, observation.LocalPort, observation.RemotePort);
                if (_flowsByTuple.ContainsKey(key)
                    && _packetHistory.TryGetValue(key, out var history))
                    history.Add(outbound, DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Log?.Invoke("UDP-анализатор: " + ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(packet);
            Marshal.FreeHGlobal(address);
        }
    }

    private bool TryParseUdpPacket(IntPtr packet, uint length, bool outbound, out UdpPacketObservation observation)
    {
        observation = default;
        if (length < 20) return false;
        int version = Marshal.ReadByte(packet) >> 4;
        int transportOffset;
        IPAddress source, destination;

        if (version == 4)
        {
            int headerLength = (Marshal.ReadByte(packet) & 0x0f) * 4;
            if (headerLength < 20 || length < headerLength + 8 || Marshal.ReadByte(packet, 9) != UdpProtocol) return false;
            int fragment = (Marshal.ReadByte(packet, 6) << 8) | Marshal.ReadByte(packet, 7);
            if ((fragment & 0x1fff) != 0) return false;
            byte[] src = new byte[4], dst = new byte[4];
            Marshal.Copy(packet + 12, src, 0, 4);
            Marshal.Copy(packet + 16, dst, 0, 4);
            source = new IPAddress(src);
            destination = new IPAddress(dst);
            transportOffset = headerLength;
        }
        else if (version == 6)
        {
            if (length < 48) return false;
            byte nextHeader = Marshal.ReadByte(packet, 6);
            transportOffset = 40;
            // Поддерживаем обычные IPv6-заголовки и типичные extension headers.
            int extensions = 0;
            while (nextHeader != UdpProtocol && extensions++ < 8)
            {
                if (nextHeader is not (0 or 43 or 44 or 60 or 51) || length < transportOffset + 2) return false;
                byte current = nextHeader;
                nextHeader = Marshal.ReadByte(packet, transportOffset);
                int extensionLength = current == 44 ? 8
                    : current == 51 ? (Marshal.ReadByte(packet, transportOffset + 1) + 2) * 4
                    : (Marshal.ReadByte(packet, transportOffset + 1) + 1) * 8;
                if (extensionLength <= 0 || length < transportOffset + extensionLength) return false;
                if (current == 44)
                {
                    int fragmentOffset = (Marshal.ReadByte(packet, transportOffset + 2) << 8) | Marshal.ReadByte(packet, transportOffset + 3);
                    if ((fragmentOffset & 0xfff8) != 0) return false;
                }
                transportOffset += extensionLength;
            }
            if (nextHeader != UdpProtocol || length < transportOffset + 8) return false;
            byte[] src = new byte[16], dst = new byte[16];
            Marshal.Copy(packet + 8, src, 0, 16);
            Marshal.Copy(packet + 24, dst, 0, 16);
            source = new IPAddress(src);
            destination = new IPAddress(dst);
        }
        else return false;

        int sourcePort = (Marshal.ReadByte(packet, transportOffset) << 8) | Marshal.ReadByte(packet, transportOffset + 1);
        int destinationPort = (Marshal.ReadByte(packet, transportOffset + 2) << 8) | Marshal.ReadByte(packet, transportOffset + 3);
        if (sourcePort == 0 || destinationPort == 0) return false;

        string remoteAddress = (outbound ? destination : source) is { IsIPv4MappedToIPv6: true } mapped
            ? mapped.MapToIPv4().ToString()
            : (outbound ? destination : source).ToString();
        observation = outbound
            ? new UdpPacketObservation(remoteAddress, sourcePort, destinationPort)
            : new UdpPacketObservation(remoteAddress, destinationPort, sourcePort);
        return true;
    }

    private static string TupleKey(string remoteAddress, int localPort, int remotePort) =>
        $"{remoteAddress}|{localPort}|{remotePort}";

    private readonly record struct UdpPacketObservation(string RemoteAddress, int LocalPort, int RemotePort);

    private sealed class UdpPacketHistory
    {
        private readonly object _gate = new();
        private int _outboundPackets;
        private DateTime _firstOutboundUtc;
        private DateTime _lastOutboundUtc;
        private DateTime _lastInboundUtc;

        public void Add(bool outbound, DateTime time)
        {
            lock (_gate)
            {
                if (!outbound)
                {
                    _lastInboundUtc = time;
                    _outboundPackets = 0;
                    _firstOutboundUtc = default;
                    return;
                }
                if (_outboundPackets == 0) _firstOutboundUtc = time;
                _outboundPackets = Math.Min(_outboundPackets + 1, 1_000_000);
                _lastOutboundUtc = time;
            }
        }

        public (int OutboundPackets, int InboundPackets, DateTime FirstOutboundUtc, DateTime LastOutboundUtc) Snapshot(DateTime cutoff)
        {
            lock (_gate)
            {
                if (_lastOutboundUtc < cutoff || _lastInboundUtc >= _firstOutboundUtc)
                    return (0, 0, default, default);
                return (_outboundPackets, 0, _firstOutboundUtc, _lastOutboundUtc);
            }
        }
    }

    private static string FormatAddress(uint a0, uint a1, uint a2, uint a3, bool ipv6)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!ipv6)
        {
            bytes[0] = (byte)a0;
            bytes[1] = (byte)(a0 >> 8);
            bytes[2] = (byte)(a0 >> 16);
            bytes[3] = (byte)(a0 >> 24);
            return new IPAddress(bytes[..4]).ToString();
        }

        BinaryPrimitives.WriteUInt32BigEndian(bytes[0..4], a0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[4..8], a1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[8..12], a2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[12..16], a3);
        var address = new IPAddress(bytes);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    private void PruneExpired()
    {
        DateTime cutoff = DateTime.UtcNow - Retention;
        foreach (var pair in _flows)
        {
            if (pair.Value.SeenUtc >= cutoff || !_flows.TryRemove(pair.Key, out var removed)) continue;
            string key = TupleKey(removed.RemoteAddress, removed.LocalPort, removed.RemotePort);
            _flowsByTuple.TryRemove(key, out _);
            _packetHistory.TryRemove(key, out _);
        }
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private static bool IsHandleValid(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    private void ReleaseLibrary()
    {
        if (_library != IntPtr.Zero)
        {
            try { NativeLibrary.Free(_library); } catch { }
            _library = IntPtr.Zero;
        }
        _open = null;
        _receive = null;
        _close = null;
        _shutdown = null;
    }

    public async Task StopAsync()
    {
        Task? worker, networkWorker;
        lock (_sync)
        {
            if (!IsHandleValid(_handle) && !IsHandleValid(_networkHandle)) return;
            _stop?.Cancel();
            if (IsHandleValid(_handle))
            {
                try { _shutdown?.Invoke(_handle, 1); } catch { } // WINDIVERT_SHUTDOWN_RECV
                try { _close?.Invoke(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
            if (IsHandleValid(_networkHandle))
            {
                try { _shutdown?.Invoke(_networkHandle, 1); } catch { }
                try { _close?.Invoke(_networkHandle); } catch { }
                _networkHandle = IntPtr.Zero;
            }
            worker = _worker;
            networkWorker = _networkWorker;
        }

        try { if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        try { if (networkWorker is not null) await networkWorker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        lock (_sync)
        {
            _stop?.Dispose();
            _stop = null;
            _worker = null;
            _networkWorker = null;
            _flows.Clear();
            _flowsByTuple.Clear();
            _packetHistory.Clear();
            ReleaseLibrary();
        }
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate IntPtr OpenDelegate([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ReceiveDelegate(IntPtr handle, IntPtr packet, uint packetLength, out uint receivedLength, IntPtr address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool CloseDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ShutdownDelegate(IntPtr handle, int how);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct FlowData
    {
        public ulong EndpointId;
        public ulong ParentEndpointId;
        public uint ProcessId;
        public uint LocalAddr0;
        public uint LocalAddr1;
        public uint LocalAddr2;
        public uint LocalAddr3;
        public uint RemoteAddr0;
        public uint RemoteAddr1;
        public uint RemoteAddr2;
        public uint RemoteAddr3;
        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct DivertAddress
    {
        [FieldOffset(0)] public long Timestamp;
        // UINT32 bitfield: Layer (bits 0..7), Event (bits 8..15), flags.
        [FieldOffset(8)] public uint LayerEventFlags;
        [FieldOffset(16)] public FlowData Flow;
    }
}

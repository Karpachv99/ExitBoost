using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace ExitBoost.Core.Routing;

/// <summary>Удалённый UDP endpoint, сопоставленный WinDivert с PID процесса.</summary>
public sealed record UdpFlowInfo(int ProcessId, string RemoteAddress, int LocalPort, int RemotePort, DateTime SeenUtc);

/// <summary>
/// Слушает WinDivert FLOW-события. FLOW сообщает 5-tuple UDP-соединения и PID,
/// не перехватывая и не изменяя пакеты. Записи хранятся только в памяти.
/// </summary>
public sealed class WinDivertFlowMonitor : IDisposable
{
    private const int FlowLayer = 2;
    private const int FlowEstablished = 1;
    private const int FlowDeleted = 2;
    private const int UdpProtocol = 17;
    private const ulong SniffAndReceiveOnly = 0x0001 | 0x0004;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private readonly string _engineDirectory;
    private readonly ConcurrentDictionary<ulong, UdpFlowInfo> _flows = new();
    private readonly object _sync = new();
    private IntPtr _library;
    private IntPtr _handle;
    private OpenDelegate? _open;
    private ReceiveDelegate? _receive;
    private CloseDelegate? _close;
    private ShutdownDelegate? _shutdown;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private bool _disposed;

    public Action<string>? Log { get; set; }

    public bool IsListening
    {
        get { lock (_sync) return IsHandleValid(_handle) && _worker is { IsCompleted: false }; }
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
                Log?.Invoke("UDP-монитор запущен: сбор адресов UDP-соединений по PID (без чтения содержимого трафика)");
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
                    _flows.TryRemove(flow.EndpointId, out _);
                    continue;
                }
                if (eventType != FlowEstablished || flow.RemotePort == 0) continue;

                bool ipv6 = (evt.LayerEventFlags & (1u << 20)) != 0;
                string remote = FormatAddress(flow.RemoteAddr0, flow.RemoteAddr1, flow.RemoteAddr2, flow.RemoteAddr3, ipv6);
                if (string.IsNullOrWhiteSpace(remote) || remote == "::" || remote == "0.0.0.0") continue;

                _flows[flow.EndpointId] = new UdpFlowInfo(
                    (int)flow.ProcessId, remote, flow.LocalPort, flow.RemotePort, DateTime.UtcNow);
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
            if (pair.Value.SeenUtc < cutoff) _flows.TryRemove(pair.Key, out _);
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
        Task? worker;
        lock (_sync)
        {
            if (!IsHandleValid(_handle)) return;
            _stop?.Cancel();
            if (IsHandleValid(_handle))
            {
                try { _shutdown?.Invoke(_handle, 1); } catch { } // WINDIVERT_SHUTDOWN_RECV
                try { _close?.Invoke(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
            worker = _worker;
        }

        try { if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        lock (_sync)
        {
            _stop?.Dispose();
            _stop = null;
            _worker = null;
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

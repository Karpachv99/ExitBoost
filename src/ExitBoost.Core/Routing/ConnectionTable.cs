using System.Runtime.InteropServices;

namespace ExitBoost.Core.Routing;

/// <summary>Локальное TCP/UDP-соединение.</summary>
public sealed record ConnectionInfo(string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string Protocol, string State, int Pid);

/// <summary>
/// Быстрое перечисление активных соединений через iphlpapi.
/// Нужно, чтобы автоматически определять адреса игровых серверов и заворачивать
/// только их трафик на ретранслятор.
/// </summary>
public static class ConnectionTable
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    private const int MIB_TCP_STATE_ESTABLISHED = 5;
    private const int MIB_TCP_STATE_SYN_SENT = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int outBufLen, bool sort, int ipVersion, int tcpClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr udpTable, ref int outBufLen, bool sort, int ipVersion, int udpClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6Row
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRow
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6Row
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    /// <summary>Все активные соединения системы.</summary>
    public static List<ConnectionInfo> GetAll()
    {
        var list = new List<ConnectionInfo>();
        try { list.AddRange(GetTcp(AF_INET)); } catch { }
        try { list.AddRange(GetTcp(AF_INET6)); } catch { }
        try { list.AddRange(GetUdp(AF_INET)); } catch { }
        try { list.AddRange(GetUdp(AF_INET6)); } catch { }
        return list;
    }

    /// <summary>Соединения указанного процесса.</summary>
    public static List<ConnectionInfo> GetByPid(int pid) =>
        GetAll().Where(c => c.Pid == pid).ToList();

    /// <summary>Уникальные удалённые IPv4-адреса указанного процесса.</summary>
    public static List<string> GetRemoteIpv4ByPid(int pid) =>
        GetByPid(pid)
            .Where(c => c.Protocol == "TCP" && c.RemoteAddress.Length > 0 && !c.RemoteAddress.Equals("0.0.0.0"))
            .Select(c => c.RemoteAddress)
            .Distinct()
            .ToList();

    private static List<ConnectionInfo> GetTcp(int ipVersion)
    {
        var result = new List<ConnectionInfo>();
        int size = 0;
        uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, ipVersion, TCP_TABLE_OWNER_PID_ALL, 0);
        if (code != ERROR_INSUFFICIENT_BUFFER) return result;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            code = GetExtendedTcpTable(buffer, ref size, false, ipVersion, TCP_TABLE_OWNER_PID_ALL, 0);
            if (code != 0) return result;

            if (ipVersion == AF_INET)
            {
                int rowSize = Marshal.SizeOf<MibTcpRow>();
                int count = ReadRowCount(buffer, size, rowSize);
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRow>(buffer + sizeof(uint) + i * rowSize);
                    if (row.State != MIB_TCP_STATE_ESTABLISHED && row.State != MIB_TCP_STATE_SYN_SENT) continue;
                    result.Add(new ConnectionInfo(
                        IpFromUInt(row.LocalAddr), (int)row.LocalPort,
                        IpFromUInt(row.RemoteAddr), (int)row.RemotePort,
                        "TCP", StateName(row.State), (int)row.OwningPid));
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<MibTcp6Row>();
                int count = ReadRowCount(buffer, size, rowSize);
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcp6Row>(buffer + sizeof(uint) + i * rowSize);
                    if (row.State != MIB_TCP_STATE_ESTABLISHED && row.State != MIB_TCP_STATE_SYN_SENT) continue;
                    result.Add(new ConnectionInfo(
                        Ip6FromBytes(row.LocalAddr), (int)row.LocalPort,
                        Ip6FromBytes(row.RemoteAddr), (int)row.RemotePort,
                        "TCP", StateName(row.State), (int)row.OwningPid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private static List<ConnectionInfo> GetUdp(int ipVersion)
    {
        var result = new List<ConnectionInfo>();
        int size = 0;
        uint code = GetExtendedUdpTable(IntPtr.Zero, ref size, false, ipVersion, UDP_TABLE_OWNER_PID, 0);
        if (code != ERROR_INSUFFICIENT_BUFFER) return result;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            code = GetExtendedUdpTable(buffer, ref size, false, ipVersion, UDP_TABLE_OWNER_PID, 0);
            if (code != 0) return result;

            if (ipVersion == AF_INET)
            {
                int rowSize = Marshal.SizeOf<MibUdpRow>();
                int count = ReadRowCount(buffer, size, rowSize);
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibUdpRow>(buffer + sizeof(uint) + i * rowSize);
                    result.Add(new ConnectionInfo(
                        IpFromUInt(row.LocalAddr), (int)row.LocalPort,
                        "", 0, "UDP", "", (int)row.OwningPid));
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<MibUdp6Row>();
                int count = ReadRowCount(buffer, size, rowSize);
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibUdp6Row>(buffer + sizeof(uint) + i * rowSize);
                    result.Add(new ConnectionInfo(
                        Ip6FromBytes(row.LocalAddr), (int)row.LocalPort,
                        "", 0, "UDP", "", (int)row.OwningPid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    // TCP_TABLE_OWNER_PID_* and UDP_TABLE_OWNER_PID_* start with a DWORD row
    // count. Skipping it is essential: treating it as row zero misaligns every
    // entry and makes the owning PID/remote address appear to be garbage.
    private static int ReadRowCount(IntPtr buffer, int bufferSize, int rowSize)
    {
        if (bufferSize < sizeof(uint)) return 0;
        uint count = unchecked((uint)Marshal.ReadInt32(buffer));
        long required = sizeof(uint) + (long)count * rowSize;
        return required <= bufferSize ? (int)count : 0;
    }

    private static string StateName(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RECV",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "UNKNOWN"
    };

    private static string IpFromUInt(uint value) =>
        $"{value & 0xFF}.{(value >> 8) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 24) & 0xFF}";

    private static string Ip6FromBytes(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 16) return "";
        var groups = new string[8];
        for (int i = 0; i < 8; i++)
            groups[i] = ((bytes[i * 2] << 8) | bytes[i * 2 + 1]).ToString("x");
        return string.Join(':', groups);
    }
}

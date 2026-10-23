namespace ExitBoost.Core.Models;

/// <summary>Тип транспорта до ретранслятора.</summary>
public enum NodeKind
{
    /// <summary>WireGuard — основной режим, минимальные задержки и накладные расходы.</summary>
    WireGuard,

    /// <summary>OpenVPN.</summary>
    OpenVpn,

    /// <summary>SSH dynamic SOCKS5-прокси (ограниченная поддержка, только для софта с поддержкой прокси).</summary>
    SshProxy
}

/// <summary>Состояние узла по результатам последних измерений.</summary>
public enum NodeState
{
    Unknown,
    Probing,
    Online,
    Degraded,
    Offline,
    Connected,
    Error
}

/// <summary>Состояние туннеля.</summary>
public enum TunnelState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Error
}

/// <summary>Режим маршрутизации трафика через узел.</summary>
public enum RoutingMode
{
    /// <summary>Весь трафик системы идёт через узел.</summary>
    FullTunnel,

    /// <summary>Через узел идёт только трафик указанных подсетей.</summary>
    NetworkSplit,

    /// <summary>Через узел идёт только трафик выбранных приложений.</summary>
    ProcessSplit
}

/// <summary>Качество канала до узла.</summary>
public enum ProbeQuality
{
    Unknown,
    Poor,
    Fair,
    Good,
    Excellent
}

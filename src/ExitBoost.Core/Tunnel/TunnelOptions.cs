using System.Text;
using ExitBoost.Core.Models;

namespace ExitBoost.Core.Tunnel;

/// <summary>Параметры подключения к узлу.</summary>
public sealed class TunnelOptions
{
    /// <summary>Режим маршрутизации.</summary>
    public RoutingMode Mode { get; set; } = RoutingMode.ProcessSplit;

    /// <summary>Маршруты (AllowedIPs), которые заворачиваются на узел.</summary>
    public List<string> AllowedIps { get; set; } = new();

    /// <summary>Подсети, которые нужно исключить из туннеля (трафик идёт напрямую).</summary>
    public List<string> BypassSubnets { get; set; } = new();

    public int Mtu { get; set; } = 1420;

    public string? Dns { get; set; }

    public bool DnsThroughTunnel { get; set; } = true;

    /// <summary>Разрешить входящие соединения из локальной сети.</summary>
    public bool AllowLan { get; set; } = true;

    /// <summary>Управлять DNS системы через туннель.</summary>
    public bool ManageDns { get; set; } = true;

    public TunnelOptions Clone() => new()
    {
        Mode = Mode,
        AllowedIps = new List<string>(AllowedIps),
        BypassSubnets = new List<string>(BypassSubnets),
        Mtu = Mtu,
        Dns = Dns,
        DnsThroughTunnel = DnsThroughTunnel,
        AllowLan = AllowLan,
        ManageDns = ManageDns
    };

    /// <summary>Текстовый вид AllowedIPs для конфига WireGuard.</summary>
    // An empty split list must mean "no routed destinations", not a silent full tunnel.
    // FullTunnel is explicit and normally already carries 0.0.0.0/0 from the route builder.
    public string AllowedIpsString => AllowedIps.Count == 0
        ? (Mode == RoutingMode.FullTunnel ? "0.0.0.0/0" : "")
        : string.Join(", ", AllowedIps);

    public override string ToString() => $"{Mode}: {AllowedIpsString}";
}

/// <summary>Результат попытки подключения.</summary>
public readonly record struct TunnelResult(bool Success, string Message, string? InterfaceAlias, IReadOnlyList<string> Routes)
{
    public static TunnelResult Fail(string message) => new(false, message, null, Array.Empty<string>());
    public static TunnelResult Ok(string message, string? alias, IReadOnlyList<string> routes) => new(true, message, alias, routes);
}

/// <summary>Абстракция транспорта до ретранслятора.</summary>
public interface ITunnelBackend : IDisposable
{
    string Name { get; }

    /// <summary>Доступен ли бэкенд в системе (найден нужный исполняемый файл).</summary>
    bool IsAvailable { get; }

    string? ErrorMessage { get; }

    TunnelState State { get; }

    /// <summary>Имя сетевого интерфейса, созданного туннелем.</summary>
    string? InterfaceAlias { get; }

    /// <summary>Локальные адреса интерфейса туннеля.</summary>
    IReadOnlyList<string> InterfaceAddresses { get; }

    event Action<string>? Log;

    Task<TunnelResult> ConnectAsync(Core.Models.NodeConfig node, TunnelOptions options, CancellationToken ct = default);

    Task DisconnectAsync();
}

/// <summary>Генератор конфигурации WireGuard.</summary>
public static class WireGuardConfigBuilder
{
    public static string Build(Core.Models.NodeConfig node, TunnelOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"PrivateKey = {node.PrivateKey}");
        if (!string.IsNullOrWhiteSpace(node.Address)) sb.AppendLine($"Address = {node.Address}");
        if (options.ManageDns && !string.IsNullOrWhiteSpace(options.Dns))
            sb.AppendLine($"DNS = {options.Dns}");
        sb.AppendLine($"MTU = {options.Mtu}");
        sb.AppendLine();

        sb.AppendLine("[Peer]");
        sb.AppendLine($"PublicKey = {node.PublicKey}");
        if (!string.IsNullOrWhiteSpace(node.PresharedKey))
            sb.AppendLine($"PresharedKey = {node.PresharedKey}");
        if (!string.IsNullOrWhiteSpace(options.AllowedIpsString))
            if (!string.IsNullOrWhiteSpace(options.AllowedIpsString))
            sb.AppendLine($"AllowedIPs = {options.AllowedIpsString}");
        sb.AppendLine($"Endpoint = {node.Host}:{node.Port}");
        if (node.PersistentKeepalive > 0)
            sb.AppendLine($"PersistentKeepalive = {node.PersistentKeepalive}");
        return sb.ToString();
    }

    /// <summary>
    /// Минимальный конфиг для wg syncconf/setconf (без Address/DNS/MTU —
    /// эти поля не принимаются утилитой wg).
    /// </summary>
    public static string BuildSyncConf(Core.Models.NodeConfig node, TunnelOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"PrivateKey = {node.PrivateKey}");
        sb.AppendLine();
        sb.AppendLine("[Peer]");
        sb.AppendLine($"PublicKey = {node.PublicKey}");
        if (!string.IsNullOrWhiteSpace(node.PresharedKey))
            sb.AppendLine($"PresharedKey = {node.PresharedKey}");
        sb.AppendLine($"AllowedIPs = {options.AllowedIpsString}");
        sb.AppendLine($"Endpoint = {node.Host}:{node.Port}");
        if (node.PersistentKeepalive > 0)
            sb.AppendLine($"PersistentKeepalive = {node.PersistentKeepalive}");
        return sb.ToString();
    }
}

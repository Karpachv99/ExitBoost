namespace ExitBoost.Core.Vpn;

/// <summary>Протоколы VPN-ключей, поддерживаемые как в Happ.</summary>
public enum VpnProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2
}

/// <summary>
/// Протокол — просто схема ссылки (vless/vmess/trojan/ss).
/// <para>Расшифровка полей — в <see cref="VpnKeyParser"/>.</para>
/// </summary>
public static class VpnProtocolParser
{
    public static bool TryParse(string protocol, out VpnProtocol parsed)
    {
        parsed = VpnProtocol.Vless;
        switch (protocol.ToLowerInvariant())
        {
            case "vless": parsed = VpnProtocol.Vless; return true;
            case "vmess": parsed = VpnProtocol.Vmess; return true;
            case "trojan": parsed = VpnProtocol.Trojan; return true;
            case "ss": parsed = VpnProtocol.Shadowsocks; return true;
            case "hysteria2":
            case "hy2": parsed = VpnProtocol.Hysteria2; return true;
            default: return false;
        }
    }
}

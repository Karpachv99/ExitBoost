namespace ExitBoost.Core.Vpn;

/// <summary>Ключ VPN-подключения (VLESS/VMess/Trojan/Shadowsocks) в разобранном виде.</summary>
public sealed class VpnKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Понятное имя (из ссылки или заданное пользователем).</summary>
    public string Name { get; set; } = "";

    public VpnProtocol Protocol { get; set; }

    /// <summary>Адрес сервера (домен или IP).</summary>
    public string Address { get; set; } = "";

    public int Port { get; set; }

    /// <summary>UUID (vless/vmess) или пароль (trojan/ss).</summary>
    public string UserId { get; set; } = "";

    /// <summary>tls | reality | none (для trojan всегда tls).</summary>
    public string Security { get; set; } = "none";

    /// <summary>Транспорт: tcp | ws | grpc | http.</summary>
    public string Transport { get; set; } = "tcp";

    /// <summary>XTLS-флоу (vless/xtls-rprx-vision).</summary>
    public string Flow { get; set; } = "";

    /// <summary>Имя сайта для TLS/Reality.</summary>
    public string Sni { get; set; } = "";

    /// <summary>Отпечаток браузера (chrome, firefox, safari…).</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>Открытый ключ Reality (pbk).</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Short ID Reality (sid).</summary>
    public string ShortId { get; set; } = "";

    /// <summary>Путь WS/HTTP или имя сервиса gRPC.</summary>
    public string Path { get; set; } = "";

    /// <summary>Заголовок Host для WS/HTTP.</summary>
    public string HostHeader { get; set; } = "";

    /// <summary>Метод шифрования Shadowsocks.</summary>
    public string Method { get; set; } = "";

    /// <summary>Alter ID (vmess).</summary>
    public int AlterId { get; set; }

    /// <summary>Вид шифрования vmess (scy): auto/aes-128-gcm…</summary>
    public string Encryption { get; set; } = "";

    /// <summary>ALPN (например, http/1.1).</summary>
    public string Alpn { get; set; } = "";

    /// <summary>Обфускация Hysteria2 (salamander).</summary>
    public string ObfsType { get; set; } = "";

    /// <summary>Пароль обфускации Hysteria2.</summary>
    public string ObfsPassword { get; set; } = "";

    /// <summary>
    /// Почему ключ нельзя использовать с ядром sing-box (пусто — можно использовать).
    /// Например, транспорт xhttp поддерживает только Xray.
    /// </summary>
    public string? UnsupportedReason =>
        Transport is "xhttp" or "splithttp" or "httpupgrade" or "splithttp"
            ? $"транспорт «{Transport}» поддерживается только Xray, ядро sing-box его не умеет"
            : null;

    /// <summary>Исходная ссылка-ключ.</summary>
    public string RawLink { get; set; } = "";

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"{Name} · {Protocol} {Address}:{Port}";
}

namespace ExitBoost.Core.Vpn;

/// <summary>Ключ VPN-подключения (VLESS/VMess/Trojan/Shadowsocks) в разобранном виде.</summary>
public sealed class VpnKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Понятное имя (из ссылки или заданное пользователем).</summary>
    public string Name { get; set; } = "";

    /// <summary>Название подписки/набора ключей, из которого добавлен ключ.</summary>
    public string SubscriptionName { get; set; } = "";

    /// <summary>
    /// Ссылка-подписка, из которой получен ключ (пусто — ключ добавлен вручную).
    /// По ней набор ключей обновляется автоматически.
    /// </summary>
    public string SubscriptionUrl { get; set; } = "";

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

    /// <summary>Не проверять сертификат сервера (allowInsecure / insecure в ключе).</summary>
    public bool AllowInsecure { get; set; }

    /// <summary>Плагин Shadowsocks (SIP003), например obfs-local или v2ray-plugin.</summary>
    public string Plugin { get; set; } = "";

    /// <summary>
    /// Почему ключ нельзя использовать с ядром sing-box (пусто — можно использовать).
    /// Например, транспорт xhttp поддерживает только Xray.
    /// </summary>
    public string? UnsupportedReason => Transport switch
    {
        "xhttp" or "splithttp" =>
            $"транспорт «{Transport}» поддерживается только Xray, ядро sing-box его не умеет",
        "kcp" =>
            "транспорт «kcp» (mKCP) ядро sing-box не поддерживает",
        _ when Plugin.Length > 0 =>
            $"Shadowsocks с плагином «{Plugin}» не поддерживается: нужен внешний плагин",
        _ => null
    };

    /// <summary>Исходная ссылка-ключ.</summary>
    public string RawLink { get; set; } = "";

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"{Name} · {Protocol} {Address}:{Port}";
}

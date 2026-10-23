using System.Text.Json.Serialization;

namespace ExitBoost.Core.Models;

/// <summary>
/// Описание одного ретранслятора (узла). Универсально покрывает три источника узлов:
/// свой VPS, импорт WireGuard/OpenVPN конфигов и публичные бесплатные узлы.
/// </summary>
public sealed class NodeConfig
{
    /// <summary>Внутренний идентификатор.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>Отображаемое имя.</summary>
    public string Name { get; set; } = "Node";

    /// <summary>Страна (для группировки в UI).</summary>
    public string Country { get; set; } = "";

    /// <summary>Город.</summary>
    public string City { get; set; } = "";

    /// <summary>Публичный адрес узла.</summary>
    public string Host { get; set; } = "";

    /// <summary>Порт сервиса на узле.</summary>
    public int Port { get; set; } = 51820;

    /// <summary>Тип транспорта.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NodeKind Kind { get; set; } = NodeKind.WireGuard;

    /// <summary>Приватный ключ клиента (WireGuard).</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Публичный ключ пира на сервере (WireGuard).</summary>
    public string? PublicKey { get; set; }

    /// <summary>Pre-shared key (WireGuard, опционально).</summary>
    public string? PresharedKey { get; set; }

    /// <summary>Локальный адрес интерфейса, например <c>10.13.0.2/32</c>.</summary>
    public string? Address { get; set; }

    /// <summary>DNS, который пробрасывается в туннель.</summary>
    public string? Dns { get; set; }

    /// <summary>MTU интерфейса.</summary>
    public int Mtu { get; set; } = 1420;

    /// <summary>PersistentKeepalive в секундах.</summary>
    public int PersistentKeepalive { get; set; } = 25;

    /// <summary>Маршруты по умолчанию для режима FullTunnel (переопределяются режимом маршрутизации).</summary>
    public string[] AllowedIps { get; set; } = { "0.0.0.0/0" };

    /// <summary>Путь к конфигу (.conf для WireGuard, .ovpn для OpenVPN).</summary>
    public string? ConfigPath { get; set; }

    /// <summary>Пользователь для авторизации (OpenVPN / SSH).</summary>
    public string? Username { get; set; }

    /// <summary>Пароль для авторизации (OpenVPN / SSH).</summary>
    public string? Password { get; set; }

    /// <summary>Путь к приватному SSH-ключу.</summary>
    public string? SshKeyPath { get; set; }

    /// <summary>Локальный порт SOCKS5 для SshProxy-узлов.</summary>
    public int SocksPort { get; set; } = 1080;

    /// <summary>Метки для фильтрации в UI.</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>Узел участвует в замерах и автовыборе.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Приоритет: выше — предпочтительнее при равных показателях.</summary>
    public int Priority { get; set; }

    /// <summary>Произвольные заметки.</summary>
    public string? Notes { get; set; }

    /// <summary>Источник узла (для бейджей в UI).</summary>
    public string? Source { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Country)
        ? Name
        : $"{Name} · {Country}";

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

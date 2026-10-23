using ExitBoost.Core.Vpn;

namespace ExitBoost.App.ViewModels;

/// <summary>
/// Строка таблицы VPN-ключей — по образцу строки узла-ретранслятора:
/// узел, страна, пинг, потери, джиттер, качество + все параметры ключа.
/// </summary>
public sealed class VpnKeyItemViewModel : ObservableObject
{
    public VpnKeyItemViewModel(VpnKey key) => Key = key;

    public VpnKey Key { get; }

    public string Id => Key.Id;

    /// <summary>Название точки (колонка «Локации»).</summary>
    public string Name => Key.Name;

    /// <summary>Страна из названия ключа.</summary>
    public string Country => VpnCountry.Guess(Key.Name);

    /// <summary>Группа — подписка целиком, а не отдельная страна.</summary>
    public string Group => string.IsNullOrWhiteSpace(Key.SubscriptionName)
        ? "Добавленные ключи"
        : Key.SubscriptionName;

    public string Protocol => Key.Protocol.ToString();

    /// <summary>Флаг страны для строки списка («🇩🇪»); пусто, если страна не определилась.</summary>
    public string Flag => VpnCountry.FlagFor(Country);

    /// <summary>Короткое обозначение транспорта: RAW TCP, XHTTP, WS, gRPC, REALITY…</summary>
    public string TransportLabel
    {
        get
        {
            string transport = Key.Transport.ToLowerInvariant();
            if (transport is "tcp" or "" or "raw")
                return Key.Security.Equals("reality", StringComparison.OrdinalIgnoreCase) ? "REALITY" : "RAW TCP";

            return transport switch
            {
                "ws" => "WS",
                "grpc" => "gRPC",
                "h2" or "http" => "HTTP/2",
                "httpupgrade" => "HTTPUPGRADE",
                "quic" => "QUIC",
                _ => transport.ToUpperInvariant()
            };
        }
    }

    /// <summary>Заголовок строки: «Германия | RAW TCP» (или название ключа, если страна не определилась).</summary>
    public string TitleText => Country == "—"
        ? Name
        : $"{Country} | {TransportLabel}";

    /// <summary>Подробности под заголовком: «VLESS / TCP / TLS».</summary>
    public string DetailsText
    {
        get
        {
            var parts = new List<string>(3) { Key.Protocol.ToString().ToUpperInvariant() };
            if (Key.Transport.Length > 0) parts.Add(Key.Transport.ToUpperInvariant());
            if (!Key.Security.Equals("none", StringComparison.OrdinalIgnoreCase) && Key.Security.Length > 0)
                parts.Add(Key.Security.ToUpperInvariant());
            return string.Join(" / ", parts);
        }
    }

    public string Address => Key.Address;

    public int Port => Key.Port;

    public string Security => Key.Security switch
    {
        "reality" => "Reality",
        "tls" => "TLS",
        "none" => "—",
        _ => Key.Security
    };

    public string Transport => Key.Transport.ToUpperInvariant();

    public string Flow => Key.Flow.Length > 0 ? Key.Flow : "—";

    public string Sni => Key.Sni.Length > 0 ? Key.Sni : "—";

    public string AddedAt => Key.AddedAt.ToLocalTime().ToString("dd.MM.yy");

    public string Server => $"{Key.Address}:{Key.Port}";

    private double _ping;
    public double Ping
    {
        get => _ping;
        private set
        {
            if (!Set(ref _ping, value)) return;
            OnPropertyChanged(nameof(PingText));
        }
    }

    private double _loss;
    public double Loss
    {
        get => _loss;
        private set
        {
            if (!Set(ref _loss, value)) return;
            OnPropertyChanged(nameof(LossText));
        }
    }

    private double _jitter;
    public double Jitter
    {
        get => _jitter;
        private set
        {
            if (!Set(ref _jitter, value)) return;
            OnPropertyChanged(nameof(JitterText));
        }
    }

    /// <summary>Пинг в миллисекундах для показа в таблице.</summary>
    public string PingText => Ping > 0 ? $"{Ping:0} мс" : "—";

    /// <summary>Потери в процентах для показа в таблице.</summary>
    public string LossText => Quality == "недоступен" ? "100 %" : Loss > 0 ? $"{Loss:0} %" : "0 %";

    /// <summary>Джиттер для показа в таблице.</summary>
    public string JitterText => Ping > 0 ? $"{Jitter:0} мс" : "—";

    private string _quality = "—";
    public string Quality
    {
        get => _quality;
        private set
        {
            if (!Set(ref _quality, value)) return;
            OnPropertyChanged(nameof(QualityColor));
        }
    }

    /// <summary>Цвет оценки качества для строки списка.</summary>
    public string QualityColor => Quality switch
    {
        "отлично" => "#2FBF71",
        "хорошо" => "#8ED16B",
        "средне" => "#F5A524",
        "плохо" => "#EF4444",
        "недоступен" => "#EF4444",
        _ => "#8C95A6"
    };

    private string _statusText = "измерение…";
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>Применить результаты измерения канала до сервера ключа.</summary>
    public void Apply(VpnKeyMetrics metrics)
    {
        Ping = metrics.HasData ? Math.Round(metrics.AvgMs) : 0;
        Loss = Math.Round(metrics.LossPercent);
        Jitter = Math.Round(metrics.JitterMs);
        Quality = metrics.QualityText;
        StatusText = metrics.StatusText;
        OnPropertyChanged(nameof(LossText));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Country));
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(Security));
        OnPropertyChanged(nameof(Transport));
        OnPropertyChanged(nameof(Flow));
        OnPropertyChanged(nameof(Sni));
        OnPropertyChanged(nameof(AddedAt));
        OnPropertyChanged(nameof(Server));
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(Flag));
        OnPropertyChanged(nameof(TransportLabel));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(DetailsText));
        OnPropertyChanged(nameof(PingText));
        OnPropertyChanged(nameof(LossText));
        OnPropertyChanged(nameof(JitterText));
        OnPropertyChanged(nameof(QualityColor));
    }
}

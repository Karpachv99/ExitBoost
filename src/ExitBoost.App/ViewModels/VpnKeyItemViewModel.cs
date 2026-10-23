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

    /// <summary>Заголовок группы в таблице: страна, «Игровые серверы» или «Прочее».</summary>
    public string Group
    {
        get
        {
            string country = Country;
            if (country != "—") return country;
            return Key.Name.Contains("Игров", StringComparison.OrdinalIgnoreCase) ? "Игровые серверы" : "Прочее";
        }
    }

    public string Protocol => Key.Protocol.ToString();

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
        private set => Set(ref _quality, value);
    }

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
        OnPropertyChanged(nameof(PingText));
        OnPropertyChanged(nameof(LossText));
        OnPropertyChanged(nameof(JitterText));
    }
}

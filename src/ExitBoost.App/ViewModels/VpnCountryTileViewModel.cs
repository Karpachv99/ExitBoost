namespace ExitBoost.App.ViewModels;

/// <summary>
/// Плитка страны на главной странице: одна страна — один ключ (лучший по пингу).
/// Клик по плитке выбирает локацию, не подключая VPN.
/// </summary>
public sealed class VpnCountryTileViewModel : ObservableObject
{
    private bool _isSelected;

    public VpnCountryTileViewModel(
        string country,
        string flag,
        IReadOnlyList<VpnKeyItemViewModel> keys,
        VpnKeyItemViewModel best,
        bool isSelected)
    {
        Country = country;
        Flag = flag;
        Key = best;
        Count = keys.Count;
        _isSelected = isSelected;

        // Пинг берём лучший по стране: так видно, насколько локация быстрая целиком.
        double ping = keys
            .Where(k => k.Ping > 0)
            .Select(k => k.Ping)
            .DefaultIfEmpty(0)
            .Min();
        PingText = ping > 0 ? $"{ping:0} мс" : "—";
        PingColor = ping <= 0 ? "#6B7488"
            : ping <= 60 ? "#2FBF71"
            : ping <= 120 ? "#8ED16B"
            : ping <= 220 ? "#F5A524"
            : "#EF4444";
    }

    /// <summary>Название страны (или «Другие локации»).</summary>
    public string Country { get; }

    /// <summary>Флаг страны.</summary>
    public string Flag { get; }

    /// <summary>Всего ключей в этой стране.</summary>
    public int Count { get; }

    /// <summary>Ключ, который будет выбран при клике.</summary>
    public VpnKeyItemViewModel Key { get; }

    public string PingText { get; }

    public string PingColor { get; }

    public string CountText => Count > 1 ? $"{Count} ключа" : "1 ключ";

    public bool IsSelected
    {
        get => _isSelected;
        private set => Set(ref _isSelected, value);
    }

    public void SetSelected(bool value) => IsSelected = value;
}

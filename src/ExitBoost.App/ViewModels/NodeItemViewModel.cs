using ExitBoost.Core.Models;
using ExitBoost.App.ViewModels;

namespace ExitBoost.App.ViewModels;

/// <summary>Строка таблицы серверов.</summary>
public sealed class NodeItemViewModel : ObservableObject
{
    private bool _isActive;

    public NodeItemViewModel(NodeStatus status) => Status = status;

    public NodeStatus Status { get; }

    public NodeConfig Config => Status.Config;

    public string Id => Config.Id;

    public string Name => Config.Name;

    public string Country => string.IsNullOrWhiteSpace(Config.Country) ? "—" : Config.Country;

    public string Host => $"{Config.Host}:{Config.Port}";

    public string Kind => Config.Kind switch
    {
        NodeKind.WireGuard => "WG",
        NodeKind.OpenVpn => "OVPN",
        _ => "SSH"
    };

    public string Source => Config.Source ?? "";

    public double Ping => Status.HasData ? Math.Round(Status.AvgPingMs) : 0;

    public double Loss => Math.Round(Status.LossPercent);

    public double Jitter => Math.Round(Status.JitterMs);

    public string Quality => Status.QualityText;

    public string StatusText => Status.StatusText;

    public string ScoreText => Status.HasData ? $"#{Math.Round(Status.Score)}" : "—";

    public bool IsConnected => Status.State == NodeState.Connected;

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Ping));
        OnPropertyChanged(nameof(Loss));
        OnPropertyChanged(nameof(Jitter));
        OnPropertyChanged(nameof(Quality));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(Country));
        OnPropertyChanged(nameof(Host));
    }
}

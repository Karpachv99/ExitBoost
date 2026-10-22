using System.Windows;
using ExitBoost.Core.Models;

namespace ExitBoost.App.Views;

/// <summary>Диалог добавления/редактирования узла.</summary>
public partial class NodeEditWindow : Window
{
    public NodeEditWindow()
    {
        InitializeComponent();
        KindBox.ItemsSource = new[] { NodeKind.WireGuard, NodeKind.OpenVpn, NodeKind.SshProxy };
        KindBox.SelectedItem = NodeKind.WireGuard;
    }

    public NodeEditWindow(NodeConfig config) : this()
    {
        _existing = config;
        Title = "Изменить узел";
        NameBox.Text = config.Name;
        CountryBox.Text = config.Country;
        CityBox.Text = config.City;
        HostBox.Text = config.Host;
        PortBox.Text = config.Port.ToString();
        KindBox.SelectedItem = config.Kind;
        PrivateKeyBox.Text = config.PrivateKey ?? "";
        PublicKeyBox.Text = config.PublicKey ?? "";
        PresharedKeyBox.Text = config.PresharedKey ?? "";
        AddressBox.Text = config.Address ?? "";
        DnsBox.Text = config.Dns ?? "";
        MtuBox.Text = config.Mtu.ToString();
        KeepaliveBox.Text = config.PersistentKeepalive.ToString();
        AllowedIpsBox.Text = string.Join(", ", config.AllowedIps);
        ConfigPathBox.Text = config.ConfigPath ?? "";
        UsernameBox.Text = config.Username ?? "";
        PasswordBox.Text = config.Password ?? "";
        NotesBox.Text = config.Notes ?? "";
    }

    private readonly NodeConfig? _existing;

    /// <summary>Результат редактирования.</summary>
    public NodeConfig? Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("Укажите имя узла.", "ExitBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            MessageBox.Show("Укажите адрес узла (IP или домен).", "ExitBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
            HostBox.Focus();
            return;
        }

        int.TryParse(PortBox.Text, out int port);
        int.TryParse(MtuBox.Text, out int mtu);
        int.TryParse(KeepaliveBox.Text, out int keepalive);

        var allowed = AllowedIpsBox.Text
            .Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).ToList();

        Result = new NodeConfig
        {
            Id = _existing?.Id ?? NodeConfig.NewId(),
            Name = NameBox.Text.Trim(),
            Country = CountryBox.Text.Trim(),
            City = CityBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port <= 0 ? 51820 : port,
            Kind = KindBox.SelectedItem is NodeKind kind ? kind : NodeKind.WireGuard,
            PrivateKey = NullIfEmpty(PrivateKeyBox.Text),
            PublicKey = NullIfEmpty(PublicKeyBox.Text),
            PresharedKey = NullIfEmpty(PresharedKeyBox.Text),
            Address = NullIfEmpty(AddressBox.Text),
            Dns = NullIfEmpty(DnsBox.Text),
            Mtu = mtu <= 0 ? 1420 : mtu,
            PersistentKeepalive = keepalive < 0 ? 25 : keepalive,
            AllowedIps = allowed.Count > 0 ? allowed.ToArray() : new[] { "0.0.0.0/0" },
            ConfigPath = NullIfEmpty(ConfigPathBox.Text),
            Username = NullIfEmpty(UsernameBox.Text),
            Password = NullIfEmpty(PasswordBox.Text),
            Tags = _existing?.Tags ?? Array.Empty<string>(),
            Enabled = _existing?.Enabled ?? true,
            Priority = _existing?.Priority ?? 0,
            Notes = NullIfEmpty(NotesBox.Text),
            Source = _existing?.Source
        };

        DialogResult = true;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

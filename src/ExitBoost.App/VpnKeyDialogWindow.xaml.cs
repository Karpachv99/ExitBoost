using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ExitBoost.Core.Vpn;

namespace ExitBoost.App;

/// <summary>
/// Диалог вставки VPN-ключа (vless/vmess/trojan/ss) с разбором и превью.
/// Также умеет скачивать ключи по ссылке-подписке (как в Happ).
/// </summary>
public partial class VpnKeyDialogWindow : Window
{
    /// <summary>Разобранные ключи (после DialogResult=true).</summary>
    public IReadOnlyList<VpnKey> ResultKeys { get; private set; } = [];

    public VpnKeyDialogWindow()
    {
        InitializeComponent();
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            PreviewText.Text = "Разберите ключ — здесь появится сводка";
            PreviewText.Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush;
            return;
        }
        if (!VpnSubscription.IsUrl(url))
        {
            PreviewText.Text = "Ссылка должна начинаться с http:// или https://";
            PreviewText.Foreground = FindResource("WarnBrush") as System.Windows.Media.Brush;
            return;
        }
        PreviewText.Text = "Нажмите «Загрузить по ссылке» — список ключей будет загружен и показан здесь";
        PreviewText.Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush;
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (!VpnSubscription.IsUrl(url))
        {
            MessageBox.Show("Укажите ссылку на подписку, например https://key.example.com/ваш_ключ",
                "Загрузка ключей", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UrlBox.IsEnabled = false;
        PreviewText.Text = "Загружаю ключи...";
        PreviewText.Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush;
        try
        {
            string text = await VpnSubscription.DownloadAsync(url).ConfigureAwait(true);
            KeysBox.Text = text;
            var keys = VpnSubscription.ParseAll(text, out var errors);
            PreviewText.Text = keys.Count > 0
                ? $"Загружено ключей: {keys.Count}" + (errors.Count > 0 ? $", не распознано: {errors.Count}" : "")
                : "Ссылка не содержит ключей (ожидались vless://, vmess://, trojan:// или ss://)";
            PreviewText.Foreground = keys.Count > 0
                ? System.Windows.Media.Brushes.LightGreen
                : FindResource("WarnBrush") as System.Windows.Media.Brush;
        }
        catch (Exception ex)
        {
            PreviewText.Text = "Не удалось загрузить: " + ex.Message;
            PreviewText.Foreground = FindResource("WarnBrush") as System.Windows.Media.Brush;
            MessageBox.Show("Не удалось загрузить ключи по ссылке:\n" + ex.Message,
                "Загрузка ключей", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UrlBox.IsEnabled = true;
        }
    }

    private void OnKeysChanged(object sender, TextChangedEventArgs e)
    {
        string text = KeysBox.Text.Trim();
        if (text.Length == 0)
        {
            PreviewText.Text = "Разберите ключ — здесь появится сводка";
            PreviewText.Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush;
            return;
        }

        var keys = VpnKeyParser.ParseMany(text, out var errors);
        if (keys.Count > 0)
        {
            string lines = string.Join("\n", keys.Select(k =>
                $"• {k.Name} — {k.Protocol} {k.Address}:{k.Port}" +
                (k.Security == "reality" ? " (Reality)" : k.Security == "tls" ? " (TLS)" : "")));
            PreviewText.Text = errors.Count > 0
                ? lines + "\nНе распознано строк: " + errors.Count
                : lines;
            PreviewText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else
        {
            string err = errors.FirstOrDefault() ?? "ключ не распознан";
            PreviewText.Text = "Не похоже на ключ: " + err;
            PreviewText.Foreground = FindResource("WarnBrush") as System.Windows.Media.Brush;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var keys = VpnKeyParser.ParseMany(KeysBox.Text, out var errors);
        if (keys.Count == 0)
        {
            MessageBox.Show(
                "Ни один ключ не распознан.\n" + (errors.FirstOrDefault() ?? ""),
                "Добавить VPN-ключ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (errors.Count > 0 &&
            MessageBox.Show($"Некоторые строки не распознаны ({errors.Count}). Добавить распознанные?",
                "Добавить VPN-ключ", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.Yes) is not MessageBoxResult.Yes)
        {
            return;
        }

        ResultKeys = keys;
        DialogResult = true;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ExitBoost.App.ViewModels;
using ExitBoost.Core.Models;

namespace ExitBoost.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += OnLoadedOnce;
    }

    private async void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOnce;
        try
        {
            await _viewModel.CheckUpdatesOnStartupAsync();
        }
        catch
        {
            // проверка обновлений не должна мешать запуску
        }
    }

    private void OnFullTunnelClick(object sender, RoutedEventArgs e) => _viewModel.Mode = RoutingMode.FullTunnel;

    /// <summary>Открыли выпадающий список «программы и сайты» — сразу ставим курсор в поиск.</summary>
    private void VpnTargetsCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Template.FindName("PART_SearchBox", combo) is TextBox search)
        {
            search.Focus();
            search.SelectAll();
        }
    }

    /// <summary>Закрыли список — сбрасываем поиск, чтобы в следующий раз был полный список.</summary>
    private void VpnTargetsCombo_DropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo && combo.DataContext is MainViewModel vm)
            vm.VpnTargetSearch = "";
    }

    private void OnProcessSplitClick(object sender, RoutedEventArgs e) => _viewModel.Mode = RoutingMode.ProcessSplit;

    private void OnNetworkSplitClick(object sender, RoutedEventArgs e) => _viewModel.Mode = RoutingMode.NetworkSplit;

    /// <summary>Двойной клик по строке локации: выбрать ключ и подключить VPN (и обход DPI).</summary>
    private void VpnKeysGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (e.OriginalSource is not DependencyObject source) return;

        // Клик по строке — конкретный ключ.
        var row = ItemsControl.ContainerFromElement(grid, source) as DataGridRow;
        if (row?.Item is VpnKeyItemViewModel key)
        {
            ConnectRow(key);
            return;
        }

        // Клик по заголовку страны — лучший по пингу ключ этой страны.
        string? group = FindGroupName(source);
        if (group is null) return;
        var best = _viewModel.VpnKeyRows
            .Where(r => r.Group == group)
            .OrderBy(r => r.Ping > 0 ? r.Ping : double.MaxValue)
            .FirstOrDefault();
        if (best is not null) ConnectRow(best);
    }

    private void ConnectRow(VpnKeyItemViewModel key)
    {
        _viewModel.SelectedVpnKey = key;
        if (_viewModel.QuickConnectCommand.CanExecute(null))
            _viewModel.QuickConnectCommand.Execute(null);
    }

    private static string? FindGroupName(DependencyObject source)
    {
        for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is GroupItem { DataContext: CollectionViewGroup group })
                return group.Name?.ToString();
        }
        return null;
    }

    /// <summary>
    /// Крестик: либо свернуть в трей, либо сначала отключить соединение,
    /// остановить обход DPI и очистить сеть — только потом выйти.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (App.ShuttingDown)
            return; // настоящий выход уже идёт — позволяем окну закрыться

        e.Cancel = true;
        Hide();

        if (_viewModel.CloseActionIndex == 0)
        {
            // Свернуть в значок трея. Соединение и обход DPI продолжают работать.
            App.ShowTrayHint();
            return;
        }

        // Полный выход: сперва отключаем соединение и чистим сеть, затем закрываемся.
        _viewModel.StateText = "Отключение...";
        try
        {
            await App.ExitAsync().ConfigureAwait(true);
        }
        catch
        {
            // ExitAsync сам переживает ошибки; в крайнем случае завершаем принудительно.
            App.ShuttingDown = true;
            Application.Current.Shutdown();
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        try
        {
            if (App.Controller.State == TunnelState.Connected)
                await App.Controller.DisconnectAsync();
        }
        catch { }
        base.OnClosed(e);
    }
}

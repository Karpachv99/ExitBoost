using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using ExitBoost.App;
using ExitBoost.App.Views;
using ExitBoost.Core;
using ExitBoost.Core.Configuration;
using ExitBoost.Core.Diagnostics;
using ExitBoost.Core.Dpi;
using ExitBoost.Core.Lists;
using ExitBoost.Core.Logging;
using ExitBoost.Core.Models;
using ExitBoost.Core.Networking;
using ExitBoost.Core.Routing;
using ExitBoost.Core.Tunnel;
using ExitBoost.Core.Updates;
using ExitBoost.Core.Vpn;
using Microsoft.Win32;

namespace ExitBoost.App.ViewModels;

/// <summary>Главная модель представления окна ExitBoost.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly NodeRegistry _registry;
    private readonly LogBus _log;
    private readonly ProbingService _probing;
    private readonly BoostController _controller;
    private readonly TunnelManager _tunnel;
    private readonly DispatcherTimer _uiTimer;
    private readonly ICollectionView _nodesView;

    private NodeItemViewModel? _selectedNode;
    private SelectedProcess? _selectedProcess;
    private RoutingMode _mode;
    private bool _autoSelect = true;
    private bool _autoDetect = true;
    private string _subnetsText = "";
    private string _statusText = "Готов к работе";
    private string _stateText = "Отключено";
    private string _stateColor = "#8C95A6";
    private string _elapsedText = "00:00:00";
    private string _interfaceText = "—";
    private string _searchText = "";
    private bool _isBusy;
    private DateTime? _connectedAt;
    private int _selectedPageIndex;
    private string _statDownloadText = "—";
    private string _statUploadText = "—";

    public MainViewModel()
    {
        _settings = App.Settings;
        _registry = App.Registry;
        _log = App.Log;
        _probing = App.Probing;
        _controller = App.Controller;
        _tunnel = App.Tunnel;

        LoadSettingsIntoUi();

        ConnectCommand = new RelayCommand(_ => ConnectAsync(), _ => !IsBusy && !IsConnected);
        DisconnectCommand = new RelayCommand(_ => DisconnectAsync(), _ => IsConnected || IsBusy);
        RefreshCommand = new RelayCommand(_ => RefreshAllAsync());
        AddNodeCommand = new RelayCommand(_ => { AddNode(); return Task.CompletedTask; });
        EditNodeCommand = new RelayCommand(_ => { EditNode(); return Task.CompletedTask; }, _ => SelectedNode is not null);
        RemoveNodeCommand = new RelayCommand(_ => { RemoveNode(); return Task.CompletedTask; }, _ => SelectedNode is not null);
        ImportConfigCommand = new RelayCommand(_ => { ImportConfig(); return Task.CompletedTask; });
        ExportNodesCommand = new RelayCommand(_ => { ExportNodes(); return Task.CompletedTask; });
        RefreshProcessesCommand = new RelayCommand(() => { RefreshProcesses(); return Task.CompletedTask; });
        OpenWireGuardSiteCommand = new RelayCommand(() =>
        {
            Process.Start(new ProcessStartInfo("https://www.wireguard.com/install/") { UseShellExecute = true });
            return Task.CompletedTask;
        });
        RestoreNetworkCommand = new RelayCommand(_ => RestoreNetworkAsync());
        LoadFeedCommand = new RelayCommand(_ => LoadFeedAsync());
        DpiUpdateCommand = new RelayCommand(_ => UpdateZapretAsync());
        DpiAutoSelectCommand = new RelayCommand(_ => AutoSelectDpiStrategyAsync(), _ => !IsDpiBusy);
        DpiApplyCommand = new RelayCommand(_ => ApplyDpiStrategyAsync(), _ => SelectedDpiStrategy is not null && !IsDpiBusy);
        DpiStopCommand = new RelayCommand(_ => StopDpiAsync());
        DpiPickTargetsCommand = new RelayCommand(() => PickDpiTargets());
        DpiCheckCommand = new RelayCommand(_ => CheckDpiAsync(), _ => !IsDpiBusy);
        DpiForceStopCommand = new RelayCommand(_ => ForceStopZapretAsync(), _ => !IsDpiBusy);
        DpiDetectGameCommand = new RelayCommand(_ => DetectGameServersAsync(), _ => !IsBusy && !IsDpiBusy);
        VpnAddKeyCommand = new RelayCommand(_ => { AddVpnKey(); return Task.CompletedTask; });
        VpnRemoveKeyCommand = new RelayCommand(_ => { RemoveVpnKey(); return Task.CompletedTask; }, _ => SelectedVpnKey is not null);
        VpnPickTargetsCommand = new RelayCommand(() => PickVpnTargets());
          VpnConnectCommand = new RelayCommand(_ => VpnConnectAsync(), _ => !_vpnBusy && !IsVpnRunning);
         RemoveDpiTargetCommand = new RelayCommand(p => { RemoveDpiTarget(p); return Task.CompletedTask; });
         RemoveVpnSiteCommand = new RelayCommand(p => { RemoveVpnSite(p); return Task.CompletedTask; });
         RemoveVpnProgramCommand = new RelayCommand(p => { RemoveVpnProgram(p); return Task.CompletedTask; });
          VpnDisconnectCommand = new RelayCommand(_ => VpnDisconnectAsync(), _ => IsVpnRunning && !_vpnBusy);
        QuickConnectCommand = new RelayCommand(_ => QuickConnectAsync(), _ => !IsBusy);
        VpnInstallCoreCommand = new RelayCommand(_ => VpnInstallCoreAsync(), _ => App.Vpn.IsRunning is false && App.Vpn.IsCoreRunning is false);
        UpdateCheckCommand = new RelayCommand(_ => CheckAppUpdateAsync(manual: true), _ => !IsUpdateBusy);
        UpdateInstallCommand = new RelayCommand(_ => InstallUpdateAsync(), _ => !IsUpdateBusy && _pendingUpdate is not null);
        UpdateOpenPageCommand = new RelayCommand(_ => { OpenReleasePage(); return Task.CompletedTask; }, _ => _pendingUpdate is not null);
        UpdateCancelCommand = new RelayCommand(() => _updateCts?.Cancel(), () => IsUpdateDownloading);
        UpdateOpenLogCommand = new RelayCommand(OpenUpdateLog, () => HasUpdateLog);
        CopyLogCommand = new RelayCommand(() =>
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Logs.Select(l => $"{l.Time} [{l.LevelText}] {l.Message}")));
            return Task.CompletedTask;
        });
        SpeedTestCommand = new RelayCommand(_ => RunSpeedTestAsync(), _ => !IsSpeedTestRunning);
        NavigateCommand = new RelayCommand(p => { NavigateTo(p); return Task.CompletedTask; });
        QuickServiceCommand = new RelayCommand(p => { AddQuickService(p); return Task.CompletedTask; });

        _probing.StatusUpdated += OnStatusUpdated;
        _log.EntryAdded += OnLogEntry;
        _controller.StatusChanged += OnControllerStatusChanged;
        _nodesView = CollectionViewSource.GetDefaultView(Nodes);
        _nodesView.Filter = o => MatchesFilter(o as NodeItemViewModel);
        VpnTargetsView = CollectionViewSource.GetDefaultView(VpnTargets);
        VpnTargetsView.Filter = o => MatchesVpnTarget(o as VpnTargetPickItem);
        VpnTargetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VpnTargetPickItem.Group)));
        DpiTargetsView = new ListCollectionView(VpnTargets);
        DpiTargetsView.Filter = o => MatchesDpiTarget(o as VpnTargetPickItem);
        DpiTargetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VpnTargetPickItem.Group)));
        BuildVpnTargets();
        VpnKeysView = CollectionViewSource.GetDefaultView(VpnKeyRows);
        VpnKeysView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VpnKeyItemViewModel.CountryGroup)));
        VpnKeysView.SortDescriptions.Add(new SortDescription(nameof(VpnKeyItemViewModel.CountryGroup), ListSortDirection.Ascending));
        DpiGameStrategiesView = new ListCollectionView(DpiStrategies)
        {
            Filter = o => o is DpiStrategyItemViewModel item && item.IsGame
        };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => Tick();
        _uiTimer.Start();
        BuildNodeList();
        RefreshProcesses();
        FillDpiStrategies(App.ZapretCatalog);
        VpnReloadFromStore();
        _ = RefreshBlockedListAsync(force: false);
        DpiEngineStatus = App.Zapret.IsInstalled
            ? $"установлен ({App.Zapret.InstalledVersion?[..Math.Min(8, App.Zapret.InstalledVersion.Length)]})"
            : "не установлен — нажмите «Обновить zapret»";
    }

    // ---------------------------------------------------------- коллекции

    public ObservableCollection<NodeItemViewModel> Nodes { get; } = new();

    public ObservableCollection<SelectedProcess> Processes { get; } = new();

    public ObservableCollection<LogEntry> Logs { get; } = new();

    public ObservableCollection<double> PingHistory { get; } = new();

    // ---------------------------------------------------------- команды

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddNodeCommand { get; }
    public RelayCommand EditNodeCommand { get; }
    public RelayCommand RemoveNodeCommand { get; }
    public RelayCommand ImportConfigCommand { get; }
    public RelayCommand ExportNodesCommand { get; }
    public RelayCommand RefreshProcessesCommand { get; }
    public RelayCommand OpenWireGuardSiteCommand { get; }
    public RelayCommand RestoreNetworkCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand LoadFeedCommand { get; }
    public RelayCommand DpiUpdateCommand { get; }
    public RelayCommand DpiAutoSelectCommand { get; }
    public RelayCommand DpiApplyCommand { get; }
    public RelayCommand DpiStopCommand { get; }
    public RelayCommand DpiPickTargetsCommand { get; }
    public RelayCommand DpiCheckCommand { get; }
    public RelayCommand DpiForceStopCommand { get; }
    public RelayCommand DpiDetectGameCommand { get; }
    public RelayCommand RemoveDpiTargetCommand { get; }
    public RelayCommand VpnAddKeyCommand { get; }
    public RelayCommand VpnRemoveKeyCommand { get; }
    public RelayCommand VpnPickTargetsCommand { get; }
    public RelayCommand VpnConnectCommand { get; }
    public RelayCommand RemoveVpnSiteCommand { get; }
    public RelayCommand RemoveVpnProgramCommand { get; }
    public RelayCommand VpnDisconnectCommand { get; }

    /// <summary>Двойной клик по строке локации: VPN + обход DPI.</summary>
    public RelayCommand QuickConnectCommand { get; }
    public RelayCommand VpnInstallCoreCommand { get; }
    public RelayCommand SpeedTestCommand { get; }

    /// <summary>Переход на страницу по номеру (0 — главная, 1 — локации, 2 — игры, 3 — DPI, 4 — журнал, 5 — настройки).</summary>
    public RelayCommand NavigateCommand { get; }

    /// <summary>Быстрое добавление сервиса в список VPN (параметр — домены через «|»).</summary>
    public RelayCommand QuickServiceCommand { get; }

    // ---------------------------------------------------------- свойства

    /// <summary>Выбранная страница в боковом меню.</summary>
    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set => Set(ref _selectedPageIndex, value);
    }

    /// <summary>Последний результат теста скорости: скачивание.</summary>
    public string StatDownloadText
    {
        get => _statDownloadText;
        private set => Set(ref _statDownloadText, value);
    }

    /// <summary>Последний результат теста скорости: отдача.</summary>
    public string StatUploadText
    {
        get => _statUploadText;
        private set => Set(ref _statUploadText, value);
    }

    /// <summary>Только игровые стратегии — для страницы «Игры и сервисы».</summary>
    public ICollectionView DpiGameStrategiesView { get; }

    public NodeItemViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            OnPropertyChanged(nameof(SelectedNodeSummary));
            EditNodeCommand.RaiseCanExecuteChanged();
            RemoveNodeCommand.RaiseCanExecuteChanged();
        }
    }

    public SelectedProcess? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (!Set(ref _selectedProcess, value)) return;
            if (value is not null)
            {
                _settings.Update(s =>
                {
                    s.TargetProcessPath = value.Path;
                    s.TargetProcessName = value.Name;
                });
            }
            OnPropertyChanged(nameof(SelectedProcessSummary));
        }
    }

    public RoutingMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            _settings.Update(s => s.DefaultMode = value);
            OnPropertyChanged(nameof(IsFullTunnelMode));
            OnPropertyChanged(nameof(IsProcessSplitMode));
            OnPropertyChanged(nameof(IsNetworkSplitMode));
        }
    }

    public bool IsFullTunnelMode
    {
        get => Mode == RoutingMode.FullTunnel;
        set { if (value) Mode = RoutingMode.FullTunnel; }
    }

    public bool IsProcessSplitMode
    {
        get => Mode == RoutingMode.ProcessSplit;
        set { if (value) Mode = RoutingMode.ProcessSplit; }
    }

    public bool IsNetworkSplitMode
    {
        get => Mode == RoutingMode.NetworkSplit;
        set { if (value) Mode = RoutingMode.NetworkSplit; }
    }

    public bool AutoSelect
    {
        get => _autoSelect;
        set
        {
            if (!Set(ref _autoSelect, value)) return;
            _settings.Update(s => s.AutoSelectBest = value);
            if (value) SelectBestNode(force: true);
        }
    }

    public bool AutoDetect
    {
        get => _autoDetect;
        set
        {
            if (!Set(ref _autoDetect, value)) return;
            _settings.Update(s => s.AutoDetectEndpoints = value);
        }
    }

    public string SubnetsText
    {
        get => _subnetsText;
        set
        {
            if (!Set(ref _subnetsText, value)) return;
            _settings.Update(s => s.SplitSubnets = value
                .Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).ToList());
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _nodesView.Refresh();
        }
    }

    private string _feedUrl = "";

    /// <summary>Адрес публичного списка узлов.</summary>
    public string FeedUrl
    {
        get => _feedUrl;
        set
        {
            if (!Set(ref _feedUrl, value)) return;
            _settings.Update(s => s.NodesFeedUrl = value);
        }
    }

    // ------------------------------------------------------------- обход DPI

    public ObservableCollection<DpiStrategyItemViewModel> DpiStrategies { get; } = new();

    private string _dpiEngineStatus = "проверка…";
    private string _dpiUpdateStatus = "";
    private string _dpiTargetHosts = "";
    private int _dpiTargetPort = 443;
    private string _dpiGameIps = "";
    private string _dpiGameTcpPorts = "";
    private string _dpiGameUdpPorts = "";
    private string _dpiGameProcess = "";
    private string _dpiBaselineText = "—";
    private string _dpiBestText = "—";
    private bool _isDpiBusy;
    private string _dpiProgressText = "";
    private DpiStrategyItemViewModel? _selectedDpiStrategy;

    // Готовый список заблокированных сервисов (общий для DPI и VPN).
    private BlockedListPreset _blockedPreset = BlockedListPreset.Off;
    private string _blockedListStatus = "готовый список выключен";
    private BlockedListInfo? _blockedListInfo;
    private int _blockedListBusy;

    private string _dpiActiveText = "Обход DPI выключен";
    private string _dpiActiveColor = "#8C95A6";

    /// <summary>Живой статус обхода: работает ли zapret прямо сейчас.</summary>
    public string DpiActiveText
    {
        get => _dpiActiveText;
        private set => Set(ref _dpiActiveText, value);
    }

    /// <summary>Цвет плашки статуса обхода (зелёный — работает).</summary>
    public string DpiActiveColor
    {
        get => _dpiActiveColor;
        private set => Set(ref _dpiActiveColor, value);
    }

    private void RefreshDpiStatus()
    {
        // вывод движка zapret читаем из его файла журнала
        string engineOutput = App.ZapretRunner.ReadNewOutput();
        if (engineOutput.Length > 0)
        {
            foreach (string line in engineOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0) _log.Info("zapret: " + trimmed);
            }
        }

        bool running = App.ZapretRunner.IsRunning;
        if (running)
        {
            string? id = App.ZapretRunner.CurrentStrategyId;
            var strategy = App.ZapretCatalog.Strategies.FirstOrDefault(s => s.Id == id);
            string name = strategy?.Name ?? id ?? "…";
            DpiActiveText = $"Обход DPI работает · «{name}»";
            DpiActiveColor = "#2FBF71";
        }
        else
        {
            DpiActiveText = "Обход DPI выключен";
            DpiActiveColor = "#8C95A6";
        }
    }

    /// <summary>Состояние движка zapret.</summary>
    public string DpiEngineStatus
    {
        get => _dpiEngineStatus;
        private set => Set(ref _dpiEngineStatus, value);
    }

    /// <summary>Информация об обновлении.</summary>
    public string DpiUpdateStatus
    {
        get => _dpiUpdateStatus;
        private set => Set(ref _dpiUpdateStatus, value);
    }

    /// <summary>Домены для обхода DPI.</summary>
    public string DpiTargetHosts
    {
        get => _dpiTargetHosts;
        set
        {
            if (!Set(ref _dpiTargetHosts, value)) return;
            _settings.Update(s => s.DpiTargetHosts = value);
            OnPropertyChanged(nameof(DpiSelectedTargets));
            OnPropertyChanged(nameof(DpiSelectedTargetsCount));
        }
    }

    public IEnumerable<string> DpiSelectedTargets => DpiTargetHosts
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int DpiSelectedTargetsCount => DpiSelectedTargets.Count();

    // ------------------------------------------- готовый список заблокированных

    /// <summary>
    /// Выбранный готовый список: 0 — не использовать, 1 — стандартный список
    /// заблокированных сервисов РФ, 2 — полный реестр РКН.
    /// </summary>
    public int BlockedPresetIndex
    {
        get => (int)_blockedPreset;
        set
        {
            var preset = value switch
            {
                1 => BlockedListPreset.Standard,
                2 => BlockedListPreset.Full,
                _ => BlockedListPreset.Off
            };
            if (preset == _blockedPreset) return;

            _blockedPreset = preset;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BlockedPresetHint));
            _settings.Update(s => s.BlockedListPreset = BlockedLists.ToSetting(preset));
            _ = RefreshBlockedListAsync(force: false);
        }
    }

    /// <summary>Пояснение под переключателем списка.</summary>
    public string BlockedPresetHint => _blockedPreset switch
    {
        BlockedListPreset.Standard =>
            "Курируемый набор заблокированных в РФ сервисов (itdoginfo/allow-domains, ~1,2 тыс. доменов). " +
            "Добавляется к вашим доменам и в обход DPI, и в VPN.",
        BlockedListPreset.Full =>
            "Полный реестр РКН (~1,7 млн доменов, обновляется еженедельно). Максимальный охват, " +
            "но VPN в режиме «только выбранные» будет стартовать заметно дольше.",
        _ => "Свои домены — вручную. Готовый список можно включить в один клик."
    };

    /// <summary>Состояние локальной копии списка (сколько доменов, когда обновлён).</summary>
    public string BlockedListStatus
    {
        get => _blockedListStatus;
        private set => Set(ref _blockedListStatus, value);
    }

    private async Task RefreshBlockedListAsync(bool force)
    {
        if (_blockedPreset == BlockedListPreset.Off)
        {
            _blockedListInfo = null;
            BlockedListStatus = "готовый список выключен";
            return;
        }
        if (Interlocked.Exchange(ref _blockedListBusy, 1) == 1) return;

        try
        {
            var preset = _blockedPreset;
            BlockedListStatus = "загрузка готового списка…";
            var info = await BlockedLists
                .EnsureAsync(preset, force, message => _log.Info(message))
                .ConfigureAwait(true);

            if (preset != _blockedPreset) return; // пользователь успел переключить

            _blockedListInfo = info;
            if (info.Ok)
            {
                string updated = info.UpdatedAt is { } at ? $", обновлён {at:dd.MM.yyyy}" : "";
                string stale = info.Error is null ? "" : $" (сеть недоступна: {info.Error})";
                BlockedListStatus = $"{BlockedLists.DisplayName(preset)}: {info.Count:N0} доменов{updated}{stale}";
            }
            else
            {
                BlockedListStatus = $"список недоступен: {info.Error}";
            }
        }
        finally
        {
            Interlocked.Exchange(ref _blockedListBusy, 0);
        }
    }

    private void RemoveDpiTarget(object? parameter)
    {
        if (parameter is not string target) return;
        DpiTargetHosts = string.Join(", ", DpiSelectedTargets
            .Where(x => !x.Equals(target, StringComparison.OrdinalIgnoreCase)));
    }

    private string _dpiProgramSearch = "";
    public string DpiProgramSearch
    {
        get => _dpiProgramSearch;
        set => Set(ref _dpiProgramSearch, value);
    }

    private SelectedProcess? _selectedDpiProcess;
    public SelectedProcess? SelectedDpiProcess
    {
        get => _selectedDpiProcess;
        set
        {
            if (!Set(ref _selectedDpiProcess, value) || value is null) return;

            var domains = PickerViewModel.DomainsForProcess(value.Name);
            if (domains.Count == 0)
            {
                DpiProgressText = $"Для {value.Name} нет готового домена — добавьте домен вручную";
            }
            else
            {
                var existing = DpiTargetHosts
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                foreach (string domain in domains)
                    if (!existing.Contains(domain, StringComparer.OrdinalIgnoreCase)) existing.Add(domain);
                DpiTargetHosts = string.Join(", ", existing);
                DpiProgressText = $"Программа {value.Name}: домены добавлены";
            }

            DpiProgramSearch = "";
            _selectedDpiProcess = null;
            OnPropertyChanged(nameof(SelectedDpiProcess));
        }
    }

    /// <summary>Порт проверки.</summary>
    public int DpiTargetPort
    {
        get => _dpiTargetPort;
        set
        {
            if (!Set(ref _dpiTargetPort, value)) return;
            _settings.Update(s => s.DpiTargetPort = value);
        }
    }

    // ------------------------------------------------------------ игры и zapret

    /// <summary>
    /// Имя процесса игры, по соединениям которого ищутся серверы.
    /// Выбирается прямо в блоке игр: цель VPN для этого не нужна.
    /// </summary>
    public string DpiGameProcess
    {
        get => _dpiGameProcess;
        set
        {
            // ComboBox при обновлении списка процессов может прислать null —
            // храним пустую строку, чтобы не ловить NullReferenceException.
            if (!Set(ref _dpiGameProcess, value ?? "")) return;
            _settings.Update(s => s.DpiGameProcess = _dpiGameProcess);
            OnPropertyChanged(nameof(DpiGameSummary));
        }
    }

    /// <summary>IP-адреса серверов игры (по одному в строке).</summary>
    public string DpiGameIps
    {
        get => _dpiGameIps;
        set
        {
            if (!Set(ref _dpiGameIps, value)) return;
            _settings.Update(s => s.DpiGameIps = value);
            OnPropertyChanged(nameof(DpiGameSummary));
        }
    }

    /// <summary>TCP-порты игры (через запятую, диапазоны допускаются).</summary>
    public string DpiGameTcpPorts
    {
        get => _dpiGameTcpPorts;
        set
        {
            if (!Set(ref _dpiGameTcpPorts, value)) return;
            _settings.Update(s => s.DpiGameTcpPorts = value);
            OnPropertyChanged(nameof(DpiGameSummary));
        }
    }

    /// <summary>UDP-порты игры (через запятую, диапазоны допускаются).</summary>
    public string DpiGameUdpPorts
    {
        get => _dpiGameUdpPorts;
        set
        {
            if (!Set(ref _dpiGameUdpPorts, value)) return;
            _settings.Update(s => s.DpiGameUdpPorts = value);
            OnPropertyChanged(nameof(DpiGameSummary));
        }
    }

    /// <summary>Короткая сводка по игровому режиму для интерфейса.</summary>
    public string DpiGameSummary
    {
        get
        {
            var ipList = GameIpListFromText(_dpiGameIps);
            var tcp = DpiPorts.Parse(_dpiGameTcpPorts);
            var udp = DpiPorts.Parse(_dpiGameUdpPorts);

            if (ipList.Count == 0 && tcp.Count == 0 && udp.Count == 0)
                return "Игровой режим выключен: укажите серверы игры или её порты";

            string parts = "";
            if (!string.IsNullOrWhiteSpace(_dpiGameProcess))
                parts = $"игра: {_dpiGameProcess}";
            if (ipList.Count > 0) parts += (parts.Length > 0 ? ", " : "") + $"адресов: {ipList.Count}";
            if (tcp.Count > 0) parts += (parts.Length > 0 ? ", " : "") + $"TCP: {string.Join(",", tcp)}";
            if (udp.Count > 0) parts += (parts.Length > 0 ? ", " : "") + $"UDP: {string.Join(",", udp)}";
            return "Игровой режим: " + parts;
        }
    }

    private static IReadOnlyList<string> GameIpListFromText(string text) => text
        .Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(IsIpAddress)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static bool IsIpAddress(string value)
    {
        string address = value;
        int slash = value.IndexOf('/');
        if (slash > 0) address = value[..slash];
        return System.Net.IPAddress.TryParse(address, out _);
    }

    /// <summary>
    /// Найти серверы игры по TCP-таблице и WinDivert FLOW (UDP), заполнить IP
    /// и удалённые порты. FLOW содержит только метаданные соединения, не payload.
    /// </summary>
    private async Task DetectGameServersAsync()
    {
        // Процесс выбирается прямо в блоке игр. Если он не задан, пробуем
        // первую программу из VPN-сплита — её пользователь уже указал.
        string name = (DpiGameProcess ?? "").Trim();
        if (name.Length == 0)
            name = VpnSelectedPrograms.FirstOrDefault() ?? "";

        if (name.Length == 0)
        {
            DpiProgressText = "Выберите процесс игры в блоке «Игры: серверы и порты»";
            return;
        }

        var pids = ProcessInspector.FindPids(name);
        if (pids.Count == 0)
        {
            DpiProgressText = $"Процесс {name} не запущен — запустите игру и войдите в неё";
            return;
        }

        // FLOW monitor started with the application, so it already knows about
        // UDP sessions created before this button was pressed. Ensure it is also
        // started on machines where zapret was installed after app startup.
        if (!App.GameFlowMonitor.IsListening && App.Zapret.IsInstalled)
            await App.GameFlowMonitor.StartAsync().ConfigureAwait(true);

        IReadOnlyList<UdpFlowInfo> udpFlows = pids
            .SelectMany(App.GameFlowMonitor.GetUdpFlows)
            .ToList();
        if (udpFlows.Count == 0 && App.GameFlowMonitor.IsListening)
        {
            DpiProgressText = $"Слушаю UDP-соединения процесса {name}… Вернитесь в игру или переподключитесь к серверу.";
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            udpFlows = pids.SelectMany(App.GameFlowMonitor.GetUdpFlows).ToList();
        }

        var connections = pids.SelectMany(pid => ConnectionTable.GetByPid(pid)).ToList();
        var tcpConnections = connections
            .Where(c => c.Protocol == "TCP"
                        && c.State is "ESTABLISHED" or "SYN_SENT"
                        && c.RemoteAddress.Length > 0
                        && IsIpAddress(c.RemoteAddress))
            .ToList();
        var udpPorts = udpFlows
            .Where(f => f.RemotePort > 0)
            .Select(f => f.RemotePort)
            .Distinct()
            .OrderBy(port => port)
            .ToList();
        var localUdpPorts = connections
            .Where(c => c.Protocol == "UDP" && c.LocalPort > 0)
            .Select(c => c.LocalPort)
            .Distinct()
            .OrderBy(port => port)
            .ToList();

        if (tcpConnections.Count == 0 && udpFlows.Count == 0 && localUdpPorts.Count == 0)
        {
            DpiProgressText = $"{name}: соединения не обнаружены. Проверьте, что приложение запущено от администратора, и повторите в игре.";
            return;
        }

        var ips = tcpConnections
            .Select(c => c.RemoteAddress)
            .Concat(udpFlows.Select(f => f.RemoteAddress))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Порт 443 — обычный HTTPS: он уже покрыт сайтовыми стратегиями,
        // в игровой профиль его не добавляем, иначе обход затронет весь браузер.
        var ports = tcpConnections
            .Select(c => c.RemotePort)
            .Where(port => port is > 0 and not 443 and not 80)
            .Distinct()
            .OrderBy(port => port)
            .ToList();

        if (ips.Count > 0) DpiGameIps = string.Join(Environment.NewLine, ips);
        if (ports.Count > 0) DpiGameTcpPorts = string.Join(",", ports);
        if (udpPorts.Count > 0) DpiGameUdpPorts = string.Join(",", udpPorts);

        if (tcpConnections.Count > 0 || udpFlows.Count > 0)
        {
            DpiProgressText = $"{name}: найдено адресов серверов {ips.Count}"
                + (ports.Count > 0 ? $", TCP {string.Join(",", ports)}" : "")
                + (udpFlows.Count > 0 ? $", UDP {string.Join(",", udpPorts)}" : "")
                + (udpFlows.Count == 0 ? ". UDP-соединений у выбранного процесса не обнаружено — возможно, эта игра сейчас использует только TCP." : "")
                + (ips.Count == 0 ? ". Адреса серверов появятся после сетевой активности игры." : "");
        }
        else
        {
            DpiProgressText = $"{name}: Windows видит UDP-порты процесса {string.Join(",", localUdpPorts)}, но удалённый адрес ещё не пойман. Вернитесь в игру/лобби и нажмите «Серверы игры» повторно.";
        }
    }

    /// <summary>Результат замера без обхода.</summary>
    public string DpiBaselineText
    {
        get => _dpiBaselineText;
        private set => Set(ref _dpiBaselineText, value);
    }

    /// <summary>Лучшая найденная стратегия.</summary>
    public string DpiBestText
    {
        get => _dpiBestText;
        private set => Set(ref _dpiBestText, value);
    }

    /// <summary>Ход выполнения автоподбора.</summary>
    public string DpiProgressText
    {
        get => _dpiProgressText;
        private set => Set(ref _dpiProgressText, value);
    }

    public bool IsDpiBusy
    {
        get => _isDpiBusy;
        private set
        {
            if (!Set(ref _isDpiBusy, value)) return;
            DpiAutoSelectCommand.RaiseCanExecuteChanged();
            DpiApplyCommand.RaiseCanExecuteChanged();
            DpiCheckCommand.RaiseCanExecuteChanged();
        }
    }

    public DpiStrategyItemViewModel? SelectedDpiStrategy
    {
        get => _selectedDpiStrategy;
        set
        {
            if (!Set(ref _selectedDpiStrategy, value)) return;
            DpiApplyCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _dpiApplyOnConnect;

    /// <summary>Применять обход DPI при подключении к ретранслятору.</summary>
    public bool DpiApplyOnConnect
    {
        get => _dpiApplyOnConnect;
        set
        {
            if (!Set(ref _dpiApplyOnConnect, value)) return;
            _settings.Update(s => s.DpiApplyOnConnect = value);
        }
    }

    private bool _dpiAutoUpdate;

    /// <summary>Автоматически обновлять движок zapret.</summary>
    public bool DpiAutoUpdate
    {
        get => _dpiAutoUpdate;
        set
        {
            if (!Set(ref _dpiAutoUpdate, value)) return;
            _settings.Update(s => s.DpiAutoUpdate = value);
        }
    }

    private bool _dpiCheckUpdates = true;

    /// <summary>Проверять обновления zapret при запуске.</summary>
    public bool DpiCheckUpdates
    {
        get => _dpiCheckUpdates;
        set
        {
            if (!Set(ref _dpiCheckUpdates, value)) return;
            _settings.Update(s => s.DpiCheckUpdates = value);
        }
    }

    private int _closeActionIndex;

    /// <summary>
    /// Что делать при нажатии на крестик: 0 — свернуть в трей,
    /// 1 — отключиться, очистить сеть и выйти.
    /// </summary>
    public int CloseActionIndex
    {
        get => _closeActionIndex;
        set
        {
            if (!Set(ref _closeActionIndex, value)) return;
            _settings.Update(s => s.CloseToTray = value == 0);
        }
    }

    private bool MatchesFilter(NodeItemViewModel? node)
    {
        if (node is null) return false;
        string q = SearchText.Trim();
        if (q.Length == 0) return true;
        return node.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || node.Country.Contains(q, StringComparison.OrdinalIgnoreCase)
            || node.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
            || node.Kind.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string StateText
    {
        get => _stateText;
        internal set
        {
            if (!Set(ref _stateText, value)) return;
            OnPropertyChanged(nameof(StateTextUpper));
        }
    }

    /// <summary>Крупная надпись под кнопкой питания — всегда заглавными.</summary>
    public string StateTextUpper => StateText.ToUpperInvariant();

    public string StateColor
    {
        get => _stateColor;
        private set => Set(ref _stateColor, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => Set(ref _elapsedText, value);
    }

    public string InterfaceText
    {
        get => _interfaceText;
        private set => Set(ref _interfaceText, value);
    }

    public bool IsConnected => IsVpnRunning || _tunnel.State == TunnelState.Connected;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
            QuickConnectCommand.RaiseCanExecuteChanged();
            DpiDetectGameCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedNodeSummary => SelectedNode is null
        ? "Узел не выбран"
        : $"{SelectedNode.Name} · {SelectedNode.Host} · {SelectedNode.Ping} мс · {SelectedNode.Loss}% потерь";

    public string SelectedProcessSummary => SelectedProcess is null
        ? "Игра не выбрана — трафик пойдёт через узел целиком"
        : $"{SelectedProcess.Name} (PID {SelectedProcess.Pid})";

    public int NodeCount => Nodes.Count;

    public bool IsElevated => WindowsRouteManager.IsElevated();

    public string WireGuardPath => _settings.Current.WireGuardExePath;

    // ---------------------------------------------------------- логика

    private void LoadSettingsIntoUi()
    {
        var s = _settings.Current;
        _mode = s.DefaultMode;
        _autoSelect = s.AutoSelectBest;
        _autoDetect = s.AutoDetectEndpoints;
        _subnetsText = string.Join(", ", s.SplitSubnets);
        _feedUrl = s.NodesFeedUrl;
        _dpiTargetHosts = s.DpiTargetHosts;
        _blockedPreset = BlockedLists.Parse(s.BlockedListPreset);
        _dpiTargetPort = s.DpiTargetPort > 0 ? s.DpiTargetPort : 443;
        _dpiGameIps = s.DpiGameIps;
        _dpiGameProcess = s.DpiGameProcess ?? "";
        _dpiGameTcpPorts = s.DpiGameTcpPorts;
        _dpiGameUdpPorts = s.DpiGameUdpPorts;
        _dpiApplyOnConnect = s.DpiApplyOnConnect;
        _dpiAutoUpdate = s.DpiAutoUpdate;
        _dpiCheckUpdates = s.DpiCheckUpdates;
        _closeActionIndex = s.CloseToTray ? 0 : 1;
        _checkAppUpdates = s.CheckAppUpdatesOnStart;
    }

    public void BuildNodeList()
    {
        var selectedId = SelectedNode?.Id;
        Nodes.Clear();
        foreach (var config in _registry.Nodes)
        {
            var status = _probing.GetOrCreateStatus(config);
            var item = new NodeItemViewModel(status);
            Nodes.Add(item);
        }
        ApplyFilter();

        if (selectedId is not null)        {
            SelectedNode = Nodes.FirstOrDefault(n => n.Id == selectedId);
        }
        else
        {
            SelectedNode = Nodes.FirstOrDefault();
        }

        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(IsFullTunnelMode));
        OnPropertyChanged(nameof(IsProcessSplitMode));
        OnPropertyChanged(nameof(IsNetworkSplitMode));
    }

    private void ApplyFilter() => _nodesView.Refresh();

    private void OnStatusUpdated(NodeStatus status)
    {
        void Apply()
        {
            var item = Nodes.FirstOrDefault(n => n.Id == status.Config.Id);
            if (item is null) return;
            item.Refresh();

            if (SelectedNode?.Id == status.Config.Id && status.HasData)
            {
                PingHistory.Add(Math.Round(status.AvgPingMs));
                while (PingHistory.Count > 120) PingHistory.RemoveAt(0);
            }
            if (AutoSelect && !IsConnected)
                SelectBestNode(force: false);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    private void SelectBestNode(bool force)
    {
        var statuses = _probing.Statuses.Where(s => s.Config.Enabled).ToList();
        if (statuses.Count == 0) return;
        var best = NodeSelector.SelectBest(statuses);
        if (best is null) return;

        var item = Nodes.FirstOrDefault(n => n.Id == best.Config.Id);
        if (item is null) return;

        if (force || SelectedNode is null || !IsConnected)
            SelectedNode = item;
    }

    private async Task ConnectAsync()
    {
        // Точки доступа — это VPN-ключи: подключаемся по выбранному ключу.
        var key = SelectedVpnKey?.Key;
        if (key is null)
        {
            bool hasKeys = App.VpnStore.Keys.Count > 0;
            StatusText = hasKeys ? "Локация не выбрана" : "Нет VPN-ключей";
            StateText = hasKeys ? "Выберите локацию" : "Нет ключей";
            StateColor = "#F5A524";
            _log.Warn(hasKeys
                ? "Локация не выбрана — подключение не начато"
                : "Нет сохранённых VPN-ключей — добавьте ключ в левой панели");
            MessageBox.Show(hasKeys
                    ? "Выберите локацию в списке слева и нажмите «Подключить VPN»."
                    : "Добавьте VPN-ключ в левой панели (кнопка «Добавить ключ…») — можно вставить ссылку или адрес подписки.",
                "Нет точек доступа", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        StatusText = "Подключение...";
        StateText = "Подключение";
        StateColor = "#F5A524";

        try
        {
            SaveVpnRouting();
            bool ok = await ConnectVpnCoreAsync(key).ConfigureAwait(true);
            if (!ok)
            {
                StatusText = VpnStatusText;
                StateText = "Ошибка";
                StateColor = "#EF4444";
                return;
            }

            _connectedAt = DateTime.UtcNow;
            StatusText = $"Подключено к {key.Name}";
            StateText = "Подключено";
            StateColor = "#2FBF71";
            InterfaceText = $"VPN · {key.Protocol}";
            RefreshHeaderKeyText();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private NodeConfig? SelectBestAndReturn()
    {
        var statuses = _probing.Statuses.Where(s => s.Config.Enabled).ToList();
        var best = NodeSelector.SelectBest(statuses);
        if (best is not null)
        {
            SelectedNode = Nodes.FirstOrDefault(n => n.Id == best.Config.Id);
        }
        return best?.Config ?? SelectedNode?.Config;
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        StateText = "Отключение";
        try
        {
            // VPN по ключу.
            if (IsVpnRunning)
            {
                await App.Vpn.DisconnectAsync().ConfigureAwait(true);
                RefreshVpnStatus();
            }

            // На всякий случай — туннель узла, если он был поднят из CLI.
            try { await _controller.DisconnectAsync().ConfigureAwait(true); } catch { }

            if (App.ZapretRunner.IsRunning)
            {
                await App.ZapretRunner.StopAsync().ConfigureAwait(true);
                DpiProgressText = "Обход DPI остановлен";
            }

            StatusText = "Отключено";
            StateText = "Отключено";
            StateColor = "#8C95A6";
            InterfaceText = "—";
            _connectedAt = null;
            ElapsedText = "00:00:00";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAllAsync()
    {
        StatusText = "Обновление измерений...";
        foreach (var node in _registry.Nodes.Where(n => n.Enabled).ToList())
        {
            try { await _probing.ProbeRoundAsync(node); }
            catch { }
        }
        StatusText = "Измерения обновлены";
        if (AutoSelect) SelectBestNode(force: false);
    }

    private void UpdateElapsed()
    {
        if (_connectedAt is null) return;
        var span = DateTime.UtcNow - _connectedAt.Value;
        ElapsedText = span.ToString(@"hh\:mm\:ss");
    }

    private int _processRefreshTicks;

    /// <summary>Периодическое обновление списка процессов (раз в 5 секунд).</summary>
    private void Tick()
    {
        UpdateElapsed();
        RefreshDpiStatus();
        RefreshVpnStatus();

        if (++_processRefreshTicks >= 5)
        {
            _processRefreshTicks = 0;
            RefreshProcesses();
        }

        // Качество VPN-ключей меряем раз в 10 секунд (TCP до сервера).
        if (++_vpnProbeTicks >= 10)
        {
            _vpnProbeTicks = 0;
            if (VpnKeyRows.Count > 0 && !_vpnProbing)
            {
                _vpnProbing = true;
                _ = Task.Run(async () =>
                {
                    try { await ProbeVpnKeysAsync(); }
                    catch { }
                    finally { _vpnProbing = false; }
                });
            }
        }
    }

    private int _vpnProbeTicks;
    private volatile bool _vpnProbing;

    private void OnLogEntry(LogEntry entry)
    {
        void Apply()
        {
            Logs.Add(entry);
            while (Logs.Count > 500) Logs.RemoveAt(0);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    private void OnControllerStatusChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnControllerStatusChanged());
            return;
        }

        InterfaceText = _controller.InterfaceAlias ?? "—";
        if (_controller.LastWarning is not null) StatusText = _controller.LastWarning;
    }

    public void RefreshProcesses()
    {
        var list = ProcessInspector.GetProcesses();

        // Обновляем список только если состав процессов изменился — иначе дёргаем UI.
        string signature = string.Join('|', list.Select(p => $"{p.Pid}:{p.Name}"));
        if (signature == _processSignature) return;
        _processSignature = signature;

        var previous = SelectedProcess;
        // ComboBox игрового режима привязан к DpiGameProcess через SelectedValue:
        // очистка списка обнуляет выбор, поэтому запоминаем имя и возвращаем его.
        string gameProcess = _dpiGameProcess ?? "";

        Processes.Clear();
        foreach (var p in list) Processes.Add(p);

        if (previous is not null)
        {
            SelectedProcess = Processes.FirstOrDefault(p => p.Path == previous.Path && p.Name == previous.Name);
        }

        if (gameProcess.Length > 0 && _dpiGameProcess != gameProcess)
            DpiGameProcess = gameProcess;

        BuildVpnTargets();
    }

    /// <summary>Диалог выбора целен обхода: сайты / история браузера / запущенные программы.</summary>
    private void PickDpiTargets()
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var dialog = new DpiTargetPickerWindow(DpiTargetHosts) { Owner = owner };

        if (dialog.ShowDialog() != true) return;

        var domains = dialog.ResultDomains;
        if (domains.Count > 0) DpiTargetHosts = string.Join(", ", domains);
    }

    /// <summary>Выбор сайтов/программ, которые пойдут через VPN — тем же окном, что и для обхода DPI.</summary>
    private void PickVpnTargets()
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var dialog = new DpiTargetPickerWindow(VpnDomainsText, forVpn: true) { Owner = owner };

        if (dialog.ShowDialog() != true) return;

        var domains = dialog.ResultDomains;
        if (domains.Count > 0) VpnDomainsText = string.Join(", ", domains);
    }

    /// <summary>
    /// Принудительная остановка обхода DPI: все процессы zapret и драйвер WinDivert.
    /// Нужен, если движок «залип» и трафик всё ещё перехватывается.
    /// </summary>
    private async Task ForceStopZapretAsync()
    {
        IsDpiBusy = true;
        try
        {
            await App.GameFlowMonitor.StopAsync().ConfigureAwait(true);
            await App.ZapretRunner.ForceStopAllAsync().ConfigureAwait(true);
            if (App.Zapret.IsInstalled)
                await App.GameFlowMonitor.StartAsync().ConfigureAwait(true);
            DpiProgressText = "zapret принудительно остановлен (процессы и драйвер)";
        }
        catch (Exception ex)
        {
            DpiProgressText = "Не удалось остановить zapret: " + ex.Message;
        }
        finally
        {
            if (!App.GameFlowMonitor.IsListening && App.Zapret.IsInstalled)
                await App.GameFlowMonitor.StartAsync().ConfigureAwait(true);
            IsDpiBusy = false;
            RefreshDpiStatus();
        }
    }
    private string _processSignature = "";

    // ---------------------------------------------------------- работа со списком узлов

    private void AddNode()
    {
        var window = new NodeEditWindow();
        window.Owner = Application.Current?.MainWindow;
        if (window.ShowDialog() == true && window.Result is not null)
        {
            _registry.Add(window.Result);
            SaveRegistry();
            BuildNodeList();
            _probing.Start(_registry.Nodes);
        }
    }

    private void EditNode()
    {
        if (SelectedNode is null) return;
        var window = new NodeEditWindow(SelectedNode.Config);
        window.Owner = Application.Current?.MainWindow;
        if (window.ShowDialog() == true && window.Result is not null)
        {
            _registry.Update(window.Result);
            SaveRegistry();
            BuildNodeList();
            _probing.Start(_registry.Nodes);
        }
    }

    private void RemoveNode()
    {
        if (SelectedNode is null) return;
        var config = SelectedNode.Config;
        if (MessageBox.Show($"Удалить узел «{config.Name}»?", "ExitBoost",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _registry.Remove(config.Id);
        SaveRegistry();
        BuildNodeList();
        _probing.Stop();
        _probing.Start(_registry.Nodes);
    }

    private void ImportConfig()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт конфигурации",
            Filter = "Конфигурации (*.conf;*.ovpn)|*.conf;*.ovpn|Все файлы (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        int added = 0;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var node = NodeRegistry.ImportConfigFile(file);
                if (node is null)
                {
                    _log.Warn($"Не удалось разобрать {Path.GetFileName(file)}");
                    continue;
                }
                _registry.Add(node);
                added++;
            }
            catch (Exception ex)
            {
                _log.Error($"Ошибка импорта {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (added > 0)
        {
            SaveRegistry();
            BuildNodeList();
            _probing.Start(_registry.Nodes);
            StatusText = $"Импортировано узлов: {added}";
        }
    }

    private void ExportNodes()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт списка узлов",
            FileName = "nodes.json",
            Filter = "JSON (*.json)|*.json|YAML (*.yaml)|*.yaml"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _registry.Save(dialog.FileName);
            StatusText = "Список узлов сохранён";
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка экспорта: {ex.Message}");
        }
    }

    private async Task RestoreNetworkAsync()
    {
        IsBusy = true;
        StatusText = "Чиню интернет...";
        _log.Info("Запущен ремонт сети");

        // Движок zapret не является причиной сбоя маршрутизации, но на всякий
        // случай останавливаем его перед диагностикой.
        try { if (App.ZapretRunner.IsRunning) await App.ZapretRunner.StopAsync().ConfigureAwait(true); } catch { }

        try
        {
            var report = await _controller.RepairAsync().ConfigureAwait(true);

            foreach (var step in report.Steps)
                _log.Info($"Ремонт: {step}");

            StateText = report.InternetAvailable ? "Интернет работает" : "Интернет недоступен";
            StateColor = report.InternetAvailable ? "#46B87A" : "#E0A44C";
            StatusText = report.InternetAvailable
                ? "Интернет исправен — причина была в приложении и устранена"
                : "Приложение ничего не блокирует — причина вне его (роутер/провайдер)";
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка ремонта сети: {ex.Message}");
            StatusText = "Не удалось выполнить ремонт";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Загрузка публичного списка узлов по URL.</summary>
    private async Task LoadFeedAsync()
    {
        if (string.IsNullOrWhiteSpace(FeedUrl))
        {
            StatusText = "Укажите адрес списка узлов";
            return;
        }

        StatusText = "Загрузка списка узлов...";
        IsBusy = true;
        try
        {
            var nodes = await NodeFeed.FetchAsync(FeedUrl);
            if (nodes.Count == 0)
            {
                StatusText = "Список пуст";
                _log.Warn($"Список по адресу {FeedUrl} пуст");
                return;
            }

            foreach (var node in nodes) _registry.Add(node);
            SaveRegistry();
            BuildNodeList();
            _probing.Start(_registry.Nodes);
            StatusText = $"Загружено узлов: {nodes.Count}";
            _log.Success($"Загружено узлов из публичного списка: {nodes.Count}");
        }
        catch (Exception ex)
        {
            StatusText = "Не удалось загрузить список";
            _log.Error($"Ошибка загрузки списка узлов: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SaveRegistry()
    {
        try
        {
            string path = _settings.Current.NodesFilePath;
            if (string.IsNullOrWhiteSpace(path)) path = AppPaths.DefaultNodesFile;
            _registry.Save(path);
            _settings.Update(s => s.NodesFilePath = path);
        }
        catch (Exception ex)
        {
            _log.Error($"Не удалось сохранить список узлов: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- обход DPI

    private async Task<DpiTarget> BuildDpiTargetAsync()
    {
        var target = new DpiTarget
        {
            Hosts = DpiTargetHosts,
            Port = DpiTargetPort,
            GameTcpPorts = DpiGameTcpPorts,
            GameUdpPorts = DpiGameUdpPorts,
            GameIps = GameIpListFromText(DpiGameIps).ToList()
        };

        // Готовый список передаём отдельным файлом: полный реестр РКН
        // (~1,7 млн строк) не копируется в hostlist.txt.
        if (_blockedPreset != BlockedListPreset.Off)
        {
            string path = BlockedLists.ListPath(_blockedPreset);
            if (File.Exists(path))
            {
                target.ExtraHostListPaths.Add(path);

                // Своих доменов нет — для автоподбора и проверки берём
                // несколько доменов из готового списка. В hostlist они не
                // добавляются: список уже передан отдельным файлом.
                if (target.HostList.Count == 0)
                {
                    var preset = _blockedPreset;
                    IReadOnlyList<string> sample = await Task
                        .Run(() => BlockedLists.Sample(preset, 4))
                        .ConfigureAwait(true);
                    target.ProbeHosts = sample.ToList();
                }
            }
        }

        // Игровой режим: если своих доменов нет, замеряем адреса серверов игры.
        // Для автоподбора это важнее выборки из готового списка.
        if (target.HostList.Count == 0 && target.GameIpList.Count > 0)
            target.ProbeHosts = target.GameIpList.ToList();

        return target;
    }

    /// <summary>Скачать готовый список, если его ещё нет. false — список недоступен.</summary>
    private async Task<bool> EnsureBlockedListAsync()
    {
        if (_blockedPreset == BlockedListPreset.Off) return true;

        string path = BlockedLists.ListPath(_blockedPreset);
        if (File.Exists(path)) return true;

        DpiProgressText = "загрузка готового списка…";
        var info = await BlockedLists
            .EnsureAsync(_blockedPreset, false, message => _log.Info(message))
            .ConfigureAwait(true);

        _blockedListInfo = info;
        if (!info.Ok)
        {
            DpiProgressText = $"Готовый список недоступен: {info.Error}";
            return false;
        }

        string updated = info.UpdatedAt is { } at ? $", обновлён {at:dd.MM.yyyy}" : "";
        BlockedListStatus = $"{BlockedLists.DisplayName(_blockedPreset)}: {info.Count:N0} доменов{updated}";
        return true;
    }

    private void FillDpiStrategies(ZapretCatalog catalog)
    {
        DpiStrategies.Clear();
        foreach (var strategy in catalog.Strategies)
            DpiStrategies.Add(new DpiStrategyItemViewModel(strategy));

        if (!string.IsNullOrEmpty(_settings.Current.DpiStrategyId))
        {
            SelectedDpiStrategy = DpiStrategies.FirstOrDefault(s => s.Strategy.Id == _settings.Current.DpiStrategyId);
        }
    }

    private async Task UpdateZapretAsync()
    {
        IsDpiBusy = true;
        DpiProgressText = "Загрузка движка zapret...";
        try
        {
            // Останавливаем движок: установка перезаписывает его файлы.
            try { await App.ZapretRunner.StopAsync().ConfigureAwait(true); } catch { }

            await App.GameFlowMonitor.StopAsync().ConfigureAwait(true);
            bool ok;
            try
            {
                ok = await App.Zapret.InstallAsync().ConfigureAwait(true);
            }
            finally
            {
                if (App.Zapret.IsInstalled)
                    await App.GameFlowMonitor.StartAsync().ConfigureAwait(true);
            }
            if (!ok)
            {
                DpiUpdateStatus = "Не удалось загрузить zapret"
                    + (App.Zapret.LastError is null ? "" : $": {App.Zapret.LastError}");
                DpiProgressText = "";
                return;
            }

            string catalogPath = Path.Combine(App.Zapret.InstallDirectory, "catalog.json");
            var catalog = ZapretCatalog.BuildDefault();
            catalog.Save(catalogPath);
            App.ZapretCatalog = catalog;
            FillDpiStrategies(catalog);

            var version = App.Zapret.InstalledVersion;
            DpiEngineStatus = version is null
                ? "установлен"
                : $"установлен ({version[..Math.Min(8, version.Length)]})";
            DpiUpdateStatus = $"Стратегий в каталоге: {catalog.Strategies.Count}";
            DpiProgressText = "Готово";
        }
        finally
        {
            IsDpiBusy = false;
        }
    }

    private async Task AutoSelectDpiStrategyAsync()
    {
        if (!App.Zapret.IsInstalled)
        {
            await UpdateZapretAsync();
            if (!App.Zapret.IsInstalled) return;
        }

        if (!await EnsureBlockedListAsync().ConfigureAwait(true)) return;

        var target = await BuildDpiTargetAsync().ConfigureAwait(true);
        if (!target.IsValid)
        {
            DpiProgressText = "Укажите домены через запятую, серверы игры или включите готовый список";
            return;
        }
        if (target.ProbeHostList.Count == 0)
        {
            DpiProgressText = target.IsGameMode
                ? "Для автоподбора игры нужны IP серверов — нажмите «Серверы игры»"
                : "Готовый список пуст — добавьте домены вручную";
            return;
        }

        IsDpiBusy = true;
        try
        {
            DpiProgressText = "Замер без обхода...";
            var probes = await App.DpiOptimizer
                .MeasureTargetAsync(target)
                .ConfigureAwait(true);
            var blocked = DpiOptimizer.SelectBlockedHosts(probes);
            DpiBaselineText = DescribeBaseline(probes);

            // Если всё открывается и так, перебирать нечего: любая «успешная»
            // стратегия была бы случайностью, а не обходом.
            if (blocked.Count == 0)
            {
                DpiBestText = target.IsGameMode
                    ? "Серверы игры открываются без обхода — автоподбор не требуется"
                    : "Домены открываются без обхода — автоподбор не требуется";
                DpiProgressText = "Перебор не нужен";
                return;
            }

            FillDpiStrategies(App.ZapretCatalog);

            // В игровом режиме перебираем только игровые профили: сайтовые
            // стратегии работают по hostlist и к IP/портам игры неприменимы.
            IReadOnlyList<DpiStrategy> candidates = target.IsGameMode
                ? App.ZapretCatalog.Strategies.Where(s => s.IsGame).ToList()
                : App.ZapretCatalog.Strategies;
            if (candidates.Count == 0)
            {
                DpiBestText = "В каталоге нет игровых стратегий";
                DpiProgressText = "Игровые стратегии не найдены";
                return;
            }

            var items = DpiStrategies.ToDictionary(i => i.Strategy.Id, i => i);
            var progress = new Progress<DpiStrategyResult>(r =>
            {
                if (items.TryGetValue(r.Strategy.Id, out var item)) item.Apply(r);
                DpiProgressText = $"Проверено: {r.Strategy.Name} — {r.ResultText}";
            });

            DpiProgressText = $"Перебор стратегий zapret (заблокировано: {blocked.Count})...";
            var results = await App.DpiOptimizer
                .OptimizeAsync(candidates, target, 2, progress, hosts: blocked)
                .ConfigureAwait(true);

            foreach (var r in results)
                if (items.TryGetValue(r.Strategy.Id, out var item)) item.Apply(r);

            var best = results.FirstOrDefault(r => r.Success);
            if (best is null)
            {
                DpiBestText = target.IsGameMode
                    ? "Ни одна стратегия не помогла — серверы игры, скорее всего, блокируются по IP: включите VPN"
                    : "Ни одна стратегия не помогла — проверьте домены и DNS";
                DpiProgressText = "Перебор завершён";
                return;
            }

            DpiBestText = $"{best.Strategy.Name} · {best.ResultText}";
            SelectedDpiStrategy = items.GetValueOrDefault(best.Strategy.Id);
            _settings.Update(s => s.DpiStrategyId = best.Strategy.Id);
            DpiProgressText = $"Перебор завершён, лучшая: {best.Strategy.Name}";

            await ApplyDpiStrategyAsync().ConfigureAwait(true);
        }
        finally
        {
            IsDpiBusy = false;
        }
    }

    private async Task ApplyDpiStrategyAsync()
    {
        if (SelectedDpiStrategy is null) return;

        IsDpiBusy = true;
        try
        {
            // Готовый список мог ещё не скачаться — без него применение
            // молча шло бы только по своим доменам.
            if (!await EnsureBlockedListAsync().ConfigureAwait(true)) return;

            var target = await BuildDpiTargetAsync().ConfigureAwait(true);
            if (!target.IsValid)
            {
                DpiProgressText = "Укажите домены через запятую, серверы игры или включите готовый список";
                return;
            }

            string? notApplicable = SelectedDpiStrategy.Strategy.Validate(target);
            if (notApplicable is not null)
            {
                DpiProgressText = notApplicable;
                return;
            }

            bool ok = await App.ZapretRunner.StartAsync(SelectedDpiStrategy.Strategy, target).ConfigureAwait(true);
            DpiProgressText = ok
                ? $"Применено: {SelectedDpiStrategy.Name}"
                : "Не удалось запустить движок";
            if (ok) _settings.Update(s => s.DpiStrategyId = SelectedDpiStrategy.Strategy.Id);
        }
        finally
        {
            IsDpiBusy = false;
        }
    }

    private async Task StopDpiAsync()
    {
        await App.ZapretRunner.StopAsync().ConfigureAwait(true);
        DpiProgressText = "Обход остановлен";
    }

    private async Task CheckDpiAsync()
    {
        if (!await EnsureBlockedListAsync().ConfigureAwait(true)) return;

        var target = await BuildDpiTargetAsync().ConfigureAwait(true);
        if (!target.IsValid)
        {
            DpiProgressText = "Укажите домены через запятую, серверы игры или включите готовый список";
            return;
        }
        if (target.ProbeHostList.Count == 0)
        {
            DpiProgressText = target.IsGameMode
                ? "Для проверки игры нужны IP серверов — нажмите «Серверы игры»"
                : "Нет доменов для проверки — добавьте домен или включите готовый список";
            return;
        }

        IsDpiBusy = true;
        try
        {
            bool wasRunning = App.ZapretRunner.IsRunning;

            DpiProgressText = "Проверка текущего состояния...";
            var probes = await App.DpiOptimizer
                .MeasureTargetAsync(target)
                .ConfigureAwait(true);
            var blocked = DpiOptimizer.SelectBlockedHosts(probes);
            DpiBaselineText = DescribeBaseline(probes);

            if (blocked.Count == 0)
            {
                DpiBestText = target.IsGameMode
                    ? "Серверы игры открываются без обхода — обход не требуется"
                    : "Домены открываются без обхода — обход не требуется";
                DpiProgressText = "Проверка завершена";
                return;
            }

            if (wasRunning && SelectedDpiStrategy is not null)
            {
                // Замер baseline остановил движок — поднимаем стратегию заново,
                // иначе «с обходом» мерилось бы уже без обхода.
                bool started = await App.ZapretRunner
                    .StartAsync(SelectedDpiStrategy.Strategy, target)
                    .ConfigureAwait(true);
                if (!started)
                {
                    DpiBestText = "не удалось запустить обход";
                }
                else
                {
                    var withBypass = await DpiOptimizer
                        .ProbeAsync(target, blocked[0], CancellationToken.None)
                        .ConfigureAwait(true);

                    // У игровых серверов TLS нет: показываем время TCP-коннекта.
                    bool gameHost = target.IsGameMode &&
                        target.GameIpList.Contains(blocked[0], StringComparer.OrdinalIgnoreCase);
                    double ms = gameHost ? withBypass.TcpMs : withBypass.TlsMs;

                    DpiBestText = withBypass.Ok
                        ? $"{ms:F0} мс с обходом ({SelectedDpiStrategy.Name})"
                        : $"с обходом не работает: {withBypass.Error}";
                }
            }
            DpiProgressText = "Проверка завершена";
        }
        finally
        {
            IsDpiBusy = false;
        }
    }

    /// <summary>Понятное описание базового замера: что заблокировано, что открыто.</summary>
    private static string DescribeBaseline(IReadOnlyList<DpiHostProbe> probes)
    {
        var blocked = probes.Where(p => !p.Ok).Select(p => p.Host).ToList();
        if (blocked.Count == 0)
            return probes.Count == 0
                ? "нет доменов для замера"
                : $"все домены открываются без обхода ({probes.Count})";

        var open = probes.Where(p => p.Ok).Select(p => p.Host).ToList();
        string openText = open.Count > 0 ? $" · открываются: {string.Join(", ", open)}" : "";
        return $"заблокировано {blocked.Count} из {probes.Count}: {string.Join(", ", blocked)}{openText}";
    }

    // ================================================== VPN по ключу (sing-box)

    /// <summary>Строки таблицы VPN-ключей (в том же виде, что и узлы-ретрансляторы).</summary>
    public ObservableCollection<VpnKeyItemViewModel> VpnKeyRows { get; } = new();

    /// <summary>Таблица локаций: строки, сгруппированные по стране (группы сворачиваются).</summary>
    public ICollectionView VpnKeysView { get; }

    /// <summary>Сколько ключей сохранено (счётчик рядом с заголовком в панели ретрансляторов).</summary>
    public int VpnKeyCount => VpnKeyRows.Count;

    private VpnKeyItemViewModel? _selectedVpnKey;
    public VpnKeyItemViewModel? SelectedVpnKey
    {
        get => _selectedVpnKey;
        set
        {
            if (!Set(ref _selectedVpnKey, value)) return;
            RefreshHeaderKeyText();
            VpnConnectCommand.RaiseCanExecuteChanged();
            ConnectCommand.RaiseCanExecuteChanged();
            VpnRemoveKeyCommand.RaiseCanExecuteChanged();
        }
    }

    private readonly VpnKeyProber _vpnProber = new();

    private string _vpnStatusText = "не активен";
    public string VpnStatusText
    {
        get => _vpnStatusText;
        set => Set(ref _vpnStatusText, value);
    }

    private string _vpnStatusColor = "#8C95A6";
    public string VpnStatusColor
    {
        get => _vpnStatusColor;
        set => Set(ref _vpnStatusColor, value);
    }

    private string _headerKeyText = "ключ не выбран";
    /// <summary>Ключ, через который идёт (или пойдёт) трафик: в шапке видно сам ключ вместе с его локацией.</summary>
    public string HeaderKeyText
    {
        get => _headerKeyText;
        private set => Set(ref _headerKeyText, value);
    }

    /// <summary>Шапка: подключённый ключ, иначе выбранный в таблице.</summary>
    private void RefreshHeaderKeyText()
    {
        bool running = App.Vpn.IsRunning || App.Vpn.IsCoreRunning;
        string? name = running ? App.Vpn.ActiveKey?.Name : null;
        name ??= SelectedVpnKey?.Name;
        HeaderKeyText = string.IsNullOrWhiteSpace(name) ? "ключ не выбран" : name;
    }

    private readonly SpeedTester _speedTester = new();

    private string _speedTestText = "";
    /// <summary>Ход и результат теста скорости.</summary>
    public string SpeedTestText
    {
        get => _speedTestText;
        set => Set(ref _speedTestText, value);
    }

    private bool _isSpeedTestRunning;
    public bool IsSpeedTestRunning
    {
        get => _isSpeedTestRunning;
        private set
        {
            if (!Set(ref _isSpeedTestRunning, value)) return;
            SpeedTestCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isVpnRunning;
    public bool IsVpnRunning
    {
        get => _isVpnRunning;
        set => Set(ref _isVpnRunning, value);
    }

    /// <summary>Защита от двойного клика: подключение уже идёт.</summary>
    private bool _vpnBusy;

    private bool _vpnModeIsFull;
    /// <summary>true — весь трафик через VPN (нужно подтверждение), false — только выбранные сайты/программы.</summary>
    public bool VpnModeIsFull
    {
        get => _vpnModeIsFull;
        set
        {
            if (value && !_vpnModeIsFull)
            {
                var answer = MessageBox.Show(
                    "Весь трафик пойдёт через VPN-сервер.\n" +
                    "Интернет целиком станет зависеть от чужого сервера — если он отвалится, не будет сети.\n\n" +
                    "Локальная сеть (роутер, принтер, ваши программы) — не затрагиваются, обмануть их нельзя.",
                    "Весь трафик через VPN?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    OnPropertyChanged(nameof(VpnModeIsFull)); // вернуть галочку
                    return;
                }
            }
            if (Set(ref _vpnModeIsFull, value))
                SaveVpnRouting();
        }
    }

    public bool VpnModeIsSplit
    {
        get => !_vpnModeIsFull;
        set { if (Set(ref _vpnModeIsFull, !value)) SaveVpnRouting(); }
    }

    private string _vpnDomainsText = "";
    public string VpnDomainsText
    {
        get => _vpnDomainsText;
        set
        {
            if (!Set(ref _vpnDomainsText, value)) return;
            SaveVpnRouting();
            OnPropertyChanged(nameof(VpnSelectedSites));
            OnPropertyChanged(nameof(VpnSelectedSitesCount));
        }
    }

    private string _vpnProcsText = "";
    public string VpnProcsText
    {
        get => _vpnProcsText;
        set
        {
            if (Set(ref _vpnProcsText, value))
            {
                SaveVpnRouting();
                OnPropertyChanged(nameof(VpnProgramsSummary));
                OnPropertyChanged(nameof(VpnSelectedPrograms));
                OnPropertyChanged(nameof(VpnSelectedSites));
            }
        }
    }

    public IEnumerable<string> VpnSelectedPrograms => VpnProcsText
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string _vpnMtuText = "1400";
    /// <summary>MTU TUN-адаптера: 1400 — PPPoE, 1500 — обычный Ethernet.</summary>
    public string VpnMtuText
    {
        get => _vpnMtuText;
        set
        {
            if (!Set(ref _vpnMtuText, value)) return;
            if (int.TryParse(value.Trim(), out int mtu) && mtu is >= 1280 and <= 9000)
            {
                App.VpnStore.Routing.Mtu = mtu;
                App.VpnStore.Save();
            }
        }
    }

    public IEnumerable<string> VpnSelectedSites => VpnDomainsText
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int VpnSelectedSitesCount => VpnSelectedSites.Count();

    private void RemoveVpnSite(object? parameter)
    {
        if (parameter is not string site) return;
        VpnDomainsText = string.Join(", ", VpnSelectedSites
            .Where(x => !x.Equals(site, StringComparison.OrdinalIgnoreCase)));
    }

    private void RemoveVpnProgram(object? parameter)
    {
        if (parameter is not string program) return;
        VpnProcsText = string.Join(", ", VpnSelectedPrograms
            .Where(x => !x.Equals(program, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Поиск по списку «программы и сайты» — прямо в выпадающем списке.</summary>
    private string _vpnTargetSearch = "";
    public string VpnTargetSearch
    {
        get => _vpnTargetSearch;
        set
        {
            if (Set(ref _vpnTargetSearch, value)) VpnTargetsView.Refresh();
        }
    }

    /// <summary>Программы и сайты, подходящие под поиск (для выпадающего списка).</summary>
    public ICollectionView VpnTargetsView { get; }
    public ICollectionView DpiTargetsView { get; }

    /// <summary>Всё, что можно добавить: запущенные программы + популярные сайты.</summary>
    public ObservableCollection<VpnTargetPickItem> VpnTargets { get; } = new();

    private string _dpiTargetSearch = "";
    public string DpiTargetSearch
    {
        get => _dpiTargetSearch;
        set
        {
            if (Set(ref _dpiTargetSearch, value)) DpiTargetsView.Refresh();
        }
    }

    private VpnTargetPickItem? _selectedDpiTarget;
    public VpnTargetPickItem? SelectedDpiTarget
    {
        get => _selectedDpiTarget;
        set
        {
            if (!Set(ref _selectedDpiTarget, value) || value is null) return;

            IEnumerable<string> domains = value.IsSite
                ? [value.Value]
                : PickerViewModel.DomainsForProcess(value.Value);
            AddDpiDomains(domains);

            _selectedDpiTarget = null;
            OnPropertyChanged(nameof(SelectedDpiTarget));
            DpiTargetSearch = "";
        }
    }

    private VpnTargetPickItem? _selectedVpnTarget;
    /// <summary>Выбранный пункт: программа идёт в «программы через VPN», сайт — в «сайты через VPN».</summary>
    public VpnTargetPickItem? SelectedVpnTarget
    {
        get => _selectedVpnTarget;
        set
        {
            if (!Set(ref _selectedVpnTarget, value)) return;
            if (value is null) return;

            if (value.IsSite) AddVpnSite(value.Value);
            else AddVpnProgram(value.Value);

            // Сброс поиска и выбора, чтобы тот же пункт можно было выбрать снова.
            _selectedVpnTarget = null;
            OnPropertyChanged(nameof(SelectedVpnTarget));
            VpnTargetSearch = "";
        }
    }

    /// <summary>Список выбранных программ (через запятую) — для подсказки под списком.</summary>
    public string VpnProgramsSummary =>
        VpnProcsText.Length == 0
            ? "Программы не выбраны — трафик идёт по правилам сайтов"
            : "Через VPN: " + VpnProcsText;

    /// <summary>Пересобрать список программ и сайтов (после запуска/остановки программ).</summary>
    public void BuildVpnTargets()
    {
        VpnTargets.Clear();

        foreach (var p in Processes)
        {
            string exe = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name : p.Name + ".exe";
            VpnTargets.Add(new VpnTargetPickItem
            {
                IsSite = false,
                Title = exe,
                Subtitle = string.IsNullOrWhiteSpace(p.WindowTitle) ? "Запущенная программа" : p.WindowTitle,
                Value = exe
            });
        }

        foreach (var (name, hint, _, domains) in PickerViewModel.BuildServices())
        {
            foreach (string domain in domains)
            {
                VpnTargets.Add(new VpnTargetPickItem
                {
                    IsSite = true,
                    Title = domain,
                    Subtitle = name + " — " + hint,
                    Value = domain
                });
            }
        }

        VpnTargetsView.Refresh();
    }

    private bool MatchesVpnTarget(VpnTargetPickItem? item)
    {
        // В списке выбора VPN показываем только приложения. Сайты вводятся
        // вручную в поле «Сайты через VPN».
        if (item is null || item.IsSite) return false;
        string filter = _vpnTargetSearch.Trim();
        if (filter.Length == 0) return true;
        return item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesDpiTarget(VpnTargetPickItem? item)
    {
        // В разделе DPI выбираются только запущенные приложения.
        if (item is null || item.IsSite) return false;
        string filter = _dpiTargetSearch.Trim();
        if (filter.Length == 0) return true;
        return item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void AddDpiDomains(IEnumerable<string> domains)
    {
        var existing = DpiTargetHosts
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (string domain in domains.Where(d => !string.IsNullOrWhiteSpace(d)))
            if (!existing.Contains(domain, StringComparer.OrdinalIgnoreCase)) existing.Add(domain);
        DpiTargetHosts = string.Join(", ", existing);
    }

    private void AddVpnSite(string domain)
    {
        var list = VpnDomainsText
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (list.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase))) return;

        list.Add(domain);
        VpnDomainsText = string.Join(", ", list);
    }

    private void AddVpnProgram(string exe)
    {
        var list = VpnProcsText
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!list.Any(p => p.Equals(exe, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(exe);
            VpnProcsText = string.Join(", ", list);
        }

        OnPropertyChanged(nameof(VpnProgramsSummary));
    }

    /// <summary>Переход между страницами бокового меню.</summary>
    private void NavigateTo(object? parameter)
    {
        if (parameter is null) return;
        if (!int.TryParse(parameter.ToString(), out int page)) return;
        SelectedPageIndex = Math.Clamp(page, 0, 5);
    }

    /// <summary>
    /// Плитка «популярные сервисы»: добавляет домены в список VPN
    /// и переключает режим на «только выбранные сайты и программы».
    /// </summary>
    private void AddQuickService(object? parameter)
    {
        if (parameter is not string raw || raw.Length == 0) return;

        string[] domains = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string domain in domains) AddVpnSite(domain);

        if (VpnModeIsFull) VpnModeIsSplit = true;

        string names = string.Join(", ", domains);
        StatusText = $"В список VPN добавлено: {names}. Переподключите VPN, чтобы применить.";
        _log.Info("Быстрое действие: в список VPN добавлено " + names);
    }

    private static readonly string[] DefaultVpnDomains =
        ["youtube.com", "discord.com", "discord.gg", "googlevideo.com", "chatgpt.com", "openai.com"];

    /// <summary>Первичная загрузка ключей и правил маршрутизации из хранилища.</summary>
    private void VpnReloadFromStore()
    {
        string? selectedId = _selectedVpnKey?.Id;

        VpnKeyRows.Clear();
        foreach (var key in App.VpnStore.Keys) VpnKeyRows.Add(new VpnKeyItemViewModel(key));

        // Ключ не выбирается автоматически: локацию пользователь выбирает сам.
        SelectedVpnKey = selectedId is null ? null : VpnKeyRows.FirstOrDefault(r => r.Id == selectedId);

        VpnDomainsText = App.VpnStore.Routing.Domains.Count > 0
            ? string.Join(", ", App.VpnStore.Routing.Domains)
            : string.Join(", ", DefaultVpnDomains);
        VpnProcsText = string.Join(", ", App.VpnStore.Routing.Processes);
        _vpnModeIsFull = App.VpnStore.Routing.Mode == "full"; // без диалога при загрузке
        _vpnMtuText = App.VpnStore.Routing.Mtu.ToString();
        OnPropertyChanged(nameof(VpnMtuText));
        OnPropertyChanged(nameof(VpnModeIsFull));
        OnPropertyChanged(nameof(VpnModeIsSplit));
        RefreshVpnStatus();
    }

    private void SaveVpnRouting()
    {
        var store = App.VpnStore;
        store.Routing.Mode = VpnModeIsFull ? "full" : "split";
        store.Routing.Domains = VpnDomainsText
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        store.Routing.Processes = VpnProcsText
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        store.Save();
    }

    private void AddVpnKey()
    {
        var dialog = new VpnKeyDialogWindow
        {
            Owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        };
        if (dialog.ShowDialog() is not true || dialog.ResultKeys.Count == 0) return;

        string subscriptionName = dialog.SubscriptionName;
        foreach (var key in dialog.ResultKeys)
        {
            if (App.VpnStore.Keys.Any(k => k.RawLink == key.RawLink)) continue;
            key.SubscriptionName = subscriptionName;
            App.VpnStore.Keys.Add(key);
        }
        App.VpnStore.Save();
        VpnReloadFromStore();

        // Только что добавленный ключ сразу выделяем — но не выбираем ничего за пользователя.
        var addedIds = dialog.ResultKeys.Select(k => k.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SelectedVpnKey = VpnKeyRows.FirstOrDefault(r => addedIds.Contains(r.Id));
    }

    private void RemoveVpnKey()
    {
        var row = _selectedVpnKey;
        if (row is null) return;
        var answer = MessageBox.Show($"Удалить ключ «{row.Name}»?", "Удаление ключа",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        App.VpnStore.Keys.RemoveAll(k => k.Id == row.Id);
        App.VpnStore.Save();
        VpnReloadFromStore();
    }

    /// <summary>Измерить доступность всех VPN-ключей (пинг, потери, качество).</summary>
    private async Task ProbeVpnKeysAsync()
    {
        var rows = VpnKeyRows.ToList();
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            try
            {
                var metrics = await _vpnProber.ProbeAsync(row.Key, 2, 1500).ConfigureAwait(true);
                row.Apply(metrics);
            }
            catch { }
        }
    }

    private async Task VpnConnectAsync()
    {
        // Статус «работает» обновляется раз в секунду — без этого флага
        // повторный клик успевал запустить второе подключение поверх первого.
        if (_vpnBusy) return;

        var key = SelectedVpnKey?.Key;
        if (key is null)
        {
            VpnStatusText = "выберите локацию для подключения";
            VpnStatusColor = "#F5A524";
            StateText = "Выберите локацию";
            StateColor = "#F5A524";
            StatusText = "Сначала выберите локацию на странице «Локации»";
            SelectedPageIndex = 1;
            return;
        }

        _vpnBusy = true;
        VpnConnectCommand.RaiseCanExecuteChanged();
        VpnDisconnectCommand.RaiseCanExecuteChanged();
        try
        {
            SaveVpnRouting();
            VpnStatusText = "подключение...";
            VpnStatusColor = "#F5A524";
            StateText = "Подключение";
            StateColor = "#F5A524";
            StatusText = $"Подключение к {key.Name}…";
            if (await ConnectVpnCoreAsync(key).ConfigureAwait(true))
            {
                _connectedAt = DateTime.UtcNow;
                StateText = "Подключено";
                StateColor = "#2FBF71";
                StatusText = $"Подключено к {key.Name}";
                InterfaceText = $"VPN · {key.Protocol}";
            }
            else
            {
                StateText = "Ошибка";
                StateColor = "#EF4444";
                StatusText = VpnStatusText;
            }
        }
        catch (Exception ex)
        {
            VpnStatusText = "ошибка: " + ex.Message;
            VpnStatusColor = "#EF4444";
            StateText = "Ошибка";
            StateColor = "#EF4444";
            StatusText = VpnStatusText;
            _log.Error("Ошибка подключения VPN: " + ex);
        }
        finally
        {
            _vpnBusy = false;
            VpnConnectCommand.RaiseCanExecuteChanged();
            VpnDisconnectCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Двойной клик по локации: подключение только выбранного VPN-ключа.</summary>
    private async Task QuickConnectAsync()
    {
        if (IsBusy) return;

        var target = SelectedVpnKey?.Key;
        if (target is null) return;

        if (IsVpnRunning)
        {
            if (App.Vpn.ActiveKey?.Id == target.Id) return; // уже подключены к этой локации
            // Отключение теперь дожидается выхода ядра и освобождения TUN —
            // обновляем статус сразу, не полагаясь на секундный таймер.
            await App.Vpn.DisconnectAsync().ConfigureAwait(true);
            RefreshVpnStatus();
        }

        if (!IsVpnRunning)
            await VpnConnectAsync().ConfigureAwait(true);
        if (!IsVpnRunning) return;
    }

    /// <summary>Подключение ядра VPN по ключу (без диалогов — для общей кнопки «Подключить»).</summary>
    private async Task<bool> ConnectVpnCoreAsync(VpnKey key)
    {
        // Готовый список добавляем к доменам пользователя, не меняя их в интерфейсе:
        // выключение переключателя сразу возвращает прежний набор.
        var routing = App.VpnStore.Routing.Clone();
        if (_blockedPreset != BlockedListPreset.Off && routing.Mode != "full")
        {
            var preset = _blockedPreset;
            var presetDomains = await Task.Run(() => BlockedLists.ReadDomains(preset)).ConfigureAwait(true);
            if (presetDomains.Count > 0)
            {
                routing.Domains = BlockedLists.Merge(routing.Domains, presetDomains);
                _log.Info($"VPN: добавлен готовый список «{BlockedLists.DisplayName(preset)}» ({presetDomains.Count:N0} доменов)");
                if (presetDomains.Count > 200_000)
                    _log.Warn("Список очень большой — ядро VPN будет стартовать дольше обычного");
            }
        }

        var result = await App.Vpn.ConnectAsync(key, routing).ConfigureAwait(true);
        if (!result.Ok)
        {
            VpnStatusText = "ошибка: " + result.Error;
            VpnStatusColor = "#EF4444";
            _log.Warn("VPN по ключу не подключился: " + result.Error);
            return false;
        }
        RefreshVpnStatus();
        return true;
    }

    private async Task VpnDisconnectAsync()
    {
        await App.Vpn.DisconnectAsync().ConfigureAwait(true);
        RefreshVpnStatus();
    }

    private async Task VpnInstallCoreAsync()
    {
        if (IsVpnRunning) return;
        VpnStatusText = "скачиваю ядро sing-box…";
        VpnStatusColor = "#F5A524";
        bool ok = await App.Vpn.InstallAsync().ConfigureAwait(true);
        if (ok)
        {
            VpnStatusText = "ядро sing-box установлено";
            VpnStatusColor = "#2FBF71";
        }
        else
        {
            VpnStatusText = "не удалось скачать ядро — смотрите журнал; попробуйте ещё раз";
            VpnStatusColor = "#EF4444";
        }
    }

    /// <summary>Раз в секунду: работает ли ядро VPN сейчас.</summary>
    private void RefreshVpnStatus()
    {
        bool running = App.Vpn.IsRunning || App.Vpn.IsCoreRunning;
        if (Set(ref _isVpnRunning, running, nameof(IsVpnRunning)))
        {
            VpnConnectCommand.RaiseCanExecuteChanged();
            VpnDisconnectCommand.RaiseCanExecuteChanged();
            QuickConnectCommand.RaiseCanExecuteChanged();
            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
        }

        if (running)
        {
            _connectedAt ??= DateTime.UtcNow;

            // Окно открылось при уже работающем VPN: подставляем активный ключ,
            // чтобы шапка и кнопка питания показывали реальную локацию.
            if (SelectedVpnKey is null && App.Vpn.ActiveKey is not null)
                SelectedVpnKey = VpnKeyRows.FirstOrDefault(k => k.Id == App.Vpn.ActiveKey.Id);

            VpnStatusText = App.Vpn.ActiveKey is null
                ? "VPN работает"
                : $"VPN работает: {App.Vpn.ActiveKey.Name}";
            VpnStatusColor = "#2FBF71";

            // Шапка приложения показывает то же состояние.
            StateText = "Подключено";
            StateColor = "#2FBF71";
            StatusText = $"Подключено к {App.Vpn.ActiveKey?.Name ?? "VPN"}";
            InterfaceText = "VPN";
        }
        else if (_vpnStatusText.StartsWith("ошибка"))
        {
            // сохраняем текст ошибки, но ядро не работает
        }
        else
        {
            VpnStatusText = "не активен";
            VpnStatusColor = "#8C95A6";

            // Ядро остановилось (в том числе вручную) — таймер соединения больше не тикает.
            if (StateText is "Подключено" or "Подключение")
            {
                StateText = "Отключено";
                StateColor = "#8C95A6";
                _connectedAt = null;
                ElapsedText = "00:00:00";
                InterfaceText = "—";
            }
        }

        RefreshHeaderKeyText();
    }

    /// <summary>Замер скорости через текущее соединение (в том числе VPN).</summary>
    private async Task RunSpeedTestAsync()
    {
        if (IsSpeedTestRunning) return;

        IsSpeedTestRunning = true;
        SpeedTestText = "тест скорости: подготовка…";
        try
        {
            var progress = new Progress<string>(m => SpeedTestText = "тест скорости: " + m);
            var result = await _speedTester.RunAsync(progress).ConfigureAwait(true);
            SpeedTestText = result.Ok
                ? "скорость: " + result.Text
                : "тест скорости — " + result.Text;
            if (result.Ok)
            {
                StatDownloadText = $"{result.DownloadMbps:0.0} Мбит/с";
                StatUploadText = $"{result.UploadMbps:0.0} Мбит/с";
            }
            else
            {
                StatDownloadText = "—";
                StatUploadText = "—";
            }
            _log.Info("Тест скорости: " + result.Text);
        }
        catch (Exception ex)
        {
            SpeedTestText = "тест скорости — ошибка: " + ex.Message;
            _log.Warn("Тест скорости не удался: " + ex.Message);
        }
        finally
        {
            IsSpeedTestRunning = false;
        }
    }

    // ================================================ обновления программы

    /// <summary>Текущая версия программы.</summary>
    public static string CurrentVersion =>
        (typeof(MainViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0)).ToString(3);

    public string CurrentVersionText => "Версия " + CurrentVersion;

    /// <summary>Репозиторий обновлений зашит в программу: GitLab и его зеркало GitHub.</summary>
    public string UpdateRepo => AppSettings.OfficialUpdateRepo;

    /// <summary>Источник обновлений для отображения.</summary>
    public string UpdateSourceText => "gitlab.com/" + UpdateRepo + " · зеркало github.com/" + UpdateRepo;

    private bool _checkAppUpdates = true;
    public bool CheckAppUpdatesOnStart
    {
        get => _checkAppUpdates;
        set
        {
            if (!Set(ref _checkAppUpdates, value)) return;
            _settings.Update(s => s.CheckAppUpdatesOnStart = value);
        }
    }

    private string _updateStatusText = "";
    public string UpdateStatusText
    {
        get => _updateStatusText;
        set => Set(ref _updateStatusText, value);
    }

    private bool _isUpdateBusy;
    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set
        {
            if (!Set(ref _isUpdateBusy, value)) return;
            UpdateCheckCommand.RaiseCanExecuteChanged();
            UpdateInstallCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isUpdateDownloading;
    public bool IsUpdateDownloading
    {
        get => _isUpdateDownloading;
        private set
        {
            if (!Set(ref _isUpdateDownloading, value)) return;
            UpdateCancelCommand.RaiseCanExecuteChanged();
        }
    }

    private string _updateResultText = "";
    public string UpdateResultText
    {
        get => _updateResultText;
        private set
        {
            if (!Set(ref _updateResultText, value)) return;
            OnPropertyChanged(nameof(HasUpdateResult));
        }
    }

    public bool HasUpdateResult => _updateResultText.Length > 0;

    private string _updateNotesText = "";
    public string UpdateNotesText
    {
        get => _updateNotesText;
        private set
        {
            if (!Set(ref _updateNotesText, value)) return;
            OnPropertyChanged(nameof(HasUpdateNotes));
        }
    }

    public bool HasUpdateNotes => _updateNotesText.Length > 0;

    private string _lastUpdateLogPath = "";
    public bool HasUpdateLog => _lastUpdateLogPath.Length > 0;

    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _updateCts;

    /// <summary>Найдено ли обновление (для показа кнопки «Установить»).</summary>
    public bool IsUpdateAvailable => _pendingUpdate is not null;

    public string UpdateAvailableText => _pendingUpdate is null
        ? ""
        : $"доступна версия {_pendingUpdate.Version} ({_pendingUpdate.SizeText})"
          + (_pendingUpdate.Mandatory ? " · обязательное" : "");

    public RelayCommand UpdateCheckCommand { get; }
    public RelayCommand UpdateInstallCommand { get; }
    public RelayCommand UpdateOpenPageCommand { get; }
    public RelayCommand UpdateCancelCommand { get; }
    public RelayCommand UpdateOpenLogCommand { get; }

    private bool _updateCheckedOnStart;

    /// <summary>Проверка обновлений при запуске — один раз за сеанс.</summary>
    public async Task CheckUpdatesOnStartupAsync()
    {
        if (_updateCheckedOnStart) return;
        _updateCheckedOnStart = true;

        ShowLastUpdateResult();

        if (!CheckAppUpdatesOnStart) return;
        await CheckAppUpdateAsync(manual: false).ConfigureAwait(true);
    }

    private void ShowLastUpdateResult()
    {
        var result = UpdateService.ReadAndClearResult();
        if (result is null) return;

        _lastUpdateLogPath = result.LogPath;
        OnPropertyChanged(nameof(HasUpdateLog));
        UpdateOpenLogCommand.RaiseCanExecuteChanged();

        if (result.IsSuccess)
        {
            UpdateResultText = $"Обновление {result.Version} установлено.";
            _log.Info($"Обновление {result.Version} установлено");
        }
        else
        {
            UpdateResultText = $"Обновление {result.Version} не установлено: {result.Error}";
            _log.Warn($"Обновление {result.Version} не установлено: {result.Error}");
        }
    }

    private async Task CheckAppUpdateAsync(bool manual)
    {
        if (IsUpdateBusy) return;

        if (!UpdateChecker.TryParseRepo(UpdateRepo, out string owner, out string repo))
        {
            UpdateStatusText = "репозиторий обновлений не задан";
            if (manual)
            {
                MessageBox.Show(
                    "Официальный репозиторий обновлений задан в программе и не должен быть пустым.",
                    "Обновления", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        IsUpdateBusy = true;
        UpdateStatusText = "проверяю обновления…";
        try
        {
            UpdateService.CleanupOldDownloads();
            var result = await UpdateService.CheckAsync(owner, repo, CurrentVersion).ConfigureAwait(true);

            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    SetPendingUpdate(null);
                    UpdateStatusText = $"обновлений нет — у вас последняя версия {CurrentVersion}";
                    _log.Info($"Обновления: у вас последняя версия {CurrentVersion}");
                    break;

                case UpdateCheckStatus.Available:
                    var info = result.Update!;
                    SetPendingUpdate(info);
                    UpdateStatusText = $"доступна версия {info.Version} ({info.SizeText})"
                                       + (info.Mandatory ? " · обязательное обновление" : "")
                                       + (info.Source == UpdateSource.LegacyRelease
                                           ? " · старый формат релиза, без SHA-256"
                                           : "");
                    _log.Info($"Обновления: доступна версия {info.Version} (сейчас {CurrentVersion}), источник: {info.Source}");
                    break;

                case UpdateCheckStatus.Failed:
                    SetPendingUpdate(null);
                    UpdateStatusText = "не удалось проверить обновления: " + result.Error;
                    _log.Warn("Проверка обновлений не удалась: " + result.Error);
                    if (manual)
                    {
                        MessageBox.Show(
                            "Не удалось проверить обновления:\n" + result.Error,
                            "Обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = "не удалось проверить обновления: " + ex.Message;
            _log.Warn("Проверка обновлений не удалась: " + ex.Message);
            if (manual)
            {
                MessageBox.Show("Не удалось проверить обновления:\n" + ex.Message,
                    "Обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private void SetPendingUpdate(UpdateInfo? info)
    {
        _pendingUpdate = info;
        UpdateNotesText = info?.Notes ?? "";
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(UpdateAvailableText));
        UpdateInstallCommand.RaiseCanExecuteChanged();
        UpdateOpenPageCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Скачать архив, запустить отдельный updater и закрыть приложение.</summary>
    private async Task InstallUpdateAsync()
    {
        var info = _pendingUpdate;
        if (info is null || IsUpdateBusy) return;

        if (!info.HasHash)
        {
            var answer = MessageBox.Show(
                "В этом релизе нет SHA-256. Проверить целостность архива можно только по размеру.\n\n" +
                "Продолжить установку?",
                "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        IsUpdateBusy = true;
        IsUpdateDownloading = true;
        _updateCts = new CancellationTokenSource();
        UpdateStatusText = "подготовка обновления…";

        try
        {
            var progress = new Progress<UpdateProgress>(p => UpdateStatusText = FormatUpdateProgress(p));
            var downloaded = await UpdateService.DownloadAsync(info, progress, _updateCts.Token)
                .ConfigureAwait(true);

            UpdateStatusText = "проверяю и запускаю установку…";
            string dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string exe = Environment.ProcessPath ?? Path.Combine(dir, "ExitBoost.exe");

            var launch = UpdateService.LaunchUpdater(
                info, downloaded.Path, dir, exe, Environment.ProcessId);
            _lastUpdateLogPath = launch.LogPath;
            OnPropertyChanged(nameof(HasUpdateLog));
            UpdateOpenLogCommand.RaiseCanExecuteChanged();

            _log.Info($"Обновление {info.Version}: архив проверен, запускаю updater");
            UpdateStatusText = "устанавливаю… программа сейчас перезапустится";

            App.ShuttingDown = true;
            Task exitTask = App.ExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(45))).ConfigureAwait(true) != exitTask)
            {
                _log.Warn("Выход затянулся — завершаю принудительно, чтобы updater смог заменить файлы");
                Application.Current?.Shutdown();
            }
            else
            {
                await exitTask.ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_updateCts?.IsCancellationRequested == true)
        {
            UpdateStatusText = "загрузка обновления отменена";
            _log.Info("Загрузка обновления отменена пользователем");
        }
        catch (Exception ex)
        {
            UpdateStatusText = "ошибка обновления: " + ex.Message;
            _log.Warn("Обновление не установилось: " + ex.Message);
            MessageBox.Show(
                "Не удалось установить обновление:\n" + ex.Message + "\n\n" +
                "Если у вас версия 1.0.16 или старше — один раз установите 1.0.17 вручную " +
                "со страницы релиза, дальше обновления пойдут автоматически.\n\n" +
                "Журнал: " + (_lastUpdateLogPath.Length > 0 ? _lastUpdateLogPath : UpdateService.LogsDirectory),
                "Обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsUpdateDownloading = false;
            IsUpdateBusy = false;
            try { _updateCts?.Dispose(); } catch { }
            _updateCts = null;
        }
    }

    private static string FormatUpdateProgress(UpdateProgress progress)
    {
        static string Mb(long bytes) =>
            (bytes / 1024.0 / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        return progress.Total > 0
            ? $"{progress.Phase}… {progress.Fraction:P0} ({Mb(progress.Received)} из {Mb(progress.Total)} МБ)"
            : $"{progress.Phase}… {Mb(progress.Received)} МБ";
    }

    private void OpenUpdateLog()
    {
        string path = _lastUpdateLogPath;
        if (path.Length == 0) return;

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(UpdateService.LogsDirectory) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Не удалось открыть журнал обновления: " + ex.Message);
        }
    }

    /// <summary>Открыть страницу релиза в браузере.</summary>
    private void OpenReleasePage()
    {
        string url = _pendingUpdate?.PageUrl ?? "";
        if (url.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Не удалось открыть страницу релиза: " + ex.Message);
        }
    }
}

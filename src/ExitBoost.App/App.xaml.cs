using System.Diagnostics;
using System.IO;
using System.Windows;
using ExitBoost.Core;
using ExitBoost.Core.Configuration;
using ExitBoost.Core.Diagnostics;
using ExitBoost.Core.Dpi;
using ExitBoost.Core.Logging;
using ExitBoost.Core.Routing;
using ExitBoost.Core.Tunnel;
using ExitBoost.Core.Vpn;

namespace ExitBoost.App;

public partial class App : Application
{
    public static SettingsStore Settings { get; private set; } = null!;
    public static NodeRegistry Registry { get; private set; } = null!;
    public static LogBus Log { get; private set; } = null!;
    public static WindowsRouteManager Routes { get; private set; } = null!;
    public static TunnelManager Tunnel { get; private set; } = null!;
    public static BoostController Controller { get; private set; } = null!;
    public static ProbingService Probing { get; private set; } = null!;
    public static ZapretBundle Zapret { get; private set; } = null!;
    public static ZapretRunner ZapretRunner { get; private set; } = null!;
    public static DpiOptimizer DpiOptimizer { get; private set; } = null!;
    public static ZapretCatalog ZapretCatalog { get; set; } = null!;

    /// <summary>Ядро VPN по ключу (sing-box).</summary>
    public static SingBoxRunner Vpn { get; private set; } = null!;

    /// <summary>Сохранённые VPN-ключи и правила маршрутизации.</summary>
    public static VpnKeyStore VpnStore { get; private set; } = null!;

    /// <summary>Сбор UDP FLOW-событий для определения серверов выбранной игры.</summary>
    public static WinDivertFlowMonitor GameFlowMonitor { get; private set; } = null!;

    /// <summary>Верна в процесс выхода — сигналы о закрытии теперь реальные.</summary>
    public static bool ShuttingDown { get; internal set; }

    private static Mutex? _singleInstance;
    private static System.Windows.Forms.NotifyIcon? _tray;
    private static bool _trayHintShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- защита от двух копий программы ----
        // Имя события, через которое вторая копия просит первую показать окно.
        const string showWindowEventName = @"Local\ExitBoost.ShowWindow";

        _singleInstance = new Mutex(true, @"Local\ExitBoost.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            // Вторая копия не ругается и не открывает второе окно: тихо
            // активируем окно уже запущенного ExitBoost и выходим.
            _singleInstance.Dispose();
            _singleInstance = null;
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, showWindowEventName, out _);
                ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        GC.KeepAlive(_singleInstance);

        // Первая копия слушает сигнал «покажи окно» от вторых запусков.
        try
        {
            var showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showWindowEventName, out _);
            var listener = new Thread(() =>
            {
                while (showWindowEvent.WaitOne())
                {
                    try { Dispatcher.Invoke(ShowFromTray); }
                    catch { break; } // приложение закрывается
                }
            })
            { IsBackground = true };
            listener.Start();
        }
        catch (Exception ex)
        {
            Log?.Warn($"Не создалось событие показа окна: {ex.Message}");
        }

        Settings = new SettingsStore();
        Settings.Load();

        Log = new LogBus();
        if (Settings.Current.FileLogging)
            Log.EnableFileLogging(AppPaths.LogFile);

        Registry = new NodeRegistry();
        try
        {
            string nodesFile = Settings.Current.NodesFilePath;
            if (string.IsNullOrWhiteSpace(nodesFile)) nodesFile = AppPaths.DefaultNodesFile;
            if (File.Exists(nodesFile))
            {
                Registry.Load(nodesFile);
                Log.Info($"Загружено узлов: {Registry.Nodes.Count}");
            }
            else
            {
                Log.Info("Список узлов пуст — добавьте свой VPS, импортируйте .conf или подключите публичный список");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось загрузить список узлов: {ex.Message}");
        }

        Routes = new WindowsRouteManager();
        Routes.Log = m => Log.Warn(m);
        Tunnel = new TunnelManager(Routes, Settings);
        Controller = new BoostController(Settings, Log, Routes, Tunnel);

        Probing = new ProbingService
        {
            IntervalSeconds = Math.Max(1, Settings.Current.ProbeIntervalSeconds),
            ProbeCount = Math.Max(1, Settings.Current.ProbeCount),
            TimeoutMs = Math.Max(200, Settings.Current.ProbeTimeoutMs),
            Window = Math.Max(4, Settings.Current.ProbeWindow)
        };

        if (!WindowsRouteManager.IsElevated())
            Log.Warn("Приложение запущено без прав администратора — подключение будет недоступно");

        // ---------------- Модуль обхода DPI (zapret) ----------------
        Zapret = new ZapretBundle();
        Zapret.Log = m => Log.Info(m);
        ZapretRunner = new ZapretRunner(Zapret);
        ZapretRunner.Log = m => Log.Info(m);
        DpiOptimizer = new DpiOptimizer(ZapretRunner);
        GameFlowMonitor = new WinDivertFlowMonitor(Zapret.EngineDirectory)
        {
            Log = m => Log.Info(m)
        };
        if (Zapret.IsInstalled)
            _ = GameFlowMonitor.StartAsync();

        // ---------------- Модуль VPN по ключу (sing-box) ----------------
        Vpn = new SingBoxRunner();
        Vpn.Log = m => Log.Info(m);
        VpnStore = new VpnKeyStore();

        string catalogPath = Path.Combine(Zapret.InstallDirectory, "catalog.json");
        ZapretCatalog = ZapretCatalog.Load(catalogPath);

        if (Settings.Current.DpiCheckUpdates)
        {
            _ = Task.Run(async () =>
            {
                var update = await Zapret.CheckUpdateAsync();
                if (update is null) return;

                if (update.IsNewer && Settings.Current.DpiAutoUpdate)
                {
                    Log.Info($"Доступно обновление zapret ({update.ShortCommit}) — обновляю");
                    await GameFlowMonitor.StopAsync();
                    bool installed = await Zapret.InstallAsync();
                    if (Zapret.IsInstalled) await GameFlowMonitor.StartAsync();
                    if (installed)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ZapretCatalog = ZapretCatalog.BuildDefault();
                            ZapretCatalog.Save(catalogPath);
                        });
                    }
                }
                else
                {
                    Log.Info($"Движок zapret: {Zapret.InstalledVersion ?? "не установлен"}, свежая версия {update.ShortCommit}");
                }
            });
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        CreateTrayIcon();
        SessionEnding += (_, _) => QuickCleanupSync();

        await Controller.RepairAsync();

        Probing.Start(Registry.Nodes);
        Registry.Changed += () => Dispatcher.InvokeAsync(() => Probing.Start(Registry.Nodes));
    }

    // ------------------------------------------------------------- иконка в трее

    private void CreateTrayIcon()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (_, _) => ShowFromTray());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Перезапустить приложение", null, (_, _) => _ = RestartAsync());
            menu.Items.Add("Отключиться", null, (_, _) => _ = DisconnectFromTrayAsync());
            menu.Items.Add("Выход (отключаться и чистить сеть)", null, (_, _) => _ = ExitAsync());
            menu.Items.Add("Выход (без ожидания)", null, (_, _) => { ShuttingDown = true; Shutdown(); });

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "ExitBoost",
                Visible = true,
                ContextMenuStrip = menu
            };
            try
            {
                if (!string.IsNullOrEmpty(Environment.ProcessPath))
                    _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            }
            catch { }
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            Log?.Info($"Не удалось создать значок в трее: {ex.Message}");
        }
    }

    private void ShowFromTray()
    {
        var w = MainWindow;
        if (w is null) return;
        w.Show();
        w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>Подсказка при первом сворачивании в трей.</summary>
    public static void ShowTrayHint()
    {
        if (_tray is null || _trayHintShown) return;
        _trayHintShown = true;
        try
        {
            _tray.BalloonTipTitle = "ExitBoost свёрнут в трей";
            _tray.BalloonTipText = "Соединение и обход DPI продолжают работать. Двойной клик — открыть окно.";
            _tray.ShowBalloonTip(4000);
        }
        catch { }
    }

    private async Task DisconnectFromTrayAsync()
    {
        try
        {
            if (Controller.State == Core.Models.TunnelState.Connected)
                await Controller.DisconnectAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { Log?.Warn($"Отключение через трей: {ex.Message}"); }
    }

    /// <summary>Полный безопасный выход: отключение, стоп обхода DPI, очистка туннелей, закрытие.</summary>
    public static async Task ExitAsync()
    {
        try
        {
            Log?.Info("Выход: отключаю соединение и чищу сеть перед закрытием");
            if (GameFlowMonitor is not null) await GameFlowMonitor.StopAsync().ConfigureAwait(true);
            try { await ZapretRunner.ForceStopAllAsync().ConfigureAwait(true); } catch { }
            try { if (Vpn is not null) await Vpn.DisconnectAsync().ConfigureAwait(true); } catch { }

            if (Controller is not null)
            {
                if (Controller.State == Core.Models.TunnelState.Connected)
                {
                    await Controller.DisconnectAsync().ConfigureAwait(true);
                }
                await Controller.CleanupAllAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log?.Warn($"Во время выхода возникла ошибка (продолжаю закрытие): {ex.Message}");
        }

        ShuttingDown = true;
        Current?.Dispatcher.Invoke(() => Current.Shutdown());
    }

    /// <summary>
    /// Перезапуск приложения: чисто закрываем текущую копию (отключение,
    /// стоп обхода DPI, чистка сети) и в OnExit запускаем exe заново.
    /// </summary>
    public static async Task RestartAsync()
    {
        Log?.Info("Перезапуск приложения: закрываю текущую копию");
        _restartAfterExit = true;
        await ExitAsync();
    }

    private static bool _restartAfterExit;

    /// <summary>Быстрая синхронная очистка (завершение сеанса Windows).</summary>
    private void QuickCleanupSync()
    {
        try
        {
            GameFlowMonitor?.StopAsync().Wait(TimeSpan.FromSeconds(3));
            ZapretRunner?.ForceStopAllAsync().Wait(TimeSpan.FromSeconds(5));
            Controller?.CleanupAllAsync().Wait(TimeSpan.FromSeconds(8));
        }
        catch { }
    }

    // -------------------------------------------------------------- выход

    protected override async void OnExit(ExitEventArgs e)
    {
        // Трей — первым делом, чтобы значок не остался висеть.
        try
        {
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
        catch { }

        // Ранний выход при втором запуске: статические сервисы ещё не созданы.
        if (Settings is null || Controller is null)
        {
            try { GameFlowMonitor?.Dispose(); } catch { }
            base.OnExit(e);
            return;
        }

        if (!ShuttingDown)
        {
            // Дошли сюда без явного «Выхода» (например, аварийный путь) —
            // всё равно не оставляем туннели и правила в системе.
            try
            {
                if (Controller.State == Core.Models.TunnelState.Connected)
                    await Controller.DisconnectAsync();
            }
            catch { }
        }

        await Probing.DisposeAsync();
        await Tunnel.DisposeAsync();
        if (Vpn is not null)
        {
            try { await Vpn.DisconnectAsync(); } catch { }
        }
        try { if (GameFlowMonitor is not null) await GameFlowMonitor.StopAsync(); } catch { }
        // На любом пути выхода гасим обход DPI целиком: процессы zapret и драйвер.
        try { await ZapretRunner.ForceStopAllAsync().ConfigureAwait(true); } catch { }
        Log.DisableFileLogging();
        base.OnExit(e);

        _singleInstance?.Dispose();

        // Перезапуск: мьютекс уже освобождён — новая копия стартует без конфликта.
        if (_restartAfterExit)
        {
            _restartAfterExit = false;
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                    Log?.Info("Перезапуск: открыта новая копия ExitBoost");
                }
                catch (Exception ex)
                {
                    Log?.Warn($"Перезапуск не удался: {ex.Message}");
                }
            }
        }
    }
}

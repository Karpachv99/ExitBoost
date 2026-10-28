using ExitBoost.Core.Configuration;
using ExitBoost.Core.Dpi;
using ExitBoost.Core.Logging;
using ExitBoost.Core.Models;
using ExitBoost.Core.Networking;
using ExitBoost.Core.Routing;
using ExitBoost.Core.Tunnel;

namespace ExitBoost.Core;

/// <summary>Результат ремонта сети.</summary>
public sealed record RepairReport(
    IReadOnlyList<string> Steps,
    bool InternetAvailable,
    string InternetSummary);

/// <summary>
/// Ядро приложения: связывает выбор узла, туннель и маршрутизацию.
/// Реализует поведение, аналогичное ExitLag:
///   * трафик целевой игры заворачивается на ближайший к игре ретранслятор;
///   * остальной трафик остаётся прямым.
/// </summary>
public sealed class BoostController : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly LogBus _log;
    private readonly WindowsRouteManager _routes;
    private readonly TunnelManager _tunnel;
    private readonly NetworkStateStore _stateStore;
    private readonly WinDivertFlowMonitor _udpMonitor;

    private readonly GameEndpointTracker _endpointTracker = new();
    private Timer? _watchTimer;
    private readonly HashSet<string> _knownEndpoints = new(StringComparer.OrdinalIgnoreCase);

    private RoutingMode _mode = RoutingMode.ProcessSplit;
    private NodeConfig? _node;
    private TunnelOptions? _options;

    public BoostController(
        SettingsStore settings,
        LogBus log,
        WindowsRouteManager routes,
        TunnelManager tunnel,
        NetworkStateStore? stateStore = null)
    {
        _settings = settings;
        _log = log;
        _routes = routes;
        _tunnel = tunnel;
        _stateStore = stateStore ?? new NetworkStateStore();
        _udpMonitor = new WinDivertFlowMonitor(new ZapretBundle().EngineDirectory);
        _udpMonitor.Log = message => _log.Write(LogLevel.Info, message);
        _tunnel.StateChanged += _ => StatusChanged?.Invoke();
        _tunnel.Log += m => _log.Write(LogLevel.Info, m);
    }

    public TunnelState State => _tunnel.State;

    public NodeConfig? ActiveNode => _node;

    public RoutingMode Mode => _mode;

    public string StatusMessage => _tunnel.LastMessage;

    public string? InterfaceAlias => _tunnel.InterfaceAlias;

    public IReadOnlyList<string> ActiveRoutes => _options?.AllowedIps ?? (IReadOnlyList<string>)Array.Empty<string>();

    public IReadOnlyCollection<string> KnownEndpoints => _knownEndpoints;

    /// <summary>Сработало при изменении состояния (для обновления UI).</summary>
    public event Action? StatusChanged;

    /// <summary>Текст последнего предупреждения/ошибки.</summary>
    public string? LastWarning { get; private set; }

    // ------------------------------------------------------------ подготовка

    /// <summary>
    /// Полный ремонт сети после сбоя при подключении или аварийного завершения.
    /// Снимает наше подключение, зависшие туннели и адаптеры, восстанавливает
    /// маршруты, сбрасывает кэш DNS и проверяет фактический доступ в интернет.
    /// Чужие туннели и настройки не трогаются.
    /// </summary>
    public async Task<RepairReport> RepairAsync(CancellationToken ct = default)
    {
        var steps = new List<string>();

        // 1. Если подключение активно — выполняем штатное отключение.
        if (_node is not null)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            steps.Add("Снято активное подключение приложения");
        }

        // 2. Снимаем туннели, оставшиеся от аварийно завершённых запусков.
        var stale = (await _routes.GetStaleTunnelServicesAsync(ct).ConfigureAwait(false)).ToList();
        var saved = _stateStore.Load();
        if (saved?.TunnelNames is { Count: > 0 })
        {
            foreach (var t in saved.TunnelNames)
                if (!stale.Contains("WireGuardTunnel$" + t))
                    stale.Add("WireGuardTunnel$" + t);
        }

        foreach (var service in stale)
        {
            string tunnelName = service["WireGuardTunnel$".Length..];
            if (await WireGuardBackend.UninstallTunnelAsync(tunnelName, ct).ConfigureAwait(false))
                steps.Add($"Снят оставшийся туннель {tunnelName}");
        }

        // 3. Убираем адаптеры туннелей, не снятые службой.
        var adapters = await _routes.RemoveTunnelAdaptersAsync(ct).ConfigureAwait(false);
        foreach (var name in adapters)
            steps.Add($"Удалён адаптер {name}");

        // 4. Лишние правила брандмауэра приложения (наследие kill-switch).
        if ((await _routes.GetRulesAsync(ct).ConfigureAwait(false)).Count > 0)
        {
            await _routes.RemoveGroupAsync(ct).ConfigureAwait(false);
            steps.Add("Удалены оставшиеся правила брандмауэра");
        }

        // 5. Восстановление маршрута, если приложение его перекрыло.
        if (saved?.Route is not null)
        {
            if (await _routes.RestoreDefaultRouteAsync(saved.Route, ct).ConfigureAwait(false))
                steps.Add($"Маршрут по умолчанию восстановлен ({saved.Route.InterfaceAlias})");
        }

        // 6. Сбрасываем кэш DNS — частая причина «интернет не работает, хотя он есть».
        if (await _routes.FlushDnsAsync(ct).ConfigureAwait(false))
            steps.Add("Кэш DNS сброшен");

        _stateStore.Clear();

        // 7. Проверяем фактический доступ в интернет (без системного DNS).
        var check = await ConnectivityCheck.CheckAsync(ct).ConfigureAwait(false);
        steps.Add(check.Summary);

        var report = new RepairReport(steps, check.IsUp, check.Summary);
        if (check.IsUp)
            _log.Success($"Ремонт сети завершён: {check.Summary}");
        else
            _log.Warn($"Ремонт сети: {check.Summary}");
        StatusChanged?.Invoke();
        return report;
    }

    /// <summary>
    /// Полная очистка: правила брандмауэра приложения, маршруты и установленные туннели.
    /// Используется консольным клиентом и при восстановлении системы.
    /// </summary>
    public async Task CleanupAllAsync(CancellationToken ct = default)
    {
        StopWatcher();
        await _udpMonitor.StopAsync().ConfigureAwait(false);

        await _routes.RemoveGroupAsync(ct).ConfigureAwait(false);

        var stale = await _routes.GetStaleTunnelServicesAsync(ct).ConfigureAwait(false);
        foreach (var service in stale)
        {
            string tunnelName = service["WireGuardTunnel$".Length..];
            await WireGuardBackend.UninstallTunnelAsync(tunnelName, ct).ConfigureAwait(false);
        }

        var saved = _stateStore.Load();
        if (saved?.Route is not null)
        {
            await _routes.RestoreDefaultRouteAsync(saved.Route, ct).ConfigureAwait(false);
            _log.Info($"Маршрут по умолчанию восстановлен ({saved.Route.InterfaceAlias})");
        }

        if (saved?.TunnelNames is { Count: > 0 })
        {
            foreach (var tunnel in saved.TunnelNames)
                await WireGuardBackend.UninstallTunnelAsync(tunnel, ct).ConfigureAwait(false);
        }

        _stateStore.Clear();
        await _tunnel.DisconnectAsync().ConfigureAwait(false);
        _node = null;
        _knownEndpoints.Clear();
        StatusChanged?.Invoke();
    }

    /// <summary>PID процессов целевой игры.</summary>
    public List<int> ResolveTargetPids()
    {
        var s = _settings.Current;
        if (!string.IsNullOrWhiteSpace(s.TargetProcessPath))
        {
            var byPath = ProcessInspector.FindPids(s.TargetProcessPath);
            if (byPath.Count > 0) return byPath;
        }
        if (!string.IsNullOrWhiteSpace(s.TargetProcessName))
            return ProcessInspector.FindPids(s.TargetProcessName);
        return new List<int>();
    }

    /// <summary>Путь к исполняемому файлу целевой игры (для правил брандмауэра).</summary>
    public string? TargetProgramPath()
    {
        var s = _settings.Current;
        if (!string.IsNullOrWhiteSpace(s.TargetProcessPath)) return s.TargetProcessPath;
        var pids = ResolveTargetPids();
        foreach (var pid in pids)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                var path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Определение адресов игровых серверов по активным соединениям.</summary>
    public HashSet<string> DetectTargetEndpoints()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in ResolveTargetPids())
        {
            foreach (var ip in ProcessInspector.GetRemoteAddresses(pid))
                if (GameEndpointTracker.TryNormalizeIpv4(ip, out var normalized)) result.Add(normalized);

            // Windows' UDP table contains local ports only. WinDivert FLOW events
            // provide the missing remote address and owning PID for UDP game traffic.
            foreach (var flow in _udpMonitor.GetUdpFlows(pid))
                if (GameEndpointTracker.TryNormalizeIpv4(flow.RemoteAddress, out var normalizedUdp)) result.Add(normalizedUdp);
        }
        return result;
    }

    /// <summary>Расчёт списка маршрутов для выбранного режима.</summary>
    public (List<string> AllowedIps, string? Warning) BuildAllowedIps(RoutingMode mode)
    {
        var s = _settings.Current;
        var list = new List<string>();
        string? warning = null;

        switch (mode)
        {
            case RoutingMode.FullTunnel:
                list.Add("0.0.0.0/0");
                break;

            case RoutingMode.NetworkSplit:
                foreach (var cidr in s.SplitSubnets)
                    if (!string.IsNullOrWhiteSpace(cidr)) list.Add(cidr.Trim());
                if (list.Count == 0)
                {
                    warning = "Не заданы подсети для NetworkSplit. Маршруты через VPN не добавлены.";
                }
                break;

            case RoutingMode.ProcessSplit:
                var manual = s.SplitSubnets.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
                var detected = s.AutoDetectEndpoints ? DetectTargetEndpoints() : new HashSet<string>();
                foreach (var ip in detected) list.Add(ip + "/32");
                list.AddRange(manual);
                if (list.Count == 0)
                {
                    // Fail closed: missing process endpoints must not enable a full-device tunnel.
                    warning = "Игровые адреса пока не обнаружены. Полный туннель не включён; запустите игру и дождитесь подключения к серверу.";
                }
                break;
        }

        // Убираем дубликаты, сохраняя порядок.
        var unique = new List<string>();
        foreach (var ip in list)
            if (!unique.Contains(ip, StringComparer.OrdinalIgnoreCase)) unique.Add(ip);

        return (unique, warning);
    }

    // ------------------------------------------------------------- подключение

    public async Task<bool> ConnectAsync(NodeConfig node, RoutingMode mode, CancellationToken ct = default)
    {
        LastWarning = null;

        if (!WindowsRouteManager.IsElevated())
        {
            _log.Error("Требуются права администратора. Запустите приложение от имени администратора.");
            StatusChanged?.Invoke();
            return false;
        }

        _node = node;
        _mode = mode;

        _endpointTracker.Clear();
        _knownEndpoints.Clear();
        if (mode == RoutingMode.ProcessSplit && _settings.Current.AutoDetectEndpoints)
        {
            // Start before the first endpoint snapshot so short-lived UDP flows are not missed.
            bool listening = await _udpMonitor.StartAsync(ct).ConfigureAwait(false);
            if (!listening)
                _log.Warn("Автоопределение UDP-серверов недоступно; продолжаю с обнаружением TCP. Установите/обновите zapret и запустите приложение от администратора.");
        }
        else
        {
            await _udpMonitor.StopAsync().ConfigureAwait(false);
        }

        var (allowedIps, warning) = BuildAllowedIps(mode);
        if (warning is not null)
        {
            LastWarning = warning;
            _log.Warn(warning);
        }

        _log.Info($"Подключение к узлу {node.Name} ({node.Host}:{node.Port}), режим: {mode}, маршруты: {string.Join(", ", allowedIps)}");
        var options = new TunnelOptions
        {
            Mode = mode,
            AllowedIps = allowedIps,
            Mtu = _settings.Current.Mtu,
            Dns = node.Dns,
            DnsThroughTunnel = _settings.Current.DnsThroughTunnel,
            ManageDns = _settings.Current.DnsThroughTunnel
        };
        _options = options;

        var result = await _tunnel.ConnectAsync(node, options, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            StopWatcher();
            await _udpMonitor.StopAsync().ConfigureAwait(false);
            _log.Error($"Не удалось подключиться: {result.Message}");
            StatusChanged?.Invoke();
            return false;
        }

        // Запоминаем туннель, чтобы его можно было снять после аварийного завершения.
        _stateStore.RememberTunnel(WireGuardBackend.TunnelNameFor(node.Id));

        foreach (var ip in _endpointTracker.Observe(DetectTargetEndpoints(), DateTime.UtcNow))
            _knownEndpoints.Add(ip);

        StartWatcher();

        _log.Success($"Подключено: {node.Name}. Интерфейс: {result.InterfaceAlias ?? "—"}");
        StatusChanged?.Invoke();
        return true;
    }

    // ------------------------------------------------------------ отключение

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        StopWatcher();
        await _udpMonitor.StopAsync().ConfigureAwait(false);

        _stateStore.Clear();

        await _tunnel.DisconnectAsync().ConfigureAwait(false);
        _node = null;
        _knownEndpoints.Clear();
        _endpointTracker.Clear();
        _log.Info("Соединение закрыто");
        StatusChanged?.Invoke();
    }

    // ------------------------------------------------------- слежение за игрой

    private void StartWatcher()
    {
        StopWatcher();
        int period = Math.Max(2, _settings.Current.EndpointRefreshSeconds);
        _watchTimer = new Timer(async _ => await WatchAsync().ConfigureAwait(false), null, TimeSpan.FromSeconds(period), TimeSpan.FromSeconds(period));
    }

    private void StopWatcher()
    {
        _watchTimer?.Dispose();
        _watchTimer = null;
    }

    private int _watchBusy;

    private async Task WatchAsync()
    {
        if (Interlocked.Exchange(ref _watchBusy, 1) == 1) return;
        try
        {
            if (_mode != RoutingMode.ProcessSplit || !_settings.Current.AutoDetectEndpoints) return;
            if (_node is null || _options is null) return;

            var current = _endpointTracker.Observe(DetectTargetEndpoints(), DateTime.UtcNow)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool changed = !_knownEndpoints.SetEquals(current);
            if (!changed) return;

            _knownEndpoints.Clear();
            foreach (var ep in current) _knownEndpoints.Add(ep);

            var manual = _settings.Current.SplitSubnets.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
            var newIps = _knownEndpoints.Select(e => e + "/32").Concat(manual)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            _options.AllowedIps = newIps;
            _log.Info($"Обновлён список игровых маршрутов: {string.Join(", ", newIps)}");

            if (_tunnel is not null && _node is not null)
            {
                var backend = _tunnel.Backend as WireGuardBackend;
                if (backend is not null)
                {
                    bool ok = await backend.UpdateAllowedIpsAsync(_node, _options).ConfigureAwait(false);
                    if (!ok)
                    {
                        _log.Warn("Не удалось обновить маршруты на лету — потребуется переподключение");
                    }
                }
            }
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ошибка наблюдения за соединениями: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _watchBusy, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopWatcher();
        await DisconnectAsync().ConfigureAwait(false);
    }
}

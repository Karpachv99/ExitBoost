using ExitBoost.Core.Models;
using ExitBoost.Core.Routing;

namespace ExitBoost.Core.Tunnel;

/// <summary>
/// Бэкенд на базе WireGuard for Windows.
/// Устанавливает туннель как службу и заворачивает в него нужные маршруты.
/// </summary>
public sealed class WireGuardBackend : ITunnelBackend
{
    private readonly WindowsRouteManager _routes;
    private readonly string? _exeOverride;
    private string? _tunnelName;
    private string? _configPath;
    private bool _installed;

    public WireGuardBackend(WindowsRouteManager routes, string? exeOverride = null)
    {
        _routes = routes;
        _exeOverride = exeOverride;
    }

    public string Name => "WireGuard";

    public bool IsAvailable => FindExecutable() is not null;

    public string? ErrorMessage => IsAvailable ? null : "Не найден wireguard.exe. Установите WireGuard for Windows.";

    public TunnelState State { get; private set; } = TunnelState.Disconnected;

    public string? InterfaceAlias { get; private set; }

    public IReadOnlyList<string> InterfaceAddresses => _addresses;

    private readonly List<string> _addresses = new();

    public event Action<string>? Log;

    /// <summary>Путь к wireguard.exe.</summary>
    public string? FindExecutable() => LocateExecutable(_exeOverride);

    /// <summary>Поиск wireguard.exe в стандартных местах установки.</summary>
    public static string? LocateExecutable(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        string[] candidates =
        {
            @"C:\Program Files\WireGuard\wireguard.exe",
            @"C:\Program Files (x86)\WireGuard\wireguard.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WireGuard", "wireguard.exe"),
            @"C:\Program Files\WireGuard\wg.exe"
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return null;
    }

    /// <summary>Имя службы туннеля для узла.</summary>
    public static string ServiceNameFor(string nodeId) => $"WireGuardTunnel$ExitBoost_{nodeId}";

    /// <summary>Имя туннеля (и адаптера) для узла.</summary>
    public static string TunnelNameFor(string nodeId) => $"ExitBoost_{nodeId}";

    /// <summary>
    /// Снятие установленного туннеля по имени.
    /// Используется при восстановлении системы после аварийного завершения.
    /// </summary>
    public static async Task<bool> UninstallTunnelAsync(string tunnelName, CancellationToken ct = default)
    {
        var exe = LocateExecutable();
        if (exe is null) return false;

        var res = await ProcessRunner.RunAsync(
            exe, $"/uninstalltunnelservice \"{tunnelName}\"", 30_000, ct).ConfigureAwait(false);

        if (!res.Ok)
        {
            string serviceName = $"WireGuardTunnel${tunnelName}";
            await ProcessRunner.RunAsync("sc.exe", $"stop \"{serviceName}\"", 15_000, ct).ConfigureAwait(false);
            await ProcessRunner.RunAsync("sc.exe", $"delete \"{serviceName}\"", 15_000, ct).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<TunnelResult> ConnectAsync(NodeConfig node, TunnelOptions options, CancellationToken ct = default)
    {
        var exe = FindExecutable();
        if (exe is null)
            return TunnelResult.Fail("WireGuard не установлен (не найден wireguard.exe)");

        if (string.IsNullOrWhiteSpace(node.PublicKey))
            return TunnelResult.Fail("У узла не задан публичный ключ пира");
        if (string.IsNullOrWhiteSpace(node.PrivateKey))
            return TunnelResult.Fail("У узла не задан приватный ключ");
        if (string.IsNullOrWhiteSpace(node.Address))
            return TunnelResult.Fail("У узла не задан локальный адрес (Address)");

        State = TunnelState.Connecting;
        _tunnelName = $"ExitBoost_{node.Id}";
        _configPath = Path.Combine(AppPaths.CacheDirectory, _tunnelName + ".conf");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);

            // Сначала снимаем возможный предыдущий туннель с тем же именем.
            await UninstallAsync(ct, deleteConfig: false).ConfigureAwait(false);
            await WaitForServiceRemovedAsync($"WireGuardTunnel${_tunnelName}", ct).ConfigureAwait(false);

            string conf = WireGuardConfigBuilder.Build(node, options);
            await File.WriteAllTextAsync(_configPath, conf, ct).ConfigureAwait(false);
            Log?.Invoke($"Установка туннеля {node.Name} (маршруты: {options.AllowedIpsString})");

            var install = await InstallAsync(ct).ConfigureAwait(false);

            if (!install.Ok)
            {
                State = TunnelState.Error;
                string err = install.Combined.Trim();
                Log?.Invoke($"Ошибка установки туннеля: {err}");
                return TunnelResult.Fail(string.IsNullOrEmpty(err) ? "Не удалось запустить wireguard.exe" : err);
            }

            _installed = true;

            var adapter = await WaitForAdapterAsync(ct).ConfigureAwait(false);
            if (adapter is null)
            {
                State = TunnelState.Error;
                string serviceState = await GetServiceStateAsync().ConfigureAwait(false);
                string detail = string.IsNullOrEmpty(serviceState)
                    ? "Интерфейс туннеля не появился за отведённое время"
                    : $"Интерфейс туннеля не появился. Служба: {serviceState}";
                Log?.Invoke(detail);
                await UninstallAsync(ct).ConfigureAwait(false);
                return TunnelResult.Fail(detail);
            }

            InterfaceAlias = adapter.Name;
            _addresses.Clear();
            _addresses.AddRange(await _routes.GetInterfaceAddressesAsync(adapter.Name, ct).ConfigureAwait(false));

            // Маршрут до узла должен идти напрямую через физический интерфейс,
            // иначе туннель не сможет подняться (зацикливание маршрутов).
            var defaultRoute = await _routes.GetDefaultRouteAsync(ct).ConfigureAwait(false);
            if (defaultRoute is not null && !string.IsNullOrWhiteSpace(node.Host))
            {
                string endpointIp = node.Host;
                if (System.Net.IPAddress.TryParse(endpointIp, out _))
                {
                    await _routes.EnsureHostRouteAsync(endpointIp, defaultRoute.InterfaceAlias, ct).ConfigureAwait(false);
                }
            }

            State = TunnelState.Connected;
            Log?.Invoke($"Туннель поднят: {adapter.Name} (адреса: {string.Join(", ", _addresses)})");
            return TunnelResult.Ok("Туннель подключён", adapter.Name, options.AllowedIps);
        }
        catch (Exception ex)
        {
            State = TunnelState.Error;
            Log?.Invoke($"Ошибка подключения: {ex.Message}");
            return TunnelResult.Fail(ex.Message);
        }
    }

    public async Task DisconnectAsync()
    {
        if (State == TunnelState.Disconnected && !_installed) return;
        State = TunnelState.Disconnecting;
        await UninstallAsync(CancellationToken.None, deleteConfig: true).ConfigureAwait(false);
        InterfaceAlias = null;
        _addresses.Clear();
        State = TunnelState.Disconnected;
    }

    /// <summary>
    /// Живое обновление списка AllowedIPs без переустановки туннеля.
    /// Нужно, когда игра подключается к новому серверу уже во время игры.
    /// </summary>
    public async Task<bool> UpdateAllowedIpsAsync(NodeConfig node, TunnelOptions options, CancellationToken ct = default)
    {
        if (_tunnelName is null || !_installed) return false;

        var wg = FindWgExecutable();
        if (wg is null)
        {
            Log?.Invoke("wg.exe не найден — обновление маршрутов невозможно");
            return false;
        }

        string syncPath = Path.Combine(AppPaths.CacheDirectory, _tunnelName + ".sync.conf");
        try
        {
            await File.WriteAllTextAsync(syncPath, WireGuardConfigBuilder.BuildSyncConf(node, options), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось записать sync-конфиг: {ex.Message}");
            return false;
        }

        var res = await ProcessRunner.RunAsync(
            wg, $"syncconf \"{_tunnelName}\" \"{syncPath}\"", 15_000, ct).ConfigureAwait(false);

        if (!res.Ok)
        {
            Log?.Invoke($"wg syncconf не сработал ({res.Combined.Trim()}), пробуем setconf");
            res = await ProcessRunner.RunAsync(
                wg, $"setconf \"{_tunnelName}\" \"{syncPath}\"", 15_000, ct).ConfigureAwait(false);
        }

        if (res.Ok)
        {
            Log?.Invoke($"Маршруты туннеля обновлены: {options.AllowedIpsString}");
            return true;
        }

        Log?.Invoke($"Не удалось обновить маршруты: {res.Combined.Trim()}");
        return false;
    }

    /// <summary>Путь к wg.exe (лежит рядом с wireguard.exe).</summary>
    public string? FindWgExecutable()
    {
        var wg = Path.Combine(Path.GetDirectoryName(FindExecutable() ?? string.Empty) ?? string.Empty, "wg.exe");
        return File.Exists(wg) ? wg : null;
    }

    private async Task UninstallAsync(CancellationToken ct, bool deleteConfig = true)
    {
        var exe = FindExecutable();
        if (exe is null || _tunnelName is null) return;

        var res = await ProcessRunner.RunAsync(
            exe, $"/uninstalltunnelservice \"{_tunnelName}\"", 30_000, ct).ConfigureAwait(false);

        if (!res.Ok)
        {
            // Запасной путь: остановить и удалить службу вручную.
            string serviceName = $"WireGuardTunnel${_tunnelName}";
            await ProcessRunner.RunAsync("sc.exe", $"stop \"{serviceName}\"", 15_000, ct).ConfigureAwait(false);
            await ProcessRunner.RunAsync("sc.exe", $"delete \"{serviceName}\"", 15_000, ct).ConfigureAwait(false);
        }

        _installed = false;
        if (deleteConfig)
        {
            try { if (_configPath is not null && File.Exists(_configPath)) File.Delete(_configPath); } catch { }
        }
    }

    /// <summary>Установка службы туннеля с повтором при занятой службе.</summary>
    private async Task<ProcessRunResult> InstallAsync(CancellationToken ct)
    {
        var exe = FindExecutable();
        if (exe is null) return new ProcessRunResult(-1, "", "wireguard.exe не найден");

        var result = await ProcessRunner.RunAsync(
            exe, $"/installtunnelservice \"{_configPath}\"", 45_000, ct).ConfigureAwait(false);

        for (int attempt = 0; attempt < 4 && !result.Ok; attempt++)
        {
            if (!result.Combined.Contains("already installed", StringComparison.OrdinalIgnoreCase))
                break;

            // Служба удаляется асинхронно — дожидаемся её исчезновения и повторяем.
            await WaitForServiceRemovedAsync($"WireGuardTunnel${_tunnelName}", ct).ConfigureAwait(false);
            await Task.Delay(500, ct).ConfigureAwait(false);
            result = await ProcessRunner.RunAsync(
                exe, $"/installtunnelservice \"{_configPath}\"", 45_000, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Ожидание исчезновения службы (1060 — служба не найдена).</summary>
    private async Task WaitForServiceRemovedAsync(string serviceName, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15_000)
        {
            ct.ThrowIfCancellationRequested();
            var res = await ProcessRunner.RunAsync("sc.exe", $"query \"{serviceName}\"", 10_000, ct).ConfigureAwait(false);
            if (res.ExitCode == 1060 || res.Combined.Contains("1060", StringComparison.Ordinal))
                return;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Состояние службы туннеля (для диагностики).</summary>
    private async Task<string> GetServiceStateAsync()
    {
        if (_tunnelName is null) return "";
        string serviceName = $"WireGuardTunnel${_tunnelName}";
        var res = await ProcessRunner.RunAsync("sc.exe", $"query \"{serviceName}\"", 15_000).ConfigureAwait(false);
        var text = res.Combined.Trim();
        if (text.Length == 0) return "не найдена";
        int idx = text.IndexOf("STATE", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? text[idx..].Replace("\r", " ").Replace("\n", " ").Trim() : text;
    }

    private async Task<NetAdapterInfo?> WaitForAdapterAsync(CancellationToken ct)
    {
        NetAdapterInfo? fallback = null;

        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            var adapters = await _routes.GetAdaptersAsync(ct).ConfigureAwait(false);

            // Точное совпадение по имени туннеля — так WireGuard for Windows называет адаптер.
            var exact = adapters.FirstOrDefault(a =>
                a.Name.Equals(_tunnelName, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(_tunnelName!, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            fallback ??= adapters.FirstOrDefault(a =>
                a.Description.Contains("WireGuard Tunnel", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("ExitBoost", StringComparison.OrdinalIgnoreCase));

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return fallback;
    }

    public void Dispose()
    {
        try { UninstallAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
    }
}

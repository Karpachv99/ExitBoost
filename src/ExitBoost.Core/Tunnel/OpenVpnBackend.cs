using System.Diagnostics;
using ExitBoost.Core.Models;
using ExitBoost.Core.Routing;

namespace ExitBoost.Core.Tunnel;

/// <summary>Бэкенд на базе OpenVPN.</summary>
public sealed class OpenVpnBackend : ITunnelBackend
{
    private readonly WindowsRouteManager _routes;
    private readonly string? _exeOverride;
    private Process? _process;

    public OpenVpnBackend(WindowsRouteManager routes, string? exeOverride = null)
    {
        _routes = routes;
        _exeOverride = exeOverride;
    }

    public string Name => "OpenVPN";

    public bool IsAvailable => FindExecutable() is not null;

    public string? ErrorMessage => IsAvailable ? null : "Не найден openvpn.exe. Установите OpenVPN Client.";

    public TunnelState State { get; private set; } = TunnelState.Disconnected;

    public string? InterfaceAlias { get; private set; }

    public IReadOnlyList<string> InterfaceAddresses => _addresses;

    private readonly List<string> _addresses = new();

    public event Action<string>? Log;

    public string? FindExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_exeOverride) && File.Exists(_exeOverride)) return _exeOverride;
        string[] candidates =
        {
            @"C:\Program Files\OpenVPN\bin\openvpn.exe",
            @"C:\Program Files\OpenVPN\config\openvpn.exe",
            @"C:\Program Files (x86)\OpenVPN\bin\openvpn.exe"
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    public async Task<TunnelResult> ConnectAsync(NodeConfig node, TunnelOptions options, CancellationToken ct = default)
    {
        var exe = FindExecutable();
        if (exe is null) return TunnelResult.Fail("OpenVPN не установлен (не найден openvpn.exe)");

        string configPath = node.ConfigPath ?? "";
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return TunnelResult.Fail("Для узла OpenVPN не найден файл конфигурации (.ovpn)");

        State = TunnelState.Connecting;
        try
        {
            var args = new List<string> { $"--config \"{configPath}\"", "--verb", "3" };

            if (!string.IsNullOrWhiteSpace(node.Username))
            {
                string authFile = Path.Combine(AppPaths.CacheDirectory, $"ovpn_auth_{node.Id}.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(authFile)!);
                await File.WriteAllTextAsync(authFile, $"{node.Username}\n{node.Password}\n", ct).ConfigureAwait(false);
                args.Add($"--auth-user-pass \"{authFile}\"");
            }

            bool initialized = false;
            string log = "";
            _process = ProcessRunner.StartStreaming(exe, string.Join(' ', args), line =>
            {
                log = (log.Length > 8000 ? log[^8000..] : log) + line + "\n";
                if (line.Contains("Initialization Sequence Completed", StringComparison.OrdinalIgnoreCase))
                    initialized = true;
                if (line.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase))
                    initialized = false;
            });

            for (int i = 0; i < 120 && !initialized; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_process.HasExited) break;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            if (!initialized)
            {
                State = TunnelState.Error;
                await DisconnectAsync().ConfigureAwait(false);
                string tail = log.Split('\n').TakeLast(8).Aggregate("", (a, b) => a + b + "\n");
                Log?.Invoke($"OpenVPN не инициализирован: {tail}");
                return TunnelResult.Fail("OpenVPN не удалось поднять туннель. " + tail.Trim());
            }

            var adapter = await _routes.FindTunnelAdapterAsync(new[] { "OVPN", "TAP", "OpenVPN" }, ct).ConfigureAwait(false);
            InterfaceAlias = adapter?.Name;
            if (InterfaceAlias is not null)
                _addresses.Clear();
            if (InterfaceAlias is not null)
                _addresses.AddRange(await _routes.GetInterfaceAddressesAsync(InterfaceAlias, ct).ConfigureAwait(false));

            State = TunnelState.Connected;
            Log?.Invoke($"OpenVPN туннель поднят: {InterfaceAlias ?? "интерфейс не определён"}");
            return TunnelResult.Ok("OpenVPN туннель подключён", InterfaceAlias, options.AllowedIps);
        }
        catch (Exception ex)
        {
            State = TunnelState.Error;
            Log?.Invoke($"Ошибка OpenVPN: {ex.Message}");
            return TunnelResult.Fail(ex.Message);
        }
    }

    public Task DisconnectAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(true);
                _process.WaitForExit(5000);
            }
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
        InterfaceAlias = null;
        _addresses.Clear();
        State = TunnelState.Disconnected;
        return Task.CompletedTask;
    }

    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();
}

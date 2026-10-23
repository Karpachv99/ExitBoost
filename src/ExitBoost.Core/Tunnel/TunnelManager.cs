using ExitBoost.Core.Configuration;
using ExitBoost.Core.Models;
using ExitBoost.Core.Routing;

namespace ExitBoost.Core.Tunnel;

/// <summary>
/// Управляет активным туннелем: выбор бэкенда по типу узла, подключение,
/// отключение и восстановление сетевого состояния.
/// </summary>
public sealed class TunnelManager : IAsyncDisposable
{
    private readonly WindowsRouteManager _routes;
    private readonly SettingsStore _settings;
    private ITunnelBackend? _backend;

    public TunnelState State { get; private set; } = TunnelState.Disconnected;

    public NodeConfig? CurrentNode { get; private set; }

    public string? InterfaceAlias => _backend?.InterfaceAlias;

    public IReadOnlyList<string> InterfaceAddresses => _backend?.InterfaceAddresses ?? Array.Empty<string>();

    /// <summary>Последнее сообщение (ошибка/статус).</summary>
    public string LastMessage { get; private set; } = "";

    public event Action<TunnelState>? StateChanged;

    public event Action<string>? Log;

    public TunnelManager(WindowsRouteManager routes, SettingsStore settings)
    {
        _routes = routes;
        _settings = settings;
    }

    public bool IsConnected => State == TunnelState.Connected && _backend is not null;

    /// <summary>Активный бэкенд туннеля.</summary>
    public ITunnelBackend? Backend => _backend;

    private ITunnelBackend CreateBackend(NodeKind kind)
    {
        var s = _settings.Current;
        return kind switch
        {
            NodeKind.OpenVpn => new OpenVpnBackend(_routes, s.OpenVpnExePath),
            NodeKind.SshProxy => new SshProxyBackend(s.SshExePath),
            _ => new WireGuardBackend(_routes, s.WireGuardExePath)
        };
    }

    public async Task<TunnelResult> ConnectAsync(NodeConfig node, TunnelOptions options, CancellationToken ct = default)
    {
        if (State == TunnelState.Connected) await DisconnectAsync().ConfigureAwait(false);

        SetState(TunnelState.Connecting);
        CurrentNode = node;

        var backend = CreateBackend(node.Kind);
        backend.Log += m => Log?.Invoke(m);

        if (!backend.IsAvailable)
        {
            SetState(TunnelState.Error);
            LastMessage = backend.ErrorMessage ?? "Транспорт недоступен";
            Log?.Invoke(LastMessage);
            backend.Dispose();
            return TunnelResult.Fail(LastMessage);
        }

        _backend = backend;
        var result = await backend.ConnectAsync(node, options, ct).ConfigureAwait(false);

        if (result.Success)
        {
            LastMessage = result.Message;
            SetState(TunnelState.Connected);
        }
        else
        {
            LastMessage = result.Message;
            SetState(TunnelState.Error);
            await SafeDisconnectBackendAsync().ConfigureAwait(false);
        }
        return result;
    }

    public async Task DisconnectAsync()
    {
        if (State == TunnelState.Disconnected) return;
        SetState(TunnelState.Disconnecting);
        await SafeDisconnectBackendAsync().ConfigureAwait(false);
        CurrentNode = null;
        SetState(TunnelState.Disconnected);
        LastMessage = "Отключено";
    }

    private async Task SafeDisconnectBackendAsync()
    {
        if (_backend is null) return;
        try { await _backend.DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log?.Invoke($"Ошибка отключения: {ex.Message}"); }
        finally
        {
            _backend.Dispose();
            _backend = null;
        }
    }

    /// <summary>Проверка живости туннеля (интерфейс и процесс).</summary>
    public bool IsAlive()
    {
        if (_backend is null || State != TunnelState.Connected) return false;
        if (_backend is WireGuardBackend wg)
        {
            var alias = wg.InterfaceAlias;
            if (string.IsNullOrEmpty(alias)) return false;
            return true;
        }
        return true;
    }

    private void SetState(TunnelState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}

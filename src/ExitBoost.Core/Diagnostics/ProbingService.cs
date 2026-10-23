using System.Collections.Concurrent;
using ExitBoost.Core.Models;

namespace ExitBoost.Core.Diagnostics;

/// <summary>
/// Фоновый измеритель качества каналов до всех узлов.
/// Каждый узел опрашивается в своём потоке, результаты публикуются через событие.
/// </summary>
public sealed class ProbingService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PingProber> _probers = new();
    private readonly ConcurrentDictionary<string, NodeStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _loops = new();
    private CancellationTokenSource? _globalCts;
    private int _disposed;

    /// <summary>Параметры измерений.</summary>
    public int IntervalSeconds { get; set; } = 5;

    public int ProbeCount { get; set; } = 4;

    public int TimeoutMs { get; set; } = 1500;

    public int Window { get; set; } = 24;

    /// <summary>Обновлённые показатели узла.</summary>
    public event Action<NodeStatus>? StatusUpdated;

    /// <summary>Лог-сообщение.</summary>
    public event Action<string>? Log;

    public IReadOnlyCollection<NodeStatus> Statuses => _statuses.Values.ToList();

    public NodeStatus? GetStatus(string nodeId) =>
        _statuses.TryGetValue(nodeId, out var s) ? s : null;

    /// <summary>
    /// Возвращает объект состояния узла, создавая его при необходимости.
    /// Нужен, чтобы интерфейс работал с тем же объектом, что и измеритель.
    /// </summary>
    public NodeStatus GetOrCreateStatus(NodeConfig config)
    {
        var status = _statuses.GetOrAdd(config.Id, _ => new NodeStatus { Config = config });
        if (!ReferenceEquals(status.Config, config)) status.Config = config;
        return status;
    }

    /// <summary>Запуск фоновых измерений для списка узлов.</summary>
    public void Start(IEnumerable<NodeConfig> nodes)
    {
        if (_disposed != 0) return;
        _globalCts ??= new CancellationTokenSource();

        foreach (var node in nodes.Where(n => n.Enabled))
        {
            GetOrCreateStatus(node);
            if (_loops.ContainsKey(node.Id)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token);
            _loops[node.Id] = cts;
            _ = Task.Run(() => LoopAsync(node, cts.Token), CancellationToken.None);
        }
    }

    /// <summary>Остановка всех измерений.</summary>
    public void Stop()
    {
        _globalCts?.Cancel();
        _globalCts?.Dispose();
        _globalCts = null;

        foreach (var kv in _loops)
        {
            kv.Value.Cancel();
            kv.Value.Dispose();
        }
        _loops.Clear();
    }

    private async Task LoopAsync(NodeConfig node, CancellationToken ct)
    {
        var prober = _probers.GetOrAdd(node.Id, _ => new PingProber(Window));
        var status = GetOrCreateStatus(node);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeRoundAsync(node, prober, status, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"[probe] {node.Name}: ошибка измерения — {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, IntervalSeconds)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Один раунд измерений по узлу.</summary>
    public async Task<NodeStatus> ProbeRoundAsync(NodeConfig node, CancellationToken ct = default)
    {
        var prober = _probers.GetOrAdd(node.Id, _ => new PingProber(Window));
        var status = GetOrCreateStatus(node);
        await ProbeRoundAsync(node, prober, status, ct).ConfigureAwait(false);
        return status;
    }

    private async Task ProbeRoundAsync(NodeConfig node, PingProber prober, NodeStatus status, CancellationToken ct)
    {
        status.State = NodeState.Probing;
        status.LastProbeUtc = DateTime.UtcNow;

        for (int i = 0; i < Math.Max(1, ProbeCount); i++)
        {
            if (ct.IsCancellationRequested) return;

            var ping = await prober.ProbeAsync(node.Host, TimeoutMs, ct).ConfigureAwait(false);
            if (ping is null)
            {
                // Фолбэк на TCP: ICMP может быть заблокирован, а канал при этом рабочий.
                var tcp = await PingProber.TcpProbeAsync(node.Host, node.Port, TimeoutMs, ct).ConfigureAwait(false);
                if (tcp is null)
                {
                    prober.Record(false, 0);
                }
                else
                {
                    prober.Record(true, tcp.Value);
                    status.TcpPingMs = tcp.Value;
                }
            }
            else
            {
                prober.Record(true, ping.Value);
                status.LastPingMs = ping.Value;
            }

            if (i + 1 < ProbeCount)
            {
                try { await Task.Delay(120, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        var m = prober.Compute();
        status.LastPingMs = m.LastMs > 0 ? m.LastMs : status.TcpPingMs;
        status.AvgPingMs = m.AvgMs > 0 ? m.AvgMs : status.TcpPingMs;
        status.MinPingMs = m.MinMs > 0 ? m.MinMs : status.TcpPingMs;
        status.MaxPingMs = m.MaxMs;
        status.JitterMs = m.JitterMs;
        status.LossPercent = m.LossPercent;
        status.SampleCount = m.Samples;
        status.FailureCount = m.Failures;
        status.Score = NodeSelector.ComputeScore(status);

        status.State = status.LossPercent >= 80 ? NodeState.Offline
            : status.LossPercent >= 15 ? NodeState.Degraded
            : NodeState.Online;

        status.StatusText = status.LossPercent >= 80
            ? "недоступен"
            : $"{status.AvgPingMs:F0} мс · {status.LossPercent:F0}% потерь";

        StatusUpdated?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        foreach (var p in _probers.Values) p.Clear();
        _probers.Clear();
        _statuses.Clear();
        await Task.CompletedTask;
    }
}

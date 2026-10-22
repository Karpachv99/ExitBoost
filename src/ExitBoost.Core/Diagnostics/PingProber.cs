using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ExitBoost.Core.Diagnostics;

/// <summary>Один замер качества канала.</summary>
public readonly record struct PingSample(DateTime AtUtc, bool Ok, double Milliseconds);

/// <summary>Агрегаты по окну измерений.</summary>
public readonly record struct NodeMetrics(
    double LastMs,
    double AvgMs,
    double MinMs,
    double MaxMs,
    double JitterMs,
    double LossPercent,
    int Samples,
    int Failures);

/// <summary>
/// Измеряет задержку и потери до узла: ICMP ping + резервный TCP-handshake
/// (на сетях, где ICMP режется, ICMP не отражает реальное качество канала).
/// </summary>
public sealed class PingProber : IDisposable
{
    private readonly ConcurrentQueue<PingSample> _samples = new();
    private readonly int _window;
    private readonly object _sync = new();

    public PingProber(int window = 24) => _window = Math.Max(4, window);

    public IReadOnlyList<PingSample> Samples => _samples.ToArray();

    /// <summary>ICMP-замер. Возвращает null, если узел не отвечает.</summary>
    public async Task<double?> ProbeAsync(string host, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(ct).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success) return (double)reply.RoundtripTime;
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (PingException) { return null; }
        catch (Exception) { return null; }
    }

    /// <summary>TCP-handshake замер до порта узла.</summary>
    public static async Task<double?> TcpProbeAsync(string host, int port, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return null;
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }
        catch (Exception) { return null; }
    }

    public void Record(bool ok, double milliseconds)
    {
        lock (_sync)
        {
            _samples.Enqueue(new PingSample(DateTime.UtcNow, ok, ok ? milliseconds : 0));
            while (_samples.Count > _window && _samples.TryDequeue(out _)) { }
        }
    }

    public void Clear()
    {
        lock (_sync) { _samples.Clear(); }
    }

    public NodeMetrics Compute()
    {
        var samples = _samples.ToArray();
        if (samples.Length == 0)
            return new NodeMetrics(0, 0, 0, 0, 0, 0, 0, 0);

        var ok = new List<double>(samples.Length);
        int failures = 0;
        foreach (var s in samples)
        {
            if (s.Ok) ok.Add(s.Milliseconds); else failures++;
        }

        double last = ok.Count > 0 ? ok[^1] : 0;
        double avg = ok.Count > 0 ? ok.Average() : 0;
        double min = ok.Count > 0 ? ok.Min() : 0;
        double max = ok.Count > 0 ? ok.Max() : 0;

        double jitter = 0;
        if (ok.Count > 1)
        {
            double sum = 0;
            for (int i = 1; i < ok.Count; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
            jitter = sum / (ok.Count - 1);
        }

        double loss = samples.Length == 0 ? 0 : failures * 100.0 / samples.Length;
        return new NodeMetrics(last, avg, min, max, jitter, loss, samples.Length, failures);
    }

    public void Dispose()
    {
        lock (_sync) { _samples.Clear(); }
    }
}

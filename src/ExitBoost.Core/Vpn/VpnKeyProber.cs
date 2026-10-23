using System.Collections.Concurrent;
using ExitBoost.Core.Diagnostics;

namespace ExitBoost.Core.Vpn;

/// <summary>Показатели качества VPN-ключа — те же, что у узлов ретранслятора.</summary>
public sealed class VpnKeyMetrics
{
    public double LastMs { get; init; }
    public double AvgMs { get; init; }
    public double MinMs { get; init; }
    public double MaxMs { get; init; }
    public double JitterMs { get; init; }
    public double LossPercent { get; init; }
    public int Samples { get; init; }
    public int Failures { get; init; }

    public bool HasData => Samples > 0;

    /// <summary>Человеческая оценка качества — как в таблице узлов.</summary>
    public string QualityText
    {
        get
        {
            if (!HasData) return "—";
            if (LossPercent >= 80) return "недоступен";
            if (LossPercent >= 15) return "плохо";
            if (AvgMs <= 100 && LossPercent < 5) return "отлично";
            if (AvgMs <= 200 && LossPercent < 10) return "хорошо";
            return "средне";
        }
    }

    public string StatusText => !HasData
        ? "нет данных"
        : LossPercent >= 80
            ? "недоступен"
            : $"{AvgMs:F0} мс · {LossPercent:F0}% потерь";
}

/// <summary>
/// Измеритель доступности VPN-серверов: TCP-handshake до host:port ключа.
/// Для sing-box-ключей этого достаточно, чтобы оценить канал до сервера.
/// </summary>
public sealed class VpnKeyProber
{
    private readonly ConcurrentDictionary<string, PingProber> _probers = new();

    /// <summary>Замерить ключ (несколько попыток) и вернуть агрегаты.</summary>
    public async Task<VpnKeyMetrics> ProbeAsync(
        VpnKey key,
        int count = 3,
        int timeoutMs = 1500,
        CancellationToken ct = default)
    {
        var prober = _probers.GetOrAdd(key.Id, _ => new PingProber(24));

        for (int i = 0; i < Math.Max(1, count); i++)
        {
            if (ct.IsCancellationRequested) break;
            var ms = await PingProber.TcpProbeAsync(key.Address, key.Port, timeoutMs, ct).ConfigureAwait(false);
            prober.Record(ms is not null, ms ?? 0);
            if (i + 1 < count)
            {
                try { await Task.Delay(150, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        var m = prober.Compute();
        return new VpnKeyMetrics
        {
            LastMs = m.LastMs,
            AvgMs = m.AvgMs,
            MinMs = m.MinMs,
            MaxMs = m.MaxMs,
            JitterMs = m.JitterMs,
            LossPercent = m.LossPercent,
            Samples = m.Samples,
            Failures = m.Failures
        };
    }

    public void Clear()
    {
        foreach (var p in _probers.Values) p.Clear();
        _probers.Clear();
    }
}

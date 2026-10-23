namespace ExitBoost.Core.Dpi;

/// <summary>
/// Автоподбор лучшей стратегии: перебирает стратегии zapret, применяет каждую
/// и замеряет качество до цели. Побеждает та, что даёт успешное TLS-рукопожатие
/// с минимальной задержкой.
/// </summary>
public sealed class DpiOptimizer
{
    private readonly ZapretRunner _runner;

    public ZapretRunner Runner => _runner;

    public DpiOptimizer(ZapretRunner runner) => _runner = runner;

    /// <summary>Понятное описание ошибки замера (вместо системных сообщений Windows).</summary>
    private static string FriendlyError(string error)
    {
        if (string.IsNullOrEmpty(error)) return error;

        // Windows: «Такой узел неизвестен.» (WSAHOST_NOT_FOUND)
        if (error.Contains("узел неизвестен", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return "DNS не смог определить адрес узла — проверьте интернет или DNS-настройки";

        if (error.Contains("удалённый" ) && error.Contains("отказ", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return "соединение отклонено — узел недоступен";
        if (error.Contains("таймаут") || error.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "таймаут — ответ не пришёл вовремя";
        if (error.Contains("RST", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("connection was forcibly closed", StringComparison.OrdinalIgnoreCase))
            return "соединение оборвано сбросом (признак блокировки DPI)";

        return error;
    }

    /// <summary>Замер без обхода — базовая линия.</summary>
    public async Task<DpiProbeResult> MeasureBaselineAsync(DpiTarget target, int attempts = 2, CancellationToken ct = default)
    {
        await _runner.StopAsync().ConfigureAwait(false);

        var host = target.HostList.FirstOrDefault();
        if (host is null) return DpiProbeResult.Failed("не указаны домены");

        int ok = 0;
        double tlsSum = 0, tcpSum = 0;
        string? error = null;

        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await DpiProbe.MeasureAsync(host, target.Port, ct: ct).ConfigureAwait(false);
            if (probe.Ok)
            {
                ok++;
                tlsSum += probe.TlsMs;
                tcpSum += probe.TcpMs;
            }
            else
            {
                error ??= probe.Error;
            }
        }

        return new DpiProbeResult(ok > 0, ok > 0 ? tcpSum / ok : 0, ok > 0, ok > 0 ? tlsSum / ok : 0, error);
    }

    /// <summary>
    /// Перебрать стратегии и вернуть отсортированный по качеству список.
    /// </summary>
    public async Task<IReadOnlyList<DpiStrategyResult>> OptimizeAsync(
        IReadOnlyList<DpiStrategy> strategies,
        DpiTarget target,
        int attempts = 3,
        IProgress<DpiStrategyResult>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<DpiStrategyResult>();
        var host = target.HostList.FirstOrDefault();
        if (host is null) return results;

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            bool started = await _runner.StartAsync(strategy, target, ct).ConfigureAwait(false);
            if (!started)
            {
                // Причина из вывода winws: какая ошибка не дала движку подняться.
                string tail = DpiOptimizerDetail.LastLine(_runner.LastOutput);
                string reason = string.IsNullOrEmpty(tail)
                    ? "движок не запустился"
                    : $"движок не запустился: {tail}";
                var failed = new DpiStrategyResult(strategy, false, 0, 0, 0, attempts, reason);
                results.Add(failed);
                progress?.Report(failed);
                continue;
            }

            int ok = 0;
            double tlsSum = 0, tcpSum = 0;
            string? error = null;

            for (int i = 0; i < attempts; i++)
            {
                ct.ThrowIfCancellationRequested();
                var probe = await DpiProbe.MeasureAsync(host, target.Port, ct: ct).ConfigureAwait(false);
                if (probe.Ok)
                {
                    ok++;
                    tlsSum += probe.TlsMs;
                    tcpSum += probe.TcpMs;
                }
                else
                {
                    error ??= FriendlyError(probe.Error);
                }
            }

            await _runner.StopAsync().ConfigureAwait(false);

            var result = new DpiStrategyResult(
                strategy,
                ok > 0,
                ok > 0 ? tlsSum / ok : 0,
                ok > 0 ? tcpSum / ok : 0,
                ok,
                attempts,
                error);

            results.Add(result);
            progress?.Report(result);
        }

        return results
            .OrderByDescending(r => r.Success)
            .ThenByDescending(r => r.Score)
            .ToList();
    }
}

internal static class DpiOptimizerDetail
{
    /// <summary>Последняя содержательная строка вывода winws (что именно сломалось).</summary>
    public static string LastLine(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}

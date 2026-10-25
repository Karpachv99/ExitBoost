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
    private static string FriendlyError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "";

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

    /// <summary>Замер без обхода по первому адресу (CLI и быстрая проверка).</summary>
    public async Task<DpiProbeResult> MeasureBaselineAsync(DpiTarget target, int attempts = 2, CancellationToken ct = default)
    {
        string? host = target.ProbeHostList.FirstOrDefault()
            ?? (target.IsGameMode ? target.GameProbeHosts.FirstOrDefault() : null);
        if (host is null) return DpiProbeResult.Failed("не указаны домены");

        DpiProbeResult probe = DpiProbeResult.Failed("нет ответа");
        for (int i = 0; i < Math.Max(1, attempts); i++)
        {
            probe = await ProbeAsync(target, host, ct).ConfigureAwait(false);
            if (probe.Ok) break;
        }
        return new DpiProbeResult(probe.Ok, probe.TcpMs, probe.Ok, probe.TlsMs, probe.Error);
    }

    /// <summary>
    /// Замер одного адреса: серверы игры проверяются TCP-коннектом на игровые
    /// порты (TLS у них нет), домены сайтов — TLS-рукопожатием. Режим
    /// определяется адресом, а не только целью: иначе включённые игровые порты
    /// ломали бы проверку обычных сайтов.
    /// </summary>
    public static async Task<DpiProbeResult> ProbeAsync(DpiTarget target, string host, CancellationToken ct)
    {
        bool gameHost = target.IsGameMode &&
            target.GameIpList.Contains(host, StringComparer.OrdinalIgnoreCase);

        if (!gameHost)
            return await DpiProbe.MeasureAsync(host, target.Port, ct: ct).ConfigureAwait(false);

        var ports = target.GameProbePorts;
        if (ports.Count == 0) ports = [target.Port];

        DpiProbeResult last = DpiProbeResult.Failed("таймаут");
        foreach (int port in ports)
        {
            last = await DpiProbe.MeasureTcpAsync(host, port, ct: ct).ConfigureAwait(false);
            if (last.Ok) return last;
        }
        return last;
    }

    /// <summary>
    /// Замер без обхода по цели: домены сайтов или адреса игровых серверов.
    /// Движок на время замера останавливается.
    /// </summary>
    public async Task<IReadOnlyList<DpiHostProbe>> MeasureTargetAsync(
        DpiTarget target, int attempts = 2, CancellationToken ct = default)
    {
        await _runner.StopAsync().ConfigureAwait(false);

        var hosts = target.IsGameMode && target.GameProbeHosts.Count > 0
            ? target.GameProbeHosts
            : target.ProbeHostList;

        var results = new List<DpiHostProbe>();
        foreach (string host in hosts
                     .Where(h => !string.IsNullOrWhiteSpace(h))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool ok = false;
            double tcpMs = 0, tlsMs = 0;
            string? error = null;

            for (int i = 0; i < Math.Max(1, attempts); i++)
            {
                ct.ThrowIfCancellationRequested();
                var probe = await ProbeAsync(target, host, ct).ConfigureAwait(false);
                if (probe.Ok)
                {
                    ok = true;
                    tcpMs = probe.TcpMs;
                    tlsMs = probe.TlsMs;
                    break;
                }
                error = probe.Error;
            }

            results.Add(new DpiHostProbe(host, ok, ok ? tcpMs : 0, ok ? tlsMs : 0, ok ? null : error));
        }

        return results;
    }

    /// <summary>
    /// Замер без обхода по нескольким доменам: показывает, какие из них
    /// действительно блокируются. Движок на время замера останавливается.
    /// </summary>
    public async Task<IReadOnlyList<DpiHostProbe>> MeasureBaselineAsync(
        IReadOnlyList<string> hosts,
        int port = 443,
        int attempts = 2,
        CancellationToken ct = default)
    {
        await _runner.StopAsync().ConfigureAwait(false);

        var results = new List<DpiHostProbe>();
        foreach (string host in hosts
                     .Where(h => !string.IsNullOrWhiteSpace(h))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool ok = false;
            double tcpMs = 0, tlsMs = 0;
            string? error = null;

            for (int i = 0; i < Math.Max(1, attempts); i++)
            {
                ct.ThrowIfCancellationRequested();
                var probe = await DpiProbe.MeasureAsync(host, port, ct: ct).ConfigureAwait(false);
                if (probe.Ok)
                {
                    ok = true;
                    tcpMs = probe.TcpMs;
                    tlsMs = probe.TlsMs;
                    break;
                }
                error = probe.Error;
            }

            results.Add(new DpiHostProbe(host, ok, ok ? tcpMs : 0, ok ? tlsMs : 0, ok ? null : error));
        }

        return results;
    }

    /// <summary>
    /// Домены, которые не открываются без обхода. Именно их должна поднять
    /// стратегия: если домен и так работает, он ничего не проверяет.
    /// </summary>
    public static IReadOnlyList<string> SelectBlockedHosts(IEnumerable<DpiHostProbe> probes) =>
        probes.Where(p => !p.Ok).Select(p => p.Host).ToList();

    /// <summary>
    /// Перебрать стратегии и вернуть отсортированный по качеству список.
    /// Стратегия считается рабочей, только если подняла все переданные домены:
    /// проверка по одному домену давала случайный результат, когда он и так
    /// открывался без обхода.
    /// </summary>
    public async Task<IReadOnlyList<DpiStrategyResult>> OptimizeAsync(
        IReadOnlyList<DpiStrategy> strategies,
        DpiTarget target,
        int attempts = 3,
        IProgress<DpiStrategyResult>? progress = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? hosts = null)
    {
        var results = new List<DpiStrategyResult>();

        var probeHosts = (hosts ?? target.ProbeHostList)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Игровой режим: перебор идёт по адресам серверов игры. Иначе сайтовые
        // стратегии проверялись бы на IP игры, а игровые — на доменах, и ни одна
        // не могла бы подойти ко всем целям сразу.
        if (target.IsGameMode && target.GameProbeHosts.Count > 0)
        {
            var gameHosts = target.GameProbeHosts
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var onlyGame = probeHosts
                .Where(h => gameHosts.Contains(h, StringComparer.OrdinalIgnoreCase))
                .ToList();

            probeHosts = onlyGame.Count > 0 ? onlyGame : gameHosts;
        }

        if (probeHosts.Count == 0) return results;

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            // Игровая стратегия без портов/IP игры затронула бы весь трафик
            // на 80/443 — в переборе она не участвует.
            string? notApplicable = strategy.Validate(target);
            if (notApplicable is not null)
            {
                var skipped = new DpiStrategyResult(strategy, false, 0, 0, 0, probeHosts.Count, notApplicable);
                results.Add(skipped);
                progress?.Report(skipped);
                continue;
            }

            bool started = await _runner.StartAsync(strategy, target, ct).ConfigureAwait(false);
            if (!started)
            {
                // Причина из вывода winws: какая ошибка не дала движку подняться.
                string tail = DpiOptimizerDetail.LastLine(_runner.LastOutput);
                string reason = string.IsNullOrEmpty(tail)
                    ? "движок не запустился"
                    : $"движок не запустился: {tail}";
                var failed = new DpiStrategyResult(strategy, false, 0, 0, 0, probeHosts.Count, reason);
                results.Add(failed);
                progress?.Report(failed);
                continue;
            }

            int hostsOk = 0;
            double tlsSum = 0, tcpSum = 0;
            string? error = null;

            foreach (string host in probeHosts)
            {
                bool hostOk = false;
                for (int i = 0; i < Math.Max(1, attempts); i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var probe = await ProbeAsync(target, host, ct).ConfigureAwait(false);
                    if (probe.Ok)
                    {
                        hostOk = true;
                        tlsSum += probe.TlsMs;
                        tcpSum += probe.TcpMs;
                        break;
                    }
                    error ??= $"{host}: {FriendlyError(probe.Error)}";
                }

                // Первый же неподнятый домен делает стратегию непригодной —
                // дальше перебирать её цели нет смысла.
                if (!hostOk) break;
                hostsOk++;
            }

            await _runner.StopAsync().ConfigureAwait(false);

            bool success = hostsOk == probeHosts.Count;
            var result = new DpiStrategyResult(
                strategy,
                success,
                success && hostsOk > 0 ? tlsSum / hostsOk : 0,
                success && hostsOk > 0 ? tcpSum / hostsOk : 0,
                hostsOk,
                probeHosts.Count,
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

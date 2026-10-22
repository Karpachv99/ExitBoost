using System.Text.Json;

namespace ExitBoost.Core.Networking;

/// <summary>
/// Проверка фактического доступа в интернет.
///
/// Не полагается на системный DNS (на пользовательских сетях он бывает подменён)
/// и не использует UDP-53 к публичным резолверам (часто режется провайдером).
/// Вместо этого шлёт DoH-запросы по HTTPS напрямую на фиксированные IP:
/// если получен настоящий DNS-ответ, интернет работает дота покрытий.
/// </summary>
public static class ConnectivityCheck
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "accept", "application/dns-json" } }
    };

    /// <summary>DoH-серверы для проверки (порядок — приоритет).</summary>
    private static readonly (string Url, string Label)[] DohServers =
    {
        ("https://1.1.1.1/dns-query?name=cloudflare.com&type=A", "Cloudflare"),
        ("https://8.8.8.8/resolve?name=cloudflare.com&type=A", "Google"),
    };

    /// <summary>Резервная цель, если DoH недоступен.</summary>
    private const string TraceUrl = "https://1.1.1.1/cdn-cgi/trace";

    /// <summary>
    /// Проверить доступ в интернет. Возвращает true, если хотя бы один
    /// публичный DoH-сервер ответил настоящим DNS-записью.
    /// </summary>
    public static async Task<ConnectivityResult> CheckAsync(CancellationToken ct = default)
    {
        string? lastError = null;

        foreach (var (url, label) in DohServers)
        {
            ct.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ok, error) = await DohQueryAsync(url, ct).ConfigureAwait(false);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ok)
                return new ConnectivityResult(true, label, ms, null, null);
            lastError = $"{label}: {error}";
        }

        // Резерв: CDN-трейс Cloudflare (не DNS — но и он достаточен).
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string body = await Http.GetStringAsync(TraceUrl, ct).ConfigureAwait(false);
            if (body.Contains("ip="))
                return new ConnectivityResult(true, "Cloudflare (trace)", sw.Elapsed.TotalMilliseconds, null, null);
            lastError = $"Cloudflare trace: нет поля ip в ответе";
        }
        catch (Exception ex)
        {
            lastError = $"Cloudflare trace: {ex.Message}";
        }

        return new ConnectivityResult(false, null, 0, null, lastError);
    }

    /// <summary>DoH-запрос через HTTPS на фиксированный IP с проверкой ответа.</summary>
    private static async Task<(bool Ok, string? Error)> DohQueryAsync(string url, CancellationToken ct)
    {
        try
        {
            string json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            int status = doc.RootElement.TryGetProperty("Status", out var st) ? st.GetInt32() : -1;
            if (status != 0)
                return (false, $"DNS-ответ не успешен (Status={status}) — вероятно, есть DNS-подмена");

            bool hasAnswer = doc.RootElement.TryGetProperty("Answer", out var answer) &&
                             answer.ValueKind == JsonValueKind.Array &&
                             answer.GetArrayLength() > 0;

            if (!hasAnswer)
                return (false, "ответ без записей — вероятно, DNS-перехват");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>Результат проверки доступа в интернет.</summary>
public sealed record ConnectivityResult(
    bool IsUp,
    string? Responder,
    double Ms,
    string? Server,
    string? Error)
{
    public string Summary => IsUp
        ? $"интернет есть ({Responder}, {Ms:F0} мс)"
        : $"интернет недоступен ({Error ?? "нет ответа"})";
}

using System.Diagnostics;
using System.Net.Http;

namespace ExitBoost.Core.Networking;

/// <summary>Результат теста скорости (через текущее соединение, в том числе VPN).</summary>
public sealed record SpeedTestResult(
    double LatencyMs,
    double JitterMs,
    double DownloadMbps,
    double UploadMbps,
    string? Error = null)
{
    public bool Ok => Error is null && DownloadMbps > 0;

    /// <summary>Короткая строка для интерфейса.</summary>
    public string Text => Error is not null
        ? "не удалось: " + Error
        : $"пинг {LatencyMs:0} мс · ↓ {DownloadMbps:0.0} Мбит/с · ↑ {UploadMbps:0.0} Мбит/с";
}

/// <summary>
/// Замер скорости через публичные точки Cloudflare (те же, что у speed.cloudflare.com):
/// задержка, джиттер, скачивание и отдача. Трафик идёт через активное соединение,
/// поэтому тест показывает реальную скорость выбранного VPN-ключа.
/// </summary>
public sealed class SpeedTester
{
    private const string DownUrl = "https://speed.cloudflare.com/__down?bytes={0}";
    private const string UpUrl = "https://speed.cloudflare.com/__up";

    private readonly HttpClient _http;

    public SpeedTester(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ExitBoost/1.0");
    }

    /// <summary>Размеры замеров: скачивание и отдача (байты). Cloudflare отдаёт не больше 10 МБ за запрос.</summary>
    public long DownloadBytes { get; init; } = 10L * 1024 * 1024;

    public long UploadBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>
    /// Полный тест. <paramref name="progress"/> получает короткие сообщения о ходе.
    /// </summary>
    public async Task<SpeedTestResult> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report("измеряю задержку…");
            var (latency, jitter) = await MeasureLatencyAsync(ct).ConfigureAwait(false);

            progress?.Report("проверяю скачивание…");
            double down = await MeasureDownloadAsync(progress, ct).ConfigureAwait(false);

            progress?.Report("проверяю отдачу…");
            double up = await MeasureUploadAsync(progress, ct).ConfigureAwait(false);

            progress?.Report("готово");
            return new SpeedTestResult(latency, jitter, down, up);
        }
        catch (OperationCanceledException)
        {
            return new SpeedTestResult(0, 0, 0, 0, "отменено");
        }
        catch (Exception ex)
        {
            return new SpeedTestResult(0, 0, 0, 0, ex.Message);
        }
    }

    private async Task<(double Latency, double Jitter)> MeasureLatencyAsync(CancellationToken ct)
    {
        var samples = new List<double>(5);
        for (int i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            using var resp = await _http.GetAsync(string.Format(DownUrl, 0), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            watch.Stop();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        if (samples.Count == 0) return (0, 0);

        double avg = samples.Average();
        double jitter = samples.Count < 2
            ? 0
            : samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        return (avg, jitter);
    }

    private async Task<double> MeasureDownloadAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(string.Format(DownUrl, DownloadBytes),
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[128 * 1024];
        long read = 0;
        int lastPercent = -1;

        var watch = Stopwatch.StartNew();
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;
            read += n;

            int percent = DownloadBytes > 0 ? (int)(read * 100 / DownloadBytes) : 0;
            if (percent >= lastPercent + 20)
            {
                lastPercent = percent;
                progress?.Report($"скачивание: {percent} %");
            }
        }
        watch.Stop();

        return Throughput(read, watch.Elapsed);
    }

    private async Task<double> MeasureUploadAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var payload = new byte[UploadBytes];
        Random.Shared.NextBytes(payload);

        var watch = Stopwatch.StartNew();
        using var content = new ProgressContent(payload, progress);
        using var resp = await _http.PostAsync(UpUrl, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        watch.Stop();

        return Throughput(UploadBytes, watch.Elapsed);
    }

    /// <summary>Бит в секунду → мегабиты в секунду.</summary>
    private static double Throughput(long bytes, TimeSpan elapsed)
        => elapsed.TotalSeconds <= 0 ? 0 : bytes * 8.0 / 1_000_000.0 / elapsed.TotalSeconds;

    /// <summary>Тело запроса с отчётом о прогрессе отдачи.</summary>
    private sealed class ProgressContent(byte[] payload, IProgress<string>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            const int chunk = 256 * 1024;
            int sent = 0;
            for (int offset = 0; offset < payload.Length; offset += chunk)
            {
                int size = Math.Min(chunk, payload.Length - offset);
                await stream.WriteAsync(payload.AsMemory(offset, size)).ConfigureAwait(false);
                sent += size;
                progress?.Report($"отдача: {sent * 100 / payload.Length} %");
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }
    }
}

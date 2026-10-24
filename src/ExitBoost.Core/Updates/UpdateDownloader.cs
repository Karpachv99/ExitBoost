using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ExitBoost.Core.Updates;

/// <summary>Архив скачан, но не совпал по размеру или SHA-256.</summary>
public sealed class UpdateIntegrityException : IOException
{
    public UpdateIntegrityException(string message) : base(message) { }
}

/// <summary>
/// Надёжное скачивание обновления: докачка по Range, параллельные части,
/// таймауты на чтение, повторные попытки и обязательная проверка SHA-256.
/// </summary>
public static class UpdateDownloader
{
    private const int MaxAttempts = 5;
    private const int ReadTimeoutSeconds = 60;
    private const int HeaderTimeoutSeconds = 45;
    private const int BufferSize = 128 * 1024;

    public static async Task<DownloadedUpdate> DownloadAsync(
        string url,
        long expectedSize,
        string expectedSha256,
        string targetPath,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Не задана ссылка на архив", nameof(url));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Не задан путь архива", nameof(targetPath));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);

        if (await TryVerifyExistingAsync(targetPath, expectedSize, expectedSha256, ct).ConfigureAwait(false))
        {
            long size = new FileInfo(targetPath).Length;
            progress?.Report(new UpdateProgress("проверено", size, size, 1));
            return new DownloadedUpdate(targetPath, size, expectedSha256.ToLowerInvariant());
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await DownloadCoreAsync(url, expectedSize, expectedSha256, targetPath, progress, ct)
                    .ConfigureAwait(false);
            }
            catch (UpdateIntegrityException) when (attempt == 0)
            {
                Cleanup(targetPath);
            }
        }
    }

    private static async Task<DownloadedUpdate> DownloadCoreAsync(
        string url,
        long expectedSize,
        string expectedSha256,
        string targetPath,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        using var http = CreateHttp();
        Probe probe = await ProbeAsync(http, url, expectedSize, ct).ConfigureAwait(false);

        IReadOnlyList<UpdatePart> parts = probe.SupportsRanges
            ? UpdatePlanner.BuildParts(probe.Total)
            : Array.Empty<UpdatePart>();

        if (parts.Count > 1)
        {
            try
            {
                await DownloadSegmentedAsync(http, url, probe, parts, expectedSha256, targetPath, progress, ct)
                    .ConfigureAwait(false);
                long size = new FileInfo(targetPath).Length;
                string actual = await ComputeSha256Async(targetPath, ct).ConfigureAwait(false);
                Verify(size, actual, probe.Total, expectedSha256);
                progress?.Report(new UpdateProgress("готово", size, size, 1));
                return new DownloadedUpdate(targetPath, size, actual);
            }
            catch (RangeNotSupportedException)
            {
                Cleanup(targetPath);
            }
        }

        DownloadedUpdate single = await DownloadSingleAsync(
            http, url, probe, expectedSha256, targetPath, progress, ct).ConfigureAwait(false);
        progress?.Report(new UpdateProgress("готово", single.Size, single.Size, 1));
        return single;
    }

    private static async Task<DownloadedUpdate> DownloadSingleAsync(
        HttpClient http,
        string url,
        Probe probe,
        string expectedSha256,
        string targetPath,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        string temp = targetPath + ".download";
        Exception? last = null;

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            TryDelete(temp);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendHeadersAsync(http, request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentRange?.Length
                             ?? response.Content.Headers.ContentLength
                             ?? probe.Total;
                if (total <= 0) total = probe.Total;

                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(
                    temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                using var hasher = SHA256.Create();
                await using var crypto = new CryptoStream(output, hasher, CryptoStreamMode.Write, leaveOpen: true);

                byte[] buffer = new byte[BufferSize];
                long received = 0;
                int lastPercent = -1;
                while (true)
                {
                    int read = await ReadWithTimeoutAsync(input, buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await crypto.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                    {
                        int percent = (int)(received * 100 / total);
                        if (percent > lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(new UpdateProgress(
                                "скачивание", received, total, Math.Min(1.0, (double)received / total)));
                        }
                    }
                }

                crypto.FlushFinalBlock();
                string actual = Convert.ToHexString(hasher.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
                long size = new FileInfo(temp).Length;

                Verify(size, actual, total, expectedSha256);
                File.Move(temp, targetPath, overwrite: true);
                return new DownloadedUpdate(targetPath, size, actual);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt + 1 < MaxAttempts)
            {
                last = ex;
                TryDelete(temp);
                await DelayAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (UpdateIntegrityException)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex) when (attempt + 1 >= MaxAttempts)
            {
                last = ex;
                break;
            }
        }

        TryDelete(temp);
        throw new IOException("Не удалось скачать архив обновления: " + (last?.Message ?? "неизвестная ошибка"), last);
    }

    private static async Task DownloadSegmentedAsync(
        HttpClient http,
        string url,
        Probe probe,
        IReadOnlyList<UpdatePart> parts,
        string expectedSha256,
        string targetPath,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        string partsDir = targetPath + ".parts";
        PreparePartsDirectory(partsDir, url, probe.Total, expectedSha256);

        var received = new long[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            string path = PartPath(partsDir, i);
            long length = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (length > parts[i].Length)
            {
                TryDelete(path);
                length = 0;
            }
            received[i] = length;
        }

        object gate = new();
        int lastPercent = -1;

        void Report(int index, long value)
        {
            lock (gate)
            {
                received[index] = value;
                long sum = 0;
                foreach (long item in received) sum += item;
                int percent = probe.Total > 0 ? (int)(sum * 100 / probe.Total) : 0;
                if (percent <= lastPercent) return;
                lastPercent = percent;
                progress?.Report(new UpdateProgress(
                    "скачивание", sum, probe.Total, Math.Min(0.99, (double)sum / probe.Total)));
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new Task[parts.Count];
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                int index = i;
                tasks[index] = DownloadPartWithRetryAsync(
                    http, url, probe, parts[index], PartPath(partsDir, index),
                    value => Report(index, value), linked.Token);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            linked.Cancel();
            throw;
        }

        for (int i = 0; i < parts.Count; i++)
        {
            long length = new FileInfo(PartPath(partsDir, i)).Length;
            if (length != parts[i].Length)
                throw new IOException($"Фрагмент {i + 1} скачан не полностью ({length} из {parts[i].Length} байт)");
        }

        await AssembleAsync(partsDir, parts, targetPath, probe.Total, expectedSha256, ct).ConfigureAwait(false);
        TryDeleteDirectory(partsDir);
    }

    private static async Task DownloadPartWithRetryAsync(
        HttpClient http,
        string url,
        Probe probe,
        UpdatePart part,
        string path,
        Action<long> report,
        CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadPartAttemptAsync(http, url, probe, part, path, report, ct).ConfigureAwait(false);
                return;
            }
            catch (RangeNotSupportedException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt + 1 < MaxAttempts)
            {
                last = ex;
                long have = File.Exists(path) ? new FileInfo(path).Length : 0;
                report(have);
                await DelayAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        throw new IOException(
            $"Не удалось скачать диапазон {part.From}-{part.To}: {last?.Message ?? "неизвестная ошибка"}", last);
    }

    private static async Task DownloadPartAttemptAsync(
        HttpClient http,
        string url,
        Probe probe,
        UpdatePart part,
        string path,
        Action<long> report,
        CancellationToken ct)
    {
        long expected = part.Length;
        long have = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (have > expected)
        {
            TryDelete(path);
            have = 0;
        }
        if (have == expected)
        {
            report(have);
            return;
        }

        long from = part.From + have;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, part.To);

        if (have > 0)
        {
            if (probe.ETag is not null && EntityTagHeaderValue.TryParse(probe.ETag, out EntityTagHeaderValue? etag))
                request.Headers.IfRange = new RangeConditionHeaderValue(etag);
            else if (probe.LastModified is { } lastModified)
                request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
        }

        using var response = await SendHeadersAsync(http, request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDelete(path);
            throw new IOException("Сервер отклонил запрошенный диапазон");
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            // Сервер проигнорировал Range. Если часть уже была начата —
            // начинаем её заново; если нет — переключаемся на обычное скачивание.
            TryDelete(path);
            report(0);
            if (have > 0) throw new IOException("Сервер проигнорировал диапазон, часть скачивается заново");
            throw new RangeNotSupportedException();
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException(
                $"Сервер вернул {(int)response.StatusCode} вместо части файла");

        var range = response.Content.Headers.ContentRange;
        if (range?.From != from || range.To != part.To || range.Length != probe.Total)
        {
            TryDelete(path);
            throw new IOException("Сервер вернул неверный диапазон файла");
        }

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(
            path, have == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        byte[] buffer = new byte[BufferSize];
        long totalRead = have;
        while (totalRead < expected)
        {
            int read = await ReadWithTimeoutAsync(input, buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (totalRead + read > expected) read = (int)(expected - totalRead);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalRead += read;
            report(totalRead);
        }

        if (totalRead != expected)
            throw new IOException($"Диапазон скачан не полностью ({totalRead} из {expected} байт)");
    }

    private static async Task AssembleAsync(
        string partsDir,
        IReadOnlyList<UpdatePart> parts,
        string targetPath,
        long total,
        string expectedSha256,
        CancellationToken ct)
    {
        string temp = targetPath + ".assembling";
        TryDelete(temp);

        string actual;
        long size;
        await using (var output = new FileStream(
                         temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        using (var hasher = SHA256.Create())
        await using (var crypto = new CryptoStream(output, hasher, CryptoStreamMode.Write, leaveOpen: true))
        {
            for (int i = 0; i < parts.Count; i++)
            {
                await using var input = new FileStream(
                    PartPath(partsDir, i), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                if (input.Length != parts[i].Length)
                    throw new IOException($"Фрагмент {i + 1} имеет неверный размер");
                await input.CopyToAsync(crypto, BufferSize, ct).ConfigureAwait(false);
            }
            crypto.FlushFinalBlock();
            actual = Convert.ToHexString(hasher.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
            size = output.Length;
        }

        Verify(size, actual, total, expectedSha256);
        File.Move(temp, targetPath, overwrite: true);
    }

    private static async Task<Probe> ProbeAsync(
        HttpClient http, string url, long expectedSize, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await SendHeadersAsync(http, request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new IOException("Сервер не отдаёт файл (416)");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Сервер вернул {(int)response.StatusCode} {response.ReasonPhrase}");

        long total = response.Content.Headers.ContentRange?.Length
                     ?? response.Content.Headers.ContentLength
                     ?? expectedSize;

        if (total <= 0) total = expectedSize;
        if (total <= 0) throw new IOException("Не удалось определить размер архива");

        if (expectedSize > 0 && total != expectedSize)
        {
            throw new UpdateIntegrityException(
                $"Сервер отдаёт архив другого размера ({total} вместо {expectedSize} байт)");
        }

        bool supportsRanges = response.StatusCode == HttpStatusCode.PartialContent
                              && response.Content.Headers.ContentRange?.Length is > 0;

        return new Probe(
            total,
            supportsRanges,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified);
    }

    private static async Task<bool> TryVerifyExistingAsync(
        string path, long expectedSize, string expectedSha256, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64) return false;
        if (expectedSize > 0 && new FileInfo(path).Length != expectedSize) return false;

        string actual = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Verify(long actualSize, string actualSha, long expectedSize, string expectedSha)
    {
        if (expectedSize > 0 && actualSize != expectedSize)
        {
            throw new UpdateIntegrityException(
                $"Размер архива не совпал: получено {actualSize}, ожидалось {expectedSize} байт");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha)
            && !actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateIntegrityException(
                $"SHA-256 не совпала: получено {actualSha}, ожидалось {expectedSha.ToLowerInvariant()}");
        }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExitBoost/1.0");
        return http;
    }

    private static async Task<HttpResponseMessage> SendHeadersAsync(
        HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(HeaderTimeoutSeconds));
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Таймаут подключения к серверу обновлений");
        }
    }

    private static async ValueTask<int> ReadWithTimeoutAsync(
        Stream input, Memory<byte> buffer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(ReadTimeoutSeconds));
        try
        {
            return await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Таймаут чтения данных обновления");
        }
    }

    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or IOException or TaskCanceledException;

    private static Task DelayAsync(int attempt, CancellationToken ct)
    {
        int seconds = Math.Min(15, 1 << Math.Min(4, attempt));
        return Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }

    private static string PartPath(string partsDir, int index) => Path.Combine(partsDir, $"part-{index}.bin");

    private static void PreparePartsDirectory(string partsDir, string url, long total, string sha)
    {
        string marker = Path.Combine(partsDir, "source.txt");
        string expected = $"{url}\n{total}\n{sha.ToLowerInvariant()}\n";
        if (Directory.Exists(partsDir) && File.Exists(marker))
        {
            try
            {
                if (!File.ReadAllText(marker).Equals(expected, StringComparison.Ordinal))
                {
                    TryDeleteDirectory(partsDir);
                }
            }
            catch
            {
                TryDeleteDirectory(partsDir);
            }
        }
        else if (Directory.Exists(partsDir))
        {
            TryDeleteDirectory(partsDir);
        }

        Directory.CreateDirectory(partsDir);
        File.WriteAllText(marker, expected);
    }

    private static void Cleanup(string targetPath)
    {
        TryDelete(targetPath);
        TryDelete(targetPath + ".download");
        TryDelete(targetPath + ".assembling");
        TryDeleteDirectory(targetPath + ".parts");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed record Probe(long Total, bool SupportsRanges, string? ETag, DateTimeOffset? LastModified);

    private sealed class RangeNotSupportedException : Exception
    {
        public RangeNotSupportedException() : base("Сервер не поддерживает Range") { }
    }
}

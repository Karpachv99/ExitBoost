using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace ExitBoost.Core.Updates;

/// <summary>
/// Загрузка и установка обновления: скачиваем zip-архив из ссылки релиза
/// и распаковываем поверх текущей папки программы.
/// </summary>
public static class UpdateInstaller
{
    private const long ParallelThreshold = 8L * 1024 * 1024;
    private const long ChunkSize = 4L * 1024 * 1024;
    private const int MaxParallel = 6;

    /// <summary>
    /// Скачать файл с прогрессом (0..1). Возвращает путь к скачанному файлу.
    /// Большие файлы качаются несколькими потоками по диапазонам — с CDN
    /// одно соединение часто отдаёт всего десятки килобайт в секунду.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string url,
        string targetPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        long expectedSize = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        using var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromMinutes(20)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExitBoost/1.0");

        // Узнаём размер файла и поддерживает ли сервер докачку по диапазонам.
        long total = expectedSize;
        bool supportsRanges = false;
        using (var probe = new HttpRequestMessage(HttpMethod.Get, url))
        {
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                // CDN иногда возвращает 206 с Content-Length только
                // для фактически отданного фрагмента. Используем диапазоны
                // только если сервер сообщил полный размер в Content-Range.
                long? fullLength = resp.Content.Headers.ContentRange?.Length;
                supportsRanges = resp.StatusCode == HttpStatusCode.PartialContent
                    && fullLength is > 0;
                if (fullLength is > 0)
                {
                    total = fullLength.Value;
                    supportsRanges = resp.StatusCode == HttpStatusCode.PartialContent;
                }
                else if (resp.StatusCode != HttpStatusCode.PartialContent && resp.Content.Headers.ContentLength is > 0)
                {
                    total = resp.Content.Headers.ContentLength.Value;
                }
            }
        }

        if (!supportsRanges || total < ParallelThreshold)
        {
            try
            {
                return await DownloadSingleAsync(http, url, targetPath, progress, total, ct)
                    .ConfigureAwait(false);
            }
            catch (IOException) when (total >= ParallelThreshold && !ct.IsCancellationRequested)
            {
                // Некоторые CDN/прокси обрывают обычный ответ, не закрывая
                // соединение корректно. Сначала пытаемся продолжить с принятого
                // байта, а если сервер не поддерживает Range — используем части.
                try
                {
                    return await DownloadResumableAsync(http, url, targetPath, progress, total, ct)
                        .ConfigureAwait(false);
                }
                catch (IOException) when (!ct.IsCancellationRequested)
                {
                    // Переходим к независимым диапазонам ниже.
                }
            }
        }

        if (total < ParallelThreshold)
            throw new IOException("Сервер прервал загрузку архива; повторите попытку позже");

        int parts = (int)Math.Min(MaxParallel, (total + ChunkSize - 1) / ChunkSize);
        string tempDir = Path.Combine(Path.GetTempPath(), "ExitBoost-dl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        var partBytes = new long[parts];
        var gate = new object();
        int lastPercent = -1;

        void Report(int index, long bytes)
        {
            lock (gate)
            {
                partBytes[index] = bytes;
                long sum = 0;
                foreach (long b in partBytes) sum += b;
                int percent = total > 0 ? (int)(sum * 100 / total) : 0;
                if (percent <= lastPercent) return;
                lastPercent = percent;
                progress?.Report(Math.Min(1.0, (double)sum / total));
            }
        }

        try
        {
            var tasks = new Task[parts];
            for (int i = 0; i < parts; i++)
            {
                int index = i;
                long from = index * ChunkSize;
                long to = Math.Min(total - 1, from + ChunkSize - 1);
                string partPath = Path.Combine(tempDir, $"part{index}.bin");

                tasks[index] = DownloadPartAsync(http, url, from, to, partPath,
                    read => Report(index, read), ct);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Собираем фрагменты в один файл по порядку.
            await using (var output = File.Create(targetPath))
            {
                for (int i = 0; i < parts; i++)
                {
                    await using var input = File.OpenRead(Path.Combine(tempDir, $"part{i}.bin"));
                    await input.CopyToAsync(output, 256 * 1024, ct).ConfigureAwait(false);
                }
            }

            long actualLength = new FileInfo(targetPath).Length;
            if (actualLength != total)
                throw new IOException($"Архив скачан не полностью ({actualLength} из {total} байт)");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        progress?.Report(1);
        return targetPath;
    }

    private static async Task<string> DownloadSingleAsync(
        HttpClient http, string url, string targetPath, IProgress<double>? progress,
        long expectedLength, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? expectedLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(targetPath);

        var buffer = new byte[128 * 1024];
        long read = 0;
        int lastPercent = -1;
        while (true)
        {
            int n = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total <= 0) continue;
            int percent = (int)(read * 100 / total);
            if (percent <= lastPercent) continue;
            lastPercent = percent;
            progress?.Report((double)read / total);
        }

        if (expectedLength > 0 && read != expectedLength)
            throw new IOException($"Архив скачан не полностью ({read} из {expectedLength} байт)");

        progress?.Report(1);
        return targetPath;
    }

    /// <summary>Возобновить оборванную загрузку с Range, сохраняя уже принятые байты.</summary>
    private static async Task<string> DownloadResumableAsync(
        HttpClient http, string url, string targetPath, IProgress<double>? progress,
        long expectedLength, CancellationToken ct)
    {
        const int maxAttempts = 8;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            long offset = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0;
            if (offset > expectedLength)
            {
                File.Delete(targetPath);
                offset = 0;
            }
            if (offset == expectedLength)
            {
                progress?.Report(1);
                return targetPath;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent
                    || response.Content.Headers.ContentRange?.From != offset))
                {
                    // Сервер проигнорировал Range: перезапускаем файл с нуля,
                    // не допуская склейки полного ответа с его префиксом.
                    File.Delete(targetPath);
                    continue;
                }

                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(targetPath,
                    offset == 0 ? FileMode.Create : FileMode.Append,
                    FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                var buffer = new byte[128 * 1024];
                long received = offset;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (count == 0) break;
                    if (received + count > expectedLength)
                        throw new IOException("Сервер передал больше данных, чем заявленный размер архива");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    received += count;
                    progress?.Report((double)received / expectedLength);
                }

                if (received == expectedLength)
                {
                    progress?.Report(1);
                    return targetPath;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       && attempt + 1 < maxAttempts && !ct.IsCancellationRequested)
            {
                await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }

        throw new IOException($"Сервер несколько раз прервал загрузку ({new FileInfo(targetPath).Length} из {expectedLength} байт)");
    }

    private static async Task DownloadPartAsync(
        HttpClient http, string url, long from, long to, string path, Action<long> report, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadRangeAsync(http, url, from, to, path, report, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                report(0);
                await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task DownloadRangeAsync(
        HttpClient http, string url, long from, long to, string path, Action<long> report, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);

        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidOperationException("Сервер не отдаёт файл частями");

        long expected = to - from + 1;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);

        var buffer = new byte[128 * 1024];
        long read = 0;
        while (true)
        {
            int n = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            report(read);
        }

        if (read != expected)
            throw new IOException($"Фрагмент скачан не полностью ({read} из {expected} байт)");
    }

    /// <summary>
    /// Распаковать архив в целевую папку (файлы перезаписываются).
    /// Бросает исключение, если папка защищена от записи.
    /// </summary>
    public static void Extract(string zipPath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length == 0 && string.IsNullOrEmpty(entry.Name)) continue;

            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(targetDirectory, relative));

            // не даём распаковать что-то вне целевой папки
            if (!destination.StartsWith(Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>Папка, куда установлено приложение (куда распаковывать обновление).</summary>
    public static string InstallDirectory => AppContext.BaseDirectory;
}

using System.IO.Compression;

namespace ExitBoost.Core.Updates;

/// <summary>
/// Загрузка и установка обновления: скачиваем zip-архив релиза с GitHub
/// и распаковываем поверх текущей папки программы.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Скачать файл с прогрессом (0..1). Возвращает путь к скачанному файлу.</summary>
    public static async Task<string> DownloadAsync(
        string url,
        string targetPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExitBoost/1.0");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? 0;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(targetPath);

        var buffer = new byte[128 * 1024];
        long read = 0;
        while (true)
        {
            int n = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }

        progress?.Report(1);
        return targetPath;
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

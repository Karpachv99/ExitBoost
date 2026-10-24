using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ExitBoost.Core.Updates;

/// <summary>Запущенный updater и пути его журнала/результата.</summary>
public sealed record UpdateLaunch(Process Process, string ResultPath, string LogPath);

/// <summary>
/// Точка входа для UI: проверка, скачивание и запуск отдельного updater-а.
/// </summary>
public static class UpdateService
{
    public const string StableChannel = "stable";

    public static string UpdatesRoot
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExitBoost", "updates");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogsDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExitBoost", "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ResultFile => Path.Combine(UpdatesRoot, "last-result.json");

    public static Task<UpdateCheckResult> CheckAsync(
        string owner,
        string repo,
        string currentVersion,
        CancellationToken ct = default)
        => UpdateChecker.CheckAsync(owner, repo, currentVersion, StableChannel, ct);

    public static string GetDownloadPath(UpdateInfo info)
    {
        string version = SafeName(info.Version);
        string hash = info.Sha256.Length >= 8 ? info.Sha256[..8] : "legacy";
        string name = SafeName(Path.GetFileName(info.AssetName));
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name = $"ExitBoost-{version}-win-x64.zip";

        string dir = Path.Combine(UpdatesRoot, $"{version}-{hash}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public static Task<DownloadedUpdate> DownloadAsync(
        UpdateInfo info,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        string path = GetDownloadPath(info);
        return UpdateDownloader.DownloadAsync(
            info.AssetUrl, info.AssetSize, info.Sha256, path, progress, ct);
    }

    /// <summary>
    /// Скопировать updater во временную папку и запустить его. Копия нужна потому,
    /// что updater переименовывает всю папку установки, включая самого себя.
    /// </summary>
    public static UpdateLaunch LaunchUpdater(
        UpdateInfo info,
        string zipPath,
        string installDirectory,
        string exePath,
        int processId)
    {
        string install = Path.GetFullPath(installDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string exe = Path.GetFullPath(exePath);

        if (!IsInside(exe, install))
            throw new InvalidOperationException("Исполняемый файл находится вне папки установки");

        string updaterSource = Path.Combine(install, "ExitBoost.Updater.exe");
        if (!File.Exists(updaterSource))
        {
            throw new FileNotFoundException(
                "В папке установки нет ExitBoost.Updater.exe. Установите версию 1.0.17 вручную один раз.",
                updaterSource);
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string work = Path.Combine(UpdatesRoot, $"{SafeName(info.Version)}-{stamp}", "apply");
        Directory.CreateDirectory(work);

        string updater = Path.Combine(work, "ExitBoost.Updater.exe");
        File.Copy(updaterSource, updater, overwrite: true);

        string log = Path.Combine(LogsDirectory, $"update-{SafeName(info.Version)}-{stamp}.log");
        string result = ResultFile;
        string args = UpdaterCommandLine.Build(
            processId, zipPath, info.Sha256, info.AssetSize,
            install, exe, info.Version, result, log);

        var startInfo = new ProcessStartInfo
        {
            FileName = updater,
            Arguments = args,
            WorkingDirectory = work,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Не удалось запустить ExitBoost.Updater.exe");

        return new UpdateLaunch(process, result, log);
    }

    /// <summary>Прочитать и удалить результат прошлого обновления (показывается один раз).</summary>
    public static UpdateApplyResult? ReadAndClearResult()
    {
        string path = ResultFile;
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new UpdateApplyResult
            {
                Status = GetString(root, "status"),
                Version = GetString(root, "version"),
                Error = GetString(root, "error"),
                CompletedAt = DateTimeOffset.TryParse(GetString(root, "completedAt"), out var at) ? at : null,
                LogPath = GetString(root, "logPath"),
                BackupPath = GetString(root, "backupPath")
            };
            return result;
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult
            {
                Status = "failed",
                Error = "не удалось прочитать результат обновления: " + ex.Message
            };
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Удалить старые каталоги загрузок (старше 7 дней).</summary>
    public static void CleanupOldDownloads()
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(UpdatesRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-7))
                        Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool IsInside(string path, string root)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Trim();
    }
}

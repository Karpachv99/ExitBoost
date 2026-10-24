using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ExitBoost.Updater;

/// <summary>
/// Отдельный маленький updater. Запускается из копии во временной папке,
/// ждёт выхода приложения, проверяет архив, распаковывает его в staging,
/// подменяет папку установки и при неудаче откатывается на предыдущую версию.
/// </summary>
internal static class Program
{
    private static Options? _options;
    private static Log? _log;
    private static string? _staging;
    private static string? _backup;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!Options.TryParse(args, out Options options, out string parseError))
        {
            TryWriteEarlyResult(args, parseError);
            return 2;
        }

        _options = options;
        _log = new Log(options.LogPath);
        _log.Write($"updater start: version={options.Version}, pid={options.ProcessId}, install={options.InstallDirectory}");

        try
        {
            if (!WaitForProcessExit(options.ProcessId, TimeSpan.FromMinutes(10)))
                return Fail("Приложение не завершилось за 10 минут — обновление отменено");

            using var mutex = new Mutex(false, @"Local\ExitBoost.Updater");
            if (!mutex.WaitOne(TimeSpan.FromSeconds(20)))
                return Fail("Другой экземпляр updater-а уже выполняется");

            try
            {
                return Run(options);
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
        catch (Exception ex)
        {
            _log.Write("fatal: " + ex);
            return Fail("Внутренняя ошибка updater-а: " + ex.Message);
        }
        finally
        {
            CleanupStaging();
        }
    }

    private static int Run(Options options)
    {
        if (!VerifyArchive(options))
            return Fail("Архив обновления повреждён (не совпал размер или SHA-256)");

        string parent = Path.GetDirectoryName(options.InstallDirectory)
                        ?? throw new InvalidOperationException("Не удалось определить родительскую папку установки");
        _staging = Path.Combine(parent, ".exitboost-staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_staging);
        _log!.Write("extracting to " + _staging);

        if (!ExtractArchive(options.ZipPath, _staging))
            return Fail("Не удалось распаковать архив обновления");

        string stagedExe = Path.Combine(_staging, "ExitBoost.exe");
        if (!File.Exists(stagedExe))
            return Fail("В архиве нет ExitBoost.exe");

        if (!VersionMatches(stagedExe, options.Version))
            return Fail("Версия файлов в архиве не совпадает с ожидаемой " + options.Version);

        PreserveUninstaller(options.InstallDirectory, _staging);

        string backup = options.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar) + ".old";
        if (Directory.Exists(backup))
        {
            try
            {
                Directory.Delete(backup, recursive: true);
            }
            catch
            {
                backup = options.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar)
                         + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            }
        }

        _log.Write("moving install to backup: " + backup);
        if (!MoveDirectory(options.InstallDirectory, backup))
            return Fail("Не удалось создать резервную копию папки установки. Закройте антивирус и повторите.");

        _backup = backup;

        _log.Write("moving staging to install");
        if (!MoveDirectory(_staging, options.InstallDirectory))
        {
            bool restored = Rollback(options, backup);
            return Fail("Не удалось заменить папку установки", restored);
        }
        _staging = null;

        string newExe = options.ExePath;
        if (!File.Exists(newExe))
        {
            bool restored = Rollback(options, backup);
            return Fail("После установки не найден ExitBoost.exe", restored);
        }

        _log.Write("starting new version");
        if (!StartApplication(options.InstallDirectory, newExe))
        {
            bool restored = Rollback(options, backup);
            return Fail("Не удалось запустить новую версию", restored);
        }

        if (!SmokeCheck(newExe))
        {
            bool restored = Rollback(options, backup);
            return Fail("Новая версия завершилась сразу после запуска", restored);
        }

        _log.Write("update completed successfully");
        WriteResult("success", "", backup);
        TryDelete(options.ZipPath);
        return 0;
    }

    private static bool VerifyArchive(Options options)
    {
        var file = new FileInfo(options.ZipPath);
        if (!file.Exists)
        {
            _log!.Write("zip not found: " + options.ZipPath);
            return false;
        }

        if (file.Length != options.Size)
        {
            _log!.Write($"zip size mismatch: {file.Length} != {options.Size}");
            return false;
        }

        if (options.Sha256.Length == 0)
        {
            _log!.Write("sha256 is empty; only size is verified");
            return true;
        }

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(options.ZipPath);
        string actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        if (!actual.Equals(options.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _log!.Write($"sha256 mismatch: {actual} != {options.Sha256}");
            return false;
        }

        return true;
    }

    private static bool ExtractArchive(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination);
        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                string relative = entry.FullName
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(root, relative));

                if (!target.Equals(root, StringComparison.OrdinalIgnoreCase)
                    && !target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    _log!.Write("skip path outside staging: " + entry.FullName);
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                string? folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                entry.ExtractToFile(target, overwrite: true);
            }
            return true;
        }
        catch (Exception ex)
        {
            _log!.Write("extract failed: " + ex);
            return false;
        }
    }

    private static bool VersionMatches(string exePath, string expected)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            string text = info.FileVersion ?? info.ProductVersion ?? "";

            if (!Version.TryParse(expected, out Version? want))
            {
                _log!.Write("cannot parse expected version; skip check: " + expected);
                return true;
            }

            if (!Version.TryParse(text, out Version? actual))
            {
                // Ожидаемая версия известна, а у файла её прочитать нельзя —
                // это либо чужая сборка, либо повреждённый архив. Не рискуем.
                _log!.Write("cannot read file version: " + text);
                return false;
            }

            bool match = actual.Major == want.Major
                         && actual.Minor == want.Minor
                         && actual.Build == want.Build;
            if (!match) _log!.Write($"version mismatch: file={actual}, expected={want}");
            return match;
        }
        catch (Exception ex)
        {
            _log!.Write("version check failed: " + ex.Message);
            return false;
        }
    }

    private static void PreserveUninstaller(string installDir, string stagingDir)
    {
        // Inno Setup хранит unins000.exe/.dat рядом с программой. При замене
        // папки их нужно перенести, иначе «Удаление программы» перестанет работать.
        foreach (string pattern in new[] { "unins*.exe", "unins*.dat" })
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(installDir, pattern, SearchOption.TopDirectoryOnly))
                {
                    string target = Path.Combine(stagingDir, Path.GetFileName(file));
                    File.Copy(file, target, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _log!.Write($"cannot preserve {pattern}: {ex.Message}");
            }
        }
    }

    private static bool MoveDirectory(string from, string to)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log!.Write($"move attempt {attempt + 1} failed: {ex.Message}");
                Thread.Sleep(500 * (attempt + 1));
            }
        }
        return false;
    }

    private static bool StartApplication(string installDir, string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = installDir,
                UseShellExecute = true
            };
            return Process.Start(psi) is not null;
        }
        catch (Exception ex)
        {
            _log!.Write("start failed: " + ex);
            return false;
        }
    }

    private static bool SmokeCheck(string exePath)
    {
        try
        {
            Thread.Sleep(5000);
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath));
            if (processes.Length == 0)
            {
                _log!.Write("smoke check: process is not running");
                return false;
            }

            foreach (var process in processes)
            {
                try { process.Dispose(); } catch { }
            }
            return true;
        }
        catch (Exception ex)
        {
            _log!.Write("smoke check failed: " + ex.Message);
            return true;
        }
    }

    private static bool Rollback(Options options, string backup)
    {
        _log?.Write("rolling back");
        try
        {
            if (Directory.Exists(options.InstallDirectory))
            {
                string failed = options.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar)
                                + ".failed-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
                Directory.Move(options.InstallDirectory, failed);
                try { Directory.Delete(failed, recursive: true); } catch { }
            }

            Directory.Move(backup, options.InstallDirectory);
            _backup = null;
            StartApplication(options.InstallDirectory, options.ExePath);
            _log?.Write("rollback completed");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Write("rollback failed: " + ex);
            return false;
        }
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            _log!.Write("waiting for process exit");
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                _log.Write("process wait timeout");
                return false;
            }
        }
        catch (ArgumentException)
        {
            _log!.Write("process already exited");
        }
        catch (Exception ex)
        {
            _log!.Write("wait failed: " + ex.Message);
        }

        Thread.Sleep(1000);
        return true;
    }

    private static int Fail(string error, bool rolledBack = false)
    {
        _log?.Write("FAIL: " + error);
        WriteResult(rolledBack ? "rolled_back" : "failed", error, _backup ?? "");
        return 1;
    }

    private static void WriteResult(string status, string error, string backup)
    {
        Options? options = _options;
        if (options is null || string.IsNullOrWhiteSpace(options.ResultPath)) return;

        try
        {
            string json = "{"
                          + "\"status\":" + Json(status) + ","
                          + "\"version\":" + Json(options.Version) + ","
                          + "\"error\":" + Json(error) + ","
                          + "\"completedAt\":" + Json(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)) + ","
                          + "\"logPath\":" + Json(options.LogPath) + ","
                          + "\"backupPath\":" + Json(backup)
                          + "}";

            string? folder = Path.GetDirectoryName(options.ResultPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temp = options.ResultPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, options.ResultPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Write("cannot write result: " + ex.Message);
        }
    }

    private static string Json(string value)
        => "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t") + "\"";

    private static void CleanupStaging()
    {
        if (_staging is null) return;
        try
        {
            if (Directory.Exists(_staging)) Directory.Delete(_staging, recursive: true);
        }
        catch (Exception ex)
        {
            _log?.Write("cannot cleanup staging: " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryWriteEarlyResult(string[] args, string error)
    {
        string? result = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--result", StringComparison.OrdinalIgnoreCase))
            {
                result = args[i + 1];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(result)) return;

        try
        {
            string json = "{\"status\":\"failed\",\"version\":\"\",\"error\":"
                          + Json("Некорректные аргументы updater-а: " + error)
                          + ",\"completedAt\":" + Json(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                          + ",\"logPath\":\"\",\"backupPath\":\"\"}";
            File.WriteAllText(result, json, new UTF8Encoding(false));
        }
        catch { }
    }

    private sealed class Log
    {
        private readonly string _path;

        public Log(string path)
        {
            _path = path;
            try
            {
                string? folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            }
            catch { }
        }

        public void Write(string message)
        {
            try
            {
                File.AppendAllText(
                    _path,
                    $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    private sealed class Options
    {
        public int ProcessId { get; private init; }
        public string ZipPath { get; private init; } = "";
        public string Sha256 { get; private init; } = "";
        public long Size { get; private init; }
        public string InstallDirectory { get; private init; } = "";
        public string ExePath { get; private init; } = "";
        public string Version { get; private init; } = "";
        public string ResultPath { get; private init; } = "";
        public string LogPath { get; private init; } = "";

        public static bool TryParse(string[] args, out Options options, out string error)
        {
            options = new Options();
            error = "";

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal)) continue;

                string value;
                int equals = key.IndexOf('=');
                if (equals > 0)
                {
                    value = key[(equals + 1)..];
                    key = key[..equals];
                }
                else
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "нет значения для " + key;
                        return false;
                    }
                    value = args[++i];
                }
                values[key[2..]] = value;
            }

            if (!values.TryGetValue("pid", out string? pidText)
                || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                || pid <= 0)
            {
                error = "--pid должен быть положительным числом";
                return false;
            }

            if (!values.TryGetValue("size", out string? sizeText)
                || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out long size)
                || size <= 0)
            {
                error = "--size должен быть положительным числом";
                return false;
            }

            string zip = GetFullPath(values, "zip", ref error);
            string install = GetFullPath(values, "install", ref error);
            string exe = GetFullPath(values, "exe", ref error);
            if (error.Length > 0) return false;

            if (!File.Exists(zip))
            {
                error = "архив не найден: " + zip;
                return false;
            }
            if (!Directory.Exists(install))
            {
                error = "папка установки не найдена: " + install;
                return false;
            }
            if (!File.Exists(exe))
            {
                error = "исполняемый файл не найден: " + exe;
                return false;
            }

            string root = Path.GetPathRoot(install)?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
            if (install.TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                error = "нельзя обновлять корень диска";
                return false;
            }

            string installWithSeparator = install.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!exe.StartsWith(installWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                error = "исполняемый файл должен находиться внутри папки установки";
                return false;
            }

            string sha = values.TryGetValue("sha256", out string? shaValue) ? shaValue.Trim() : "";
            if (sha.Length > 0 && (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)))
            {
                error = "--sha256 должен быть SHA-256 в hex";
                return false;
            }

            string version = values.TryGetValue("version", out string? versionValue) ? versionValue.Trim() : "";
            if (version.Length == 0)
            {
                error = "не указана версия";
                return false;
            }

            string result = GetFullPath(values, "result", ref error);
            string log = GetFullPath(values, "log", ref error);
            if (error.Length > 0) return false;

            options = new Options
            {
                ProcessId = pid,
                ZipPath = zip,
                Sha256 = sha.ToLowerInvariant(),
                Size = size,
                InstallDirectory = install.TrimEnd(Path.DirectorySeparatorChar),
                ExePath = exe,
                Version = version,
                ResultPath = result,
                LogPath = log
            };
            return true;
        }

        private static string GetFullPath(Dictionary<string, string> values, string key, ref string error)
        {
            if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                error = "не указан --" + key;
                return "";
            }

            try
            {
                return Path.GetFullPath(value);
            }
            catch (Exception ex)
            {
                error = $"некорректный --{key}: {ex.Message}";
                return "";
            }
        }
    }
}

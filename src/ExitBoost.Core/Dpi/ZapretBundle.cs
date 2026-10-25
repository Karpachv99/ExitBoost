using System.IO.Compression;
using System.Text.Json;
using ExitBoost.Core.Routing;

namespace ExitBoost.Core.Dpi;

/// <summary>
/// Установщик и автообновлятель движка zapret для Windows
/// (официальный бандл bol-van/zapret-win-bundle: winws.exe + WinDivert).
/// </summary>
public sealed class ZapretBundle
{
    /// <summary>Официальный бандл с Windows-бинарниками zapret.</summary>
    public const string BundleUrl = "https://github.com/bol-van/zapret-win-bundle/archive/refs/heads/master.zip";

    /// <summary>API для проверки обновлений (последний коммит бандла).</summary>
    public const string CommitApiUrl = "https://api.github.com/repos/bol-van/zapret-win-bundle/commits/master";

    /// <summary>RSS-фид коммитов — доступен без лимитов API (основной способ проверки).</summary>
    public const string CommitsAtomUrl = "https://github.com/bol-van/zapret-win-bundle/commits/master.atom";

    /// <summary>Релизы самого zapret (для отображения версии).</summary>
    public const string ReleaseApiUrl = "https://api.github.com/repos/bol-van/zapret/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <summary>Причина последней неудачной установки (для отображения в интерфейсе).</summary>
    public string? LastError { get; private set; }

    public string InstallDirectory { get; }
    public string EngineDirectory => Path.Combine(InstallDirectory, "zapret-winws");
    public string WinwsPath => Path.Combine(EngineDirectory, "winws.exe");
    public string Winws2Path => Path.Combine(EngineDirectory, "winws2.exe");
    public string HostListPath => Path.Combine(InstallDirectory, "hostlist.txt");

    /// <summary>Адреса игровых серверов для --ipset (обход только их трафика).</summary>
    public string IpSetPath => Path.Combine(InstallDirectory, "ipset.txt");
    public string StatePath => Path.Combine(InstallDirectory, "install.json");

    /// <summary>Журнал диагностики.</summary>
    public Action<string>? Log { get; set; }

    public ZapretBundle(string? installDirectory = null)
    {
        InstallDirectory = installDirectory ?? Path.Combine(AppPaths.DataDirectory, "zapret");
    }

    public bool IsInstalled => File.Exists(WinwsPath);

    /// <summary>Установленная версия (идентификатор коммита бандла).</summary>
    public string? InstalledVersion
    {
        get
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                var json = File.ReadAllText(StatePath);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("commit", out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }
    }

    public DateTime? InstalledAt
    {
        get
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                var json = File.ReadAllText(StatePath);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("installedAt", out var v)
                    ? DateTime.Parse(v.GetString() ?? "")
                    : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Проверить, есть ли обновления бандла.</summary>
    /// <param name="force">true — проверять немедленно (кнопка «Обновить»), 
    /// false — учитывать кэш и не дёргать GitHub чаще раза в 6 часов.</param>
    public async Task<ZapretUpdateInfo?> CheckUpdateAsync(bool force = false, CancellationToken ct = default)
    {
        // Кэш: фоновые проверки не должны ловить лимит GitHub API (403),
        // который наступает после 60 запросов в час.
        if (!force && LastCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(6))
            return null;

        // Основной источник — RSS-фид коммитов (без лимитов API).
        var info = await CheckAtomAsync(ct).ConfigureAwait(false);

        // Запасной — официальный API.
        if (info is null)
            info = await CheckApiAsync(ct).ConfigureAwait(false);

        if (info is null) return null;

        SaveLastCheck(info.Commit);
        info.IsNewer = IsRemoteNewer(info.Commit);
        return info;
    }

    /// <summary>Штатный RSS-фид коммитов — доступен без лимитов GitHub API.</summary>
    private async Task<ZapretUpdateInfo?> CheckAtomAsync(CancellationToken ct)
    {
        try
        {
            string xml = await Http.GetStringAsync(CommitsAtomUrl, ct).ConfigureAwait(false);
            var sha = System.Text.RegularExpressions.Regex.Match(xml, @"commit/([0-9a-f]{40})");
            if (!sha.Success) return null;

            DateTime? date = null;
            var updated = System.Text.RegularExpressions.Regex.Match(xml, @"<updated>([^<]+)</updated>");
            if (updated.Success && DateTime.TryParse(updated.Groups[1].Value, out var dt))
                date = dt;

            return new ZapretUpdateInfo
            {
                Commit = sha.Groups[1].Value,
                ShortCommit = sha.Groups[1].Value[..8],
                Date = date
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Запасной источник — GitHub REST API (ограничен лимитом запросов).</summary>
    private async Task<ZapretUpdateInfo?> CheckApiAsync(CancellationToken ct)
    {
        try
        {
            string json = await Http.GetStringAsync(CommitApiUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string? sha = doc.RootElement.TryGetProperty("sha", out var s) ? s.GetString() : null;
            string? date = doc.RootElement.TryGetProperty("commit", out var c) &&
                           c.TryGetProperty("committer", out var cm) &&
                           cm.TryGetProperty("date", out var d) ? d.GetString() : null;

            if (string.IsNullOrEmpty(sha)) return null;

            return new ZapretUpdateInfo
            {
                Commit = sha,
                ShortCommit = sha.Length >= 8 ? sha[..8] : sha,
                Date = date is null ? null : DateTime.TryParse(date, out var dt) ? dt : null
            };
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Проверка обновлений zapret отложена (GitHub: {ex.Message}). Движок установлен — это не мешает работе.");
            return null;
        }
    }

    /// <summary>Считать ли удалённую версию новее установленной.</summary>
    private bool IsRemoteNewer(string remoteCommit)
    {
        string? installed = InstalledVersion;
        if (installed is null || installed == remoteCommit) return false;

        // Если прошлую установку не удалось зафиксировать (commit=unknown),
        // не считаем её устаревшей в течение двух суток после установки —
        // иначе получается цикл «установили → сразу переустанавливаем».
        if (installed == "unknown")
            return InstalledAt is not { } at || DateTime.UtcNow - at > TimeSpan.FromDays(2);

        return true;
    }

    // ---------------------------------------------------- состояние установки

    private sealed record BundleState(string? Commit, DateTime? InstalledAt, DateTime? LastCheckUtc);

    private BundleState? LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            string? commit = doc.RootElement.TryGetProperty("commit", out var c) ? c.GetString() : null;
            DateTime? installedAt = doc.RootElement.TryGetProperty("installedAt", out var ia) &&
                                   DateTime.TryParse(ia.GetString(), out var t1) ? t1 : null;
            DateTime? lastCheck = doc.RootElement.TryGetProperty("lastCheckUtc", out var lc) &&
                                  DateTime.TryParse(lc.GetString(), out var t2) ? t2 : null;
            return new BundleState(commit, installedAt, lastCheck);
        }
        catch { return null; }
    }

    private void SaveLastCheck(string commit)
    {
        try
        {
            var state = LoadState();
            string installedAt = (state?.InstalledAt ?? DateTime.UtcNow).ToString("O");
            Directory.CreateDirectory(InstallDirectory);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new
            {
                commit = state?.Commit ?? commit,
                installedAt,
                lastCheckUtc = DateTime.UtcNow.ToString("O")
            }));
            _lastCheckUtc = DateTime.UtcNow;
        }
        catch { }
    }

    private DateTime? _lastCheckUtc;

    private DateTime? LastCheckUtc => _lastCheckUtc ??= LoadState()?.LastCheckUtc;

    /// <summary>Последний релиз zapret (для отображения версии).</summary>
    public async Task<string?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await Http.GetStringAsync(ReleaseApiUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Скачать и установить (или обновить) движок zapret.</summary>
    public async Task<bool> InstallAsync(CancellationToken ct = default)
    {
        // Фоновая проверка обновлений и кнопка «Обновить» могут стартовать
        // одновременно — не даём двум установкам конфликтовать.
        if (!await InstallLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            Log?.Invoke("Установка zapret уже выполняется — пропуск");
            return false;
        }

        LastError = null;
        try
        {
            Log?.Invoke("Загрузка zapret-win-bundle...");
            byte[] data = await Http.GetByteArrayAsync(BundleUrl, ct).ConfigureAwait(false);
            Log?.Invoke($"Загружено {data.Length / 1024} КБ, распаковка...");

            string temp = Path.Combine(Path.GetTempPath(), $"zapret_{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith('/')) continue;
                        string target = Path.GetFullPath(Path.Combine(temp, entry.FullName));
                        if (!target.StartsWith(Path.GetFullPath(temp), StringComparison.OrdinalIgnoreCase))
                            continue; // защита от path traversal
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, true);
                    }
                }

                string root = Directory.GetDirectories(temp).FirstOrDefault() ?? temp;
                string source = Path.Combine(root, "zapret-winws");
                if (!Directory.Exists(source))
                    source = Directory.GetDirectories(root, "zapret-winws", SearchOption.AllDirectories).FirstOrDefault() ?? "";

                if (string.IsNullOrEmpty(source) || !File.Exists(Path.Combine(source, "winws.exe")))
                    throw new InvalidOperationException("в архиве нет winws.exe");

                Directory.CreateDirectory(InstallDirectory);

                // Драйвер WinDivert выгружается из ядра несколько секунд после
                // смерти winws.exe — поэтому копирование движка делаем с попытками.
                string engineError = "";
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    StopOrphanWinws();
                    await StopWinDivertServicesAsync(ct).ConfigureAwait(false);
                    CopyDirectory(source, EngineDirectory, out engineError);
                    if (string.IsNullOrEmpty(engineError)) break;
                    if (attempt < 4)
                        Log?.Invoke($"Файлы движка заняты (WinDivert выгружается), повтор через 2 с (попытка {attempt + 1}/4)");
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                if (!string.IsNullOrEmpty(engineError))
                    throw new IOException($"движок: {engineError}");

                // Дополнительно забираем пресеты и наборы стратегий для каталога.
                string filesDir = Path.Combine(source, "files");
                if (Directory.Exists(filesDir))
                    CopyDirectory(filesDir, Path.Combine(InstallDirectory, "files"), out _);

                string filtersDir = Path.Combine(source, "windivert.filter");
                if (Directory.Exists(filtersDir))
                    CopyDirectory(filtersDir, Path.Combine(InstallDirectory, "windivert.filter"), out _);

                string standardDir = Path.Combine(root, "blockcheck", "zapret2", "blockcheck2.d", "standard");
                if (Directory.Exists(standardDir))
                    CopyDirectory(standardDir, Path.Combine(InstallDirectory, "standard"), out _);

                // Фейковые пакеты для пресетов Flowseal (в бандле их нет) —
                // скачиваем из репозитория Flowseal/zapret-discord-youtube.
                await DownloadFlowsealFakesAsync(ct).ConfigureAwait(false);

                var info = await CheckAtomAsync(ct).ConfigureAwait(false)
                           ?? await CheckApiAsync(ct).ConfigureAwait(false);
                Directory.CreateDirectory(InstallDirectory);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(new
                {
                    commit = info?.Commit ?? "unknown",
                    installedAt = DateTime.UtcNow.ToString("O"),
                    lastCheckUtc = DateTime.UtcNow.ToString("O")
                }));

                Log?.Invoke($"zapret установлен: {EngineDirectory}");
                return true;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log?.Invoke($"Ошибка установки zapret: {ex.Message}");
            return false;
        }
        finally
        {
            InstallLock.Release();
        }
    }

    /// <summary>Путь к корню бандла с наборами стратегий (если сохранён при установке).</summary>
    public string CatalogRoot => InstallDirectory;

    /// <summary>Папка с фейковыми .bin-пакетами Flowseal (подстановки %FAKES% в стратегиях).</summary>
    public string FakesDirectory => Path.Combine(InstallDirectory, "fakes");

    /// <summary>Фейковые файлы репозитория Flowseal, нужные пресетам каталога.</summary>
    public static readonly string[] FlowsealFakeFiles =
    [
        "tls_clienthello_www_google_com.bin",
        "tls_clienthello_www_sferum_ru.bin",
        "tls_clienthello_4pda_to.bin",
        "tls_clienthello_sochi_park.bin",
        "stun.bin",
        "stun2.bin"
    ];

    private const string FlowsealBinUrl =
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/bin/";

    /// <summary>
    /// Скачивает фейковые .bin-пакеты Flowseal в fakes\. Отдельные сбои не валят
    /// установку — просто пишем в журнал (пресет без своего файла не сработает,
    /// но остальные продолжат работать).
    /// </summary>
    private async Task DownloadFlowsealFakesAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(FakesDirectory);
            int ok = 0;
            foreach (var file in FlowsealFakeFiles)
            {
                string target = Path.Combine(FakesDirectory, file);
                try
                {
                    if (File.Exists(target) && new FileInfo(target).Length > 0)
                    {
                        ok++;
                        continue;
                    }
                    byte[] data = await Http.GetByteArrayAsync(FlowsealBinUrl + file, ct).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(target, data, ct).ConfigureAwait(false);
                    ok++;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Не удалось скачать {file}: {ex.Message}");
                }
            }
            Log?.Invoke($"Фейковые пакеты Flowseal: {ok}/{FlowsealFakeFiles.Length} в {FakesDirectory}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Папка fakes недоступна: {ex.Message}");
        }
    }

    /// <summary>Копирует каталог с перезаписью; один занятый файл не прерывает всю операцию.</summary>
    private static void CopyDirectory(string source, string target, out string error)
    {
        error = "";
        Directory.CreateDirectory(target);
        var failures = new List<string>();

        foreach (var file in Directory.GetFiles(source))
        {
            string dest = Path.Combine(target, Path.GetFileName(file));
            try
            {
                File.Copy(file, dest, overwrite: true);
            }
            catch (IOException ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), out var sub);
            if (!string.IsNullOrEmpty(sub)) failures.Add(sub);
        }

        if (failures.Count > 0)
            error = string.Join("; ", failures.Take(3));
    }

    /// <summary>Остановить winws.exe, запущенный именно из нашей папки (не трогаем чужие).</summary>
    public void StopOrphanWinws()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("winws"))
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path is not null && string.Equals(
                            Path.GetFullPath(path), Path.GetFullPath(WinwsPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        // Важно: дождаться выхода. Файл WinDivert64.sys остаётся
                        // занятым ещё пару секунд после смерти процесса,
                        // пока драйвер выгружается из ядра.
                        p.WaitForExit(5000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    /// <summary>
    /// Остановить службы-драйверы WinDivert, чей ImagePath указывает на наш файл.
    /// Пока такой драйвер загружен в ядре, файл WinDivert64.sys заблокирован
    /// даже если все процессы winws уже остановлены.
    /// </summary>
    private static async Task StopWinDivertServicesAsync(CancellationToken ct)
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-ChildItem HKLM:\SYSTEM\CurrentControlSet\Services | ForEach-Object {
                $img = (Get-ItemProperty $_.PSPath).ImagePath
                if ($img -and $img -like '*ExitBoost*WinDivert64.sys') {
                    sc.exe stop $_.PSChildName | Out-Null
                }
            }
            Start-Sleep -Milliseconds 2000
            """;
        await PowerShellRunner.RunAsync(script, 30_000, ct).ConfigureAwait(false);
    }
}

/// <summary>Информация об обновлении бандла.</summary>
public sealed class ZapretUpdateInfo
{
    public string Commit { get; init; } = "";
    public string ShortCommit { get; init; } = "";
    public DateTime? Date { get; init; }
    public bool IsNewer { get; set; }
}

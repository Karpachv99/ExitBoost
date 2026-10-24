using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using ExitBoost.Core.Routing;

namespace ExitBoost.Core.Vpn;

/// <summary>
/// Запуск ядра sing-box: скачивание релиза, сборка конфига, мягкий останов.
/// Ядро само управляетм TUN-адаптером и маршрутизацией (auto_route) и снимает
/// свои маршруты при останове — WindowsRouteManager не трогаем вообще.
/// </summary>
public sealed class SingBoxRunner
{
    public Action<string>? Log;

    public string BaseDirectory { get; }
    public string CoreDirectory => Path.Combine(BaseDirectory, "core");
    public string BinaryPath => Path.Combine(CoreDirectory, "sing-box.exe");
    public string ConfigPath { get; }
    public string LogPath { get; }

    private Process? _proc;
    private readonly object _lock = new();
    private HttpClient Http { get; } = CreateHttp();

    public SingBoxRunner(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? Path.Combine(AppPaths.DataDirectory, "vpn");
        ConfigPath = Path.Combine(BaseDirectory, "config.json");
        LogPath = Path.Combine(BaseDirectory, "singbox.log");
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }
        };
        var http = new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(40);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExitBoost/1.0");
        return http;
    }

    /// <summary>Ядро работает (в т.ч. независимо от этого процесса — «сироты»).</summary>
    public bool IsCoreRunning => FindOurCoreProcesses().Length > 0;

    public bool IsInstalled
    {
        get
        {
            try
            {
                return File.Exists(BinaryPath) && new FileInfo(BinaryPath).Length > 1_000_000;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    /// <summary>Ключ, под которым работает обход прямо сейчас (для статуса).</summary>
    public VpnKey? ActiveKey { get; private set; }

    // -------------------------------------------------------------- установка

    /// <summary>Скачивает sing-box (последний релиз GitHub) и кладёт sing-box.exe в core\.</summary>
    public async Task<bool> InstallAsync(CancellationToken ct = default)
    {
        try
        {
            Log?.Invoke("Поиск релиза sing-box...");
            var release = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            string url = release.url, tag = release.tag;
            if (tag.Length == 0) throw new InvalidOperationException("не найден актуальный релиз sing-box");

            Log?.Invoke($"скачивание sing-box {tag} ({url[(url.LastIndexOf('/') + 1)..]})…");
            byte[] data = await DownloadArchiveAsync(url, ct).ConfigureAwait(false);
            Log?.Invoke($"загружено {data.Length / 1024} КБ, распаковка…");

            Directory.CreateDirectory(CoreDirectory);
            using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            bool found = false;
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith("sing-box.exe", StringComparison.OrdinalIgnoreCase)) continue;
                entry.ExtractToFile(BinaryPath, true);
                found = true;
                break;
            }
            if (!found) throw new InvalidOperationException("в архиве нет sing-box.exe");

            Log?.Invoke($"sing-box установлен: {BinaryPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"не удалось установить sing-box: {ex.Message}");
            return false;
        }
    }

    private async Task<byte[]> DownloadArchiveAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Актуальный релиз через GitHub API; при сбое — известная свежая версия.</summary>
    private static async Task<(string url, string tag)> GetLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var http = CreateHttp();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ExitBoost/1.0");

            using var resp = await http.GetAsync(
                "https://api.github.com/repos/SagerNet/sing-box/releases/latest", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Equals($"sing-box-{tag.TrimStart('v')}-windows-amd64.zip", StringComparison.OrdinalIgnoreCase))
                        return (asset.GetProperty("browser_download_url").GetString() ?? "", tag);
                }
            }
        }
        catch { }
        return ("https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip",
                "v1.14.4");
    }

    // ------------------------------------------------------------ подключение

    public sealed record ConnectResult(bool Ok, string Error = "");

    /// <summary>
    /// Подключение по ключу: собирает конфиг, снимает оставшийся процесс и запускает ядро.
    /// </summary>
    public async Task<ConnectResult> ConnectAsync(VpnKey key, VpnRoutingConfig routing, CancellationToken ct = default)
    {
        try
        {
            string? problem = Validate(key);
            if (problem is not null) return new ConnectResult(false, problem);

            if (!WindowsRouteManager.IsElevated())
                return new ConnectResult(false, "нужны права администратора (VPN-адаптер) — запустите программу от администратора");

            if (!IsInstalled)
            {
                Log?.Invoke("ядро sing-box не установлено — скачиваю…");
                if (!await InstallAsync(ct).ConfigureAwait(false))
                    return new ConnectResult(false, "ядро не установлено — проверьте интернет и повторите");
            }

            StopLeftoverCore();

            string json = SingBoxConfigBuilder.Build(key, routing, LogPath);
            Directory.CreateDirectory(BaseDirectory);
            await File.WriteAllTextAsync(ConfigPath, json, ct).ConfigureAwait(false);

            // ядро пишёт журнал в LogPath само — не перехватываем его пайпы,
            // чтобы консоль (CLI/PowerShell) не ждала завершения ядра.
            // Ядро — самостоятельный процесс в отдельном скрытом консольном окне:
            // когда подключающий процесс (CLI) завершится, ядро продолжит работать.
            var info = new ProcessStartInfo
            {
                FileName = BinaryPath,
                Arguments = $"run -c \"{ConfigPath}\"",
                WorkingDirectory = CoreDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = new Process { StartInfo = info, EnableRaisingEvents = true };
            proc.Exited += (_, _) => { lock (_lock) if (_proc == proc) { CleanRef(); Log?.Invoke("ядро sing-box завершило работу"); } };

            proc.Start();
            lock (_lock) _proc = proc;

            // Ждём, пока ядро поднимет TUN и службу управления: на полном реестре
            // доменов разбор конфига занимает несколько секунд, фиксированные 3 с
            // могли вернуть «успех» уже упавшему ядру.
            bool ready = await WaitForCoreReadyAsync(proc, ct).ConfigureAwait(false);
            if (!ready)
            {
                lock (_lock) if (_proc == proc) _proc = null;
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                string tail = ReadLogTail();
                string reason = tail.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                    || tail.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                    ? "сервер ключа отклонил соединение — локация недоступна или сервер выключен"
                    : $"ядро не смогло запуститься ({key.Protocol} {key.Address}:{key.Port}).{tail}";
                return new ConnectResult(false, reason);
            }

            ActiveKey = key;
            Log?.Invoke($"VPN по ключу работает: \"{key.Name}\" ({key.Protocol} {key.Address}:{key.Port}\u00a0режим {(routing.Mode == "full" ? "весь трафик" : "только выбранные")})");
            return new ConnectResult(true);
        }
        catch (OperationCanceledException)
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            return new ConnectResult(false, ex.Message);
        }
    }

    private void CleanRef()
    {
        _proc = null;
        ActiveKey = null;
    }

    /// <summary>Ждём ответа службы управления sing-box — значит, ядро реально запустилось.</summary>
    private static async Task<bool> WaitForCoreReadyAsync(Process proc, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(700) };
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited) return false;
            try
            {
                using var resp = await http.GetAsync(
                    $"http://127.0.0.1:{SingBoxConfigBuilder.ControlPort}/version", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // ядро ещё стартует
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return !proc.HasExited;
    }

    private static string? Validate(VpnKey key)
    {
        if (key.Address.Length == 0) return "в ключе нет адреса сервера";
        if (key.Port is < 1 or > 65535) return "в ключе неверный порт";
        if (key.UserId.Length == 0) return "в ключе нет uuid или пароля";
        if (key.UnsupportedReason is not null) return key.UnsupportedReason;
        if (key.Security == "reality" && key.PublicKey.Length == 0)
            return "в Reality-ключе нет публичного ключа (pbk) — без него ядро не подключится";
        if (key.Transport == "quic" && key.Security is not ("tls" or "reality"))
            return "транспорт quic работает только поверх TLS — в ключе нет security=tls";
        return null;
    }

    // ---------------------------------------------------------- отключение

    /// <summary>
    /// Мягкая остановка через службу управления sing-box (ядро само снимает маршруты);
    /// если не сработало — завершаем процесс.
    /// </summary>
    public async Task DisconnectAsync()
    {
        Process? proc;
        bool orphan;
        lock (_lock)
        {
            proc = _proc;
            _proc = null;
            ActiveKey = null;
        }
        orphan = proc is null && FindOurCoreProcesses().Length > 0;
        if (proc is null && !orphan) return;

        Log?.Invoke("остановка VPN по ключу…");

        bool softStop = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var body = new StringContent("", System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"http://127.0.0.1:{SingBoxConfigBuilder.ControlPort}/shutdown", body)
                .ConfigureAwait(false);
            softStop = response.IsSuccessStatusCode;
        }
        catch { /* тихо уходим на жёсткий останов */ }

        if (proc is not null)
        {
            try
            {
                // /shutdown поддерживают не все версии sing-box (в 1.14 — 404).
                // Если мягкий останов недоступен, не ждём 5 секунд впустую.
                if (!proc.HasExited && !proc.WaitForExit(softStop ? 5000 : 0))
                    proc.Kill(entireProcessTree: true);
                proc.Dispose();
            }
            catch { }
        }

        StopLeftoverCore();
        Log?.Invoke("VPN по ключу отключён");
    }

    /// <summary>Снимает sing-box-процессы, оставшиеся от прежнего запуска.</summary>
    public void StopLeftoverCore()
    {
        foreach (var orphan in FindOurCoreProcesses())
        {
            try
            {
                orphan.Kill(entireProcessTree: true);
                Log?.Invoke($"снят оставшийся процесс sing-box (PID {orphan.Id})");
            }
            catch { }
            finally
            {
                try { orphan.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Наши процессы ядра (наша папка core или без пути — честно «свои»).</summary>
    private Process[] FindOurCoreProcesses()
    {
        try
        {
            return Process.GetProcessesByName("sing-box")
                .Where(p =>
                {
                    try
                    {
                        string path = "";
                        try { path = p.MainModule?.FileName ?? ""; } catch { }
                        return path.Length == 0 || path.StartsWith(CoreDirectory, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    // --------------------------------------------------------------- журнал

    private string ReadLogTail()
    {
        try
        {
            string[] lines = File.ReadAllLines(LogPath);
            string[] tail = lines[^Math.Min(lines.Length, 8)..];
            string text = string.Join("; ", tail.Where(l =>
                l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("FATAL", StringComparison.OrdinalIgnoreCase)));
            return text.Length > 0 ? " " + text[..Math.Min(300, text.Length)] : "";
        }
        catch
        {
            return "";
        }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

    /// <summary>Скачанное обновление ядра, которое ещё нельзя применить (ядро запущено).</summary>
    private string PendingBinaryPath => BinaryPath + ".new";
    public string ConfigPath { get; }
    public string LogPath { get; }

    private Process? _proc;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpClient Http { get; } = CreateHttp();

    /// <summary>Имя TUN-адаптера sing-box (см. конфиг: inbound tun, tag tun0).</summary>
    private const string TunInterfaceName = "tun0";

    /// <summary>Адрес TUN-адаптера из конфига — надёжнее имени, если оно изменится.</summary>
    private const string TunAddress = "172.19.0.1";

    /// <summary>Сколько раз пробуем поднять ядро за одно подключение.</summary>
    private const int StartAttempts = 3;

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
    public bool IsCoreRunning
    {
        get
        {
            // Статус опрашивается раз в секунду, поэтому найденные процессы
            // обязательно освобождаем: иначе за сутки накапливаются тысячи handles.
            Process[] processes = FindOurCoreProcesses();
            try { return processes.Length > 0; }
            finally { DisposeAll(processes); }
        }
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try { process.Dispose(); } catch { }
        }
    }

    public bool IsInstalled
    {
        get
        {
            try
            {
                return IsCoreFile(BinaryPath) || IsCoreFile(PendingBinaryPath);
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool IsCoreFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 1_000_000;

    /// <summary>Применить скачанное обновление, если ядро не запущено.</summary>
    private void ApplyPendingCoreUpdate()
    {
        try
        {
            if (!File.Exists(PendingBinaryPath) || IsCoreRunning) return;
            File.Move(PendingBinaryPath, BinaryPath, true);
            Log?.Invoke("ядро sing-box обновлено");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"не удалось применить обновление ядра: {ex.Message}");
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _proc is not null && !SafeHasExited(_proc);
        }
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
                // Распаковываем рядом: работающий sing-box.exe нельзя перезаписать.
                entry.ExtractToFile(PendingBinaryPath, true);
                found = true;
                break;
            }
            if (!found) throw new InvalidOperationException("в архиве нет sing-box.exe");

            // Ядро запущено — обновление применится сразу после отключения VPN.
            if (IsCoreRunning)
            {
                Log?.Invoke("ядро sing-box запущено — новая версия применится после отключения VPN");
                return true;
            }

            ApplyPendingCoreUpdate();
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
    /// Подключение и отключение сериализованы: раньше одновременные клики могли
    /// отправить мягкий останов новому ядру и уронить уже запущенное.
    /// </summary>
    public async Task<ConnectResult> ConnectAsync(VpnKey key, VpnRoutingConfig routing, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ConnectCoreAsync(key, routing, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ConnectResult> ConnectCoreAsync(VpnKey key, VpnRoutingConfig routing, CancellationToken ct)
    {
        try
        {
            string? problem = Validate(key);
            if (problem is not null) return new ConnectResult(false, problem);

            if (!WindowsRouteManager.IsElevated())
                return new ConnectResult(false, "нужны права администратора (VPN-адаптер) — запустите программу от администратора");

            // Снимаем прежнее ядро и ждём, пока освободятся TUN и порт управления.
            // Иначе новое ядро открывает tun0 поверх ещё не удалённого и падает.
            await StopLeftoverCoreAsync(ct).ConfigureAwait(false);

            if (!IsCoreFile(BinaryPath))
            {
                Log?.Invoke("ядро sing-box не установлено — скачиваю…");
                if (!await InstallAsync(ct).ConfigureAwait(false))
                    return new ConnectResult(false, "ядро не установлено — проверьте интернет и повторите");
                ApplyPendingCoreUpdate();
                if (!IsCoreFile(BinaryPath))
                    return new ConnectResult(false, "не удалось установить ядро sing-box");
            }

            string json = SingBoxConfigBuilder.Build(key, routing, LogPath);
            Directory.CreateDirectory(BaseDirectory);
            await File.WriteAllTextAsync(ConfigPath, json, ct).ConfigureAwait(false);

            string lastReason = "";
            for (int attempt = 1; attempt <= StartAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var proc = CreateCoreProcess();
                lock (_lock) _proc = proc;
                proc.Exited += OnCoreExited;
                try
                {
                    proc.Start();
                }
                catch (Exception ex)
                {
                    lock (_lock) if (_proc == proc) _proc = null;
                    proc.Dispose();
                    return new ConnectResult(false, $"не удалось запустить sing-box: {ex.Message}");
                }

                TimeSpan startupTimeout = StartTimeout();
                Log?.Invoke($"ожидание готовности sing-box и TUN (до {startupTimeout.TotalSeconds:0} с, попытка {attempt}/{StartAttempts})");
                bool ready = await WaitForCoreReadyAsync(proc, startupTimeout, ct).ConfigureAwait(false);
                if (ready)
                {
                    ActiveKey = key;
                    Log?.Invoke($"VPN по ключу работает: \"{key.Name}\" ({key.Protocol} {key.Address}:{key.Port}\u00a0режим {(routing.Mode == "full" ? "весь трафик" : "только выбранные")})");
                    return new ConnectResult(true);
                }

                // Причина из журнала ядра, затем чистим процесс и TUN перед повтором.
                string tail = ReadLogTail();
                lastReason = tail.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                    || tail.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                    ? "сервер ключа отклонил соединение — локация недоступна или сервер выключен"
                    : $"ядро не смогло запуститься ({key.Protocol} {key.Address}:{key.Port}).{tail}";

                lock (_lock) if (_proc == proc) _proc = null;
                await KillCoreAsync(proc).ConfigureAwait(false);

                if (attempt < StartAttempts)
                {
                    Log?.Invoke($"ядро не поднялось с попытки {attempt} — повторяю");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }

            return new ConnectResult(false, lastReason);
        }
        catch (OperationCanceledException)
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            return new ConnectResult(false, ex.Message);
        }
    }

    private Process CreateCoreProcess()
    {
        TrimLogIfHuge();
        // ядро пишет журнал в LogPath само — не перехватываем его пайпы,
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
        return new Process { StartInfo = info, EnableRaisingEvents = true };
    }

    private void OnCoreExited(object? sender, EventArgs e)
    {
        if (sender is not Process proc) return;

        bool notify;
        lock (_lock)
        {
            notify = _proc == proc;
            if (notify)
            {
                _proc = null;
                ActiveKey = null;
            }
        }
        if (notify) Log?.Invoke("ядро sing-box завершило работу");
    }

    /// <summary>
    /// Ждём, пока ядро поднимет TUN и службу управления. Таймаут — это неудача:
    /// раньше он возвращал «успех» живому, но ещё не готовому ядру.
    /// </summary>
    private async Task<bool> WaitForCoreReadyAsync(Process proc, TimeSpan startupTimeout, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(700) };
        var deadline = DateTime.UtcNow + startupTimeout;
        bool apiReady = false;

        while (DateTime.UtcNow < deadline)
        {
            if (SafeHasExited(proc)) return false;

            if (!apiReady)
            {
                try
                {
                    using var resp = await http.GetAsync(
                        $"http://127.0.0.1:{SingBoxConfigBuilder.ControlPort}/version", ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) apiReady = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // ядро ещё стартует
                }
            }

            // Готовность — это и служба управления, и поднятый TUN: служба может
            // ответить раньше, чем ядро откроет адаптер, а без TUN трафик не идёт.
            if (apiReady && TunInterfaceUp())
            {
                // Короткая стабилизация: ядро не должно упасть сразу после старта.
                await Task.Delay(400, ct).ConfigureAwait(false);
                return !SafeHasExited(proc);
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        Log?.Invoke($"sing-box не дождался готовности: control API={(apiReady ? "доступен" : "не отвечает")}, TUN={(TunInterfaceUp() ? "поднят" : "не поднят")}");
        return false;
    }

    /// <summary>Таймаут готовности зависит от размера конфига: полный реестр РКН большой.</summary>
    private TimeSpan StartTimeout()
    {
        long size = 0;
        try { size = new FileInfo(ConfigPath).Length; } catch { }

        // На Windows создание Wintun и установка маршрутов могут занимать
        // десятки секунд, особенно сразу после завершения предыдущего ядра.
        // Короткий таймаут убивал живой sing-box, из-за чего подключение
        // стабильно удавалось лишь со следующего запуска.
        double seconds = 60;
        if (size > 20 * 1024 * 1024) seconds = 180;
        else if (size > 5 * 1024 * 1024) seconds = 120;
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool SafeHasExited(Process? proc)
    {
        try { return proc is null || proc.HasExited; }
        catch { return true; }
    }

    /// <summary>Убить ядро, дождаться выхода и освобождения TUN.</summary>
    private static async Task KillCoreAsync(Process proc)
    {
        try { if (!SafeHasExited(proc)) proc.Kill(entireProcessTree: true); } catch { }
        await WaitForExitAsync(proc, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        try { proc.Dispose(); } catch { }
        await WaitForTunGoneAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    private static async Task WaitForExitAsync(Process proc, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (SafeHasExited(proc)) return;
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static bool TunInterfaceUp() =>
        NetworkInterfaces().Any(n =>
            n.OperationalStatus == OperationalStatus.Up && (IsOurTun(n) || HasOurTunAddress(n)));

    private static bool HasOurTunAddress(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().UnicastAddresses
                .Any(a => a.Address.ToString() == TunAddress);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<NetworkInterface> NetworkInterfaces()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return []; }
    }

    private static bool IsOurTun(NetworkInterface n) =>
        n.Name.Equals(TunInterfaceName, StringComparison.OrdinalIgnoreCase) ||
        n.Description.Contains("sing-tun", StringComparison.OrdinalIgnoreCase);

    private static async Task WaitForTunGoneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && TunInterfaceUp())
            await Task.Delay(150).ConfigureAwait(false);
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
    /// если не сработало — завершаем процесс. Отключение сериализовано с подключением:
    /// иначе «мягкий останов» мог уйти только что запущенному ядру.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _proc;
            _proc = null;
            ActiveKey = null;
        }

        bool orphan = proc is null && IsCoreRunning;
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
            // /shutdown поддерживают не все версии sing-box (в 1.14 — 404).
            // Если мягкий останов недоступен, не ждём 5 секунд впустую.
            if (softStop) await WaitForExitAsync(proc, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await KillCoreAsync(proc).ConfigureAwait(false);
        }

        await StopLeftoverCoreAsync(CancellationToken.None).ConfigureAwait(false);
        Log?.Invoke("VPN по ключу отключён");
    }

    /// <summary>
    /// Снимает sing-box-процессы, оставшиеся от прежнего запуска, и ждёт, пока
    /// освободятся TUN-адаптер и порт управления. Без этого новое ядро открывает
    /// tun0 поверх ещё не удалённого и завершается через пару секунд после старта.
    /// </summary>
    private async Task StopLeftoverCoreAsync(CancellationToken ct)
    {
        var orphans = FindOurCoreProcesses();
        foreach (var orphan in orphans)
        {
            try
            {
                if (!SafeHasExited(orphan))
                {
                    orphan.Kill(entireProcessTree: true);
                    Log?.Invoke($"снят оставшийся процесс sing-box (PID {orphan.Id})");
                }
            }
            catch { }
        }

        foreach (var orphan in orphans)
        {
            await WaitForExitAsync(orphan, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            try { orphan.Dispose(); } catch { }
        }

        if (orphans.Length > 0 || TunInterfaceUp())
        {
            await WaitForTunGoneAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            // Ядро снимает адаптер не мгновенно после выхода процесса.
            try { await Task.Delay(300, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        ApplyPendingCoreUpdate();
    }

    /// <summary>
    /// Наши процессы ядра (наша папка core или без пути — честно «свои»).
    /// Возвращённые процессы принадлежат вызывающему коду: их нужно освободить.
    /// </summary>
    private Process[] FindOurCoreProcesses()
    {
        Process[] all;
        try { all = Process.GetProcessesByName("sing-box"); }
        catch { return []; }

        var ours = new List<Process>(all.Length);
        foreach (var process in all)
        {
            bool mine;
            try
            {
                string path = "";
                try { path = process.MainModule?.FileName ?? ""; } catch { }
                mine = path.Length == 0
                    || path.Equals(BinaryPath, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(CoreDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                mine = false;
            }

            if (mine) ours.Add(process);
            else DisposeAll([process]);
        }
        return ours.ToArray();
    }

    // --------------------------------------------------------------- журнал

    /// <summary>Не даём журналу ядра расти бесконечно: старше 8 МБ — начинаем заново.</summary>
    private void TrimLogIfHuge()
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 8 * 1024 * 1024)
                File.Delete(LogPath);
        }
        catch { }
    }

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

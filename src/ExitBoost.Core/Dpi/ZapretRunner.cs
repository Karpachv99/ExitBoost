using System.Diagnostics;
using System.Text;
using ExitBoost.Core.Tunnel;

namespace ExitBoost.Core.Dpi;

/// <summary>
/// Запускает и останавливает движок zapret (winws.exe) с выбранной стратегией.
/// </summary>
public sealed class ZapretRunner : IDisposable
{
    private readonly ZapretBundle _bundle;
    private Process? _process;

    public ZapretRunner(ZapretBundle bundle) => _bundle = bundle;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Идентификатор применяемой стратегии.</summary>
    public string? CurrentStrategyId { get; private set; }

    /// <summary>Последний вывод winws (для диагностики).</summary>
    public string LastOutput { get; private set; } = "";

    /// <summary>Журнал работы движка.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Запустить выбранную стратегию для указанных целей.</summary>
    public async Task<bool> StartAsync(DpiStrategy strategy, DpiTarget target, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);

        if (!_bundle.IsInstalled)
        {
            Log?.Invoke("Движок zapret не установлен — выполните «Обновить zapret».");
            return false;
        }

        if (!target.IsValid)
        {
            Log?.Invoke("Не указаны домены для обхода.");
            return false;
        }

        string? invalid = strategy.Validate(target);
        if (invalid is not null)
        {
            Log?.Invoke(invalid);
            return false;
        }

        string? ipSetPath = null;
        try
        {
            Directory.CreateDirectory(_bundle.InstallDirectory);
            await File.WriteAllLinesAsync(_bundle.HostListPath, target.HostList, ct).ConfigureAwait(false);

            // Адреса игровых серверов — отдельным списком для --ipset:
            // обход применяется только к ним, остальной трафик не затрагивается.
            var gameIps = target.GameIpList;
            if (gameIps.Count > 0)
            {
                await File.WriteAllLinesAsync(_bundle.IpSetPath, gameIps, ct).ConfigureAwait(false);
                ipSetPath = _bundle.IpSetPath;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось записать список доменов: {ex.Message}");
            return false;
        }

        // Остатки прошлых запусков (в т.ч. из CLI) мешают старту:
        // winws отказывается работать — «A copy of winws is already running».
        for (int attempt = 1; ; attempt++)
        {
            _bundle.StopOrphanWinws();

            string args = BuildArguments(strategy, target, _bundle.HostListPath, _bundle.EngineDirectory, ipSetPath);
            Log?.Invoke($"Запуск zapret: {args}"); LastOutput = "";

            // Движок запускаем через отдельную обёртку: он получает своё скрытое окно
            // и пишет вывод в файл. Иначе winws наследует консоль вызывающей программы
            // (CLI) и держит её открытой, а после её выхода — теряет вывод.
            string logPath = Path.Combine(_bundle.InstallDirectory, "zapret.log");
            string wrapperPath = Path.Combine(_bundle.InstallDirectory, "run-zapret.cmd");

            try
            {
                File.WriteAllText(logPath, "");
                _engineLogPath = logPath;
                _engineLogPosition = 0;
                File.WriteAllText(wrapperPath,
                    "@echo off\r\n\"" + _bundle.WinwsPath + "\" " + args + " > \"" + logPath + "\" 2>&1\r\n");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Не удалось подготовить запуск zapret: {ex.Message}");
                _process = null;
                return false;
            }

            try
            {
                _process = ProcessRunner.StartDetached("cmd.exe", $"/c \"{wrapperPath}\"", _bundle.EngineDirectory);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Не удалось запустить winws.exe: {ex.Message}");
                _process = null;
                return false;
            }

            // Даём движку поднять перехват. Полный реестр РКН (~1,7 млн строк)
            // читается заметно дольше обычного списка — иначе первые замеры
            // уходят в ещё не готовый winws.
            var proc = _process;
            await Task.Delay(StartupDelayMs(target), ct).ConfigureAwait(false);

            if (proc is null) return false;

            if (!proc.HasExited)
            {
                CurrentStrategyId = strategy.Id;
                Log?.Invoke($"zapret запущен, стратегия «{strategy.Name}»");
                return true;
            }

            string output = ReadEngineLog(logPath).Trim();
            LastOutput = output;

            // Другой наш экземпляр winws держит тот же фильтр — убираем и пробуем ещё раз.
            if (output.Contains("already running", StringComparison.OrdinalIgnoreCase) && attempt == 1)
            {
                Log?.Invoke("Другой экземпляр zapret уже работает с теми же фильтрами — останавливаю его и пробую снова");
                proc.Dispose();
                _process = null;
                _bundle.StopOrphanWinws();
                await Task.Delay(1500, ct).ConfigureAwait(false);
                continue;
            }

            // Явная ошибка стратегии: winws отвергает её синтаксис (v72.12).
            if (output.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Invalid argument", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke($"Стратегия «{strategy.Name}» не поддерживается этой версией zapret: {output.Split('\n')[^1].Trim()}");
            }
            else
            {
                Log?.Invoke($"winws.exe завершился. Вывод: {output}");
            }

            _process = null;
            return false;
        }
    }

    /// <summary>Полная командная строка winws для стратегии и цели.</summary>
    /// <remarks>
    /// В аргументах стратегии доступны подстановки:
    /// %HOSTLIST% — списки доменов (свои + готовый список), %ENGINE% — папка движка
    /// (для фейковых .bin), %FAKES% — папка fakes рядом с движком (фейковые пакеты Flowseal).
    /// Для игр: %GAMETCPPORTS% — TCP-порты игры, %GAMEUDPPORTS% — UDP-порты,
    /// %IPSET% — файл с адресами серверов (если адреса известны).
    /// </remarks>
    public static string BuildArguments(
        DpiStrategy strategy,
        DpiTarget target,
        string hostListPath,
        string? engineDirectory = null,
        string? ipSetPath = null)
    {
        // Порты перехвата: 80/443 нужны сайтам, порты игры — её профилям.
        // --wf-tcp/--wf-udp глобальные, поэтому собираем их из стратегии
        // и цели в один аргумент каждый: повторный --wf-udp перезаписал бы первый.
        string tcpPorts = DpiPorts.Merge("80,443", target.TcpPorts);
        string udpPorts = target.UdpPorts;

        var profileArgs = new List<string>();
        foreach (var arg in strategy.Args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            if (arg.StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase))
            {
                tcpPorts = DpiPorts.Merge(tcpPorts, arg["--wf-tcp=".Length..]);
                continue;
            }
            if (arg.StartsWith("--wf-udp=", StringComparison.OrdinalIgnoreCase))
            {
                udpPorts = DpiPorts.Merge(udpPorts, arg["--wf-udp=".Length..]);
                continue;
            }

            profileArgs.Add(arg);
        }

        var parts = new List<string> { $"--wf-tcp={tcpPorts}" };
        if (udpPorts.Length > 0) parts.Add($"--wf-udp={udpPorts}");

        // Сайтовый профиль (443 + hostlist) игровой стратегии не нужен:
        // её фильтры заданы портами игры. Иначе в одном профиле оказались бы
        // два --filter-tcp и обход смешался бы с сайтовым.
        if (!strategy.IsGame)
        {
            parts.Add($"--filter-tcp={target.Port}");
            parts.Add($"--hostlist=\"{Escape(hostListPath)}\"");

            // Готовый список заблокированных сервисов — отдельным файлом:
            // полный реестр РКН (~1,7 млн строк) не копируем в hostlist.txt.
            foreach (string extra in target.ExtraHostListPaths)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;
                parts.Add($"--hostlist=\"{Escape(extra)}\"");
            }
        }

        // %HOSTLIST% в профилях стратегии (в т.ч. UDP/QUIC) должен видеть те же
        // списки, что и TCP-профиль. Иначе готовый список фильтрует только TCP,
        // а QUIC остаётся без обхода.
        string hostListArg = $"\"{Escape(hostListPath)}\"";
        foreach (string extra in target.ExtraHostListPaths)
        {
            if (string.IsNullOrWhiteSpace(extra)) continue;
            hostListArg += $" --hostlist=\"{Escape(extra)}\"";
        }

        // %IPSET% подставляется как готовый путь в кавычках: в каталоге
        // аргумент записан целиком (--ipset=%IPSET%), иначе получилось бы
        // --ipset=--ipset="...".
        string ipSetArg = string.IsNullOrEmpty(ipSetPath) ? "" : $"\"{Escape(ipSetPath)}\"";
        string gameTcpPorts = string.Join(',', target.GameTcpPortList);
        string gameUdpPorts = string.Join(',', target.GameUdpPortList);

        // Папка fakes лежит рядом с папкой движка: <install>\zapret-winws -> <install>\fakes.
        string fakesPrefix = string.IsNullOrEmpty(engineDirectory)
            ? ""
            : Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(engineDirectory))!, "fakes") + "\\";

        foreach (var arg in profileArgs)
        {
            // Адресов серверов нет — убираем только сам --ipset, профиль по
            // портам игры остаётся рабочим. Пустой --ipset winws не примет.
            if (arg.Contains("%IPSET%", StringComparison.Ordinal) && ipSetArg.Length == 0) continue;

            string a = arg
                .Replace("%HOSTLIST%", hostListArg)
                .Replace("%IPSET%", ipSetArg)
                .Replace("%GAMETCPPORTS%", gameTcpPorts)
                .Replace("%GAMEUDPPORTS%", gameUdpPorts)
                .Replace("%TCPPORTS%", target.TcpPorts)
                .Replace("%UDPPORTS%", target.UdpPorts)
                .Replace("%FAKES%", fakesPrefix)
                .Replace("%ENGINE%", engineDirectory ?? "");
            if (string.IsNullOrWhiteSpace(a)) continue;
            parts.Add(a);
        }

        parts.Add("--new");
        return string.Join(' ', parts);
    }

    /// <summary>Порог «тяжёлого» списка: полный реестр РКН.</summary>
    private const long LargeHostListBytes = 5 * 1024 * 1024;

    /// <summary>Пауза после запуска winws: большие списки читаются дольше.</summary>
    private static int StartupDelayMs(DpiTarget target)
    {
        foreach (string path in target.ExtraHostListPaths)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                    new FileInfo(path).Length > LargeHostListBytes)
                    return 3500;
            }
            catch
            {
                // Размер не критичен: при ошибке используем обычную паузу.
            }
        }
        return 1200;
    }

    private static string Escape(string path) => path.Replace("\"", "\\\"");

    private string? _engineLogPath;
    private long _engineLogPosition;

    /// <summary>Содержимое журнала движка (winws пишет его сам в файл).</summary>
    private static string ReadEngineLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string text = reader.ReadToEnd();
            return text.Length > 4000 ? text[^4000..] : text;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Новые строки журнала движка с прошлого вызова — чтобы показывать
    /// работу zapret в журнале приложения без перехвата вывода процесса.
    /// </summary>
    public string ReadNewOutput()
    {
        string path = _engineLogPath ?? "";
        if (path.Length == 0) return "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _engineLogPosition) _engineLogPosition = 0;
            fs.Seek(_engineLogPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string text = reader.ReadToEnd();
            _engineLogPosition = fs.Position;
            return text;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Остановить движок.</summary>
    public async Task StopAsync()
    {
        // Берём локальную ссылку: Stop можно вызвать параллельно с запуском,
        // и _process может обнулиться посреди проверки.
        var proc = _process;
        _process = null;
        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(true);
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            proc.Dispose();
        }

        CurrentStrategyId = null;
        Log?.Invoke("zapret остановлен");
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }

    // ------------------------------------------------- принудительная остановка

    /// <summary>
    /// Полная остановка обхода DPI: наш движок, осиротевшие процессы winws
    /// и драйвер WinDivert. Используется при выходе из программы и по кнопке
    /// «Принудительно остановить» — после неё сеть не остаётся в перехвате.
    /// </summary>
    public async Task ForceStopAllAsync()
    {
        await StopAsync().ConfigureAwait(false);
        KillLeftoverEngines();
        UnloadDivertDriver();
    }

    /// <summary>Снимает все процессы winws (движок zapret), в том числе чужие/осиротевшие.</summary>
    private void KillLeftoverEngines()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("winws");
        }
        catch
        {
            return;
        }

        foreach (var proc in processes)
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                Log?.Invoke($"принудительно снят процесс zapret (PID {proc.Id})");
            }
            catch { }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Выгружает драйвер WinDivert (он держит перехват трафика на уровне ядра).
    /// Если драйвера нет — ничего не делаем.
    /// </summary>
    private void UnloadDivertDriver()
    {
        foreach (string service in new[] { "WinDivert1.4", "WinDivert", "WinDivert32", "WinDivert64" })
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"stop {service}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(info);
                if (proc is null) continue;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                    Log?.Invoke($"драйвер {service} остановлен");
            }
            catch { }
        }
    }
}

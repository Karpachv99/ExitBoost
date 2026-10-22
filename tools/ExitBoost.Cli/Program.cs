using System.Text;
using ExitBoost.Core;
using ExitBoost.Core.Configuration;
using ExitBoost.Core.Diagnostics;
using ExitBoost.Core.Dpi;
using ExitBoost.Core.Logging;
using ExitBoost.Core.Models;
using ExitBoost.Core.Networking;
using ExitBoost.Core.Routing;
using ExitBoost.Core.Tunnel;
using ExitBoost.Core.Vpn;

namespace ExitBoost.Cli;

/// <summary>Консольный клиент ExitBoost: измерения, выбор узла, подключение.</summary>
public static class Program
{
    private static readonly SettingsStore Settings = new();
    private static readonly LogBus Log = new();
    private static readonly NodeRegistry Registry = new();
    private static readonly WindowsRouteManager Routes = new();
    private static readonly TunnelManager Tunnel = new(Routes, Settings);
    private static readonly ProbingService Probing = new();
    private static BoostController? _controller;

    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        Settings.Load();
        Log.EnableFileLogging(AppPaths.LogFile);
        Routes.Log = m => Log.Warn(m);

        string nodesFile = string.IsNullOrWhiteSpace(Settings.Current.NodesFilePath)
            ? AppPaths.DefaultNodesFile
            : Settings.Current.NodesFilePath;
        if (File.Exists(nodesFile))
        {
            try { Registry.Load(nodesFile); }
            catch (Exception ex) { Log.Error($"Не удалось загрузить узлы: {ex.Message}"); }
        }

        string command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "nodes":
                ShowNodes();
                return 0;

            case "probe":
                await ProbeAllAsync();
                ShowNodes();
                return 0;

            case "best":
                await ProbeAllAsync();
                ShowBest();
                return 0;

            case "status":
                ShowStatus();
                return 0;

            case "connect":
                return await ConnectAsync(args.Skip(1).ToArray());

            case "disconnect":
                await DisconnectAsync();
                return 0;

            case "import":
                return Import(args.Skip(1).ToArray());

            case "export":
                return Export(args.Skip(1).ToArray());

            case "add":
                return Add(args.Skip(1).ToArray());

            case "repair":
                _controller ??= new BoostController(Settings, Log, Routes, Tunnel);
                var repair = await _controller.RepairAsync();
                Console.WriteLine("Отчёт ремонта:");
                foreach (var step in repair.Steps)
                    Console.WriteLine($"  · {step}");
                Console.WriteLine(repair.InternetAvailable
                    ? "Интернет работает."
                    : "Интернет недоступен — причина вне приложения (роутер/провайдер).");
                return repair.InternetAvailable ? 0 : 1;

            case "dpi":
                return await DpiCommandAsync(args.Skip(1).ToArray());

            case "vpn":
                return await VpnCommandAsync(args.Skip(1).ToArray());

            case "force-stop":
                // принудительное закрытие всех служб запрета: процессы zapret + драйвер WinDivert
                return await ForceStopDpiAsync();

            default:
                Console.Error.WriteLine($"Неизвестная команда: {command}");
                PrintHelp();
                return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ExitBoost — консольный клиент игрового бустера

            Команды:
              nodes                  список ретрансляторов и их состояние
              probe                  выполнить один раунд измерений и показать результат
              best                   измерить и выбрать лучший узел
              connect <id|--best>    подключиться (опции: --mode full|proc|net, --subnets 1.2.3.0/24)
              disconnect             отключиться
              status                 текущее состояние туннеля
              import <файл...>       импортировать конфиги .conf/.ovpn
              export <файл>          выгрузить список узлов (.json/.yaml)
              add <имя> <хост> <порт> --pubkey ... --privkey ... --address ...
              repair                 починить интернет: снять туннели, вернуть маршруты, очистить DNS
              dpi install            скачать/обновить движок zapret
              dpi list               показать каталог стратегий zapret
              dpi optimize --host d  подобрать лучшую стратегию обхода DPI
              dpi apply <id> --host d применить стратегию обхода DPI
              dpi stop               остановить обход DPI
              force-stop             принудительно закрыть всё: процессы zapret и драйвер WinDivert
              vpn install            скачать ядро sing-box
              vpn keys list          список сохранённых VPN-ключей
              vpn keys add <ссылка>  добавить ключ (vless/vmess/trojan/ss) можно несколько в кавычках
              vpn keys remove <id>   удалить сохранённый ключ
              vpn connect [id|имя]   подключиться по ключу (опции: --full, --domains a.com,b, --procs x.exe)
              vpn disconnect         отключить VPN по ключу
              vpn status             состояние VPN по ключу
            """);
    }

    private static void ShowNodes()
    {
        var statuses = Probing.Statuses.ToList();
        if (statuses.Count == 0)
        {
            foreach (var node in Registry.Nodes)
                statuses.Add(new NodeStatus { Config = node });
        }

        Console.WriteLine();
        Console.WriteLine($"{"ID",-10} {"Узел",-24} {"Страна",-10} {"Адрес",-24} {"Пинг",7} {"Потери",7} {"Джиттер",8} {"Качество",-10}");
        Console.WriteLine(new string('-', 118));

        foreach (var s in NodeSelector.Rank(statuses))
        {
            string ping = s.HasData ? $"{s.AvgPingMs:F0} мс" : "—";
            string loss = s.HasData ? $"{s.LossPercent:F0}%" : "—";
            string jitter = s.HasData ? $"{s.JitterMs:F0} мс" : "—";
            Console.WriteLine($"{s.Config.Id,-10} {Truncate(s.Config.Name, 24),-24} {Truncate(s.Config.Country, 10),-10} {Truncate(s.Config.Host + ":" + s.Config.Port, 24),-24} {ping,7} {loss,7} {jitter,8} {s.QualityText,-10}");
        }
        Console.WriteLine();
    }

    private static async Task ProbeAllAsync()
    {
        Probing.IntervalSeconds = 1;
        Probing.ProbeCount = Math.Max(3, Settings.Current.ProbeCount);
        Probing.TimeoutMs = Settings.Current.ProbeTimeoutMs;
        Probing.Window = Settings.Current.ProbeWindow;

        var nodes = Registry.Nodes.Where(n => n.Enabled).ToList();
        if (nodes.Count == 0)
        {
            Console.WriteLine("Список узлов пуст. Импортируйте конфиг или добавьте узел.");
            return;
        }

        var tasks = nodes.Select(async n =>
        {
            var status = await Probing.ProbeRoundAsync(n);
            Console.WriteLine($"  измерено: {n.Name} → {(status.HasData ? $"{status.AvgPingMs:F0} мс, потерь {status.LossPercent:F0}%" : "нет ответа")}");
        });

        await Task.WhenAll(tasks);
    }

    private static void ShowBest()
    {
        var best = NodeSelector.SelectBest(Probing.Statuses);
        if (best is null)
        {
            Console.WriteLine("Нет доступных узлов.");
            return;
        }
        Console.WriteLine();
        Console.WriteLine($"Лучший узел: {best.Config.Name} ({best.Config.Host}:{best.Config.Port})");
        Console.WriteLine($"  задержка: {best.AvgPingMs:F1} мс | потери: {best.LossPercent:F1}% | джиттер: {best.JitterMs:F1} мс | балл: {best.Score:F0}");
        Console.WriteLine($"  id: {best.Config.Id}");
        Console.WriteLine();
    }

    private static void ShowStatus()
    {
        Console.WriteLine();
        Console.WriteLine($"Состояние туннеля : {Tunnel.State}");
        Console.WriteLine($"Узел             : {Tunnel.CurrentNode?.Name ?? "—"}");
        Console.WriteLine($"Интерфейс        : {Tunnel.InterfaceAlias ?? "—"}");
        Console.WriteLine($"Адреса           : {string.Join(", ", Tunnel.InterfaceAddresses)}");
        Console.WriteLine($"Прав админа      : {(WindowsRouteManager.IsElevated() ? "да" : "нет")}");
        Console.WriteLine();
    }

    private static async Task<int> ConnectAsync(string[] args)
    {
        if (!WindowsRouteManager.IsElevated())
        {
            Console.Error.WriteLine("Нужны права администратора. Запустите консоль от имени администратора.");
            return 2;
        }

        string id = args.FirstOrDefault() ?? "--best";
        var mode = RoutingMode.ProcessSplit;
        var subnets = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mode" when i + 1 < args.Length:
                    mode = args[++i].ToLowerInvariant() switch
                    {
                        "full" => RoutingMode.FullTunnel,
                        "net" or "network" => RoutingMode.NetworkSplit,
                        _ => RoutingMode.ProcessSplit
                    };
                    break;
                case "--subnets" when i + 1 < args.Length:
                    subnets.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries));
                    break;
            }
        }

        NodeConfig? node;
        if (id == "--best")
        {
            await ProbeAllAsync();
            node = NodeSelector.SelectBest(Probing.Statuses)?.Config;
        }
        else
        {
            node = Registry.Get(id) ?? Registry.Nodes.FirstOrDefault(n =>
                n.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        if (node is null)
        {
            Console.Error.WriteLine($"Узел «{id}» не найден.");
            return 1;
        }

        if (subnets.Count > 0)
        {
            Settings.Update(s => s.SplitSubnets = subnets);
        }

        _controller ??= new BoostController(Settings, Log, Routes, Tunnel);
        bool ok = await _controller.ConnectAsync(node, mode);
        Console.WriteLine(ok ? $"Подключено к {node.Name}." : $"Не удалось подключиться: {_controller.StatusMessage}");
        return ok ? 0 : 1;
    }

    private static async Task DisconnectAsync()
    {
        _controller ??= new BoostController(Settings, Log, Routes, Tunnel);
        await _controller.CleanupAllAsync();
        Console.WriteLine("Отключено: туннели сняты, маршруты и правила брандмауэра восстановлены.");
    }

    private static int Import(string[] files)
    {
        if (files.Length == 0)
        {
            Console.Error.WriteLine("Укажите файлы конфигурации.");
            return 1;
        }

        int added = 0;
        foreach (var file in files)
        {
            try
            {
                var node = NodeRegistry.ImportConfigFile(file);
                if (node is null)
                {
                    Console.Error.WriteLine($"Не удалось разобрать {Path.GetFileName(file)}");
                    continue;
                }
                Registry.Add(node);
                added++;
                Console.WriteLine($"Добавлен узел {node.Name} ({node.Host}:{node.Port})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Ошибка {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (added > 0) SaveRegistry();
        return added > 0 ? 0 : 1;
    }

    private static int Export(string[] files)
    {
        if (files.Length == 0) files = new[] { AppPaths.DefaultNodesFile };
        try
        {
            Registry.Save(files[0]);
            Console.WriteLine($"Сохранено в {files[0]}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Add(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Использование: add <имя> <хост> <порт> [--pubkey K] [--privkey K] [--address A] [--country C] [--dns D]");
            return 1;
        }

        var node = new NodeConfig
        {
            Name = args[0],
            Host = args[1],
            Port = int.TryParse(args[2], out int p) ? p : 51820
        };

        for (int i = 3; i < args.Length - 1; i++)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--pubkey": node.PublicKey = value; break;
                case "--privkey": node.PrivateKey = value; break;
                case "--psk": node.PresharedKey = value; break;
                case "--address": node.Address = value; break;
                case "--country": node.Country = value; break;
                case "--dns": node.Dns = value; break;
                case "--city": node.City = value; break;
                case "--mtu" when int.TryParse(value, out int mtu): node.Mtu = mtu; break;
            }
        }

        Registry.Add(node);
        SaveRegistry();
        Console.WriteLine($"Добавлен узел {node.Name} ({node.Host}:{node.Port}), id={node.Id}");
        return 0;
    }

    // ---------------------------------------------------------- обход DPI

    private static async Task<int> DpiCommandAsync(string[] args)
    {
        string action = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        string host = "discord.com";
        int port = 443;
        string? strategyId = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Length: host = args[++i]; break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int p): port = p; i++; break;
            }
        }
        // id стратегии — первый позиционный аргумент (не «--опция»).
        if (args.Length > 1 && !args[1].StartsWith("--"))
            strategyId = args[1];

        var bundle = new ZapretBundle();
        bundle.Log = m => Log.Info(m);
        var runner = new ZapretRunner(bundle);
        runner.Log = m => Log.Info(m);
        var optimizer = new DpiOptimizer(runner);
        var catalogPath = Path.Combine(bundle.InstallDirectory, "catalog.json");
        var catalog = ZapretCatalog.Load(catalogPath);

        switch (action)
        {
            case "install":
            {
                Console.WriteLine("Загрузка zapret-win-bundle...");
                if (!await bundle.InstallAsync())
                {
                    Console.Error.WriteLine($"Не удалось установить zapret. Причина: {bundle.LastError ?? "неизвестна"}");
                    return 1;
                }
                catalog = ZapretCatalog.BuildDefault();
                catalog.Save(catalogPath);
                Console.WriteLine($"Установлено в {bundle.EngineDirectory}");
                Console.WriteLine($"Стратегий в каталоге: {catalog.Strategies.Count}");
                return 0;
            }

            case "list":
            {
                if (!bundle.IsInstalled)
                {
                    Console.WriteLine("Движок не установлен. Выполните: ExitBoost dpi install");
                    return 1;
                }
                Console.WriteLine();
                Console.WriteLine($"{"ID",-26} {"Стратегия",-34} {"Источник",-18} Команда");
                Console.WriteLine(new string('-', 120));
                foreach (var s in catalog.Strategies)
                    Console.WriteLine($"{s.Id,-26} {Truncate(s.Name, 34),-34} {Truncate(s.Source, 18),-18} {s.CommandLine}");
                Console.WriteLine();
                return 0;
            }

            case "optimize":
            {
                if (!bundle.IsInstalled)
                {
                    Console.WriteLine("Движок не установлен. Выполните: ExitBoost dpi install");
                    return 1;
                }

                var target = new DpiTarget { Hosts = host, Port = port };
                Console.WriteLine($"Цель: {host}:{port}");

                var baseline = await optimizer.MeasureBaselineAsync(target);
                Console.WriteLine(baseline.Ok
                    ? $"Без обхода: {baseline.TlsMs:F0} мс (TLS ok)"
                    : $"Без обхода: не работает ({baseline.Error})");

                var progress = new Progress<DpiStrategyResult>(r =>
                    Console.WriteLine($"  {r.Strategy.Name,-36} {r.ResultText}"));

                var results = await optimizer.OptimizeAsync(catalog.Strategies, target, 2, progress);

                Console.WriteLine();
                Console.WriteLine("Итог (по убыванию качества):");
                foreach (var r in results.Take(10))
                    Console.WriteLine($"  {r.Score,8:F1}  {r.Strategy.Name,-36} {r.ResultText}");

                var best = results.FirstOrDefault(r => r.Success);
                if (best is not null)
                    Console.WriteLine($"\nЛучшая стратегия: {best.Strategy.Name} (id={best.Strategy.Id})");
                else
                    Console.WriteLine("\nРаботающей стратегии не найдено.");
                return best is null ? 1 : 0;
            }

            case "apply":
            {
                if (!bundle.IsInstalled)
                {
                    Console.WriteLine("Движок не установлен.");
                    return 1;
                }
                if (strategyId is null)
                {
                    Console.Error.WriteLine("Укажите id стратегии: ExitBoost dpi apply <id> --host domain");
                    return 1;
                }

                var strategy = catalog.Strategies.FirstOrDefault(s => s.Id == strategyId);
                if (strategy is null)
                {
                    Console.Error.WriteLine($"Стратегия «{strategyId}» не найдена.");
                    return 1;
                }

                var target = new DpiTarget { Hosts = host, Port = port };
                bool ok = await runner.StartAsync(strategy, target);
                Console.WriteLine(ok ? $"Применено: {strategy.Name}" : "Не удалось запустить движок.");
                return ok ? 0 : 1;
            }

            case "stop":
                await runner.StopAsync();
                // CLI не сохраняет состояние между запусками: winws мог остаться
                // от прошлого вызова apply — прибираем и его (только наш путь).
                bundle.StopOrphanWinws();
                Console.WriteLine("Обход остановлен.");
                return 0;

            default:
                Console.Error.WriteLine($"Неизвестное действие: {action}");
                return 1;
        }
    }

    private static void SaveRegistry()    {
        string path = string.IsNullOrWhiteSpace(Settings.Current.NodesFilePath)
            ? AppPaths.DefaultNodesFile
            : Settings.Current.NodesFilePath;
        Registry.Save(path);
        Settings.Update(s => s.NodesFilePath = path);
    }

    /// <summary>Принудительная остановка обхода DPI: все процессы zapret и драйвер WinDivert.</summary>
    private static async Task<int> ForceStopDpiAsync()
    {
        var bundle = new ZapretBundle();
        bundle.Log = m => Log.Info(m);
        var runner = new ZapretRunner(bundle);
        runner.Log = m => Log.Info(m);

        await runner.ForceStopAllAsync();
        Console.WriteLine("Обход DPI принудительно остановлен: процессы zapret сняты, драйвер выгружен.");
        return 0;
    }

    // ------------------------------------------------------------ VPN по ключу
    /// <summary>Разбивает строку «vless://a vless://b ss://c» на отдельные ссылки (по известным схемам).</summary>
    private static string[] SplitKeyLines(string links)
    {
        var groups = new List<string>();
        var current = new List<string>();
        foreach (var token in links.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool startsKnown = token.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
                            || token.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
                            || token.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
                            || token.StartsWith("ss://", StringComparison.OrdinalIgnoreCase);
            if (startsKnown && current.Count > 0)
            {
                groups.Add(string.Join(" ", current));
                current.Clear();
            }
            current.Add(token);
        }
        if (current.Count > 0) groups.Add(string.Join(" ", current));
        return groups.ToArray();
    }

    private static async Task<int> VpnCommandAsync(string[] args)
    {
        string action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        var runner = new SingBoxRunner();
        runner.Log = m => Log.Info(m);
        var store = new VpnKeyStore();

        string ModeArg() => args.Contains("--full") ? "full" : "split";
        string Param(string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return "";
        }

        switch (action)
        {
            case "install":
                return await runner.InstallAsync() ? 0 : 1;

            case "keys":
            {
                string keysAction = args.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "list";
                switch (keysAction)
                {
                    case "add":
                    {
                        string links = Param("--link");
                        if (args.Length > 2 && !args[2].StartsWith("--")) links = args[2];
                        if (links.Length == 0)
                        {
                            Console.Error.WriteLine("Укажите ключ: vpn keys add \"vless://…\" (можно несколько через пробел в кавычках)");
                            return 1;
                        }
                        List<VpnKey> parsed;
                        List<string> errors;
                        if (VpnSubscription.IsUrl(links))
                        {
                            // ссылка-подписка: скачиваем и разбираем всё, что в ней есть
                            string text = await VpnSubscription.DownloadAsync(links);
                            parsed = VpnSubscription.ParseAll(text, out errors);
                        }
                        else
                        {
                            parsed = VpnKeyParser.ParseMany(string.Join("\n", SplitKeyLines(links)), out errors);
                        }
                        foreach (var e in errors) Console.Error.WriteLine("• " + e);
                        if (parsed.Count == 0) return 1;
                        foreach (var key in parsed)
                        {
                            if (store.Keys.Any(k => k.RawLink == key.RawLink))
                            {
                                Console.WriteLine($"уже сохранён: {key.Name}");
                                continue;
                            }
                            store.Keys.Add(key);
                            Console.WriteLine($"добавлен: {key.Name} · {key.Protocol} {key.Address}:{key.Port}");
                        }
                        store.Save();
                        return 0;
                    }
                    case "remove":
                    {
                        string id = args.ElementAtOrDefault(2) ?? "";
                        var key = store.Keys.FirstOrDefault(k =>
                            k.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                            k.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
                        if (key is null)
                        {
                            Console.Error.WriteLine($"Ключ «{id}» не найден. Список: vpn keys list");
                            return 1;
                        }
                        store.Keys.Remove(key);
                        store.Save();
                        Console.WriteLine($"удалён: {key.Name}");
                        return 0;
                    }
                    default:
                        if (store.Keys.Count == 0)
                            Console.WriteLine("Ключей пока нет. Добавьте: vpn keys add \"vless://…\"");
                        foreach (var key in store.Keys)
                            Console.WriteLine($"{key.Id}  {key.Name,-24} {key.Protocol,-12} {key.Address}:{key.Port}");
                        return 0;
                }
            }

            case "connect":
            {
                VpnKey? key = null;
                string selector = args.ElementAtOrDefault(1) ?? "";
                if (selector.Length > 0 && !selector.StartsWith("--"))
                    key = store.Keys.FirstOrDefault(k =>
                        k.Id.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
                        k.Name.Equals(selector, StringComparison.OrdinalIgnoreCase));
                key ??= store.Keys.FirstOrDefault();

                if (key is null)
                {
                    Console.Error.WriteLine("Нет сохранённых ключей. Добавьте: vpn keys add \"vless://…\"");
                    return 1;
                }

                var lastRouting = store.Routing.Clone();
                store.Routing.Mode = ModeArg();
                string domainsArg = Param("--domains");
                store.Routing.Domains = domainsArg.Length > 0
                    ? domainsArg.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : lastRouting.Domains;
                string procsArg = Param("--procs");
                store.Routing.Processes = procsArg.Length > 0
                    ? procsArg.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : lastRouting.Processes;
                store.Save();

                var result = await runner.ConnectAsync(key, store.Routing);
                if (result.Ok)
                    Console.WriteLine($"VPN работает: {key.Name} ({key.Protocol} {key.Address}:{key.Port}), режим {(store.Routing.Mode == "full" ? "весь трафик" : "только выбранные")}");
                else
                {
                    Console.Error.WriteLine("Не удалось подключить VPN: " + result.Error);
                    return 1;
                }
                return 0;
            }

            case "disconnect":
                await runner.DisconnectAsync();
                return 0;

            case "status":
            default:
            {
                if (runner.IsCoreRunning)
                    Console.WriteLine("VPN по ключу работает (ядро sing-box запущено)");
                else
                    Console.WriteLine("VPN по ключу не активен");
                if (store.Keys.Count > 0)
                    Console.WriteLine($"ключей сохранено: {store.Keys.Count}");
                return 0;
            }
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..(max - 1)] + "…";
}


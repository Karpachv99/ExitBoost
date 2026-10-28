namespace ExitBoost.Core.Models;

/// <summary>Пользовательские настройки приложения. Хранятся в %APPDATA%\ExitBoost\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Путь к wireguard.exe (WireGuard for Windows).</summary>
    public string WireGuardExePath { get; set; } = "";

    /// <summary>Путь к openvpn.exe.</summary>
    public string OpenVpnExePath { get; set; } = "";

    /// <summary>Путь к ssh.exe.</summary>
    public string SshExePath { get; set; } = "";

    /// <summary>Режим маршрутизации по умолчанию.</summary>
    public RoutingMode DefaultMode { get; set; } = RoutingMode.ProcessSplit;

    /// <summary>Интервал между раундами измерений, с.</summary>
    public int ProbeIntervalSeconds { get; set; } = 5;

    /// <summary>Количество пакетов в одном раунде измерений.</summary>
    public int ProbeCount { get; set; } = 4;

    /// <summary>Таймаут одного пакета, мс.</summary>
    public int ProbeTimeoutMs { get; set; } = 1500;

    /// <summary>Размер окна измерений (количество последних пакетов для расчёта статистики).</summary>
    public int ProbeWindow { get; set; } = 24;

    /// <summary>MTU по умолчанию.</summary>
    public int Mtu { get; set; } = 1420;

    /// <summary>Автоматически выбирать лучший узел.</summary>
    public bool AutoSelectBest { get; set; } = true;

    /// <summary>Идентификатор выбранного узла.</summary>
    public string? SelectedNodeId { get; set; }

    /// <summary>Путь к целевому процессу (игре) для ProcessSplit.</summary>
    public string? TargetProcessPath { get; set; }

    /// <summary>Имя целевого процесса.</summary>
    public string? TargetProcessName { get; set; }

    /// <summary>Подсети, заворачиваемые на узел в режиме NetworkSplit.</summary>
    public List<string> SplitSubnets { get; set; } = new();

    /// <summary>Автоматически определять адреса назначения целевого процесса.</summary>
    public bool AutoDetectEndpoints { get; set; } = true;

    /// <summary>Периодически переопределять маршруты по обнаруженным соединениям, с.</summary>
    public int EndpointRefreshSeconds { get; set; } = 5;

    /// <summary>Разрешить DNS через туннель.</summary>
    public bool DnsThroughTunnel { get; set; } = true;

    /// <summary>Записывать подробный лог в файл.</summary>
    public bool FileLogging { get; set; } = true;

    /// <summary>Путь к файлу узлов.</summary>
    public string NodesFilePath { get; set; } = "";

    /// <summary>Адрес публичного списка узлов (JSON или YAML).</summary>
    public string NodesFeedUrl { get; set; } = "";

    /// <summary>Сворачивать в трей при нажатии на крестик (иначе — отключаться и выходить).</summary>
    public bool CloseToTray { get; set; } = true;

    // ------------------------------------------------------------ обход DPI

    /// <summary>Проверять обновления движка zapret при запуске.</summary>
    public bool DpiCheckUpdates { get; set; } = true;

    /// <summary>Автоматически скачивать обновления движка zapret.</summary>
    public bool DpiAutoUpdate { get; set; }

    /// <summary>Домены, для которых применяется обход DPI (через запятую).</summary>
    public string DpiTargetHosts { get; set; } = "";

    /// <summary>Порт проверки обхода.</summary>
    public int DpiTargetPort { get; set; } = 443;

    /// <summary>IP-адреса серверов игры для обхода DPI (по одному в строке).</summary>
    public string DpiGameIps { get; set; } = "";

    /// <summary>Имя процесса игры, по которому ищутся её серверы.</summary>
    public string DpiGameProcess { get; set; } = "";

    /// <summary>Автоматически переводить только зависшие TCP-соединения игры на VPN.</summary>
    public bool AutoGameVpnFallback { get; set; }

    /// <summary>Имя процесса, отслеживаемого игровым автообходом VPN (не настройка DPI).</summary>
    public string VpnGameProcess { get; set; } = "";

    /// <summary>Весь трафик выбранной игры идёт через VPN (одно правило по процессу).</summary>
    public bool RouteGameThroughVpn { get; set; }

    /// <summary>TCP-порты игры для обхода DPI (через запятую).</summary>
    public string DpiGameTcpPorts { get; set; } = "";

    /// <summary>UDP-порты игры для обхода DPI (через запятую).</summary>
    public string DpiGameUdpPorts { get; set; } = "";

    /// <summary>Идентификатор выбранной стратегии zapret.</summary>
    public string DpiStrategyId { get; set; } = "";

    /// <summary>Применять обход DPI автоматически при подключении к ретранслятору.</summary>
    public bool DpiApplyOnConnect { get; set; }

    /// <summary>
    /// Готовый список заблокированных в РФ ресурсов: "off", "standard" или "full".
    /// Используется и обходом DPI (hostlist zapret), и VPN (домены sing-box).
    /// </summary>
    public string BlockedListPreset { get; set; } = "off";

    /// <summary>Запускать движок zapret вместе с приложением (служба).</summary>
    public bool DpiRunAsService { get; set; }

    // ------------------------------------------------------------ списки и подписки

    /// <summary>Автоматически обновлять готовые списки РКН (раз в неделю, в фоне).</summary>
    public bool AutoUpdateLists { get; set; } = true;

    /// <summary>Автоматически обновлять ключи по сохранённым ссылкам-подпискам.</summary>
    public bool AutoUpdateVpnSubscriptions { get; set; } = true;

    /// <summary>Ключ (локация), выбранный последним: его показывает шапка и кнопка питания.</summary>
    public string? VpnLastKeyId { get; set; }

    /// <summary>Последняя выбранная страна — на случай, если ключ из подписки исчез.</summary>
    public string? VpnLastCountry { get; set; }

    // ------------------------------------------------------------ обновления

    /// <summary>Официальный репозиторий: обновления всегда берутся только отсюда.</summary>
    public const string OfficialUpdateRepo = "karpachv99/ExitBoost";

    /// <summary>Репозиторий GitLab с релизами в формате «владелец/имя» (откуда обновляться).</summary>
    public string UpdateRepo { get; set; } = OfficialUpdateRepo;

    /// <summary>Проверять обновления самой программы при запуске.</summary>
    public bool CheckAppUpdatesOnStart { get; set; } = true;
}

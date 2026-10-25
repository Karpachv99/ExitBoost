namespace ExitBoost.Core.Dpi;

/// <summary>
/// Стратегия обхода DPI — набор аргументов winws.exe в терминах zapret
/// (--dpi-desync=..., --dpi-desync-split-pos=..., --hostcase и т.п.).
/// </summary>
public sealed record DpiStrategy
{
    /// <summary>Идентификатор.</summary>
    public string Id { get; init; } = "";

    /// <summary>Отображаемое имя.</summary>
    public string Name { get; init; } = "";

    /// <summary>Пояснение, что делает стратегия.</summary>
    public string Description { get; init; } = "";

    /// <summary>Аргументы winws.exe (без пути к исполняемому файлу).</summary>
    public string[] Args { get; init; } = Array.Empty<string>();

    /// <summary>Источник: «встроенная» или «zapret vXX».</summary>
    public string Source { get; init; } = "встроенная";

    /// <summary>
    /// Стратегия для игр: рассчитана на TCP-порты игры (%GAMETCPPORTS%),
    /// а не на домены. Без портов/IP игры её применять нельзя — иначе
    /// обход затронул бы весь HTTPS-трафик.
    /// </summary>
    public bool IsGameTcp { get; init; }

    /// <summary>Стратегия для UDP-трафика игры (%GAMEUDPPORTS%).</summary>
    public bool IsGameUdp { get; init; }

    public bool IsGame => IsGameTcp || IsGameUdp;

    /// <summary>
    /// Проверка применимости к цели. Возвращает текст ошибки или null.
    /// </summary>
    public string? Validate(DpiTarget target)
    {
        if (IsGameTcp && target.GameTcpPortList.Count == 0)
            return "Это стратегия для игр: укажите TCP-порты игры (кнопка «Серверы игры»)";
        if (IsGameUdp && target.GameUdpPortList.Count == 0)
            return "Это стратегия для игр: укажите UDP-порты игры";
        return null;
    }

    /// <summary>Только для чтения — команда для отображения.</summary>
    public string CommandLine => string.Join(' ', Args);

    public override string ToString() => Name;
}

/// <summary>
/// Разбор списков портов для winws: «443», «6112,27015-27020».
/// Нужен, чтобы в командную строку попадали только корректные значения.
/// </summary>
public static class DpiPorts
{
    /// <summary>Нормализованные порты: без пробелов, мусора и повторов.</summary>
    public static IReadOnlyList<string> Parse(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return result;

        foreach (string raw in value.Split(
                     [',', ';', ' ', '\t', '\n', '\r'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            if (token.Length == 0 || !TryParse(token, out int from, out int to)) continue;

            string normalized = from == to ? from.ToString() : $"{from}-{to}";
            if (!result.Contains(normalized, StringComparer.Ordinal)) result.Add(normalized);
        }

        return result;
    }

    /// <summary>Объединить несколько списков портов в один аргумент winws.</summary>
    public static string Merge(params string?[] values)
    {
        var result = new List<string>();
        foreach (string? value in values)
        {
            foreach (string port in Parse(value))
                if (!result.Contains(port, StringComparer.Ordinal)) result.Add(port);
        }
        return string.Join(',', result);
    }

    private static bool TryParse(string token, out int from, out int to)
    {
        from = to = 0;
        int dash = token.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(token, out from) || from is < 1 or > 65535) return false;
            to = from;
            return true;
        }

        if (!int.TryParse(token[..dash], out from) || !int.TryParse(token[(dash + 1)..], out to)) return false;
        return from is >= 1 and <= 65535 && to is >= 1 and <= 65535 && from <= to;
    }
}

/// <summary>Цель проверки обхода (домены, которые нужно разблокировать).</summary>
public sealed class DpiTarget
{
    /// <summary>Список доменов/IP через запятую.</summary>
    public string Hosts { get; set; } = "";

    /// <summary>TCP-порт для проверки.</summary>
    public int Port { get; set; } = 443;

    /// <summary>
    /// Дополнительные файлы hostlist (готовый список заблокированных сервисов).
    /// winws принимает несколько --hostlist и объединяет их.
    /// </summary>
    public List<string> ExtraHostListPaths { get; set; } = [];

    /// <summary>
    /// Домены для замера, когда своих доменов нет, но включён готовый список.
    /// В сам hostlist они не попадают: список уже передаётся отдельным файлом.
    /// </summary>
    public List<string> ProbeHosts { get; set; } = [];

    // ------------------------------------------------------------ игры

    /// <summary>
    /// TCP-порты игры. Для них winws добавляет отдельный профиль обхода
    /// (обычные стратегии рассчитаны на 80/443 и домены).
    /// </summary>
    public string GameTcpPorts { get; set; } = "";

    /// <summary>UDP-порты игры (реальный бой, голос, игровые сервисы).</summary>
    public string GameUdpPorts { get; set; } = "";

    /// <summary>
    /// IP-адреса игровых серверов. Передаются в winws как --ipset:
    /// обход применяется только к ним, остальной трафик не затрагивается.
    /// </summary>
    public List<string> GameIps { get; set; } = [];

    /// <summary>Нормализованные TCP-порты игры.</summary>
    public IReadOnlyList<string> GameTcpPortList => DpiPorts.Parse(GameTcpPorts);

    /// <summary>Нормализованные UDP-порты игры.</summary>
    public IReadOnlyList<string> GameUdpPortList => DpiPorts.Parse(GameUdpPorts);

    /// <summary>Корректные IP-адреса серверов игры (без пустых строк и мусора).</summary>
    public IReadOnlyList<string> GameIpList => GameIps
        .Select(ip => ip.Trim())
        .Where(IsIpOrSubnet)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Есть ли что обходить в игре: порты или адреса серверов.</summary>
    public bool IsGameMode =>
        GameTcpPortList.Count > 0 || GameUdpPortList.Count > 0 || GameIpList.Count > 0;

    /// <summary>Все TCP-порты, которые должен видеть winws (80/443 добавляются отдельно).</summary>
    public string TcpPorts => DpiPorts.Merge(Port.ToString(), GameTcpPorts);

    /// <summary>Все UDP-порты для --wf-udp.</summary>
    public string UdpPorts => DpiPorts.Merge(GameUdpPorts);

    /// <summary>Порты для замера игрового соединения.</summary>
    public IReadOnlyList<int> GameProbePorts => GameTcpPortList
        .Select(p => p.Contains('-') ? int.Parse(p.Split('-')[0]) : int.Parse(p))
        .Distinct()
        .ToList();

    private static bool IsIpOrSubnet(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        string address = value;
        int? prefix = null;
        int slash = value.IndexOf('/');
        if (slash > 0)
        {
            address = value[..slash];
            if (!int.TryParse(value[(slash + 1)..], out int parsed)) return false;
            prefix = parsed;
        }

        if (!System.Net.IPAddress.TryParse(address, out var ip)) return false;

        // У IPv4 максимум /32, у IPv6 — /128: winws не примет лишнее.
        int maxPrefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return prefix is null || (prefix >= 0 && prefix <= maxPrefix);
    }

    /// <summary>Разделённые домены.</summary>
    public IReadOnlyList<string> HostList => Hosts
        .Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(h => h.Trim())
        .Where(h => h.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Домены для замера: свои, иначе выборка из готового списка.</summary>
    public IReadOnlyList<string> ProbeHostList => HostList.Count > 0 ? HostList : ProbeHosts;

    /// <summary>Адреса для замера игрового соединения.</summary>
    public IReadOnlyList<string> GameProbeHosts => GameIpList;

    public bool IsValid => HostList.Count > 0 || ExtraHostListPaths.Count > 0 || IsGameMode;
}

/// <summary>Результат замера доступности цели.</summary>
public sealed record DpiProbeResult(
    bool TcpOk,
    double TcpMs,
    bool TlsOk,
    double TlsMs,
    string? Error)
{
    public bool Ok => TcpOk && TlsOk;

    public static DpiProbeResult Failed(string error) => new(false, 0, false, 0, error);
}

/// <summary>
/// Результат замера одного домена. Нужен, чтобы отличить реально
/// заблокированные домены от тех, что открываются без обхода.
/// </summary>
public sealed record DpiHostProbe(
    string Host,
    bool Ok,
    double TcpMs,
    double TlsMs,
    string? Error);

/// <summary>Результат проверки стратегии.</summary>
public sealed record DpiStrategyResult(
    DpiStrategy Strategy,
    bool Success,
    double AverageTlsMs,
    double AverageTcpMs,
    int Successes,
    int Attempts,
    string? Error)
{
    /// <summary>Балл: чем выше, тем лучше.</summary>
    public double Score => Success
        ? Math.Round(1000 - AverageTlsMs * 2 + Successes * 10, 1)
        : 0;

    public string ResultText => Success
        ? $"{AverageTlsMs:F0} мс · {Successes}/{Attempts}"
        : (Error ?? "не работает");
}

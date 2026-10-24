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

    /// <summary>Только для чтения — команда для отображения.</summary>
    public string CommandLine => string.Join(' ', Args);

    public override string ToString() => Name;
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

    /// <summary>Разделённые домены.</summary>
    public IReadOnlyList<string> HostList => Hosts
        .Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(h => h.Trim())
        .Where(h => h.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool IsValid => HostList.Count > 0 || ExtraHostListPaths.Count > 0;
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

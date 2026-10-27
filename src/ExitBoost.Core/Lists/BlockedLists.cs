using System.Text;
using System.Text.RegularExpressions;

namespace ExitBoost.Core.Lists;

/// <summary>Готовый список заблокированных в РФ ресурсов.</summary>
public enum BlockedListPreset
{
    /// <summary>Не использовать готовый список — только свои домены.</summary>
    Off = 0,

    /// <summary>
    /// «Стандартный список заблокированных сервисов для РФ» — курируемый набор
    /// itdoginfo/allow-domains (Russia inside): популярные сервисы и категории,
    /// ~1,2 тыс. доменов. Подходит и для обхода DPI, и для VPN.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Полный реестр РКН из AbbeyLubber/ru-blocklist-aggregator (~1,7 млн доменов).
    /// Максимальный охват, но тяжёлый: для VPN-режима «только выбранные» заметно
    /// замедляет старт ядра, поэтому по умолчанию выключен.
    /// </summary>
    Full = 2
}

/// <summary>Состояние локальной копии списка.</summary>
public sealed record BlockedListInfo(
    BlockedListPreset Preset,
    string Path,
    int Count,
    DateTime? UpdatedAt,
    bool FromCache,
    string? Error)
{
    public bool Ok => Error is null && Count > 0;
}

/// <summary>
/// Загрузка и кэширование списков заблокированных ресурсов.
/// <para>Списки скачиваются в %APPDATA%\ExitBoost\lists и обновляются не чаще
/// раза в неделю. При недоступности сети используется последняя копия.</para>
/// </summary>
public static class BlockedLists
{
    /// <summary>Курируемый список «заблокировано в РФ» (itdoginfo/allow-domains).</summary>
    public const string StandardUrl =
        "https://raw.githubusercontent.com/itdoginfo/allow-domains/main/Russia/inside-raw.lst";

    /// <summary>Полный реестр РКН (AbbeyLubber/ru-blocklist-aggregator).</summary>
    public const string FullUrl =
        "https://raw.githubusercontent.com/AbbeyLubber/ru-blocklist-aggregator/main/blocked/rkn-domain.txt";

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Через сколько дней локальная копия списка считается устаревшей.</summary>
    public static TimeSpan RefreshPeriod => RefreshAfter;

    /// <summary>
    /// Информация о локальной копии без обращения к сети: сколько доменов и когда
    /// обновлён. null — копии ещё нет.
    /// </summary>
    public static BlockedListInfo? Cached(BlockedListPreset preset)
    {
        if (preset == BlockedListPreset.Off) return null;
        string path = ListPath(preset);
        return File.Exists(path) ? Inspect(preset, path, fromCache: true, error: null) : null;
    }

    /// <summary>Человекочитаемое имя списка.</summary>
    public static string DisplayName(BlockedListPreset preset) => preset switch
    {
        BlockedListPreset.Standard => "Стандартный список заблокированных сервисов РФ",
        BlockedListPreset.Full => "Полный реестр РКН",
        _ => "Не использовать"
    };

    /// <summary>Разбор строки настройки (в settings.json хранится строкой).</summary>
    public static BlockedListPreset Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "standard" => BlockedListPreset.Standard,
        "full" => BlockedListPreset.Full,
        _ => BlockedListPreset.Off
    };

    /// <summary>Строка для settings.json.</summary>
    public static string ToSetting(BlockedListPreset preset) => preset switch
    {
        BlockedListPreset.Standard => "standard",
        BlockedListPreset.Full => "full",
        _ => "off"
    };

    /// <summary>Путь к очищенному списку (его читают zapret и sing-box).</summary>
    public static string ListPath(BlockedListPreset preset) =>
        Path.Combine(ListsDirectory, preset == BlockedListPreset.Full ? "rkn-full.txt" : "russia-inside.txt");

    private static string RawPath(BlockedListPreset preset) =>
        Path.Combine(ListsDirectory, preset == BlockedListPreset.Full ? "rkn-full.raw.txt" : "russia-inside.raw.txt");

    private static string ListsDirectory
    {
        get
        {
            string dir = Path.Combine(AppPaths.DataDirectory, "lists");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string UrlFor(BlockedListPreset preset) =>
        preset == BlockedListPreset.Full ? FullUrl : StandardUrl;

    /// <summary>
    /// Убедиться, что локальная копия списка есть и не старше недели.
    /// Ошибки сети не считаются фатальными: вернётся старая копия с текстом ошибки.
    /// </summary>
    public static async Task<BlockedListInfo> EnsureAsync(
        BlockedListPreset preset,
        bool force = false,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (preset == BlockedListPreset.Off)
            return new BlockedListInfo(preset, "", 0, null, false, null);

        string listPath = ListPath(preset);
        string rawPath = RawPath(preset);

        bool fresh = File.Exists(listPath) &&
                     DateTime.UtcNow - File.GetLastWriteTimeUtc(listPath) < RefreshAfter;
        if (fresh && !force)
            return Inspect(preset, listPath, fromCache: true, error: null);

        string? error = null;
        try
        {
            log?.Invoke($"Загрузка списка «{DisplayName(preset)}»…");
            using var response = await Http.GetAsync(UrlFor(preset), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string tempRaw = rawPath + ".tmp";
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(tempRaw))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);

            File.Move(tempRaw, rawPath, overwrite: true);

            int count = SanitizeFile(rawPath, listPath);
            if (count == 0) throw new InvalidOperationException("список пуст после очистки");

            log?.Invoke($"Список «{DisplayName(preset)}»: {count:N0} доменов");
            return Inspect(preset, listPath, fromCache: false, error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log?.Invoke($"Не удалось обновить список «{DisplayName(preset)}»: {ex.Message}");

            // Сеть недоступна, но прошлая копия есть — работаем на ней.
            if (File.Exists(listPath))
                return Inspect(preset, listPath, fromCache: true, error);

            // Совсем нет копии, но есть сырой файл — попробуем очистить его.
            if (File.Exists(rawPath))
            {
                try
                {
                    int count = SanitizeFile(rawPath, listPath);
                    if (count > 0) return Inspect(preset, listPath, fromCache: true, error);
                }
                catch { }
            }

            return new BlockedListInfo(preset, listPath, 0, null, false, error);
        }
    }

    private static BlockedListInfo Inspect(BlockedListPreset preset, string path, bool fromCache, string? error)
    {
        int count = 0;
        try
        {
            // Считаем строки потоково: файл полного реестра ~30 МБ.
            using var reader = new StreamReader(path, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
                if (line.Length > 0) count++;
        }
        catch (Exception ex)
        {
            return new BlockedListInfo(preset, path, 0, null, fromCache, ex.Message);
        }

        return new BlockedListInfo(preset, path, count, File.GetLastWriteTimeUtc(path).ToLocalTime(), fromCache, error);
    }

    /// <summary>Прочитать очищенный список построчно (для sing-box).</summary>
    public static List<string> ReadDomains(BlockedListPreset preset, CancellationToken ct = default)
    {
        var result = new List<string>();
        string path = ListPath(preset);
        if (preset == BlockedListPreset.Off || !File.Exists(path)) return result;

        using var reader = new StreamReader(path, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length > 0) result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Несколько доменов из готового списка для замера DPI, когда своих доменов
    /// нет. Сначала ищем популярные заблокированные сервисы: на них видно,
    /// помогает ли стратегия. Если их нет — берём первые домены списка.
    /// </summary>
    public static IReadOnlyList<string> Sample(BlockedListPreset preset, int count = 4, CancellationToken ct = default)
    {
        if (preset == BlockedListPreset.Off || count <= 0) return Array.Empty<string>();
        return SampleFile(ListPath(preset), count, ct);
    }

    /// <summary>Выборка доменов из указанного файла (для тестов и повторного использования).</summary>
    public static IReadOnlyList<string> SampleFile(string path, int count = 4, CancellationToken ct = default)
    {
        if (count <= 0 || !File.Exists(path)) return Array.Empty<string>();

        var popular = new List<string>(count);
        var first = new List<string>(count);

        using var reader = new StreamReader(path, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;

            if (first.Count < count) first.Add(line);

            if (ProbeCandidates.Contains(line)
                && !popular.Contains(line, StringComparer.OrdinalIgnoreCase))
            {
                popular.Add(line);
                if (popular.Count >= count) break;
            }
        }

        return popular.Count > 0 ? popular : first;
    }

    private static readonly HashSet<string> ProbeCandidates = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "discord.com",
        "instagram.com",
        "facebook.com",
        "x.com",
        "twitter.com",
        "telegram.org",
        "openai.com",
        "twitch.tv",
        "spotify.com",
        "steamcommunity.com"
    };

    /// <summary>Очистить сырой список в файл; вернуть количество доменов.</summary>
    public static int SanitizeFile(string sourcePath, string targetPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string temp = targetPath + ".tmp";

        using (var reader = new StreamReader(sourcePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            while (reader.ReadLine() is { } line)
            {
                string? domain = NormalizeDomain(line);
                if (domain is null || !seen.Add(domain)) continue;
                writer.WriteLine(domain);
            }
        }

        File.Move(temp, targetPath, overwrite: true);
        return seen.Count;
    }

    /// <summary>Очистить домены (для тестов и для слияния со списком пользователя).</summary>
    public static IReadOnlyList<string> Sanitize(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string line in lines)
        {
            string? domain = NormalizeDomain(line);
            if (domain is null || !seen.Add(domain)) continue;
            result.Add(domain);
        }
        return result;
    }

    private static readonly Regex DomainPattern = new(
        @"^(?:[a-z0-9](?:[a-z0-9\-_]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9\-]{0,61}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Привести строку списка к домену: убрать комментарии, кавычки, «*.», «||»,
    /// завершающие точки и слэши. Не-домены (IP, мусор) отбрасываются.
    /// </summary>
    public static string? NormalizeDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string s = raw.Trim().Trim('\uFEFF');
        if (s.Length == 0 || s[0] is '#' or '!' or ';') return null;

        int comment = s.IndexOfAny(['#', ' ', '\t']);
        if (comment >= 0) s = s[..comment];

        s = s.Trim().Trim('"', '\'', ',');
        s = s.TrimStart('|', '.', '*', '/');
        s = s.TrimEnd('.', '/', '^', '|');
        if (s.Length == 0) return null;

        // Adblock-формат: ||example.com^
        if (s.StartsWith("||", StringComparison.Ordinal)) s = s[2..];
        if (s.Contains('/') || s.Contains(':')) return null;

        s = s.ToLowerInvariant();
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        if (s.Length < 4 || s.Length > 253) return null;

        // Числовые «домены» — это IP-адреса, для hostlist/domain_suffix они не нужны.
        if (s.Split('.').All(part => part.All(char.IsDigit))) return null;

        return DomainPattern.IsMatch(s) ? s : null;
    }

    /// <summary>Добавить список в набор доменов пользователя без дублей.</summary>
    public static List<string> Merge(IEnumerable<string> userDomains, IEnumerable<string> presetDomains)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string domain in userDomains)
        {
            string? normalized = NormalizeDomain(domain);
            if (normalized is null || !seen.Add(normalized)) continue;
            result.Add(normalized);
        }
        foreach (string domain in presetDomains)
        {
            if (domain.Length == 0 || !seen.Add(domain)) continue;
            result.Add(domain);
        }
        return result;
    }
}

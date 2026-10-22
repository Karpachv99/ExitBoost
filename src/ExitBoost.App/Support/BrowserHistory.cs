using System.Diagnostics;
using System.IO;
using System.Net;
using Microsoft.Data.Sqlite;

namespace ExitBoost.App.Support;

/// <summary>
/// Чтение доменов из истории браузеров (Chrome/Edge/Brave/Vivaldi/Opera/Яндекс — Chromium,
/// Firefox — places.sqlite). Файл истории заблокирован запущенным браузером,
/// поэтому сначала копируется во временный файл.
/// </summary>
public static class BrowserHistory
{
    public sealed record BrowserInfo(string Key, string Name, string HistoryPath, bool IsFirefox);

    /// <summary>Список всех известных браузеров с путями к истории.</summary>
    public static IReadOnlyList<BrowserInfo> Known =>
    [
        new("chrome", "Chrome",
            Combine("Google", "Chrome", true), false),
        new("msedge", "Microsoft Edge",
            Combine("Microsoft", "Edge", true), false),
        new("yandex", "Яндекс Браузер",
            Combine("Yandex", "YandexBrowser", true), false),
        new("brave", "Brave",
            Combine("BraveSoftware", "Brave-Browser", true), false),
        new("vivaldi", "Vivaldi",
            Combine("Vivaldi", "Vivaldi", true), false),
        new("opera", "Opera",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Opera Software", "Opera Stable", "History"), false),
        new("firefox", "Firefox",
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appdata
                ? Path.Combine(appdata, "Mozilla", "Firefox", "Profiles")
                : "", true)
    ];

    private static string Combine(string dir1, string dir2, bool userDataPattern)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = Path.Combine(local, dir1, dir2, "User Data");
        return userDataPattern ? Path.Combine(root, "Default", "History") : root;
    }

    /// <summary>Какие браузеры сейчас запущены (по имени процесса).</summary>
    public static IReadOnlyList<BrowserInfo> DetectRunning()
    {
        var result = new List<BrowserInfo>();
        foreach (var b in Known)
        {
            if (IsRunning(b)) result.Add(b);
        }
        return result;
    }

    private static bool IsRunning(BrowserInfo b) => b.Key switch
    {
        "yandex" => HasProcess("yandex") || HasProcessWhere(p => p.Equals("browser", StringComparison.OrdinalIgnoreCase)) && YandexDirExists(),
        "firefox" => HasProcess("firefox"),
        _ => HasProcess(b.Key)
    };

    private static bool YandexDirExists() => Directory.Exists(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yandex", "YandexBrowser"));

    private static bool HasProcess(string name) =>
        Process.GetProcessesByName(name).Length > 0;

    private static bool HasProcessWhere(Func<string, bool> predicate)
    {
        try
        {
            return Process.GetProcesses().Any(p => predicate(p.ProcessName));
        }
        catch { return false; }
    }

    /// <summary>Прочитать домены из истории браузера: самые посещаемые сверху.</summary>
    public static List<HistoryDomain> LoadDomains(BrowserInfo browser)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastSeen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var dbPath in ResolveHistoryFiles(browser))
        {
            try
            {
                using var temp = CopyToTemp(dbPath);
                foreach (var (host, visits, ts) in QueryHosts(temp.Path, browser.IsFirefox))
                {
                    if (!LooksBrowsable(host)) continue;
                    counts[host] = counts.TryGetValue(host, out int c) ? c + visits : visits;
                    if (!lastSeen.TryGetValue(host, out long old) || ts > old) lastSeen[host] = ts;
                }
            }
            catch
            {
                // Профиль может отсутствовать или быть нечитаемым — пропускаем.
            }
        }

        return counts
            .Select(kv => new HistoryDomain(kv.Key, kv.Value, lastSeen.TryGetValue(kv.Key, out long t) ? t : 0))
            .OrderByDescending(d => d.Visits)
            .ThenByDescending(d => d.LastSeen)
            .Take(80)
            .ToList();
    }

    public sealed record HistoryDomain(string Host, int Visits, long LastSeen);

    private static List<string> ResolveHistoryFiles(BrowserInfo browser)
    {
        if (!browser.IsFirefox)
        {
            if (File.Exists(browser.HistoryPath)) return [browser.HistoryPath];

            // Произвольные профили Chromium: User Data\Profile */History
            var list = new List<string>();
            var root = Path.GetDirectoryName(Path.GetDirectoryName(browser.HistoryPath));
            if (root is not null && Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root, "Profile *"))
                {
                    string h = Path.Combine(dir, "History");
                    if (File.Exists(h)) list.Add(h);
                }
            }
            return list;
        }

        // Firefox: Profiles\*default*\places.sqlite
        var found = new List<string>();
        if (!string.IsNullOrEmpty(browser.HistoryPath) && Directory.Exists(browser.HistoryPath))
        {
            foreach (var f in Directory.GetFiles(browser.HistoryPath, "places.sqlite"))
                found.Add(f);
        }
        return found;
    }

    private sealed class TempCopy : IDisposable
    {
        public TempCopy(string path) => Path = path;
        public string Path { get; }
        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }

    private static TempCopy CopyToTemp(string source)
    {
        string temp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "exboost-" + Guid.NewGuid().ToString("N")[..8] + ".sqlite");
        using (var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write))
            src.CopyTo(dst);
        return new TempCopy(temp);
    }

    private static IEnumerable<(string Host, int Visits, long Timestamp)> QueryHosts(string dbPath, bool firefox)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        };
        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        string urlColumn = firefox ? "url" : "url";
        string table = firefox ? "moz_places" : "urls";
        string order = firefox ? "last_visit_date" : "last_visit_time";
        string visits = firefox ? "visit_count" : "visit_count";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {urlColumn}, {visits}, {order} FROM {table} ORDER BY {order} DESC LIMIT 500";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string? url = reader.IsDBNull(0) ? null : reader.GetString(0);
            int visitsCount = reader.IsDBNull(1) ? 1 : reader.GetInt64(1) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(1);
            long ts = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);

            string? host = ExtractHost(url);
            if (host is null) continue;

            yield return (host, Math.Max(1, visitsCount), ts);
        }
    }

    private static string? ExtractHost(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.HostNameType switch
        {
            UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6 => uri.Host,
            _ => null
        };
    }

    private static bool LooksBrowsable(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.Contains("127.0.0.1")) return false;

        // Локальные адреса семейств: 192.168.x.x, 10.x.x.x, 172.16-31.x.x
        if (IPAddress.TryParse(host, out _) && !host.StartsWith("100.6", StringComparison.Ordinal))
        {
            // Локальные подсети — «не в интересах» обхода DPI.
            if (host.StartsWith("192.168.") || host.StartsWith("10.") || host.StartsWith("172."))
                return false;
        }

        if (!host.Contains('.')) return false; // «about», «file»
        return true;
    }
}

using System.Text.Json;
using System.Net;

namespace ExitBoost.Core.Updates;

/// <summary>Информация о доступном обновлении.</summary>
public sealed record UpdateInfo(
    string Version,
    string Tag,
    string PageUrl,
    string Notes,
    string AssetName,
    string AssetUrl,
    long AssetSize)
{
    /// <summary>Размер файла в мегабайтах (для показа пользователю).</summary>
    public string SizeText => AssetSize <= 0
        ? "—"
        : (AssetSize / 1024.0 / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " МБ";
}

/// <summary>
/// Проверка обновлений через GitLab Releases API.
/// Пользователь задаёт репозиторий в формате «владелец/имя» в настройках.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExitBoost/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        return http;
    }

    /// <summary>Разбор «owner/repo». Возвращает false, если строка не похожа на репозиторий.</summary>
    public static bool TryParseRepo(string? text, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        string value = text.Trim().Trim('/');
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = value["https://".Length..];
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            value = value["http://".Length..];
        if (value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            value = value["github.com/".Length..];
        if (value.StartsWith("gitlab.com/", StringComparison.OrdinalIgnoreCase))
            value = value["gitlab.com/".Length..];

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        owner = parts[0];
        repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        return owner.Length > 0 && repo.Length > 0;
    }

    /// <summary>Проверить наличие новой версии. null — обновлений нет или проверка не удалась.</summary>
    public static async Task<UpdateInfo?> CheckAsync(
        string owner,
        string repo,
        string currentVersion,
        CancellationToken ct = default)
    {
        // Используем официальный GitLab API; cache-bust обходит устаревшие ответы прокси.
        string projectPath = Uri.EscapeDataString($"{owner}/{repo}");
        string cacheBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string url = $"https://gitlab.com/api/v4/projects/{projectPath}/releases/permalink/latest?exitboost={cacheBust}";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        if (tag.Length == 0) return null;

        string version = tag.TrimStart('v', 'V');
        if (!IsNewer(version, currentVersion)) return null;

        string notes = "";
        if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String)
            notes = bodyEl.GetString() ?? "";

        string pageUrl = $"https://gitlab.com/{owner}/{repo}/-/releases/{Uri.EscapeDataString(tag)}";

        string assetName = "", assetUrl = "";
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assetsEl)
            && assetsEl.TryGetProperty("links", out var linksEl)
            && linksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in linksEl.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                assetUrl = asset.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string fileName;
                try { fileName = Path.GetFileName(new Uri(assetUrl).AbsolutePath); }
                catch { continue; }
                if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                // CLI-архив тоже zip — для обновления нужен именно архив программы.
                if (name.Contains("cli", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("cli", StringComparison.OrdinalIgnoreCase)) continue;
                assetName = fileName;
                break;
            }
        }

        // GitLab release links do not expose file size. A release note may embed
        // the expected ZIP size so interrupted downloads are detected exactly.
        if (root.TryGetProperty("description", out var descriptionEl)
            && descriptionEl.ValueKind == JsonValueKind.String)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                descriptionEl.GetString() ?? "", "exitboost-asset-size=(\\d+)");
            if (match.Success) long.TryParse(match.Groups[1].Value, out assetSize);
        }

        if (assetUrl.Length == 0) return null;

        return new UpdateInfo(version, tag, pageUrl, notes, assetName, assetUrl, assetSize);
    }

    /// <summary>Сравнение версий «1.2.3» и «1.10.0» (без учёта суффиксов).</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = Parse(candidate);
        var b = Parse(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    private static int[] Parse(string version)
    {
        string[] parts = (version ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = int.TryParse(new string(parts[i].SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray()), out int v) ? v : 0;
        return result;
    }
}

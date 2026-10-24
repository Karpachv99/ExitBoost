using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ExitBoost.Core.Updates;

/// <summary>
/// Проверка обновлений: основной источник — GitLab Releases, резервный — GitHub.
/// Сначала читается строгий манифест update.json; если его нет — используется
/// старый формат с ZIP-ассетом и размером из release notes.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly Regex LegacySize = new(@"exitboost-asset-size=(\d+)", RegexOptions.Compiled);
    private static readonly Regex LegacySha = new(@"exitboost-asset-sha256=([0-9a-fA-F]{64})", RegexOptions.Compiled);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15)
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

        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        owner = parts[0];
        repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        return owner.Length > 0 && repo.Length > 0;
    }

    /// <summary>
    /// Проверить наличие новой версии. Сначала GitLab; если он недоступен
    /// (сеть, блокировка, ошибка API), проверяется зеркало GitHub.
    /// Ошибка обоих источников возвращается как <see cref="UpdateCheckStatus.Failed"/>,
    /// а не маскируется под «обновлений нет».
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        string owner,
        string repo,
        string currentVersion,
        string channel = "stable",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return UpdateCheckResult.Failed("не задан репозиторий обновлений");

        UpdateCheckResult gitlab = await CheckGitLabAsync(owner, repo, currentVersion, channel, ct)
            .ConfigureAwait(false);
        if (gitlab.Status != UpdateCheckStatus.Failed)
            return gitlab;

        UpdateCheckResult github = await CheckGitHubAsync(owner, repo, currentVersion, channel, ct)
            .ConfigureAwait(false);
        if (github.Status != UpdateCheckStatus.Failed)
            return github;

        return UpdateCheckResult.Failed(
            $"GitLab: {gitlab.Error}; зеркало GitHub: {github.Error}");
    }

    private static async Task<UpdateCheckResult> CheckGitLabAsync(
        string owner,
        string repo,
        string currentVersion,
        string channel,
        CancellationToken ct)
    {
        try
        {
            string projectPath = Uri.EscapeDataString($"{owner}/{repo}");
            string cacheBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string url = $"https://gitlab.com/api/v4/projects/{projectPath}/releases/permalink/latest?exitboost={cacheBust}";

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                return UpdateCheckResult.Failed(
                    $"GitLab вернул {(int)response.StatusCode} {response.ReasonPhrase}{detail}");
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            GitLabRelease? release;
            try
            {
                release = JsonSerializer.Deserialize<GitLabRelease>(json);
            }
            catch (JsonException ex)
            {
                return UpdateCheckResult.Failed("некорректный ответ GitLab: " + ex.Message);
            }

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return UpdateCheckResult.Failed("в ответе GitLab нет тега релиза");

            string tag = release.TagName.Trim();
            string releaseVersion = UpdateVersion.Normalize(tag);
            if (!UpdateVersion.IsValid(releaseVersion))
                return UpdateCheckResult.Failed($"не удалось разобрать версию из тега «{tag}»");

            string pageUrl = $"https://gitlab.com/{owner}/{repo}/-/releases/{Uri.EscapeDataString(tag)}";
            string notes = release.Description ?? "";
            var links = release.Assets?.Links ?? new List<GitLabAssetLink>();

            GitLabAssetLink? manifestLink = links.FirstOrDefault(IsManifestLink);
            if (manifestLink is not null)
            {
                return await ReadManifestAsync(
                    manifestLink.Url, releaseVersion, pageUrl, currentVersion, channel,
                    UpdateSource.Manifest, ct).ConfigureAwait(false);
            }

            // Переходный fallback: релиз без манифеста (например, 1.0.16).
            return BuildLegacyResult(links, releaseVersion, tag, pageUrl, notes, currentVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Failed("таймаут проверки обновлений GitLab");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failed("ошибка сети GitLab: " + ex.Message);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed("не удалось проверить обновления GitLab: " + ex.Message);
        }
    }

    /// <summary>
    /// Проверка зеркала GitHub без обращения к GitLab. Сначала пробуем прямой
    /// адрес последнего манифеста (без API-лимитов), затем GitHub API — он нужен
    /// для старых релизов без update.json.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckGitHubAsync(
        string owner,
        string repo,
        string currentVersion,
        string channel = "stable",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return UpdateCheckResult.Failed("не задан репозиторий зеркала");

        UpdateCheckResult direct = await CheckGitHubDirectAsync(owner, repo, currentVersion, channel, ct)
            .ConfigureAwait(false);
        if (direct.Status != UpdateCheckStatus.Failed)
            return direct;

        UpdateCheckResult api = await CheckGitHubApiAsync(owner, repo, currentVersion, channel, ct)
            .ConfigureAwait(false);
        if (api.Status != UpdateCheckStatus.Failed)
            return api;

        return UpdateCheckResult.Failed($"{direct.Error}; API GitHub: {api.Error}");
    }

    private static async Task<UpdateCheckResult> CheckGitHubDirectAsync(
        string owner,
        string repo,
        string currentVersion,
        string channel,
        CancellationToken ct)
    {
        try
        {
            string url =
                $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest/download/update.json";
            (UpdateManifest? manifest, string error) = await DownloadManifestAsync(url, ct).ConfigureAwait(false);
            if (manifest is null)
                return UpdateCheckResult.Failed(error);

            string releaseVersion = manifest.Tag.Length > 0 ? manifest.Tag : "v" + manifest.Version;
            string pageUrl =
                $"https://github.com/{owner}/{repo}/releases/tag/{Uri.EscapeDataString(releaseVersion)}";
            return BuildManifestResult(
                manifest, releaseVersion, pageUrl, currentVersion, channel, url,
                UpdateSource.GitHubManifest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Failed("таймаут проверки зеркала GitHub");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failed("ошибка сети GitHub: " + ex.Message);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed("не удалось проверить зеркало GitHub: " + ex.Message);
        }
    }

    private static async Task<UpdateCheckResult> CheckGitHubApiAsync(
        string owner,
        string repo,
        string currentVersion,
        string channel,
        CancellationToken ct)
    {
        try
        {
            string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                return UpdateCheckResult.Failed(
                    $"GitHub вернул {(int)response.StatusCode} {response.ReasonPhrase}{detail}");
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            GitHubRelease? release;
            try
            {
                release = JsonSerializer.Deserialize<GitHubRelease>(json);
            }
            catch (JsonException ex)
            {
                return UpdateCheckResult.Failed("некорректный ответ GitHub: " + ex.Message);
            }

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return UpdateCheckResult.Failed("в ответе GitHub нет тега релиза");

            string tag = release.TagName.Trim();
            string releaseVersion = UpdateVersion.Normalize(tag);
            if (!UpdateVersion.IsValid(releaseVersion))
                return UpdateCheckResult.Failed($"не удалось разобрать версию из тега «{tag}»");

            string pageUrl = $"https://github.com/{owner}/{repo}/releases/tag/{Uri.EscapeDataString(tag)}";
            string notes = release.Body ?? "";
            var links = (release.Assets ?? new List<GitHubAsset>())
                .Select(asset => new GitLabAssetLink
                {
                    Name = asset.Name ?? "",
                    Url = asset.BrowserDownloadUrl ?? "",
                    LinkType = "package"
                })
                .ToList();

            GitLabAssetLink? manifestLink = links.FirstOrDefault(IsManifestLink);
            if (manifestLink is not null)
            {
                return await ReadManifestAsync(
                    manifestLink.Url, releaseVersion, pageUrl, currentVersion, channel,
                    UpdateSource.GitHubManifest, ct).ConfigureAwait(false);
            }

            return BuildLegacyResult(links, releaseVersion, tag, pageUrl, notes, currentVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Failed("таймаут проверки GitHub API");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failed("ошибка сети GitHub API: " + ex.Message);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed("не удалось проверить GitHub API: " + ex.Message);
        }
    }

    private static async Task<UpdateCheckResult> ReadManifestAsync(
        string manifestUrl,
        string releaseVersion,
        string releasePageUrl,
        string currentVersion,
        string channel,
        UpdateSource source,
        CancellationToken ct)
    {
        (UpdateManifest? manifest, string error) = await DownloadManifestAsync(manifestUrl, ct).ConfigureAwait(false);
        if (manifest is null)
            return UpdateCheckResult.Failed(error);

        return BuildManifestResult(
            manifest, releaseVersion, releasePageUrl, currentVersion, channel, manifestUrl, source);
    }

    private static async Task<(UpdateManifest? Manifest, string Error)> DownloadManifestAsync(
        string manifestUrl,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? manifestUri)
            || !IsAllowedReleaseUri(manifestUri))
        {
            return (null, "ссылка на манифест должна вести на https://gitlab.com или https://github.com");
        }

        using var response = await Http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await ReadErrorAsync(response, ct).ConfigureAwait(false);
            return (null, $"не удалось скачать манифест: HTTP {(int)response.StatusCode}{detail}");
        }

        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!UpdateManifest.TryParse(json, out UpdateManifest manifest, out string parseError))
            return (null, "манифест обновления некорректен: " + parseError);

        return (manifest, "");
    }

    private static UpdateCheckResult BuildManifestResult(
        UpdateManifest manifest,
        string releaseVersion,
        string releasePageUrl,
        string currentVersion,
        string channel,
        string manifestUrl,
        UpdateSource source)
    {
        if (!manifest.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCheckResult.Failed(
                $"последний релиз относится к каналу «{manifest.Channel}», а вы на канале «{channel}»");
        }

        if (!UpdateVersion.IsNewer(manifest.Version, currentVersion))
            return UpdateCheckResult.UpToDate();

        bool tooOld = manifest.MinVersion.Length > 0
                       && UpdateVersion.IsNewer(manifest.MinVersion, currentVersion);

        string pageUrl = Uri.TryCreate(manifest.NotesUrl, UriKind.Absolute, out Uri? notesUri)
                         && notesUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? manifest.NotesUrl
            : releasePageUrl;

        var info = new UpdateInfo
        {
            Version = manifest.Version,
            Tag = manifest.Tag.Length > 0 ? manifest.Tag : "v" + manifest.Version,
            PageUrl = pageUrl,
            Notes = manifest.Notes,
            AssetName = manifest.Asset!.Name,
            AssetUrl = manifest.Asset.Url,
            AssetSize = manifest.Asset.Size,
            Sha256 = manifest.Asset.Sha256.ToLowerInvariant(),
            Channel = manifest.Channel.ToLowerInvariant(),
            Mandatory = manifest.Mandatory || tooOld,
            MinVersion = manifest.MinVersion,
            ManifestUrl = manifestUrl,
            Source = source
        };

        // Тег релиза и версия манифеста должны совпадать — иначе легко
        // опубликовать манифест от другой сборки.
        if (!UpdateVersion.Normalize(releaseVersion).Equals(UpdateVersion.Normalize(info.Version), StringComparison.Ordinal))
        {
            return UpdateCheckResult.Failed(
                $"версия в манифесте ({info.Version}) не совпадает с тегом релиза ({releaseVersion})");
        }

        return UpdateCheckResult.Available(info);
    }

    private static UpdateCheckResult BuildLegacyResult(
        IReadOnlyList<GitLabAssetLink> links,
        string releaseVersion,
        string tag,
        string pageUrl,
        string notes,
        string currentVersion)
    {
        if (!UpdateVersion.IsNewer(releaseVersion, currentVersion))
            return UpdateCheckResult.UpToDate();

        GitLabAssetLink? zipLink = links
            .Where(IsApplicationZip)
            .OrderByDescending(ScoreApplicationZip)
            .FirstOrDefault();

        if (zipLink is null)
            return UpdateCheckResult.Failed("в релизе нет ZIP-архива приложения");

        if (!Uri.TryCreate(zipLink.Url, UriKind.Absolute, out Uri? uri)
            || !IsAllowedReleaseUri(uri))
        {
            return UpdateCheckResult.Failed("ссылка на архив ведёт на неизвестный хост");
        }

        string fileName = Path.GetFileName(uri.AbsolutePath);
        if (fileName.Length == 0) fileName = zipLink.Name;

        long size = 0;
        Match sizeMatch = LegacySize.Match(notes);
        if (sizeMatch.Success) long.TryParse(sizeMatch.Groups[1].Value, out size);

        string sha = "";
        Match shaMatch = LegacySha.Match(notes);
        if (shaMatch.Success) sha = shaMatch.Groups[1].Value.ToLowerInvariant();

        var info = new UpdateInfo
        {
            Version = releaseVersion,
            Tag = tag,
            PageUrl = pageUrl,
            Notes = notes,
            AssetName = fileName,
            AssetUrl = zipLink.Url,
            AssetSize = size,
            Sha256 = sha,
            Channel = "stable",
            ManifestUrl = "",
            Source = UpdateSource.LegacyRelease
        };
        return UpdateCheckResult.Available(info);
    }

    private static bool IsManifestLink(GitLabAssetLink link)
    {
        if (link.Name.Equals("update.json", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(link.Url, UriKind.Absolute, out Uri? uri)
               && uri.AbsolutePath.EndsWith("/update.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApplicationZip(GitLabAssetLink link)
    {
        string name = link.Name.Length > 0 ? link.Name : SafeFileName(link.Url);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("cli", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("symbols", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static int ScoreApplicationZip(GitLabAssetLink link)
    {
        string name = link.Name.Length > 0 ? link.Name : SafeFileName(link.Url);
        int score = 0;
        if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) score += 10;
        if (name.StartsWith("ExitBoost", StringComparison.OrdinalIgnoreCase)) score += 5;
        if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) score -= 20;
        return score;
    }

    private static bool IsAllowedReleaseUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        string host = uri.Host;
        return host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".gitlab.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFileName(string url)
    {
        try { return Path.GetFileName(new Uri(url).AbsolutePath); }
        catch { return ""; }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            string body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (body.Length == 0) return "";
            if (body.Length > 240) body = body[..240] + "…";
            return ": " + body.Replace('\n', ' ').Replace('\r', ' ');
        }
        catch
        {
            return "";
        }
    }

    private sealed class GitLabRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("assets")] public GitLabAssets? Assets { get; set; }
    }

    private sealed class GitLabAssets
    {
        [JsonPropertyName("links")] public List<GitLabAssetLink>? Links { get; set; }
    }

    private sealed class GitLabAssetLink
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("link_type")] public string LinkType { get; set; } = "other";
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExitBoost.Core.Updates;

/// <summary>
/// Манифест обновления (update.json), который публикуется вместе с релизом.
/// Именно он, а не порядок ссылок в релизе, определяет файл и его контрольную сумму.
/// </summary>
public sealed class UpdateManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public int Schema { get; init; }
    public string Channel { get; init; } = "stable";
    public string Version { get; init; } = "";
    public string Tag { get; init; } = "";
    public DateTimeOffset? ReleasedAt { get; init; }
    public string MinVersion { get; init; } = "";
    public string Notes { get; init; } = "";
    public string NotesUrl { get; init; } = "";
    public bool Mandatory { get; init; }
    public UpdateAsset? Asset { get; init; }

    /// <summary>Зарезервировано под подпись манифеста; сейчас не проверяется.</summary>
    public string Signature { get; init; } = "";

    public static bool TryParse(string json, out UpdateManifest manifest, out string error)
    {
        manifest = new UpdateManifest();
        error = "";

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "пустой манифест";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
            if (parsed is null)
            {
                error = "манифест не разобран";
                return false;
            }

            // Нормализуем строки: в JSON может оказаться null, и любая
            // проверка ниже не должна падать с NullReferenceException.
            manifest = new UpdateManifest
            {
                Schema = parsed.Schema,
                Channel = (parsed.Channel ?? "").Trim().ToLowerInvariant(),
                Version = (parsed.Version ?? "").Trim(),
                Tag = (parsed.Tag ?? "").Trim(),
                ReleasedAt = parsed.ReleasedAt,
                MinVersion = (parsed.MinVersion ?? "").Trim(),
                Notes = parsed.Notes ?? "",
                NotesUrl = (parsed.NotesUrl ?? "").Trim(),
                Mandatory = parsed.Mandatory,
                Signature = parsed.Signature ?? "",
                Asset = parsed.Asset is null
                    ? null
                    : new UpdateAsset
                    {
                        Name = (parsed.Asset.Name ?? "").Trim(),
                        Url = (parsed.Asset.Url ?? "").Trim(),
                        Size = parsed.Asset.Size,
                        Sha256 = (parsed.Asset.Sha256 ?? "").Trim().ToLowerInvariant()
                    }
            };
        }
        catch (JsonException ex)
        {
            error = "некорректный JSON: " + ex.Message;
            return false;
        }

        if (manifest.Schema != 1)
        {
            error = $"неподдерживаемая схема манифеста: {manifest.Schema}";
            return false;
        }

        string channel = (manifest.Channel ?? "").Trim();
        if (channel.Length == 0 || !(channel.Equals("stable", StringComparison.OrdinalIgnoreCase)
                                     || channel.Equals("beta", StringComparison.OrdinalIgnoreCase)))
        {
            error = "неизвестный канал обновлений";
            return false;
        }

        if (!UpdateVersion.IsValid(manifest.Version))
        {
            error = "некорректная версия в манифесте";
            return false;
        }

        if (manifest.MinVersion.Length > 0 && !UpdateVersion.IsValid(manifest.MinVersion))
        {
            error = "некорректная минимальная версия в манифесте";
            return false;
        }

        if (manifest.Asset is null)
        {
            error = "в манифесте нет описания файла";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Asset.Name)
            || !manifest.Asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            error = "в манифесте указан не ZIP-архив";
            return false;
        }

        if (!Uri.TryCreate(manifest.Asset.Url, UriKind.Absolute, out Uri? assetUri)
            || !IsAllowedAssetUri(assetUri))
        {
            error = "ссылка на архив должна вести на https://gitlab.com или https://github.com";
            return false;
        }

        if (manifest.Asset.Size <= 0)
        {
            error = "в манифесте не указан размер архива";
            return false;
        }

        if (manifest.Asset.Sha256.Length != 64
            || !manifest.Asset.Sha256.All(c => char.IsAsciiHexDigit(c)))
        {
            error = "в манифесте нет корректной SHA-256";
            return false;
        }

        return true;
    }

    private static bool IsAllowedAssetUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".gitlab.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class UpdateAsset
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}

/// <summary>Сериализация манифеста — нужна скриптам публикации и тестам.</summary>
public static class UpdateManifestJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(UpdateManifest manifest)
        => JsonSerializer.Serialize(manifest, Options);
}

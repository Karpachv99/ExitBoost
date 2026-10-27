namespace ExitBoost.Core.Updates;

/// <summary>Откуда взята информация об обновлении.</summary>
public enum UpdateSource
{
    /// <summary>Строгий манифест update.json с размером и SHA-256.</summary>
    Manifest,

    /// <summary>Строгий манифест update.json, полученный с GitHub.</summary>
    GitHubManifest,

    /// <summary>Старый формат: ZIP-ассет релиза и размер из release notes.</summary>
    LegacyRelease
}

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    Failed
}

/// <summary>Проверенное описание доступного обновления.</summary>
public sealed record UpdateInfo
{
    public string Version { get; init; } = "";
    public string Tag { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string Notes { get; init; } = "";
    public string AssetName { get; init; } = "";
    public string AssetUrl { get; init; } = "";
    public long AssetSize { get; init; }
    public string Sha256 { get; init; } = "";
    public string Channel { get; init; } = "stable";
    public bool Mandatory { get; init; }
    public string MinVersion { get; init; } = "";
    public string ManifestUrl { get; init; } = "";
    public UpdateSource Source { get; init; }

    /// <summary>Есть ли контрольная сумма для проверки целостности.</summary>
    public bool HasHash => Sha256.Length == 64;

    /// <summary>Размер файла в мегабайтах (для показа пользователю).</summary>
    public string SizeText => AssetSize <= 0
        ? "—"
        : (AssetSize / 1024.0 / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " МБ";
}

/// <summary>Результат проверки обновлений: успех, «нет обновлений» или конкретная ошибка.</summary>
public sealed record UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public UpdateInfo? Update { get; init; }
    public string Error { get; init; } = "";

    public static UpdateCheckResult UpToDate() => new() { Status = UpdateCheckStatus.UpToDate };
    public static UpdateCheckResult Available(UpdateInfo info) => new() { Status = UpdateCheckStatus.Available, Update = info };
    public static UpdateCheckResult Failed(string error) => new() { Status = UpdateCheckStatus.Failed, Error = error };
}

/// <summary>Прогресс скачивания обновления.</summary>
public sealed record UpdateProgress(string Phase, long Received, long Total, double Fraction);

/// <summary>Скачанный и проверенный архив обновления.</summary>
public sealed record DownloadedUpdate(string Path, long Size, string Sha256);

/// <summary>Результат работы updater-а, который читается при следующем запуске приложения.</summary>
public sealed record UpdateApplyResult
{
    public string Status { get; init; } = "";
    public string Version { get; init; } = "";
    public string Error { get; init; } = "";
    public DateTimeOffset? CompletedAt { get; init; }
    public string LogPath { get; init; } = "";
    public string BackupPath { get; init; } = "";

    public bool IsSuccess => Status.Equals("success", StringComparison.OrdinalIgnoreCase);
}

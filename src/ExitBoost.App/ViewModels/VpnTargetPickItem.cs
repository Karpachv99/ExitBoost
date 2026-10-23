namespace ExitBoost.App.ViewModels;

/// <summary>
/// Строка выпадающего списка «программы и сайты через VPN»:
/// запущенная программа (добавляется имя процесса) или сайт (добавляется домен).
/// </summary>
public sealed class VpnTargetPickItem
{
    /// <summary>true — сайт, false — запущенная программа.</summary>
    public bool IsSite { get; init; }

    /// <summary>Главный текст строки: имя процесса или домен.</summary>
    public string Title { get; init; } = "";

    /// <summary>Подпись под главным текстом.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Что добавляется в список: «discord.exe» или «discord.com».</summary>
    public string Value { get; init; } = "";

    /// <summary>Группа в выпадающем списке: «Программы» или «Сайты».</summary>
    public string Group => IsSite ? "Сайты" : "Программы";
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace ExitBoost.Core.Updates;

/// <summary>Сравнение версий вида «1.2.3» без внешних зависимостей.</summary>
public static class UpdateVersion
{
    private static readonly Regex Strict = new(@"^\d+(\.\d+){0,3}$", RegexOptions.Compiled);

    /// <summary>Убирает ведущие «v», пробелы и суффикс после «-»/«+».</summary>
    public static string Normalize(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        int cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) text = text[..cut];
        return text;
    }

    /// <summary>Строгая проверка: только цифры и точки, не больше четырёх компонентов.</summary>
    public static bool IsValid(string? value) => Strict.IsMatch(Normalize(value));

    /// <summary>Сравнение версий «1.2.3» и «1.10.0».</summary>
    public static bool IsNewer(string candidate, string current)
    {
        int[] a = Parse(candidate);
        int[] b = Parse(current);
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
        string normalized = Normalize(version);
        string[] parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            result[i] = int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
        }
        return result;
    }
}

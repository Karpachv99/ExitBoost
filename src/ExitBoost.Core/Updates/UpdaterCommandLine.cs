using System.Globalization;
using System.Text;

namespace ExitBoost.Core.Updates;

/// <summary>Сборка командной строки для ExitBoost.Updater.exe.</summary>
public static class UpdaterCommandLine
{
    public static string Build(
        int processId,
        string zipPath,
        string sha256,
        long size,
        string installDirectory,
        string exePath,
        string version,
        string resultPath,
        string logPath)
    {
        return string.Join(' ',
            "--pid", processId.ToString(CultureInfo.InvariantCulture),
            "--zip", Quote(zipPath),
            "--sha256", Quote(sha256),
            "--size", size.ToString(CultureInfo.InvariantCulture),
            "--install", Quote(installDirectory),
            "--exe", Quote(exePath),
            "--version", Quote(version),
            "--result", Quote(resultPath),
            "--log", Quote(logPath));
    }

    /// <summary>Квотирование аргумента по правилам CommandLineToArgvW.</summary>
    public static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;

        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }
            sb.Append(c);
        }

        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}

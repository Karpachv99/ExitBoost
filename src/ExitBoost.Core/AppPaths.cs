namespace ExitBoost.Core;

/// <summary>Стандартные пути приложения.</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ExitBoost");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string DefaultNodesFile => Path.Combine(DataDirectory, "nodes.json");

    public static string LogFile => Path.Combine(DataDirectory, "exitboost.log");

    public static string CacheDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

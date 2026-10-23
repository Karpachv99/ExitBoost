using System.Text.Json;
using System.Text.Json.Serialization;
using ExitBoost.Core.Models;

namespace ExitBoost.Core.Configuration;

/// <summary>Хранилище пользовательских настроек (JSON в %APPDATA%\ExitBoost).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    private AppSettings _settings = new();

    public AppSettings Current => _settings;

    public event Action<AppSettings>? Changed;

    public void Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                _settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_settings, Options));
        }
        catch
        {
            // Настройки не критичны — молча продолжаем работу.
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        mutate(_settings);
        Save();
        Changed?.Invoke(_settings);
    }
}

using System.Text.Json;
using ExitBoost.Core.Routing;

namespace ExitBoost.Core.Networking;

/// <summary>
/// Сохраняет информацию об установленных туннелях, чтобы снять их
/// после аварийного завершения приложения.
/// </summary>
public sealed class NetworkStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(AppPaths.DataDirectory, "network_state.json");

    /// <summary>Журнал диагностики.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Сохранённое состояние сети.</summary>
    public sealed record SavedState
    {
        public DefaultRouteInfo? Route { get; init; }
        public DateTime AtUtc { get; init; }
        public List<string> TunnelNames { get; init; } = new();

        public static SavedState Create(DefaultRouteInfo? route, IEnumerable<string>? tunnels = null) =>
            new()
            {
                Route = route,
                AtUtc = DateTime.UtcNow,
                TunnelNames = tunnels?.ToList() ?? new List<string>()
            };
    }

    public void Save(DefaultRouteInfo? route, List<string>? tunnelNames = null)
    {
        try
        {
            var state = SavedState.Create(route, tunnelNames);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось сохранить состояние сети: {ex.Message}");
        }
    }

    /// <summary>Запомнить имя установленного туннеля (на случай аварийного завершения).</summary>
    public void RememberTunnel(string tunnelName)
    {
        try
        {
            var current = Load();
            var names = current?.TunnelNames ?? new List<string>();
            if (!names.Contains(tunnelName)) names.Add(tunnelName);
            Save(current?.Route, names);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось сохранить информацию о туннеле: {ex.Message}");
        }
    }

    public SavedState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<SavedState>(json, Options);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось прочитать состояние сети: {ex.Message}");
            return null;
        }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}

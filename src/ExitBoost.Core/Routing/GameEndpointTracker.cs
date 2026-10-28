using System.Net;
using System.Net.Sockets;

namespace ExitBoost.Core.Routing;

/// <summary>
/// Tracks recently observed IPv4 game-server endpoints. Short-lived connections
/// remain routable briefly, while old servers eventually leave the route set.
/// </summary>
public sealed class GameEndpointTracker
{
    private readonly TimeSpan _retention;
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public GameEndpointTracker(TimeSpan? retention = null)
    {
        _retention = retention ?? TimeSpan.FromMinutes(10);
        if (_retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
    }

    public IReadOnlyCollection<string> Observe(IEnumerable<string> endpoints, DateTime nowUtc)
    {
        foreach (var value in endpoints)
            if (TryNormalizeIpv4(value, out var address))
                _lastSeen[address] = nowUtc;

        Prune(nowUtc);
        return Snapshot();
    }

    public IReadOnlyCollection<string> Snapshot(DateTime nowUtc)
    {
        Prune(nowUtc);
        return Snapshot();
    }

    public void Clear() => _lastSeen.Clear();

    public static bool TryNormalizeIpv4(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) ||
            IPAddress.IsLoopback(address) || address.GetAddressBytes()[0] >= 224)
            return false;

        normalized = address.ToString();
        return true;
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var stale in _lastSeen
            .Where(pair => nowUtc - pair.Value > _retention)
            .Select(pair => pair.Key)
            .ToList())
            _lastSeen.Remove(stale);
    }

    private IReadOnlyCollection<string> Snapshot() =>
        _lastSeen.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
}

using ExitBoost.Core.Routing;
using Xunit;

namespace ExitBoost.Tests;

public class GameEndpointTrackerTests
{
    [Fact]
    public void Observe_NormalizesAndDeduplicatesIpv4Endpoints()
    {
        var tracker = new GameEndpointTracker();
        var now = DateTime.UtcNow;

        var endpoints = tracker.Observe(new[] { "203.0.113.7", "203.0.113.7", "not-an-ip", "::1" }, now);

        Assert.Equal(new[] { "203.0.113.7" }, endpoints);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("not-an-ip")]
    public void TryNormalizeIpv4_RejectsNonRemoteOrInvalidAddresses(string address)
    {
        Assert.False(GameEndpointTracker.TryNormalizeIpv4(address, out _));
    }

    [Fact]
    public void Snapshot_ExpiresEndpointsAfterRetention()
    {
        var tracker = new GameEndpointTracker(TimeSpan.FromMinutes(2));
        var start = DateTime.UtcNow;
        tracker.Observe(new[] { "203.0.113.7" }, start);
        tracker.Observe(new[] { "198.51.100.12" }, start.AddMinutes(1));

        var snapshot = tracker.Snapshot(start.AddMinutes(2).AddSeconds(1));

        Assert.DoesNotContain("203.0.113.7", snapshot);
        Assert.Contains("198.51.100.12", snapshot);
    }

    [Fact]
    public void Observe_RefreshesRetentionForStillActiveEndpoint()
    {
        var tracker = new GameEndpointTracker(TimeSpan.FromMinutes(2));
        var start = DateTime.UtcNow;
        tracker.Observe(new[] { "203.0.113.7" }, start);
        tracker.Observe(new[] { "203.0.113.7" }, start.AddMinutes(1));

        var snapshot = tracker.Snapshot(start.AddMinutes(2).AddSeconds(1));

        Assert.Contains("203.0.113.7", snapshot);
    }
}

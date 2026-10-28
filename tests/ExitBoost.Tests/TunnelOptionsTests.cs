using ExitBoost.Core.Models;
using ExitBoost.Core.Tunnel;

namespace ExitBoost.Tests;

public class TunnelOptionsTests
{
    private static NodeConfig CreateNode() => new()
    {
        PrivateKey = "private-key",
        PublicKey = "public-key",
        Host = "203.0.113.10",
        Port = 51820
    };

    [Theory]
    [InlineData(RoutingMode.ProcessSplit)]
    [InlineData(RoutingMode.NetworkSplit)]
    public void Empty_Split_Routes_DoNotBecomeFullTunnel(RoutingMode mode)
    {
        var options = new TunnelOptions { Mode = mode };

        Assert.Equal("", options.AllowedIpsString);
        Assert.DoesNotContain("AllowedIPs =", WireGuardConfigBuilder.Build(CreateNode(), options));
        Assert.DoesNotContain("AllowedIPs =", WireGuardConfigBuilder.BuildSyncConf(CreateNode(), options));
    }

    [Fact]
    public void FullTunnel_RemainsExplicitWhenRouteListIsEmpty()
    {
        var options = new TunnelOptions { Mode = RoutingMode.FullTunnel };

        Assert.Equal("0.0.0.0/0", options.AllowedIpsString);
        Assert.Contains("AllowedIPs = 0.0.0.0/0", WireGuardConfigBuilder.Build(CreateNode(), options));
        Assert.Contains("AllowedIPs = 0.0.0.0/0", WireGuardConfigBuilder.BuildSyncConf(CreateNode(), options));
    }
}

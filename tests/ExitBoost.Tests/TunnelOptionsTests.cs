using ExitBoost.Core.Models;
using ExitBoost.Core.Tunnel;

namespace ExitBoost.Tests;

public class TunnelOptionsTests
{
    [Fact]
    public void Empty_ProcessSplit_DoesNotBecomeFullTunnel()
    {
        var options = new TunnelOptions { Mode = RoutingMode.ProcessSplit };

        Assert.Equal("", options.AllowedIpsString);
    }

    [Fact]
    public void FullTunnel_RemainsExplicitWhenRouteListIsEmpty()
    {
        var options = new TunnelOptions { Mode = RoutingMode.FullTunnel };

        Assert.Equal("0.0.0.0/0", options.AllowedIpsString);
    }
}

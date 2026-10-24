using ExitBoost.Core.Dpi;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>Цель DPI: готовый список без своих доменов должен всё равно давать домены для замера.</summary>
public class DpiTargetTests
{
    [Fact]
    public void ProbeHostList_использует_свои_домены()
    {
        var target = new DpiTarget
        {
            Hosts = "youtube.com, discord.com",
            ProbeHosts = ["example.com"]
        };

        Assert.Equal(["youtube.com", "discord.com"], target.ProbeHostList);
    }

    [Fact]
    public void ProbeHostList_берёт_выборку_из_готового_списка()
    {
        var target = new DpiTarget
        {
            ProbeHosts = ["youtube.com", "discord.com"]
        };

        Assert.Equal(["youtube.com", "discord.com"], target.ProbeHostList);
    }

    [Fact]
    public void IsValid_принимает_готовый_список_без_своих_доменов()
    {
        var target = new DpiTarget();
        target.ExtraHostListPaths.Add(@"C:\list.txt");

        Assert.True(target.IsValid);
        Assert.Empty(target.HostList);
    }
}

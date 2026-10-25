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

    [Fact]
    public void IsValid_принимает_игровые_порты_без_доменов()
    {
        var target = new DpiTarget { GameTcpPorts = "6112" };

        Assert.True(target.IsValid);
        Assert.True(target.IsGameMode);
    }

    [Fact]
    public void GameIps_отбрасывает_мусор_и_повторы()
    {
        var target = new DpiTarget
        {
            GameIps = ["1.2.3.4", " 1.2.3.4 ", "не-ip", "5.6.7.0/24", "", "1.2.3.4/128"]
        };

        Assert.Equal(["1.2.3.4", "5.6.7.0/24"], target.GameIpList);
    }

    [Fact]
    public void TcpPorts_объединяет_основной_порт_и_порты_игры()
    {
        var target = new DpiTarget { Port = 443, GameTcpPorts = "6112, 27015-27020" };

        Assert.Equal("443,6112,27015-27020", target.TcpPorts);
    }

    [Theory]
    [InlineData("6112", "6112")]
    [InlineData("6112, 27015-27020", "6112,27015-27020")]
    [InlineData("0, 70000, 443", "443")]
    [InlineData("abc;443;443", "443")]
    [InlineData("", "")]
    public void DpiPorts_нормализует_список(string input, string expected)
    {
        Assert.Equal(expected, DpiPorts.Merge(input));
    }
}

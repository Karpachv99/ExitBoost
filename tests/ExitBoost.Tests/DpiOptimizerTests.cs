using ExitBoost.Core.Dpi;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>
/// Автоподбор: стратегия должна поднимать именно заблокированные домены.
/// Открывающиеся без обхода домены ничего не проверяют и отбрасываются.
/// </summary>
public class DpiOptimizerTests
{
    [Fact]
    public void SelectBlockedHosts_оставляет_только_неработающие_домены()
    {
        var probes = new[]
        {
            new DpiHostProbe("youtube.com", false, 0, 0, "таймаут"),
            new DpiHostProbe("example.com", true, 20, 45, null),
            new DpiHostProbe("discord.com", false, 0, 0, "сброс")
        };

        var blocked = DpiOptimizer.SelectBlockedHosts(probes);

        Assert.Equal(["youtube.com", "discord.com"], blocked);
    }

    [Fact]
    public void SelectBlockedHosts_пусто_когда_всё_открывается()
    {
        var probes = new[]
        {
            new DpiHostProbe("youtube.com", true, 20, 45, null),
            new DpiHostProbe("discord.com", true, 22, 50, null)
        };

        Assert.Empty(DpiOptimizer.SelectBlockedHosts(probes));
    }

    [Fact]
    public void SelectBlockedHosts_сохраняет_порядок_замеров()
    {
        var probes = new[]
        {
            new DpiHostProbe("first.com", false, 0, 0, null),
            new DpiHostProbe("second.com", false, 0, 0, null),
            new DpiHostProbe("third.com", false, 0, 0, null)
        };

        Assert.Equal(["first.com", "second.com", "third.com"], DpiOptimizer.SelectBlockedHosts(probes));
    }
}

using ExitBoost.Core.Diagnostics;
using ExitBoost.Core.Models;
using Xunit;

namespace ExitBoost.Tests;

public class NodeSelectorTests
{
    private static NodeStatus Make(string name, double ping, double loss, double jitter, int priority = 0) => new()
    {
        Config = new NodeConfig { Id = name, Name = name, Priority = priority },
        AvgPingMs = ping,
        LossPercent = loss,
        JitterMs = jitter,
        SampleCount = 10,
        LastPingMs = ping
    };

    [Fact]
    public void Низкие_потери_важнее_меньшей_задержки()
    {
        var fast = Make("fast", 20, 25, 1);
        var stable = Make("stable", 40, 0, 2);

        var best = NodeSelector.SelectBest(new[] { fast, stable });

        Assert.NotNull(best);
        Assert.Equal("stable", best!.Config.Name);
    }

    [Fact]
    public void Мёртвый_узел_всегда_в_конце_рейтинга()
    {
        var dead = Make("dead", 5, 100, 0);
        var alive = Make("alive", 90, 0, 5);

        var best = NodeSelector.SelectBest(new[] { dead, alive });

        Assert.NotNull(best);
        Assert.Equal("alive", best!.Config.Name);
    }

    [Fact]
    public void Балл_растёт_с_потерями()
    {
        var clean = NodeSelector.ComputeScore(Make("a", 30, 0, 2));
        var lossy = NodeSelector.ComputeScore(Make("b", 30, 10, 2));

        Assert.True(lossy > clean);
    }

    [Fact]
    public void Приоритет_сдвигает_узел_в_рейтинге()
    {
        var plain = Make("plain", 50, 0, 1);
        var preferred = Make("preferred", 50, 0, 1, priority: 20);

        var ranked = NodeSelector.Rank(new[] { plain, preferred });

        Assert.Equal("preferred", ranked[0].Config.Name);
    }

    [Fact]
    public void Выключенные_узлы_не_выбираются()
    {
        var disabled = Make("off", 10, 0, 0);
        disabled.Config.Enabled = false;
        var enabled = Make("on", 80, 0, 0);

        var best = NodeSelector.SelectBest(new[] { disabled, enabled });

        Assert.NotNull(best);
        Assert.Equal("on", best!.Config.Name);
    }
}

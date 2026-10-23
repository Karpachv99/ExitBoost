using ExitBoost.Core.Diagnostics;
using Xunit;

namespace ExitBoost.Tests;

public class PingProberTests
{
    [Fact]
    public void Считаются_потери_и_джиттер()
    {
        var prober = new PingProber(window: 10);

        prober.Record(true, 20);
        prober.Record(true, 30);
        prober.Record(false, 0);
        prober.Record(true, 25);

        var m = prober.Compute();

        Assert.Equal(4, m.Samples);
        Assert.Equal(1, m.Failures);
        Assert.Equal(25, m.LossPercent, 1);
        Assert.Equal(25, m.AvgMs, 1);
        Assert.Equal(20, m.MinMs);
        Assert.Equal(30, m.MaxMs);
        // джиттер = (|30-20| + |25-30|) / 2 = 7.5
        Assert.Equal(7.5, m.JitterMs, 1);
    }

    [Fact]
    public void Окно_измерений_ограничено()
    {
        var prober = new PingProber(window: 4);
        for (int i = 0; i < 20; i++) prober.Record(true, 10 + i);

        Assert.Equal(4, prober.Compute().Samples);
    }

    [Fact]
    public void Пустое_окно_даёт_нулевые_метрики()
    {
        var m = new PingProber().Compute();

        Assert.Equal(0, m.Samples);
        Assert.Equal(0, m.LossPercent);
    }
}

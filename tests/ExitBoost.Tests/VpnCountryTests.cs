using ExitBoost.Core.Vpn;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>Страна и качество VPN-ключей — как у узлов ретранслятора.</summary>
public class VpnCountryTests
{
    private static string Flag(string code) => VpnCountry.FlagFromCode(code);

    [Fact]
    public void Country_From_Flag()
    {
        Assert.Equal("Германия", VpnCountry.Guess(Flag("DE") + " Германия | RAW TCP"));
        Assert.Equal("Россия", VpnCountry.Guess(Flag("RU") + " Россия | HYSTERIA"));
        Assert.Equal("Финляндия", VpnCountry.Guess(Flag("FI") + " RAW TCP | 🎮 | 1"));
    }

    [Fact]
    public void Country_From_Name_Without_Flag()
    {
        Assert.Equal("Германия", VpnCountry.Guess("Германия premium"));
        Assert.Equal("Нидерланды", VpnCountry.Guess("node Нидерланды 2"));
    }

    [Fact]
    public void Country_Unknown_Is_Dash()
    {
        Assert.Equal("—", VpnCountry.Guess("⬇️ Игровые сервера ⬇️"));
        Assert.Equal("—", VpnCountry.Guess(""));
        Assert.Equal("—", VpnCountry.Guess(null));
    }

    [Fact]
    public void Metrics_Quality_Text()
    {
        var good = new VpnKeyMetrics { AvgMs = 60, LossPercent = 0, Samples = 4 };
        var bad = new VpnKeyMetrics { AvgMs = 900, LossPercent = 95, Samples = 4 };
        var none = new VpnKeyMetrics();

        Assert.Equal("отлично", good.QualityText);
        Assert.Equal("недоступен", bad.QualityText);
        Assert.Equal("—", none.QualityText);
    }
}

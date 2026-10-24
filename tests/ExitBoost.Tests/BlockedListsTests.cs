using ExitBoost.Core.Lists;
using Xunit;

namespace ExitBoost.Tests;

public class BlockedListsTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM  ", "example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData(".example.com", "example.com")]
    [InlineData("||example.com^", "example.com")]
    [InlineData("\"example.com", "example.com")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("example.com # комментарий", "example.com")]
    public void NormalizeDomain_приводит_строки_к_домену(string raw, string expected) =>
        Assert.Equal(expected, BlockedLists.NormalizeDomain(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment")]
    [InlineData("! adblock comment")]
    [InlineData("1.2.3.4")]
    [InlineData("http://example.com/path")]
    [InlineData("example")]
    [InlineData("плохой домен.рф")]
    public void NormalizeDomain_отбрасывает_мусор(string raw) =>
        Assert.Null(BlockedLists.NormalizeDomain(raw));

    [Fact]
    public void Sanitize_убирает_дубликаты_без_учёта_регистра()
    {
        var result = BlockedLists.Sanitize(["example.com", "EXAMPLE.com", "*.example.com", "other.org"]);

        Assert.Equal(["example.com", "other.org"], result);
    }

    [Fact]
    public void Sanitize_файла_пишет_очищенный_список()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eb-lists-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string raw = Path.Combine(dir, "raw.txt");
            string clean = Path.Combine(dir, "clean.txt");
            File.WriteAllLines(raw, ["# comment", "Example.com", "*.example.com", "1.2.3.4", "other.org"]);

            int count = BlockedLists.SanitizeFile(raw, clean);

            Assert.Equal(2, count);
            Assert.Equal(["example.com", "other.org"], File.ReadAllLines(clean));
            Assert.False(File.Exists(clean + ".tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Merge_сохраняет_порядок_и_не_дублирует()
    {
        var result = BlockedLists.Merge(
            ["YouTube.com", "discord.com"],
            ["youtube.com", "discord.gg", "discord.com"]);

        Assert.Equal(["youtube.com", "discord.com", "discord.gg"], result);
    }

    [Theory]
    [InlineData("off", BlockedListPreset.Off)]
    [InlineData("standard", BlockedListPreset.Standard)]
    [InlineData("FULL", BlockedListPreset.Full)]
    [InlineData("", BlockedListPreset.Off)]
    [InlineData("что-то", BlockedListPreset.Off)]
    public void Parse_читает_настройку(string value, BlockedListPreset expected) =>
        Assert.Equal(expected, BlockedLists.Parse(value));

    [Fact]
    public void ToSetting_обратим()
    {
        foreach (var preset in Enum.GetValues<BlockedListPreset>())
            Assert.Equal(preset, BlockedLists.Parse(BlockedLists.ToSetting(preset)));
    }

    [Fact]
    public void ReadDomains_возвращает_пусто_для_выключенного_списка() =>
        Assert.Empty(BlockedLists.ReadDomains(BlockedListPreset.Off));
}

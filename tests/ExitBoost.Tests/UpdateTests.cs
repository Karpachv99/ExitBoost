using ExitBoost.Core.Updates;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>Проверка обновлений: версии, разбор адреса репозитория, скрипт установки.</summary>
public class UpdateTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("2.0", "1.99.99", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0.0", "1.0", false)]
    [InlineData("v1.2.0", "1.1.0", true)]
    public void Version_Compare(string candidate, string current, bool expected)
        => Assert.Equal(expected, UpdateChecker.IsNewer(candidate, current));

    [Theory]
    [InlineData("owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/", "owner", "repo")]
    [InlineData("github.com/owner/repo.git", "owner", "repo")]
    [InlineData("  owner/repo  ", "owner", "repo")]
    public void Repo_Parse(string text, string owner, string repo)
    {
        Assert.True(UpdateChecker.TryParseRepo(text, out string actualOwner, out string actualRepo));
        Assert.Equal(owner, actualOwner);
        Assert.Equal(repo, actualRepo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("owner")]
    [InlineData("https://github.com/owner")]
    public void Repo_Parse_Invalid(string text)
        => Assert.False(UpdateChecker.TryParseRepo(text, out _, out _));

    [Fact]
    public void Install_Script_Quotes_Paths()
    {
        string script = UpdateApplier.BuildScript(
            @"C:\Temp\it's update.zip",
            @"C:\Program Files\ExitBoost",
            @"C:\Program Files\ExitBoost\ExitBoost.exe",
            1234);

        Assert.Contains("'C:\\Temp\\it''s update.zip'", script);
        Assert.Contains("'C:\\Program Files\\ExitBoost'", script);
        Assert.Contains("WaitForExit", script);
        Assert.Contains("Start-Process -FilePath 'C:\\Program Files\\ExitBoost\\ExitBoost.exe'", script);
    }

    [Fact]
    public void Update_Info_Size_Text()
    {
        var info = new UpdateInfo("1.2.3", "v1.2.3", "https://example", "", "app.zip", "https://example/app.zip", 5 * 1024 * 1024);
        Assert.Equal("5.0 МБ", info.SizeText);
        Assert.Equal("—", (info with { AssetSize = 0 }).SizeText);
    }
}

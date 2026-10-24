using ExitBoost.Core.Updates;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>Проверка обновлений: версии, манифест, разбиение на части и аргументы updater-а.</summary>
public class UpdateTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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
        => Assert.Equal(expected, UpdateVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.2.3-beta", true)]
    [InlineData("1.2", true)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1..3", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void Version_Validate(string value, bool expected)
        => Assert.Equal(expected, UpdateVersion.IsValid(value));

    [Theory]
    [InlineData("owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("https://gitlab.com/owner/repo/", "owner", "repo")]
    [InlineData("gitlab.com/owner/repo.git", "owner", "repo")]
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
    public void Planner_Covers_Whole_File()
    {
        // Регрессия: для 65 605 833 байт старый код скачивал только 25 165 824 байта.
        const long total = 65_605_833;
        var parts = UpdatePlanner.BuildParts(total);

        Assert.InRange(parts.Count, 2, UpdatePlanner.DefaultMaxParts);
        Assert.Equal(0, parts[0].From);
        Assert.Equal(total - 1, parts[^1].To);
        Assert.Equal(total, parts.Sum(p => p.Length));

        for (int i = 1; i < parts.Count; i++)
        {
            Assert.Equal(parts[i - 1].To + 1, parts[i].From);
            Assert.True(parts[i].Length > 0);
        }
    }

    [Fact]
    public void Planner_Small_File_Is_Single_Part()
    {
        var parts = UpdatePlanner.BuildParts(10);
        Assert.Single(parts);
        Assert.Equal(0, parts[0].From);
        Assert.Equal(9, parts[0].To);
    }

    [Fact]
    public void Planner_Limits_Part_Count()
    {
        var parts = UpdatePlanner.BuildParts(512L * 1024 * 1024);
        Assert.Equal(UpdatePlanner.DefaultMaxParts, parts.Count);
        Assert.Equal(512L * 1024 * 1024, parts.Sum(p => p.Length));
    }

    [Fact]
    public void Manifest_Parses_Valid_Document()
    {
        Assert.True(UpdateManifest.TryParse(ValidManifest(assetUrl: "https://gitlab.com/x/y/-/raw/v1.0.17/app.zip"),
            out UpdateManifest manifest, out string error), error);

        Assert.Equal("1.0.17", manifest.Version);
        Assert.Equal("stable", manifest.Channel);
        Assert.NotNull(manifest.Asset);
        Assert.Equal(65_605_833, manifest.Asset!.Size);
        Assert.Equal(ValidSha, manifest.Asset.Sha256);
    }

    [Theory]
    [InlineData("http://gitlab.com/x/y/app.zip")]
    [InlineData("https://evil.example/app.zip")]
    public void Manifest_Rejects_Foreign_Asset_Host(string url)
    {
        Assert.False(UpdateManifest.TryParse(ValidManifest(assetUrl: url), out _, out string error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Manifest_Rejects_Wrong_Schema()
    {
        string json = ValidManifest().Replace("\"schema\": 1", "\"schema\": 2");
        Assert.False(UpdateManifest.TryParse(json, out _, out _));
    }

    [Fact]
    public void Manifest_Rejects_Bad_Hash()
    {
        string json = ValidManifest().Replace(ValidSha, "xyz");
        Assert.False(UpdateManifest.TryParse(json, out _, out _));
    }

    [Fact]
    public void Manifest_Rejects_Non_Zip_Asset()
    {
        string json = ValidManifest().Replace("app.zip", "app.exe");
        Assert.False(UpdateManifest.TryParse(json, out _, out _));
    }

    [Fact]
    public void Manifest_Serializes_CamelCase()
    {
        Assert.True(UpdateManifest.TryParse(ValidManifest(), out UpdateManifest manifest, out _));
        string json = UpdateManifestJson.Serialize(manifest);

        Assert.Contains("\"version\"", json);
        Assert.Contains("\"sha256\"", json);
        Assert.Contains("\"asset\"", json);
    }

    [Fact]
    public void CommandLine_Quotes_Paths_And_Arguments()
    {
        string args = UpdaterCommandLine.Build(
            1234,
            @"C:\Temp\it's update.zip",
            ValidSha,
            5_000_000,
            @"C:\Program Files\ExitBoost",
            @"C:\Program Files\ExitBoost\ExitBoost.exe",
            "1.0.17",
            @"C:\Users\me\AppData\Local\ExitBoost\updates\last-result.json",
            @"C:\Users\me\AppData\Local\ExitBoost\logs\update.log");

        Assert.Contains("--pid 1234", args);
        Assert.Contains("--zip \"C:\\Temp\\it's update.zip\"", args);
        Assert.Contains(@"""C:\Program Files\ExitBoost""", args);
        Assert.Contains("--sha256", args);
        Assert.Contains("--size 5000000", args);
        Assert.Contains("--version", args);
    }

    [Fact]
    public void CommandLine_Quotes_Embedded_Quote()
    {
        Assert.Equal("\"a\\\"b\"", UpdaterCommandLine.Quote("a\"b"));
    }

    [Fact]
    public void Update_Info_Size_Text()
    {
        var info = new UpdateInfo { AssetName = "app.zip", AssetUrl = "https://gitlab.com/app.zip", AssetSize = 5 * 1024 * 1024 };
        Assert.Equal("5.0 МБ", info.SizeText);
        Assert.Equal("—", (info with { AssetSize = 0 }).SizeText);
    }

    [Fact]
    public void Update_Info_Has_Hash_Only_For_Complete_Sha()
    {
        Assert.True(new UpdateInfo { Sha256 = ValidSha }.HasHash);
        Assert.False(new UpdateInfo { Sha256 = "" }.HasHash);
        Assert.False(new UpdateInfo { Sha256 = "abc" }.HasHash);
    }

    private static string ValidManifest(string assetUrl = "https://gitlab.com/x/y/-/raw/v1.0.17/app.zip") => $$"""
        {
          "schema": 1,
          "channel": "stable",
          "version": "1.0.17",
          "tag": "v1.0.17",
          "minVersion": "1.0.17",
          "notes": "Надёжные обновления",
          "asset": {
            "name": "app.zip",
            "url": "{{assetUrl}}",
            "size": 65605833,
            "sha256": "{{ValidSha}}"
          }
        }
        """;
}

using ExitBoost.Core.Dpi;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>
/// Командная строка winws: готовый список должен попадать и в TCP-профиль,
/// и в подстановку %HOSTLIST% (UDP/QUIC), иначе QUIC остаётся без обхода.
/// </summary>
public class ZapretRunnerTests
{
    private static DpiStrategy UdpStrategy() => new()
    {
        Id = "test",
        Name = "тест",
        Args = ["--filter-udp=443", "--hostlist=%HOSTLIST%", "--dpi-desync=fake"]
    };

    [Fact]
    public void BuildArguments_добавляет_готовый_список_в_tcp_профиль()
    {
        var target = new DpiTarget { Hosts = "youtube.com" };
        target.ExtraHostListPaths.Add(@"C:\lists\russia-inside.txt");

        string args = ZapretRunner.BuildArguments(UdpStrategy(), target, @"C:\zapret\hostlist.txt");

        Assert.Contains(@"--hostlist=""C:\zapret\hostlist.txt""", args);
        Assert.Contains(@"--hostlist=""C:\lists\russia-inside.txt""", args);
    }

    [Fact]
    public void BuildArguments_подставляет_все_списки_в_HOSTLIST()
    {
        var target = new DpiTarget { Hosts = "youtube.com" };
        target.ExtraHostListPaths.Add(@"C:\lists\russia-inside.txt");
        target.ExtraHostListPaths.Add(@"C:\lists\extra.txt");

        string args = ZapretRunner.BuildArguments(UdpStrategy(), target, @"C:\zapret\hostlist.txt");

        // В UDP-профиле %HOSTLIST% должен развернуться в свои домены и оба списка.
        Assert.Contains(
            @"--hostlist=""C:\zapret\hostlist.txt"" --hostlist=""C:\lists\russia-inside.txt"" --hostlist=""C:\lists\extra.txt""",
            args);
        Assert.DoesNotContain("%HOSTLIST%", args);
    }

    [Fact]
    public void BuildArguments_без_готового_списка_оставляет_только_свои_домены()
    {
        var target = new DpiTarget { Hosts = "youtube.com" };

        string args = ZapretRunner.BuildArguments(UdpStrategy(), target, @"C:\zapret\hostlist.txt");

        Assert.Contains(@"--hostlist=""C:\zapret\hostlist.txt""", args);
        Assert.DoesNotContain("%HOSTLIST%", args);
        Assert.Equal(2, args.Split("--hostlist=").Length - 1);
    }

    [Fact]
    public void BuildArguments_подставляет_папку_движка_и_fakes()
    {
        var strategy = new DpiStrategy
        {
            Args = ["--dpi-desync-split-seqovl-pattern=%FAKES%tls.bin", "--lua-init=%ENGINE%\\x.lua"]
        };
        var target = new DpiTarget { Hosts = "youtube.com" };

        string args = ZapretRunner.BuildArguments(
            strategy, target, @"C:\zapret\hostlist.txt", @"C:\zapret\zapret-winws");

        Assert.Contains(@"C:\zapret\fakes\tls.bin", args);
        Assert.Contains(@"C:\zapret\zapret-winws\x.lua", args);
    }

    [Fact]
    public void BuildArguments_завершает_набор_параметром_new()
    {
        var target = new DpiTarget { Hosts = "youtube.com" };

        string args = ZapretRunner.BuildArguments(UdpStrategy(), target, @"C:\zapret\hostlist.txt");

        Assert.EndsWith("--new", args);
    }

    [Fact]
    public void BuildArguments_добавляет_порты_игры_в_перехват()
    {
        var target = new DpiTarget
        {
            Hosts = "pathofexile.com",
            GameTcpPorts = "6112",
            GameUdpPorts = "3074"
        };

        string args = ZapretRunner.BuildArguments(UdpStrategy(), target, @"C:\zapret\hostlist.txt");

        Assert.Contains("--wf-tcp=80,443,6112", args);
        Assert.Contains("--wf-udp=3074", args);
    }

    [Fact]
    public void BuildArguments_игровая_стратегия_подставляет_порты_и_ipset()
    {
        var strategy = new DpiStrategy
        {
            Id = "game",
            IsGameTcp = true,
            Args = ["--filter-tcp=%GAMETCPPORTS%", "--ipset=%IPSET%", "--dpi-desync=fake,fakedsplit"]
        };
        var target = new DpiTarget { GameTcpPorts = "6112", GameIps = ["1.2.3.4"] };

        string args = ZapretRunner.BuildArguments(
            strategy, target, @"C:\zapret\hostlist.txt", @"C:\zapret\zapret-winws", @"C:\zapret\ipset.txt");

        Assert.Contains("--filter-tcp=6112", args);
        Assert.Contains(@"--ipset=""C:\zapret\ipset.txt""", args);
        Assert.DoesNotContain("%GAMETCPPORTS%", args);
        Assert.DoesNotContain("%IPSET%", args);

        // Сайтовый профиль (443 + hostlist) в игру не подмешивается.
        Assert.DoesNotContain("--hostlist", args);
        Assert.DoesNotContain("--filter-tcp=443", args);
    }

    [Fact]
    public void BuildArguments_без_адресов_убирает_профиль_с_ipset()
    {
        var strategy = new DpiStrategy
        {
            Id = "game",
            IsGameTcp = true,
            Args = ["--filter-tcp=%GAMETCPPORTS%", "--ipset=%IPSET%", "--dpi-desync=fake"]
        };
        var target = new DpiTarget { GameTcpPorts = "6112" };

        string args = ZapretRunner.BuildArguments(strategy, target, @"C:\zapret\hostlist.txt");

        Assert.DoesNotContain("--ipset", args);
        Assert.Contains("--filter-tcp=6112", args);
    }

    [Fact]
    public void Validate_требует_порты_для_игровой_стратегии()
    {
        var tcp = new DpiStrategy { IsGameTcp = true };
        var udp = new DpiStrategy { IsGameUdp = true };
        var site = new DpiStrategy();

        Assert.NotNull(tcp.Validate(new DpiTarget { Hosts = "youtube.com" }));
        Assert.NotNull(udp.Validate(new DpiTarget { GameTcpPorts = "6112" }));
        Assert.Null(tcp.Validate(new DpiTarget { GameTcpPorts = "6112" }));
        Assert.Null(udp.Validate(new DpiTarget { GameUdpPorts = "3074" }));
        Assert.Null(site.Validate(new DpiTarget { Hosts = "youtube.com" }));
    }

    [Fact]
    public void BuildArguments_не_дублирует_wf_udp_из_стратегии()
    {
        var strategy = new DpiStrategy
        {
            Args = ["--wf-udp=443", "--filter-udp=443", "--dpi-desync=fake"]
        };
        var target = new DpiTarget { Hosts = "youtube.com", GameUdpPorts = "3074" };

        string args = ZapretRunner.BuildArguments(strategy, target, @"C:\zapret\hostlist.txt");

        Assert.Contains("--wf-udp=3074,443", args);
        Assert.Equal(1, args.Split("--wf-udp=").Length - 1);
    }

    [Fact]
    public void Каталог_содержит_игровые_стратегии()
    {
        var catalog = ZapretCatalog.BuildDefault();

        Assert.Contains(catalog.Strategies, s => s.Id == "game_tcp_fake" && s.IsGameTcp);
        Assert.Contains(catalog.Strategies, s => s.Id == "game_udp_fake" && s.IsGameUdp);
    }
}

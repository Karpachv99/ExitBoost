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
}

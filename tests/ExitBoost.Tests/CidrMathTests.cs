using ExitBoost.Core.Networking;
using Xunit;

namespace ExitBoost.Tests;

public class CidrMathTests
{
    [Fact]
    public void Частные_подсети_исключаются_из_публичного_пространства()
    {
        var publicOnly = CidrMath.PublicOnly();

        Assert.DoesNotContain(publicOnly, c => CidrMath.Contains(c, "10.1.2.3"));
        Assert.DoesNotContain(publicOnly, c => CidrMath.Contains(c, "192.168.1.10"));
        Assert.DoesNotContain(publicOnly, c => CidrMath.Contains(c, "172.16.5.5"));
    }

    [Fact]
    public void Публичные_адреса_остаются_в_результате()
    {
        var publicOnly = CidrMath.PublicOnly();

        Assert.Contains(publicOnly, c => CidrMath.Contains(c, "1.1.1.1"));
        Assert.Contains(publicOnly, c => CidrMath.Contains(c, "8.8.8.8"));
        Assert.Contains(publicOnly, c => CidrMath.Contains(c, "185.60.216.10"));
    }

    [Fact]
    public void Результат_покрывает_публичное_пространство_целиком()
    {
        var publicOnly = CidrMath.PublicOnly();

        foreach (var ip in new[] { "1.0.0.1", "9.9.9.9", "223.255.255.255" })
            Assert.True(publicOnly.Any(c => CidrMath.Contains(c, ip)), $"Адрес {ip} не покрыт");
    }

    [Fact]
    public void Вычитание_одного_диапазона_из_другого()
    {
        var result = CidrMath.Subtract("10.0.0.0/8", new[] { "10.1.0.0/16" });

        Assert.Contains(result, c => CidrMath.Contains(c, "10.0.0.1"));
        Assert.Contains(result, c => CidrMath.Contains(c, "10.2.0.1"));
        Assert.Contains(result, c => CidrMath.Contains(c, "10.255.255.255"));
        Assert.DoesNotContain(result, c => CidrMath.Contains(c, "10.1.0.1"));
    }

    [Fact]
    public void Разбор_одиночного_адреса()
    {
        Assert.True(CidrMath.TryParse("192.0.2.5", out var start, out var end));
        Assert.Equal(start, end);
    }

    [Theory]
    [InlineData("192.0.2.1/not-a-prefix")]
    [InlineData("192.0.2.1/")]
    [InlineData("192.0.2.1/33")]
    [InlineData("192.0.2.1/-1")]
    [InlineData("192.0.2.1/24/extra")]
    [InlineData("")]
    public void Некорректный_CIDR_возвращает_false_без_исключения(string cidr)
    {
        var exception = Record.Exception(() => CidrMath.TryParse(cidr, out _, out _));

        Assert.Null(exception);
        Assert.False(CidrMath.TryParse(cidr, out _, out _));
    }
}

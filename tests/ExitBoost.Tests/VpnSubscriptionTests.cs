using System.Text;
using ExitBoost.Core.Vpn;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>Импорт ключей по ссылке-подписке (как в Happ): ответ — base64 или обычный текст.</summary>
public class VpnSubscriptionTests
{
    private const string VlessLink =
        "vless://11112222-3333-4444-5555-666677778888@example.com:2053?encryption=none&security=reality" +
        "&sni=www.google.com&fp=chrome&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=bb12&type=grpc&serviceName=grpc1#%D0%9F%D0%BE%D0%BF%D1%80%D0%BE%D0%B1%D0%BA%D0%B0";

    [Fact]
    public void IsUrl_Detects_Http_Links()
    {
        Assert.True(VpnSubscription.IsUrl("https://key.f0rtuna.fast/Tu4Z07JPQ_jawCp7"));
        Assert.True(VpnSubscription.IsUrl("http://example.com/sub"));
        Assert.False(VpnSubscription.IsUrl("vless://abc@host:443"));
        Assert.False(VpnSubscription.IsUrl(null));
    }

    [Fact]
    public void DecodeIfBase64_Unpacks_Subscription()
    {
        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes(VlessLink));
        string decoded = VpnSubscription.DecodeIfBase64(body);

        Assert.Contains("vless://", decoded);
        Assert.Contains("%D0%9F", decoded); // имя осталось в percent-encoding
    }

    [Fact]
    public void DecodeIfBase64_Keeps_Plain_Text()
    {
        Assert.Equal(VlessLink, VpnSubscription.DecodeIfBase64(VlessLink));
        Assert.Equal("просто текст", VpnSubscription.DecodeIfBase64("просто текст"));
    }

    [Fact]
    public void ParseAll_Reads_Links_From_Base64_Body()
    {
        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes(VlessLink + "\n" + VlessLink));

        var keys = VpnSubscription.ParseAll(body, out var errors);

        Assert.Empty(errors);
        Assert.Equal(2, keys.Count);
        Assert.Equal("Попробка", keys[0].Name); // имя декодировано из percent-encoding
        Assert.Equal(VpnProtocol.Vless, keys[0].Protocol);
        Assert.Equal(2053, keys[0].Port);
    }

    [Fact]
    public void ParseAll_Reports_Unparsable_Lines()
    {
        var keys = VpnSubscription.ParseAll("vless://" + VlessLink[8..] + "\nмусор", out var errors);

        Assert.Single(keys);
        Assert.Single(errors);
    }

    [Fact]
    public void ParseAll_Skips_Transports_Unsupported_By_SingBox()
    {
        string xhttp =
            "vless://11112222-3333-4444-5555-666677778888@1.2.3.4:443?encryption=none&type=xhttp&path=%2Fapi#Xhttp";

        var keys = VpnSubscription.ParseAll(xhttp, out var errors);

        Assert.Empty(keys);
        Assert.Single(errors);
        Assert.Contains("xhttp", errors[0]);
    }

    [Fact]
    public void Hwid_Is_Stable_And_Local()
    {
        string first = VpnSubscription.Hwid;
        string second = VpnSubscription.Hwid;

        Assert.Equal(first, second);
        Assert.True(first.Length >= 16);
    }
}

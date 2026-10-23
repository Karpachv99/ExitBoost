using System.Text;
using ExitBoost.Core.Vpn;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>Парсинг ключей VPN как в Happ: vless / vmess / trojan / ss.</summary>
public class VpnKeyParserTests
{
    [Fact]
    public void Vless_Is_Parsed()
    {
        var link = "vless://11112222-3333-4444-5555-666677778888@example.com:2053?" +
                   "encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.google.com" +
                   "&fp=chrome&pbk=ABC123&sid=bb12&type=grpc&serviceName=TlsServiceOverVlessWS#Моя%20точка";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal(VpnProtocol.Vless, key!.Protocol);
        Assert.Equal("11112222-3333-4444-5555-666677778888", key.UserId);
        Assert.Equal("example.com", key.Address);
        Assert.Equal(2053, key.Port);
        Assert.Equal("Моя точка", key.Name);
        Assert.Equal("reality", key.Security);
        Assert.Equal("www.google.com", key.Sni);
        Assert.Equal("chrome", key.Fingerprint);
        Assert.Equal("ABC123", key.PublicKey);
        Assert.Equal("bb12", key.ShortId);
        Assert.Equal("grpc", key.Transport);
        Assert.Equal("TlsServiceOverVlessWS", key.Path);
        Assert.Equal("xtls-rprx-vision", key.Flow);
    }

    [Fact]
    public void Vless_WebSocket_Path_And_Host()
    {
        var link = "vless://uuid-test@vpn.example.org:443?type=ws&path=%2Fwspath&host=front.example.org&security=tls#WS";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("ws", key!.Transport);
        Assert.Equal("/wspath", key.Path);
        Assert.Equal("front.example.org", key.HostHeader);
        Assert.Equal("tls", key.Security);
        Assert.Equal("front.example.org", key.Sni);
    }

    [Fact]
    public void Vmess_Base64_Is_Parsed()
    {
        // {"v":"2","ps":"Тест VM","add":"1.2.3.4","port":"8443","id":"uuid-abc","aid":"0",
        //  "scy":"auto","net":"ws","type":"","host":"cdn.example.net","path":"/ws2","tls":"tls","fp":"chrome","sni":"cdn.example.net"}
        var json = """{"v":"2","ps":"Тест VM","add":"1.2.3.4","port":"8443","id":"uuid-abc","aid":"0","scy":"auto","net":"ws","type":"","host":"cdn.example.net","path":"/ws2","tls":"tls","fp":"chrome","sni":"cdn.example.net"}""";
        var link = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var key = VpnKeyParser.Parse(link, out var reason);
        Assert.True(key is not null, reason);
        Assert.Equal(VpnProtocol.Vmess, key!.Protocol);
        Assert.Equal("Тест VM", key.Name);
        Assert.Equal("1.2.3.4", key.Address);
        Assert.Equal(8443, key.Port);
        Assert.Equal("uuid-abc", key.UserId);
        Assert.Equal("auto", key.Encryption);
        Assert.Equal("ws", key.Transport);
        Assert.Equal("/ws2", key.Path);
        Assert.Equal("cdn.example.net", key.Sni);
        Assert.Equal("tls", key.Security);
    }

    [Fact]
    public void Trojan_Is_Parsed()
    {
        var link = "trojan://secretpass@trojan.example.com:443?sni=trojan.example.com&type=tcp#TrojanNode";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal(VpnProtocol.Trojan, key!.Protocol);
        Assert.Equal("secretpass", key.UserId);
        Assert.Equal("trojan.example.com", key.Address);
        Assert.Equal(443, key.Port);
        Assert.Equal("tls", key.Security);
        Assert.Equal("trojan.example.com", key.Sni);
        Assert.Equal("TrojanNode", key.Name);
    }

    [Fact]
    public void Shadowsocks_UrlSafeBase64_Whole()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:secrecy-pass@5.6.7.8:8388"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var link = $"ss://{payload}#МойSS";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal(VpnProtocol.Shadowsocks, key!.Protocol);
        Assert.Equal("aes-256-gcm", key.Method);
        Assert.Equal("secrecy-pass", key.UserId);
        Assert.Equal("5.6.7.8", key.Address);
        Assert.Equal(8388, key.Port);
        Assert.Equal("МойSS", key.Name);
    }

    [Fact]
    public void Shadowsocks_Userinfo_Form()
    {
        var userinfo = Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:pass2"));
        var link = $"ss://{userinfo}@10.20.30.40:9101#node2";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("chacha20-ietf-poly1305", key!.Method);
        Assert.Equal("pass2", key.UserId);
        Assert.Equal("10.20.30.40", key.Address);
        Assert.Equal(9101, key.Port);
    }

    [Fact]
    public void ParseMany_Skips_Junk_And_Counts_Errors()
    {
        var keys = VpnKeyParser.ParseMany(
            "vless://uuid-a@h.example:443#a1\n" +
            "мусор\n" +
            "trojan://p@t.example:443#t1", out var errors);

        Assert.Equal(2, keys.Count);
        Assert.Single(errors);
    }

    [Fact]
    public void Routing_Normalizes_Input()
    {
        var routing = new VpnRoutingConfig
        {
            Mode = "whatever",
            Domains = ["YouTube.com", ".vk.com; mail.ru,,google.ru."],
            Processes = ["discord.exe", "steam", "STEAM.EXE"]
        };
        routing.Normalize();

        Assert.Equal("split", routing.Mode);
        Assert.Equal(["youtube.com", "vk.com", "mail.ru", "google.ru"], routing.Domains);
        Assert.Equal(["discord.exe", "steam.exe"], routing.Processes);
    }

    [Fact]
    public void Config_Split_Has_Vpn_Direct_And_Rules()
    {
        var key = new VpnKey
        {
            Protocol = VpnProtocol.Vless,
            Address = "vpn.example.com",
            Port = 443,
            UserId = "u-1234",
            Security = "tls",
            Sni = "vpn.example.com"
        };
        var routing = new VpnRoutingConfig
        {
            Mode = "split",
            Domains = ["youtube.com"],
            Processes = ["discord.exe"]
        };

        string json = SingBoxConfigBuilder.Build(key, routing);

        Assert.Contains("\"type\": \"vless\"", json);
        Assert.Contains("\"tag\": \"vpn\"", json);
        Assert.Contains("\"tag\": \"direct\"", json);
        Assert.Contains("\"domain_suffix\"", json);
        Assert.Contains("youtube.com", json);
        Assert.Contains("\"process_name\"", json);
        Assert.Contains("discord.exe", json);
        Assert.Contains("\"final\": \"direct\"", json);
        Assert.Contains("\"auto_route\": true", json);
    }

    [Fact]
    public void Hysteria2_Is_Parsed_And_Built()
    {
        var link = "hysteria2://018b24d0-d737-430c-b20a-423b56c78b72@191.40.4.184:443/?sni=de.cdn.f0rtuna.fast" +
                   "#%D0%93%D0%B5%D1%80%D0%BC%D0%B0%D0%BD%D0%B8%D1%8F";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal(VpnProtocol.Hysteria2, key!.Protocol);
        Assert.Equal("018b24d0-d737-430c-b20a-423b56c78b72", key.UserId);
        Assert.Equal("191.40.4.184", key.Address);
        Assert.Equal(443, key.Port);
        Assert.Equal("de.cdn.f0rtuna.fast", key.Sni);
        Assert.Equal("Германия", key.Name);

        string json = SingBoxConfigBuilder.Build(key, new VpnRoutingConfig { Mode = "split" });
        Assert.Contains("\"type\": \"hysteria2\"", json);
        Assert.Contains("\"password\": \"018b24d0-d737-430c-b20a-423b56c78b72\"", json);
        Assert.Contains("de.cdn.f0rtuna.fast", json);
    }

    [Fact]
    public void Alpn_Is_Parsed_And_Written()
    {
        var link = "vless://uuid-a@1.2.3.4:8443?encryption=none&security=tls&sni=a.example&fp=firefox&alpn=http/1.1#RAW";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("http/1.1", key!.Alpn);

        string json = SingBoxConfigBuilder.Build(key, new VpnRoutingConfig { Mode = "split" });
        Assert.Contains("\"alpn\"", json);
        Assert.Contains("http/1.1", json);
    }

    [Fact]
    public void Xhttp_Key_Is_Marked_As_Unsupported()
    {
        var link = "vless://uuid-a@1.2.3.4:443?encryption=none&type=xhttp&path=%2Fapi#Xhttp";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.NotNull(key!.UnsupportedReason);
        Assert.Contains("xhttp", key.UnsupportedReason!);
    }

    [Fact]
    public void Config_Full_Routes_Everything_And_Keeps_Local_Network_Direct()
    {
        var key = new VpnKey
        {
            Protocol = VpnProtocol.Shadowsocks,
            Address = "1.2.3.4",
            Port = 8388,
            UserId = "pass",
            Method = "aes-256-gcm"
        };
        var routing = new VpnRoutingConfig { Mode = "full", Domains = ["unused.example"] };

        string json = SingBoxConfigBuilder.Build(key, routing);

        Assert.Contains("\"type\": \"shadowsocks\"", json);
        Assert.Contains("\"method\": \"aes-256-gcm\"", json);
        Assert.Contains("\"final\": \"vpn\"", json);
        Assert.Contains("\"ip_is_private\"", json);
        // в режиме «весь трафик» селектирующих правил быть не должно
        Assert.DoesNotContain("\"domain_suffix\"", json);
    }
}

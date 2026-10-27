using System.Text;
using System.Text.Json;
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

    [Fact]
    public void Reality_Without_Fingerprint_Gets_Default_Utls()
    {
        // В ключе нет fp — sing-box требует uTLS для Reality, иначе падает при старте.
        var link = "vless://uuid-a@1.2.3.4:443?encryption=none&security=reality&sni=www.google.com&pbk=ABC123&sid=bb12#R";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("", key!.Fingerprint);

        string json = SingBoxConfigBuilder.Build(key, new VpnRoutingConfig());
        Assert.Contains("\"utls\"", json);
        Assert.Contains("\"fingerprint\": \"chrome\"", json);
        Assert.Contains("\"reality\"", json);
    }

    [Fact]
    public void Reality_Fingerprint_From_Key_Is_Kept()
    {
        var link = "vless://uuid-a@1.2.3.4:443?security=reality&sni=a.example&fp=firefox&pbk=ABC#R";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        string json = SingBoxConfigBuilder.Build(key!, new VpnRoutingConfig());
        Assert.Contains("\"fingerprint\": \"firefox\"", json);
        Assert.DoesNotContain("\"fingerprint\": \"chrome\"", json);
    }

    [Fact]
    public void AllowInsecure_Is_Parsed_And_Written()
    {
        var link = "trojan://pass@example.com:443?sni=example.com&allowInsecure=1#T";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.True(key!.AllowInsecure);

        string json = SingBoxConfigBuilder.Build(key, new VpnRoutingConfig());
        Assert.Contains("\"insecure\": true", json);
    }

    [Fact]
    public void Hysteria2_Insecure_Is_Parsed_And_Written()
    {
        var link = "hysteria2://pass@example.com:443/?sni=example.com&insecure=true#H";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.True(key!.AllowInsecure);

        string json = SingBoxConfigBuilder.Build(key, new VpnRoutingConfig());
        Assert.Contains("\"insecure\": true", json);
    }

    [Fact]
    public void Shadowsocks_UrlEncoded_Base64_Userinfo()
    {
        // SIP002: base64(method:password) в userinfo, знак '=' закодирован как %3D
        string userinfo = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pa%ss")).Replace("=", "%3D");
        var link = $"ss://{userinfo}@1.2.3.4:8388#SS";

        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("aes-256-gcm", key!.Method);
        Assert.Equal("pa%ss", key.UserId);
        Assert.Equal("1.2.3.4", key.Address);
        Assert.Equal(8388, key.Port);
    }

    [Fact]
    public void Shadowsocks_Ipv6_Address()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pass@[2001:db8::1]:8388"));
        var link = $"ss://{payload}#v6";

        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("2001:db8::1", key!.Address);
        Assert.Equal(8388, key.Port);
    }

    [Fact]
    public void Shadowsocks_With_Plugin_Is_Marked_Unsupported()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pass@1.2.3.4:8388"));
        var link = $"ss://{payload}?plugin=obfs-local%3Bobfs%3Dhttp#p";

        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("obfs-local;obfs=http", key!.Plugin);
        Assert.NotNull(key.UnsupportedReason);
        Assert.Contains("плагин", key.UnsupportedReason!);
    }

    [Theory]
    [InlineData("kcp")]
    [InlineData("xhttp")]
    [InlineData("splithttp")]
    public void Unsupported_Transports_Are_Reported(string transport)
    {
        var link = $"vless://uuid-a@1.2.3.4:443?security=tls&type={transport}#T";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.NotNull(key!.UnsupportedReason);
        Assert.Contains(transport, key.UnsupportedReason!);
    }

    [Fact]
    public void HttpUpgrade_And_Quic_Are_Written_As_Transports()
    {
        var upgrade = VpnKeyParser.Parse(
            "vless://uuid-a@1.2.3.4:443?security=tls&type=httpupgrade&path=%2Fup&host=cdn.example#U",
            out var reason1);
        Assert.True(upgrade is not null, reason1);
        string json1 = SingBoxConfigBuilder.Build(upgrade!, new VpnRoutingConfig());
        Assert.Contains("\"type\": \"httpupgrade\"", json1);
        Assert.Contains("/up", json1);

        var quic = VpnKeyParser.Parse(
            "vless://uuid-a@1.2.3.4:443?security=tls&type=quic#Q",
            out var reason2);
        Assert.True(quic is not null, reason2);
        string json2 = SingBoxConfigBuilder.Build(quic!, new VpnRoutingConfig());
        Assert.Contains("\"type\": \"quic\"", json2);
    }

    [Fact]
    public void H2_Transport_Is_Mapped_To_Http()
    {
        var link = "vless://uuid-a@1.2.3.4:443?security=tls&type=h2&path=%2Fh2&host=a.example#H2";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("http", key!.Transport);

        string json = SingBoxConfigBuilder.Build(key, new VpnRoutingConfig());
        Assert.Contains("\"type\": \"http\"", json);
    }

    [Fact]
    public void RawLink_Keeps_Query_And_Fragment()
    {
        var link = "vless://uuid-a@1.2.3.4:443?security=tls&sni=a.example&type=ws&path=%2Fws#My%20Node";
        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal(link, key!.RawLink);
    }

    [Fact]
    public void Routing_Mtu_Is_Clamped_And_Written()
    {
        var key = new VpnKey
        {
            Protocol = VpnProtocol.Shadowsocks,
            Address = "1.2.3.4",
            Port = 8388,
            UserId = "pass",
            Method = "aes-256-gcm"
        };

        var routing = new VpnRoutingConfig { Mtu = 1500 };
        string json = SingBoxConfigBuilder.Build(key, routing);
        Assert.Contains("\"mtu\": 1500", json);

        routing.Mtu = 100;
        routing.Normalize();
        Assert.Equal(1280, routing.Mtu);
    }

    [Fact]
    public void Config_Split_Resolves_Selected_Domains_Through_Vpn_Dns()
    {
        var key = new VpnKey
        {
            Protocol = VpnProtocol.Vless,
            Address = "vpn.example.com",
            Port = 443,
            UserId = "uuid",
            Security = "tls",
            Sni = "vpn.example.com"
        };
        var routing = new VpnRoutingConfig { Mode = "split", Domains = ["youtube.com", "discord.com"] };

        string json = SingBoxConfigBuilder.Build(key, routing);

        Assert.Contains("\"type\": \"https\"", json);
        Assert.Contains("\"tag\": \"remote\"", json);
        Assert.Contains("\"server\": \"1.1.1.1\"", json);
        Assert.Contains("\"detour\": \"vpn\"", json);
        Assert.Contains("\"default_domain_resolver\": \"local\"", json);
        Assert.Contains("\"final\": \"local\"", json); // остальной DNS — локальный
        Assert.Contains("youtube.com", json);
    }

    [Fact]
    public void Config_Split_Routes_Only_Matched_Game_Process_And_Server_Ip()
    {
        var key = new VpnKey
        {
            Protocol = VpnProtocol.Vless,
            Address = "vpn.example.com",
            Port = 443,
            UserId = "uuid",
            Security = "tls",
            Sni = "vpn.example.com"
        };
        var routing = new VpnRoutingConfig
        {
            Mode = "split",
            Domains = ["opencode.ai"],
            Processes = ["Game.exe"],
            GameEndpoints = [new VpnGameEndpoint("Game.exe", "203.0.113.7")]
        };

        string json = SingBoxConfigBuilder.Build(key, routing);
        using var document = JsonDocument.Parse(json);
        var rules = document.RootElement.GetProperty("route").GetProperty("rules");
        var fallback = rules.EnumerateArray().First(rule =>
            rule.TryGetProperty("ip_cidr", out var cidr) && cidr.ToString().Contains("203.0.113.7"));

        Assert.Equal("route", fallback.GetProperty("action").GetString());
        Assert.Equal("vpn", fallback.GetProperty("outbound").GetString());
        Assert.Equal("Game.exe", fallback.GetProperty("process_name")[0].GetString());
        Assert.Equal("203.0.113.7/32", fallback.GetProperty("ip_cidr")[0].GetString());
        int endpointRuleIndex = -1, generalProcessRuleIndex = -1, index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("ip_cidr", out _)) endpointRuleIndex = index;
            else if (rule.TryGetProperty("process_name", out var process) && process.ToString().Contains("game.exe", StringComparison.OrdinalIgnoreCase))
                generalProcessRuleIndex = index;
            index++;
        }
        Assert.True(endpointRuleIndex >= 0 && endpointRuleIndex < generalProcessRuleIndex);
    }

    [Fact]
    public void Config_Full_Sends_All_Dns_Through_Vpn()
    {
        var key = new VpnKey
        {
            Protocol = VpnProtocol.Shadowsocks,
            Address = "1.2.3.4",
            Port = 8388,
            UserId = "pass",
            Method = "aes-256-gcm"
        };

        string json = SingBoxConfigBuilder.Build(key, new VpnRoutingConfig { Mode = "full" });

        Assert.Contains("\"final\": \"remote\"", json);
        Assert.Contains("\"default_domain_resolver\": \"local\"", json);
    }

    [Fact]
    public void Shadowsocks_Base64_With_Slash_Is_Decoded()
    {
        // base64 содержит '/': раньше он заменялся на '_' и Convert.FromBase64String падал.
        string plain = "aes-256-gcm:p??ss@1.2.3.4:8388";
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        Assert.Contains('/', payload);

        var key = VpnKeyParser.Parse($"ss://{payload}#x", out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("aes-256-gcm", key!.Method);
        Assert.Equal("p??ss", key.UserId);
    }

    [Fact]
    public void Shadowsocks_Base64_With_Plus_Is_Decoded()
    {
        string plain = "aes-256-gcm:~~~~@1.2.3.4:8388";
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        Assert.Contains('+', payload);

        var key = VpnKeyParser.Parse($"ss://{payload}#x", out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("~~~~", key!.UserId);
    }

    [Fact]
    public void Vmess_Boolean_Tls_And_UpperCase_H2()
    {
        var json = """{"v":"2","ps":"VM","add":"1.2.3.4","port":"443","id":"uuid","aid":"0","net":"H2","tls":true,"host":"a.example","path":"/"}""";
        var link = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var key = VpnKeyParser.Parse(link, out var reason);

        Assert.True(key is not null, reason);
        Assert.Equal("tls", key!.Security);
        Assert.Equal("http", key.Transport);
    }
}

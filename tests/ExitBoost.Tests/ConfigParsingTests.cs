using ExitBoost.Core.Configuration;
using ExitBoost.Core.Models;
using ExitBoost.Core.Tunnel;
using Xunit;

namespace ExitBoost.Tests;

public class ConfigParsingTests
{
    private const string SampleConf = """
        [Interface]
        PrivateKey = cHJpdmF0ZWtleXByaXZhdGVrZXlwcml2YXRla2V5cHJpdmF0ZWtleXByaXZhdGVrZXk=
        Address = 10.13.0.2/32
        DNS = 1.1.1.1
        MTU = 1420

        [Peer]
        PublicKey = cHVibGlja2V5cHVibGlja2V5cHVibGlja2V5cHVibGlja2V5cHVibGlja2V5MTI=
        PresharedKey = cHNrc2Vzc2hlcmQ=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = wg.example.com:51820
        PersistentKeepalive = 25
        """;

    [Fact]
    public void Разбор_конфига_WireGuard()
    {
        var node = NodeRegistry.ParseWireGuardConf(SampleConf);

        Assert.NotNull(node);
        Assert.Equal("wg.example.com", node!.Host);
        Assert.Equal(51820, node.Port);
        Assert.Equal("10.13.0.2/32", node.Address);
        Assert.Equal("1.1.1.1", node.Dns);
        Assert.Equal(1420, node.Mtu);
        Assert.Equal(25, node.PersistentKeepalive);
        Assert.Equal(2, node.AllowedIps.Length);
        Assert.Equal(NodeKind.WireGuard, node.Kind);
    }

    [Fact]
    public void Разбор_конфига_с_IP_и_портом()
    {
        var text = SampleConf.Replace("Endpoint = wg.example.com:51820", "Endpoint = 203.0.113.10:1234");
        var node = NodeRegistry.ParseWireGuardConf(text);

        Assert.NotNull(node);
        Assert.Equal("203.0.113.10", node!.Host);
        Assert.Equal(1234, node.Port);
    }

    [Fact]
    public void Разбор_JSON_списка_узлов()
    {
        const string json = """
            { "nodes": [ { "id": "a", "name": "Alpha", "host": "1.2.3.4", "port": 51820, "kind": "WireGuard" } ] }
            """;

        var nodes = NodeRegistry.ParseJson(json);

        Assert.Single(nodes);
        Assert.Equal("Alpha", nodes[0].Name);
        Assert.Equal(51820, nodes[0].Port);
    }

    [Fact]
    public void Разбор_YAML_списка_узлов()
    {
        const string yaml = """
            nodes:
              - id: b
                name: Beta
                host: 5.6.7.8
                port: 51820
                kind: WireGuard
                address: 10.0.0.2/32
                allowed_ips:
                  - 0.0.0.0/0
            """;

        var nodes = NodeRegistry.ParseYaml(yaml);

        Assert.Single(nodes);
        Assert.Equal("Beta", nodes[0].Name);
        Assert.Equal("10.0.0.2/32", nodes[0].Address);
        Assert.Equal(new[] { "0.0.0.0/0" }, nodes[0].AllowedIps);
    }

    [Fact]
    public void Генерация_конфига_WireGuard_содержит_маршруты()
    {
        var node = NodeRegistry.ParseWireGuardConf(SampleConf)!;
        var options = new TunnelOptions
        {
            Mode = RoutingMode.ProcessSplit,
            AllowedIps = { "185.60.216.0/22", "104.16.0.0/13" },
            Mtu = 1400,
            Dns = "1.1.1.1"
        };

        string conf = WireGuardConfigBuilder.Build(node, options);

        Assert.Contains("AllowedIPs = 185.60.216.0/22, 104.16.0.0/13", conf);
        Assert.Contains("MTU = 1400", conf);
        Assert.Contains("DNS = 1.1.1.1", conf);
        Assert.Contains("Endpoint = wg.example.com:51820", conf);
    }

    [Fact]
    public void Sync_конфиг_не_содержит_полей_которые_не_принимает_wg()
    {
        var node = NodeRegistry.ParseWireGuardConf(SampleConf)!;
        var options = new TunnelOptions { AllowedIps = { "10.0.0.0/8" } };

        string conf = WireGuardConfigBuilder.BuildSyncConf(node, options);

        Assert.DoesNotContain("Address", conf);
        Assert.DoesNotContain("DNS", conf);
        Assert.DoesNotContain("MTU", conf);
        Assert.Contains("AllowedIPs = 10.0.0.0/8", conf);
    }
}

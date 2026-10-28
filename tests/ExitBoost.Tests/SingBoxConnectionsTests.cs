using System.Text.Json;
using ExitBoost.Core.Vpn;
using Xunit;

namespace ExitBoost.Tests;

/// <summary>Разбор Clash API sing-box: порты приходят строками, счётчики — числами.</summary>
public class SingBoxConnectionsTests
{
    private const string Payload = """
    {
      "connections": [
        {
          "id": "ff1cba5c-dd09-460f-aa74-b3ff8febe216",
          "upload": 40,
          "download": 3827,
          "start": "2026-10-28T14:37:25.2238628+03:00",
          "metadata": {
            "network": "tcp",
            "sourceIP": "172.19.0.1",
            "sourcePort": "62526",
            "destinationIP": "134.168.255.11",
            "destinationPort": "21360",
            "host": "",
            "processPath": ""
          },
          "rule": "final"
        },
        {
          "id": "second",
          "upload": 0,
          "download": 0,
          "metadata": {
            "network": "udp",
            "sourcePort": "51243",
            "destinationIP": "2.16.168.208",
            "destinationPort": "443"
          }
        },
        {
          "id": "broken",
          "metadata": { "network": "tcp", "destinationPort": "0" }
        }
      ]
    }
    """;

    [Fact]
    public void Parse_Reads_Counters_And_String_Ports()
    {
        using var document = JsonDocument.Parse(Payload);
        var list = SingBoxConnectionsClient.Parse(document.RootElement);

        Assert.Equal(2, list.Count);
        var game = list[0];
        Assert.Equal("134.168.255.11", game.DestinationIP);
        Assert.Equal(21360, game.DestinationPort);
        Assert.Equal(62526, game.SourcePort);
        Assert.Equal(40, game.Upload);
        Assert.Equal(3827, game.Download);
        Assert.Equal(3867, game.TotalBytes);
        Assert.Equal("tcp", game.Network);
        Assert.Equal(2026, game.StartUtc.Year);
        Assert.Equal(DateTimeKind.Utc, game.StartUtc.Kind);
    }

    [Fact]
    public void Parse_Skips_Connections_Without_Destination_Port()
    {
        using var document = JsonDocument.Parse("""{ "connections": [ { "id": "x", "metadata": { "destinationIP": "1.2.3.4", "destinationPort": "0" } } ] }""");
        Assert.Empty(SingBoxConnectionsClient.Parse(document.RootElement));
    }

    [Fact]
    public void Parse_Handles_Empty_Response()
    {
        using var document = JsonDocument.Parse("{}");
        Assert.Empty(SingBoxConnectionsClient.Parse(document.RootElement));
    }
}

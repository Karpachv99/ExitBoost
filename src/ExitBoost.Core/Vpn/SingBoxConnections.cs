using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace ExitBoost.Core.Vpn;

/// <summary>Активное соединение sing-box из Clash API: точные счётчики байтов по каждому потоку.</summary>
public sealed record SingBoxConnection(string Id, string Network, string DestinationIP, int DestinationPort,
    string SourceIP, int SourcePort, string Host, string Rule, long Upload, long Download, DateTime StartUtc)
{
    public long TotalBytes => Upload + Download;
}

/// <summary>
/// Читает <c>GET /connections</c> у Clash API sing-box. Даёт точные счётчики
/// отправлено/получено по каждому соединению — по ним видно, ответил сервер
/// или игра стучится в пустоту. Содержимое трафика не читается.
/// </summary>
public sealed class SingBoxConnectionsClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(3)
    })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private readonly string _endpoint;

    public SingBoxConnectionsClient(int port) => _endpoint = $"http://127.0.0.1:{port}/connections";

    /// <summary>Список соединений; пустой список, если ядро ещё не подняло API.</summary>
    public async Task<IReadOnlyList<SingBoxConnection>> GetAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(_endpoint, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Разбор ответа Clash API; вынесен отдельно, чтобы покрыть тестом формат sing-box.</summary>
    public static List<SingBoxConnection> Parse(JsonElement root)
    {
        var result = new List<SingBoxConnection>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("connections", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                continue;

            string id = GetString(item, "id");
            string destination = GetString(metadata, "destinationIP");
            int destinationPort = GetInt(metadata, "destinationPort");
            if (destination.Length == 0 || destinationPort <= 0) continue;

            DateTime start = DateTime.TryParse(GetString(item, "start"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.UtcNow;

            result.Add(new SingBoxConnection(
                id,
                GetString(metadata, "network"),
                destination,
                destinationPort,
                GetString(metadata, "sourceIP"),
                GetInt(metadata, "sourcePort"),
                GetString(metadata, "host"),
                GetString(item, "rule"),
                GetLong(item, "upload"),
                GetLong(item, "download"),
                start));
        }

        return result;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement element, string name)
    {
        long value = GetLong(element, name);
        return value is > 0 and <= int.MaxValue ? (int)value : 0;
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => 0
        };
    }
}

using System.Text.Json;

namespace ExitBoost.Core.Vpn;

/// <summary>Точный адрес игрового сервера, который следует отправлять через VPN для указанного процесса.</summary>
public sealed record VpnGameEndpoint(string ProcessName, string Address);

/// <summary>Правила маршрутизации VPN-ключа (режим «только выбранные» или «весь трафик»).</summary>
public sealed class VpnRoutingConfig
{
    /// <summary>split — через VPN только выбранные сайты/программы; full — весь трафик.</summary>
    public string Mode { get; set; } = "split";

    /// <summary>Домены, уходящие через VPN в режиме split (+ все их поддомены).</summary>
    public List<string> Domains { get; set; } = [];

    /// <summary>Имена процессов (.exe), чей трафик уходит через VPN в режиме split.</summary>
    public List<string> Processes { get; set; } = [];

    /// <summary>Временные endpoint-правила автообхода игры; не сохраняются в keys.json.</summary>
    public List<VpnGameEndpoint> GameEndpoints { get; set; } = [];

    /// <summary>MTU TUN-адаптера: 1400 подходит для PPPoE, 1500 — для обычного Ethernet.</summary>
    public int Mtu { get; set; } = 1400;

    public VpnRoutingConfig Clone() => new()
    {
        Mode = Mode,
        Domains = [.. Domains],
        Processes = [.. Processes],
        GameEndpoints = [.. GameEndpoints],
        Mtu = Mtu
    };

    public void Normalize()
    {
        Mode = Mode == "full" ? "full" : "split";
        Mtu = Math.Clamp(Mtu, 1280, 9000);
        Domains = Domains
            .SelectMany(d => d.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(d => d.Length > 0)
            .Select(d => d.Trim('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Processes = Processes
            .SelectMany(p => p.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(p => p.Length > 0)
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe")
            .Select(p => p.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        GameEndpoints = GameEndpoints
            .Where(e => !string.IsNullOrWhiteSpace(e.ProcessName) && System.Net.IPAddress.TryParse(e.Address, out _))
            .Select(e => new VpnGameEndpoint(
                e.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? e.ProcessName : e.ProcessName + ".exe",
                System.Net.IPAddress.Parse(e.Address).ToString()))
            .Distinct()
            .ToList();
    }
}

/// <summary>
/// Сохранённые VPN-ключи и настройка маршрутизации: %APPDATA%\ExitBoost\vpn\keys.json.
/// </summary>
public sealed class VpnKeyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string StoragePath { get; }

    public List<VpnKey> Keys { get; } = [];

    public VpnRoutingConfig Routing { get; } = new();

    public VpnKeyStore()
    {
        StoragePath = Path.Combine(AppPaths.DataDirectory, "vpn", "keys.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(StoragePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StoragePath));
            if (!doc.RootElement.TryGetProperty("keys", out var keysEl)) return;

            Keys.Clear();
            foreach (var el in keysEl.EnumerateArray())
            {
                var key = DeserializeKey(el);
                if (key is not null) Keys.Add(key);
            }

            if (doc.RootElement.TryGetProperty("routing", out var routingEl))
            {
                Routing.Mode = routingEl.TryGetProperty("mode", out var m) ? m.GetString() ?? "split" : "split";
                Routing.Domains = GetStringList(routingEl, "domains");
                Routing.Processes = GetStringList(routingEl, "processes");
                Routing.Mtu = routingEl.TryGetProperty("mtu", out var mtu) && mtu.TryGetInt32(out var mtuValue)
                    ? mtuValue
                    : 1400;
            }
            Routing.Normalize();
        }
        catch
        {
            // повреждённый файл не должен мешать работе — начнём с пустого списка
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            string temp = StoragePath + ".tmp";

            // Пишем во временный файл и подменяем: сбой посреди записи
            // не должен уничтожить сохранённые ключи.
            using (var writer = new Utf8JsonWriter(File.Create(temp), new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("keys");
                writer.WriteStartArray();
                foreach (var key in Keys) SerializeKey(writer, key);
                writer.WriteEndArray();
                writer.WritePropertyName("routing");
                writer.WriteStartObject();
                writer.WriteString("mode", Routing.Mode);
                writer.WritePropertyName("domains");
                writer.WriteStartArray();
                foreach (var d in Routing.Domains) writer.WriteStringValue(d);
                writer.WriteEndArray();
                writer.WritePropertyName("processes");
                writer.WriteStartArray();
                foreach (var p in Routing.Processes) writer.WriteStringValue(p);
                writer.WriteEndArray();
                writer.WriteNumber("mtu", Routing.Mtu);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            File.Move(temp, StoragePath, overwrite: true);
        }
        catch
        {
        }
    }

    // ---- JSON ключа ----

    private static void SerializeKey(Utf8JsonWriter w, VpnKey key)
    {
        w.WriteStartObject();
        void S(string name, string value) { if (value.Length > 0) w.WriteString(name, value); }
        S("id", key.Id);
        S("name", key.Name);
        S("subscription", key.SubscriptionName);
        S("sub_url", key.SubscriptionUrl);
        w.WriteString("protocol", key.Protocol.ToString().ToLowerInvariant());
        S("address", key.Address);
        w.WriteNumber("port", key.Port);
        S("id_user", key.UserId);
        S("security", key.Security);
        S("transport", key.Transport);
        S("flow", key.Flow);
        S("sni", key.Sni);
        S("fp", key.Fingerprint);
        S("pbk", key.PublicKey);
        S("sid", key.ShortId);
        S("path", key.Path);
        S("host", key.HostHeader);
        S("method", key.Method);
        if (key.AlterId != 0) w.WriteNumber("aid", key.AlterId);
        S("encrypt", key.Encryption);
        S("alpn", key.Alpn);
        S("obfs", key.ObfsType);
        S("obfs_pass", key.ObfsPassword);
        S("plugin", key.Plugin);
        if (key.AllowInsecure) w.WriteBoolean("insecure", true);
        S("raw", key.RawLink);
        w.WriteString("addedAt", key.AddedAt);
        w.WriteEndObject();
    }

    private static VpnKey? DeserializeKey(JsonElement el)
    {
        string Get(string name)
        {
            if (el.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number)
                    return v.GetRawText();
                if (v.ValueKind != JsonValueKind.Null)
                    return v.GetString() ?? "";
            }
            return "";
        }

        if (!VpnProtocolParser.TryParse(Get("protocol"), out var proto)) return null;
        string address = Get("address");
        if (address.Length == 0) return null;
        int port = el.TryGetProperty("port", out var p) && p.TryGetInt32(out var pv) ? pv : 443;

        return new VpnKey
        {
            Id = Get("id").Length == 0 ? Guid.NewGuid().ToString("N")[..8] : Get("id"),
            Name = Get("name").Length == 0 ? address : Get("name"),
            SubscriptionName = Get("subscription"),
            SubscriptionUrl = Get("sub_url"),
            Protocol = proto,
            Address = address,
            Port = port,
            UserId = Get("id_user"),
            Security = Get("security").Length == 0 ? "none" : Get("security"),
            Transport = Get("transport").Length == 0 ? "tcp" : Get("transport"),
            Flow = Get("flow"),
            Sni = Get("sni"),
            Fingerprint = Get("fp"),
            PublicKey = Get("pbk"),
            ShortId = Get("sid"),
            Path = Get("path"),
            HostHeader = Get("host"),
            Method = Get("method"),
            AlterId = int.TryParse(Get("aid"), out var aid) ? aid : 0,
            Encryption = Get("encrypt"),
            Alpn = Get("alpn"),
            ObfsType = Get("obfs"),
            ObfsPassword = Get("obfs_pass"),
            Plugin = Get("plugin"),
            AllowInsecure = el.TryGetProperty("insecure", out var ins) && ins.ValueKind == JsonValueKind.True,
            RawLink = Get("raw"),
            AddedAt = el.TryGetProperty("addedAt", out var at) && at.ValueKind == JsonValueKind.String
                ? DateTime.TryParse(at.GetString(), out var dt) ? dt : DateTime.UtcNow
                : DateTime.UtcNow
        };
    }

    private static List<string> GetStringList(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array)
            return [];
        return a.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

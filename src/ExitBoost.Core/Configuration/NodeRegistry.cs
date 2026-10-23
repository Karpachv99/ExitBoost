using System.Text.Json;
using System.Text.Json.Serialization;
using ExitBoost.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ExitBoost.Core.Configuration;

/// <summary>
/// Хранилище узлов. Поддерживает JSON и YAML, а также импорт одиночных
/// конфигов WireGuard (.conf) и OpenVPN (.ovpn).
/// </summary>
public sealed class NodeRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<NodeConfig> _nodes = new();

    public IReadOnlyList<NodeConfig> Nodes => _nodes;

    /// <summary>Узел изменился (добавлен, удалён, отредактирован).</summary>
    public event Action? Changed;

    public NodeConfig? Get(string id) => _nodes.FirstOrDefault(n => n.Id == id);

    public void Add(NodeConfig node)
    {
        if (string.IsNullOrWhiteSpace(node.Id)) node.Id = NodeConfig.NewId();
        _nodes.Add(node);
        Changed?.Invoke();
    }

    public void Update(NodeConfig node)
    {
        int idx = _nodes.FindIndex(n => n.Id == node.Id);
        if (idx >= 0) _nodes[idx] = node;
        else _nodes.Add(node);
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        int idx = _nodes.FindIndex(n => n.Id == id);
        if (idx < 0) return false;
        _nodes.RemoveAt(idx);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        _nodes.Clear();
        Changed?.Invoke();
    }

    public void ReplaceAll(IEnumerable<NodeConfig> nodes)
    {
        _nodes.Clear();
        _nodes.AddRange(nodes);
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- загрузка

    /// <summary>Загрузка списка узлов из файла (.json или .yaml/.yml).</summary>
    public void Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл со списком узлов не найден", path);

        string text = File.ReadAllText(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var parsed = ext switch
        {
            ".yaml" or ".yml" => ParseYaml(text),
            ".json" => ParseJson(text),
            _ => ParseJson(text)
        };

        ReplaceAll(parsed);
    }

    /// <summary>Сохранение списка узлов.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".yaml" or ".yml")
        {
            var yaml = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var doc = new YamlFile { Nodes = _nodes.Select(YamlNode.From).ToList() };
            File.WriteAllText(path, yaml.Serialize(doc));
        }
        else
        {
            var doc = new JsonFile { Nodes = _nodes };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
        }
    }

    public static List<NodeConfig> ParseJson(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return new List<NodeConfig>();

        // Либо корень-массив, либо объект с полем nodes.
        if (text[0] == '[')
        {
            return JsonSerializer.Deserialize<List<NodeConfig>>(text, JsonOptions) ?? new List<NodeConfig>();
        }

        var doc = JsonSerializer.Deserialize<JsonFile>(text, JsonOptions);
        return doc?.Nodes ?? new List<NodeConfig>();
    }

    public static List<NodeConfig> ParseYaml(string text)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var parsed = deserializer.Deserialize<YamlFile>(text);
        if (parsed?.Nodes is null) return new List<NodeConfig>();
        return parsed.Nodes.Select(n => n.ToConfig()).ToList();
    }

    // ------------------------------------------------------------ импорт .conf

    /// <summary>Разбор конфига WireGuard в узел.</summary>
    public static NodeConfig? ParseWireGuardConf(string text, string? nameOverride = null, string? source = null)
    {
        string? privateKey = null, address = null, dns = null;
        string? publicKey = null, presharedKey = null, endpoint = null;
        int mtu = 1420, keepalive = 25;
        var allowed = new List<string>();
        string section = "";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();

            if (section == "interface")
            {
                switch (key)
                {
                    case "privatekey": privateKey = value; break;
                    case "address": address = value; break;
                    case "dns": dns = value; break;
                    case "mtu": int.TryParse(value, out mtu); break;
                }
            }
            else if (section == "peer")
            {
                switch (key)
                {
                    case "publickey": publicKey = value; break;
                    case "presharedkey": presharedKey = value; break;
                    case "allowedips":
                        allowed.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    case "endpoint": endpoint = value; break;
                    case "persistentkeepalive": int.TryParse(value, out keepalive); break;
                }
            }
        }

        if (publicKey is null || endpoint is null) return null;

        string host = endpoint;
        int port = 51820;
        int colon = endpoint.LastIndexOf(':');
        if (colon > 0 && int.TryParse(endpoint[(colon + 1)..], out int p))
        {
            port = p;
            host = endpoint[..colon];
        }

        return new NodeConfig
        {
            Id = NodeConfig.NewId(),
            Name = nameOverride ?? host,
            Host = host,
            Port = port,
            Kind = NodeKind.WireGuard,
            PrivateKey = privateKey,
            PublicKey = publicKey,
            PresharedKey = presharedKey,
            Address = address,
            Dns = dns,
            Mtu = mtu,
            PersistentKeepalive = keepalive,
            AllowedIps = allowed.Count > 0 ? allowed.ToArray() : new[] { "0.0.0.0/0" },
            Source = source ?? "импорт .conf"
        };
    }

    /// <summary>Разбор конфига OpenVPN в узел (минимальный).</summary>
    public static NodeConfig? ParseOpenVpnConf(string text, string? nameOverride = null)
    {
        string? remote = null;
        int port = 1194;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("remote ", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                remote = parts[1];
                if (parts.Length >= 3 && int.TryParse(parts[2], out int p)) port = p;
                break;
            }
        }
        if (remote is null) return null;

        return new NodeConfig
        {
            Id = NodeConfig.NewId(),
            Name = nameOverride ?? remote,
            Host = remote,
            Port = port,
            Kind = NodeKind.OpenVpn,
            Source = "импорт .ovpn"
        };
    }

    /// <summary>Импорт узла из файла конфигурации на диске.</summary>
    public static NodeConfig? ImportConfigFile(string path, string? nameOverride = null)
    {
        string text = File.ReadAllText(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".conf" => ParseWireGuardConf(text, nameOverride, Path.GetFileName(path)),
            ".ovpn" => ParseOpenVpnConf(text, nameOverride),
            _ => ParseWireGuardConf(text, nameOverride, Path.GetFileName(path))
        };
    }

    // ---------------------------------------------------------- DTO для JSON/YAML

    private sealed class JsonFile
    {
        public List<NodeConfig>? Nodes { get; set; }
    }

    private sealed class YamlFile
    {
        public List<YamlNode>? Nodes { get; set; }
    }

    private sealed class YamlNode
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Country { get; set; }
        public string? City { get; set; }
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? Kind { get; set; }
        public string? PrivateKey { get; set; }
        public string? PublicKey { get; set; }
        public string? PresharedKey { get; set; }
        public string? Address { get; set; }
        public string? Dns { get; set; }
        public int? Mtu { get; set; }
        public int? PersistentKeepalive { get; set; }
        public List<string>? AllowedIps { get; set; }
        public string? ConfigPath { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? SshKeyPath { get; set; }
        public int? SocksPort { get; set; }
        public List<string>? Tags { get; set; }
        public bool? Enabled { get; set; }
        public int? Priority { get; set; }
        public string? Notes { get; set; }
        public string? Source { get; set; }

        public static YamlNode From(NodeConfig c) => new()
        {
            Id = c.Id,
            Name = c.Name,
            Country = c.Country,
            City = c.City,
            Host = c.Host,
            Port = c.Port,
            Kind = c.Kind.ToString(),
            PrivateKey = c.PrivateKey,
            PublicKey = c.PublicKey,
            PresharedKey = c.PresharedKey,
            Address = c.Address,
            Dns = c.Dns,
            Mtu = c.Mtu,
            PersistentKeepalive = c.PersistentKeepalive,
            AllowedIps = c.AllowedIps.ToList(),
            ConfigPath = c.ConfigPath,
            Username = c.Username,
            Password = c.Password,
            SshKeyPath = c.SshKeyPath,
            SocksPort = c.SocksPort,
            Tags = c.Tags.ToList(),
            Enabled = c.Enabled,
            Priority = c.Priority,
            Notes = c.Notes,
            Source = c.Source
        };

        public NodeConfig ToConfig() => new()
        {
            Id = string.IsNullOrWhiteSpace(Id) ? NodeConfig.NewId() : Id!,
            Name = Name ?? "Node",
            Country = Country ?? "",
            City = City ?? "",
            Host = Host ?? "",
            Port = Port ?? 51820,
            Kind = Enum.TryParse<NodeKind>(Kind, ignoreCase: true, out var k) ? k : NodeKind.WireGuard,
            PrivateKey = PrivateKey,
            PublicKey = PublicKey,
            PresharedKey = PresharedKey,
            Address = Address,
            Dns = Dns,
            Mtu = Mtu ?? 1420,
            PersistentKeepalive = PersistentKeepalive ?? 25,
            AllowedIps = AllowedIps?.ToArray() ?? new[] { "0.0.0.0/0" },
            ConfigPath = ConfigPath,
            Username = Username,
            Password = Password,
            SshKeyPath = SshKeyPath,
            SocksPort = SocksPort ?? 1080,
            Tags = Tags?.ToArray() ?? Array.Empty<string>(),
            Enabled = Enabled ?? true,
            Priority = Priority ?? 0,
            Notes = Notes,
            Source = Source
        };
    }
}

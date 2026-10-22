using System.Text;

namespace ExitBoost.Core.Vpn;

/// <summary>
/// Разбор ключей-ссылок, как в Happ: vless://, vmess://, trojan://, ss://.
/// Возвращает ключ либо понятную ошибку по-русски.
/// </summary>
public static class VpnKeyParser
{
    /// <summary>Разобрать одну ссылку. null — если ссылка не распознана, тогда reason объясняет почему.</summary>
    public static VpnKey? Parse(string? link, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(link)) { reason = "Пустая строка"; return null; }

        string text = link.Trim();
        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            reason = "Не похоже на ключ: нет префикса vless://, vmess://, trojan:// или ss://";
            return null;
        }

        // Ссылки-подписки (http/https) разбираются скачиванием — см. VpnSubscriptionLoader.
        if (VpnSubscriptionLoader.IsSubscriptionLink(text))
        {
            reason = "ссылка-подписка: ключи будут скачаны по ней";
            return null;
        }

        string protocol = text[..schemeEnd];
        if (!VpnProtocolParser.TryParse(protocol, out var proto))
        {
            reason = $"Неизвестный протокол «{protocol}». Поддерживаются vless, vmess, trojan и ss";
            return null;
        }

        try
        {
            string rest = text[(schemeEnd + 3)..];
            VpnKey? key = proto switch
            {
                VpnProtocol.Vless or VpnProtocol.Trojan => ParseStandard(rest, proto),
                VpnProtocol.Vmess => ParseVmess(text),
                VpnProtocol.Shadowsocks => ParseShadowsocks(text),
                VpnProtocol.Hysteria2 => ParseHysteria2(rest),
                _ => null
            };
            if (key is null && reason.Length == 0)
                reason = "Не удалось разобрать ключ";
            return key;
        }
        catch (Exception ex)
        {
            reason = $"Ошибка разбора ключа: {ex.Message}";
            return null;
        }
    }

    /// <summary>Одна строка может содержать ключи построчно (как из клипборда клубных подписок).</summary>
    public static List<VpnKey> ParseMany(string text, out List<string> errors)
    {
        var keys = new List<VpnKey>();
        errors = [];
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = Parse(line, out var reason);
            if (key is not null) keys.Add(key);
            else errors.Add($"{line[..Math.Min(48, line.Length)]}…: {reason}");
        }
        return keys;
    }

    // ---- vless://uuid@host:port?params#имя  и  trojan://password@host:port?params#имя ----

    /// <summary>Пробелы допускаются только в имени-якорье: экранируем их, чтобы не потерять их в имени.</summary>
    private static string Normalize(string link)
    {
        int hash = link.IndexOf('#');
        if (hash < 0) return link;
        return link[..(hash + 1)] + link[(hash + 1)..].Replace(" ", "%20");
    }

    private static VpnKey ParseStandard(string link, VpnProtocol proto)
    {
        link = Normalize(link);

        // имя (якорь)
        string name = "";
        int hash = link.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(link[(hash + 1)..]).Trim();
            link = link[..hash];
        }

        // параметры
        string query = "";
        int qmark = link.IndexOf('?');
        if (qmark >= 0)
        {
            query = link[(qmark + 1)..];
            link = link[..qmark];
        }

        // uuid/пароль @ сервер:порт
        int at = link.IndexOf('@');
        if (at <= 0)
            throw new InvalidOperationException("в ключе нет идентификатора (uuid или пароля)");
        string userinfo = Uri.UnescapeDataString(link[..at].Trim());
        string hostport = link[(at + 1)..].Trim().TrimEnd('/');

        if (userinfo.Length == 0)
            throw new InvalidOperationException("в ключе нет идентификатора (uuid или пароля)");

        // адрес/порт (учёт [IPv6])
        string address;
        int port;
        if (hostport.StartsWith('['))
        {
            int close = hostport.IndexOf(']');
            if (close < 0) throw new InvalidOperationException("некорректный IPv6-адрес в ключе");
            address = hostport[1..close];
            int colon = hostport.IndexOf(':', close + 1);
            port = colon > 0 && int.TryParse(hostport[(colon + 1)..], out var p6) ? p6 : 443;
        }
        else
        {
            int colon = hostport.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hostport[(colon + 1)..], out var p4))
            {
                // после порта не бывает других частей — хвост вида ":443?" уже срезан выше
                address = hostport[..colon];
                port = p4;
            }
            else
            {
                address = hostport;
                port = 443;
            }
        }

        if (address.Length == 0)
            throw new InvalidOperationException("в ключе нет адреса сервера");
        if (port <= 0 || port > 65535)
            throw new InvalidOperationException($"в ключе неверный порт: {port}");

        string type = Param(query, "type") ?? "";
        string security = Param(query, "security") ?? "";
        string hostHeader = Param(query, "host") ?? "";
        string sni = Param(query, "sni") ?? "";
        string path = Param(query, "path") ?? Param(query, "serviceName") ?? "";

        var key = new VpnKey
        {
            Protocol = proto,
            UserId = userinfo,
            Address = address,
            Port = port,
            Name = name.Length > 0 ? name : address,
            Security = security switch
            {
                "reality" => "reality",
                "tls" => "tls",
                "none" or "" => proto == VpnProtocol.Trojan || port == 443 ? "tls" : "none",
                _ => security
            },
            Transport = type.Length > 0 ? type : "tcp",
            Flow = Param(query, "flow") ?? "",
            Sni = sni,
            Fingerprint = Param(query, "fp") ?? "",
            PublicKey = Param(query, "pbk") ?? "",
            ShortId = Param(query, "sid") ?? "",
            Path = path,
            HostHeader = hostHeader,
            Encryption = Param(query, "encryption") ?? (proto == VpnProtocol.Vless ? "none" : ""),
            Alpn = Param(query, "alpn") ?? "",
        };

        if (key.Security == "reality" && key.Transport is "ws" or "http") key.Transport = "tcp";
        if (key.Security == "tls" && key.Sni.Length == 0)
            key.Sni = key.HostHeader.Length > 0 ? key.HostHeader : key.Address;
        key.RawLink = link;
        return key;
    }

    /// <summary>Значение параметра из строки запроса вида a=1&amp;b=2 (без System.Uri).</summary>
    private static string? Param(string query, string field)
    {
        if (query.Length == 0) return null;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq].Trim().Equals(field, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(eq + 1)..]).Trim();
        }
        return null;
    }

    // ---- vmess://BASE64(json) ----

    private static VpnKey ParseVmess(string link)
    {
        string payload = link["vmess://".Length..].Trim();
        string json = DecodeBase64(payload);
        if (!json.TrimStart().StartsWith('{'))
            throw new InvalidOperationException("vmess-ключ не закодирован в base64");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        string GetText(string field) =>
            root.TryGetProperty(field, out var v)
                ? (v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetRawText() : v.ToString())
                : "";

        string add = GetText("add");
        if (string.IsNullOrWhiteSpace(add))
            throw new InvalidOperationException("в vmess-ключе нет адреса сервера");

        string portText = GetText("port");
        if (!int.TryParse(portText, out int port) || port <= 0) port = 443;

        string net = GetText("net");
        if (net.Length == 0) net = "tcp";
        if (net == "h2") net = "http";

        var key = new VpnKey
        {
            Protocol = VpnProtocol.Vmess,
            Address = add,
            Port = port,
            UserId = GetText("id"),
            Name = string.IsNullOrWhiteSpace(GetText("ps")) ? add : GetText("ps"),
            Security = GetText("tls") is "tls" or "true" ? "tls" : "none",
            Transport = net,
            Path = GetText("path"),
            HostHeader = string.IsNullOrWhiteSpace(GetText("host")) ? GetText("add") : GetText("host"),
            Sni = GetText("sni"),
            Fingerprint = GetText("fp"),
            Encryption = string.IsNullOrWhiteSpace(GetText("scy")) ? "auto" : GetText("scy"),
            AlterId = int.TryParse(GetText("aid"), out var aid) ? aid : 0,
        };
        if (key.Security == "tls" && key.Sni.Length == 0) key.Sni = key.HostHeader;
        key.RawLink = link;
        return key;
    }

    // ---- ss:// ----

    private static VpnKey ParseShadowsocks(string link)
    {
        string rest = link["ss://".Length..];

        // отрезаем якорь (имя) и параметры
        string fragment = "";
        int hash = rest.IndexOf('#');
        if (hash >= 0) { fragment = Uri.UnescapeDataString(rest[(hash + 1)..]).Trim(); rest = rest[..hash]; }

        string userinfo, hostport;
        int at = lastUnescapedAt(rest);
        if (at > 0)
        {
            userinfo = rest[..at];
            hostport = rest[(at + 1)..];
        }
        else
        {
            // весь остаток — base64(method:password@host:port); параметры (?plugin=…) отрезаем
            int qmark = rest.IndexOf('?');
            string decoded = DecodeBase64(qmark >= 0 ? rest[..qmark] : rest);
            int innerAt = decoded.LastIndexOf('@');
            if (innerAt <= 0)
                throw new InvalidOperationException("ss-ключ не содержит адреса сервера");
            userinfo = decoded[..innerAt];
            hostport = decoded[(innerAt + 1)..];
        }

        string methodpass = IsBase64(userinfo)
            ? DecodeBase64(userinfo)
            : Uri.UnescapeDataString(userinfo);

        int colon = methodpass.IndexOf(':');
        if (colon <= 0)
            throw new InvalidOperationException("в ss-ключе нет метода шифрования или пароля");

        int portColon = hostport.LastIndexOf(':');
        if (portColon <= 0)
            throw new InvalidOperationException("ss-ключ не содержит порта");
        string address = hostport[..portColon];
        if (!int.TryParse(hostport[(portColon + 1)..].Trim('[', ']'), out int port) || port <= 0) port = 443;

        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("ss-ключ не содержит адреса сервера");

        var key = new VpnKey
        {
            Protocol = VpnProtocol.Shadowsocks,
            Method = methodpass[..colon],
            UserId = methodpass[(colon + 1)..],
            Address = address,
            Port = port,
            Name = fragment.Length == 0 ? address : fragment,
        };
        key.RawLink = link;
        return key;
    }

    // ---- hysteria2://пароль@host:port/?sni=...#имя ----

    private static VpnKey ParseHysteria2(string link)
    {
        string name = "";
        int hash = link.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(link[(hash + 1)..]).Trim();
            link = link[..hash];
        }

        string query = "";
        int qmark = link.IndexOf('?');
        if (qmark >= 0)
        {
            query = link[(qmark + 1)..];
            link = link[..qmark];
        }

        int at = link.IndexOf('@');
        if (at <= 0)
            throw new InvalidOperationException("в ключе нет пароля");
        string password = Uri.UnescapeDataString(link[..at].Trim());
        string hostport = link[(at + 1)..].Trim().TrimEnd('/');

        int colon = hostport.LastIndexOf(':');
        string address;
        int port;
        if (colon > 0 && int.TryParse(hostport[(colon + 1)..], out var p))
        {
            address = hostport[..colon];
            port = p;
        }
        else
        {
            address = hostport;
            port = 443;
        }
        if (address.Length == 0) throw new InvalidOperationException("в ключе нет адреса сервера");
        if (port <= 0 || port > 65535) throw new InvalidOperationException($"в ключе неверный порт: {port}");

        string sni = Param(query, "sni") ?? "";
        string insecure = Param(query, "insecure") ?? "";

        var key = new VpnKey
        {
            Protocol = VpnProtocol.Hysteria2,
            UserId = password,
            Address = address,
            Port = port,
            Name = name.Length > 0 ? name : address,
            Security = "tls",
            Transport = "tcp",
            Sni = sni.Length > 0 ? sni : address,
            ObfsType = Param(query, "obfs") ?? "",
            ObfsPassword = Param(query, "obfs-password") ?? "",
        };
        key.RawLink = link;
        _ = insecure;
        return key;
    }

    // ---- вспомогательное ----

    private static int lastUnescapedAt(string s)
    {
        for (int i = s.Length - 1; i > 0; i--)
            if (s[i] == '@') return i;
        return -1;
    }

    private static bool IsBase64(string s)
    {
        if (s.Length < 6) return false;
        foreach (char c in s)
            if (!(char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '-' || c == '_' || c == '='))
                return false;
        return true;
    }

    /// <summary>base64 / base64url, в том числе без дополнения.</summary>
    private static string DecodeBase64(string input)
    {
        string s = input.Trim().Replace('+', '-').Replace('/', '_');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("некорректная base64-строка в ключе");
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}

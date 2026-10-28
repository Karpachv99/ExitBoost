using System.Text.Json;

namespace ExitBoost.Core.Vpn;

/// <summary>
/// Сборка конфига sing-box из VPN-ключа и правил маршрутизации.
/// <para>Режим split: через VPN идут только выбранные домены/программы,
/// остальное — напрямую (маршрут по умолчанию используется как обычно).</para>
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string VpnOutboundTag = "vpn";
    public const string DirectTag = "direct";

    /// <summary>Порт локального mixed-прокси (for debug/programs manually).</summary>
    public const int MixedPort = 2080;

    /// <summary>Порт службы управления (graceful stop).</summary>
    public const int ControlPort = 9097;

    /// <summary>
    /// Верхний предел доменов, для которых создаются отдельные DNS-правила.
    /// Большие списки остаются на локальном DNS: их размер не должен незаметно
    /// переводить DNS всего компьютера на VPN.
    /// </summary>
    public const int MaxDomainsForDnsRules = 20_000;

    public static string Build(VpnKey key, VpnRoutingConfig routing, string? logOutput = null)
    {
        routing.Normalize();

        bool full = routing.Mode == "full";

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();

            w.WritePropertyName("log");
            w.WriteStartObject();
            w.WriteString("level", "warn");
            if (!string.IsNullOrEmpty(logOutput))
                w.WriteString("output", logOutput); // ядро пишёт журнал само — CLI не держит его пайпы
            w.WriteEndObject();

            w.WritePropertyName("dns");
            w.WriteStartObject();
            w.WritePropertyName("servers");
            w.WriteStartArray();

            // DNS через VPN: иначе провайдер видит запросы и может подменить ответы
            // для заблокированных доменов (в режиме «весь трафик» — для всех).
            w.WriteStartObject();
            w.WriteString("type", "https");
            w.WriteString("tag", "remote");
            w.WriteString("server", "1.1.1.1");
            w.WriteString("detour", VpnOutboundTag);
            w.WriteEndObject();

            w.WriteStartObject();
            w.WriteString("type", "local");
            w.WriteString("tag", "local");
            w.WriteEndObject();
            w.WriteEndArray();

            // В split-режиме не отправляем весь DNS через VPN только потому, что
            // список доменов большой. Иначе приложения, чей трафик идёт напрямую,
            // могут получить адреса CDN, недоступные из их прямого маршрута.
            // Для небольших списков выбранные домены резолвятся через VPN; для
            // больших списков DNS остаётся локальным, а route-правила по доменам
            // продолжают работать для соединений, где sing-box видит домен/SNI.
            bool remoteDnsForAll = full;
            bool useDomainDnsRules = !full
                && routing.Domains.Count > 0
                && routing.Domains.Count <= MaxDomainsForDnsRules;
            if (useDomainDnsRules)
            {
                w.WritePropertyName("rules");
                w.WriteStartArray();
                w.WriteStartObject();
                w.WriteString("server", "remote");
                w.WritePropertyName("domain_suffix");
                w.WriteStartArray();
                foreach (var d in routing.Domains) w.WriteStringValue(d);
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteString("final", remoteDnsForAll ? "remote" : "local");
            w.WriteEndObject();

            w.WritePropertyName("inbounds");
            w.WriteStartArray();

            // TUN-адаптер: принимает трафик, определяет сайт из TLS SNI.
            w.WriteStartObject();
            w.WriteString("type", "tun");
            w.WriteString("tag", "tun0");
            w.WritePropertyName("address");
            w.WriteStartArray();
            w.WriteStringValue("172.19.0.1/30");
            w.WriteEndArray();
            w.WriteNumber("mtu", routing.Mtu);
            w.WriteBoolean("auto_route", true);
            w.WriteBoolean("strict_route", false);
            w.WriteEndObject();

            // Одновременно локальный SOCKS/HTTP-прокси 127.0.0.1:2080 — можно вручную
            // направлять отдельные программы без TUN.
            w.WriteStartObject();
            w.WriteString("type", "mixed");
            w.WriteString("tag", "mixed0");
            w.WriteString("listen", "127.0.0.1");
            w.WriteNumber("listen_port", MixedPort);
            w.WriteEndObject();

            w.WriteEndArray();

            w.WritePropertyName("outbounds");
            w.WriteStartArray();
            WriteVpnOutbound(w, key);
            w.WriteStartObject();
            w.WriteString("type", "direct");
            w.WriteString("tag", DirectTag);
            w.WriteEndObject();
            w.WriteEndArray();

            w.WritePropertyName("route");
            w.WriteStartObject();
            w.WritePropertyName("rules");
            w.WriteStartArray();

            // Определяем сайт из TLS SNI / QUIC — без этого доменные правила не работают.
            w.WriteStartObject();
            w.WriteString("action", "sniff");
            w.WriteEndObject();

            // Локальная сеть всегда напрямую — чтобы не потерять роутер и принтер.
            w.WriteStartObject();
            w.WriteString("outbound", DirectTag);
            w.WritePropertyName("ip_is_private");
            w.WriteBooleanValue(true);
            w.WriteEndObject();

            if (!full)
            {
                if (routing.Domains.Count > 0)
                {
                    w.WriteStartObject();
                    w.WriteString("action", "route");
                    w.WriteString("outbound", VpnOutboundTag);
                    w.WritePropertyName("domain_suffix");
                    w.WriteStartArray();
                    foreach (var d in routing.Domains) w.WriteStringValue(d);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                if (routing.Processes.Count > 0)
                {
                    w.WriteStartObject();
                    w.WriteString("action", "route");
                    w.WriteString("outbound", VpnOutboundTag);
                    w.WritePropertyName("process_name");
                    w.WriteStartArray();
                    foreach (var p in routing.Processes) w.WriteStringValue(p);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            w.WriteBoolean("auto_detect_interface", true); // свои пакеты — мимо TUN, иначе цикл
            w.WriteString("default_domain_resolver", "local"); // адрес VPN-сервера резолвим локально
            w.WriteString("final", full ? VpnOutboundTag : DirectTag);
            w.WriteEndObject();

            // Управление: позволяет останавливать ядро мягко (правильная чистка маршрутов).
            w.WritePropertyName("experimental");
            w.WriteStartObject();
            w.WritePropertyName("clash_api");
            w.WriteStartObject();
            w.WriteString("external_controller", $"127.0.0.1:{ControlPort}");
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteEndObject();
        }

        using var reader = new StreamReader(new MemoryStream(stream.ToArray()));
        return reader.ReadToEnd();
    }

    private static void WriteVpnOutbound(Utf8JsonWriter w, VpnKey key)
    {
        w.WriteStartObject();
        w.WriteString("tag", VpnOutboundTag);
        w.WriteString("server", key.Address);
        w.WriteNumber("server_port", key.Port);

        switch (key.Protocol)
        {
            case VpnProtocol.Vless:
            {
                w.WriteString("type", "vless");
                w.WriteString("uuid", key.UserId);
                if (key.Flow.Length > 0) w.WriteString("flow", key.Flow);
                break;
            }
            case VpnProtocol.Vmess:
            {
                w.WriteString("type", "vmess");
                w.WriteString("uuid", key.UserId);
                w.WriteNumber("alter_id", key.AlterId);
                w.WriteString("security", key.Encryption.Length == 0 ? "auto" : key.Encryption);
                break;
            }
            case VpnProtocol.Trojan:
            {
                w.WriteString("type", "trojan");
                w.WriteString("password", key.UserId);
                break;
            }
            case VpnProtocol.Hysteria2:
            {
                w.WriteString("type", "hysteria2");
                w.WriteString("password", key.UserId);
                break;
            }
            default:
            {
                w.WriteString("type", "shadowsocks");
                w.WriteString("method", key.Method);
                w.WriteString("password", key.UserId);
                break;
            }
        }

        if (key.Protocol == VpnProtocol.Hysteria2)
        {
            // Hysteria2 всегда поверх TLS/QUIC
            w.WritePropertyName("tls");
            w.WriteStartObject();
            w.WriteBoolean("enabled", true);
            w.WriteString("server_name", key.Sni.Length > 0 ? key.Sni : key.Address);
            if (key.AllowInsecure) w.WriteBoolean("insecure", true);
            w.WriteEndObject();

            if (key.ObfsType.Length > 0)
            {
                w.WritePropertyName("obfs");
                w.WriteStartObject();
                w.WriteString("type", key.ObfsType);
                w.WriteString("password", key.ObfsPassword);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            return;
        }

        if (key.Protocol == VpnProtocol.Trojan || key.Protocol == VpnProtocol.Vless || key.Protocol == VpnProtocol.Vmess)
            WriteTlsBlock(w, key);

        WriteTransport(w, key);
        w.WriteEndObject();
    }

    private static void WriteTlsBlock(Utf8JsonWriter w, VpnKey key)
    {
        bool enableTls = key.Security is "tls" or "reality" || key.Protocol == VpnProtocol.Trojan;
        if (!enableTls) return;

        w.WritePropertyName("tls");
        w.WriteStartObject();
        w.WriteBoolean("enabled", true);
        w.WriteString("server_name",
            key.Sni.Length > 0 ? key.Sni :
            key.HostHeader.Length > 0 ? key.HostHeader :
            key.Address);

        if (key.Alpn.Length > 0)
        {
            w.WritePropertyName("alpn");
            w.WriteStartArray();
            foreach (var item in key.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                w.WriteStringValue(item);
            w.WriteEndArray();
        }

        // Reality в sing-box работает только через uTLS. Если в ключе не указан
        // отпечаток — берём chrome, иначе ядро падает с «uTLS is required by reality client».
        string fingerprint = key.Fingerprint;
        if (fingerprint.Length == 0 && key.Security == "reality") fingerprint = "chrome";

        if (key.Security == "reality" && key.PublicKey.Length > 0)
        {
            w.WritePropertyName("reality");
            w.WriteStartObject();
            w.WriteBoolean("enabled", true);
            w.WriteString("public_key", key.PublicKey);
            w.WriteString("short_id", key.ShortId);
            w.WriteEndObject();
        }

        if (fingerprint.Length > 0)
        {
            w.WritePropertyName("utls");
            w.WriteStartObject();
            w.WriteBoolean("enabled", true);
            w.WriteString("fingerprint", fingerprint);
            w.WriteEndObject();
        }

        if (key.AllowInsecure) w.WriteBoolean("insecure", true);

        w.WriteEndObject();
    }

    private static void WriteTransport(Utf8JsonWriter w, VpnKey key)
    {
        string transport = key.Transport == "" ? "tcp" : key.Transport.ToLowerInvariant();
        if (transport == "h2") transport = "http";
        string path = key.Path, host = key.HostHeader.Length > 0 ? key.HostHeader : key.Address;

        switch (transport)
        {
            case "ws":
            {
                w.WritePropertyName("transport");
                w.WriteStartObject();
                w.WriteString("type", "ws");
                w.WriteString("path", path.Length > 0 ? path : "/");
                if (host.Length > 0)
                {
                    w.WritePropertyName("headers");
                    w.WriteStartObject();
                    w.WriteString("Host", host);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                break;
            }
            case "grpc":
            {
                w.WritePropertyName("transport");
                w.WriteStartObject();
                w.WriteString("type", "grpc");
                w.WriteString("service_name", path);
                w.WriteEndObject();
                break;
            }
            case "http":
            {
                w.WritePropertyName("transport");
                w.WriteStartObject();
                w.WriteString("type", "http");
                w.WriteString("path", path.Length > 0 ? path : "/");
                if (host.Length > 0)
                {
                    w.WritePropertyName("host");
                    w.WriteStartArray();
                    w.WriteStringValue(host);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                break;
            }
            case "httpupgrade":
            {
                w.WritePropertyName("transport");
                w.WriteStartObject();
                w.WriteString("type", "httpupgrade");
                w.WriteString("path", path.Length > 0 ? path : "/");
                if (host.Length > 0) w.WriteString("host", host);
                w.WriteEndObject();
                break;
            }
            case "quic":
            {
                w.WritePropertyName("transport");
                w.WriteStartObject();
                w.WriteString("type", "quic");
                w.WriteEndObject();
                break;
            }
            // tcp — обычные пакеты, транспорт не указывается
        }
    }
}

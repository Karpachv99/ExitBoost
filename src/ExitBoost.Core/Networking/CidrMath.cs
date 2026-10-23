using System.Net;
using System.Net.Sockets;

namespace ExitBoost.Core.Networking;

/// <summary>Операции над диапазонами IPv4 в нотации CIDR.</summary>
public static class CidrMath
{
    /// <summary>Частные и служебные диапазоны, которые обычно не нужно блокировать.</summary>
    public static IReadOnlyList<string> PrivateRanges { get; } = new[]
    {
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "100.64.0.0/10"
    };

    /// <summary>
    /// Вычесть исключаемые диапазоны из базового.
    /// Используется, чтобы правило блокировки не затрагивало локальную сеть.
    /// </summary>
    public static IReadOnlyList<string> Subtract(string baseCidr, IEnumerable<string> excluded)
    {
        if (!TryParse(baseCidr, out ulong bStart, out ulong bEnd))
            return new[] { baseCidr };

        var ex = excluded
            .Select(c => TryParse(c, out ulong s, out ulong e) ? (s, e) : (0UL, 0UL))
            .Where(t => t.Item2 >= t.Item1)
            .OrderBy(t => t.Item1)
            .ToList();

        var result = new List<(ulong Start, ulong End)>();
        ulong cur = bStart;

        foreach (var (s, e) in ex)
        {
            if (e < cur) continue;
            if (s > bEnd) break;
            if (s > cur) result.Add((cur, Math.Min(s - 1, bEnd)));
            if (e >= bEnd) { cur = bEnd + 1; break; }
            cur = Math.Max(cur, e + 1);
        }

        if (cur <= bEnd) result.Add((cur, bEnd));

        var outList = new List<string>();
        foreach (var (s, e) in result) EncodeRange(s, e, outList);
        return outList;
    }

    /// <summary>Публичное пространство IPv4 без частных диапазонов.</summary>
    public static IReadOnlyList<string> PublicOnly(IEnumerable<string>? extraExcluded = null)
    {
        var excluded = PrivateRanges.Concat(extraExcluded ?? Array.Empty<string>()).ToList();
        return Subtract("0.0.0.0/0", excluded);
    }

    /// <summary>Разобрать CIDR или одиночный IP в диапазон.</summary>
    public static bool TryParse(string cidr, out ulong start, out ulong end)
    {
        start = end = 0;
        if (string.IsNullOrWhiteSpace(cidr)) return false;

        string[] parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0].Trim(), out var ip)) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

        uint value = ToUInt(ip);
        int prefix = parts.Length > 1 ? int.Parse(parts[1]) : 32;
        if (prefix < 0 || prefix > 32) return false;

        ulong mask = prefix == 0 ? 0UL : (0xFFFFFFFFUL << (32 - prefix)) & 0xFFFFFFFFUL;
        start = value & mask;
        end = start + (prefix == 0 ? 0xFFFFFFFFUL : (0xFFFFFFFFUL >> prefix));
        return true;
    }

    /// <summary>Преобразовать диапазон обратно в список CIDR.</summary>
    public static void EncodeRange(ulong start, ulong end, List<string> output)
    {
        ulong cur = start;
        while (cur <= end && cur <= 0xFFFFFFFFUL)
        {
            ulong maxSize = 1;
            while (cur % (maxSize * 2) == 0 && (maxSize * 2) <= (end - cur + 1) && maxSize < 0x80000000UL)
                maxSize *= 2;

            int prefix = 32 - (int)Math.Log2(maxSize);
            output.Add($"{FromUInt(cur)}/{prefix}");
            cur += maxSize;
            if (cur == 0) break;
        }
    }

    /// <summary>Содержит ли диапазон адрес.</summary>
    public static bool Contains(string cidr, string ip)
    {
        if (!TryParse(cidr, out ulong s, out ulong e)) return false;
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
        ulong v = ToUInt(addr);
        return v >= s && v <= e;
    }

    private static uint ToUInt(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static string FromUInt(ulong v) =>
        $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
}

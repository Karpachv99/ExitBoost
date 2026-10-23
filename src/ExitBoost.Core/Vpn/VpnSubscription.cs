using System.Security.Cryptography;
using System.Text;

namespace ExitBoost.Core.Vpn;

/// <summary>
/// Загрузка ключей по ссылке-подписке — как в Happ: пользователь вставляет
/// адрес вида https://key.f0rtuna.fast/Tu4Z07JPQ_jawCp7, а программа сама
/// скачивает список ключей (ответ может быть base64 или обычным текстом).
/// </summary>
public static class VpnSubscription
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        // некоторые подписки отдают ключи только «клиентам» (v2rayN/Happ)
        http.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/6.45");
        return http;
    }

    /// <summary>
    /// Идентификатор устройства (HWID). Многие сервисы подписок (например, f0rtuna)
    /// по заголовку X-HWID выдают настоящие ключи, а без него — заглушки.
    /// Идентификатор один раз создаётся и хранится локально, поэтому ключ
    /// остаётся привязанным к этой машине.
    /// </summary>
    public static string Hwid
    {
        get
        {
            string path = Path.Combine(AppPaths.DataDirectory, "vpn", "hwid.txt");
            try
            {
                if (File.Exists(path))
                {
                    string saved = File.ReadAllText(path).Trim();
                    if (saved.Length >= 8) return saved;
                }
            }
            catch { }

            string value = GenerateHwid();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, value);
            }
            catch { }
            return value;
        }
    }

    private static string GenerateHwid()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Похоже ли значение на ссылку (http/https).</summary>
    public static bool IsUrl(string? text) =>
        text is not null &&
        (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Скачать содержимое подписки и вернуть текст с ключами
    /// (base64 распаковывается автоматически).
    /// </summary>
    public static async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // сервисы подписок (f0rtuna и подобные) по X-HWID отдают настоящие ключи
        request.Headers.TryAddWithoutValidation("X-HWID", Hwid);
        request.Headers.TryAddWithoutValidation("HWID", Hwid);
        request.Headers.TryAddWithoutValidation("X-Device-Id", Hwid);

        using var resp = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"сервер ответил {resp.StatusCode}");
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return DecodeIfBase64(body);
    }

    /// <summary>Если текст — base64 с ключами, распаковывает; иначе возвращает как есть.</summary>
    public static string DecodeIfBase64(string body)
    {
        string trimmed = body.Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.Contains("://")) return trimmed; // уже готовые ссылки

        string candidate = trimmed
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace(" ", "")
            .Replace("\t", "");
        if (candidate.Length < 16) return trimmed;

        foreach (char c in candidate)
            if (!(char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '-' || c == '_' || c == '='))
                return trimmed;

        try
        {
            string s = candidate.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return trimmed;
            }
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return decoded.Contains("://") ? decoded : trimmed;
        }
        catch
        {
            return trimmed;
        }
    }

    /// <summary>Разобрать текст подписки на ключи; нераспознанные строки — в errors.</summary>
    public static List<VpnKey> ParseAll(string text, out List<string> errors)
    {
        var keys = new List<VpnKey>();
        errors = [];

        string body = DecodeIfBase64(text);
        foreach (var raw in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            var key = VpnKeyParser.Parse(line, out var reason);
            if (key is null)
            {
                errors.Add($"{Shorten(line)}: {reason}");
                continue;
            }
            if (key.UnsupportedReason is not null)
            {
                // например xhttp: такие варианты дублируют обычные, а sing-box их не умеет
                errors.Add($"{key.Name}: пропущено — {key.UnsupportedReason}");
                continue;
            }
            keys.Add(key);
        }
        return keys;
    }

    private static string Shorten(string value) =>
        value.Length <= 48 ? value : value[..48] + "…";
}

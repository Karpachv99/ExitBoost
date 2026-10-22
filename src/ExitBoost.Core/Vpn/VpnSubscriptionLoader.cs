namespace ExitBoost.Core.Vpn;

/// <summary>
/// Ключ-ссылка вида https://…(как у «фортуны»): по ней отдаётся список ключей
/// vless/vmess/trojan/ss (иногда в base64). Скачиваем и разбираем.
/// </summary>
public static class VpnSubscriptionLoader
{
    public static bool IsSubscriptionLink(string? link) =>
        link is not null &&
        (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         link.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public sealed record LoadResult(IReadOnlyList<VpnKey> Keys, string? Error = null)
    {
        public bool Ok => Error is null && Keys.Count > 0;
    }

    /// <summary>Скачивает ключи по ссылке. body — обычные ссылки или base64-блоб.</summary>
    public static async Task<LoadResult> LoadAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ExitBoost/1.0");

            string body = (await http.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
            if (body.Length == 0)
                return new LoadResult([], "по ссылке пусто — проверьте, что ключ ещё действует");

            // base64-блоб вместо списка ссылок?
            if (!body.Contains("://"))
            {
                try
                {
                    body = DecodeLoose(body).Trim();
                }
                catch
                {
                    // оставляем как есть — вдруг это HTML/текст с ключами
                }
            }

            if (!body.Contains("://"))
                return new LoadResult([], "сервер прислал не ключи — проверьте ссылку в браузере");

            var keys = VpnKeyParser.ParseMany(body, out var errors);
            if (keys.Count == 0)
            {
                string first = errors.Count > 0 ? errors[0] : "не распознано ни одного ключа";
                return new LoadResult([], first);
            }
            return new LoadResult(keys);
        }
        catch (OperationCanceledException)
        {
            return new LoadResult([], "скачивание не успело — проверьте интернет");
        }
        catch (Exception ex)
        {
            return new LoadResult([], $"ссылка не отвечает: {ex.Message}");
        }
    }

    /// <summary>base64 / base64url без дополнения, до ~1 МБ.</summary>
    private static string DecodeLoose(string input)
    {
        string s = input.Trim().Replace('+', '-').Replace('/', '_');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}

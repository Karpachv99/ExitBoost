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

    /// <summary>
    /// Скачивает ключи по ссылке. Использует тот же клиент, что и диалог добавления
    /// (с заголовками HWID): многие сервисы подписок без них отдают заглушки.
    /// </summary>
    public static async Task<LoadResult> LoadAsync(string url, CancellationToken ct = default)
    {
        try
        {
            string body = (await VpnSubscription.DownloadAsync(url, ct).ConfigureAwait(false)).Trim();
            if (body.Length == 0)
                return new LoadResult([], "по ссылке пусто — проверьте, что ключ ещё действует");

            if (!body.Contains("://"))
                return new LoadResult([], "сервер прислал не ключи — проверьте ссылку в браузере");

            var keys = VpnSubscription.ParseAll(body, out var errors);
            if (keys.Count == 0)
            {
                string first = errors.Count > 0 ? errors[0] : "не распознано ни одного ключа";
                return new LoadResult([], first);
            }
            return new LoadResult(keys);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
}

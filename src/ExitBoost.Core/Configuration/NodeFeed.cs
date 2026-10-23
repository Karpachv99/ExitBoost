using ExitBoost.Core.Models;

namespace ExitBoost.Core.Configuration;

/// <summary>
/// Загрузка списка узлов по URL (публичные списки конфигов WireGuard).
/// Поддерживаются форматы JSON и YAML с той же структурой, что и у локального
/// файла узлов.
/// </summary>
public static class NodeFeed
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Скачать и разобрать список узлов.</summary>
    public static async Task<IReadOnlyList<NodeConfig>> FetchAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Не указан адрес списка узлов");

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Адрес должен начинаться с http:// или https://");

        string text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);

        var nodes = url.Contains("yaml", StringComparison.OrdinalIgnoreCase) || url.Contains("yml", StringComparison.OrdinalIgnoreCase)
            ? NodeRegistry.ParseYaml(text)
            : NodeRegistry.ParseJson(text);

        foreach (var node in nodes)
        {
            node.Source ??= "публичный список";
            if (string.IsNullOrWhiteSpace(node.Id)) node.Id = NodeConfig.NewId();
        }

        return nodes;
    }

    /// <summary>Скачать одиночный конфиг WireGuard (.conf) по URL.</summary>
    public static async Task<NodeConfig?> FetchWireGuardConfigAsync(string url, CancellationToken ct = default)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Адрес должен начинаться с http:// или https://");

        string text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        return NodeRegistry.ParseWireGuardConf(text, Path.GetFileName(url), "публичный конфиг");
    }
}

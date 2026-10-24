namespace ExitBoost.Core.Updates;

/// <summary>Один диапазон байт для параллельного скачивания.</summary>
public readonly record struct UpdatePart(long From, long To)
{
    public long Length => To - From + 1;
}

/// <summary>
/// Разбиение файла на диапазоны. Количество частей ограничивается параллелизмом,
/// а размер части вычисляется от общего размера — иначе для больших файлов
/// терялся бы «хвост» архива.
/// </summary>
public static class UpdatePlanner
{
    public const int DefaultMaxParts = 6;
    public const long DefaultMinChunk = 4L * 1024 * 1024;

    public static IReadOnlyList<UpdatePart> BuildParts(
        long total,
        int maxParts = DefaultMaxParts,
        long minChunk = DefaultMinChunk)
    {
        if (total <= 0) throw new ArgumentOutOfRangeException(nameof(total));
        if (maxParts < 1) throw new ArgumentOutOfRangeException(nameof(maxParts));
        if (minChunk < 1) throw new ArgumentOutOfRangeException(nameof(minChunk));

        long chunksBySize = (total + minChunk - 1) / minChunk;
        int count = (int)Math.Min(maxParts, Math.Max(1, chunksBySize));
        long chunk = (total + count - 1) / count;

        var parts = new List<UpdatePart>(count);
        for (long from = 0; from < total && parts.Count < count; from += chunk)
        {
            long to = Math.Min(total - 1, from + chunk - 1);
            parts.Add(new UpdatePart(from, to));
        }
        return parts;
    }
}

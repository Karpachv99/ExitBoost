using ExitBoost.Core.Models;

namespace ExitBoost.Core.Diagnostics;

/// <summary>Веса критерия выбора узла.</summary>
public sealed record ScoreWeights
{
    /// <summary>Вес средней задержки.</summary>
    public double Latency { get; init; } = 1.0;

    /// <summary>Вес джиттера.</summary>
    public double Jitter { get; init; } = 2.0;

    /// <summary>Вес потерь (1% потерь ≈ 12 "мс эквивалента").</summary>
    public double Loss { get; init; } = 12.0;

    public static ScoreWeights Default { get; } = new();
}

/// <summary>
/// Алгоритм выбора лучшего узла. Полностью повторяет логику ExitLag:
/// решает не минимальный пинг, а взвешенное качество канала — потери и
/// джиттер бьют по геймплею сильнее, чем абсолютная задержка.
/// </summary>
public static class NodeSelector
{
    /// <summary>Балл узла: меньше — лучше.</summary>
    public static double ComputeScore(NodeStatus status, ScoreWeights? weights = null)
    {
        var w = weights ?? ScoreWeights.Default;
        if (!status.HasData) return double.MaxValue / 4;

        double latency = status.AvgPingMs > 0 ? status.AvgPingMs : status.TcpPingMs;
        double score = latency * w.Latency + status.JitterMs * w.Jitter + status.LossPercent * w.Loss;

        // Узлы, которые не отвечают вообще, принудительно в конец рейтинга.
        if (status.LossPercent >= 80) score += 100_000;

        // Небольшой бонус за ручное повышение приоритета.
        score -= Math.Clamp(status.Config.Priority, -50, 50) * 0.5;
        return score;
    }

    /// <summary>Ранжирование узлов: от лучшего к худшему.</summary>
    public static IReadOnlyList<NodeStatus> Rank(IEnumerable<NodeStatus> statuses, ScoreWeights? weights = null)
    {
        var list = statuses.ToList();
        foreach (var s in list) s.Score = ComputeScore(s, weights);
        return list
            .OrderBy(s => s.Score)
            .ThenBy(s => s.AvgPingMs)
            .ThenBy(s => s.Config.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Выбор лучшего узла. null — если нет ни одного пригодного узла.</summary>
    public static NodeStatus? SelectBest(IEnumerable<NodeStatus> statuses, ScoreWeights? weights = null)
    {
        var ranked = Rank(statuses.Where(s => s.Config.Enabled), weights);
        return ranked.FirstOrDefault();
    }

    /// <summary>Узлы, пригодные для автоматического выбора.</summary>
    public static bool IsUsable(NodeStatus status) =>
        status.HasData && status.LossPercent < 80;
}

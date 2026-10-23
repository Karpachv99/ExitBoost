namespace ExitBoost.Core.Models;

/// <summary>Живые показатели качества канала до узла.</summary>
public sealed class NodeStatus
{
    public required NodeConfig Config { get; set; }

    /// <summary>Последний успешный ping, мс.</summary>
    public double LastPingMs { get; set; }

    /// <summary>Средний ping по окну измерений, мс.</summary>
    public double AvgPingMs { get; set; }

    /// <summary>Минимальный ping по окну измерений, мс.</summary>
    public double MinPingMs { get; set; }

    /// <summary>Максимальный ping по окну измерений, мс.</summary>
    public double MaxPingMs { get; set; }

    /// <summary>Джиттер (среднее абсолютное отклонение между соседними успешными замерами), мс.</summary>
    public double JitterMs { get; set; }

    /// <summary>Задержка установления TCP-соединения до порта узла, мс.</summary>
    public double TcpPingMs { get; set; }

    /// <summary>Процент потерянных пакетов, 0..100.</summary>
    public double LossPercent { get; set; }

    /// <summary>Всего замеров в окне.</summary>
    public int SampleCount { get; set; }

    /// <summary>Неудачных замеров в окне.</summary>
    public int FailureCount { get; set; }

    public NodeState State { get; set; } = NodeState.Unknown;

    public string StatusText { get; set; } = "не измерен";

    public DateTime LastProbeUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Итоговый балл: меньше — лучше.</summary>
    public double Score { get; set; }

    public bool HasData => SampleCount > 0;

    public ProbeQuality Quality
    {
        get
        {
            if (!HasData) return ProbeQuality.Unknown;
            if (LossPercent >= 25 || AvgPingMs >= 300) return ProbeQuality.Poor;
            if (LossPercent >= 10 || AvgPingMs >= 180) return ProbeQuality.Fair;
            if (LossPercent >= 3 || AvgPingMs >= 90) return ProbeQuality.Good;
            return ProbeQuality.Excellent;
        }
    }

    public string QualityText => Quality switch
    {
        ProbeQuality.Excellent => "отлично",
        ProbeQuality.Good => "хорошо",
        ProbeQuality.Fair => "средне",
        ProbeQuality.Poor => "плохо",
        _ => "—"
    };

    public void Reset()
    {
        LastPingMs = AvgPingMs = MinPingMs = MaxPingMs = JitterMs = TcpPingMs = 0;
        LossPercent = 0;
        SampleCount = FailureCount = 0;
        Score = 0;
        State = NodeState.Unknown;
        StatusText = "не измерен";
    }
}

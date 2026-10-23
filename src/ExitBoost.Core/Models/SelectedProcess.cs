namespace ExitBoost.Core.Models;

/// <summary>Описание запущенного процесса, доступного для выбора как целевого (игры).</summary>
public sealed class SelectedProcess
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public string? WindowTitle { get; init; }
    public string DisplayName =>
        string.IsNullOrWhiteSpace(WindowTitle) ? $"{Name} ({Pid})" : $"{WindowTitle} — {Name}";

    public override string ToString() => DisplayName;
}

/// <summary>Активное соединение процесса.</summary>
public sealed record ProcessEndpoint(string Address, int Port, string Protocol);

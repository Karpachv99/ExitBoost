using ExitBoost.Core.Dpi;

namespace ExitBoost.App.ViewModels;

/// <summary>Стратегия zapret с результатами её проверки.</summary>
public sealed class DpiStrategyItemViewModel : ObservableObject
{
    public DpiStrategyItemViewModel(DpiStrategy strategy)
    {
        Strategy = strategy;
    }

    public DpiStrategy Strategy { get; }

    public string Name => Strategy.Name;

    public string Description => Strategy.Description;

    public string Command => Strategy.CommandLine;

    public string Source => Strategy.Source;

    private bool _success;
    private string _result = "не проверена";
    private double _score;
    private string _scoreText = "—";

    public bool Success
    {
        get => _success;
        private set => Set(ref _success, value);
    }

    public string Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public double Score
    {
        get => _score;
        private set => Set(ref _score, value);
    }

    /// <summary>Словесная оценка стратегии — понятнее, чем цифра балла.</summary>
    public string ScoreText
    {
        get => _scoreText;
        private set => Set(ref _scoreText, value);
    }

    /// <summary>Обновить показатели по результату проверки.</summary>
    public void Apply(DpiStrategyResult result)
    {
        Success = result.Success;
        Result = result.ResultText;
        Score = result.Score;
        ScoreText = Verdict(result);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
    }

    private static string Verdict(DpiStrategyResult result)
    {
        if (!result.Success) return "не работает";

        // Оценка по средней задержке TLS-рукопожатия — то, что чувствует пользователь.
        return result.AverageTlsMs switch
        {
            <= 100 => "отлично",
            <= 200 => "хорошо",
            <= 400 => "средне",
            _ => "медленно"
        };
    }
}

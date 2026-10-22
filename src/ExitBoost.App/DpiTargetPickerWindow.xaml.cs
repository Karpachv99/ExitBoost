using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using ExitBoost.App.Support;
using ExitBoost.App.ViewModels;

namespace ExitBoost.App;

/// <summary>
/// Диалог выбора доменов для обхода DPI: популярные сайты, история запущенного браузера,
/// запущенные программы (домены популярных сервисов подставляются автоматически).
/// </summary>
public partial class DpiTargetPickerWindow : Window
{
    public PickerViewModel Model { get; }

    public DpiTargetPickerWindow(string initialDomains)
    {
        InitializeComponent();
        Model = new PickerViewModel(initialDomains);
        DataContext = Model;
    }

    /// <summary>Выбранные домены (после диалога с DialogResult=true).</summary>
    public IReadOnlyList<string> ResultDomains => Model.SelectedDomains;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>Один чекбокс домена.</summary>
public sealed class PickerItem : ObservableObject
{
    public string Domain { get; init; } = "";

    private bool _checked;
    private string _display = "";
    public PickerViewModel? Model { get; set; }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (!Set(ref _checked, value)) return;
            Model?.RefreshSummary();
        }
    }

    /// <summary>Текст чекбокса: домен + пояснение (для истории браузера).</summary>
    public string Display
    {
        get => _display;
        set => Set(ref _display, value);
    }
}

/// <summary>Группа доменов одного сервиса (Discord, YouTube…).</summary>
public sealed class PickerService
{
    public string Name { get; init; } = "";
    public string Hint { get; init; } = "";
    public bool IsDefault { get; init; }
    public ObservableCollection<PickerItem> Domains { get; } = new();
}

/// <summary>Запущенный процесс в списке выбора.</summary>
public sealed record ProcessPick(string Name, string Title)
{
    public string Display => Title.Length > 0 ? $"{Name} — {Title}" : Name;
}

public sealed class PickerViewModel : ObservableObject
{
    public PickerViewModel(string initialDomains)
    {
        LoadHistoryCommand = new RelayCommand(() => LoadHistory());
        PickProcessCommand = new RelayCommand(() => ApplyProcessSelection());

        var preselected = initialDomains
            .Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim().TrimStart('.').ToLowerInvariant())
            .ToHashSet();

        foreach (var (name, hint, isDefault, domains) in BuildServices())
        {
            var svc = new PickerService { Name = name, Hint = hint, IsDefault = isDefault };
            foreach (string domain in domains)
            {
                var item = new PickerItem
                {
                    Domain = domain,
                    Display = domain,
                    Checked = preselected.Contains(domain.ToLowerInvariant()),
                    Model = this
                };
                svc.Domains.Add(item);
                _allItems.Add(item);
            }
            _servicesAll.Add(svc);
        }

        foreach (var b in BrowserHistory.DetectRunning()) RunningBrowsers.Add(b);
        SelectedBrowser = RunningBrowsers.FirstOrDefault();

        _processesAll.AddRange(GetProcesses());

        ProcessHint = "Выберите запущенную программу — домены популярных сервисов отметятся автоматически.\n" +
                      "Для неизвестных программ добавьте домен вручную в поле «Что обходим».";

        ApplyFilter();
        RefreshSummary();
    }

    public ObservableCollection<PickerService> Services { get; } = new();
    public ObservableCollection<PickerItem> BrowserDomains { get; } = new();
    public ObservableCollection<BrowserHistory.BrowserInfo> RunningBrowsers { get; } = new();
    public ObservableCollection<ProcessPick> Processes { get; } = new();

    private readonly List<PickerItem> _allItems = new();
    private readonly List<PickerService> _servicesAll = new();
    private readonly List<PickerItem> _browserAll = new();
    private readonly List<ProcessPick> _processesAll = new();

    private string _searchText = "";
    /// <summary>Поиск по сайтам, доменам истории браузера и программам.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    /// <summary>
    /// Пересобрать видимые списки с учётом поискового запроса.
    /// Внутри групп используются те же экземпляры PickerItem, поэтому
    /// отметки галочек при фильтрации не сбрасываются.
    /// </summary>
    private void ApplyFilter()
    {
        string q = _searchText.Trim();
        bool empty = q.Length == 0;
        var cmp = StringComparison.OrdinalIgnoreCase;
        bool Match(string? s) => !empty && s is not null && s.Contains(q, cmp);

        Services.Clear();
        foreach (var svc in _servicesAll)
        {
            bool nameMatch = empty || Match(svc.Name);
            IEnumerable<PickerItem> matched = nameMatch
                ? svc.Domains
                : svc.Domains.Where(d => Match(d.Domain));
            if (!nameMatch && !matched.Any()) continue;

            var visible = new PickerService { Name = svc.Name, Hint = svc.Hint, IsDefault = svc.IsDefault };
            foreach (var it in matched) visible.Domains.Add(it);
            Services.Add(visible);
        }

        BrowserDomains.Clear();
        foreach (var it in _browserAll.Where(i => empty || Match(i.Domain)))
            BrowserDomains.Add(it);

        Processes.Clear();
        foreach (var p in _processesAll.Where(p => empty || Match(p.Name) || Match(p.Title)))
            Processes.Add(p);
    }

    public RelayCommand LoadHistoryCommand { get; }
    public RelayCommand PickProcessCommand { get; }

    private BrowserHistory.BrowserInfo? _selectedBrowser;
    public BrowserHistory.BrowserInfo? SelectedBrowser
    {
        get => _selectedBrowser;
        set => Set(ref _selectedBrowser, value);
    }

    private ProcessPick? _selectedProcess;
    public ProcessPick? SelectedProcess
    {
        get => _selectedProcess;
        set => Set(ref _selectedProcess, value);
    }

    private string _processHint = "";
    public string ProcessHint
    {
        get => _processHint;
        set => Set(ref _processHint, value);
    }

    private string _selectionSummary = "";
    public string SelectionSummary
    {
        get => _selectionSummary;
        set => Set(ref _selectionSummary, value);
    }

    public IReadOnlyList<string> SelectedDomains =>
        _allItems.Where(i => i.Checked).Select(i => i.Domain)
            .Union(_browserAll.Where(i => i.Checked).Select(i => i.Domain), StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void RefreshSummary() => SelectionSummary = $"Выбрано доменов: {SelectedDomains.Count}";

    private void LoadHistory()
    {
        if (SelectedBrowser is null)
        {
            MessageBox.Show(
                "Запущенный браузер не обнаружен. Откройте браузер и попробуйте снова, либо выберите сайты на первой вкладке.",
                "История браузера", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var browser = SelectedBrowser;
        BrowserDomains.Clear();
        _browserAll.Clear();

        var domains = BrowserHistory.LoadDomains(browser);
        foreach (var d in domains)
        {
            _browserAll.Add(new PickerItem
            {
                Domain = d.Host,
                Display = d.Host + (d.Visits > 1 ? $"  (посещений: {d.Visits})" : ""),
                Model = this
            });
        }

        if (_browserAll.Count == 0)
        {
            MessageBox.Show(
                $"{browser.Name}: не удалось прочитать историю (файл занят браузером или профиль защищён).\n" +
                "Можно выбрать сайты вручную на первой вкладке.",
                "История пуста", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        ApplyFilter();
        RefreshSummary();
    }

    private void ApplyProcessSelection()
    {
        if (SelectedProcess is null) return;

        string name = SelectedProcess.Name.ToLowerInvariant();
        string? service = KnownPrograms.FirstOrDefault(g => name.Contains(g.Key)).Service;

        if (service is null)
        {
            ProcessHint = $"Для «{SelectedProcess.Display}» известные домены не определены — узнайте домен сервиса и впишите его в поле «Что обходим».";
            return;
        }

        var svc = _servicesAll.FirstOrDefault(s => s.Name == service);
        if (svc is null)
        {
            ProcessHint = $"Сервис «{service}» отсутствует в списке.";
            return;
        }

        foreach (var d in svc.Domains) d.Checked = true;
        ProcessHint = $"«{SelectedProcess.Display}» → сервис «{service}»: отмечено {svc.Domains.Count} доменов (вкладка «Популярные сайты»).";
    }

    private static IEnumerable<ProcessPick> GetProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Where(p => p.SessionId >= 0)
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var p = g.OrderByDescending(x => (x.MainWindowTitle ?? "").Length).First();
                    var title = p.MainWindowTitle ?? "";
                    if (title.Length > 55) title = title[..55] + "…";
                    return new ProcessPick(g.Key, title);
                })
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Программа → сервис, чьи домены известны.</summary>
    private static readonly (string Key, string Service)[] KnownPrograms =
    [
        ("discord", "Discord"),
        ("steam", "Steam"),
        ("epicgameslauncher", "Epic Games"),
        ("epic", "Epic Games"),
        ("riotclientservices", "Riot Games"),
        ("valorant", "Riot Games"),
        ("leagueclient", "Riot Games"),
        ("battle.net", "Blizzard Battle.net"),
        ("agent", "Blizzard Battle.net"),
        ("robloxplayerbeta", "Roblox"),
        ("roblox", "Roblox"),
        ("rockstar", "Rockstar Games"),
        ("goggalaxy", "GOG.com"),
        ("ubisoftconnect", "Ubisoft Connect"),
        ("upc", "Ubisoft Connect"),
        ("spotify", "Spotify"),
        ("telegram", "Telegram Web"),
        ("vk", "VK")
    ];

    // Соответствие «программы» из KnownPrograms → группа сайтов ниже.

    private static IEnumerable<(string Name, string Hint, bool Default, List<string> Domains)> BuildServices() =>
    [
        ("Discord", "Клиент, CDN, медиа и голос", true,
            ["discord.com", "discordapp.com", "cdn.discordapp.com", "gateway.discord.gg", "discord.gg", "discord.media", "dl.discordapp.net"]),
        ("YouTube", "Видео, CDN и аватарки", true,
            ["youtube.com", "googlevideo.com", "ytimg.com", "ggpht.com", "youtubei.googleapis.com"]),
        ("Google", "Поиск и сервисы Google", true,
            ["google.com", "gstatic.com", "googleapis.com", "googleusercontent.com"]),
        ("Steam", "Магазин, сообщество и контент", false,
            ["steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com"]),
        ("Twitch", "Стримы", false, ["twitch.tv", "ttvnw.net", "twitchcdn.net"]),
        ("X / Twitter", "Соцсеть X", false, ["x.com", "twitter.com", "twimg.com"]),
        ("Instagram / Facebook", "Сервисы Meta", false,
            ["instagram.com", "cdninstagram.com", "facebook.com", "fbcdn.net"]),
        ("Roblox", "Игровая платформа", false, ["roblox.com", "rbxcdn.com"]),
        ("ChatGPT / OpenAI", "AI-ассистенты", false,
            ["chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com", "claude.ai", "anthropic.com"]),
        ("Telegram Web", "Веб-версия и CDN", false, ["web.telegram.org", "telegram.org"]),
        ("Epic Games", "Магазин и лаунчер", false, ["epicgames.com", "unrealengine.com"]),
        ("Riot Games", "League of Legends, Valorant", false, ["riotgames.com", "leagueoflegends.com"]),
        ("Blizzard", "Battle.net", false, ["battle.net", "blizzard.com"]),
        ("Rockstar Games", "Лаунчер и соцклуб", false, ["rockstargames.com", "cdn.rockstargames.com"]),
        ("GOG", "Магазин GOG.com", false, ["gog.com"]),
        ("Ubisoft", "Ubisoft Connect", false, ["ubisoft.com", "uplay.ubi.com"]),
        ("GitHub", "Репозитории и аватарки", false, ["github.com", "githubusercontent.com", "github.io"]),
        ("Reddit", "Соцсеть Reddit", false, ["reddit.com", "redd.it", "redditstatic.com", "redditmedia.com"]),
        ("Spotify", "Музыка (веб/CDN)", false, ["spotify.com", "scdn.co"]),
        ("VK", "VK и медиа", false, ["vk.com", "userapi.com", "vk-cdn.net"])
    ];
}

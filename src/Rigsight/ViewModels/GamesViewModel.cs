using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A cover in the library: the launcher's picture, else the game's own icon in a tile with its name.</summary>
public sealed partial class GameCard : ObservableObject
{
    public required GameEntry Entry { get; init; }
    public string Name => Entry.Name;
    public ImageSource? Cover { get; init; }
    public ImageSource? Icon { get; init; }
    public bool HasCover => Cover is not null;
    public bool HasIcon => Icon is not null;
    public string Badge { get; init; } = "";
    public bool HasBadge => Badge.Length > 0;
    public string Sub { get; init; } = "";
    /// <summary>Not installed any more: drawn faded, its history kept.</summary>
    public bool Faded => !Entry.IsInstalled;
    [ObservableProperty] private bool _isPlaying;
}

/// <summary>An installed game not played lately, on the shelf under the library.</summary>
public sealed partial class ShelfRow(GameEntry entry, string name, string sub, string size, string drive, ImageSource? thumb, ImageSource? icon, string uninstallTip) : ObservableObject
{
    public GameEntry Entry { get; } = entry;
    public string Name { get; } = name;
    public string Sub { get; } = sub;
    public string Size { get; } = size;
    public string Drive { get; } = drive;
    public ImageSource? Thumb { get; } = thumb;
    public ImageSource? Icon { get; } = icon;
    public string UninstallTip { get; } = uninstallTip;
    public bool HasThumb => Thumb is not null;
    public bool HasFolder => GameLauncher.FolderOf(Entry) is not null;
    /// <summary>Running now: its uninstall button is off.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUninstall))] private bool _isRunning;
    public bool CanUninstall => !IsRunning;
}

/// <summary>A small rounded label over the banner ("Played 24h 31m").</summary>
/// <summary>A figure over the banner, one line: plain lead, the figure in bold, plain trail.</summary>
public sealed record GamePill(string Lead, string Bold, string Trail = "");

/// <summary>A line of "Problems in …": amber only when it counts something that needs a look.</summary>
public sealed record ProblemRow(string Label, string Value, string Note, bool Warm, bool Grey = false);

/// <summary>A label and its value in "Heat and power".</summary>
public sealed record HeatRow(string Label, string Value, Brush? Brush = null);

/// <summary>One line in "Can it run better?": an icon in the part's colour and one statement.</summary>
public sealed record BetterLine(string Glyph, string Text, Brush? Brush);

/// <summary>
/// A line of "How it's set": a group's heading, or a setting named from the game's key with its value and NVIDIA's pick (empty
/// without the NVIDIA App; "Not covered" when NVIDIA leaves the setting out).
/// </summary>
public sealed record SettingLine(bool IsGroup, string Label, string Value = "", string Pick = "", bool Differs = false, bool NotCovered = false)
{
    /// <summary>The third column's width: none without NVIDIA's pick, so the list never leaves an empty strip.</summary>
    public System.Windows.GridLength PickWidth => new(Pick.Length > 0 ? 3 : 0, System.Windows.GridUnitType.Star);
}

/// <summary>One week's bar in "Play time".</summary>
public sealed record WeekBar(string Label, string Value, double Height, bool Current);

/// <summary>A row of "Every play": a play, or a change between two plays (a driver, Windows, the game's update).</summary>
public sealed record PlayRow
{
    public bool IsChange { get; init; }
    public bool IsPlay => !IsChange;
    /// <summary>A play right under a change's box: no line over it (the box already sets it apart).</summary>
    public bool AfterChange { get; init; }
    public bool IsNow { get; init; }
    public string Day { get; init; } = "";
    public string Times { get; init; } = "";
    public string Played { get; init; } = "";
    public string? Fps { get; init; }
    public string? Low { get; init; }
    public bool HasFps => Fps is not null;
    /// <summary>"12 stutters", from every frame of the play (recorded since 2026-10-25); null before.</summary>
    public string? Stutters { get; init; }
    public bool StuttersWarm { get; init; }
    public string? Gpu { get; init; }
    public string? GpuPeak { get; init; }
    public bool HasGpu => Gpu is not null;
    public string? Usual { get; init; }
    public bool UsualWarm { get; init; }
    public string Power { get; init; } = "";
    /// <summary>How a play ended, only when it ended badly ("Game crashed 1h 30m in"); a closed game says nothing.</summary>
    public string? Ended { get; init; }
    public string EndedJoin => Ended is null ? "" : " · ";
    public bool EndedWarm { get; init; }
    /// <summary>A play that ended badly, else one with felt stutters: its AI button asks why ("Why did it end?").</summary>
    public AiQuestion? Ask { get; init; }
    public string AskText { get; init; } = "";
    public bool HasAsk => Ask is not null;
    // A change row
    public string Glyph { get; init; } = "";
    public string Text { get; init; } = "";
    public string When { get; init; } = "";
    public bool IsGameUpdate { get; init; }
}

/// <summary>
/// The Games page: a library of every game on the PC from any launcher (covers the launchers keep, the game's icon
/// where they keep none), and one game opened: how it runs, what holds it back, its problems, heat and power, play
/// time and every play. Built from <see cref="GameHistory"/>; the launchers are read by <see cref="GameLibrary"/>.
/// </summary>
public sealed partial class GamesViewModel : ObservableObject
{
    private readonly ReportService _reports;
    private readonly SettingsModel _settings;
    private readonly LiveData _live;
    private readonly OverlayViewModel _overlay;

    private List<InstalledGame> _installed = [];
    private DateTime _installedRead = DateTime.MinValue;
    private List<GameEntry> _all = [];

    /// <summary>Every game in the library, as last read (for the page's Export box).</summary>
    internal IReadOnlyList<GameEntry> Library => _all;
    private string? _pendingOpen;

    public GamesViewModel(ReportService reports, SettingsModel settings, LiveData live, OverlayViewModel overlay)
    {
        _reports = reports;
        _settings = settings;
        _live = live;
        _overlay = overlay;
        _live.PropertyChanged += OnLiveChanged;
        _overlay.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OverlayViewModel.RtssState)) OnPropertyChanged(nameof(FpsEmpty)); };
    }

    // ── Library ──────────────────────────────────────────────────────────

    public ObservableCollection<GameCard> Cards { get; } = [];
    public ObservableCollection<ShelfRow> Shelf { get; } = [];

    [ObservableProperty] private string _shelfSummary = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _ready;

    /// <summary>recent · most · hard · size</summary>
    [ObservableProperty] private string _sort = "recent";
    /// <summary>all · installed · before</summary>
    [ObservableProperty] private string _filter = "all";

    /// <summary>Words typed in the library's search: a game shows when its name or launcher has every one.</summary>
    [ObservableProperty] private string _search = "";
    /// <summary>Something is typed and no game has it.</summary>
    public bool NoMatch => Ready && _all.Count > 0 && Cards.Count == 0 && Core.TextMatch.Words(Search).Length > 0;

    partial void OnSortChanged(string value) { BuildCards(); _ = RefreshHeroAsync(); }
    partial void OnFilterChanged(string value) { BuildCards(); _ = RefreshHeroAsync(); }
    partial void OnSearchChanged(string value) => BuildCards();

    public bool HasShelf => Shelf.Count > 0;
    public bool IsEmpty => Ready && _all.Count == 0;

    // The banner over the library: the last game played, or the one being played right now.
    [ObservableProperty] private GameEntry? _heroGame;
    [ObservableProperty] private ImageSource? _heroBanner;
    [ObservableProperty] private ImageSource? _heroLogo;
    [ObservableProperty] private ImageSource? _heroIcon;
    [ObservableProperty] private string _heroEyebrow = "";
    [ObservableProperty] private IReadOnlyList<GamePill> _heroPills = [];
    [ObservableProperty] private bool _heroLive;
    public bool HasHero => HeroGame is not null;
    public bool HeroHasBanner => HeroBanner is not null;
    public bool HeroHasLogo => HeroLogo is not null;
    public string HeroName => HeroGame?.Name ?? "";
    partial void OnHeroGameChanged(GameEntry? value) => OnPropertyChanged(nameof(HasHero), nameof(HeroName), nameof(HeroInstalled));
    partial void OnHeroBannerChanged(ImageSource? value) => OnPropertyChanged(nameof(HeroHasBanner));
    partial void OnHeroLogoChanged(ImageSource? value) => OnPropertyChanged(nameof(HeroHasLogo));
    /// <summary>Play shows for an installed game; while the game runs it reads "Running" and can't be pressed.</summary>
    public bool HeroInstalled => HeroGame is { IsInstalled: true };
    public bool HeroNotRunning => !HeroLive;
    partial void OnHeroLiveChanged(bool value) => OnPropertyChanged(nameof(HeroNotRunning));

    private GameStory? _heroStory;

    public async Task LoadAsync()
    {
        IsLoading = true;
        if (DateTime.Now - _installedRead > TimeSpan.FromMinutes(1))
        {
            _installed = await Task.Run(() => GameLibrary.Scan(GameSources.ThisPc(withArt: true)));
            _installedRead = DateTime.Now;
            Core.Apps.AppCatalog.InstalledGames = _installed;
        }
        var installed = _installed;
        var library = await _reports.QueryAsync((db, s) => GameHistory.Library(db, installed, s)) ?? [];
        _all = library;
        var last = library.Where(g => g.LastPlayed is not null).OrderByDescending(g => g.LastPlayed).FirstOrDefault();
        SetRunning(FindRunning());
        var hero = HeroPick() ?? _running ?? last;
        _heroStory = hero is null ? null : await _reports.QueryAsync((db, s) => GameHistory.Story(db, hero, s, DateTime.Now));
        await Task.Run(() => { foreach (var g in library) Prewarm(g); });
        BuildCards();
        BuildShelf();
        BuildHero(HeroPick() ?? last);
        IsLoading = false;
        Ready = true;
        OnPropertyChanged(nameof(IsEmpty));
        if (_pendingOpen is { } key) { _pendingOpen = null; await OpenKeyAsync(key); }
        else if (Opened is { } open && _all.FirstOrDefault(g => g.Key == open.Entry.Key) is { } fresh) await OpenAsync(fresh, quiet: true);
    }

    /// <summary>
    /// The game's own video memory where it was recorded (since 0.31.2); older plays only kept all the card had in use,
    /// every app together, so that says so rather than passing for the game's.
    /// </summary>
    internal static string GraphicsMemory(PlayStats s)
    {
        if (s.VramTotal is not double t) return Lang.T("Not recorded");
        if (s.VramOwnMax is double own) return Lang.T($"up to {own / 1024:0.0} of {t / 1024:0} GB");
        return s.VramMax is double v ? Lang.T($"up to {v / 1024:0.0} of {t / 1024:0} GB · whole card, all apps") : Lang.T("Not recorded");
    }

    /// <summary>The pictures of a game read off the window's thread (they're frozen), so the cards build without a pause.</summary>
    private static void Prewarm(GameEntry g)
    {
        if (g.Installed?.Art.Cover is { } c) GamePictures.Picture(c, 300);
        else GamePictures.Icon(g.ExePath ?? GamePictures.ProgramIn(g.Installed?.InstallDir));
    }

    /// <summary>The library as the filter and order pick it (and the search, when asked), the game running now first.</summary>
    private List<GameEntry> Ordered(bool searched)
    {
        IEnumerable<GameEntry> list = Filter switch
        {
            "installed" => _all.Where(g => g.IsInstalled),
            "before" => _all.Where(g => !g.IsInstalled),
            _ => _all,
        };
        var words = searched ? Core.TextMatch.Words(Search) : [];
        if (words.Length > 0) list = list.Where(g => Core.TextMatch.Has(words, g.Name, LauncherName(g.Launcher)));
        list = Sort switch
        {
            "most" => list.OrderByDescending(g => g.ActiveSec),
            "hard" => list.OrderByDescending(g => g.GpuLoad ?? -1),
            "size" => list.OrderByDescending(g => g.Installed?.SizeBytes ?? -1),
            _ => list.OrderByDescending(g => g.LastPlayed ?? g.Installed?.LastPlayed ?? DateTime.MinValue),
        };
        var playing = PlayingEntry();
        var ordered = list.ToList();
        if (playing is not null && ordered.Remove(playing)) ordered.Insert(0, playing);
        return ordered;
    }

    private void BuildCards()
    {
        var playing = PlayingEntry();
        var ordered = Ordered(searched: true);
        Cards.Clear();
        foreach (var g in ordered) Cards.Add(CardOf(g, playing));
        CountText = Cards.Count == 1 ? Lang.T("1 game") : Lang.T($"{Cards.Count} games");
        OnPropertyChanged(nameof(NoMatch));
    }

    private static GameCard CardOf(GameEntry g, GameEntry? playing)
    {
        var cover = GamePictures.Picture(g.Installed?.Art.Cover, 300);
        string sub = g.HasHistory
            ? $"{Units.Duration(g.ActiveSec)} · {(g.IsInstalled ? Day(g.LastPlayed!.Value) : Lang.T("not installed"))}"
            : g.Installed?.LastPlayed is { } lp ? LastPlayed(lp) : NotPlayedYet;
        return new GameCard
        {
            Entry = g,
            Cover = cover,
            Icon = cover is null ? GamePictures.Icon(g.ExePath ?? GamePictures.ProgramIn(g.Installed?.InstallDir)) : null,
            Badge = GameLauncher.Badge(g.Launcher),
            Sub = sub,
            IsPlaying = ReferenceEquals(g, playing),
        };
    }

    private void BuildShelf()
    {
        var cutoff = DateTime.Now.AddDays(-14);
        // Launcher games only: their size is known. A game found from its own program (OnDisk) has none to show.
        var idle = _all.Where(g => g.Installed is not null && (g.HasHistory ? g.LastPlayed < cutoff : !(g.Installed.LastPlayed >= cutoff)))
            .OrderByDescending(g => g.Installed!.SizeBytes ?? 0).ToList();
        Shelf.Clear();
        foreach (var g in idle)
        {
            var thumb = GamePictures.Picture(g.Installed!.Art.Banner, 240);
            string when = g.HasHistory ? LastPlayed(g.LastPlayed!.Value) : g.Installed.LastPlayed is { } lp ? LastPlayed(lp) : NotPlayedYet;
            Shelf.Add(new ShelfRow(g, g.Name, $"{when} · {LauncherName(g.Launcher)}", Size(g.Installed.SizeBytes),
                OnDrive(DriveOf(g.Installed.InstallDir)), thumb, thumb is null ? GamePictures.Icon(g.ExePath ?? GamePictures.ProgramIn(g.Installed.InstallDir)) : null,
                UninstallThrough(g.Launcher)) { IsRunning = IsRunning(g) });
        }
        double gb = idle.Sum(g => (g.Installed!.SizeBytes ?? 0) / 1e9);
        ShelfSummary = idle.Count == 1 ? Lang.T($"1 game · {gb:0} GB") : Lang.T($"{idle.Count} games · {gb:0} GB");
        OnPropertyChanged(nameof(HasShelf));
    }

    /// <summary>
    /// The banner's game: the first of the library as filtered and ordered (the search aside, so typing doesn't swap
    /// it), which is the game running now when it is in that list. Null when the list is empty.
    /// </summary>
    private GameEntry? HeroPick() => Ordered(searched: false).FirstOrDefault();

    private void BuildHero(GameEntry? last)
    {
        // The game running now takes the banner; its figures are its last play's, like any other.
        var hero = last;
        if (hero is null) { HeroGame = null; return; }
        if (_heroStory?.Entry.Key != hero.Key) _heroStory = null;
        HeroGame = hero;
        HeroLive = IsRunning(hero);
        HeroBanner = GamePictures.Picture(hero.Installed?.Art.Banner, 1600);
        HeroLogo = GamePictures.Picture(hero.Installed?.Art.Logo, 720);
        HeroIcon = HeroBanner is null ? GamePictures.Icon(hero.ExePath) : null;
        var play = _heroStory?.Plays.LastOrDefault();
        // "Continue" only for a game that can be: one no longer installed (Played before) or never played says so.
        HeroEyebrow = HeroLive ? Lang.T("PLAYING NOW")
            : !hero.IsInstalled ? (play is null ? Lang.T("NOT INSTALLED") : Lang.T($"LAST PLAYED {play.StartTime:ddd d MMM} · NOT INSTALLED").ToUpperInvariant())
            : play is not null ? Lang.T($"CONTINUE · LAST PLAYED {play.StartTime:ddd d MMM}").ToUpperInvariant()
            : hero.HasHistory || hero.Installed?.LastPlayed is not null ? Lang.T("CONTINUE")
            : NotPlayedYet.ToUpperInvariant();
        var pills = new List<GamePill>();
        if (play is not null)
        {
            pills.Add(new("", Units.Duration(play.ActiveSec), Lang.T($" last time · {Units.Duration(hero.ActiveSec)} in all")));
            // The graphics card's temperature and its power while played, named (a bare "253 W" said nothing).
            if (play.Stats.Gpu is double gpu) pills.Add(new(Lang.T("Graphics card "), Units.TempShort(gpu), play.Stats.GpuPower is double gw ? $" · {gw:0} W" : ""));
            else if (play.Stats.GpuPower is double w) pills.Add(new(Lang.T("Graphics card "), $"{w:0} W"));
            if (play.Stats.Fps is double fps) pills.Add(new("", $"{fps:0} fps", play.Stats.FpsLow is double low ? Lang.T($" · 1% low {low:0}") : ""));
        }
        HeroPills = pills;
    }

    // ── Running ──────────────────────────────────────────────────────────

    /// <summary>Whether the Games page is in view: only then is the running game looked for every few seconds.</summary>
    public bool IsShown { get; set; }

    private GameEntry? _running;
    private DateTime _runningChecked = DateTime.MinValue;

    /// <summary>The library's game running now, in front or behind another window.</summary>
    private GameEntry? PlayingEntry() => _running;

    /// <summary>The library is read again every load, so a game is the same game by its key, not its object.</summary>
    private bool IsRunning(GameEntry? g) => g is not null && _running?.Key == g.Key;

    /// <summary>
    /// The game in front, else one still running behind another window. The agent names only the app in front, so a
    /// game tabbed out of (to RigSight itself, a browser, Discord) is found by its programs among the running ones.
    /// </summary>
    private GameEntry? FindRunning()
    {
        _runningChecked = DateTime.Now;
        if (_live.Activity is { Category: AppCategory.Game, Path: { } path } && Match(path) is { } front) return front;
        var names = new Dictionary<string, List<GameEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _all)
            foreach (var exe in g.Exes.Append(Path.GetFileName(g.ExePath ?? "")).Where(x => x.Length > 0).Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!names.TryGetValue(exe!, out var games)) names[exe!] = games = [];
                games.Add(g);
            }
        if (names.Count == 0) return null;
        var found = new List<GameEntry>();
        foreach (var p in Process.GetProcesses())
            using (p)
                if (names.TryGetValue(p.ProcessName, out var games)) found.AddRange(games);
        // The one already running stays; else the one played most lately.
        return found.FirstOrDefault(IsRunning) ?? found.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).FirstOrDefault();
    }

    private GameEntry? Match(string path) =>
        _all.FirstOrDefault(g => string.Equals(g.ExePath, path, StringComparison.OrdinalIgnoreCase))
        ?? _all.FirstOrDefault(g => g.Installed?.Contains(path) == true);

    private void SetRunning(GameEntry? running)
    {
        bool same = running?.Key == _running?.Key;
        _running = running;
        if (same) return;
        foreach (var c in Cards) c.IsPlaying = IsRunning(c.Entry);
        foreach (var r in Shelf) r.IsRunning = IsRunning(r.Entry);
        OnPropertyChanged(nameof(OpenedLive));
        OnPropertyChanged(nameof(OpenedNotRunning));
    }

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Ready || !IsShown) return;
        // The app in front changing is checked at once; a game closed behind another window within a few seconds.
        if (e.PropertyName == nameof(LiveData.Activity) || (e.PropertyName == nameof(LiveData.Tick) && DateTime.Now - _runningChecked > TimeSpan.FromSeconds(3)))
        {
            var was = _running?.Key;
            SetRunning(FindRunning());
            if (was == _running?.Key) return;
            // The game started or closed: its cover moves to the front (or back to its place) and the banner follows.
            BuildCards();
            _ = RefreshHeroAsync();
        }
    }

    private int _heroAsk;

    /// <summary>
    /// The banner after a game starts or closes, or the filter or order changes: the first game of the list as shown,
    /// with its last play's figures. Of quick clicks, only the last one's game is put up.
    /// </summary>
    private async Task RefreshHeroAsync()
    {
        int ask = ++_heroAsk;
        // An empty list (nothing played before, say) keeps the last game played up there rather than no banner.
        var hero = HeroPick() ?? _all.Where(g => g.LastPlayed is not null).OrderByDescending(g => g.LastPlayed).FirstOrDefault();
        GameStory? story = _heroStory;
        if (hero is not null && story?.Entry.Key != hero.Key)
            story = await _reports.QueryAsync((db, s) => GameHistory.Story(db, hero, s, DateTime.Now));
        if (ask != _heroAsk) return;
        _heroStory = story;
        BuildHero(hero);
    }

    private static Brush? Brush(string key) => System.Windows.Application.Current?.TryFindResource(key) as Brush;

    // ── One game ─────────────────────────────────────────────────────────

    [ObservableProperty] private GameStory? _opened;
    [ObservableProperty] private bool _isOpening;
    public bool IsLibrary => Opened is null && !IsOpening;
    public bool IsGame => Opened is not null;
    partial void OnOpenedChanged(GameStory? value) => OnPropertyChanged(nameof(IsLibrary), nameof(IsGame), nameof(OpenedLive), nameof(OpenedNotRunning));
    partial void OnIsOpeningChanged(bool value) => OnPropertyChanged(nameof(IsLibrary));

    /// <summary>Where the game was opened from (Reports, Crashes…), for "Back to …"; null from the library itself.</summary>
    [ObservableProperty] private string _backText = Lang.T("Back to library");
    private string? _returnTo;
    public Action<string>? GoBackTo { get; set; }

    [ObservableProperty] private ImageSource? _gameBanner;
    [ObservableProperty] private ImageSource? _gameLogo;
    [ObservableProperty] private ImageSource? _gameIcon;
    [ObservableProperty] private string _gameEyebrow = "";
    [ObservableProperty] private IReadOnlyList<GamePill> _gamePills = [];
    [ObservableProperty] private string _uninstallTip = "";
    public bool GameHasBanner => GameBanner is not null;
    public bool GameHasLogo => GameLogo is not null;
    partial void OnGameBannerChanged(ImageSource? value) => OnPropertyChanged(nameof(GameHasBanner));
    partial void OnGameLogoChanged(ImageSource? value) => OnPropertyChanged(nameof(GameHasLogo));
    public string GameName => Opened?.Entry.Name ?? "";
    public bool GameInstalled => Opened?.Entry.IsInstalled == true;
    public bool GameHasFolder => Opened is { } o && GameLauncher.FolderOf(o.Entry) is not null;
    public bool OpenedLive => Opened is { } o && PlayingEntry() is { } p && p.Key == o.Entry.Key;
    /// <summary>Play and uninstall are on: the opened game isn't running.</summary>
    public bool OpenedNotRunning => !OpenedLive;

    // How it runs
    [ObservableProperty] private bool _hasFps;
    [ObservableProperty] private string _fpsAvg = "";
    [ObservableProperty] private string _fpsLow = "";
    /// <summary>
    /// How smooth it is, from every frame (since 2026-10-25), in place of "Since the last update" once there are frames:
    /// "Mostly smooth" over "12 stutters an hour · 0.1% low 84 fps" (approved mock-up). Empty without frames.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSmooth))] private string _smoothVerdict = "";
    [ObservableProperty] private string _smoothDetail = "";
    public bool HasSmooth => SmoothVerdict.Length > 0;
    public bool ShowSinceUpdate => !HasSmooth;
    partial void OnSmoothVerdictChanged(string value) => OnPropertyChanged(nameof(ShowSinceUpdate));

    // ── How it runs, as one chart (approved mock-up, 2026-10-27) ─────────
    // The plays over a range (Last 7 days, 30 days, 12 months, stepped back, or custom; a point a week past two months),
    // or one play with frame data a minute at a time, on the same scale. The numbers beside it follow what's shown.

    /// <summary>Showing one play (its minutes) rather than the plays over the range.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPlaysChart))] private bool _framesView;
    public bool ShowPlaysChart => !FramesView;
    [RelayCommand] private void ShowFrames() { if (_play is null) return; FramesView = true; _ = ShowRunsAsync(); }
    [RelayCommand] private void ShowPlays() { FramesView = false; _ = ShowRunsAsync(); }
    /// <summary>The switch's second side: "Mon 26 Oct play".</summary>
    [ObservableProperty] private string _framesViewName = "";
    /// <summary>When the play's stutters bunched up and what else was going on then (see StutterCauses), over the chart.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStutterWhy))] private string _stutterWhy = "";
    public bool HasStutterWhy => StutterWhy.Length > 0 && FramesView;
    partial void OnFramesViewChanged(bool value) => OnPropertyChanged(nameof(HasStutterWhy));

    [ObservableProperty] private string _sinceUpdate = "";
    [ObservableProperty] private string _updatedNote = "";
    [ObservableProperty] private IReadOnlyList<RunsPoint> _runsPoints = [];
    [ObservableProperty] private IReadOnlyList<RunsMark> _runsMarks = [];
    [ObservableProperty] private DateTime _runsFrom = DateTime.Now.AddDays(-30);
    [ObservableProperty] private DateTime _runsTo = DateTime.Now;
    [ObservableProperty] private IReadOnlyList<RunsMinute> _playMinutes = [];
    [ObservableProperty] private IReadOnlyList<Stutter> _playStutters = [];
    [ObservableProperty] private DateTime? _playStart;
    /// <summary>What the numbers are for: "Last 30 days · 14 plays", or "Mon 26 Oct, 2 h 28 min".</summary>
    [ObservableProperty] private string _runsScope = "";
    /// <summary>Under a play's smoothness: "765 stutters in this play". Empty over a range.</summary>
    [ObservableProperty] private string _smoothFrom = "";
    /// <summary>The pager's words: the range, or the play shown.</summary>
    [ObservableProperty] private string _runsLabel = "";
    [ObservableProperty] private bool _canRunsPrevious;
    [ObservableProperty] private bool _canRunsNext;
    /// <summary>A play with frame data to open: the switch shows.</summary>
    [ObservableProperty] private bool _hasFramePlays;
    /// <summary>The key's ringed dot: plays drawn one by one (not weeks), some with frame data.</summary>
    [ObservableProperty] private bool _showRingKey;
    /// <summary>The range picked, for the menu's tick: 7, 30 or 365 days, or -1 (custom or stepped back).</summary>
    public int RunsPick => _runsCustom is null && _runsBack == 0 ? _runsDays : -1;
    /// <summary>The earliest a range can start: the game's first play with a frame rate.</summary>
    public DateTime RunsHistoryStart => _withFps.Count > 0 ? _withFps[0].StartTime.Date : DateTime.Now.Date;
    /// <summary>"Custom range…" was picked: the page opens the range editor.</summary>
    public event Action? RunsRangeEditRequested;

    private List<GamePlay> _withFps = [], _framePlays = [];
    private List<RunsMark> _allMarks = [];
    private GamePlay? _play;
    private int _runsDays = 30, _runsBack;
    private (DateTime From, DateTime To)? _runsCustom;
    private long _app;
    private int _runsAsk;

    [RelayCommand]
    private void PickRunsRange(object? days)
    {
        int d = days is int i ? i : int.TryParse(days?.ToString(), out int n) ? n : 30;
        if (d < 0) { RunsRangeEditRequested?.Invoke(); return; }
        (_runsDays, _runsBack, _runsCustom) = (d, 0, null);
        _ = ShowRunsAsync();
    }

    /// <summary>A custom range from the range editor.</summary>
    public void SetRunsCustom(DateTime from, DateTime to)
    {
        _runsCustom = (from, to > DateTime.Now ? DateTime.Now : to);
        _runsBack = 0;
        _ = ShowRunsAsync();
    }

    /// <summary>The range's own length back (or the play before, in a play).</summary>
    [RelayCommand]
    private void RunsPrevious()
    {
        if (FramesView) { StepPlay(-1); return; }
        if (_runsCustom is { } c) _runsCustom = (c.From - (c.To - c.From), c.From); else _runsBack++;
        _ = ShowRunsAsync();
    }

    [RelayCommand]
    private void RunsNext()
    {
        if (FramesView) { StepPlay(1); return; }
        if (_runsCustom is { } c)
        {
            var to = c.To + (c.To - c.From);
            _runsCustom = to >= DateTime.Now ? (DateTime.Now - (c.To - c.From), DateTime.Now) : (c.To, to);
        }
        else _runsBack = Math.Max(0, _runsBack - 1);
        _ = ShowRunsAsync();
    }

    private void StepPlay(int by)
    {
        int at = _play is null ? -1 : _framePlays.IndexOf(_play);
        if (at + by < 0 || at + by >= _framePlays.Count) return;
        _play = _framePlays[at + by];
        _ = ShowRunsAsync();
    }

    /// <summary>A play with frame data, or a week, clicked on the chart.</summary>
    public void OpenFromChart(RunsPoint point)
    {
        if (point.IsWeek)
        {
            var monday = point.End.AddDays(-7).Date;
            SetRunsCustom(monday, monday.AddDays(7) > DateTime.Now ? DateTime.Now : monday.AddDays(7));
            return;
        }
        if (_framePlays.FirstOrDefault(p => p.StartTime == point.At) is not { } play) return;
        _play = play;
        FramesView = true;
        _ = ShowRunsAsync();
    }

    private (DateTime From, DateTime To) RunsRange()
    {
        if (_runsCustom is { } c) return c;
        var to = DateTime.Now.AddDays(-_runsDays * _runsBack);
        return (to.AddDays(-_runsDays), to);
    }

    private static string RangeText(DateTime from, DateTime to) =>
        from.Year == to.Year && to.Year == DateTime.Now.Year ? $"{from:d MMM} – {to:d MMM}" : $"{from:d MMM yyyy} – {to:d MMM yyyy}";

    /// <summary>
    /// What "How it runs" shows now: the plays over the range (a point a week past two months) with their numbers, or the
    /// play picked a minute at a time with its own. Reads the records off the window's thread.
    /// </summary>
    private async Task ShowRunsAsync()
    {
        int ask = ++_runsAsk;
        var (from, to) = RunsRange();
        var inRange = _withFps.Where(p => p.EndTime >= from && p.StartTime <= to).ToList();
        bool weekly = (to - from).TotalDays > 62;
        RunsFrom = from;
        RunsTo = to;
        RunsPoints = weekly
            ? inRange.GroupBy(p => p.StartTime.Date.AddDays(-(((int)p.StartTime.DayOfWeek + 6) % 7))).Select(g => Week(g.Key, g.ToList(), to)).ToList()
            : inRange.Select(p => new RunsPoint(p.StartTime, p.EndTime, p.ActiveSec, p.Stats.Fps!.Value, p.Stats.FpsLow, p.Stats.StuttersAnHour, 1, false,
                p.Stats.FrameMinutes > 0, p.Ending == PlayEnd.PowerCut, p.Stats.Gpu, p.Stats.GpuPower)).ToList();
        RunsMarks = _allMarks;
        ShowRingKey = !weekly && RunsPoints.Any(p => p.CanOpen);
        CanRunsPrevious = FramesView ? _play is not null && _framePlays.IndexOf(_play) > 0 : from > RunsHistoryStart;
        CanRunsNext = FramesView ? _play is not null && _framePlays.IndexOf(_play) < _framePlays.Count - 1 : to < DateTime.Now.AddMinutes(-1);
        OnPropertyChanged(nameof(RunsPick));

        if (FramesView && _play is { } play)
        {
            RunsLabel = $"{play.StartTime:ddd d MMM}, {TimeText.At(play.StartTime, "h:mm tt")}";
            FramesViewName = Lang.T($"{play.StartTime:ddd d MMM} play");
            RunsScope = $"{play.StartTime:ddd d MMM}, {Units.Duration(play.ActiveSec)}";
            Numbers(play.Stats);
            SmoothFrom = play.Stats.Stutters is int n ? (n == 1 ? Lang.T("1 stutter in this play") : Lang.T($"{n} stutters in this play")) : "";
            long app = _app;
            var (minutes, stutters, why) = await _reports.QueryAsync((db, settings) =>
            {
                var m = db.GetFrameMinutes(app, play.Start, play.End + 60).Select(x => new RunsMinute(TimeUtil.FromUnix(x.Ts), x.Fps, x.Low, x.Stutters)).ToList();
                var s = db.GetStutters(app, play.Start * 1000, (play.End + 60) * 1000);
                return (m, s, StutterCauses.Explain(db, app, s, play.Start, play.End + 60, settings));
            });
            if (ask != _runsAsk) return;
            PlayMinutes = minutes ?? [];
            PlayStutters = stutters ?? [];
            PlayStart = play.StartTime;
            StutterWhy = why ?? "";
            return;
        }

        if (_play is { } shown) { PlayStart = shown.StartTime; FramesViewName = Lang.T($"{shown.StartTime:ddd d MMM} play"); }
        RunsLabel = RunsPick switch { 7 => Lang.T("Last 7 days"), 30 => Lang.T("Last 30 days"), 365 => Lang.T("Last 12 months"), _ => RangeText(from, to) };
        RunsScope = (RunsPick switch { 7 => Lang.T("Last 7 days"), 30 => Lang.T("Last 30 days"), 365 => Lang.T("Last 12 months"), _ => RangeText(from, to) })
            + " · " + (inRange.Count == 1 ? Lang.T("1 play") : Lang.T($"{inRange.Count} plays"));
        SmoothFrom = "";
        long game = _app;
        var stats = await _reports.QueryAsync((db, _) => GameHistory.StatsOf(db, game, inRange));
        if (ask != _runsAsk) return;
        Numbers(stats ?? new PlayStats());
    }

    /// <summary>A week of plays as one point: its plays' average and 1% low by time played, and its stutters an hour.</summary>
    /// <param name="to">The range's end: this week's point sits in the middle of its days so far, not past the end.</param>
    private static RunsPoint Week(DateTime monday, List<GamePlay> plays, DateTime to)
    {
        double sec = Math.Max(1, plays.Sum(p => p.ActiveSec));
        double fps = plays.Sum(p => p.Stats.Fps!.Value * p.ActiveSec) / sec;
        var lows = plays.Where(p => p.Stats.FpsLow is not null).ToList();
        double? low = lows.Count == 0 ? null : lows.Sum(p => p.Stats.FpsLow!.Value * p.ActiveSec) / Math.Max(1, lows.Sum(p => p.ActiveSec));
        var framed = plays.Where(p => p.Stats.FrameMinutes > 0).ToList();
        int frameMin = framed.Sum(p => p.Stats.FrameMinutes);
        double? rate = frameMin > 0 ? framed.Sum(p => p.Stats.Stutters ?? 0) * 60.0 / frameMin : null;
        var end = monday.AddDays(7) < to ? monday.AddDays(7) : to;
        return new RunsPoint(monday + (end - monday) / 2, monday.AddDays(7), plays.Sum(p => p.ActiveSec), fps, low, rate, plays.Count, true, true, false, null, null);
    }

    /// <summary>The numbers beside the chart, for what's shown.</summary>
    private void Numbers(PlayStats stats)
    {
        FpsAvg = stats.Fps is double fps ? $"{fps:0}" : "—";
        FpsLow = stats.FpsLow is double low ? $"{low:0}" : "—";
        (SmoothVerdict, SmoothDetail) = SmoothOf(stats);
    }

    /// <summary>Why a game has no frame rate yet, and what to do about it.</summary>
    public string FpsEmpty => _overlay.RtssState switch
    {
        "running" => Lang.T($"No FPS recorded for {GameName} yet. RivaTuner measures a game that starts after it, so its next play will have it."),
        "stopped" => Lang.T("No FPS for these plays. RigSight reads the frame rate from RivaTuner, which isn't running. Start it before the game."),
        "installing" => Lang.T("No FPS for these plays. RivaTuner is being installed; start the game after it's done."),
        _ => Lang.T("No FPS for these plays. RigSight reads the frame rate from RivaTuner Statistics Server, a free tool anti-cheat accepts, and it isn't installed."),
    };
    public bool CanStartRtss => _overlay.CanStartRtss;
    public bool CanInstallRtss => _overlay.CanInstallRtss;
    [RelayCommand] private void StartRtss() => _overlay.StartRtssCommand.Execute(null);
    [RelayCommand] private void InstallRtss() => _overlay.InstallRtssCommand.Execute(null);

    // Can it run better?
    [ObservableProperty] private IReadOnlyList<BetterLine> _better = [];

    [ObservableProperty] private IReadOnlyList<ProblemRow> _problems = [];
    [ObservableProperty] private IReadOnlyList<HeatRow> _heat = [];
    /// <summary>The game itself: launcher, size, drive, folder, version, when it was played (see <see cref="AboutRows"/>).</summary>
    [ObservableProperty] private IReadOnlyList<HeatRow> _about = [];
    [ObservableProperty] private IReadOnlyList<WeekBar> _weeks = [];
    [ObservableProperty] private string _playedMostOn = "";
    [ObservableProperty] private IReadOnlyList<PlayRow> _rows = [];
    public string ProblemsTitle => Lang.T($"Problems in {GameName}");

    /// <summary>Opens a game from another page, by its key (see <see cref="GameEntry.Key"/>) or one of its programs.</summary>
    public async Task OpenKeyAsync(string key, (string Page, string Name)? from = null)
    {
        if (!Ready) { _pendingOpen = key; ComeFrom(from); return; }
        var game = _all.FirstOrDefault(g => g.Key == key)
            ?? _all.FirstOrDefault(g => g.Exes.Contains(key, StringComparer.OrdinalIgnoreCase))
            ?? _all.FirstOrDefault(g => string.Equals(g.ExePath, key, StringComparison.OrdinalIgnoreCase))
            ?? _all.FirstOrDefault(g => string.Equals(g.Name, key, StringComparison.OrdinalIgnoreCase));
        ComeFrom(from);
        if (game is not null) await OpenAsync(game);
    }

    private void ComeFrom((string Page, string Name)? from)
    {
        _returnTo = from?.Page;
        BackText = from is { } f ? Lang.T($"Back to {f.Name}") : Lang.T("Back to library");
    }

    [RelayCommand]
    private Task Open(GameCard card)
    {
        ComeFrom(null);
        return OpenAsync(card.Entry);
    }

    [RelayCommand]
    private Task OpenHero() => HeroGame is { } g ? OpenFromLibraryAsync(g) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenShelf(ShelfRow row) => OpenFromLibraryAsync(row.Entry);

    private Task OpenFromLibraryAsync(GameEntry g)
    {
        ComeFrom(null);
        return OpenAsync(g);
    }

    private async Task OpenAsync(GameEntry entry, bool quiet = false)
    {
        if (!quiet) IsOpening = true;
        var story = await _reports.QueryAsync((db, s) => GameHistory.Story(db, entry, s, DateTime.Now)) ?? new GameStory { Entry = entry };
        // Another game: "How it runs" starts again on the last 30 days. A quiet refresh of the same one keeps the range,
        // the view and the play picked (2026-10-27: every refresh put it back on All plays).
        if (!quiet || Opened?.Entry.Key != entry.Key) { (_runsDays, _runsBack, _runsCustom, _play) = (30, 0, null, null); FramesView = false; }
        Fill(story);
        Opened = story;
        IsOpening = false;
        OnPropertyChanged(nameof(GameName), nameof(GameInstalled), nameof(GameHasFolder), nameof(OpenedLive), nameof(OpenedNotRunning), nameof(ProblemsTitle), nameof(FpsEmpty), nameof(CanStartRtss), nameof(CanInstallRtss), nameof(BetterAsk), nameof(NoFpsAsk), nameof(RunsAsk), nameof(SettingsAsk));
        await LoadSettingsAsync(story);
        await ShowRunsAsync();
    }

    /// <summary>How smooth, from every frame: a verdict, then the stutters an hour and the 0.1% low. Empty without frames.</summary>
    internal static (string Verdict, string Detail) SmoothOf(PlayStats all)
    {
        if (all.StuttersAnHour is not double rate) return ("", "");
        string verdict = rate < 3 ? Lang.T("Smooth") : rate < 30 ? Lang.T("Mostly smooth") : Lang.T("It stutters");
        var parts = new List<string> { rate < 0.5 ? Lang.T("no stutters") : Lang.T($"{rate:0} stutters an hour") };
        if (all.FpsLow01 is double low) parts.Add(Lang.T($"0.1% low {low:0} fps"));
        return (verdict, string.Join(Environment.NewLine, parts));
    }

    // ── How it's set ────────────────────────────────────────────────────

    /// <summary>Where games keep their settings (this PC's folders; a test's own).</summary>
    internal GameSettings.Places SettingsPlaces { get; set; } = GameSettings.Places.ThisPc();

    /// <summary>The game's own saved settings in plain words (the person's ask, 2026-10-25); empty when none were found.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSettings))] private IReadOnlyList<HeatRow> _settingsRows = [];
    /// <summary>Which file they came from and when the game last saved it.</summary>
    [ObservableProperty] private string _settingsNote = "";
    /// <summary>Every graphics setting named from the game's keys, by group (the person's ask, 2026-10-27; approved mock-up).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSettings), nameof(HasSettingLines))] private IReadOnlyList<SettingLine> _settingLines = [];
    /// <summary>Whether NVIDIA's pick is shown beside them (the NVIDIA App is on this PC and has one for the game).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SettingsAskText), nameof(SettingsAskNote), nameof(PickColumnWidth))] private bool _hasNvidiaPick;
    /// <summary>"2 settings differ from NVIDIA's pick for this PC at 2560 × 1440: anti-aliasing and shadow distance."</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSettingsDiff))] private string _settingsDiff = "";
    public bool HasSettings => SettingsRows.Count > 0 || SettingLines.Count > 0;
    public bool HasSettingLines => SettingLines.Count > 0;
    public bool HasSettingsDiff => SettingsDiff.Length > 0;
    public System.Windows.GridLength PickColumnWidth => new(HasNvidiaPick ? 3 : 0, System.Windows.GridUnitType.Star);
    public string SettingsAskText => HasNvidiaPick ? Lang.T("Should I change any of these?") : Lang.T("Compare with recommended settings");
    public string SettingsAskNote => HasNvidiaPick ? Lang.T("NVIDIA's pick is read from the NVIDIA App on this PC.")
        : Lang.T("The AI looks up what guides and players online recommend for your graphics card.");
    /// <summary>The question "How it's set" asks: with NVIDIA's pick, whether to change any; without, against what's recommended online.</summary>
    public AiQuestion? SettingsAsk => Opened?.Entry is { } g
        ? new(HasNvidiaPick ? Lang.T($"Should I change any of {g.Name}'s settings?") : Lang.T($"How do {g.Name}'s settings compare with what's recommended for my PC?"), AiPages.Games)
        : null;

    /// <summary>"Ask how to make it run better": the question its AI button asks about the game that's open.</summary>
    public AiQuestion? BetterAsk => Opened?.Entry is { } g ? new(Lang.T($"How can I make {g.Name} run better?"), AiPages.Games) : null;
    /// <summary>No frame rate on record (RivaTuner wasn't running): what it can still say from the rest.</summary>
    public AiQuestion? NoFpsAsk => Opened?.Entry is { } g ? new(Lang.T($"How does {g.Name} run, from what you can see without a frame rate?"), AiPages.Games) : null;
    /// <summary>"Explain this chart", beside How it runs' range: how the game runs and what pulls it down.</summary>
    public AiQuestion? RunsAsk => Opened?.Entry is { } g ? new(Lang.T($"How does {g.Name} run, and what makes it drop when it does?"), AiPages.Games) : null;

    /// <summary>Reads the game's settings off the window's thread, then says them and lets "Can it run better?" use them.</summary>
    private async Task LoadSettingsAsync(GameStory story)
    {
        SettingsRows = [];
        SettingLines = [];
        SettingsNote = "";
        SettingsDiff = "";
        HasNvidiaPick = false;
        var places = SettingsPlaces;
        var read = await Task.Run(() => GameSettings.Read(story.Entry, places));
        if (Opened != story) return;
        SettingsRows = [.. read.Facts.Select(f => new HeatRow(Lang.T(f.Label), Shown(f.Value)))];
        HasNvidiaPick = read.Nvidia is not null;
        SettingLines = Lines(read);
        SettingsDiff = DiffSentence(read);
        SettingsNote = SourceNote(read);
        OnPropertyChanged(nameof(SettingsAsk));
        var withFps = story.Plays.Where(p => p.Stats.Fps is not null).ToList();
        Better = BetterLines(story, withFps, read);
    }

    /// <summary>The settings by group, each group under its heading, with NVIDIA's pick when there is one.</summary>
    private static List<SettingLine> Lines(GameSettingsRead read)
    {
        var lines = new List<SettingLine>();
        foreach (var group in read.Rows.GroupBy(r => r.Group))
        {
            lines.Add(new(true, Lang.T(group.Key)));
            foreach (var r in group)
                lines.Add(new(false, r.Label, Shown(r.Value), read.Nvidia is null ? "" : r.Recommended is { } rec ? Shown(rec) : Lang.T("Not covered"),
                    r.Differs, read.Nvidia is not null && r.Recommended is null));
        }
        return lines;
    }

    /// <summary>"2 settings differ from NVIDIA's pick for this PC at 2560 × 1440: anti-aliasing and shadow distance." Empty when none do.</summary>
    internal static string DiffSentence(GameSettingsRead read)
    {
        if (read.Nvidia is not { } pick || read.Differing is not { Count: > 0 } diff) return "";
        string names = Wording.JoinAnd(diff.Select(r => GameSettingNames.InSentence(r.Label)));
        string size = pick.Resolution.Replace("x", " × ");
        return diff.Count == 1 ? Lang.T($"1 setting differs from NVIDIA's pick for this PC at {size}: {names}.")
            : Lang.T($"{diff.Count} settings differ from NVIDIA's pick for this PC at {size}: {names}.");
    }

    /// <summary>Where the settings came from and when the game last saved them: "From config.cfg and its saved preferences in Windows · saved Mon 26 Oct".</summary>
    private static string SourceNote(GameSettingsRead read)
    {
        if (read.Files.Count == 0) return "";
        var where = read.Files.OrderBy(f => f.Path.StartsWith("HKEY_", StringComparison.Ordinal))
            .Select(f => f.Path.StartsWith("HKEY_", StringComparison.Ordinal) ? Lang.T("its saved preferences in Windows") : Path.GetFileName(f.Path)).Distinct().ToList();
        string files = Wording.JoinAnd(where);
        return read.Saved is { } saved && saved > DateTime.MinValue.AddYears(1) ? Lang.T($"From {files} · saved {saved.ToString(TimeText.Fit("ddd d MMM"))}") : Lang.T($"From {files}");
    }

    /// <summary>A setting's value in the language shown: "Borderless window", "Mostly Epic", "144 fps".</summary>
    private static string Shown(string value) =>
        value.StartsWith("Mostly ", StringComparison.Ordinal) ? Lang.T($"Mostly {Lang.T(value[7..])}")
        : value.EndsWith(" fps", StringComparison.Ordinal) && int.TryParse(value[..^4], out int n) ? Lang.T($"{n} fps")
        : Lang.T(value);

    [RelayCommand]
    private void Back()
    {
        Opened = null;
        if (_returnTo is { } page) { _returnTo = null; BackText = Lang.T("Back to library"); GoBackTo?.Invoke(page); }
    }

    /// <summary>The sidebar's Games pressed while a game is open: back to the library.</summary>
    public void ShowLibrary()
    {
        Opened = null;
        _returnTo = null;
        BackText = Lang.T("Back to library");
    }

    // A running game is never started again or uninstalled (its buttons are off too).
    [RelayCommand] private void Play(GameEntry? g) { if ((g ?? Opened?.Entry) is { } game && !IsRunning(game)) GameLauncher.Play(game); }
    [RelayCommand] private void PlayHero() { if (HeroGame is { } g && !IsRunning(g)) GameLauncher.Play(g); }
    [RelayCommand] private void OpenFolder(GameEntry? g) { if ((g ?? Opened?.Entry) is { } game) GameLauncher.OpenFolder(game); }
    [RelayCommand] private void Uninstall(GameEntry? g) { if ((g ?? Opened?.Entry) is { } game && !IsRunning(game)) GameLauncher.Uninstall(game); }

    private void Fill(GameStory st)
    {
        var e = st.Entry;
        GameBanner = GamePictures.Picture(e.Installed?.Art.Banner, 1600);
        GameLogo = GamePictures.Picture(e.Installed?.Art.Logo, 720);
        GameIcon = GameBanner is null ? GamePictures.Icon(e.ExePath ?? GamePictures.ProgramIn(e.Installed?.InstallDir)) : null;
        UninstallTip = UninstallThrough(e.Launcher);

        // Over the name, the game itself: its launcher, where it is and how big, its last update. Under it, your playing.
        // Nothing is said in both.
        var eyebrow = new List<string> { LauncherName(e.Launcher).ToUpperInvariant() };
        if (e.Installed?.SizeBytes is long size && size > 0) eyebrow.Add(Lang.T($"{Size(size)} ON THE {DriveOf(e.Installed.InstallDir)} DRIVE"));
        if (st.LastUpdate is { } upd) eyebrow.Add(Lang.T($"UPDATED {upd:ddd d MMM}").ToUpperInvariant());
        if (e.Installed?.AddedThrough is { } through) eyebrow.Add(Lang.T($"ADDED THROUGH {LauncherName(through).ToUpperInvariant()}"));
        GameEyebrow = string.Join(" · ", eyebrow.Where(x => x.Length > 0));

        var pills = new List<GamePill>();
        if (st.Plays.Count > 0)
        {
            pills.Add(new(Lang.T("Played "), Units.Duration(e.ActiveSec)));
            var since = st.Plays[0].StartTime;
            pills.Add(new("", $"{st.Plays.Count}", st.Plays.Count == 1 ? Lang.T($" play since {since:ddd d MMM}") : Lang.T($" plays since {since:ddd d MMM}")));
            if (st.Longest is { } longest) pills.Add(new(Lang.T("Longest "), Units.Duration(longest.ActiveSec), $" · {longest.StartTime:d MMM}"));
        }
        else pills.Add(new("", e.Installed?.LastPlayed is { } lp ? LastPlayed(lp) : NotPlayedYet));
        GamePills = pills;
        About = AboutRows(e, st);

        // How it runs: the plays with a frame rate, those with frame data (they open), and the updates. What's shown is
        // worked out for the range picked (ShowRunsAsync).
        var withFps = st.Plays.Where(p => p.Stats.Fps is not null).ToList();
        HasFps = withFps.Count > 0;
        _withFps = withFps;
        _framePlays = withFps.Where(p => p.Stats.FrameMinutes > 0).ToList();
        _app = e.AppIds.Count > 0 ? e.AppIds[0] : 0;
        HasFramePlays = _framePlays.Count > 0;
        _play = _play is { } was ? _framePlays.FirstOrDefault(p => p.Start == was.Start) : null;
        _play ??= _framePlays.LastOrDefault();
        if (_play is null) FramesView = false;
        // Opening on the last 30 days, or longer when the game wasn't played in them.
        if (_runsCustom is null && _runsBack == 0 && withFps.Count > 0 && !withFps.Any(p => p.EndTime >= DateTime.Now.AddDays(-_runsDays)))
        {
            if (withFps.Any(p => p.EndTime >= DateTime.Now.AddDays(-365))) _runsDays = 365;
            else _runsCustom = (withFps[0].StartTime.Date, DateTime.Now);
        }
        SinceUpdate = st.SinceUpdate ?? (st.LastUpdate is null ? Lang.T("No update seen") : Lang.T("Too soon to tell"));
        UpdatedNote = st.LastUpdate is { } u ? Lang.T($"Updated {u:ddd d MMM}") : "";
        _allMarks = st.Changes.Where(c => c.Change.IsGraphicsDriver || c.Change.Kind == ChangeKind.GameUpdated)
            .Select(c => new RunsMark(c.Change.Time,
                c.Change.Kind == ChangeKind.GameUpdated ? Lang.T("Game update") : Lang.T($"Driver {c.Change.Now ?? c.Change.Title.Split(' ').LastOrDefault() ?? ""}"),
                c.Change.Shown))
            .ToList();

        Better = BetterLines(st, withFps);

        // Problems: the game's own, and the PC's that weren't a power cut. Power cuts in grey.
        var problems = new List<ProblemRow>
        {
            new(Lang.T("Shut-offs that weren't a power cut"), st.ShutOffs > 0 ? $"{st.ShutOffs}" : None, "", st.ShutOffs > 0),
            new(Lang.T("Game crashes Windows recorded"), st.Crashes > 0 ? $"{st.Crashes}" : None, "", st.Crashes > 0),
            new(Lang.T("Graphics driver resets"), st.DriverResets > 0 ? $"{st.DriverResets}" : None, "", st.DriverResets > 0),
            new(Lang.T("Not responding"), st.Hangs > 0 ? $"{st.Hangs}" : None, "", st.Hangs > 0),
        };
        if (st.PowerCuts.Count > 0)
            problems.Add(new(Lang.T("Power cuts"), $"{st.PowerCuts.Count}", " · " + string.Join(", ", st.PowerCuts.Select(p => p.StartTime.ToString("d MMM"))), false, Grey: true));
        Problems = problems;

        // Heat and power
        var heat = new List<HeatRow>
        {
            new(Lang.T("GPU while playing"), st.Usual is { } uu ? Lang.T($"{Units.TempShort(uu.Gpu)} usual") : Units.TempShort(st.All.Gpu), Brush("GpuBrush")),
            new(Lang.T("CPU while playing"), st.Usual is { } uc ? Lang.T($"{Units.TempShort(uc.Cpu)} usual") : Units.TempShort(st.All.Cpu), Brush("CpuBrush")),
            new(Lang.T("Hot spot peak"), Units.TempShort(st.All.HotMax), Brush("PurpleBrush")),
            new(Lang.T("CPU and GPU power"), st.All.Power is double p ? Lang.T($"{p:0} W · {p / 1000:0.00} kWh an hour") : "—"),
            new(Lang.T("Graphics memory"), GraphicsMemory(st.All)),
        };
        Heat = heat;

        // Play time over the last four weeks
        double top = Math.Max(st.WeekSec.DefaultIfEmpty(0).Max(), 1);
        string[] names = [Lang.T("3 wks ago"), Lang.T("2 wks ago"), Lang.T("Last week"), Lang.T("This week")];
        Weeks = st.WeekSec.Select((s, i) => new WeekBar(names[i], s >= 60 ? Units.Duration(s) : "", Math.Max(2, s / top * 62), i == 3)).ToList();
        PlayedMostOn = st.PlayedMostOn is { } d ? OnDays(d) : "—";

        // Every play, newest first, with the changes between them.
        var rows = new List<PlayRow>();
        for (int i = st.Plays.Count - 1; i >= 0; i--)
        {
            foreach (var c in st.Changes.Where(c => c.After == i).Reverse())
                rows.Add(ChangeRow(c.Change));
            rows.Add(PlayRowOf(st.Plays[i], st.Usual, st.Entry.Name) with { AfterChange = rows.Count > 0 && rows[^1].IsChange });
        }
        Rows = rows;
    }

    /// <summary>
    /// "Can it run better?": whether a frame cap sets the pace, how smooth it plays (1% low against the average) and
    /// what the graphics card has left. Each line only when there's data for it.
    /// </summary>
    private static List<BetterLine> BetterLines(GameStory st, List<GamePlay> withFps, GameSettingsRead? set = null)
    {
        var lines = new List<BetterLine>();
        double? cap = null;
        if (withFps.Count >= 3)
        {
            double max = withFps.Max(p => p.Stats.Fps!.Value), median = withFps.Select(p => p.Stats.Fps!.Value).OrderBy(x => x).ElementAt(withFps.Count / 2);
            if (median >= max * 0.97)
            {
                cap = max;
                // The game's own settings say which it is, when they were read: its frame limit near the flat line, or V-Sync on.
                string? limit = set?.Facts.FirstOrDefault(f => f.Label == GameSettings.FrameLimit)?.Value;
                bool vsync = set?.Facts.Any(f => f.Label == GameSettings.VSync && f.Value == GameSettings.On) == true;
                string say = limit is { } l && l.EndsWith(" fps", StringComparison.Ordinal) && int.TryParse(l[..^4], out int n) && Math.Abs(n - max) <= Math.Max(3, max * 0.05)
                        ? Lang.T($"Flat at {max:0} fps most of the time, because the game's own frame limit of {n} fps sets the pace.")
                    : vsync ? Lang.T($"Flat at {max:0} fps most of the time, because V-Sync is on in the game's settings and sets the pace.")
                    : Lang.T($"Flat at {max:0} fps most of the time, so a frame cap or V-Sync sets the pace.");
                lines.Add(new("", say, Brush("FpsBrush")));
            }
        }
        // From the game's own settings (when they were read): no frame limit and VSync off on a screen it outruns.
        if (cap is null && set is not null && st.All.Fps is double avgFps && GameSettings.ScreenHz() is int hz && avgFps > hz * 0.95
            && set.Facts.Any(f => f.Label == GameSettings.FrameLimit && f.Value == GameSettings.NoLimit)
            && set.Facts.Any(f => f.Label == GameSettings.VSync && f.Value == GameSettings.Off))
            lines.Add(new("\ue7fc", Lang.T($"No frame limit and VSync off on a {hz} Hz screen, at {avgFps:0} fps on average. A limit of {hz - 3} fps in its settings would keep frames even and the card cooler."), Brush("FpsBrush")));
        // Smoothness from every frame is said once, in "How it runs"; without frames, the lows say it here.
        if (st.All.StuttersAnHour is null && st.All.Fps is double avg && st.All.FpsLow is double low && avg > 0)
        {
            double share = low / avg;
            string say = share >= 0.7 ? Lang.T($"The 1% lows stay at {low:0} fps, close to the {avg:0} average, so it plays smoothly.")
                : share >= 0.5 ? Lang.T($"The 1% lows drop to {low:0} fps against a {avg:0} average, so you may feel the odd stutter.")
                : Lang.T($"The 1% lows fall to {low:0} fps against a {avg:0} average, so it stutters.");
            lines.Add(new("\ue9d9", say, Brush("FpsBrush")));
        }
        if (st.GpuFullShare is double full)
        {
            double busy = st.All.GpuLoad ?? 0;
            string load = full >= 1 ? Lang.T($"was at full load only {full:0}% of the time") : Lang.T("never hit full load");
            string say = full >= 70 ? Lang.T($"Your graphics card was at full load {full:0}% of the time, so lower settings would give more frames.") + UpscalerHint(set)
                : full >= 30 ? Lang.T($"Your graphics card was at full load {full:0}% of the time, so in busy scenes it's the limit.")
                : cap is double c ? Lang.T($"Your graphics card averaged {busy:0}% busy and {load}, so you can raise the settings and keep {c:0} fps.")
                : Lang.T($"Your graphics card averaged {busy:0}% busy and {load}, so something else sets the pace, most likely the CPU.");
            lines.Add(new("", say, Brush("GpuBrush")));
        }
        // NVIDIA's pick for this PC, when the NVIDIA App has one and it differs from how the game is set.
        if (set?.Nvidia is not null && set.Differing is { Count: > 0 } diff)
        {
            string names = Wording.JoinAnd(diff.Select(r => GameSettingNames.InSentence(r.Label)));
            lines.Add(new("", diff.Count == 1 ? Lang.T($"NVIDIA's pick for this PC differs on 1 setting: {names}.") : Lang.T($"NVIDIA's pick for this PC differs on {diff.Count} settings: {names}."), Brush("AccentBrush")));
        }
        if (lines.Count == 0) lines.Add(new("", Lang.T("Not enough recorded yet to say. Play it with RivaTuner running to see its frame rate here."), Brush("FaintBrush")));
        return lines;
    }

    /// <summary>
    /// With the card the limit: what its upscaler could still give, by the game's own settings ("DLSS is on Balanced;
    /// Performance would add the most."). Empty when the settings don't say.
    /// </summary>
    private static string UpscalerHint(GameSettingsRead? set)
    {
        if (set?.Facts.FirstOrDefault(f => f.Label == GameSettings.Upscaling)?.Value is not { } up) return "";
        if (up == GameSettings.Off) return " " + Lang.T("Turning on DLSS or FSR in its settings would add the most.");
        var parts = up.Split(' ', 2);
        if (parts.Length == 2 && !parts[1].Contains("Performance", StringComparison.OrdinalIgnoreCase))
            return " " + Lang.T($"{parts[0]} is on {parts[1]}; Performance would add the most.");
        return "";
    }

    private static PlayRow ChangeRow(SystemChange c) => new()
    {
        IsChange = true,
        IsGameUpdate = c.Kind == ChangeKind.GameUpdated,
        Glyph = c.Kind switch { ChangeKind.Fans => "", ChangeKind.GameUpdated => "", ChangeKind.WindowsUpdate or ChangeKind.Windows => "", _ => "" },
        Text = c.Kind == ChangeKind.GameUpdated && c.Now is { } v ? $"{c.Shown} ({v})" : c.Shown,
        When = c.Time.ToString(TimeText.Fit("ddd d MMM, h:mm tt")),
    };

    private static PlayRow PlayRowOf(GamePlay p, GameUsual? usual, string game)
    {
        string? note = null;
        bool warm = false;
        if (GameHistory.LikeUsual(p, usual))
        {
            double diff = p.Stats.Gpu!.Value - usual!.Gpu;
            string degrees = Units.Degrees(diff);
            if (Math.Abs(diff) >= 4) { note = diff > 0 ? Lang.T($"{degrees} hotter than usual") : Lang.T($"{degrees} cooler than usual"); warm = diff > 0; }
            else note = Lang.T("as usual");
        }
        // Only a play that ended badly says how: amber for a crash or the PC going off, grey for a power cut.
        var (ended, endWarm) = p.Ending switch
        {
            PlayEnd.PowerCut => (Lang.T("Power cut"), false),
            PlayEnd.ShutOff => (Lang.T("PC went off"), true),
            PlayEnd.BlueScreen => (Lang.T("Blue screen"), true),
            PlayEnd.DriverReset => (Lang.T("Driver reset"), true),
            PlayEnd.Crashed => (Lang.T("Game crashed"), true),
            PlayEnd.Hung => (Lang.T("Stopped responding"), true),
            _ => ((string?)null, false),
        };
        return new PlayRow
        {
            Day = p.StartTime.ToString("ddd d MMM"),
            Times = Lang.T($"{p.StartTime:h:mm tt} to {p.EndTime:h:mm tt}"),
            Played = Units.Duration(p.ActiveSec),
            Fps = p.Stats.Fps is double f ? $"{f:0}" : null,
            Low = p.Stats.FpsLow is double l ? $"· {l:0}" : null,
            Stutters = p.Stats.Stutters switch { null => null, 0 => Lang.T("No stutters"), 1 => Lang.T("1 stutter"), int n => Lang.T($"{n} stutters") },
            // Amber from a stutter every two minutes on: felt, not a one-off.
            StuttersWarm = p.Stats.StuttersAnHour is >= 30,
            Gpu = p.Stats.Gpu is double g ? Units.TempShort(g) : null,
            GpuPeak = p.Stats.GpuMax is double gm ? Lang.T($"peak {Units.TempShort(gm)}") : null,
            Usual = note,
            UsualWarm = warm,
            Power = p.Stats.GpuPower is double w ? $"{w:0} W" : "",
            Ended = ended is null ? null : $"{ended} {In(p)}",
            EndedWarm = endWarm,
            Ask = endWarm && ended is not null ? new(Lang.T($"Why did my {game} play on {p.StartTime:ddd d MMM} at {p.StartTime:h:mm tt} end ({ended.ToLowerInvariant()})?"), AiPages.Games)
                : p.Stats.StuttersAnHour is >= 30 ? new(Lang.T($"Why did {game} stutter in my play on {p.StartTime:ddd d MMM} at {p.StartTime:h:mm tt}?"), AiPages.Games) : null,
            AskText = endWarm ? Lang.T("Why did it end?") : Lang.T("Why does it stutter?"),
        };

        static string In(GamePlay p) => p.ActiveSec < 90 ? Lang.T("about a minute in") : Lang.T($"{Units.Duration(p.ActiveSec)} in");
    }

    // ── Words ────────────────────────────────────────────────────────────

    public static string LauncherName(Launcher? l) => l switch
    {
        Launcher.Steam => "Steam",
        Launcher.Epic => "Epic Games",
        Launcher.Riot => "Riot Client",
        Launcher.Gog => "GOG Galaxy",
        Launcher.Ubisoft => "Ubisoft Connect",
        Launcher.Ea => Lang.T("EA app"),
        Launcher.BattleNet => "Battle.net",
        _ => "",
    };

    private static string None => Lang.T("None");

    private static string NotPlayedYet => Lang.T("Not played yet");

    /// <summary>A weekday as a habit, whole for each day ("Saturdays"): another language says it its own way ("los sábados").</summary>
    private static string OnDays(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Lang.T("Mondays"),
        DayOfWeek.Tuesday => Lang.T("Tuesdays"),
        DayOfWeek.Wednesday => Lang.T("Wednesdays"),
        DayOfWeek.Thursday => Lang.T("Thursdays"),
        DayOfWeek.Friday => Lang.T("Fridays"),
        DayOfWeek.Saturday => Lang.T("Saturdays"),
        _ => Lang.T("Sundays"),
    };

    private static string LastPlayed(DateTime d)
    {
        string day = Day(d);
        return Lang.T($"Last played {day}");
    }

    private static string OnDrive(string drive) => Lang.T($"on the {drive} drive");

    private static string UninstallThrough(Launcher? launcher)
    {
        string who = GameLauncher.Who(launcher);
        return Lang.T($"Uninstall through {who}");
    }

    private static string Size(long? bytes) => bytes is long b && b > 0 ? b >= 1e9 ? $"{b / 1e9:0} GB" : $"{b / 1e6:0} MB" : "";

    /// <summary>Everything about the game itself in one place: where it is, how big, which version, when it was played.</summary>
    private static List<HeatRow> AboutRows(GameEntry e, GameStory st)
    {
        var rows = new List<HeatRow>();
        var inst = e.Installed;
        string launcher = LauncherName(e.Launcher);
        if (inst?.AddedThrough is { } through)
        {
            string also = LauncherName(through);
            launcher = Lang.T($"{launcher} (also in {also})");
        }
        rows.Add(new(Lang.T("Launcher"), launcher));
        if (inst is null)
        {
            rows.Add(new(Lang.C("about", "Installed"), Lang.T("Not any more")));
        }
        else
        {
            rows.Add(new(Lang.T("Size on disk"), inst.SizeBytes is long b && b > 0 ? Size(b) : Lang.T("Not listed by the launcher")));
            string drive = DriveOf(inst.InstallDir);
            if (drive.Length > 0) rows.Add(new(Lang.T("Drive"), Lang.T($"{drive} drive"))); // the game's drive, not how full it is
            rows.Add(new(Lang.T("Folder"), inst.InstallDir));
            if (!string.IsNullOrEmpty(inst.Version))
            {
                // Steam gives a build number ("build 1234" as the library keeps it): worded here, in the person's language.
                string build = inst.Version.StartsWith("build ", StringComparison.OrdinalIgnoreCase) ? inst.Version[6..] : inst.Version;
                rows.Add(new(Lang.T("Version"), inst.Launcher == Core.Games.Launcher.Steam || build.Length != inst.Version.Length
                    ? Lang.T($"Build {build}") : char.ToUpperInvariant(inst.Version[0]) + inst.Version[1..]));
            }
        }
        if ((st.LastUpdate ?? inst?.Updated) is { } updated) rows.Add(new(Lang.T("Last updated"), Day(updated)));
        if (e.ExePath is { } exe) rows.Add(new(Lang.T("Program"), Path.GetFileName(exe)));
        if (e.FirstPlayed is { } first) rows.Add(new(Lang.T("First played"), Day(first)));
        if ((e.LastPlayed ?? inst?.LastPlayed) is { } last) rows.Add(new(Lang.T("Last played"), Day(last)));
        return rows;
    }

    private static string DriveOf(string dir) => dir.Length >= 2 && dir[1] == ':' ? char.ToUpperInvariant(dir[0]).ToString() : "";

    /// <summary>A day as the page writes it: "Wed 14 Oct" this year, "22 Aug 2025" before.</summary>
    private static string Day(DateTime d) => d.Year == DateTime.Now.Year ? d.ToString("ddd d MMM") : d.ToString("d MMM yyyy");

    private void OnPropertyChanged(params string[] names)
    {
        foreach (var n in names) base.OnPropertyChanged(n); // base: a single name would come back here, round and round
    }
}

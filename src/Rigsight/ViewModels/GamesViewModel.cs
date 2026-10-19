using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
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
    public string? Gpu { get; init; }
    public string? GpuPeak { get; init; }
    public bool HasGpu => Gpu is not null;
    public string? Usual { get; init; }
    public bool UsualWarm { get; init; }
    public string Power { get; init; } = "";
    public string Ended { get; init; } = "";
    public string? EndedSub { get; init; }
    public bool EndedWarm { get; init; }
    public bool EndedGrey { get; init; }
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

    partial void OnSortChanged(string value) => BuildCards();
    partial void OnFilterChanged(string value) => BuildCards();
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
        var hero = _running ?? last;
        _heroStory = hero is null ? null : await _reports.QueryAsync((db, s) => GameHistory.Story(db, hero, s, DateTime.Now));
        await Task.Run(() => { foreach (var g in library) Prewarm(g); });
        BuildCards();
        BuildShelf();
        BuildHero(last);
        IsLoading = false;
        Ready = true;
        OnPropertyChanged(nameof(IsEmpty));
        if (_pendingOpen is { } key) { _pendingOpen = null; await OpenKeyAsync(key); }
        else if (Opened is { } open && _all.FirstOrDefault(g => g.Key == open.Entry.Key) is { } fresh) await OpenAsync(fresh, quiet: true);
    }

    /// <summary>The pictures of a game read off the window's thread (they're frozen), so the cards build without a pause.</summary>
    private static void Prewarm(GameEntry g)
    {
        if (g.Installed?.Art.Cover is { } c) GamePictures.Picture(c, 300);
        else GamePictures.Icon(g.ExePath ?? GamePictures.ProgramIn(g.Installed?.InstallDir));
    }

    private void BuildCards()
    {
        var now = DateTime.Now;
        IEnumerable<GameEntry> list = Filter switch
        {
            "installed" => _all.Where(g => g.IsInstalled),
            "before" => _all.Where(g => !g.IsInstalled),
            _ => _all,
        };
        var words = Core.TextMatch.Words(Search);
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
        var idle = _all.Where(g => g.IsInstalled && (g.HasHistory ? g.LastPlayed < cutoff : !(g.Installed!.LastPlayed >= cutoff)))
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

    private void BuildHero(GameEntry? last)
    {
        // The game running now takes the banner; its figures are its last play's, like any other.
        var hero = PlayingEntry() ?? last;
        if (hero is null) { HeroGame = null; return; }
        if (_heroStory?.Entry.Key != hero.Key) _heroStory = null;
        HeroGame = hero;
        HeroLive = IsRunning(hero);
        HeroBanner = GamePictures.Picture(hero.Installed?.Art.Banner, 1600);
        HeroLogo = GamePictures.Picture(hero.Installed?.Art.Logo, 720);
        HeroIcon = HeroBanner is null ? GamePictures.Icon(hero.ExePath) : null;
        var play = _heroStory?.Plays.LastOrDefault();
        HeroEyebrow = HeroLive ? Lang.T("PLAYING NOW") : play is null ? Lang.T("CONTINUE") : Lang.T($"CONTINUE · LAST PLAYED {play.StartTime:ddd d MMM}").ToUpperInvariant();
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

    /// <summary>The banner after a game starts or closes: the running one, else the last played, with its last play's figures.</summary>
    private async Task RefreshHeroAsync()
    {
        var hero = _running ?? _all.Where(g => g.LastPlayed is not null).OrderByDescending(g => g.LastPlayed).FirstOrDefault();
        if (hero is not null && _heroStory?.Entry.Key != hero.Key)
            _heroStory = await _reports.QueryAsync((db, s) => GameHistory.Story(db, hero, s, DateTime.Now));
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
    [ObservableProperty] private string _sinceUpdate = "";
    [ObservableProperty] private string _updatedNote = "";
    [ObservableProperty] private IReadOnlyList<FpsPoint> _fpsPoints = [];
    [ObservableProperty] private IReadOnlyList<FpsMark> _fpsMarks = [];
    /// <summary>The chart shows the only play's minutes, not plays: power cuts and updates have no place in it.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FpsByPlay))] private bool _fpsByMinute;
    public bool FpsByPlay => !FpsByMinute;
    [ObservableProperty] private string _fpsChartNote = "";
    public bool HasFpsChart => FpsPoints.Count >= 2;
    partial void OnFpsPointsChanged(IReadOnlyList<FpsPoint> value) => OnPropertyChanged(nameof(HasFpsChart));

    /// <summary>Why a game has no frame rate yet, and what to do about it.</summary>
    public string FpsEmpty => _overlay.RtssState switch
    {
        "running" => Lang.T($"No FPS recorded for {GameName} yet. RivaTuner measures a game that starts after it, so its next play will have it."),
        "stopped" => Lang.T("No FPS for these plays. RigSight reads the frame rate from RivaTuner, which isn't running: start it before the game."),
        "installing" => Lang.T("No FPS for these plays. RivaTuner is being installed; start the game after it's done."),
        _ => Lang.T("No FPS for these plays. RigSight reads the frame rate from RivaTuner Statistics Server, a free tool anti-cheat accepts, and it isn't installed."),
    };
    public bool CanStartRtss => _overlay.CanStartRtss;
    public bool CanInstallRtss => _overlay.CanInstallRtss;
    [RelayCommand] private void StartRtss() => _overlay.StartRtssCommand.Execute(null);
    [RelayCommand] private void InstallRtss() => _overlay.InstallRtssCommand.Execute(null);

    // What holds it back
    [ObservableProperty] private string _limitSay = "";
    [ObservableProperty] private string _limitSub = "";
    [ObservableProperty] private double _gpuFull;
    [ObservableProperty] private string _gpuFullText = "";
    [ObservableProperty] private string _roomText = "";
    [ObservableProperty] private string? _capLine;
    public bool HasCapLine => CapLine is not null;
    partial void OnCapLineChanged(string? value) => OnPropertyChanged(nameof(HasCapLine));

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
        Fill(story);
        Opened = story;
        IsOpening = false;
        OnPropertyChanged(nameof(GameName), nameof(GameInstalled), nameof(GameHasFolder), nameof(OpenedLive), nameof(OpenedNotRunning), nameof(ProblemsTitle), nameof(FpsEmpty), nameof(CanStartRtss), nameof(CanInstallRtss));
    }

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

        // How it runs
        var withFps = st.Plays.Where(p => p.Stats.Fps is not null).ToList();
        HasFps = withFps.Count > 0;
        FpsAvg = st.All.Fps is double fps ? $"{fps:0}" : "—";
        FpsLow = st.All.FpsLow is double low ? $"{low:0}" : "—";
        SinceUpdate = st.SinceUpdate ?? (st.LastUpdate is null ? Lang.T("No update seen") : Lang.T("Too soon to tell"));
        UpdatedNote = st.LastUpdate is { } u ? Lang.T($"Updated {u:ddd d MMM}") : "";
        // One play with frames: that play minute by minute, until a second gives the play-by-play line.
        FpsByMinute = withFps.Count == 1 && st.OnlyPlayMinutes.Count >= 2;
        FpsChartNote = FpsByMinute ? Lang.T($"Your {withFps[0].StartTime:ddd d MMM} play, minute by minute") : "";
        FpsPoints = FpsByMinute
            ? st.OnlyPlayMinutes.Select(m => new FpsPoint(TimeUtil.FromUnix(m.Ts), TimeUtil.FromUnix(m.Ts + 60), 60, m.Fps!.Value, m.FpsLow, m.GpuTemp, m.GpuPower, false)).ToList()
            : withFps.Select(p => new FpsPoint(p.StartTime, p.EndTime, p.ActiveSec, p.Stats.Fps!.Value, p.Stats.FpsLow, p.Stats.Gpu, p.Stats.GpuPower, p.Ending == PlayEnd.PowerCut)).ToList();
        var marks = new List<FpsMark>();
        foreach (var c in st.Changes.Where(c => c.Change.IsGraphicsDriver || c.Change.Kind == ChangeKind.GameUpdated))
        {
            // Between the plays with frames on either side of it.
            int before = st.Plays.Take(c.After + 1).Count(p => p.Stats.Fps is not null) - 1;
            if (FpsByMinute || before < 0 || before >= withFps.Count - 1) continue;
            string driver = c.Change.Now ?? c.Change.Title.Split(' ').LastOrDefault() ?? "";
            string label = c.Change.Kind == ChangeKind.GameUpdated ? Lang.T("Game update") : Lang.T($"Driver {driver}");
            marks.Add(new FpsMark(before + 0.5, label, c.Change.Title, c.Change.Time));
        }
        FpsMarks = marks;

        // What holds it back: how much of the time the graphics card was at full load.
        double full = st.GpuFullShare ?? 0;
        GpuFull = Math.Round(full);
        GpuFullText = Lang.T($"Graphics card at full load {GpuFull:0}%");
        double spare = 100 - GpuFull, busy = st.All.GpuLoad ?? 0;
        RoomText = Lang.T($"Room to spare {spare:0}%");
        (LimitSay, LimitSub) = full >= 70
            ? (Lang.T("Your graphics card is the limit"), Lang.T($"It ran at full load {full:0}% of the time you played. More frames means lower settings or a faster graphics card."))
            : full >= 30
                ? (Lang.T("Partly your graphics card"), Lang.T($"It was at full load {full:0}% of the time. The rest of the time something else set the pace: a frame cap, V-Sync or the CPU."))
                : (Lang.T("Your graphics card has room to spare"), full >= 1
                    ? Lang.T($"It averaged {busy:0}% busy and was at full load only {full:0}% of the time. Frames are held by a frame cap or V-Sync, or by the CPU.")
                    : Lang.T($"It averaged {busy:0}% busy and was never at full load. Frames are held by a frame cap or V-Sync, or by the CPU."));
        CapLine = null;
        if (withFps.Count >= 3 && full < 70)
        {
            double max = withFps.Max(p => p.Stats.Fps!.Value), median = withFps.Select(p => p.Stats.Fps!.Value).OrderBy(x => x).ElementAt(withFps.Count / 2);
            CapLine = median >= max * 0.97
                ? Lang.T($"Flat at {max:0} fps most of the time, so it's a frame cap, not the CPU.")
                : Lang.T("FPS moves with the action and never sits flat, so the CPU sets the pace.");
        }

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
            new(Lang.T("Graphics memory"), st.All.VramMax is double v && st.All.VramTotal is double t ? Lang.T($"up to {v / 1024:0.0} of {t / 1024:0} GB") : Lang.T("Not recorded")),
        };
        Heat = heat;

        // Play time over the last four weeks
        double top = Math.Max(st.WeekSec.DefaultIfEmpty(0).Max(), 1);
        string[] names = [Lang.T("3 wks ago"), Lang.T("2 wks ago"), Lang.T("Last week"), Lang.T("This week")];
        Weeks = st.WeekSec.Select((s, i) => new WeekBar(names[i], s >= 60 ? Units.Duration(s) : "", Math.Max(2, s / top * 62), i == 3)).ToList();
        PlayedMostOn = st.PlayedMostOn is { } d ? Lang.T($"{d}s") : "—";

        // Every play, newest first, with the changes between them.
        var rows = new List<PlayRow>();
        for (int i = st.Plays.Count - 1; i >= 0; i--)
        {
            foreach (var c in st.Changes.Where(c => c.After == i).Reverse())
                rows.Add(ChangeRow(c.Change));
            rows.Add(PlayRowOf(st.Plays[i], st.Usual) with { AfterChange = rows.Count > 0 && rows[^1].IsChange });
        }
        Rows = rows;
    }

    private static PlayRow ChangeRow(SystemChange c) => new()
    {
        IsChange = true,
        IsGameUpdate = c.Kind == ChangeKind.GameUpdated,
        Glyph = c.Kind switch { ChangeKind.Fans => "", ChangeKind.GameUpdated => "", ChangeKind.WindowsUpdate or ChangeKind.Windows => "", _ => "" },
        Text = c.Kind == ChangeKind.GameUpdated && c.Now is { } v ? $"{c.Title} ({v})" : c.Title,
        When = c.Time.ToString("ddd d MMM, h:mm tt"),
    };

    private static PlayRow PlayRowOf(GamePlay p, GameUsual? usual)
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
        var (ended, sub, endWarm, grey) = p.Ending switch
        {
            PlayEnd.PowerCut => (Lang.T("Power cut"), In(p), false, true),
            PlayEnd.ShutOff => (Lang.T("PC went off"), In(p), true, false),
            PlayEnd.BlueScreen => (Lang.T("Blue screen"), In(p), true, false),
            PlayEnd.DriverReset => (Lang.T("Driver reset"), In(p), true, false),
            PlayEnd.Crashed => (Lang.T("Game crashed"), In(p), true, false),
            PlayEnd.Hung => (Lang.T("Stopped responding"), In(p), true, false),
            _ => (Lang.T("Closed"), null, false, false),
        };
        return new PlayRow
        {
            Day = p.StartTime.ToString("ddd d MMM"),
            Times = Lang.T($"{p.StartTime:h:mm tt} to {p.EndTime:h:mm tt}"),
            Played = Units.Duration(p.ActiveSec),
            Fps = p.Stats.Fps is double f ? $"{f:0}" : null,
            Low = p.Stats.FpsLow is double l ? $"· {l:0}" : null,
            Gpu = p.Stats.Gpu is double g ? Units.TempShort(g) : null,
            GpuPeak = p.Stats.GpuMax is double gm ? Lang.T($"peak {Units.TempShort(gm)}") : null,
            Usual = note,
            UsualWarm = warm,
            Power = p.Stats.GpuPower is double w ? $"{w:0} W" : "",
            Ended = ended,
            EndedSub = sub,
            EndedWarm = endWarm,
            EndedGrey = grey,
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
            rows.Add(new(Lang.T("Installed"), Lang.T("Not any more")));
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

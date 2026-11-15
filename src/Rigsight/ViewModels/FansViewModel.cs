using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Reports;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>How a fan is doing right now, in a word or two, and whether that needs a look.</summary>
public enum FanState { NoReading, Spinning, Resting, Stopped, StoppedHot }

/// <summary>One of the six facts on a fan's details: a heading, a value, and a line under it.</summary>
public sealed record FanFact(string Label, string Value, string Note);

/// <summary>What a Today card fact is about, for its icon and colour.</summary>
public enum FanFactTone { Loud, GpuStarts, FanStarts, Still }

/// <summary>
/// One of the Today card's three facts (side by side, the same parts): its id (what it marks on the strip when
/// hovered), heading, value and the line under it.
/// </summary>
public sealed record FanTodayFact(string Id, string Label, string Value, string Note, FanFactTone Tone)
{
    public bool IsFan => Tone is FanFactTone.GpuStarts or FanFactTone.FanStarts;
}

/// <summary>An item of the Today card's legend: a level (its swatch's height and strength) or the starts' mark, with what it means on hover.</summary>
public sealed record FanLevelKey(string Name, string Tip, double Height, double Strength, bool IsTick = false, bool Gpu = false);

/// <summary>An app on a fan's "what makes it spin": the fan's average speed while that app worked the chip it follows.</summary>
public sealed record FanAppRow(string Name, string RpmText, double Share, bool Dim);

/// <summary>
/// What the Fans page shows for a row beyond its live reading, from today's minutes (and the month's days): three facts,
/// its speed at each temperature against its usual curve, the apps that make it spin, and its last 30 days.
/// </summary>
public sealed record FanDetail(
    string FollowsValue, string FollowsNote, string StillValue, string StillNote, string ThirdLabel, string ThirdValue, string ThirdNote,
    string CurveTitle, IReadOnlyList<Controls.FanDot> Dots, IReadOnlyList<Controls.FanTempBin> Bins, IReadOnlyList<System.Windows.Point> Line, string LineLabel, double? SilentBelow,
    string AppsTitle, IReadOnlyList<FanAppRow> Apps, string AppsNote, bool IsSteady, IReadOnlyList<Controls.FanDayBar> Days)
{
    public bool HasApps => Apps.Count > 0;

    /// <summary>The six facts shown (two rows of three): the three above first, then three more for the kind of fan.</summary>
    public IReadOnlyList<FanFact> Facts { get; init; } = [];

    /// <summary>The first fact's heading: "Follows", or for a graphics card that stands still, "Starts spinning".</summary>
    public string FollowsLabel { get; init; } = Lang.T("Follows");

    /// <summary>The second fact's heading ("Stood still", or "Average" for a year, which is kept by the day).</summary>
    public string StillLabel { get; init; } = Lang.T("Stood still");

    /// <summary>The speed chart's bars are months (a year).</summary>
    public bool Monthly { get; init; }

    /// <summary>A year: the fan's speed at one temperature, month by month (a slower fan shows as falling bars).</summary>
    public IReadOnlyList<Controls.FanDayBar> Trend { get; init; } = [];
    public string TrendTitle { get; init; } = "";
    public string TrendLabel { get; init; } = "";
    public bool HasTrend => Trend.Any(b => b.Average is not null);

    /// <summary>Its average and fastest over the period, and when it was fastest (for the tiles).</summary>
    public double? AverageRpm { get; init; }
    public double? FastestRpm { get; init; }
    public string FastestWhen { get; init; } = "";

    /// <summary>Said above the facts when the fan's speed setting changed during the period (they are of the setting since).</summary>
    public string SettingNote { get; init; } = "";
    public bool HasSettingNote => SettingNote.Length > 0;
}

/// <summary>
/// One row on the Fans page: a fan (renamable, as on All sensors), or a graphics card's fans together (two or three
/// that turn as one, so one row with each fan's speed in its details), and what today's minutes show about it.
/// </summary>
public sealed partial class FanCard : ObservableObject
{
    /// <summary>A chip counts as working hard from here, so a fan that should cool it and stands still needs a look.</summary>
    internal const double HotFrom = 70;

    /// <summary>A fan turns from here (see <see cref="FanList.MinSpinRpm"/>).</summary>
    internal const double MinSpinRpm = FanList.MinSpinRpm;

    // None green, amber, orange or red: those say how a reading is doing, not which fan a line is.
    private static readonly string[] Colors = ["GpuColor", "PurpleColor", "PinkColor", "SkyColor"];

    public FanCard(IReadOnlyList<SensorItem> sensors)
    {
        Sensors = sensors;
        Sensor = sensors[0];
        IsGpu = FanList.IsGpu(Sensor.HardwareType);
        Place = IsGpu ? "GPU" : Sensor.HardwareType == "Cooler" ? Sensor.HardwareName : Lang.T("Motherboard");
        Series = [.. sensors.Select((x, i) => new ChartSeries(x.DisplayName, x, IsGpu ? Colors[i % Colors.Length] : i == 0 ? "CpuColor" : Colors[i % Colors.Length]))];
    }

    /// <summary>The fans in this row (several for a graphics card), and the first of them.</summary>
    public IReadOnlyList<SensorItem> Sensors { get; }
    public SensorItem Sensor { get; }

    /// <summary>A graphics card's own fans (they follow the GPU; standing still while the card is cool is by design).</summary>
    public bool IsGpu { get; }

    public bool IsGroup => Sensors.Count > 1;

    /// <summary>What the list groups this row under: the graphics card, a cooler (an AIO: pump and radiator fans, by its
    /// name), or the motherboard's headers.</summary>
    public string Place { get; internal set; }

    /// <summary>The row's name: a fan's own (or the name the user gave it), or "GPU fans" for a card's.</summary>
    public string Title => FanList.Title(Sensors.Count, Sensor.DisplayName);

    /// <summary>Under the name in the list: a warning first, then how many fans a card's row stands for, or the state.</summary>
    public string Note => HasAlert ? Lang.T("Needs a look")
        : IsGroup ? Lang.T($"{Sensors.Count} fans · {StateText.ToLowerInvariant()}")
        : Facts?.Follows == FanFollows.Steady && State == FanState.Spinning ? Lang.T("Steady speed") : StateText;

    /// <summary>The speed now: a card's fans averaged (they turn together), one fan's as it reads.</summary>
    public double? Value => FanList.Speed(Sensors.Select(x => x.Value));

    public string ValueText => Units.Format(SensorKind.Fan, Value);

    /// <summary>This row's lines on the chart (one per fan): live readings, today's minutes before them.</summary>
    public IReadOnlyList<ChartSeries> Series { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowsText), nameof(StoppedText), nameof(FastestText), nameof(HasFastest), nameof(Note))]
    private FanFacts? _facts;

    /// <summary>The page's detail for this row (see <see cref="FanDetail"/>).</summary>
    [ObservableProperty] private FanDetail? _detail;

    /// <summary>Minutes recorded for this row today (the PC on).</summary>
    public int MinutesOn { get; internal set; }

    /// <summary>Under the fastest speed: what was going on ("While playing Rematch"), or when.</summary>
    [ObservableProperty] private string _fastestLine = "";

    /// <summary>Today's warning about this fan (stopped, or slower than usual), as the day's insights put it; empty if none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlert), nameof(Note), nameof(NeedsLook), nameof(AlertAsk))]
    private string _alert = "";

    [ObservableProperty] private bool _alertIsHot;

    public bool HasAlert => Alert.Length > 0;
    /// <summary>The warning's AI button: why, and what to do, from the Fans page's records.</summary>
    public Services.AiQuestion? AlertAsk => HasAlert ? new(Lang.T($"{Title}. {Alert} Why, and what should I do?"), Core.Ai.AiPages.Fans) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(NeedsLook), nameof(Note))]
    private FanState _state = FanState.NoReading;

    public string StateText => State switch
    {
        FanState.Spinning => Lang.T("Spinning"),
        FanState.Resting => Lang.T("Resting while cool"),
        FanState.Stopped => Lang.T("Stopped"),
        FanState.StoppedHot => Lang.T("Stopped while hot"),
        _ => Lang.T("No reading"),
    };

    /// <summary>Standing still while the chip it cools is hot, or today's insights warn about it: worth checking the fan.</summary>
    public bool NeedsLook => State == FanState.StoppedHot || HasAlert;

    public string FollowsText => Facts?.Follows switch
    {
        FanFollows.Gpu => Lang.T("Speeds up with your GPU"),
        FanFollows.Cpu => Lang.T("Speeds up with your CPU"),
        FanFollows.Steady => Lang.T("Runs at a steady speed"),
        FanFollows.Varies => Lang.T("Changes speed, not clearly with your CPU or GPU"),
        _ => "",
    };

    public string StoppedText => Facts is { StoppedMinutes: > 0 } f
        ? IsGpu ? Lang.T($"Rested for {Units.Duration(f.StoppedMinutes * 60)} today") : Lang.T($"Stood still for {Units.Duration(f.StoppedMinutes * 60)} today")
        : "";

    public bool HasFastest => Facts?.Fastest is not null;
    public string FastestText => Facts?.Fastest is { } p ? Units.Format(SensorKind.Fan, p.Rpm) : "—";

    /// <summary>The state now, from the row's speed and the temperature of the chip it follows; the speed shown moves too.</summary>
    internal void UpdateState(double? cpuTemp, double? gpuTemp)
    {
        double? part = IsGpu || Facts?.Follows == FanFollows.Gpu ? gpuTemp : Facts?.Follows == FanFollows.Cpu ? cpuTemp : null;
        State = Value switch
        {
            null => FanState.NoReading,
            > 0 => FanState.Spinning,
            _ when part >= HotFrom => FanState.StoppedHot,
            _ when IsGpu => FanState.Resting,
            _ => FanState.Stopped,
        };
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
    }

    /// <summary>After a rename on the page or on All sensors.</summary>
    internal void Renamed()
    {
        OnPropertyChanged(nameof(Title));
        foreach (var (series, sensor) in Series.Zip(Sensors)) series.Label = sensor.DisplayName;
    }

    /// <summary>
    /// A card's rows put together from each fan's facts: what the first follows (a card's fans turn as one), how long
    /// the card rested (while all its fans stood still: the least of theirs), and the fastest any of them spun.
    /// </summary>
    internal static FanFacts Merge(IReadOnlyList<FanFacts> each) => each.Count == 1 ? each[0] : new FanFacts(
        each[0].Follows, each.Max(f => f.SpinningMinutes), each.Min(f => f.StoppedMinutes),
        each.Select(f => f.Fastest).Where(p => p is not null).MaxBy(p => p!.Rpm));
}

/// <summary>
/// The Fans page: a list of every fan that spins, grouped by where it is (graphics card, cooler, motherboard), and the
/// picked one's details: its speed now, what it follows, how long it stood still today, its fastest moment and what
/// was running then, and today's speeds on a chart. Headers that have never spun are left out and counted.
/// </summary>
public sealed partial class FansViewModel : ObservableObject
{
    /// <summary>How far back a header must have spun at least once to be shown.</summary>
    internal const int SpunWithinDays = FanList.SpunWithinDays;

    /// <summary>The days a fan's usual curve (the line on its temperature chart) is drawn from, before today.</summary>
    internal const int UsualDays = 30;

    /// <summary>An app makes the "what makes it spin" list from this many minutes working the fan's chip today.</summary>
    internal const int AppMinutes = 10;

    // ── The period the page shows (Day, Week or Month; today unless picked otherwise, like Home) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(IsYear), nameof(RangeNote), nameof(PeriodCaption), nameof(Days), nameof(DayOfChart), nameof(CurveEmptyText))]
    private ReportRange _unit = ReportRange.Day;

    /// <summary>Any day in the period.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(PeriodCaption), nameof(Days), nameof(DayOfChart), nameof(CurveEmptyText))]
    private DateTime _anchor = DateTime.Today;

    /// <summary>The first day with fan speeds recorded (the picker goes no further back).</summary>
    [ObservableProperty]
    private DateTime? _firstDay;

    /// <summary>The picker's custom range (not offered here; bound so the picker has somewhere to put it).</summary>
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    private (DateTime From, DateTime To) Period => ReportBuilder.Bounds(Unit, Anchor);
    public bool IsDay => Unit == ReportRange.Day;

    /// <summary>A year: read from each day's totals and curves (a year of minutes would be too many), so a trend.</summary>
    public bool IsYear => Unit == ReportRange.Year;
    public bool IncludesToday => Period.To > DateTime.Today;

    /// <summary>The speed-at-each-temperature chart with no minutes: still filling in today, or not recorded for a period that's over.</summary>
    public string CurveEmptyText => !IncludesToday ? Wording.NotRecorded : IsDay ? Lang.T("Nothing measured yet today") : Lang.T("Nothing measured yet");
    public string RangeNote => Controls.PeriodPicker.Span(Unit, Anchor);

    /// <summary>The period as a caption ("TODAY", "THIS WEEK", "MON, 29 SEP").</summary>
    public string PeriodCaption => Controls.PeriodPicker.Text(Unit, Anchor).ToUpperInvariant();

    /// <summary>The day the speed chart shows (a Day period).</summary>
    public DateTime DayOfChart => Period.From;

    /// <summary>The days in the period, for the week's and month's speed chart.</summary>
    public int Days => (int)Math.Round((Period.To - Period.From).TotalDays);

    partial void OnUnitChanged(ReportRange value) { if (!_quiet) _ = RefreshAsync(); }
    partial void OnAnchorChanged(DateTime value) { if (!_quiet) _ = RefreshAsync(); }

    /// <summary>Midnight passed with the window open (see <see cref="Controls.PeriodPicker.AfterMidnight"/>).</summary>
    public void NewDay(DateTime was)
    {
        _loadedPast = null;
        var anchor = Controls.PeriodPicker.AfterMidnight(Unit, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        OnPropertyChanged(nameof(RangeNote));
        OnPropertyChanged(nameof(PeriodCaption));
        _ = RefreshAsync();
    }

    // A past period doesn't change: read once, not again each minute.
    private (ReportRange Unit, DateTime From)? _loadedPast;

    private readonly ReportService _reports;
    private readonly SettingsModel _settings;
    private int _load;

    /// <param name="listen">Off for a page that's never shown (see <see cref="SummaryAsync"/>): it doesn't follow the live readings.</param>
    public FansViewModel(ReportService reports, LiveData live, SettingsModel settings, bool listen = true)
    {
        _reports = reports;
        _settings = settings;
        Live = live;
        _listen = listen;
        if (!listen) return;
        live.PropertyChanged += OnLiveChanged;
        // New hardware (the agent reconnected): the rows again, once the page has been opened.
        live.SensorsRebuilt += () => { if (Loaded) _ = RefreshAsync(); };
    }

    /// <summary>
    /// The page's summary for a period, worked out the way the page works it out (a page of its own, never shown): what
    /// the Export box saves for a period other than the one on screen.
    /// </summary>
    internal static async Task<List<FactRow>> SummaryAsync(ReportService reports, LiveData live, SettingsModel settings, ReportRange unit, DateTime anchor)
    {
        var page = new FansViewModel(reports, live, settings, listen: false);
        // Its period set without a read for each step (setting Unit and Anchor one by one reads twice, once for a period nobody wants).
        page._quiet = true;
        (page.Unit, page.Anchor) = (unit, anchor);
        page._quiet = false;
        await page.RefreshAsync();
        return page.SummaryFacts();
    }

    private readonly bool _listen;

    // Set while a page that's never shown is given its period (see SummaryAsync).
    private bool _quiet;

    /// <summary>The summary as the page shows it now: quiet time (a year: average speed), hardest, fastest, and the period's card.</summary>
    internal List<FactRow> SummaryFacts()
    {
        var facts = new List<FactRow>
        {
            new(IsYear ? Lang.T("Average speed") : Lang.T("Quiet time"), QuietText, QuietNote),
            new(Lang.T("Made them work hardest"), HardestApp, HardestNote),
            new(Lang.T("Fastest"), FastestText, FastestNote),
        };
        if (LoudestHour is { } loud) facts.Add(new(LoudestLabel, loud, LoudestNote));
        if (StartsText is { } starts) facts.Add(new(StartsLabel, starts, StartsNote));
        if (SilentText is { } silent) facts.Add(new(SilentLabel, silent, SilentNote));
        return facts;
    }

    public LiveData Live { get; }

    /// <summary>The rows, in the list's order (graphics cards, coolers, then the motherboard's headers).</summary>
    public ObservableCollection<FanCard> Fans { get; } = [];

    /// <summary>The row whose details and chart show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartSeries), nameof(HasSelection))]
    private FanCard? _selected;

    public IReadOnlyList<ChartSeries> ChartSeries => Selected?.Series ?? [];
    public bool HasSelection => Selected is not null;

    /// <summary>Headers on the board that have never spun (nothing plugged in), left out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyHeadersText))]
    private int _emptyHeaders;

    public string EmptyHeadersText => EmptyHeaders switch
    {
        0 => "",
        1 => Lang.T("1 fan header free"),
        var n => Lang.T($"{n} fan headers free"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFans), nameof(ShowLoading))]
    private bool _loaded;
    public bool HasFans => Fans.Count > 0;

    /// <summary>"No fan speeds reported": only once the agent's readings are in and the period is read (a loader until then).</summary>
    public bool ShowNoFans => Loaded && !HasFans && !Live.ReadingsPending;
    public bool ShowLoading => !HasFans && !ShowNoFans;

    // The row of answers at the top.

    /// <summary>"All OK", or how many fans need a look; and how many fans (and free headers) there are.</summary>
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusOk = true;
    [ObservableProperty] private string _statusNote = "";

    /// <summary>How long a graphics card's fans stood still (a year: their average speed, as days are kept).</summary>
    [ObservableProperty] private string _quietLabel = Lang.T("QUIET TIME");
    [ObservableProperty] private string _quietText = "—";
    [ObservableProperty] private string _quietNote = "";

    /// <summary>The app that made a fan on a curve spin hardest, and how fast (always shown: "Nothing yet" on a quiet day).</summary>
    [ObservableProperty] private string _hardestApp = "—";
    [ObservableProperty] private string _hardestNote = "";

    /// <summary>The fastest any fan spun, and what was going on.</summary>
    [ObservableProperty] private string _fastestText = "—";
    [ObservableProperty] private string _fastestNote = "";

    // The "Today" card under the even row (approved mock-up 2026-11-14): a strip of the period's loudness and three facts.

    /// <summary>The hour (a day), the day (a week, a month) or the month (a year) the fans ran loudest, and what was in front then.</summary>
    [ObservableProperty] private string? _loudestHour;
    [ObservableProperty] private string _loudestNote = "";
    [ObservableProperty] private string _loudestLabel = Lang.T("Loudest hour");

    /// <summary>How many times the fans started up (a card's, or one fan that stops), or sped up from quiet with none that stops.</summary>
    [ObservableProperty] private string? _startsText;
    [ObservableProperty] private string _startsNote = "";
    [ObservableProperty] private string _startsLabel = Lang.T("GPU fans started up");

    /// <summary>The longest stretch the fans that stop stood still (or, with none, stayed quiet), and when.</summary>
    [ObservableProperty] private string? _silentText;
    [ObservableProperty] private string _silentNote = "";
    [ObservableProperty] private string _silentLabel = Lang.T("Longest silent stretch");

    /// <summary>The card's three facts, its strip and its legend.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToday))]
    private IReadOnlyList<FanTodayFact> _todayFacts = [];
    [ObservableProperty] private Controls.FanStripData? _strip;
    [ObservableProperty] private IReadOnlyList<FanLevelKey> _levelKeys = [];

    public bool HasToday => TodayFacts.Count > 0;

    /// <summary>The level each minute was at, from the period's minutes (shared with the year's today).</summary>
    internal LevelRun? Levels { get; private set; }

    // The "Over the months" tile under the list (every period: it's about the weeks, not the period picked).

    /// <summary>A line a fan row: its speed at the same heat now against its first weeks (see <see cref="Core.Reports.Wear.Fans"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWear))]
    private IReadOnlyList<WearTileRow> _wearRows = [];
    public bool HasWear => WearRows.Count > 0;

    /// <summary>"Against late September": the first weeks the lines compare with.</summary>
    [ObservableProperty] private string _wearAgainst = "";

    /// <summary>Every fan's weeks, read once a day (they change by the week).</summary>
    private (DateTime Day, List<Core.Reports.WearRow> Rows)? _fanWear;

    private async Task LoadWearAsync(int id, IReadOnlyList<FanCard> cards, IReadOnlyDictionary<string, long> bySensor)
    {
        if (_fanWear?.Day != DateTime.Today && await _reports.FanWearAsync() is { } rows) _fanWear = (DateTime.Today, rows);
        if (id != _load || _fanWear is not { } wear) return;
        var byId = wear.Rows.ToDictionary(r => r.Part);
        var fans = new List<WearTiles.FanWear>();
        foreach (var card in cards)
        {
            // A card's fans together: the one with the most weeks speaks for them (they turn as one).
            var mine = card.Sensors.Select(x => bySensor.TryGetValue(x.Id, out var f) && byId.TryGetValue($"fan:{f}", out var r) ? r : null)
                .OfType<Core.Reports.WearRow>().MaxBy(r => r.History.Count);
            if (mine is null) continue;
            fans.Add(new WearTiles.FanWear(card.Title, card.IsGroup, mine, card.Series.Count > 0 ? card.Series[0].Brush : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("MutedBrush")));
        }
        WearRows = WearTiles.Fans(fans);
        WearAgainst = WearTiles.Against(fans.Where(f => f.Wear.State is Core.Reports.WearState.Moved or Core.Reports.WearState.Steady).Select(f => f.Wear.ThenDate).Where(d => d is not null).Min());
    }

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveData.ReadingsPending))
        {
            OnPropertyChanged(nameof(ShowNoFans));
            OnPropertyChanged(nameof(ShowLoading));
        }
        if (e.PropertyName != nameof(LiveData.Tick)) return;
        foreach (var f in Fans) f.UpdateState(Live.CpuTemp?.Value, Live.GpuTemp?.Value);
    }

    public async Task RefreshAsync()
    {
        var (from, to) = Period;
        // A past period is read once; the minute refresh only moves the live readings then.
        if (!IncludesToday && _loadedPast == (Unit, from) && Fans.Count > 0)
        {
            _load++; // a read still on its way is for a period since left: it mustn't land on this one
            IsLoading = false;
            return;
        }
        int id = ++_load;
        IsLoading = true;
        try
        {
            await ReadAsync(id, from, to);
        }
        finally
        {
            if (id == _load) IsLoading = false;
        }
    }

    /// <summary>The period is being read (the picker says so when it takes a moment).</summary>
    [ObservableProperty] private bool _isLoading;

    private async Task ReadAsync(int id, DateTime from, DateTime to)
    {
        var today = DateTime.Today;
        // A year: its days and daily curves, and today's minutes (what each fan follows, its state); otherwise the period's
        // minutes, its days, and the month before it for the usual curve.
        var history = IsYear
            ? await _reports.FanHistoryAsync(today.AddDays(-SpunWithinDays), today, today.AddDays(1), from, to, from, to)
            : await _reports.FanHistoryAsync(today.AddDays(-SpunWithinDays), from, to);
        if (id != _load || history is null) return;
        // The period's fan warnings, as Home and Reports show them: on the row they're about.
        var warnings = (await _reports.BuildAsync(Unit, Anchor))?.Insights
            .Where(i => i.Key is "fan-stopped" or "fan-slower" or "fan-older").ToList() ?? [];
        // The first day there is, not the first of the days just read (those go back 90 at most, and the picker stopped there).
        var first = await _reports.FirstFanDayAsync();
        // Each fan's range over the 30 days to the period's end (the Today card's loudness), and a year's finished days.
        var spans = await SpansAsync(IsYear ? today.AddDays(1) : to);
        var levelDays = IsYear ? await _reports.FanLevelDaysAsync(from, today) ?? [] : [];
        if (id != _load) return;
        FirstDay = first ?? today;

        // The first of any two rows for one sensor: a duplicate must not leave the page loading for good.
        var bySensor = history.Fans.GroupBy(f => f.Sensor).ToDictionary(g => g.Key, g => g.First().Id);
        var spun = history.Days.Where(d => d.RpmMax >= FanCard.MinSpinRpm).Select(d => d.Fan).ToHashSet();
        var minutes = history.System.ToDictionary(m => m.Ts);
        var byFan = history.Minutes.GroupBy(m => m.Fan).ToDictionary(g => g.Key, g => g.ToList());
        var apps = history.Apps.ToDictionary(a => a.Id);
        var s = _settings.Current;
        // A launcher named for the GPU's work while a game was in front: the game's (see GameWork).
        GameWork.Settle(history.System, app => app is long i && apps.TryGetValue(i, out var row) ? CategoryOf(row, s) : Core.Settings.AppCategory.Other);
        List<FanMinute> MinutesOf(SensorItem x) => bySensor.TryGetValue(x.Id, out var f) && byFan.TryGetValue(f, out var list) ? list : [];

        // A graphics card's fans always show, as one row per card (they stand still for days on a light load); a board
        // or cooler header only once it has spun, now, today or in the recorded days. The widgets' Fans tile lists the
        // same rows (FanList).
        var sensors = Live.AllSensors.Where(x => x.Kind == SensorKind.Fan).ToList();
        bool HasSpun(SensorItem x) => x.Value >= FanCard.MinSpinRpm || x.Max >= FanCard.MinSpinRpm || bySensor.TryGetValue(x.Id, out var f) && spun.Contains(f);
        var rows = FanList.Rows(sensors, x => x.HardwareType, x => x.HardwareName, HasSpun);
        int empty = sensors.Count(x => !FanList.IsGpu(x.HardwareType) && !HasSpun(x));

        var shown = new List<FanCard>();
        foreach (var fans in rows)
        {
            var card = Fans.FirstOrDefault(c => c.Sensors.SequenceEqual(fans)) ?? NewCard(fans, _listen);
            var ids = fans.Select(x => bySensor.TryGetValue(x.Id, out var f) ? f : -1).Where(f => f >= 0).ToHashSet();
            var fanDays = history.Days.Where(d => ids.Contains(d.Fan)).ToList();
            // A fan given another speed or curve during the period: what it did since (the two together are an average it
            // never ran at). A card's fans stand still or not with a degree of heat at idle: not looked at.
            DateTime? changed = !card.IsGpu && !IsYear && FanSetting.ChangedOn(fanDays) is long day ? TimeUtil.FromUnix(day).Date : null;
            long since = changed > from ? TimeUtil.ToUnix(changed.Value) : 0;
            List<FanMinute> Current(SensorItem x) => since > 0 ? [.. MinutesOf(x).Where(m => m.Ts >= since)] : MinutesOf(x);
            var each = fans.Select(x => FanAnalysis.Of(card.IsGpu, Current(x), minutes)).ToList();
            card.Facts = FanCard.Merge(each);
            card.FastestLine = card.Facts.Fastest is { } p
                ? PeakWords.Line(p.Rpm, p.App is long a && apps.TryGetValue(a, out var app) ? NameOf(app, s) : null, p.Ts,
                    p.App is long b && apps.TryGetValue(b, out var row) ? CategoryOf(row, s) : null) ?? ""
                : "";
            foreach (var (series, sensor) in card.Series.Zip(fans))
                series.LoadPoints(MinutesOf(sensor).Select(m => (m.Ts, (double?)m.RpmAvg)));
            // A fan line says which fan it's about: the card's, or a board fan by the name its chip gives it.
            var warning = warnings.FirstOrDefault(w => w.FanName is { } name && (card.IsGpu
                ? w.FanGpu
                : !w.FanGpu && fans.Any(x => x.Name == name)));
            card.Alert = warning?.Text ?? "";
            card.AlertIsHot = warning?.Tone == Core.Reports.InsightTone.Hot;
            card.MinutesOn = fans.Max(x => Current(x).Count);
            var curveDays = history.CurveDays.Where(c => ids.Contains(c.Fan) && (changed is null || TimeUtil.FromUnix(c.Day).Date >= changed)).ToList();
            // Worked out off the window's thread (a day of minutes for each fan: a tenth of a second for a PC with five).
            var current = fans.Select(Current).ToList();
            var detail = IsYear ? YearOf(card, curveDays, fanDays, apps, s, from, to)
                : await Task.Run(() => DetailOf(card, current, minutes, curveDays, fanDays, apps, s, from, to, changed));
            if (id != _load) return;
            card.Detail = detail;
            card.UpdateState(Live.CpuTemp?.Value, Live.GpuTemp?.Value);
            shown.Add(card);
        }

        // A board with two fan chips (each with its own "Fan #1"…): the second chip's fans under a heading of their own.
        // (The list's groups are made as rows are added: a row whose heading changes is added again.)
        // The board's own fans: neither a card's nor a cooler's (the heading is in the person's language, so not compared).
        static bool OnBoard(FanCard c) => !c.IsGpu && c.Sensor.HardwareType != "Cooler";
        var chips = shown.Where(OnBoard).Select(c => c.Sensor.HardwareName).Distinct().ToList();
        bool regroup = false;
        foreach (var c in shown.Where(OnBoard))
        {
            string place = chips.IndexOf(c.Sensor.HardwareName) is > 0 and var i ? Lang.T($"Motherboard (chip {i + 1})") : Lang.T("Motherboard");
            regroup |= c.Place != place;
            c.Place = place;
        }

        var picked = Selected; // the list clears its pick while its rows are put back
        if (regroup || !shown.SequenceEqual(Fans))
        {
            Fans.Clear();
            foreach (var c in shown) Fans.Add(c);
            OnPropertyChanged(nameof(HasFans));
            OnPropertyChanged(nameof(ShowNoFans));
            OnPropertyChanged(nameof(ShowLoading));
        }
        EmptyHeaders = empty;
        Summarise();
        // The rows as the levels see them (a card's fans together), each against its range; worked out off the window's thread.
        var levelRows = shown.Select(c => new LevelRow(c.Title, [.. c.Sensors.Select(x => bySensor.TryGetValue(x.Id, out var f) ? f : -1).Where(f => f >= 0)], c.IsGpu)).ToList();
        var spanOf = spans.GroupBy(x => x.Fan).ToDictionary(g => g.Key, g => g.First());
        var ranges = levelRows.Select(r => FanLevels.RangeOf(r.Fans.Where(spanOf.ContainsKey).Select(f => spanOf[f]))).ToList();
        var run = await Task.Run(() => FanLevels.Of(levelRows, ranges, history.Minutes));
        if (id != _load) return;
        Levels = run;
        if (IsYear) SummariseYear(run, levelDays, minutes, apps, s);
        else SummariseToday(run, minutes, apps, s);
        Selected = picked is not null && Fans.Contains(picked) ? picked : Fans.FirstOrDefault();
        OnPropertyChanged(nameof(ChartSeries));
        await LoadWearAsync(id, shown, bySensor);
        if (id != _load) return;
        _loadedPast = IncludesToday ? null : (Unit, from);
        Loaded = true;
    }

    /// <summary>The row of answers at the top, from the rows as they now stand.</summary>
    private void Summarise()
    {
        int needs = Fans.Count(f => f.NeedsLook), total = Fans.Sum(f => f.Sensors.Count);
        StatusOk = needs == 0;
        StatusText = needs == 0 ? Lang.T("All OK") : needs == 1 ? Lang.T("1 needs a look") : Lang.T($"{needs} need a look");
        StatusNote = (total == 1 ? Lang.T("1 fan") : Lang.T($"{total} fans")) + (EmptyHeaders > 0 ? $" · {EmptyHeadersText}" : "");

        var card = Fans.FirstOrDefault(f => f.IsGpu);
        if (IsYear)
        {
            // Kept by the day: how fast the card's fans ran on average instead of how long they stood still.
            QuietLabel = Lang.T("AVERAGE SPEED");
            QuietText = card?.Detail?.AverageRpm is double avg ? Units.Format(SensorKind.Fan, avg) : "—";
            QuietNote = card is null ? "" : Lang.T("GPU fans, across every day of the 12 months");
        }
        else
        {
            QuietLabel = Lang.T("QUIET TIME");
            QuietText = card?.Facts is { StoppedMinutes: > 0 } q ? Units.Duration(q.StoppedMinutes * 60) : card is null ? "—" : Lang.T("None");
            QuietNote = card is null ? Lang.T("No GPU fans reported") : card.Facts is { StoppedMinutes: > 0 } ? Lang.T("GPU fans stood still while the GPU was cool") : Lang.T("GPU fans turned the whole time");
        }

        // The app that made a fan on a curve work hardest: the card's first, else any that follows a chip.
        var curve = Fans.Where(f => f.IsGpu || f.Facts?.Follows is FanFollows.Gpu or FanFollows.Cpu).OrderByDescending(f => f.IsGpu).FirstOrDefault();
        var top = curve?.Detail?.Apps.FirstOrDefault(a => !a.Dim);
        HardestApp = top?.Name ?? Lang.T("Nothing yet");
        HardestNote = top is not null ? Lang.T($"{(curve!.IsGroup ? Lang.T("GPU fans") : curve.Title)} at {top.RpmText} on average")
            : IsDay && IncludesToday ? Lang.T("No game or app has warmed them up for long today") : Lang.T("Nothing warmed them up for long");

        var fastest = Fans.Where(f => f.Detail?.FastestRpm is not null).MaxBy(f => f.Detail!.FastestRpm);
        FastestText = fastest is null ? "—" : Units.Format(SensorKind.Fan, fastest.Detail!.FastestRpm);
        FastestNote = fastest is null ? "" : fastest.Detail!.FastestWhen.Length > 0 ? $"{fastest.Title} · {fastest.Detail.FastestWhen}" : fastest.Title;
    }

    // ── The Today card (approved mock-up 2026-11-14) ──

    private (DateTime To, int Hour, List<FanSpan> Spans)? _spans;

    /// <summary>Each fan's range over the 30 days to <paramref name="to"/>: read again each hour while the period runs to now, once for one that's over.</summary>
    private async Task<List<FanSpan>> SpansAsync(DateTime to)
    {
        int hour = to > DateTime.Now ? DateTime.Now.Hour : -1;
        if (_spans is { } c && c.To == to && c.Hour == hour) return c.Spans;
        var spans = await _reports.FanSpansAsync(to) ?? [];
        _spans = (to, hour, spans);
        return spans;
    }

    /// <summary>What the middle fact is called for what it counts.</summary>
    internal static string StartsName(FanStartKind kind) => kind switch
    {
        FanStartKind.GpuFans => Lang.T("GPU fans started up"),
        FanStartKind.OneFan => Lang.T("The fan started up"),
        _ => Lang.T("Fans sped up"),
    };

    private static string Times(int n) => n == 1 ? Lang.T("1 time") : Lang.T($"{n:N0} times");

    /// <summary>"While playing Rematch", in the peaks' words, for the app in front while the fans ran hardest (no app: said so).</summary>
    private static string While(long? app, IReadOnlyDictionary<long, AppRow> apps, Core.Settings.RigsightSettings s) =>
        app is long a && apps.TryGetValue(a, out var row) ? PeakWords.Line(1, NameOf(row, s), null, CategoryOf(row, s)) ?? "" : Controls.HeatHover.NoApp;

    /// <summary>The same inside a sentence: "while playing Rematch".</summary>
    private static string WhileInside(AppRow app, Core.Settings.RigsightSettings s)
    {
        string name = NameOf(app, s);
        return CategoryOf(app, s) switch
        {
            Core.Settings.AppCategory.Game => Lang.T($"while playing {name}"),
            Core.Settings.AppCategory.Media => Lang.T($"while {name} was playing"),
            Core.Settings.AppCategory.Browser or Core.Settings.AppCategory.Communication or Core.Settings.AppCategory.Productivity or Core.Settings.AppCategory.Development => Lang.T($"while using {name}"),
            _ => Lang.T($"while {name} was running"),
        };
    }

    /// <summary>"6 PM" and "9 PM": the hours a bunch of starts began and ended.</summary>
    private static (string From, string To) Hours(int hour) =>
        (TimeText.At(DateTime.Today.AddHours(hour), "h tt"), TimeText.At(DateTime.Today.AddHours(hour + FanLevels.BunchHours), "h tt"));

    /// <summary>The legend: the levels there can be (no Silent with no fan that stops), and on a day, the starts' mark.</summary>
    private static List<FanLevelKey> Legend(FanStartKind kind, bool day)
    {
        string range = Lang.T("A fan's range runs from its slowest to its fastest while spinning, over the last 30 days.");
        var keys = new List<FanLevelKey>();
        if (kind != FanStartKind.SpeedUps)
            keys.Add(new(Controls.FanStrip.LevelName(FanLevel.Silent), (kind == FanStartKind.GpuFans
                ? Lang.T("GPU fans standing still, and every other fan in the slowest third of its range.")
                : Lang.T("The fan standing still, and every other fan in the slowest third of its range.")) + " " + range, 0, 0));
        keys.Add(new(Controls.FanStrip.LevelName(FanLevel.Quiet), Lang.T("Every fan in the slowest third of its range.") + " " + range, 1, 0));
        keys.Add(new(Controls.FanStrip.LevelName(FanLevel.Busy), Lang.T("A fan in the middle third of its range.") + " " + range, 2, 0));
        keys.Add(new(Controls.FanStrip.LevelName(FanLevel.Loud), Lang.T("A fan in the fastest third of its range.") + " " + range, 3, 0));
        keys = [.. keys.Select(k => k with { Height = 4 + Controls.FanStrip.Tall[(int)k.Height] * 8, Strength = Controls.FanStrip.Strength[(int)k.Height] })];
        if (day)
            keys.Add(new(StartsName(kind), kind switch
            {
                FanStartKind.GpuFans => Lang.T("Each mark is a minute in which the GPU fans started up."),
                FanStartKind.OneFan => Lang.T("Each mark is a minute in which the fan started up."),
                _ => Lang.T("Each mark is a minute in which the fans went from quiet to busy or loud."),
            }, 10, 1, IsTick: true, Gpu: kind == FanStartKind.GpuFans));
        return keys;
    }

    /// <summary>A bar's hover rows: each level's time (Silent only where there can be one), then its starts.</summary>
    private static List<(string, string)> BarRows(int[] levels, int starts, FanStartKind kind) =>
        [.. Enumerable.Range(0, 4).Where(k => k > 0 || kind != FanStartKind.SpeedUps)
            .Select(k => (Controls.FanStrip.LevelName((FanLevel)k), Units.Duration(levels[k] * 60))), (StartsName(kind), Times(starts))];

    /// <summary>The longest stretch's fact: how long and when, or none.</summary>
    private void SetLongest(FanStartKind kind, long from, int length, bool withDay)
    {
        SilentLabel = kind == FanStartKind.SpeedUps ? Lang.T("Longest quiet stretch") : Lang.T("Longest silent stretch");
        SilentText = length > 0 ? Units.Duration(length * 60) : Lang.T("None");
        var (a, z) = (TimeUtil.FromUnix(from), TimeUtil.FromUnix(from + length * 60L));
        SilentNote = length > 0
            ? withDay ? $"{TimeText.At(a, "ddd d MMM, h:mm tt")} – {TimeText.At(z, "h:mm tt")}" : $"{TimeText.At(a, "h:mm tt")} – {TimeText.At(z, "h:mm tt")}"
            : kind switch
            {
                FanStartKind.GpuFans => Lang.T("The fans never stopped"),
                FanStartKind.OneFan => Lang.T("The fan never stopped"),
                _ => Lang.T("No quiet minutes"),
            };
    }

    private void SetFacts(FanStartKind kind)
    {
        TodayFacts =
        [
            new("loud", LoudestLabel, LoudestHour ?? Wording.NotRecorded, LoudestNote, FanFactTone.Loud),
            new("starts", StartsLabel, StartsText ?? "", StartsNote, kind == FanStartKind.GpuFans ? FanFactTone.GpuStarts : FanFactTone.FanStarts),
            new("sil", SilentLabel, SilentText ?? "", SilentNote, FanFactTone.Still),
        ];
    }

    private void ClearToday()
    {
        (LoudestHour, LoudestNote, StartsText, StartsNote, SilentText, SilentNote) = (null, "", null, "", null, "");
        TodayFacts = [];
        Strip = null;
        LevelKeys = [];
    }

    /// <summary>
    /// The Today card for a day, a week or a month, from the period's minutes (see <see cref="FanLevels"/>): the loudest
    /// hour (day), the fans starting up and where they bunched, the longest silent stretch, and the strip.
    /// </summary>
    private void SummariseToday(LevelRun run, IReadOnlyDictionary<long, SystemMinute> minutes, IReadOnlyDictionary<long, AppRow> apps, Core.Settings.RigsightSettings s)
    {
        if (run.Minutes.Count == 0)
        {
            ClearToday();
            return;
        }
        var (from, to) = Period;
        long? AppAt(long ts) => minutes.TryGetValue(ts, out var m) ? m.FgApp : null;
        var kind = run.Kind;
        int n = run.Starts.Count;
        StartsLabel = StartsName(kind);
        StartsText = Times(n);
        var bunch = FanLevels.Bunch(run.Starts);
        var marks = new Dictionary<string, Controls.FanStripMark>();

        if (IsDay)
        {
            var loud = FanLevels.Loudest(run.Minutes, t => t.Date.AddHours(t.Hour), FanLevels.HourMinutes);
            LoudestLabel = Lang.T("Loudest hour");
            LoudestHour = loud is { } l ? $"{TimeText.At(l.Slot, "h tt")} – {TimeText.At(l.Slot.AddHours(1), "h tt")}" : null;
            LoudestNote = loud is { } l2 ? While(FanLevels.AppWhileHardest(l2.Minutes, AppAt), apps, s)
                : IncludesToday ? Lang.T("No hour with 20 minutes on yet") : Lang.T("No hour with 20 minutes on");

            // Where the starts bunched, and how warm the GPU sat at them (on a laptop, what was in front).
            string onFor = Units.Duration(run.Minutes.Count * 60);
            if (n < FanLevels.BunchFrom) StartsNote = Lang.T($"Over {onFor} on");
            else if (bunch is { } b)
            {
                var (a, z) = Hours(b.Hour);
                int k = b.Starts.Count;
                string lead = k == n ? Lang.T($"All {n} between {a} and {z}") : Lang.T($"{k} of them between {a} and {z}");
                string note = lead;
                if (kind == FanStartKind.GpuFans)
                {
                    var temps = b.Starts.Select(t => minutes.TryGetValue(t, out var m) ? m.GpuTemp : null).OfType<double>().ToList();
                    if (temps.Count > 0) note = Lang.T($"{lead}, while the GPU sat around {Units.TempShort(Math.Round(temps.Average()))}");
                }
                else if (b.Starts.Select(AppAt).OfType<long>().GroupBy(x => x).MaxBy(g => g.Count())?.Key is long app && apps.TryGetValue(app, out var row))
                    note = Lang.T($"{lead}, {WhileInside(row, s)}");
                StartsNote = kind == FanStartKind.SpeedUps ? Lang.T("Each from quiet to busy or loud.") + " " + note : note;
            }
            else StartsNote = kind == FanStartKind.SpeedUps ? Lang.T("Each from quiet to busy or loud") : Lang.T($"Spread across {onFor} on");
            SetLongest(kind, run.Longest.From, run.Longest.Minutes, withDay: false);

            // The strip: from the first hour the PC was on to the last, six hours at least, so a day that starts at 9 AM has no empty morning.
            DateTime first = TimeUtil.FromUnix(run.Minutes[0].Ts), last = TimeUtil.FromUnix(run.Minutes[^1].Ts + 60);
            DateTime start = first.Date.AddHours(first.Hour), end = last.Date.AddHours(last.Hour);
            if (end < last) end = end.AddHours(1);
            if (end - start < TimeSpan.FromHours(6))
            {
                var mid = start + (end - start) / 2;
                start = mid.AddHours(-3);
                start = start.Date.AddHours(start.Hour);
                if (start < from) start = from;
                end = start.AddHours(6);
                if (end > from.AddDays(1)) (end, start) = (from.AddDays(1), from.AddDays(1).AddHours(-6));
            }
            long t0 = TimeUtil.ToUnix(start);
            int span = (int)((TimeUtil.ToUnix(end) - t0) / 60);
            var levels = new FanLevel?[span];
            var at = new Dictionary<long, LevelMinute>();
            foreach (var m in run.Minutes)
            {
                at[m.Ts] = m;
                long i = (m.Ts - t0) / 60;
                if (i >= 0 && i < span) levels[i] = m.Level;
            }
            int Index(long ts) => (int)((ts - t0) / 60);
            if (loud is { } lh) marks["loud"] = new(Index(TimeUtil.ToUnix(lh.Slot)), Index(TimeUtil.ToUnix(lh.Slot)) + 60, Lang.T("Loudest hour"));
            if (run.Longest.Minutes > 0) marks["sil"] = new(Index(run.Longest.From), Index(run.Longest.From) + run.Longest.Minutes, SilentLabel);
            if (bunch is { } bb)
            {
                int i = Index(TimeUtil.ToUnix(from.AddHours(bb.Hour)));
                marks["starts"] = new(i, i + FanLevels.BunchHours * 60, Lang.T($"{bb.Starts.Count} of {n}"), Ticks: true);
            }
            var starts = run.Starts.ToHashSet();
            var rows = run.Rows;
            Controls.FanStripTip TipAt(int i)
            {
                long ts = t0 + i * 60L;
                if (!at.TryGetValue(ts, out var m))
                {
                    int a = i, z = i;
                    while (a > 0 && levels[a - 1] is null) a--;
                    while (z < span - 1 && levels[z + 1] is null) z++;
                    return new($"{TimeText.Time(start.AddMinutes(a))} – {TimeText.Time(start.AddMinutes(z + 1))}", null, [], Wording.NotRecorded);
                }
                var counted = Enumerable.Range(0, rows.Count).Where(r => m.Places[r] is not null).OrderByDescending(r => m.Places[r]).ToList();
                var others = Enumerable.Range(0, rows.Count).Where(r => m.Places[r] is null && m.Speeds[r] is not null).ToList();
                var list = counted.Take(3).Select(r => (rows[r].Name, Units.Format(SensorKind.Fan, m.Speeds[r]))).ToList();
                int more = counted.Count - 3 + others.Count;
                if (counted.Count <= 3) list.AddRange(others.Select(r => (rows[r].Name, Units.Format(SensorKind.Fan, m.Speeds[r]))));
                else if (more > 0) list.Add((more == 1 ? Lang.T("1 more fan") : Lang.T($"{more} more fans"), Lang.T("Slow or set")));
                if (minutes.TryGetValue(ts, out var sm))
                {
                    if (sm.GpuTemp is double g) list.Add(("GPU", Units.TempShort(g)));
                    else if (sm.CpuTemp is double c) list.Add(("CPU", Units.TempShort(c)));
                    if (sm.FgApp is long fg && apps.TryGetValue(fg, out var app)) list.Add((Lang.T("In front"), NameOf(app, s)));
                }
                string? note = !starts.Contains(ts) ? null : kind switch
                {
                    FanStartKind.GpuFans => Lang.T("GPU fans started up"),
                    FanStartKind.OneFan => Lang.T("The fan started up"),
                    _ => Lang.T("The fans sped up"),
                };
                var when = TimeUtil.FromUnix(ts);
                return new($"{TimeText.Time(when)} – {TimeText.Time(when.AddMinutes(1))}", m.Level, list, note);
            }
            int hours = span / 60;
            Strip = new Controls.FanStripData
            {
                IsDay = true, From = start, Levels = levels, GpuTicks = kind == FanStartKind.GpuFans, HourStep = Math.Max(1, (int)Math.Ceiling(hours / 8.0)),
                Ticks = [.. run.Starts.Select(Index).Where(i => i >= 0 && i < span)], TipAt = TipAt, Marks = marks,
            };
        }
        else
        {
            var loud = FanLevels.Loudest(run.Minutes, t => t.Date, FanLevels.DayMinutes);
            LoudestLabel = Lang.T("Loudest day");
            LoudestHour = loud?.Slot.ToString("dddd d MMM");
            LoudestNote = loud is { } l ? While(FanLevels.AppWhileHardest(l.Minutes, AppAt), apps, s) : Lang.T("No day with an hour on");
            var byDay = run.Minutes.GroupBy(m => TimeUtil.FromUnix(m.Ts).Date).ToDictionary(g => g.Key, g => g.ToList());
            int daysOn = byDay.Count;
            double rate = Math.Round(n / (double)daysOn);
            if (rate >= 1)
            {
                if (bunch is { } b)
                {
                    var (a, z) = Hours(b.Hour);
                    StartsNote = Lang.T($"About {rate:0} a day, most between {a} and {z}");
                }
                else StartsNote = Lang.T($"About {rate:0} a day");
            }
            else StartsNote = daysOn == 1 ? Lang.T("Across 1 day recorded") : Lang.T($"Across {daysOn} days recorded");
            SetLongest(kind, run.Longest.From, run.Longest.Minutes, withDay: true);

            // A bar a day of the four levels.
            var days = Enumerable.Range(0, Days).Select(i => from.AddDays(i)).ToList();
            var startsBy = run.Starts.GroupBy(t => TimeUtil.FromUnix(t).Date).ToDictionary(g => g.Key, g => g.Count());
            var bars = days.Select((d, i) =>
            {
                string title = d.ToString("ddd d MMM");
                if (d == DateTime.Today) title = Lang.T($"{title}, today");
                bool labelled = Days <= 7 || i % 5 == 0 || i == Days - 1;
                string label = !labelled ? "" : Days <= 7 ? d.ToString("ddd d") : d.ToString("d MMM");
                if (!byDay.TryGetValue(d, out var list)) return new Controls.FanStripBar(d, new int[4], label, d == DateTime.Today, new(title, null, [], Wording.NotRecorded));
                var lv = FanLevels.LevelMinutes(list);
                return new Controls.FanStripBar(d, lv, label, d == DateTime.Today, new(title, null, BarRows(lv, startsBy.GetValueOrDefault(d), kind)));
            }).ToList();
            if (loud is { } ld) marks["loud"] = new(days.IndexOf(ld.Slot), days.IndexOf(ld.Slot) + 1, null);
            if (run.Longest.Minutes > 0) marks["sil"] = new(days.IndexOf(TimeUtil.FromUnix(run.Longest.From).Date), 0, null);
            Strip = new Controls.FanStripData { IsDay = false, Bars = bars, GpuTicks = kind == FanStartKind.GpuFans, Marks = marks };
        }
        LevelKeys = Legend(kind, IsDay);
        SetFacts(kind);
    }

    /// <summary>
    /// A year's card, from each finished day's loudness (fan_level_day, kept from 2026-11-14 on, see <see cref="FanLevels"/>)
    /// and today's minutes: the loudest month, the starts a day, the longest silent stretch, and a bar a month of its average day.
    /// </summary>
    private void SummariseYear(LevelRun today, List<FanLevelDay> kept, IReadOnlyDictionary<long, SystemMinute> minutes, IReadOnlyDictionary<long, AppRow> apps,
        Core.Settings.RigsightSettings s)
    {
        var (from, to) = Period;
        var days = kept.Where(d => TimeUtil.FromUnix(d.Day).Date >= from && TimeUtil.FromUnix(d.Day).Date < DateTime.Today).ToList();
        if (today.Minutes.Count > 0 && DateTime.Today >= from && DateTime.Today < to)
            days.Add(FanLevels.DayRow(TimeUtil.ToUnix(DateTime.Today), today, t => minutes.TryGetValue(t, out var m) ? m.FgApp : null));
        if (days.Count == 0)
        {
            ClearToday();
            return;
        }
        var kind = today.Minutes.Count > 0 ? today.Kind : (FanStartKind)days[^1].Kind;
        DateTime MonthOf(FanLevelDay d) => new(TimeUtil.FromUnix(d.Day).Year, TimeUtil.FromUnix(d.Day).Month, 1);
        var byMonth = days.GroupBy(MonthOf).ToDictionary(g => g.Key, g => g.ToList());

        var loud = byMonth.Where(m => m.Value.Sum(d => d.Minutes) >= FanLevels.DayMinutes)
            .Select(m => (Month: m.Key, Score: m.Value.Sum(d => d.ScoreSum) / m.Value.Sum(d => d.Minutes), Days: m.Value))
            .OrderByDescending(m => m.Score).FirstOrDefault();
        LoudestLabel = Lang.T("Loudest month");
        LoudestHour = loud.Days is null ? null : loud.Month.ToString("MMMM yyyy");
        LoudestNote = loud.Days is null ? Lang.T("No day with an hour on")
            : While(loud.Days.Where(d => d.LoudApp is not null).GroupBy(d => d.LoudApp!.Value).MaxBy(g => g.Sum(d => d.LoudAppScore))?.Key, apps, s);

        // The starts and the longest stretch from the days that counted the same thing as today (a day before a card's
        // fans had ever spun counted the fans speeding up and the longest quiet stretch, which aren't the same facts).
        var alike = days.Where(d => d.Kind == (int)kind).ToList();
        if (alike.Count == 0) alike = days;
        int n = alike.Sum(d => d.Starts);
        double rate = Math.Round(n / (double)alike.Count);
        StartsLabel = StartsName(kind);
        StartsText = rate >= 1 ? Lang.T($"About {rate:0} a day") : Times(n);
        StartsNote = alike.Count == 1 ? Lang.T("Across 1 day recorded") : Lang.T($"Across {alike.Count} days recorded");
        var longest = alike.MaxBy(d => d.LongestN)!;
        SetLongest(kind, longest.LongestFrom, longest.LongestN, withDay: true);

        // A bar a month: its average day at each level.
        var months = Enumerable.Range(0, 12).Select(i => from.AddMonths(i)).Where(m => m < to).ToList();
        var bars = months.Select(m =>
        {
            bool now = m.Year == DateTime.Today.Year && m.Month == DateTime.Today.Month;
            string title = m.ToString("MMMM yyyy");
            if (!byMonth.TryGetValue(m, out var list)) return new Controls.FanStripBar(m, new int[4], m.ToString("MMM"), now, new(title, null, [], Wording.NotRecorded));
            var lv = Enumerable.Range(0, 4).Select(k => (int)Math.Round(list.Average(d => d.Levels[k]))).ToArray();
            return new Controls.FanStripBar(m, lv, m.ToString("MMM"), now, new(title, null, BarRows(lv, (int)Math.Round(list.Average(d => d.Starts)), kind), Sub: Lang.T("An average day")));
        }).ToList();
        var marks = new Dictionary<string, Controls.FanStripMark>();
        if (loud.Days is not null) marks["loud"] = new(months.IndexOf(loud.Month), 0, null);
        if (longest.LongestN > 0) marks["sil"] = new(months.IndexOf(MonthOf(longest)), 0, null);
        Strip = new Controls.FanStripData { IsDay = false, Bars = bars, GpuTicks = kind == FanStartKind.GpuFans, Marks = marks };
        LevelKeys = Legend(kind, false);
        SetFacts(kind);
    }

    /// <summary>A row's detail (see <see cref="FanDetail"/>) from its fans' minutes today and their days before.</summary>
    /// <param name="changed">The first day of the speed setting the fan is on (see <see cref="FanSetting"/>), when it
    /// changed: the minutes and daily curves given are from then on, and the days before it are another setting's.</param>
    internal static FanDetail DetailOf(FanCard card, IReadOnlyList<List<FanMinute>> fans, IReadOnlyDictionary<long, SystemMinute> minutes,
        List<FanCurveDay> curveDays, List<FanDay> days, IReadOnlyDictionary<long, AppRow> apps, Core.Settings.RigsightSettings s, DateTime from, DateTime to,
        DateTime? changed = null)
    {
        var facts = card.Facts;
        var follows = facts?.Follows ?? FanFollows.Unknown;
        bool steady = follows == FanFollows.Steady, onGpu = card.IsGpu || follows == FanFollows.Gpu;

        // The row's speed each minute of the period (a card's fans averaged), with the temperature of the chip it follows.
        var speed = fans.SelectMany(f => f).GroupBy(f => f.Ts).ToDictionary(g => g.Key, g => g.Average(f => (double)f.RpmAvg));
        var points = new List<System.Windows.Point>();
        foreach (var (ts, rpm) in speed)
            if (minutes.TryGetValue(ts, out var m) && (onGpu ? m.GpuTemp : m.CpuTemp) is double t)
                points.Add(new System.Windows.Point(t, rpm));
        // Gathered into dots (half a degree, 25 rpm) and an average speed per degree (while turning) for the hover.
        var dots = points.GroupBy(p => (T: Math.Round(p.X * 2) / 2, R: Math.Round(p.Y / 25) * 25))
            .Select(g => new Controls.FanDot(g.Key.T, g.Key.R, g.Count())).ToList();
        var tempBins = points.GroupBy(p => (int)Math.Floor(p.X)).Select(g =>
        {
            var turningHere = g.Where(p => p.Y >= FanCard.MinSpinRpm).Select(p => p.Y).Order().ToList();
            return new Controls.FanTempBin(g.Key, turningHere.Count > 0 ? turningHere.Average() : 0, g.Count(), 1 - turningHere.Count / (double)g.Count());
        }).OrderBy(b => b.Temp).ToList();

        // Where it stands still: up to the warmest degree at which it was seen still in most minutes (not beyond: with no
        // minutes between that and where it turns, where it starts isn't known), when it turned above that.
        double? silent = null;
        if (points.Any(p => p.Y < FanCard.MinSpinRpm))
        {
            var bins = points.GroupBy(p => (int)Math.Floor(p.X)).Where(g => g.Count() >= 3)
                .Select(g => (T: g.Key, Turning: g.Count(p => p.Y >= FanCard.MinSpinRpm) / (double)g.Count())).OrderBy(b => b.T).ToList();
            int lastStill = bins.FindLastIndex(b => b.Turning < 0.5);
            if (lastStill >= 0 && lastStill < bins.Count - 1) silent = bins[lastStill].T + 1;
        }

        // Its usual curve: the month before today under steady load (any app), or a set speed's level across the chart.
        var turning = speed.Values.Where(v => v >= FanCard.MinSpinRpm).ToList();
        var line = new List<System.Windows.Point>();
        string lineLabel = "";
        if (steady && turning.Count > 0 && points.Count > 0)
        {
            double level = turning.Average();
            line = [new(points.Min(p => p.X), level), new(points.Max(p => p.X), level)];
            lineLabel = Lang.T("same speed at every temperature");
        }
        else if (!steady)
        {
            line = [.. curveDays.GroupBy(c => c.Temp)
                .Select(g => (T: g.Key + FanCurves.BinDegrees / 2.0, N: g.Sum(c => c.N), Rpm: g.Sum(c => c.RpmSum) / g.Sum(c => c.N)))
                .Where(b => b.N >= 5).OrderBy(b => b.T).Select(b => new System.Windows.Point(b.T, b.Rpm))];
            if (line.Count < 2) line = [];
            else lineLabel = Lang.T("usual curve");
        }

        // What makes it spin: its average speed while each app worked the chip it follows (enough minutes to count).
        var byApp = new Dictionary<long, (double Sum, int N)>();
        foreach (var (ts, rpm) in speed)
            if (minutes.TryGetValue(ts, out var m) && (onGpu ? m.GpuApp : m.CpuApp) is long app && apps.ContainsKey(app))
            {
                var (sum, n) = byApp.GetValueOrDefault(app);
                byApp[app] = (sum + rpm, n + 1);
            }
        var candidates = byApp.Where(x => x.Value.N >= AppMinutes)
            .Select(x => (Name: NameOf(apps[x.Key], s), Rpm: x.Value.Sum / x.Value.N, x.Value.N)).ToList();
        // A fan at a set speed turns the same whatever runs: no list for it.
        var ordered = steady ? [] : candidates.OrderByDescending(a => a.Rpm).Take(5).ToList();
        double topRpm = ordered.Select(a => a.Rpm).DefaultIfEmpty(1).Max();
        var rows = ordered.Select(a => new FanAppRow(a.Name, Units.Format(SensorKind.Fan, a.Rpm), topRpm > 0 ? 100 * a.Rpm / topRpm : 0,
            !steady && a.Rpm < topRpm * 0.25)).ToList();

        // Each day of the period (a week's, a month's speed chart): its average and fastest (a card's fans together).
        var bars = Enumerable.Range(0, (int)Math.Round((to - from).TotalDays)).Select(i => from.AddDays(i)).Select(d =>
        {
            var ofDay = days.Where(x => TimeUtil.FromUnix(x.Day).Date == d).ToList();
            int n = ofDay.Sum(x => x.RpmN);
            return new Controls.FanDayBar(d, n > 0 ? ofDay.Sum(x => x.RpmSum) / n : null, ofDay.Count > 0 ? ofDay.Max(x => x.RpmMax) : null, d < changed);
        }).ToList();

        string still = facts is { StoppedMinutes: > 0 } f ? Units.Duration(f.StoppedMinutes * 60) : Lang.T("Never");
        string stillNote = facts is { StoppedMinutes: > 0 } && card.MinutesOn > 0 ? Lang.T($"of {Units.Duration(card.MinutesOn * 60)} on") : Lang.T("Turning all the time the PC was on");
        var (followsValue, followsNote) = follows switch
        {
            // A card's own fans: that they follow its heat goes without saying; where they start is what's worth knowing.
            FanFollows.Gpu when card.IsGpu && silent is double sb => (Lang.T($"Around {Units.TempShort(sb)}"), Lang.T("Silent below that, faster as it warms")),
            FanFollows.Gpu => (Lang.T("Your GPU's heat"), Lang.T("Faster as it warms")),
            FanFollows.Cpu => (Lang.T("Your CPU's heat"), Lang.T("Faster as it warms")),
            FanFollows.Steady => (Lang.T("A set speed"), Lang.T($"About {Units.Format(SensorKind.Fan, turning.DefaultIfEmpty().Average())}, whatever the heat")),
            FanFollows.Varies => (Lang.T("Its own curve"), Lang.T("Not clearly with your CPU or GPU")),
            _ => (Lang.T("Not known yet"), Lang.T("Needs an hour of turning")),
        };
        var (thirdLabel, thirdValue, thirdNote) = steady && turning.Count > 0
            ? (Lang.T("Steadiness"), Lang.T($"Within {Math.Round(100 * (turning.Max() - turning.Min()) / turning.Average()):0}%"), Lang.T("From its slowest to its fastest"))
            : (Lang.T("Fastest"), card.FastestText, card.FastestLine);

        // Three more facts. Every fan: its average while turning. A fan on a curve: its average speed while its chip works
        // hard, and (a card's zero-rpm fans) how often they started up; a set speed: its range, and its usual speed the 30
        // days before (a slower fan shows there first).
        string Rpm(double? v) => Units.Format(SensorKind.Fan, v);
        var more = new List<FanFact>
        {
            new(Lang.T("Average while spinning"), turning.Count > 0 ? Rpm(turning.Average()) : "—",
                turning.Count > 0 ? Lang.T($"Over {Units.Duration(turning.Count * 60)} of turning") : Lang.T("It didn't turn")),
        };
        // Usual: under the setting it's on. Days of an earlier one aren't its usual any more.
        bool newSetting = changed > from.AddDays(-UsualDays);
        var usual = days.Where(d => TimeUtil.FromUnix(d.Day).Date >= from.AddDays(-UsualDays) && TimeUtil.FromUnix(d.Day).Date < from
            && !(TimeUtil.FromUnix(d.Day).Date < changed)).ToList();
        int usualN = usual.Sum(d => d.RpmN);
        var usualFact = new FanFact(Lang.T("Usual"), usualN > 0 ? Rpm(usual.Sum(d => d.RpmSum) / usualN) : "—",
            newSetting ? usualN > 0 ? Lang.T($"Since its setting changed on {changed:d MMM}") : Lang.T($"Nothing since its setting changed on {changed:d MMM}")
            : usualN > 0 ? Lang.T($"The {UsualDays} days before") : Lang.T("Not recorded before this"));
        if (steady)
        {
            more.Add(new(Lang.T("Range"), turning.Count > 0 ? Lang.T($"{turning.Min():N0} to {turning.Max():N0}") : "—", Lang.T("Slowest to fastest while spinning, in rpm")));
            more.Add(usualFact);
        }
        else
        {
            var hard = speed.Where(x => minutes.TryGetValue(x.Key, out var m)
                    && (onGpu ? m.GpuLoad >= LoadBands.GpuHeavyLoad : m.CpuLoad >= LoadBands.CpuHeavyLoad) && x.Value >= FanCard.MinSpinRpm)
                .Select(x => x.Value).ToList();
            // The mean of those minutes, called what it is: a middle figure of a period is an average.
            more.Add(new(Lang.T("Under heavy load"), hard.Count >= 5 ? Rpm(hard.Average()) : "—",
                hard.Count >= 5 ? onGpu ? Lang.T("Average while games pushed the GPU") : Lang.T("Average while work pushed the CPU")
                : onGpu ? Lang.T("The GPU didn't work hard for long") : Lang.T("The CPU didn't work hard for long")));
            // Starts only for a card whose fans stood still in the period (one that never stops has none to count); counted
            // as the Today card counts them, so the two say the same number.
            if (card.IsGpu && speed.Values.Any(v => v < FanCard.MinSpinRpm))
            {
                int starts = FanLevels.Starts(speed.Select(x => (x.Key, x.Value))).Count;
                int daysOn = speed.Keys.Select(t => TimeUtil.FromUnix(t).Date).Distinct().Count();
                more.Add(new(Lang.T("Started up"), starts == 1 ? Lang.T("1 time") : Lang.T($"{starts} times"),
                    daysOn > 1 && Math.Round(starts / (double)daysOn) >= 1 ? Lang.T($"About {Math.Round(starts / (double)daysOn):0} a day, each past its silent point") : Lang.T("Each time the GPU warmed past its silent point")));
            }
            else more.Add(usualFact);
        }

        // Headings in the page's caption style (capitals).
        string firstLabel = card.IsGpu && follows == FanFollows.Gpu && silent is not null ? Lang.T("Starts spinning") : Lang.T("Follows");
        return new FanDetail(followsValue, followsNote, still, stillNote, thirdLabel, thirdValue, thirdNote,
            onGpu ? Lang.T("SPEED AT EACH GPU TEMPERATURE") : Lang.T("SPEED AT EACH CPU TEMPERATURE"), dots, tempBins, line, lineLabel, silent,
            card.IsGroup ? Lang.T("WHAT MAKES THEM SPIN") : Lang.T("WHAT MAKES IT SPIN"), rows, "", steady, bars)
        {
            Facts = [new(firstLabel, followsValue, followsNote), new(Lang.T("Stood still"), still, stillNote), new(thirdLabel, thirdValue, thirdNote), .. more],
            FollowsLabel = firstLabel,
            AverageRpm = turning.Count > 0 ? speed.Values.Average() : null,
            FastestRpm = facts?.Fastest?.Rpm,
            FastestWhen = card.FastestLine,
            SettingNote = changed > from ? Lang.T($"Its speed setting changed on {changed:ddd d MMM}. These are its speeds since then.") : "",
        };
    }

    /// <summary>
    /// A year's detail, from each day's totals (fan_day) and curves (fan_curve_day), never a year of minutes: its average
    /// and fastest, its speed each month, its speed at each temperature in games, the apps that made it spin, and (a fan on
    /// a curve) its speed at one temperature month by month, where a slower fan shows as the bars falling.
    /// </summary>
    internal static FanDetail YearOf(FanCard card, List<FanCurveDay> curveDays, List<FanDay> days, IReadOnlyDictionary<long, AppRow> apps,
        Core.Settings.RigsightSettings s, DateTime from, DateTime to)
    {
        var follows = card.Facts?.Follows ?? FanFollows.Unknown;
        bool steady = follows == FanFollows.Steady, onGpu = card.IsGpu || follows == FanFollows.Gpu;

        int n = days.Sum(d => d.RpmN);
        double? average = n > 0 ? days.Sum(d => d.RpmSum) / n : null;
        var top = days.Count > 0 ? days.MaxBy(d => d.RpmMax) : null;

        // The curve under load: each day's steps, gathered (half-degree dots are too fine for daily averages: a degree).
        var bins = curveDays.GroupBy(c => c.Temp).Select(g => (Temp: g.Key, N: g.Sum(c => c.N), Rpm: g.Sum(c => c.RpmSum) / g.Sum(c => c.N))).ToList();
        var dots = curveDays.GroupBy(c => (T: c.Temp + FanCurves.BinDegrees / 2.0, R: Math.Round(c.RpmSum / c.N / 25) * 25))
            .Select(g => new Controls.FanDot(g.Key.T, g.Key.R, g.Sum(c => c.N))).ToList();
        var tempBins = bins.OrderBy(b => b.Temp).Select(b => new Controls.FanTempBin(b.Temp, b.Rpm, b.N, 0)).ToList();

        // What made it spin: the apps working its chip, by its speed with them (an hour of it at least).
        var candidates = curveDays.GroupBy(c => c.App).Where(g => g.Sum(c => c.N) >= 60 && apps.ContainsKey(g.Key))
            .Select(g => (Name: NameOf(apps[g.Key], s), Rpm: g.Sum(c => c.RpmSum) / g.Sum(c => c.N))).ToList();
        var ordered = steady ? [] : candidates.OrderByDescending(a => a.Rpm).Take(5).ToList();
        double topRpm = ordered.Select(a => a.Rpm).DefaultIfEmpty(1).Max();
        var rows = ordered.Select(a => new FanAppRow(a.Name, Units.Format(SensorKind.Fan, a.Rpm), topRpm > 0 ? 100 * a.Rpm / topRpm : 0, a.Rpm < topRpm * 0.25)).ToList();

        // Each month: its average and fastest.
        var months = Enumerable.Range(0, 12).Select(i => from.AddMonths(i)).Where(m => m < to).ToList();
        var bars = months.Select(m =>
        {
            var ofMonth = days.Where(d => TimeUtil.FromUnix(d.Day).Date >= m && TimeUtil.FromUnix(d.Day).Date < m.AddMonths(1)).ToList();
            int k = ofMonth.Sum(d => d.RpmN);
            return new Controls.FanDayBar(m, k > 0 ? ofMonth.Sum(d => d.RpmSum) / k : null, ofMonth.Count > 0 ? ofMonth.Max(d => d.RpmMax) : null);
        }).ToList();

        // The trend: its speed at the temperature it spent most time at (and a step either side), month by month.
        var trend = new List<Controls.FanDayBar>();
        string trendTitle = "", trendLabel = "";
        if (!steady && bins.Count > 0)
        {
            int at = bins.MaxBy(b => b.N).Temp;
            var near = curveDays.Where(c => Math.Abs(c.Temp - at) <= FanCurves.BinDegrees).ToList();
            trend = [.. months.Select(m =>
            {
                var ofMonth = near.Where(c => TimeUtil.FromUnix(c.Day).Date >= m && TimeUtil.FromUnix(c.Day).Date < m.AddMonths(1)).ToList();
                int k = ofMonth.Sum(c => c.N);
                return new Controls.FanDayBar(m, k >= 30 ? ofMonth.Sum(c => c.RpmSum) / k : null, null);
            })];
            string temp = Units.TempShort(at + FanCurves.BinDegrees / 2.0);
            trendTitle = Lang.T($"SPEED AT {temp} EACH MONTH");
            trendLabel = Lang.T($"At {temp}");
        }

        var (followsValue, followsNote) = follows switch
        {
            FanFollows.Gpu => (Lang.T("Your GPU's heat"), Lang.T("Faster as it warms")),
            FanFollows.Cpu => (Lang.T("Your CPU's heat"), Lang.T("Faster as it warms")),
            FanFollows.Steady => (Lang.T("A set speed"), average is double a ? Lang.T($"About {Units.Format(SensorKind.Fan, a)}, whatever the heat") : Lang.T("Whatever the heat")),
            FanFollows.Varies => (Lang.T("Its own curve"), Lang.T("Not clearly with your CPU or GPU")),
            _ => (Lang.T("Not known yet"), Lang.T("Needs an hour of turning")),
        };
        string fastestWhen = top is null ? "" : Lang.T($"On {TimeUtil.FromUnix(top.Day):ddd d MMM}");

        // Three more for a year: its average speed in games (the temperature it spent most time at), the days it has, and
        // how its speed at the same heat (a set speed: its speed) changed from the first month with data to the last.
        string Rpm(double? v) => Units.Format(SensorKind.Fan, v);
        var busiest = bins.Count > 0 ? bins.MaxBy(b => b.N) : default;
        var change = (steady ? bars : trend).Where(b => b.Average is not null).ToList();
        string changeValue = "—", changeNote = Lang.T("Needs two months of it");
        if (change.Count >= 2)
        {
            double pct = 100 * (change[^1].Average!.Value / change[0].Average!.Value - 1);
            changeValue = Math.Abs(pct) < 1 ? Lang.T("No change") : $"{(pct > 0 ? "+" : "−")}{Math.Abs(pct):0}%";
            changeNote = steady ? Lang.T($"Its speed, {change[0].Day:MMM} to {change[^1].Day:MMM}") : Lang.T($"At the same heat, {change[0].Day:MMM} to {change[^1].Day:MMM}");
        }
        var facts = new List<FanFact>
        {
            new(Lang.T("Follows"), followsValue, followsNote),
            new(Lang.T("Average"), average is double av2 ? Rpm(av2) : "—", Lang.T("Across every day of the 12 months")),
            new(Lang.T("Fastest"), top is null ? "—" : Rpm(top.RpmMax), fastestWhen),
            new(Lang.T("Under heavy load"), busiest.N > 0 ? Rpm(busiest.Rpm) : "—",
                busiest.N > 0 ? Lang.T($"Average at {Units.TempShort(busiest.Temp + FanCurves.BinDegrees / 2.0)}, where it spent most time") : Lang.T("No steady load")),
            new(Lang.T("Days recorded"), $"{days.Select(d => d.Day).Distinct().Count()}", Lang.T("Days with this fan's speeds")),
            new(Lang.T("Change over the 12 months"), changeValue, changeNote),
        };
        return new FanDetail(followsValue, followsNote, average is double av ? Units.Format(SensorKind.Fan, av) : "—", Lang.T("Across every day of the 12 months"),
            Lang.T("Fastest"), top is null ? "—" : Units.Format(SensorKind.Fan, top.RpmMax), fastestWhen,
            onGpu ? Lang.T("SPEED AT EACH GPU TEMPERATURE · IN GAMES") : Lang.T("SPEED AT EACH CPU TEMPERATURE · IN GAMES"), dots, tempBins, [], "", null,
            card.IsGroup ? Lang.T("WHAT MADE THEM SPIN") : Lang.T("WHAT MADE IT SPIN"), rows, "", steady, bars)
        {
            StillLabel = Lang.T("Average"),
            Facts = facts,
            Monthly = true,
            Trend = trend,
            TrendTitle = trendTitle,
            TrendLabel = trendLabel,
            AverageRpm = average,
            FastestRpm = top?.RpmMax,
            FastestWhen = fastestWhen,
        };
    }



    /// <param name="listen">Off for a page that's never shown: a card that followed its fans' names would live as long as they do.</param>
    private static FanCard NewCard(IReadOnlyList<SensorItem> fans, bool listen)
    {
        var card = new FanCard(fans);
        if (!listen) return card;
        foreach (var x in fans)
            x.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SensorItem.DisplayName)) card.Renamed(); };
        return card;
    }

    private static string NameOf(AppRow app, Core.Settings.RigsightSettings s) =>
        s.AppNames.TryGetValue(app.Exe, out var alias) ? alias : app.Name;

    private static Core.Settings.AppCategory CategoryOf(AppRow app, Core.Settings.RigsightSettings s) =>
        s.AppCategories.TryGetValue(app.Exe, out var c) ? c : app.Category;
}

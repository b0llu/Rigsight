using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>How a fan is doing right now, in a word or two, and whether that needs a look.</summary>
public enum FanState { NoReading, Spinning, Resting, Stopped, StoppedHot }

/// <summary>One of the six facts on a fan's details: a heading, a value, and a line under it.</summary>
public sealed record FanFact(string Label, string Value, string Note);

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

    public bool HasAppsNote => AppsNote.Length > 0;

    /// <summary>The six facts shown (two rows of three): the three above first, then three more for the kind of fan.</summary>
    public IReadOnlyList<FanFact> Facts { get; init; } = [];

    /// <summary>The first fact's heading: "Follows", or for a graphics card that stands still, "Starts spinning".</summary>
    public string FollowsLabel { get; init; } = "Follows";

    /// <summary>The second fact's heading ("Stood still", or "Average" for a year, which is kept by the day).</summary>
    public string StillLabel { get; init; } = "Stood still";

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

    /// <summary>A fan turns from here: real fans don't go slower than about 200 rpm, and a header with nothing on it can
    /// read a stray few rpm.</summary>
    internal const double MinSpinRpm = 100;

    // None green, amber, orange or red: those say how a reading is doing, not which fan a line is.
    private static readonly string[] Colors = ["GpuColor", "PurpleColor", "PinkColor", "CoolColor"];

    public FanCard(IReadOnlyList<SensorItem> sensors)
    {
        Sensors = sensors;
        Sensor = sensors[0];
        IsGpu = Sensor.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);
        Place = IsGpu ? "Graphics card" : Sensor.HardwareType == "Cooler" ? Sensor.HardwareName : "Motherboard";
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
    public string Title => IsGroup ? "GPU fans" : Sensor.DisplayName;

    /// <summary>Under the name in the list: a warning first, then how many fans a card's row stands for, or the state.</summary>
    public string Note => HasAlert ? "Needs a look"
        : IsGroup ? $"{Sensors.Count} fans · {StateText.ToLowerInvariant()}"
        : Facts?.Follows == FanFollows.Steady && State == FanState.Spinning ? "Steady speed" : StateText;

    /// <summary>The speed now: a card's fans averaged (they turn together), one fan's as it reads.</summary>
    public double? Value => (IsGroup
        ? Sensors.Where(x => x.Value is not null).Select(x => x.Value!.Value).DefaultIfEmpty(double.NaN).Average() is double v && !double.IsNaN(v) ? v : null
        : Sensor.Value) is double rpm ? (rpm < MinSpinRpm ? 0 : rpm) : null;

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
    [NotifyPropertyChangedFor(nameof(HasAlert), nameof(Note), nameof(NeedsLook))]
    private string _alert = "";

    [ObservableProperty] private bool _alertIsHot;

    public bool HasAlert => Alert.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(NeedsLook), nameof(Note))]
    private FanState _state = FanState.NoReading;

    public string StateText => State switch
    {
        FanState.Spinning => "Spinning",
        FanState.Resting => "Resting while cool",
        FanState.Stopped => "Stopped",
        FanState.StoppedHot => "Stopped while hot",
        _ => "No reading",
    };

    /// <summary>Standing still while the chip it cools is hot, or today's insights warn about it: worth checking the fan.</summary>
    public bool NeedsLook => State == FanState.StoppedHot || HasAlert;

    public string FollowsText => Facts?.Follows switch
    {
        FanFollows.Gpu => "Speeds up with your GPU",
        FanFollows.Cpu => "Speeds up with your CPU",
        FanFollows.Steady => "Runs at a steady speed",
        FanFollows.Varies => "Changes speed, not clearly with your CPU or GPU",
        _ => "",
    };

    public string StoppedText => Facts is { StoppedMinutes: > 0 } f
        ? $"{(IsGpu ? "Rested" : "Stood still")} for {Units.Duration(f.StoppedMinutes * 60)} today"
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
    internal const int SpunWithinDays = 90;

    /// <summary>The days a fan's usual curve (the line on its temperature chart) is drawn from, before today.</summary>
    internal const int UsualDays = 30;

    /// <summary>An app makes the "what makes it spin" list from this many minutes working the fan's chip today.</summary>
    internal const int AppMinutes = 10;

    // ── The period the page shows (Day, Week or Month; today unless picked otherwise, like Home) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(IsYear), nameof(RangeNote), nameof(PeriodCaption), nameof(Days), nameof(DayOfChart))]
    private ReportRange _unit = ReportRange.Day;

    /// <summary>Any day in the period.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(PeriodCaption), nameof(Days), nameof(DayOfChart))]
    private DateTime _anchor = DateTime.Today;

    /// <summary>The first day with fan speeds recorded (the picker goes no further back).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote))]
    private DateTime? _firstDay;

    /// <summary>The picker's custom range (not offered here; bound so the picker has somewhere to put it).</summary>
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    private (DateTime From, DateTime To) Period => ReportBuilder.Bounds(Unit, Anchor);
    public bool IsDay => Unit == ReportRange.Day;

    /// <summary>A year: read from each day's totals and curves (a year of minutes would be too many), so a trend.</summary>
    public bool IsYear => Unit == ReportRange.Year;
    public bool IncludesToday => Period.To > DateTime.Today;
    public string RangeNote => Controls.PeriodPicker.Span(Unit, Anchor, FirstDay);

    /// <summary>The period as a caption ("TODAY", "THIS WEEK", "MON, 29 SEP").</summary>
    public string PeriodCaption => Controls.PeriodPicker.Text(Unit, Anchor).ToUpperInvariant();

    /// <summary>The day the speed chart shows (a Day period).</summary>
    public DateTime DayOfChart => Period.From;

    /// <summary>The days in the period, for the week's and month's speed chart.</summary>
    public int Days => (int)Math.Round((Period.To - Period.From).TotalDays);

    partial void OnUnitChanged(ReportRange value) => _ = RefreshAsync();
    partial void OnAnchorChanged(DateTime value) => _ = RefreshAsync();

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

    public FansViewModel(ReportService reports, LiveData live, SettingsModel settings)
    {
        _reports = reports;
        _settings = settings;
        Live = live;
        live.PropertyChanged += OnLiveChanged;
        // New hardware (the agent reconnected): the rows again, once the page has been opened.
        live.SensorsRebuilt += () => { if (Loaded) _ = RefreshAsync(); };
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
        1 => "1 fan header free",
        var n => $"{n} fan headers free",
    };

    [ObservableProperty] private bool _loaded;
    public bool HasFans => Fans.Count > 0;

    // The row of answers at the top.

    /// <summary>"All OK", or how many fans need a look; and how many fans (and free headers) there are.</summary>
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusOk = true;
    [ObservableProperty] private string _statusNote = "";

    /// <summary>How long a graphics card's fans stood still (a year: their average speed, as days are kept).</summary>
    [ObservableProperty] private string _quietLabel = "QUIET TIME";
    [ObservableProperty] private string _quietText = "—";
    [ObservableProperty] private string _quietNote = "";

    /// <summary>The app that made a fan on a curve spin hardest, and how fast (always shown: "Nothing yet" on a quiet day).</summary>
    [ObservableProperty] private string _hardestApp = "—";
    [ObservableProperty] private string _hardestNote = "";

    /// <summary>The fastest any fan spun, and what was going on.</summary>
    [ObservableProperty] private string _fastestText = "—";
    [ObservableProperty] private string _fastestNote = "";

    // The "Today" card under the list.

    /// <summary>The hour (a day) or the day (a week, a month) the fans spun fastest together, and what was going on.</summary>
    [ObservableProperty] private string? _loudestHour;
    [ObservableProperty] private string _loudestNote = "";
    [ObservableProperty] private string _loudestLabel = "Loudest hour";

    /// <summary>How many times a graphics card's fans started up (zero-rpm fans starting and stopping).</summary>
    [ObservableProperty] private string? _startsText;
    [ObservableProperty] private string _startsNote = "";

    /// <summary>A graphics card's fans' longest stretch standing still today, and when.</summary>
    [ObservableProperty] private string? _silentText;
    [ObservableProperty] private string _silentNote = "";

    public bool HasToday => LoudestHour is not null || StartsText is not null || SilentText is not null;

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
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
        if (id != _load) return;
        FirstDay = first ?? today;

        var bySensor = history.Fans.ToDictionary(f => f.Sensor, f => f.Id);
        var spun = history.Days.Where(d => d.RpmMax >= FanCard.MinSpinRpm).Select(d => d.Fan).ToHashSet();
        var minutes = history.System.ToDictionary(m => m.Ts);
        var byFan = history.Minutes.GroupBy(m => m.Fan).ToDictionary(g => g.Key, g => g.ToList());
        var apps = history.Apps.ToDictionary(a => a.Id);
        var s = _settings.Current;
        List<FanMinute> MinutesOf(SensorItem x) => bySensor.TryGetValue(x.Id, out var f) && byFan.TryGetValue(f, out var list) ? list : [];

        // A graphics card's fans always show, as one row per card (they stand still for days on a light load); a board
        // or cooler header only once it has spun, now, today or in the recorded days.
        var sensors = Live.AllSensors.Where(x => x.Kind == SensorKind.Fan).ToList();
        bool IsGpu(SensorItem x) => x.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);
        bool HasSpun(SensorItem x) => x.Value >= FanCard.MinSpinRpm || x.Max >= FanCard.MinSpinRpm || bySensor.TryGetValue(x.Id, out var f) && spun.Contains(f);
        var rows = new List<IReadOnlyList<SensorItem>>();
        rows.AddRange(sensors.Where(IsGpu).GroupBy(x => x.HardwareName).Select(g => (IReadOnlyList<SensorItem>)[.. g]));
        var others = sensors.Where(x => !IsGpu(x)).ToList();
        rows.AddRange(others.Where(x => x.HardwareType == "Cooler" && HasSpun(x)).Select(x => (IReadOnlyList<SensorItem>)[x]));
        rows.AddRange(others.Where(x => x.HardwareType != "Cooler" && HasSpun(x)).Select(x => (IReadOnlyList<SensorItem>)[x]));
        int empty = others.Count(x => !HasSpun(x));

        var shown = new List<FanCard>();
        foreach (var fans in rows)
        {
            var card = Fans.FirstOrDefault(c => c.Sensors.SequenceEqual(fans)) ?? NewCard(fans);
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
            // The insights name a card's fans "your GPU fan(s)", a board's by the name its chip gives it.
            var warning = warnings.FirstOrDefault(w => card.IsGpu
                ? w.Text.StartsWith("Your GPU fan", StringComparison.Ordinal)
                : fans.Any(x => w.Text.StartsWith(x.Name + " on your motherboard", StringComparison.Ordinal)));
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
        var chips = shown.Where(c => c.Place.StartsWith("Motherboard", StringComparison.Ordinal)).Select(c => c.Sensor.HardwareName).Distinct().ToList();
        bool regroup = false;
        foreach (var c in shown.Where(c => c.Place.StartsWith("Motherboard", StringComparison.Ordinal)))
        {
            string place = chips.IndexOf(c.Sensor.HardwareName) is > 0 and var i ? $"Motherboard (chip {i + 1})" : "Motherboard";
            regroup |= c.Place != place;
            c.Place = place;
        }

        var picked = Selected; // the list clears its pick while its rows are put back
        if (regroup || !shown.SequenceEqual(Fans))
        {
            Fans.Clear();
            foreach (var c in shown) Fans.Add(c);
            OnPropertyChanged(nameof(HasFans));
        }
        EmptyHeaders = empty;
        Summarise();
        if (IsYear) SummariseYear(shown.SelectMany(c => c.Sensors).Select(x => bySensor.TryGetValue(x.Id, out var f) ? f : -1).ToHashSet(), history.Days);
        else SummariseToday(shown, MinutesOf, minutes, apps, s);
        Selected = picked is not null && Fans.Contains(picked) ? picked : Fans.FirstOrDefault();
        OnPropertyChanged(nameof(ChartSeries));
        _loadedPast = IncludesToday ? null : (Unit, from);
        Loaded = true;
    }

    /// <summary>The row of answers at the top, from the rows as they now stand.</summary>
    private void Summarise()
    {
        int needs = Fans.Count(f => f.NeedsLook), total = Fans.Sum(f => f.Sensors.Count);
        StatusOk = needs == 0;
        StatusText = needs == 0 ? "All OK" : needs == 1 ? "1 needs a look" : $"{needs} need a look";
        StatusNote = $"{total} fan{(total == 1 ? "" : "s")}" + (EmptyHeaders > 0 ? $" · {EmptyHeadersText}" : "");

        var card = Fans.FirstOrDefault(f => f.IsGpu);
        if (IsYear)
        {
            // Kept by the day: how fast the card's fans ran on average instead of how long they stood still.
            QuietLabel = "AVERAGE SPEED";
            QuietText = card?.Detail?.AverageRpm is double avg ? Units.Format(SensorKind.Fan, avg) : "—";
            QuietNote = card is null ? "" : "GPU fans, across the year's days";
        }
        else
        {
            QuietLabel = "QUIET TIME";
            QuietText = card?.Facts is { StoppedMinutes: > 0 } q ? Units.Duration(q.StoppedMinutes * 60) : card is null ? "—" : "None";
            QuietNote = card is null ? "No graphics card fans reported" : card.Facts is { StoppedMinutes: > 0 } ? "GPU fans stood still while the card was cool" : "GPU fans turned the whole time";
        }

        // The app that made a fan on a curve work hardest: the card's first, else any that follows a chip.
        var curve = Fans.Where(f => f.IsGpu || f.Facts?.Follows is FanFollows.Gpu or FanFollows.Cpu).OrderByDescending(f => f.IsGpu).FirstOrDefault();
        var top = curve?.Detail?.Apps.FirstOrDefault(a => !a.Dim);
        HardestApp = top?.Name ?? "Nothing yet";
        HardestNote = top is not null ? $"{(curve!.IsGroup ? "GPU fans" : curve.Title)} at {top.RpmText} on average"
            : IsDay && IncludesToday ? "No game or app has warmed them up for long today" : "Nothing warmed them up for long";

        var fastest = Fans.Where(f => f.Detail?.FastestRpm is not null).MaxBy(f => f.Detail!.FastestRpm);
        FastestText = fastest is null ? "—" : Units.Format(SensorKind.Fan, fastest.Detail!.FastestRpm);
        FastestNote = fastest is null ? "" : fastest.Detail!.FastestWhen.Length > 0 ? $"{fastest.Title} · {fastest.Detail.FastestWhen}" : fastest.Title;
    }

    /// <summary>
    /// The "Today" card: the hour the fans spun fastest together (and what was going on), and for a graphics card how many
    /// times its fans started up and their longest stretch standing still.
    /// </summary>
    private void SummariseToday(List<FanCard> rows, Func<SensorItem, List<FanMinute>> minutesOf, IReadOnlyDictionary<long, SystemMinute> minutes,
        IReadOnlyDictionary<long, AppRow> apps, Core.Settings.RigsightSettings s)
    {
        // All the fans together each minute, then each hour's (a day) or day's (a week, a month) average: an hour needs a
        // third of its minutes, a day an hour of them.
        var total = rows.SelectMany(r => r.Sensors).SelectMany(minutesOf).GroupBy(f => f.Ts).ToDictionary(g => g.Key, g => g.Sum(f => (double)f.RpmAvg));
        DateTime Slot(long ts) => IsDay ? TimeUtil.FromUnix(ts).Date.AddHours(TimeUtil.FromUnix(ts).Hour) : TimeUtil.FromUnix(ts).Date;
        var loudest = total.GroupBy(x => Slot(x.Key)).Where(g => g.Count() >= (IsDay ? 20 : 60))
            .Select(g => (Slot: g.Key, Rpm: g.Average(x => x.Value))).OrderByDescending(h => h.Rpm).FirstOrDefault();
        LoudestLabel = IsDay ? "Loudest hour" : "Loudest day";
        if (loudest.Slot == default) (LoudestHour, LoudestNote) = (null, "");
        else
        {
            LoudestHour = IsDay ? $"{loudest.Slot:h tt} – {loudest.Slot.AddHours(1):h tt}" : loudest.Slot.ToString("dddd d MMM");
            // What was in front most of it, in the peaks' words ("While playing Rematch").
            var front = total.Keys.Where(t => Slot(t) == loudest.Slot && minutes.TryGetValue(t, out var m) && m.FgApp is not null)
                .GroupBy(t => minutes[t].FgApp!.Value).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            LoudestNote = front is long f && apps.TryGetValue(f, out var app) ? PeakWords.Line(1, NameOf(app, s), null, CategoryOf(app, s)) ?? "" : "";
        }

        // A graphics card's fans: starts, and the longest stretch still.
        var card = rows.FirstOrDefault(r => r.IsGpu);
        var speed = card is null ? [] : card.Sensors.SelectMany(minutesOf)
            .GroupBy(f => f.Ts).OrderBy(g => g.Key).Select(g => (Ts: g.Key, Rpm: g.Average(f => (double)f.RpmAvg))).ToList();
        int starts = 0, run = 0, best = 0;
        long runFrom = 0, bestFrom = 0, previous = 0;
        bool wasStill = false;
        foreach (var (ts, rpm) in speed)
        {
            bool still = rpm < FanCard.MinSpinRpm, joined = ts == previous + 60;
            if (joined && wasStill && !still) starts++;
            if (still)
            {
                if (!joined || !wasStill) (run, runFrom) = (0, ts);
                run++;
                if (run > best) (best, bestFrom) = (run, runFrom);
            }
            (wasStill, previous) = (still, ts);
        }
        StartsText = card is null || speed.Count == 0 ? null : starts == 1 ? "1 time" : $"{starts} times";
        int daysOn = speed.Select(x => TimeUtil.FromUnix(x.Ts).Date).Distinct().Count();
        // A rate only when there's one to give: twice in six days isn't "about 0 a day".
        StartsNote = !IsDay && daysOn > 1 && Math.Round(starts / (double)daysOn) >= 1 ? $"About {Math.Round(starts / (double)daysOn):0} a day, each time the card warmed past its silent point"
            : "Each time the card warmed past its silent point";
        SilentText = best >= 5 ? Units.Duration(best * 60) : null;
        SilentNote = best < 5 ? ""
            : IsDay ? $"{TimeUtil.FromUnix(bestFrom):h:mm tt} – {TimeUtil.FromUnix(bestFrom + best * 60):h:mm tt}"
            : $"{TimeUtil.FromUnix(bestFrom):ddd d MMM, h:mm tt} – {TimeUtil.FromUnix(bestFrom + best * 60):h:mm tt}";
        OnPropertyChanged(nameof(HasToday));
    }

    /// <summary>A year's card, from each day's totals: the month the fans spun fastest together (starts and silent stretches need minutes).</summary>
    private void SummariseYear(HashSet<long> fans, List<FanDay> days)
    {
        var (from, to) = Period;
        var loudest = days.Where(d => fans.Contains(d.Fan) && d.RpmN > 0 && TimeUtil.FromUnix(d.Day) >= from && TimeUtil.FromUnix(d.Day) < to)
            .GroupBy(d => new DateTime(TimeUtil.FromUnix(d.Day).Year, TimeUtil.FromUnix(d.Day).Month, 1))
            .Select(g => (Month: g.Key, Rpm: g.GroupBy(d => d.Fan).Sum(f => f.Sum(d => d.RpmSum) / f.Sum(d => d.RpmN))))
            .OrderByDescending(m => m.Rpm).FirstOrDefault();
        LoudestLabel = "Loudest month";
        LoudestHour = loudest.Month == default ? null : loudest.Month.ToString("MMMM yyyy");
        LoudestNote = "";
        (StartsText, StartsNote, SilentText, SilentNote) = (null, "", null, "");
        OnPropertyChanged(nameof(HasToday));
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
        string chip = onGpu ? "GPU" : "CPU";

        // The row's speed each minute of the period (a card's fans averaged), with the temperature of the chip it follows.
        var speed = fans.SelectMany(f => f).GroupBy(f => f.Ts).ToDictionary(g => g.Key, g => g.Average(f => (double)f.RpmAvg));
        var points = new List<System.Windows.Point>();
        foreach (var (ts, rpm) in speed)
            if (minutes.TryGetValue(ts, out var m) && (onGpu ? m.GpuTemp : m.CpuTemp) is double t)
                points.Add(new System.Windows.Point(t, rpm));
        // Gathered into dots (half a degree, 25 rpm) and a typical speed per degree for the hover.
        var dots = points.GroupBy(p => (T: Math.Round(p.X * 2) / 2, R: Math.Round(p.Y / 25) * 25))
            .Select(g => new Controls.FanDot(g.Key.T, g.Key.R, g.Count())).ToList();
        var tempBins = points.GroupBy(p => (int)Math.Floor(p.X)).Select(g =>
        {
            var turningHere = g.Where(p => p.Y >= FanCard.MinSpinRpm).Select(p => p.Y).Order().ToList();
            return new Controls.FanTempBin(g.Key, turningHere.Count > 0 ? turningHere[turningHere.Count / 2] : 0, g.Count(), 1 - turningHere.Count / (double)g.Count());
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
            lineLabel = "same speed at every temperature";
        }
        else if (!steady)
        {
            line = [.. curveDays.GroupBy(c => c.Temp)
                .Select(g => (T: g.Key + FanCurves.BinDegrees / 2.0, N: g.Sum(c => c.N), Rpm: g.Sum(c => c.RpmSum) / g.Sum(c => c.N)))
                .Where(b => b.N >= 5).OrderBy(b => b.T).Select(b => new System.Windows.Point(b.T, b.Rpm))];
            if (line.Count < 2) line = [];
            else lineLabel = "usual curve";
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

        string still = facts is { StoppedMinutes: > 0 } f ? Units.Duration(f.StoppedMinutes * 60) : "Never";
        string stillNote = facts is { StoppedMinutes: > 0 } && card.MinutesOn > 0 ? $"of {Units.Duration(card.MinutesOn * 60)} on" : "Turning all the time the PC was on";
        var (followsValue, followsNote) = follows switch
        {
            // A card's own fans: that they follow its heat goes without saying; where they start is what's worth knowing.
            FanFollows.Gpu when card.IsGpu && silent is double sb => ($"Around {Units.TempShort(sb)}", "Silent below that, faster as it warms"),
            FanFollows.Gpu => ("Your GPU's heat", "Faster as it warms"),
            FanFollows.Cpu => ("Your CPU's heat", "Faster as it warms"),
            FanFollows.Steady => ("A set speed", $"About {Units.Format(SensorKind.Fan, turning.DefaultIfEmpty().Average())}, whatever the heat"),
            FanFollows.Varies => ("Its own curve", "Not clearly with your CPU or GPU"),
            _ => ("Learning", "Needs an hour of turning"),
        };
        var (thirdLabel, thirdValue, thirdNote) = steady && turning.Count > 0
            ? ("Steadiness", $"Within {Math.Round(100 * (turning.Max() - turning.Min()) / turning.Average()):0}%", "From its slowest to its fastest")
            : ("Fastest", card.FastestText, card.FastestLine);

        // Three more facts. Every fan: its average while turning. A fan on a curve: its typical speed while its chip works
        // hard, and (a card's zero-rpm fans) how often they started up; a set speed: its range, and its usual speed the 30
        // days before (a slower fan shows there first).
        string Rpm(double? v) => Units.Format(SensorKind.Fan, v);
        var more = new List<FanFact>
        {
            new("Average while spinning", turning.Count > 0 ? Rpm(turning.Average()) : "—",
                turning.Count > 0 ? $"Over {Units.Duration(turning.Count * 60)} of turning" : "It didn't turn"),
        };
        // Usual: under the setting it's on. Days of an earlier one aren't its usual any more.
        bool newSetting = changed > from.AddDays(-UsualDays);
        var usual = days.Where(d => TimeUtil.FromUnix(d.Day).Date >= from.AddDays(-UsualDays) && TimeUtil.FromUnix(d.Day).Date < from
            && !(TimeUtil.FromUnix(d.Day).Date < changed)).ToList();
        int usualN = usual.Sum(d => d.RpmN);
        var usualFact = new FanFact("Usual", usualN > 0 ? Rpm(usual.Sum(d => d.RpmSum) / usualN) : "—",
            newSetting ? $"{(usualN > 0 ? "Since" : "Nothing since")} its setting changed on {changed:d MMM}"
            : usualN > 0 ? $"Over the {UsualDays} days before" : "Nothing recorded before this");
        if (steady)
        {
            more.Add(new("Range", turning.Count > 0 ? $"{turning.Min():N0} – {turning.Max():N0}" : "—", "Slowest to fastest while spinning, RPM"));
            more.Add(usualFact);
        }
        else
        {
            var hard = speed.Where(x => minutes.TryGetValue(x.Key, out var m)
                    && (onGpu ? m.GpuLoad >= LoadBands.GpuHeavyLoad : m.CpuLoad >= LoadBands.CpuHeavyLoad) && x.Value >= FanCard.MinSpinRpm)
                .Select(x => x.Value).Order().ToList();
            more.Add(new("Under heavy load", hard.Count >= 5 ? Rpm(hard[hard.Count / 2]) : "—",
                hard.Count >= 5 ? $"Typical while {(onGpu ? "games pushed the GPU" : "work pushed the CPU")}" : $"The {chip} didn't work hard for long"));
            if (card.IsGpu && speed.Count > 0)
            {
                int starts = 0;
                long previous = 0;
                bool wasStill = false;
                foreach (var (ts, rpm) in speed.OrderBy(x => x.Key))
                {
                    bool isStill = rpm < FanCard.MinSpinRpm;
                    if (ts == previous + 60 && wasStill && !isStill) starts++;
                    (wasStill, previous) = (isStill, ts);
                }
                int daysOn = speed.Keys.Select(t => TimeUtil.FromUnix(t).Date).Distinct().Count();
                more.Add(new("Started up", starts == 1 ? "1 time" : $"{starts} times",
                    daysOn > 1 && Math.Round(starts / (double)daysOn) >= 1 ? $"About {Math.Round(starts / (double)daysOn):0} a day, each past its silent point" : "Each time the card warmed past its silent point"));
            }
            else more.Add(usualFact);
        }

        // Headings in the page's caption style (capitals).
        string firstLabel = card.IsGpu && follows == FanFollows.Gpu && silent is not null ? "Starts spinning" : "Follows";
        return new FanDetail(followsValue, followsNote, still, stillNote, thirdLabel, thirdValue, thirdNote,
            $"SPEED AT EACH {chip} TEMPERATURE", dots, tempBins, line, lineLabel, silent,
            card.IsGroup ? "WHAT MAKES THEM SPIN" : "WHAT MAKES IT SPIN", rows, "", steady, bars)
        {
            Facts = [new(firstLabel, followsValue, followsNote), new("Stood still", still, stillNote), new(thirdLabel, thirdValue, thirdNote), .. more],
            FollowsLabel = firstLabel,
            AverageRpm = turning.Count > 0 ? speed.Values.Average() : null,
            FastestRpm = facts?.Fastest?.Rpm,
            FastestWhen = card.FastestLine,
            SettingNote = changed > from ? $"Its speed setting changed on {changed:ddd d MMM}. These are its speeds since then." : "",
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
        string chip = onGpu ? "GPU" : "CPU";

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
            trendTitle = $"SPEED AT {temp} EACH MONTH";
            trendLabel = $"At {temp}";
        }

        var (followsValue, followsNote) = follows switch
        {
            FanFollows.Gpu => ("Your GPU's heat", "Faster as it warms"),
            FanFollows.Cpu => ("Your CPU's heat", "Faster as it warms"),
            FanFollows.Steady => ("A set speed", average is double a ? $"About {Units.Format(SensorKind.Fan, a)}, whatever the heat" : "Whatever the heat"),
            FanFollows.Varies => ("Its own curve", "Not clearly with your CPU or GPU"),
            _ => ("Learning", "Needs an hour of turning"),
        };
        string fastestWhen = top is null ? "" : $"On {TimeUtil.FromUnix(top.Day):ddd d MMM}";

        // Three more for a year: its typical speed in games (the temperature it spent most time at), the days it has, and
        // how its speed at the same heat (a set speed: its speed) changed from the first month with data to the last.
        string Rpm(double? v) => Units.Format(SensorKind.Fan, v);
        var busiest = bins.Count > 0 ? bins.MaxBy(b => b.N) : default;
        var change = (steady ? bars : trend).Where(b => b.Average is not null).ToList();
        string changeValue = "—", changeNote = "Needs two months of it";
        if (change.Count >= 2)
        {
            double pct = 100 * (change[^1].Average!.Value / change[0].Average!.Value - 1);
            changeValue = Math.Abs(pct) < 1 ? "No change" : $"{(pct > 0 ? "+" : "−")}{Math.Abs(pct):0}%";
            changeNote = $"{(steady ? "Its speed" : "At the same heat")}, {change[0].Day:MMM} to {change[^1].Day:MMM}";
        }
        var facts = new List<FanFact>
        {
            new(steady ? "Follows" : "Follows", followsValue, followsNote),
            new("Average", average is double av2 ? Rpm(av2) : "—", "Across the year's days"),
            new("Fastest", top is null ? "—" : Rpm(top.RpmMax), fastestWhen),
            new("Under heavy load", busiest.N > 0 ? Rpm(busiest.Rpm) : "—",
                busiest.N > 0 ? $"Typical at {Units.TempShort(busiest.Temp + FanCurves.BinDegrees / 2.0)}, where it spent most time" : "No steady load recorded"),
            new("Days recorded", $"{days.Select(d => d.Day).Distinct().Count()}", "Days with this fan's speeds"),
            new("Change over the year", changeValue, changeNote),
        };
        return new FanDetail(followsValue, followsNote, average is double av ? Units.Format(SensorKind.Fan, av) : "—", "Across the year's days",
            "Fastest", top is null ? "—" : Units.Format(SensorKind.Fan, top.RpmMax), fastestWhen,
            $"SPEED AT EACH {chip} TEMPERATURE · IN GAMES", dots, tempBins, [], "", null,
            card.IsGroup ? "WHAT MADE THEM SPIN" : "WHAT MADE IT SPIN", rows, "", steady, bars)
        {
            StillLabel = "Average",
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



    private static FanCard NewCard(IReadOnlyList<SensorItem> fans)
    {
        var card = new FanCard(fans);
        foreach (var x in fans)
            x.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SensorItem.DisplayName)) card.Renamed(); };
        return card;
    }

    private static string NameOf(AppRow app, Core.Settings.RigsightSettings s) =>
        s.AppNames.TryGetValue(app.Exe, out var alias) ? alias : app.Name;

    private static Core.Settings.AppCategory CategoryOf(AppRow app, Core.Settings.RigsightSettings s) =>
        s.AppCategories.TryGetValue(app.Exe, out var c) ? c : app.Category;
}

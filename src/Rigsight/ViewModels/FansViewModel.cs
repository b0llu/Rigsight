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

/// <summary>An app on a fan's "what makes it spin": the fan's average speed while that app worked the chip it follows.</summary>
public sealed record FanAppRow(string Name, string RpmText, double Share, bool Dim);

/// <summary>
/// What the Fans page shows for a row beyond its live reading, from today's minutes (and the month's days): three facts,
/// its speed at each temperature against its usual curve, the apps that make it spin, and its last 30 days.
/// </summary>
public sealed record FanDetail(
    string FollowsValue, string FollowsNote, string StillValue, string StillNote, string ThirdLabel, string ThirdValue, string ThirdNote,
    string CurveTitle, IReadOnlyList<System.Windows.Point> Points, IReadOnlyList<System.Windows.Point> Line, string LineLabel, double? SilentBelow,
    string AppsTitle, IReadOnlyList<FanAppRow> Apps, string AppsNote, bool IsSteady, IReadOnlyList<Controls.FanDayBar> Days)
{
    public bool HasApps => Apps.Count > 0;

    /// <summary>The 30-day chart's heading ("LAST 30 DAYS · GPU FANS"), in the page's caption style.</summary>
    public string DaysTitle { get; init; } = "";
    public bool HasAppsNote => AppsNote.Length > 0;
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

    private static readonly string[] Colors = ["GpuColor", "CoolColor", "PurpleColor", "OrangeColor"];

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

    /// <summary>How long a graphics card's fans stood still today (null: no card fans, or they never did).</summary>
    [ObservableProperty] private string? _quietText;
    [ObservableProperty] private string _quietNote = "";

    /// <summary>The app that made a fan on a curve spin hardest today, and how fast.</summary>
    [ObservableProperty] private string? _hardestApp;
    [ObservableProperty] private string _hardestNote = "";

    /// <summary>The fastest any fan spun today, and what was going on.</summary>
    [ObservableProperty] private string? _fastestText;
    [ObservableProperty] private string _fastestNote = "";

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LiveData.Tick)) return;
        foreach (var f in Fans) f.UpdateState(Live.CpuTemp?.Value, Live.GpuTemp?.Value);
    }

    public async Task RefreshAsync()
    {
        int id = ++_load;
        var today = DateTime.Today;
        var history = await _reports.FanHistoryAsync(today.AddDays(-SpunWithinDays), today);
        if (id != _load || history is null) return;
        // Today's fan warnings, as Home and Reports show them: on the row they're about.
        var warnings = (await _reports.BuildAsync(Core.Reports.ReportRange.Day, today))?.Insights
            .Where(i => i.Key is "fan-stopped" or "fan-slower" or "fan-older").ToList() ?? [];
        if (id != _load) return;

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
            var each = fans.Select(x => FanAnalysis.Of(card.IsGpu, MinutesOf(x), minutes)).ToList();
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
            var ids = fans.Select(x => bySensor.TryGetValue(x.Id, out var f) ? f : -1).Where(f => f >= 0).ToHashSet();
            card.MinutesOn = fans.Max(x => MinutesOf(x).Count);
            card.Detail = DetailOf(card, [.. fans.Select(MinutesOf)], minutes, history.CurveDays.Where(c => ids.Contains(c.Fan)).ToList(),
                history.Days.Where(d => ids.Contains(d.Fan)).ToList(), apps, s, today);
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
        Selected = picked is not null && Fans.Contains(picked) ? picked : Fans.FirstOrDefault();
        OnPropertyChanged(nameof(ChartSeries));
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
        QuietText = card?.Facts is { StoppedMinutes: > 0 } q ? Units.Duration(q.StoppedMinutes * 60) : null;
        QuietNote = "GPU fans stood still while the card was cool";

        // The app that made a fan on a curve work hardest: the card's first, else any that follows a chip.
        var curve = Fans.Where(f => f.Facts?.Follows is FanFollows.Gpu or FanFollows.Cpu).OrderByDescending(f => f.IsGpu).FirstOrDefault();
        var top = curve?.Detail?.Apps.FirstOrDefault(a => !a.Dim);
        HardestApp = top?.Name;
        HardestNote = top is null ? "" : $"{(curve!.IsGroup ? "GPU fans" : curve.Title)} at {top.RpmText} on average";

        var fastest = Fans.Where(f => f.Facts?.Fastest is not null).MaxBy(f => f.Facts!.Fastest!.Rpm);
        FastestText = fastest?.FastestText;
        FastestNote = fastest is null ? "" : fastest.FastestLine.Length > 0 ? $"{fastest.Title} · {fastest.FastestLine}" : fastest.Title;
    }

    /// <summary>A row's detail (see <see cref="FanDetail"/>) from its fans' minutes today and their days before.</summary>
    internal static FanDetail DetailOf(FanCard card, IReadOnlyList<List<FanMinute>> fans, IReadOnlyDictionary<long, SystemMinute> minutes,
        List<FanCurveDay> curveDays, List<FanDay> days, IReadOnlyDictionary<long, AppRow> apps, Core.Settings.RigsightSettings s, DateTime today)
    {
        var facts = card.Facts;
        var follows = facts?.Follows ?? FanFollows.Unknown;
        bool steady = follows == FanFollows.Steady, onGpu = card.IsGpu || follows == FanFollows.Gpu;
        string chip = onGpu ? "GPU" : "CPU";

        // The row's speed each minute (a card's fans averaged), with the temperature of the chip it follows.
        var speed = fans.SelectMany(f => f).GroupBy(f => f.Ts).ToDictionary(g => g.Key, g => g.Average(f => (double)f.RpmAvg));
        var points = new List<System.Windows.Point>();
        foreach (var (ts, rpm) in speed.OrderBy(x => x.Key))
            if (minutes.TryGetValue(ts, out var m) && (onGpu ? m.GpuTemp : m.CpuTemp) is double t)
                points.Add(new System.Windows.Point(t, rpm));

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
        var ordered = (steady ? candidates.OrderByDescending(a => a.N) : candidates.OrderByDescending(a => a.Rpm)).Take(5).ToList();
        double topRpm = ordered.Select(a => a.Rpm).DefaultIfEmpty(1).Max();
        var rows = ordered.Select(a => new FanAppRow(a.Name, Units.Format(SensorKind.Fan, a.Rpm), topRpm > 0 ? 100 * a.Rpm / topRpm : 0,
            !steady && a.Rpm < topRpm * 0.25)).ToList();

        // Its last 30 days: each day's average and fastest (a card's fans together).
        var bars = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).Select(d =>
        {
            var ofDay = days.Where(x => TimeUtil.FromUnix(x.Day).Date == d).ToList();
            int n = ofDay.Sum(x => x.RpmN);
            return new Controls.FanDayBar(d, n > 0 ? ofDay.Sum(x => x.RpmSum) / n : null, ofDay.Count > 0 ? ofDay.Max(x => x.RpmMax) : null);
        }).ToList();

        string still = facts is { StoppedMinutes: > 0 } f ? Units.Duration(f.StoppedMinutes * 60) : "Never";
        string stillNote = facts is { StoppedMinutes: > 0 } && card.MinutesOn > 0 ? $"of {Units.Duration(card.MinutesOn * 60)} on" : "Turning all the time the PC was on";
        var (followsValue, followsNote) = follows switch
        {
            FanFollows.Gpu => ("Your GPU's heat", silent is double sb ? $"Silent below about {Units.TempShort(sb)}" : "Faster as it warms"),
            FanFollows.Cpu => ("Your CPU's heat", "Faster as it warms"),
            FanFollows.Steady => ("A set speed", $"About {Units.Format(SensorKind.Fan, turning.DefaultIfEmpty().Average())}, whatever the heat"),
            FanFollows.Varies => ("Its own curve", "Not clearly with your CPU or GPU"),
            _ => ("Learning", "Needs an hour of turning"),
        };
        var (thirdLabel, thirdValue, thirdNote) = steady && turning.Count > 0
            ? ("Steadiness", $"Within {Math.Round(100 * (turning.Max() - turning.Min()) / turning.Average()):0}%", "Slowest to fastest, today")
            : ("Fastest today", card.FastestText, card.FastestLine);

        // Headings in the page's caption style (capitals).
        return new FanDetail(followsValue, followsNote, still, stillNote, thirdLabel, thirdValue, thirdNote,
            $"SPEED AT EACH {chip} TEMPERATURE", points, line, lineLabel, silent,
            steady ? "ACROSS YOUR APPS" : card.IsGroup ? "WHAT MAKES THEM SPIN" : "WHAT MAKES IT SPIN", rows,
            steady && rows.Count > 1 ? "About the same whatever runs: this fan doesn't react to load." : "", steady, bars)
        {
            DaysTitle = $"LAST 30 DAYS · {card.Title.ToUpperInvariant()}",
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

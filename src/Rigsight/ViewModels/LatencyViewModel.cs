using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Drivers;
using Rigsight.Core.Export;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>The Latency page's facts for a period, the Timeline's driver marks in it, and the app in front at its longest.</summary>
/// <param name="Games">Since 1.22.0: the games in front in the chart's hours, their names and paths (for the hover's icon).</param>
public sealed record LatencyPage(LatencyReport Report, List<DelayStretch> Marks, string? LongestApp,
    IReadOnlyDictionary<long, (string Name, string? Path)>? Games = null);

/// <summary>What an opened driver shows of the period (see <see cref="ReportService.LatencyDriverAsync"/>).</summary>
public sealed record LatencyDriverData(List<DriverTally> Hours, (long Minute, int Us, string Driver)? Longest, DriverSplit? Split,
    List<Core.Stability.SystemChange> Changes, int? UsualUs, long? FirstSplitHour);

/// <summary>A line of the summary beside the longest delay: an icon, the statement, amber when it needs a look, grey when it's only context.</summary>
/// <param name="IconBack">Its square's colour (1.22.0: the green check's soft green); the usual grey square when null.</param>
public sealed record LatencyLine(string Icon, string Text, bool Warm = false, bool Quiet = false, string IconBrush = "MutedBrush", string IconBack = "Surface2Brush");

/// <summary>A driver in the list: who, its file, its longest, its calls over 1 ms, its time in all, its usual longest.</summary>
/// <param name="IconBrush">Since 1.22.0 its icon's colour and its square's (<paramref name="IconBack"/>), by its kind (see <see cref="LatencyViewModel.KindTint"/>),
/// and its longest as a share of the period's longest (<paramref name="Share"/>, its bar).</param>
public sealed record LatencyDriverRow(string Driver, string Icon, string Who, string File, string Longest, string Over, bool HasOver, string Busy, string Usual, string Tip,
    string IconBrush = "MutedBrush", string IconBack = "Surface2Brush", double Share = 0);

/// <summary>
/// A row of Drivers right now (1.23.0, issue #18 item 11): one per driver, kept while the driver keeps making calls and
/// changed in place every 2 seconds, so the list is never built again as it moves.
/// </summary>
public sealed partial class LiveDriverRow(string driver) : ObservableObject
{
    /// <summary>Its file, the row's key; it opens as an Every driver row does.</summary>
    public string Driver { get; } = driver;
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private string _iconBrush = "MutedBrush";
    [ObservableProperty] private string _iconBack = "Surface2Brush";
    [ObservableProperty] private string _who = "";
    [ObservableProperty] private string _file = "";
    [ObservableProperty] private string _busy = "";
    [ObservableProperty] private string _longest = "";
    /// <summary>Its longest call was 1 ms or longer: its dot amber, else blue.</summary>
    [ObservableProperty] private bool _isLong;
    [ObservableProperty] private string _calls = "";
    /// <summary>Its time busy as a share of the busiest driver's: its bar.</summary>
    [ObservableProperty] private double _share;
    [ObservableProperty] private string _tip = "";

    // What it's sorted by.
    internal long BusyUs;
    internal int LongestUs;
    internal double Rate;
}

/// <summary>A line of What stood out: a driver mark (opens the driver here) or an update's before and after (opens it on Timeline).</summary>
public sealed record LatencyMarkRow(string Icon, string Title, string Detail, string When, DelayStretch? Mark, DateTime? ResultDay);

/// <summary>
/// The Latency page (issue #15, approved mock-up 2026-11-24): everything about latency in one place. Right now, the
/// longest a driver kept a CPU core busy each second and the app wake-up check (with its switch); the period's longest
/// delay with whose and when; each hour's longest; every driver, any of which opens; and what stood out (the Timeline's
/// driver marks and updates that changed driver delays). Facts only, no verdict, no other tool named.
/// </summary>
public sealed partial class LatencyViewModel : ObservableObject
{
    private readonly ReportService _reports;
    private readonly SettingsModel _settings;

    /// <summary>Opens a day's update before and after on the Timeline.</summary>
    public Action<DateTime>? OpenChange { get; init; }
    /// <summary>Goes to another page (Back to Timeline).</summary>
    public Action<string>? GoTo { get; init; }

    public LatencyViewModel(ReportService reports, LiveData live, SettingsModel settings)
    {
        _reports = reports;
        _settings = settings;
        live.LatencyChanged += ApplyLive;
        settings.Changed += () => { OnPropertyChanged(nameof(WakeOn)); OnPropertyChanged(nameof(WakeOff)); };
    }

    // ── The period (as the other look-back pages: a day, the last 7 days, 30 days, 12 months, or a custom range) ──

    [ObservableProperty] private ReportRange _unit = ReportRange.Week;
    [ObservableProperty] private DateTime _anchor = DateTime.Today;
    [ObservableProperty] private DateTime? _firstDay;
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    private (DateTime From, DateTime To) Period => PeriodPicker.Bounds(Unit, Anchor, CustomFrom, CustomTo);
    public bool IncludesToday => Period.To > DateTime.Today;
    public string RangeNote => PeriodPicker.Span(Unit, Anchor, CustomFrom, CustomTo);

    partial void OnUnitChanged(ReportRange value) { OnPropertyChanged(nameof(RangeNote)); if (value != ReportRange.Custom || CustomTo > CustomFrom) _ = RefreshAsync(); }
    partial void OnAnchorChanged(DateTime value) { OnPropertyChanged(nameof(RangeNote)); _ = RefreshAsync(); }
    partial void OnCustomFromChanged(DateTime value) => CustomChanged();
    partial void OnCustomToChanged(DateTime value) => CustomChanged();

    private void CustomChanged()
    {
        OnPropertyChanged(nameof(RangeNote));
        if (Unit == ReportRange.Custom && CustomTo > CustomFrom) _ = RefreshAsync();
    }

    public void NewDay(DateTime was)
    {
        var anchor = PeriodPicker.AfterMidnight(Unit, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        OnPropertyChanged(nameof(RangeNote));
        _ = RefreshAsync();
    }

    /// <summary>All, In games or Outside games.</summary>
    [ObservableProperty] private LatencyFilter _filter = LatencyFilter.All;
    partial void OnFilterChanged(LatencyFilter value) => _ = RefreshAsync();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowPage), nameof(ShowNoAdmin), nameof(ShowDrivers), nameof(ShowNothing))] private bool _loaded;

    /// <summary>The agent runs without admin rights: Windows gives it no driver timings (set by the shell).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowNoAdmin), nameof(ShowDrivers), nameof(ShowNothing), nameof(WakeOffText))] private bool _noAdmin;

    public bool ShowLoading => !Loaded;
    public bool ShowPage => Loaded;
    /// <summary>No admin rights and nothing recorded in the period: one message in place of the driver parts.</summary>
    public bool ShowNoAdmin => Loaded && NoAdmin && !HasDrivers;
    public bool ShowDrivers => Loaded && HasDrivers;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowNoAdmin), nameof(ShowDrivers), nameof(ShowNothing))] private bool _hasDrivers;
    /// <summary>Admin rights, but nothing recorded in the period (before the trace started, or the PC off).</summary>
    public bool ShowNothing => Loaded && !NoAdmin && !HasDrivers;

    /// <summary>The sidebar's "Restart with admin", offered on the no-admin card (set by the shell).</summary>
    public System.Windows.Input.ICommand? RestartWithAdmin { get; init; }

    /// <summary>Whether this account can give admin rights (a standard account can't, so no button there).</summary>
    public Func<bool>? CanRestartAdmin { get; init; }

    public bool ShowRestartAdmin => RestartWithAdmin is not null && (CanRestartAdmin?.Invoke() ?? true);

    private LatencyPage? _page;
    private int _load;

    public async Task RefreshAsync()
    {
        int id = ++_load;
        IsLoading = true;
        try
        {
            FirstDay ??= await _reports.FirstMinuteDayAsync();
            var (from, to) = Period;
            if (to <= from) return;
            var page = await _reports.LatencyAsync(from, to, Filter);
            if (id != _load || page is null) return;
            // Why its empty stretches are empty, from Windows' own log, as Temperatures says it (a long period just says "Not recorded").
            _power = (to - from).TotalDays <= LatencyReport.HourlyDays ? await Task.Run(() => Core.Stability.CrashLogReader.ReadPower(from.AddDays(-2))) : null;
            if (id != _load) return;
            OnPropertyChanged(nameof(GapReason));
            _page = page;
            Fill(page);
            Loaded = true;
            // The driver open keeps up with the period, unless another one is being opened right now (a Timeline line).
            if (_opening == 0 && Opened is { } open && !open.IsMark) await OpenDriverAsync(open.Driver, keepFrom: open.FromTimeline);
        }
        finally
        {
            if (id == _load) IsLoading = false;
        }
    }

    private IReadOnlyList<Core.Stability.PowerEvent>? _power;

    /// <summary>Why a stretch of the charts has nothing recorded (its ends in Unix milliseconds): the PC was off, asleep, shut off suddenly.</summary>
    public Func<long, long, Core.Stability.GapReason> GapReason => (from, to) => Core.Stability.PowerLog.Reason(
        DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime, DateTimeOffset.FromUnixTimeMilliseconds(to).LocalDateTime, _power, TimeSpan.FromMinutes(61));

    // ── Right now (live, only while the page is open) ──

    [ObservableProperty] private bool _liveDrivers = true;
    [ObservableProperty] private string _liveMissingTitle = "";
    [ObservableProperty] private string _liveMissingText = "";
    [ObservableProperty] private string _liveDriverNumber = "–";
    [ObservableProperty] private string _liveDriverWho = "";
    [ObservableProperty] private string _liveDriverNote = "";
    [ObservableProperty] private IReadOnlyList<LiveBar> _liveDriverBars = [];
    [ObservableProperty] private string _liveWakeNumber = "–";
    [ObservableProperty] private string _liveWakeNote = "";
    [ObservableProperty] private IReadOnlyList<LiveBar> _liveWakeBars = [];
    [ObservableProperty] private string _wakeHow = LatencyWords.HowItRuns(8);
    [ObservableProperty] private bool _hasLive;

    public string WakeOffText => NoAdmin ? LatencyWords.OffNoAdmin : LatencyWords.Off;

    /// <summary>The app wake-up check runs (its setting; the switch on the page).</summary>
    public bool WakeOn
    {
        get => _settings.Current.Latency.WakeCheck;
        set
        {
            if (value == WakeOn) return;
            _settings.Update(s => s.Latency.WakeCheck = value);
            Log.Write("latency", value ? "App wake-up check turned on" : "App wake-up check turned off");
            OnPropertyChanged();
            OnPropertyChanged(nameof(WakeOff));
            if (!value) { LiveWakeBars = []; LiveWakeNumber = "–"; LiveWakeNote = ""; }
            // The summary's wake-up line follows the switch (the approved mock-up: off, no wake-up line).
            if (_page is { } page) Fill(page);
        }
    }

    public bool WakeOff => !WakeOn;

    [RelayCommand] private void TurnWakeOn() => WakeOn = true;

    /// <summary>Each second's figures: drivers in bars (each pair of seconds one bar, as the taskbar reads it), the wake-up check as a line.</summary>
    private void ApplyLive(LatencyLive live)
    {
        HasLive = true;
        LiveDrivers = live.Drivers;
        // No driver timings: refused (no admin rights), or the trace is down for now and tried again later.
        (LiveMissingTitle, LiveMissingText) = live.Admin
            ? (Lang.T("Driver timings aren't coming in right now."), Lang.T("Windows stopped giving them to RigSight for now. It tries again every half hour, and what was recorded before still shows below."))
            : (Lang.T("RigSight isn't running as administrator."), Lang.T("Windows only gives driver timings to apps with admin rights. What was recorded before still shows below."));
        int n = live.Seconds.Length;
        var parts = _page?.Report.Drivers.ToDictionary(d => d.Driver, d => d.Part, StringComparer.OrdinalIgnoreCase) ?? [];
        if (live.DriversNow is { } now) ApplyDriversNow(now, live.DriversNowSeconds, parts);
        if (n == 0) return;

        if (live.Drivers)
        {
            // Seconds are paired by the clock (an even second and the one after), not by their place in the 2 minutes: paired
            // by place, every new second moved each pair along one, and two bars side by side merged into one as it came in.
            var bars = new List<LiveBar>();
            int longest = live.DriverUs.Max(), longCount = 0;
            for (int i = 0; i < n;)
            {
                int j = i, us = live.DriverUs[i], next = i + 1;
                if (next < n && live.Seconds[next] / 2 == live.Seconds[i] / 2)
                {
                    if (live.DriverUs[next] > us) (j, us) = (next, live.DriverUs[next]);
                    next++;
                }
                i = next;
                string at = TimeText.At(TimeUtil.FromUnix(live.Seconds[j]), "h:mm:ss tt");
                string file = live.Driver[j];
                if (us >= DriverRules.LongUs) longCount++;
                bars.Add(new LiveBar(us, us >= DriverRules.LongUs,
                    us == 0 ? $"{at}\n{Lang.T("No driver call")}" : $"{at}\n{LatencyWords.Ms(us)}, {LatencyWords.Who(file, parts.GetValueOrDefault(file))}",
                    () => LiveDriverTip(at, us, file, parts.GetValueOrDefault(file), longest, longCount)));
            }
            LiveDriverBars = bars;
            // The last 2 seconds, as the taskbar's Driver delay reads.
            int last = n - 1, prev = Math.Max(0, n - 2);
            int k = live.DriverUs[last] >= live.DriverUs[prev] ? last : prev;
            LiveDriverNumber = LatencyWords.Number(live.DriverUs[k]);
            LiveDriverWho = live.DriverUs[k] == 0 ? "" : LiveWho(live.Driver[k], parts);
            int top = Array.IndexOf(live.DriverUs, live.DriverUs.Max());
            LiveDriverNote = live.DriverUs[top] == 0 ? ""
                : Lang.T($"The longest a single driver kept a CPU core busy, each 2 seconds. Longest in the last 2 minutes {LatencyWords.Ms(live.DriverUs[top])}, {LatencyWords.Mid(LatencyWords.Who(live.Driver[top], parts.GetValueOrDefault(live.Driver[top])))}.");
        }

        if (live.WakeOn)
        {
            WakeHow = LatencyWords.HowItRuns(live.WakeRate);
            var line = new List<LiveBar>();
            int highest = live.WakeUs.Max(), rate = live.WakeRate;
            for (int i = 0; i < n; i++)
            {
                int us = live.WakeUs[i], usual = live.WakeMedianUs[i];
                string at = TimeText.At(TimeUtil.FromUnix(live.Seconds[i]), "h:mm:ss tt");
                line.Add(new LiveBar(us, us >= DriverRules.LongUs, us == 0 ? $"{at}\n{Lang.T("No check")}" : $"{at}\n{Lang.T($"Longest {LatencyWords.Ms(us)}, usually {LatencyWords.Ms(usual)}")}",
                    () => LiveWakeTip(at, us, usual, highest, rate)));
            }
            LiveWakeBars = line;
            var checkedSeconds = Enumerable.Range(0, n).Where(i => live.WakeUs[i] > 0).ToList();
            if (checkedSeconds.Count > 0)
            {
                var medians = checkedSeconds.Select(i => live.WakeMedianUs[i]).Order().ToList();
                LiveWakeNumber = LatencyWords.Number(medians[medians.Count / 2]);
                LiveWakeNote = Lang.T($"Highest in the last 2 minutes {LatencyWords.Ms(checkedSeconds.Max(i => live.WakeUs[i]))}");
            }
            else { LiveWakeNumber = "–"; LiveWakeNote = live.WakeRate == 0 ? Lang.T("Paused while Battery Saver is on.") : ""; }
        }
    }

    // ── Drivers right now (1.23.0, issue #18 item 11, approved mock-up "New"): every driver of the last 10 seconds ──

    public enum LiveSort { Busy, Longest, Calls }

    [ObservableProperty] private LiveSort _liveDriverSort = LiveSort.Busy;
    partial void OnLiveDriverSortChanged(LiveSort value) => SortLiveRows();

    /// <summary>The rows, kept and changed in place; only their order moves.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LiveDriverRow> LiveDriverRows { get; } = [];

    /// <summary>No list from the agent yet since the page opened: the spinner.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LiveRowsEmpty), nameof(LiveRowsShown))] private bool _liveRowsWaiting = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LiveRowsEmpty), nameof(LiveRowsShown))] private bool _hasLiveRows;
    public bool LiveRowsEmpty => !LiveRowsWaiting && !HasLiveRows;
    public bool LiveRowsShown => !LiveRowsWaiting && HasLiveRows;

    private readonly Dictionary<string, LiveDriverRow> _liveRowsByDriver = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The page went off screen: the rows go, so it opens again on the spinner rather than on old figures.</summary>
    public void LiveClosed()
    {
        LiveDriverRows.Clear();
        _liveRowsByDriver.Clear();
        HasLiveRows = false;
        LiveRowsWaiting = true;
    }

    private void ApplyDriversNow(List<LatencyDriverNow> now, int seconds, IReadOnlyDictionary<string, DriverPart?> parts)
    {
        LiveRowsWaiting = false;
        long top = Math.Max(1, now.Count == 0 ? 1 : now.Max(d => d.BusyUs));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in now)
        {
            if (!seen.Add(d.Driver)) continue;
            if (!_liveRowsByDriver.TryGetValue(d.Driver, out var row))
            {
                row = new LiveDriverRow(d.Driver);
                _liveRowsByDriver[d.Driver] = row;
                LiveDriverRows.Add(row);
            }
            var part = parts.GetValueOrDefault(d.Driver);
            (row.IconBrush, row.IconBack) = KindTint(d.Driver, part);
            row.Icon = LatencyWords.Icon(d.Driver, part);
            row.Who = LatencyWords.Who(d.Driver, part);
            row.File = LatencyWords.File(d.Driver, part);
            (row.BusyUs, row.LongestUs, row.Rate) = (d.BusyUs, d.LongestUs, LatencyWords.Rate(d.Calls, seconds));
            row.Busy = LatencyWords.Ms(d.BusyUs);
            row.Longest = LatencyWords.Ms(d.LongestUs);
            row.IsLong = LatencyWords.IsLong(d.LongestUs);
            row.Calls = LatencyWords.PerSecond(row.Rate);
            row.Share = Math.Clamp((double)d.BusyUs / top, 0.02, 1);
            row.Tip = LatencyWords.Split(d.DpcUs, d.IsrUs);
        }
        // Drivers that made no call in the window leave the list.
        for (int i = LiveDriverRows.Count - 1; i >= 0; i--)
            if (!seen.Contains(LiveDriverRows[i].Driver))
            {
                _liveRowsByDriver.Remove(LiveDriverRows[i].Driver);
                LiveDriverRows.RemoveAt(i);
            }
        SortLiveRows();
        HasLiveRows = LiveDriverRows.Count > 0;
    }

    // The order by the heading picked, by moving rows (never rebuilding them); the name breaks ties so equal rows don't swap.
    private void SortLiveRows()
    {
        var order = (LiveDriverSort switch
        {
            LiveSort.Longest => LiveDriverRows.OrderByDescending(r => r.LongestUs).ThenByDescending(r => r.BusyUs),
            LiveSort.Calls => LiveDriverRows.OrderByDescending(r => r.Rate).ThenByDescending(r => r.BusyUs),
            _ => LiveDriverRows.OrderByDescending(r => r.BusyUs),
        }).ThenBy(r => r.Driver, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            int at = LiveDriverRows.IndexOf(order[i]);
            if (at != i) LiveDriverRows.Move(at, i);
        }
    }

    [RelayCommand]
    private Task OpenLiveDriver(LiveDriverRow row) => OpenDriverAsync(row.Driver, keepFrom: false);

    private static string LiveWho(string driver, IReadOnlyDictionary<string, DriverPart?> parts) =>
        driver.Length == 0 ? LatencyWords.Who("", null) : $"{LatencyWords.Who(driver, parts.GetValueOrDefault(driver))} · {driver}";

    // ── The period's summary ──

    [ObservableProperty] private string _longestNumber = "";
    [ObservableProperty] private string _longestWho = "";
    [ObservableProperty] private string _longestFile = "";
    [ObservableProperty] private string _longestWhen = "";
    [ObservableProperty] private IReadOnlyList<LatencyLine> _lines = [];

    // ── The chart ──

    public enum ChartMetric { Driver, Wake, Over }

    [ObservableProperty] private ChartMetric _metric = ChartMetric.Driver;
    partial void OnMetricChanged(ChartMetric value) { if (_page is { } p) FillChart(p.Report); }
    [ObservableProperty] private IReadOnlyList<LatencyChartSlot> _slots = [];
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private bool _chartIsMs = true;
    [ObservableProperty] private bool _chartShowsGuidance = true;
    /// <summary>The bars' colour: the wake-up check's violet for its own chart (as its live line), else the text colour.</summary>
    [ObservableProperty] private string _chartBrush = "TextBrush";
    /// <summary>The colour of delays of 1 ms or longer (the amber, driver delays only); null: the bars' own, in full.</summary>
    [ObservableProperty] private string? _chartLongBrush = "WarmBrush";
    [ObservableProperty] private string _chartNote = "";

    // ── The drivers ──

    public enum DriverSort { Longest, Over, Busy }

    [ObservableProperty] private DriverSort _sort = DriverSort.Longest;
    partial void OnSortChanged(DriverSort value) { if (_page is { } p) FillDrivers(p.Report); }
    [ObservableProperty] private IReadOnlyList<LatencyDriverRow> _driverRows = [];
    [ObservableProperty] private string _driversNote = "";

    // ── What stood out ──

    [ObservableProperty] private IReadOnlyList<LatencyMarkRow> _marks = [];
    [ObservableProperty] private string _marksEmptyTitle = "";
    [ObservableProperty] private string _marksEmptyText = "";
    public bool HasMarks => Marks.Count > 0;
    partial void OnMarksChanged(IReadOnlyList<LatencyMarkRow> value) => OnPropertyChanged(nameof(HasMarks));

    private Dictionary<DateTime, ChangeResult> _results = [];

    private void Fill(LatencyPage page)
    {
        var r = page.Report;
        HasDrivers = r.HasDrivers;

        // The longest delay of the period, whose, when, and what was in front.
        if (r.Longest is { } l)
        {
            var part = r.Drivers.FirstOrDefault(d => d.Driver.Equals(l.Driver, StringComparison.OrdinalIgnoreCase))?.Part;
            LongestNumber = LatencyWords.Number(l.Us);
            LongestWho = LatencyWords.Who(l.Driver, part);
            LongestFile = LatencyWords.File(l.Driver, part);
            var at = TimeUtil.FromUnix(l.Minute);
            LongestWhen = l.Minute == 0 ? "" : page.LongestApp is { } app
                ? Lang.T($"{at:dddd d MMMM}, {TimeText.Time(at)}, with {app} in front")
                : Lang.T($"{at:dddd d MMMM}, {TimeText.Time(at)}");
        }

        var lines = new List<LatencyLine>();
        var most = r.Drivers.MaxBy(d => d.Long);
        lines.Add(new("", LatencyWords.OverLine(r.Long, most)));
        if (r.TooSoon) lines.Add(new("", LatencyWords.TooSoon(r.HistoryDays)));
        if (r.Wake is { } w && WakeOn) lines.Add(new("", LatencyWords.WakeLine(w)));
        if (LatencyWords.LostLine(r.LostMinutes) is { } lost) lines.Add(new("", lost, Warm: true, IconBrush: "WarmBrush"));
        // Nothing dropped: the one coloured sentence icon, a green check (the approved mock-up, 1.22.0).
        else if (r.FirstDriverHour is long first) lines.Add(new("", first > TimeUtil.ToUnix(r.From) ? LatencyWords.Recording(TimeUtil.FromUnix(first)) : LatencyWords.NothingDropped, Quiet: true,
            IconBrush: "GoodBrush", IconBack: "GoodSoftBrush"));
        Lines = lines;

        FillChart(r);
        FillDrivers(r);
        _ = FillMarksAsync(page);
    }

    /// <summary>When the chart's figures were first recorded (the wake-up check began later than the drivers): before it, "not recorded yet".</summary>
    [ObservableProperty] private DateTime? _chartRecordedFrom;
    /// <summary>When driver activity was first recorded, for an opened driver's chart.</summary>
    [ObservableProperty] private DateTime? _driverRecordedFrom;

    private void FillChart(LatencyReport r)
    {
        long? first = Metric == ChartMetric.Wake ? r.FirstWakeHour : r.FirstDriverHour;
        ChartRecordedFrom = first is long f ? TimeUtil.FromUnix(f) : null;
        DriverRecordedFrom = r.FirstDriverHour is long d ? TimeUtil.FromUnix(d) : null;
        bool hourly = (r.To - r.From).TotalDays <= LatencyReport.HourlyDays;
        ChartIsMs = Metric != ChartMetric.Over;
        ChartShowsGuidance = Metric == ChartMetric.Driver;
        // Driver delays blue under 1 ms and amber from 1 ms (1.23.0, the owner's "Blue and amber"); the wake-up check's
        // violet for its own (1.22.0), brighter from 1 ms; calls over 1 ms are counts, in one blue.
        ChartBrush = Metric == ChartMetric.Wake ? "PurpleBrush" : "CpuBrush";
        ChartLongBrush = Metric == ChartMetric.Driver ? "WarmBrush" : null;
        ShowLegend = Metric == ChartMetric.Driver;
        ChartTitle = (Metric, hourly) switch
        {
            (ChartMetric.Driver, true) => Lang.T("Longest driver delay each hour"),
            (ChartMetric.Driver, false) => Lang.T("Longest driver delay each day"),
            (ChartMetric.Wake, true) => Lang.T("Longest app wake-up delay each hour"),
            (ChartMetric.Wake, false) => Lang.T("Longest app wake-up delay each day"),
            (_, true) => Lang.T("Times over 1 ms each hour"),
            _ => Lang.T("Times over 1 ms each day"),
        };
        ChartNote = Metric == ChartMetric.Wake && r.Wake is null ? Lang.T("No wake-up checks were recorded in these days.") : "";
        var parts = r.Drivers.ToDictionary(d => d.Driver, d => d.Part, StringComparer.OrdinalIgnoreCase);
        var slots = new List<LatencyChartSlot>();
        int every = Math.Max(1, (int)Math.Ceiling(r.Slots.Count / 8.0));
        for (int i = 0; i < r.Slots.Count; i++)
        {
            var s = r.Slots[i];
            string axis = !hourly ? (i % every == 0 ? s.Start.ToString("d MMM", CultureInfo.CurrentCulture) : "")
                : r.Slots.Count <= 24 ? (i % 3 == 0 ? TimeText.At(s.Start, "h tt") : "")
                : s.Start.Hour == 0 || i == 0 ? s.Start.ToString("ddd d", CultureInfo.CurrentCulture) : "";
            string title = hourly ? Lang.T($"{s.Start:ddd d MMM}, {TimeText.Time(s.Start)} to {TimeText.Time(s.End)}") : s.Start.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
            double? value = Metric switch { ChartMetric.Driver => s.LongestUs, ChartMetric.Wake => s.WakeUs, _ => s.LongestUs is null ? null : s.Long };
            var tip = new List<string>();
            if (value is null) tip.Add(Wording.NotRecorded);
            else if (Metric == ChartMetric.Driver)
            {
                tip.Add(Lang.T($"Longest {LatencyWords.Ms(s.LongestUs!.Value)}, {LatencyWords.Mid(LatencyWords.Who(s.Driver, parts.GetValueOrDefault(s.Driver)))}"));
                tip.Add(s.Long == 0 ? Lang.T("No call over 1 ms") : s.Long == 1 ? Lang.T("Over 1 ms once") : Lang.T($"Over 1 ms {s.Long:N0} times"));
            }
            else if (Metric == ChartMetric.Wake)
            {
                tip.Add(Lang.T($"Longest {LatencyWords.Ms(s.WakeUs!.Value)}"));
                if (s.WakeMedianUs is int m) tip.Add(Lang.T($"Usually {LatencyWords.Ms(m)}"));
            }
            else tip.Add(s.Long == 0 ? Lang.T("No call over 1 ms") : s.Long == 1 ? Lang.T("Over 1 ms once") : Lang.T($"Over 1 ms {s.Long:N0} times"));
            // Dropped readings are the driver trace's: the wake-up check's figures are whole then.
            bool lost = s.Lost && Metric != ChartMetric.Wake;
            if (lost) tip.Add(Lang.T("Windows dropped some readings in this time, so a longer delay could be missing."));
            var metric = Metric switch { ChartMetric.Wake => LatencyMetric.Wake, ChartMetric.Over => LatencyMetric.Over, _ => LatencyMetric.Driver };
            var games = _page?.Games;
            Func<RichTip>? rich = value is null ? null : () => HourTip(s, hourly, metric, parts, games);
            slots.Add(new LatencyChartSlot(axis, title, value, lost, value is double v && Metric != ChartMetric.Over && v >= DriverRules.LongUs, tip, s.Start, s.End, rich));
        }
        Slots = slots;
    }

    /// <summary>The chart's legend: Under 1 ms and 1 ms or longer, for the driver delays.</summary>
    [ObservableProperty] private bool _showLegend = true;

    /// <summary>
    /// An hour's hover (1.22.0, the approved mock-up): the hour, the game in front with its own icon, the chart's figure
    /// big, who it was (the driver's kind icon, its name and file on one row), its other figures, and the other drivers
    /// busy then with their longest. No click line: a click on an hour opens nothing.
    /// </summary>
    private static RichTip HourTip(LatencySlot s, bool hourly, LatencyMetric metric, IReadOnlyDictionary<string, DriverPart?> parts,
        IReadOnlyDictionary<long, (string Name, string? Path)>? games)
    {
        var f = LatencyHover.Of(s, hourly, metric, d => parts.GetValueOrDefault(d))!;
        RichIcon? subIcon = null;
        string sub = Lang.T("No game in front");
        if (f.Game is long g && games?.GetValueOrDefault(g) is { Name.Length: > 0 } game)
        {
            sub = Lang.T($"{game.Name} in front");
            subIcon = IconCache.Get(game.Path) is { } img ? new RichIcon(Image: img) : new RichIcon(Letter: game.Name[..1].ToUpperInvariant());
        }
        var facts = new List<RichFact>();
        if (f.By is { } by) facts.Add(new RichFact(Lang.T("By"), by.Who, Wide: true, Icon: KindIcon(by), Detail: by.Driver.Length > 0 ? by.Driver : null));
        facts.AddRange(f.Facts.Select(x => new RichFact(x.Label, x.Value)));
        var others = f.Others.Select(o => new RichItem(KindIcon(o), o.Who, o.Ms)).ToList();
        return new RichTip(f.Title)
        {
            Sub = sub,
            SubIcon = subIcon,
            Big = f.Big,
            BigWord = f.BigWord,
            Facts = facts,
            Notes = others.Count > 0 ? [new RichNote(f.OthersLabel, others)] : [],
            Mark = f.Lost ? Lang.T("Windows dropped some readings in this time, so a longer delay could be missing.") : null,
        };
    }

    /// <summary>
    /// A bar of Right now's driver strip (1.23.0, the hover rule: as useful as Reports' and Games' hovers): its 2 seconds,
    /// the longest call big, who made it (the driver's kind icon, its name and file), and how it sits in the 2 minutes shown
    /// (the longest there, and how many bars reached 1 ms). Figures in the text colour; the bar itself carries the amber.
    /// </summary>
    private static RichTip LiveDriverTip(string at, int us, string file, DriverPart? part, int longest, int longCount)
    {
        if (us == 0) return new RichTip(at) { Big = Lang.T("No driver call"), Sub = Lang.T("In these 2 seconds") };
        string who = LatencyWords.Who(file, part);
        var by = new LatencyHoverDriver(file, who, part, LatencyWords.Ms(us));
        var facts = new List<RichFact>
        {
            new(Lang.T("By"), who, Wide: true, Icon: KindIcon(by), Detail: file.Length > 0 ? file : null),
            new(Lang.T("Longest in these 2 minutes"), LatencyWords.Ms(longest)),
            new(Lang.T("1 ms or longer in these 2 minutes"), longCount == 0 ? Lang.T("None") : longCount == 1 ? Lang.T("Once") : Lang.T($"{longCount} times")),
        };
        return new RichTip(at)
        {
            Sub = Lang.T("In these 2 seconds"),
            Big = LatencyWords.Ms(us),
            BigWord = Lang.T("longest driver call"),
            Facts = facts,
        };
    }

    /// <summary>
    /// A second of Right now's app wake-up line (1.23.0): the longest wait big, against that second's usual and the
    /// highest in the 2 minutes shown, and how often RigSight checks right now.
    /// </summary>
    private static RichTip LiveWakeTip(string at, int us, int usual, int highest, int rate)
    {
        if (us == 0) return new RichTip(at) { Big = Lang.T("No check"), Sub = rate == 0 ? Lang.T("Paused while Battery Saver is on.") : null };
        return new RichTip(at)
        {
            Big = LatencyWords.Ms(us),
            BigWord = Lang.T("longest wake-up"),
            Facts =
            [
                new(Lang.T("Usually"), LatencyWords.Ms(usual)),
                new(Lang.T("Highest in these 2 minutes"), LatencyWords.Ms(highest)),
                new(Lang.T("Checks"), Lang.T($"{rate} a second"), Wide: true),
            ],
        };
    }

    /// <summary>A driver's icon in its kind's tint, for a hover.</summary>
    private static RichIcon KindIcon(LatencyHoverDriver d)
    {
        var (ink, back) = KindTint(d.Driver, d.Part);
        return new RichIcon(Glyph: LatencyWords.Icon(d.Driver, d.Part), Ink: Converters.ResourceBrushConverter.Of(ink), Back: Converters.ResourceBrushConverter.Of(back));
    }

    /// <summary>
    /// A driver's kind as a colour (1.22.0, the approved mock-up): graphics the GPU's cyan, network indigo, sound pink,
    /// storage the CPU's blue, each on its soft wash; the rest grey. The icon's colour and its square's, as palette keys.
    /// </summary>
    internal static (string Ink, string Back) KindTint(string file, DriverPart? part) =>
        part?.Kind == PartKind.Gpu ? ("GpuBrush", "GpuSoftBrush")
        : part?.Kind is PartKind.Ethernet or PartKind.WiFi ? ("IndigoBrush", "IndigoSoftBrush")
        : file.Length == 0 ? ("MutedBrush", "Surface2Brush")
        : DriverWords.AreaOf(file, part) switch
        {
            DriverArea.Graphics => ("GpuBrush", "GpuSoftBrush"),
            DriverArea.Network => ("IndigoBrush", "IndigoSoftBrush"),
            DriverArea.Sound => ("PinkBrush", "PinkSoftBrush"),
            DriverArea.Storage => ("CpuBrush", "CpuSoftBrush"),
            _ => ("MutedBrush", "Surface2Brush"),
        };

    private void FillDrivers(LatencyReport r)
    {
        IEnumerable<LatencyDriver> list = Sort switch
        {
            DriverSort.Over => r.Drivers.OrderByDescending(d => d.Long).ThenByDescending(d => d.LongestUs),
            DriverSort.Busy => r.Drivers.OrderByDescending(d => d.TotalUs),
            _ => r.Drivers.OrderByDescending(d => d.LongestUs),
        };
        double top = Math.Max(1, r.Drivers.Count == 0 ? 1 : r.Drivers.Max(d => d.LongestUs));
        DriverRows = [.. list.Select(d =>
        {
            var (ink, back) = KindTint(d.Driver, d.Part);
            string who = LatencyWords.Who(d.Driver, d.Part);
            string usual = d.UsualUs is int u ? LatencyWords.Ms(u) : Lang.T("Too soon to say");
            string busy = d.TotalUs < 1_000_000 ? Lang.T("under 1s") : Units.Duration(d.TotalUs / 1_000_000.0);
            string over = d.Long == 0 ? Lang.T("None") : d.Long.ToString("N0", CultureInfo.CurrentCulture);
            return new LatencyDriverRow(d.Driver, LatencyWords.Icon(d.Driver, d.Part), who, LatencyWords.File(d.Driver, d.Part), LatencyWords.Ms(d.LongestUs), over, d.Long > 0, busy, usual,
                Lang.T($"{d.Calls:N0} calls in these days, {busy} on the CPU in all."), ink, back, Math.Clamp(d.LongestUs / top, 0.02, 1));
        })];
        DriversNote = r.Drivers.Count == 1 ? Lang.T("1 driver kept a CPU core busy in these days. Click it for its details.")
            : Lang.T($"{r.Drivers.Count} drivers kept a CPU core busy in these days. Click one for its details.");
    }

    private async Task FillMarksAsync(LatencyPage page)
    {
        var r = page.Report;
        var rows = page.Marks.Select(m => new LatencyMarkRow(LatencyWords.Icon(m.Driver, m.Part), m.Title, m.Detail, TimeText.At(TimeUtil.FromUnix(m.From), "ddd d, h:mm tt"), m, null)).ToList();
        try
        {
            _results = await _reports.ChangeResultsAsync();
            foreach (var res in _results.Values.Where(x => x.DriverDelays && x.Day >= r.From.Date && x.Day < r.To))
                rows.Add(new LatencyMarkRow("", res.Said, res.Context, TimeText.At(res.Day, "ddd d"), null, res.Day));
        }
        catch (Exception ex) { Log.Error("latency", ex); }
        Marks = [.. rows.OrderByDescending(x => x.Mark is { } m ? TimeUtil.FromUnix(m.From) : x.ResultDay)];
        if (Marks.Count == 0)
        {
            MarksEmptyTitle = r.TooSoon ? Lang.T("Nothing to compare with yet") : Lang.T("Nothing stood out");
            MarksEmptyText = r.TooSoon
                ? Lang.T($"A driver is marked here when it keeps a CPU core busy much longer than its own usual. That needs {DriverRules.UsualDays} days of history.")
                : Lang.T("No driver kept a CPU core busy much longer than its own usual outside games, and no update changed driver delays.");
        }
    }

    // ── An opened driver (from the list, a Timeline line, the taskbar or a play) ──

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsListShown))] private LatencyDriverView? _opened;
    public bool IsOpen => Opened is not null;
    public bool IsListShown => Opened is null;
    /// <summary>Raised when a driver opens, to scroll its view to the top.</summary>
    public event Action? DriverOpened;
    private int _openAsk;
    // Opens on their way (a row, a Timeline line, the taskbar): a refresh doesn't re-open the old one over them.
    private int _opening;

    [RelayCommand]
    private Task OpenDriver(LatencyDriverRow row) => OpenDriverAsync(row.Driver, keepFrom: false);

    [RelayCommand]
    private async Task OpenMark(LatencyMarkRow row)
    {
        if (row.Mark is { } mark) await ShowMarkAsync(mark, fromTimeline: false);
        else if (row.ResultDay is DateTime day) OpenChange?.Invoke(day);
    }

    private async Task OpenDriverAsync(string driver, bool keepFrom)
    {
        int ask = ++_openAsk;
        var (from, to) = Period;
        _opening++;
        LatencyDriverData? data;
        try { data = await _reports.LatencyDriverAsync(driver, from, to); }
        finally { _opening--; }
        if (ask != _openAsk || data is null) return;
        var row = _page?.Report.Drivers.FirstOrDefault(d => d.Driver.Equals(driver, StringComparison.OrdinalIgnoreCase));
        Opened = new LatencyDriverView(driver, row?.Part, row, data, from, to, null, keepFrom && Opened?.FromTimeline == true);
        DriverOpened?.Invoke();
    }

    /// <summary>A Timeline driver line opened here (issue #15 follow-up): the mark's own story, then the driver over that day. Back goes to Timeline.</summary>
    public async Task ShowMarkAsync(DelayStretch mark, bool fromTimeline)
    {
        int ask = ++_openAsk;
        var day = TimeUtil.FromUnix(mark.LongestAt).Date;
        _opening++;
        DelayCase? found;
        LatencyDriverData? data;
        try
        {
            found = await _reports.DelayCaseAsync(mark);
            data = await _reports.LatencyDriverAsync(mark.Driver, day, day.AddDays(1));
        }
        finally { _opening--; }
        if (ask != _openAsk || found is null || data is null) return;
        Opened = new LatencyDriverView(mark.Driver, mark.Part, null, data, day, day.AddDays(1), new DelayView(found), fromTimeline);
        DriverOpened?.Invoke();
    }

    /// <summary>A driver opened from elsewhere (the taskbar's Driver delay, a game play), over the page's period, or a play's hours.</summary>
    public async Task ShowDriverAsync(string driver, DateTime? from = null, DateTime? to = null, LatencyFilter? filter = null)
    {
        if (from is DateTime f && to is DateTime t && t > f)
        {
            (CustomFrom, CustomTo) = (f, t);
            Unit = ReportRange.Custom;
        }
        if (filter is { } fl) Filter = fl;
        await RefreshAsync();
        if (driver.Length > 0) await OpenDriverAsync(driver, keepFrom: false);
    }

    [RelayCommand]
    private void CloseDriver()
    {
        _openAsk++;
        bool back = Opened?.FromTimeline == true;
        Opened = null;
        if (back) GoTo?.Invoke("timeline");
    }

    // ── Export ──

    /// <summary>The page's figures as facts (the Export box's summary).</summary>
    internal List<FactRow> SummaryFacts() => FactsOf(_page);

    /// <summary>The summary for a period other than the one on screen (the Export box), read without a page.</summary>
    internal static async Task<List<FactRow>> SummaryAsync(ReportService reports, ReportRange unit, DateTime anchor, LatencyFilter filter)
    {
        var (from, to) = PeriodPicker.Bounds(unit, anchor, default, default);
        return FactsOf(to > from ? await reports.LatencyAsync(from, to, filter) : null);
    }

    private static List<FactRow> FactsOf(LatencyPage? page)
    {
        if (page?.Report is not { HasDrivers: true } r) return [new(Lang.T("Longest driver delay"), Wording.NotRecorded, "")];
        var facts = new List<FactRow>();
        if (r.Longest is { } l)
        {
            var part = r.Drivers.FirstOrDefault(d => d.Driver.Equals(l.Driver, StringComparison.OrdinalIgnoreCase))?.Part;
            string when = l.Minute == 0 ? "" : TimeText.At(TimeUtil.FromUnix(l.Minute), "ddd d MMM, h:mm tt");
            facts.Add(new(Lang.T("Longest driver delay"), LatencyWords.Ms(l.Us), when.Length == 0 ? LatencyWords.Who(l.Driver, part) : $"{LatencyWords.Who(l.Driver, part)}, {when}"));
        }
        facts.Add(new(Lang.T("Over 1 ms"), r.Long.ToString("N0", CultureInfo.CurrentCulture), ""));
        if (r.Wake is { } w) facts.Add(new(Lang.T("App wake-up delay"), LatencyWords.Ms(w.HighestUs), Lang.T($"Usually {LatencyWords.Ms(w.MedianUs)}")));
        foreach (var d in r.Drivers.Take(20))
            facts.Add(new(LatencyWords.Who(d.Driver, d.Part), LatencyWords.Ms(d.LongestUs), d.Long == 0 ? LatencyWords.File(d.Driver, d.Part)
                : d.Long == 1 ? Lang.T($"{LatencyWords.File(d.Driver, d.Part)}, over 1 ms once") : Lang.T($"{LatencyWords.File(d.Driver, d.Part)}, over 1 ms {d.Long:N0} times")));
        return facts;
    }
}

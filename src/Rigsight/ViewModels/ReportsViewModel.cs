using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Converters;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>One "hot moment" row on the Reports page.</summary>
public sealed record PeakRow(string Label, string Value, string When, string? App, Brush Brush);

public sealed partial class ReportsViewModel(ReportService reports) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(IsMultiDay), nameof(IsYear), nameof(BarsTitle), nameof(InsightsTitle))]
    private ReportRange _range = ReportRange.Day;

    [ObservableProperty] private DateTime _anchor = DateTime.Today;

    /// <summary>A custom range's start and end (whole hours), when <see cref="Range"/> is Custom.</summary>
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    /// <summary>The first day with any history (the period picker starts there).</summary>
    [ObservableProperty] private DateTime? _firstDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ReportAsk), nameof(Subtitle), nameof(TimelineEnd), nameof(Insights), nameof(Peaks), nameof(TopSessions), nameof(HasData), nameof(ShowEmpty), nameof(ShowData),
        nameof(MaxActive), nameof(BackgroundOnlyCount), nameof(BackgroundToggleText), nameof(CoverageNote), nameof(InsightsTitle))]
    private Report? _report;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private bool _showBackgroundApps;

    // The report is read again on every visit and every minute, and most of it comes back the same: lists that say
    // the same stay the ones the page has, so their rows aren't built again (see Kept).
    partial void OnReportChanged(Report? value)
    {
        SyncApps();
        foreach (var name in new[]
                 {
                     nameof(ActiveLine), nameof(GamesText), nameof(GamesLine), nameof(InternetText), nameof(InternetLine), nameof(HasEnergy), nameof(EnergyText),
                     nameof(TimeSplit), nameof(HasTimeSplit), nameof(TimeSplitNote), nameof(ShownInsights), nameof(UsualRows), nameof(HasUsual), nameof(UsualNow), nameof(ProblemsLine), nameof(ProblemsTone),
                 })
            OnPropertyChanged(name);
    }

    // ── The five figures on top, each against something ──

    /// <summary>Under the time in use: how it compares ("4h 38m more than your usual day of 8h 47m"), or how long the PC was on.</summary>
    public string ActiveLine => Report is not { } r ? ""
        // In another language the sentence's own opening can't be cut off by its English words: its one statement is used.
        : r.Insights.FirstOrDefault(i => i.Key == "screen-compare") is { } compare ? (Lang.Translating && compare.Short is { } s ? s : Bare(compare.Text))
        : PcOnFor(Units.Duration(r.OnSec));

    private static string PcOnFor(string time) => Lang.T($"PC on for {time}");

    /// <summary>A line of the list as a few words under a figure: without its "That's" and its full stop.</summary>
    internal static string Bare(string line)
    {
        string text = line.StartsWith("That's ", StringComparison.Ordinal) ? line[7..] : line;
        text = text.TrimEnd('.');
        return text.Length == 0 ? text : char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];
    }

    public string GamesText => Report is { GamingSec: >= 60 } r ? Units.Duration(r.GamingSec) : "—";
    public string GamesLine => Report is not { GamingSec: >= 60 } r ? NoGames(When)
        : r.Apps.Count(a => a.Category == Core.Settings.AppCategory.Game && a.ActiveSec >= 60) is var n && n == 1 ? Lang.T("1 game") : Lang.T($"{n} games");

    private static string NoGames(string when) => Lang.T($"No games {when}");

    public string InternetText => Report?.Net is { Down: > 0 } net ? Units.Data(net.Down) : "—";
    public string InternetLine
    {
        get
        {
            // Not read at all is "Not recorded"; read, and nothing came down, is what didn't happen.
            if (Report?.Net is not { } read) return Core.Wording.NotRecorded;
            if (read is not { Down: > 0 } net) return Lang.T("No internet use");
            if (net.PreviousDown is not > 0)
            {
                string up = Units.Data(net.Up);
                return Lang.T($"{up} uploaded");
            }
            double change = (net.Down - net.PreviousDown.Value) / (double)net.PreviousDown.Value * 100;
            return AgainstBefore(Range, change);
        }
    }

    /// <summary>The period's download against the one before it, each sentence whole: "12% more than the week before".</summary>
    private static string AgainstBefore(ReportRange range, double change)
    {
        double by = Math.Abs(change);
        if (by < 3)
            return range switch
            {
                ReportRange.Day => Lang.T("About the same as the day before"),
                ReportRange.Week => Lang.T("About the same as the week before"),
                ReportRange.Month => Lang.T("About the same as the month before"),
                ReportRange.Year => Lang.T("About the same as the year before"),
                _ => Lang.T("About the same as the period before"),
            };
        if (change > 0)
            return range switch
            {
                ReportRange.Day => Lang.T($"{by:0}% more than the day before"),
                ReportRange.Week => Lang.T($"{by:0}% more than the week before"),
                ReportRange.Month => Lang.T($"{by:0}% more than the month before"),
                ReportRange.Year => Lang.T($"{by:0}% more than the year before"),
                _ => Lang.T($"{by:0}% more than the period before"),
            };
        return range switch
        {
            ReportRange.Day => Lang.T($"{by:0}% less than the day before"),
            ReportRange.Week => Lang.T($"{by:0}% less than the week before"),
            ReportRange.Month => Lang.T($"{by:0}% less than the month before"),
            ReportRange.Year => Lang.T($"{by:0}% less than the year before"),
            _ => Lang.T($"{by:0}% less than the period before"),
        };
    }

    /// <summary>What the CPU and the graphics card drew ("1.9 kWh", "410 Wh"); left out where no power is read.</summary>
    public bool HasEnergy => Report?.EnergyWh is > 0;
    public string EnergyText => Report?.EnergyWh is > 0 and var wh ? EnergyOf(wh) : "—";
    internal static string EnergyOf(double wattHours) => wattHours >= 1000 ? $"{wattHours / 1000:0.0} kWh" : $"{wattHours:0} Wh";

    // The PC's own problems (blue screens, graphics driver resets, sudden shut-offs in use), as Home and the "Against your
    // usual" card count them; likely power cuts and app crashes are said under the number, apart.
    private List<CrashRow> PcProblems => [.. Crashes.Where(c => PcUsual.CountsAsPcProblem(c.Event, c.Finding))];
    private int PowerCount => Crashes.Count(c => PcUsual.LooksLikePower(c.Event, c.Finding));
    private int PowerLikelyCount => Crashes.Count(c => PcUsual.ShutOffOf(c.Event, c.Finding) == ShutOff.PowerLikely);

    public string ProblemsText => PcProblems.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// The number's colour, by the same rule as everywhere in R4: amber only when more than usual (or, where there is no
    /// usual to go by, when there were any), green with none, plain when there were some as usual.
    /// </summary>
    public string ProblemsTone => PcProblems.Count == 0 ? "Good"
        : (Report?.Usual?.Problems is { } pr && PcUsual.CardRows(Report).Count > 0 ? pr.Tone == EffectTone.Worse : true) ? "Warm" : "Plain";

    public string ProblemsLine
    {
        get
        {
            var pc = PcProblems;
            int apps = Crashes.Count(c => !PcUsual.IsPcProblem(c.Event));
            // Against the usual where the period has its card (a week, a month); else what they were.
            // ("As usual" only for some: none is said as none, and app crashes after it aren't "usual").
            string? first = pc.Count > 0 && PcUsual.CardRows(Report).FirstOrDefault(r => r.Key == "problems") is { } row ? row.Means
                : pc.Count > 0 ? string.Join(", ", pc.GroupBy(c => c.Event.Kind).Select(g => Core.Wording.Count(g.Key, g.Count()))) : null;
            var parts = new[] { first, apps > 0 ? Core.Wording.Count(Core.Stability.CrashKind.AppCrash, apps) : null, PowerCount > 0 ? Core.Wording.PowerOffs(PowerLikelyCount, PowerCount) : null }
                .Where(t => t is not null).ToList();
            return parts.Count == 0 ? Lang.T("No PC problems") : string.Join(" · ", parts);
        }
    }

    // ── Your day, minute by minute: what its curves are drawn from ──

    private readonly LiveData? _live;
    private string _localPlot = "Avg";

    /// <summary>
    /// The Temperatures page's data (set by the window): its graph's Average / Highest / Lowest choice is this page's too,
    /// one remembered pick for both, so changing it on either page changes it on the other.
    /// </summary>
    public LiveData? Live
    {
        get => _live;
        init
        {
            _live = value;
            if (value is null) return;
            value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(LiveData.ChartPlot)) return;
                OnPropertyChanged(nameof(ChartPlot));
            };
        }
    }

    /// <summary>What the minute-by-minute curves are: each minute's average ("Avg"), highest ("High") or lowest ("Low").</summary>
    public string ChartPlot
    {
        get => Live?.ChartPlot ?? _localPlot;
        set
        {
            if (value == ChartPlot) return;
            if (Live is { } live) live.ChartPlot = value; // says so back (above)
            else
            {
                _localPlot = value;
                OnPropertyChanged();
            }
        }
    }

    // ── Where the time went ──

    private List<TimePart> _timeSplit = [];

    /// <summary>The period's time in use by kind of app, biggest first; kinds under a fiftieth of it go into "Everything else".</summary>
    public List<TimePart> TimeSplit => _timeSplit = Kept.Or(_timeSplit, SplitOf(Report));
    public bool HasTimeSplit => TimeSplit.Count >= 2;
    public string TimeSplitNote => Report is { } r ? InUseAway(Units.Duration(r.ActiveSec), Units.Duration(r.AwaySec)) : "";

    private static string InUseAway(string inUse, string away) => Lang.T($"{inUse} in use · {away} away");

    internal static List<TimePart> SplitOf(Report? report)
    {
        if (report is null) return [];
        var kinds = report.Apps.Where(a => a.ActiveSec > 0).GroupBy(a => a.Category).Select(g => (Kind: g.Key, Sec: g.Sum(a => a.ActiveSec))).OrderByDescending(k => k.Sec).ToList();
        double total = kinds.Sum(k => k.Sec);
        if (total < 60) return [];
        var parts = new List<TimePart>();
        double rest = 0;
        foreach (var (kind, sec) in kinds)
        {
            // System apps, anything unsorted and slivers too small to see in the bar are one grey part at the end.
            if (kind is Core.Settings.AppCategory.Other or Core.Settings.AppCategory.System || sec < total / 50) rest += sec;
            else parts.Add(new TimePart(Core.Apps.AppCatalog.Label(kind), sec, ThemeManager.CategoryHex(kind)));
        }
        if (rest >= 60) parts.Add(new TimePart(Lang.T("Everything else"), rest, ThemeManager.CategoryHex(Core.Settings.AppCategory.System)));
        return parts;
    }

    // ── Against your usual ──

    /// <summary>
    /// The period against what is normal for this PC: a week, a month, or a custom range of a week or more (a day is
    /// Home's PC health to say). The temperatures, the fans, the PC's problems and the power cuts, each now and usually;
    /// only what moved is coloured.
    /// </summary>
    private List<UsualRow> _usualRows = [];
    public List<UsualRow> UsualRows => _usualRows = Kept.Or(_usualRows, UsualOf(Report));
    public bool HasUsual => UsualRows.Count > 0;

    /// <summary>The column of this period's figures: "THIS WEEK", "THIS MONTH", "THESE DAYS".</summary>
    public string UsualNow => Range switch
    {
        ReportRange.Week => IncludesNow ? Lang.T("THIS WEEK") : Lang.T("THAT WEEK"),
        ReportRange.Month => IncludesNow ? Lang.T("THIS MONTH") : Lang.T("THAT MONTH"),
        _ => Lang.T("THESE DAYS"),
    };

    internal static List<UsualRow> UsualOf(Report? r) => PcUsual.CardRows(r);

    /// <summary>
    /// Opened from a line of Home about the usual: the days it speaks of (a week, a month, or the 7 days Home's usual is
    /// read over), with their card in view.
    /// </summary>
    public void ShowUsual(ReportRange range, DateTime from, DateTime to)
    {
        WantUsual = true;
        if (range == ReportRange.Custom)
        {
            // The days first, then the range: one load.
            CustomFrom = from;
            CustomTo = to;
            Range = ReportRange.Custom;
        }
        else ShowPeriod(range, from);
        if (!IsLoading) OnPropertyChanged(nameof(UsualRows));
    }

    /// <summary>The page should bring the "Against your usual" card into view once it's there (the view does, then clears it).</summary>
    public bool WantUsual { get; set; }

    // ── What stood out ──

    // The totals are the tiles above: not said again as the first line.
    private List<Insight> _shownInsights = [];
    public List<Insight> ShownInsights => _shownInsights = Kept.Or(_shownInsights, [.. Insights.Where(i => i.Key != "screen")]);

    // ── What changed on the PC in the period ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    private IReadOnlyList<HomeLine> _changes = [];
    public bool HasChanges => Changes.Count > 0;

    /// <summary>How many changes the card lists (the Timeline has them all).</summary>
    public const int ChangesShown = 8;

    /// <summary>
    /// The period's changes, newest first; one that made something better or worse is always among them, with what it
    /// did under it as a link to its before and after.
    /// </summary>
    private IReadOnlyList<HomeLine> ChangeLines(IEnumerable<Core.Stability.SystemChange> changes, IReadOnlyDictionary<DateTime, ChangeResult> results)
    {
        var all = changes.ToList();
        ChangeResult? ResultOf(Core.Stability.SystemChange c) => results.TryGetValue(c.Time.Date, out var r) && r.Main == c ? r : null;
        var shown = all.Where(c => ResultOf(c) is not null).Concat(all.Where(c => ResultOf(c) is null).OrderByDescending(c => c.Time))
            .Take(ChangesShown).OrderByDescending(c => c.Time).ToList();
        var lines = ChangeLines(shown);
        return [.. lines.Select((line, i) => ResultOf(shown[i]) is { Short.Length: > 0 } r
            ? line with { Result = char.ToUpperInvariant(r.Short[0]) + r.Short[1..], ResultBrush = r.Tone == EffectTone.Worse ? "WarmBrush" : "GoodBrush", ChangeDay = r.Day }
            : line)];
    }

    /// <summary>Opens a change's before and after on the Timeline (set by the window).</summary>
    public Action<DateTime>? OpenChange { get; set; }

    [RelayCommand]
    private void OpenLine(HomeLine line)
    {
        if (line.ChangeDay is { } day) OpenChange?.Invoke(day);
    }

    private IReadOnlyList<HomeLine> ChangeLines(IEnumerable<Core.Stability.SystemChange> changes) =>
        HomeViewModel.ChangeLines(changes, ChangesShown, c =>
            c.Kind == Core.Stability.ChangeKind.Storage ? IsDay ? Lang.T("All day") : c.Time.ToString("ddd d MMM")
            : IsDay ? (c.IsApproximate ? "~" : "") + c.Time.ToString(TimeText.Fit("h:mm tt")) : c.Time.ToString(TimeText.Fit("ddd d MMM, h:mm tt")));

    partial void OnShowBackgroundAppsChanged(bool value) => SyncApps();

    private void SyncApps() => Kept.Sync(Apps, Report is null ? []
        : [.. Report.Apps.Where(a => UsedActively(a) || (ShowBackgroundApps && a.OpenSec >= 30))], Kept.Values);

    private List<Insight> _insights = [];
    public List<Insight> Insights => _insights = Kept.Or(_insights, Report?.Insights ?? []);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrashesShown), nameof(HasMoreCrashes), nameof(MoreCrashesText), nameof(ProblemsText), nameof(ProblemsLine), nameof(ProblemsTone))]
    private List<CrashRow> _crashes = [];

    // A month can list dozens of crashes. The list sits mid-page (more sections follow), so it starts short and grows
    // on request rather than as the page scrolls. (Apps scroll inside a box of their own instead.)
    private const int CrashesPage = 5;
    private int _crashLimit = CrashesPage;

    private List<CrashRow> _crashesShown = [];
    public List<CrashRow> CrashesShown => _crashesShown = Kept.Or(_crashesShown, [.. Crashes.Take(_crashLimit)],
        (a, b) => a.Event == b.Event && a.TimeText == b.TimeText && a.CpuBefore == b.CpuBefore && a.GpuBefore == b.GpuBefore && a.FrontApp == b.FrontApp);
    public bool HasMoreCrashes => Crashes.Count > _crashLimit;
    public string MoreCrashesText
    {
        get
        {
            int hidden = Crashes.Count - _crashLimit, next = Math.Min(20, hidden);
            return Lang.T($"Show {next} more ({hidden} not shown)");
        }
    }

    /// <summary>Opens a crash as its own case on the Crashes page (set by the window).</summary>
    public Action<DateTime>? OpenCrash { get; set; }

    /// <summary>Opens a game on the Games page, by its program (set by the window).</summary>
    public Action<string>? OpenGame { get; set; }

    [RelayCommand]
    private void OpenGameRow(string? exe)
    {
        if (exe is not null) OpenGame?.Invoke(exe);
    }

    [RelayCommand]
    private void OpenCase(CrashRow row) => OpenCrash?.Invoke(row.Time);

    [RelayCommand]
    private void ShowMoreCrashes()
    {
        _crashLimit += 20;
        OnPropertyChanged(nameof(CrashesShown));
        OnPropertyChanged(nameof(HasMoreCrashes));
        OnPropertyChanged(nameof(MoreCrashesText));
    }

    /// <summary>A day, or a custom range of up to two days: minute by minute (the timeline), not daily bars.</summary>
    public bool IsDay => Range == ReportRange.Day || (Range == ReportRange.Custom && CustomTo - CustomFrom <= ReportBuilder.DayLikeLimit);
    public bool IsMultiDay => !IsDay;
    /// <summary>A year: bars per month (built from the daily totals; see ReportBuilder.BuildLong).</summary>
    public bool IsYear => ReportBuilder.IsLong(Range, CustomFrom, CustomTo);
    public string BarsTitle => IsYear ? Lang.T("Active time per month") : Lang.T("Active time per day");

    /// <summary>A custom range's timeline ends with it; a day's is the whole day (null).</summary>
    public DateTime? TimelineEnd => Report is { Range: ReportRange.Custom } r ? r.To : null;

    /// <summary>The period shown includes now, so it can still change.</summary>
    public bool IncludesNow => Report is { } r && r.From <= DateTime.Now && DateTime.Now < r.To;

    /// <summary>A period in words ("Today", "Last week", "August 2026"): see <see cref="Controls.PeriodPicker.Text"/>.</summary>
    public static string PeriodText(ReportRange range, DateTime anchor) => Controls.PeriodPicker.Text(range, anchor);

    public string InsightsTitle => Report is { } r && r.From <= DateTime.Now && DateTime.Now < r.To ? Lang.T("What stands out so far") : Lang.T("What stood out");
    public bool HasData => Report is { HasData: true };

    /// <summary>The first period has been read: until then the page says it's loading, not that nothing was recorded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowLoading), nameof(EmptyTitle))]
    private bool _ready;

    public bool ShowEmpty => Ready && !HasData && !Switching;

    /// <summary>The period shown as it reads after a statement: "today", "last week", "in August 2026" (see <see cref="Controls.PeriodPicker.Words"/>).</summary>
    private string When => Controls.PeriodPicker.Words(Range, Anchor);

    /// <summary>What the page says when the period has nothing.</summary>
    public string EmptyTitle
    {
        get
        {
            string when = When;
            return Lang.T($"Not recorded {when}");
        }
    }

    /// <summary>
    /// Another period was picked and is taking a moment to read (a week or a month, the first time): the page shows
    /// that it's loading instead of the period it is leaving.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowData), nameof(ShowLoading), nameof(Title), nameof(ReportAsk), nameof(Subtitle))]
    private bool _switching;

    public bool ShowData => HasData && !Switching;
    public bool ShowLoading => !Ready || Switching;

    /// <summary>How long a newly picked period may take before the page says it's loading (one read at once never blanks it).</summary>
    internal static TimeSpan SwitchDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    // The period the page shows now (not the one being read).
    private (ReportRange Range, DateTime Anchor, DateTime From, DateTime To)? _shown;

    private async Task SwitchingAfterAsync(int id)
    {
        await Task.Delay(SwitchDelay);
        if (id == _loadId && IsLoading) Switching = true;
    }

    // While another period is read the heading is empty: the one it has is the period being left.
    public string Title => Switching ? "" : Report?.Title ?? "";
    /// <summary>The period's AI button: how it went, from the Reports page's records.</summary>
    public Services.AiQuestion? ReportAsk => Switching || Report is null ? null
        : new(Lang.T($"How did my PC do over this period ({Report.Title}), and is anything worth a look?"), Core.Ai.AiPages.Reports);

    public string Subtitle => Report is null || Switching ? "" : Range == ReportRange.Custom
        ? $"{Controls.PeriodPicker.Duration(Report.To - Report.From)}" : Controls.PeriodPicker.Span(Range, Report.From);

    /// <summary>Explains short weeks/months while Rigsight is new.</summary>
    public string? CoverageNote
    {
        get
        {
            if (Report is null || Range is ReportRange.Day or ReportRange.Custom || FirstDay is not { } since) return null;
            // The days of the period shown that history covers (not the days since history began: looking at last
            // month, those said "this month has 17 days" of a September with 11).
            bool inProgress = Report.To > DateTime.Today;
            var end = inProgress ? DateTime.Today.AddDays(1) : Report.To;
            if (since <= Report.From || since >= end) return null;
            int days = (int)(end - since).TotalDays;
            return Coverage(Range, inProgress, since, days);
        }
    }

    /// <summary>"History starts on 3 October, so this week has 4 days of data.", each sentence whole.</summary>
    private static string Coverage(ReportRange range, bool inProgress, DateTime since, int days) => (range, inProgress, days == 1) switch
    {
        (ReportRange.Week, true, true) => Lang.T($"History starts on {since:d MMMM}, so this week has {days} day of data."),
        (ReportRange.Week, true, false) => Lang.T($"History starts on {since:d MMMM}, so this week has {days} days of data."),
        (ReportRange.Week, false, true) => Lang.T($"History starts on {since:d MMMM}, so that week has {days} day of data."),
        (ReportRange.Week, false, false) => Lang.T($"History starts on {since:d MMMM}, so that week has {days} days of data."),
        (ReportRange.Year, true, true) => Lang.T($"History starts on {since:d MMMM}, so this year has {days} day of data."),
        (ReportRange.Year, true, false) => Lang.T($"History starts on {since:d MMMM}, so this year has {days} days of data."),
        (ReportRange.Year, false, true) => Lang.T($"History starts on {since:d MMMM}, so that year has {days} day of data."),
        (ReportRange.Year, false, false) => Lang.T($"History starts on {since:d MMMM}, so that year has {days} days of data."),
        (_, true, true) => Lang.T($"History starts on {since:d MMMM}, so this month has {days} day of data."),
        (_, true, false) => Lang.T($"History starts on {since:d MMMM}, so this month has {days} days of data."),
        (_, false, true) => Lang.T($"History starts on {since:d MMMM}, so that month has {days} day of data."),
        _ => Lang.T($"History starts on {since:d MMMM}, so that month has {days} days of data."),
    };

    private static bool UsedActively(AppStat a) => a.ActiveSec >= 30;

    /// <summary>Apps you actually used; background-only apps are added when the toggle is on.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AppStat> Apps { get; } = [];

    public int BackgroundOnlyCount => Report?.Apps.Count(a => !UsedActively(a) && a.OpenSec >= 30) ?? 0;

    public string BackgroundToggleText
    {
        get
        {
            int count = BackgroundOnlyCount;
            return Lang.T($"Show unused apps ({count})");
        }
    }

    public double MaxActive => Math.Max(1, Report?.Apps.FirstOrDefault()?.ActiveSec ?? 1);

    // Windows' own parts and Rigsight itself aren't what anyone means by a session.
    private List<SessionInfo> _topSessions = [];
    public List<SessionInfo> TopSessions => _topSessions = Kept.Or(_topSessions,
        Report?.Sessions.Where(s => s.Category != Core.Settings.AppCategory.System).OrderByDescending(s => s.ActiveSec).Take(10).ToList() ?? [], Kept.Values);

    private List<PeakRow> _peaks = [];

    /// <summary>The period's highs: temperatures, the graphics card's power, memory, the fastest download, then core voltages and CPU power.</summary>
    public List<PeakRow> Peaks => _peaks = Kept.Or(_peaks, BuildPeaks());

    private List<PeakRow> BuildPeaks()
    {
        var r = Report;
        if (r is null) return [];
        var rows = new List<PeakRow>();
        bool oneDay = IsDay && r.From.Date == r.To.AddTicks(-1).Date;
        string At(DateTime t) => oneDay ? t.ToString(TimeText.Fit("h:mm tt")) : IsDay ? t.ToString(TimeText.Fit("ddd h:mm tt")) : IsYear ? t.ToString(TimeText.Fit("d MMM, h:mm tt")) : t.ToString(TimeText.Fit("ddd d MMM, h:mm tt"));
        string When(Peak p) => At(p.Time);
        var tempBrush = new TempToBrushConverter();
        Brush TempBrush(double c) => (Brush)tempBrush.Convert(c, typeof(Brush), null, System.Globalization.CultureInfo.CurrentCulture);
        var plain = Controls.ChartPaint.TextBrush;

        if (r.CpuTempPeak is { } a) rows.Add(new(Lang.T("CPU temperature"), Units.TempShort(a.Value), When(a), a.App, TempBrush(a.Value)));
        if (r.GpuTempPeak is { } b) rows.Add(new(Lang.T("GPU temperature"), Units.TempShort(b.Value), When(b), b.App, TempBrush(b.Value)));
        if (r.GpuHotPeak is { } c) rows.Add(new(Lang.T("GPU hot spot"), Units.TempShort(c.Value), When(c), c.App, TempBrush(c.Value - 10)));
        if (r.GpuPowerPeak is { } g) rows.Add(new(Lang.T("GPU power (1-minute average)"), $"{g.Value:0} W", When(g), g.App, plain));
        if (r.RamPeak is { } m) rows.Add(new(Lang.T("Memory in use"), $"{m.Value:0.0} GB", When(m), m.App, plain));
        if (r.Net is { TopSpeed: > 0, TopSpeedAt: { } at } net) rows.Add(new(Lang.T("Fastest download"), Units.Speed(net.TopSpeed.Value), At(at), net.TopSpeedApp, plain));
        if (r.CpuVoltPeak is { } d) rows.Add(new(Lang.T("CPU core voltage"), $"{d.Value:0.000} V", When(d), d.App, plain));
        if (r.GpuVoltPeak is { } e) rows.Add(new(Lang.T("GPU core voltage"), $"{e.Value:0.000} V", When(e), e.App, plain));
        if (r.CpuPowerPeak is { } f) rows.Add(new(Lang.T("CPU power (1-minute average)"), $"{f.Value:0} W", When(f), f.App, plain));
        return rows;
    }

    partial void OnRangeChanged(ReportRange value) => _ = LoadAsync();
    partial void OnAnchorChanged(DateTime value) => _ = LoadAsync();
    /// <summary>Midnight passed with the window open (see <see cref="Controls.PeriodPicker.AfterMidnight"/>).</summary>
    public void NewDay(DateTime was)
    {
        var anchor = Controls.PeriodPicker.AfterMidnight(Range, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        // The same report, in the new day's words ("Yesterday" is a date now).
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ReportAsk));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(InsightsTitle));
        OnPropertyChanged(nameof(CoverageNote));
    }

    partial void OnCustomFromChanged(DateTime value) => CustomChanged();
    partial void OnCustomToChanged(DateTime value) => CustomChanged();

    // Both ends usually change together: one load for the pair.
    private bool _customPending;

    private void CustomChanged()
    {
        OnPropertyChanged(nameof(IsDay));
        OnPropertyChanged(nameof(IsMultiDay));
        OnPropertyChanged(nameof(IsYear));
        OnPropertyChanged(nameof(BarsTitle));
        if (Range != ReportRange.Custom || _customPending) return;
        _customPending = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            _customPending = false;
            _ = LoadAsync();
        });
    }

    private int _loadId;

    public async Task LoadAsync()
    {
        // Range and date can change together (e.g. "open yesterday"): only the latest load's results are shown.
        int id = ++_loadId;
        IsLoading = true;
        var range = Range;
        var anchor = Anchor;
        var (customFrom, customTo) = (CustomFrom, CustomTo);
        var period = (range, anchor, range == ReportRange.Custom ? customFrom : default, range == ReportRange.Custom ? customTo : default);
        if (Ready && _shown != period) _ = SwitchingAfterAsync(id);
        var first = await reports.FirstDayAsync();
        var report = range != ReportRange.Custom ? await reports.BuildAsync(range, anchor)
            : customTo > customFrom ? await reports.BuildCustomAsync(customFrom, customTo) : null;
        var crashes = report is null ? [] : await reports.CrashesAsync(report.From, report.To) ?? [];
        var changed = report is null ? [] : await reports.QueryAsync((db, _) =>
        {
            long from = Core.Data.TimeUtil.ToUnix(report.From), to = Core.Data.TimeUtil.ToUnix(report.To);
            var list = db.GetChanges(from, to);
            list.AddRange(Core.Stability.StorageChanges.From(db.GetDriveDays(from - 86400)).Where(c => c.Time >= report.From && c.Time < report.To));
            return list;
        }) ?? [];
        if (id != _loadId) return;
        // A different period starts with short lists again; the minute refresh of the same one keeps them open.
        if (report?.From != Report?.From || report?.To != Report?.To) _crashLimit = CrashesPage;
        FirstDay = first;
        Report = report;
        Crashes = crashes;
        Changes = Kept.Or(Changes, ChangeLines(changed, changed.Any(ChangeEffects.IsMajor) ? await reports.ChangeResultsAsync() : new Dictionary<DateTime, ChangeResult>()));
        _shown = period;
        IsLoading = false;
        Switching = false;
        Ready = true;
    }

    public void ShowDay(DateTime day)
    {
        Range = ReportRange.Day;
        Anchor = day.Date;
    }

    /// <summary>The report over <paramref name="first"/> to <paramref name="last"/> (both whole days), as a custom range.</summary>
    public void ShowDays(DateTime first, DateTime last)
    {
        // The days first, then the range: one load.
        CustomFrom = first.Date;
        CustomTo = last.Date.AddDays(1);
        Range = ReportRange.Custom;
    }

    /// <summary>The report for the period of <paramref name="anchor"/>: a week, a month, a year.</summary>
    public void ShowPeriod(ReportRange range, DateTime anchor)
    {
        Range = range;
        Anchor = anchor.Date;
    }
}

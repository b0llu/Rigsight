using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Converters;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A line of one of Home's small cards: an icon in its colour (a theme brush's name), what it says, and a few words under it.</summary>
public sealed record HomeLine(string Icon, string Brush, string Text, string? Detail = null)
{
    /// <summary>What this change made better or worse, as a link to it; only on a change that did.</summary>
    public string? Result { get; init; }
    public string ResultBrush { get; init; } = "MutedBrush";
    /// <summary>The day of the change whose before and after the line (or its <see cref="Result"/>) opens.</summary>
    public DateTime? ChangeDay { get; init; }

    /// <summary>When, at the line's right ("Yesterday"): Home's lines are one statement, with nothing under them.</summary>
    public string? When { get; init; }
    /// <summary>The text's colour when it needs a look (amber) or is good news; null: the plain text colour.</summary>
    public string? TextBrush { get; init; }
    /// <summary>The page the line opens (see <see cref="InsightLinks"/>); a line about a change opens that change.</summary>
    public InsightOpens Opens { get; init; }
    /// <summary>The insight the line says (null: a change, a drive).</summary>
    public string? Key { get; init; }
    /// <summary>The game the line is about, which it opens (see <see cref="Insight.Game"/>).</summary>
    public string? Game { get; init; }
    public bool CanOpen => Opens != InsightOpens.None || ChangeDay is not null;
}

/// <summary>
/// A row of the recap card's list: an app with its time, or a line about the period. The card shows as many as fit
/// its height, the lowest <see cref="Rank"/>s first; the <see cref="Essential"/> ones are the least it shows.
/// </summary>
/// <summary>
/// Where a line of Home leads: its page, on the days it speaks of (<see cref="From"/> to <see cref="To"/>, as a week, a
/// month, a day or a custom range), with only the PC's own problems when that's what it counted.
/// </summary>
public sealed record HomeOpen(InsightOpens Opens, ReportRange Range, DateTime From, DateTime To, bool PcOnly, string? Game = null);

public sealed record RecapRow(AppStat? App, Insight? Line, int Rank, bool Essential, bool StartsLines = false)
{
    public bool IsApp => App is not null;
}

/// <summary>The period the recap card on Home shows: yesterday, or the last whole week, month or year.</summary>
public enum RecapPeriod { Yesterday, LastWeek, LastMonth, LastYear }

public sealed partial class HomeViewModel(ReportService reports, LiveData live) : ObservableObject
{
    public LiveData Live { get; } = live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayTopApps), nameof(TodayTopMax), nameof(TodayInsights), nameof(DashboardInsights), nameof(HasTodayData), nameof(ShowLearning),
        nameof(TodayInternet), nameof(TodayInternetLine), nameof(ShowNoInsights))]
    private Report? _today;

    /// <summary>What was downloaded today, and by what mostly ("Mostly Google Chrome"); a dash before anything is.</summary>
    public string TodayInternet => Today?.Net is { Down: > 0 } net ? Units.Data(net.Down) : "—";
    public string TodayInternetLine => Today is null ? "" : Today.Net is { Down: > 0 } net
        ? net.Apps.MaxBy(a => a.Use.Down) is { } top && top.Use.Down >= net.Down * 0.4 ? Lang.T($"Mostly {top.Name}") : Lang.T("Downloaded")
        : Lang.T("Nothing downloaded yet");

    /// <summary>"Learning your day" only once history has loaded and really has no apps yet.</summary>
    public bool ShowLearning => Loaded && TodayTopApps.Count == 0;

    /// <summary>The recap card's report: the period picked (yesterday until another is).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecapTopApps), nameof(RecapHasData), nameof(RecapInsights), nameof(RecapEmptyTitle), nameof(RecapEmptyText),
        nameof(RecapRows), nameof(RecapTopMax), nameof(RecapInternet), nameof(ShowRecapEmpty), nameof(ShowRecapCpuBusiest))]
    private Report? _recap;

    /// <summary>
    /// The recap's period has no CPU temperature on a standard account: its line says how busy the CPU got instead (a
    /// period that has one, read while admin rights were on for a while, says its peak as usual).
    /// </summary>
    public bool ShowRecapCpuBusiest => Live.StandardAccount && Recap is { CpuTempPeak: null, CpuLoadPeak: not null };

    /// <summary>Yesterday's report (the recap card's first period; the dashboard's Yesterday tile shows it whatever the card shows).</summary>
    public Report? Yesterday => _recaps.GetValueOrDefault(RecapPeriod.Yesterday);
    public bool YesterdayHasData => Yesterday is { HasData: true };
    public List<AppStat> YesterdayTopApps => TopApps(Yesterday);

    private void YesterdayChanged()
    {
        OnPropertyChanged(nameof(Yesterday));
        OnPropertyChanged(nameof(YesterdayHasData));
        OnPropertyChanged(nameof(YesterdayTopApps));
    }

    private static List<AppStat> TopApps(Report? r, int count = 3) => r?.Apps.Where(a => a.ActiveSec >= 60 && a.Category != AppCategory.System).Take(count).ToList() ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLearning), nameof(ShowRecapEmpty), nameof(ShowNoInsights), nameof(ShowNoChanges))]
    private bool _loaded;

    // What a card says when it has nothing, only once the history has been read: until then it shows that it's loading.
    public bool ShowRecapEmpty => Loaded && !RecapLoading && !RecapHasData;
    public bool ShowNoInsights => Loaded && TodayInsights.Count == 0;
    public bool ShowNoChanges => Loaded && !HasChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecapTitle), nameof(RecapArg), nameof(RecapNote), nameof(IsYesterday), nameof(IsLastWeek), nameof(IsLastMonth), nameof(IsLastYear))]
    private RecapPeriod _period;

    /// <summary>The periods there's history for, in order (a period only once it's whole and something was recorded in it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastWeek), nameof(HasLastMonth), nameof(HasLastYear), nameof(HasPeriods))]
    private List<RecapPeriod> _periods = [RecapPeriod.Yesterday];

    public bool HasPeriods => Periods.Count > 1;
    public bool HasLastWeek => Periods.Contains(RecapPeriod.LastWeek);
    public bool HasLastMonth => Periods.Contains(RecapPeriod.LastMonth);
    public bool HasLastYear => Periods.Contains(RecapPeriod.LastYear);

    // The segmented control's buttons.
    public bool IsYesterday { get => Period == RecapPeriod.Yesterday; set { if (value) Period = RecapPeriod.Yesterday; } }
    public bool IsLastWeek { get => Period == RecapPeriod.LastWeek; set { if (value) Period = RecapPeriod.LastWeek; } }
    public bool IsLastMonth { get => Period == RecapPeriod.LastMonth; set { if (value) Period = RecapPeriod.LastMonth; } }
    public bool IsLastYear { get => Period == RecapPeriod.LastYear; set { if (value) Period = RecapPeriod.LastYear; } }

    /// <summary>
    /// The day the card's "Day" shows: yesterday, or, when the PC wasn't used yesterday, the last day it was (null:
    /// yesterday). A PC left off over a weekend opens on Friday's recap, not on an empty card.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecapTitle), nameof(RecapArg), nameof(RecapNote))]
    private DateTime? _lastUsedDay;

    /// <summary>The last day used stands in for yesterday (the "Day" recap, and that day isn't yesterday).</summary>
    private DateTime? StandIn => Period == RecapPeriod.Yesterday && LastUsedDay is { } day && day < DateTime.Today.AddDays(-1) ? day : null;

    public string RecapTitle => Period switch
    {
        RecapPeriod.LastWeek => Lang.T("Last week"),
        RecapPeriod.LastMonth => Lang.T("Last month"),
        RecapPeriod.LastYear => Lang.T("Last year"),
        // A day of the last week by its name; an older one by its date.
        _ when StandIn is { } day => (DateTime.Today - day).TotalDays <= 6 ? day.ToString("dddd") : day.ToString("d MMMM"),
        _ => Lang.T("Yesterday"),
    };

    /// <summary>Under the title when another day stands in for yesterday: why ("Not used for 2 days.").</summary>
    public string? RecapNote => StandIn is not { } day ? null
        : (int)(DateTime.Today - day).TotalDays - 1 is var idle && idle <= 1 ? Lang.T("Your PC wasn't used yesterday.") : Lang.T($"Your PC wasn't used for {idle} days.");

    /// <summary>What "Open full recap" asks the Reports page for (see ShellViewModel.Navigate).</summary>
    public string RecapArg => Period switch
    {
        RecapPeriod.LastWeek => "last-week",
        RecapPeriod.LastMonth => "last-month",
        RecapPeriod.LastYear => "last-year",
        _ when StandIn is { } day => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        _ => "yesterday",
    };

    // Each period's sentence whole; a day standing in for yesterday by its name.
    public string RecapEmptyTitle => Period switch
    {
        RecapPeriod.LastWeek => Lang.T("No history for last week yet."),
        RecapPeriod.LastMonth => Lang.T("No history for last month yet."),
        RecapPeriod.LastYear => Lang.T("No history for last year yet."),
        _ when StandIn is not null => DayEmptyTitle(RecapTitle.ToLowerInvariant()),
        _ => Lang.T("No history for yesterday yet."),
    };

    private static string DayEmptyTitle(string day) => Lang.T($"No history for {day} yet.");

    public string RecapEmptyText => Period switch
    {
        RecapPeriod.LastWeek => Lang.T("Last week's active time, most-used apps and highlights show here once there's history for it."),
        RecapPeriod.LastMonth => Lang.T("Last month's active time, most-used apps and highlights show here once there's history for it."),
        RecapPeriod.LastYear => Lang.T("Last year's active time, most-used apps and highlights show here once there's history for it."),
        _ when StandIn is not null => DayEmptyText(RecapTitle),
        _ => Lang.T("Yesterday's active time, most-used apps and highlights show here once there's a full day of history."),
    };

    private static string DayEmptyText(string day) => Lang.T($"{day}'s active time, most-used apps and highlights show here once there's history for it.");

    // The hours just after noon and in the early evening carry their own context: a language whose day splits
    // differently (Spanish keeps "buenos días" until lunch and "buenas tardes" until about nine) says them its own way.
    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => Lang.T("Up late"),
        < 12 => Lang.T("Good morning"),
        < 14 => Lang.C("midday", "Good afternoon"),
        < 17 => Lang.T("Good afternoon"),
        < 21 => Lang.C("early evening", "Good evening"),
        _ => Lang.T("Good evening"),
    };

    public string DateText => DateTime.Now.ToString("dddd, d MMMM");

    public bool HasTodayData => Today is { HasData: true };
    public List<AppStat> TodayTopApps => Today?.Apps.Where(a => a.ActiveSec >= 30 && a.Category != AppCategory.System).Take(6).ToList() ?? [];
    public double TodayTopMax => Math.Max(1, TodayTopApps.FirstOrDefault()?.ActiveSec ?? 1);
    // Home already shows the time totals and the most-used apps, so those lines are left out here.
    private static readonly HashSet<string> ShownElsewhere = ["screen", "top-app"];

    // Today's internet in a line is for looking back (the recap), not news while the day goes on. What PC health says
    // (a game hotter than usual, the PC's problems, a fan) isn't said here again: one fact, one line on Home. Each line is
    // read as its one statement (Insight.Line), opening a page only where it has more.
    private static readonly HashSet<string> InHealth = ["usual-heat", "usual-problems", "usual-power", "fan-stopped", "fan-slower", "fan-older"];
    public List<Insight> TodayInsights => Today?.Insights.Where(i => !ShownElsewhere.Contains(i.Key) && !InHealth.Contains(i.Key) && i.Key != "net-recap").Take(5).ToList() ?? [];
    /// <summary>A dashboard's highlights: today's lines with what PC health says on Home (a dashboard has no PC health).</summary>
    public List<Insight> DashboardInsights => Today?.Insights.Where(i => !ShownElsewhere.Contains(i.Key) && i.Key != "net-recap").Take(5).ToList() ?? [];

    public bool RecapHasData => Recap is { HasData: true };
    /// <summary>The most apps and lines the recap card lists (it shows as many of them as fit its height).</summary>
    public const int RecapMost = 8;

    public List<AppStat> RecapTopApps => TopApps(Recap, RecapMost);
    public double RecapTopMax => Math.Max(1, RecapTopApps.FirstOrDefault()?.ActiveSec ?? 1);
    /// <summary>What the period downloaded, on the card's line of figures (null: nothing recorded).</summary>
    public string? RecapInternet => Recap?.Net is { Down: > 0 } net ? Units.Data(net.Down) : null;

    /// <summary>The recap's lines, most telling first, the period's internet in a line always among the first three.</summary>
    public List<Insight> RecapInsights
    {
        get
        {
            if (Recap is null) return [];
            var net = Recap.Insights.FirstOrDefault(i => i.Key == "net-recap");
            var lines = Recap.Insights.Where(i => !ShownElsewhere.Contains(i.Key) && i.Key != "net-recap").Take(net is null ? RecapMost : RecapMost - 1).ToList();
            if (net is not null) lines.Insert(Math.Min(2, lines.Count), net);
            return lines;
        }
    }

    /// <summary>
    /// The card's list: the apps, then the lines. Three apps and two lines are the least it shows; with room to spare
    /// (the card is as tall as the one beside it) a line and an app more come in by turns.
    /// </summary>
    public List<RecapRow> RecapRows => RowsOf(RecapTopApps, RecapInsights);

    internal static List<RecapRow> RowsOf(IReadOnlyList<AppStat> apps, IReadOnlyList<Insight> lines)
    {
        const int LeastApps = 3, LeastLines = 2;
        int AppRank(int i) => i < LeastApps ? i : LeastApps + LeastLines + (i - LeastApps) * 2 + 1;
        int LineRank(int i) => i < LeastLines ? LeastApps + i : LeastApps + LeastLines + (i - LeastLines) * 2;
        return [.. apps.Select((a, i) => new RecapRow(a, null, AppRank(i), i < LeastApps)),
                .. lines.Select((l, i) => new RecapRow(null, l, LineRank(i), i < LeastLines, StartsLines: i == 0))];
    }

    private readonly Dictionary<RecapPeriod, Report?> _recaps = [];
    private DateTime _recapsFor;
    private int _recapLoad;

    /// <summary>The period's bounds: a whole one, ending before today.</summary>
    public static (ReportRange Range, DateTime Anchor) Bounds(RecapPeriod period, DateTime today) => period switch
    {
        RecapPeriod.LastWeek => (ReportRange.Week, today.AddDays(-7)),
        RecapPeriod.LastMonth => (ReportRange.Month, new DateTime(today.Year, today.Month, 1).AddDays(-1)),
        RecapPeriod.LastYear => (ReportRange.Year, new DateTime(today.Year - 1, 1, 1)),
        _ => (ReportRange.Day, today.AddDays(-1)),
    };

    /// <summary>The periods to offer, from when history starts: each once it's whole and history reaches into it.</summary>
    public static List<RecapPeriod> PeriodsFor(DateTime? firstDay, DateTime today)
    {
        var list = new List<RecapPeriod> { RecapPeriod.Yesterday };
        if (firstDay is not { } first) return list;
        foreach (var period in new[] { RecapPeriod.LastWeek, RecapPeriod.LastMonth, RecapPeriod.LastYear })
        {
            var (range, anchor) = Bounds(period, today);
            var (from, to) = ReportBuilder.Bounds(range, anchor);
            if (first < to && to <= today) list.Add(period);
        }
        return list;
    }

    partial void OnPeriodChanged(RecapPeriod value) => _ = LoadRecapAsync();

    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(DateText));
        Today = await reports.BuildAsync(ReportRange.Day, DateTime.Today);
        await LoadCardsAsync();
        if (_recapsFor != DateTime.Today)
        {
            _recapsFor = DateTime.Today;
            _recaps.Clear();
            LastUsedDay = null;
            _dayRecap = LoadDayRecapAsync();
        }
        // Another refresh may be the one reading the recap (the window asks again once the agent answers): this one
        // is done when that is. It used to say "loaded" on its own, and Home said there was no history for yesterday
        // until the recap came in.
        if (_dayRecap is { } reading) await reading;
        Loaded = true;
    }

    private Task? _dayRecap;

    private async Task LoadDayRecapAsync()
    {
        Periods = PeriodsFor(await reports.FirstDayAsync(), DateTime.Today);
        if (!Periods.Contains(Period)) Period = RecapPeriod.Yesterday;
        await LoadRecapAsync();
        YesterdayChanged();
    }

    // ── The two small cards beside "What stands out today" ──

    /// <summary>The latest changes on the Timeline, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(ShowNoChanges))]
    private IReadOnlyList<HomeLine> _changes = [];
    public bool HasChanges => Changes.Count > 0;

    /// <summary>A line each for crashes, drives and fans: fine, or what needs a look.</summary>
    [ObservableProperty] private IReadOnlyList<HomeLine> _health = [];

    /// <summary>How many lines each small card holds.</summary>
    public const int CardLines = 4;

    private async Task LoadCardsAsync()
    {
        var now = DateTime.Now;
        var read = await reports.QueryAsync((db, s) =>
        {
            long from = TimeUtil.ToUnix(now.Date.AddDays(-30)), to = TimeUtil.ToUnix(now.AddDays(1));
            var changes = db.GetChanges(from, to);
            changes.AddRange(StorageChanges.From(db.GetDriveDays(from - 86400)).Where(c => c.Time >= now.Date.AddDays(-30)));
            var crashes = db.GetCrashes(TimeUtil.ToUnix(now.Date.AddDays(-6)), to).Where(e => !s.IsCrashMuted(e.AppExe)).ToList();
            // A shut-off that looks like the power is no fault of the PC's: counted, and said, apart (PcUsual.ShutOffOf).
            var found = CrashCases.Of(db, crashes, s);
            bool Mains(CrashEvent e) => PcUsual.LooksLikePower(e, found.GetValueOrDefault(e.Id));
            return (Changes: changes, Problems: crashes.Where(e => PcUsual.CountsAsPcProblem(e, found.GetValueOrDefault(e.Id))).Select(e => e.Kind).ToList(), Mains: crashes.Count(Mains),
                MainsLikely: crashes.Count(e => PcUsual.ShutOffOf(e, found.GetValueOrDefault(e.Id)) == ShutOff.PowerLikely));
        });
        var results = await reports.ChangeResultsAsync();
        var changeLines = HomeChangeLines(read.Changes ?? [], now.Date, results);
        // One fact, one line: a change that made things worse, on top of "What changed lately", isn't said again here.
        var worse = WorseLine(results, now.Date);
        if (worse is not null && changeLines.Any(l => l.ChangeDay == worse.ChangeDay)) worse = null;
        Changes = Kept.Or(Changes, changeLines);
        Health = Kept.Or(Health, HealthLines(Today, read.Problems ?? [], read.Mains, Live.Drives, FullestVolume(), worse, read.MainsLikely));
    }

    /// <summary>How long the latest change that made a difference stays on top of "What changed lately".</summary>
    internal const int PinDays = Core.Reports.ChangeEffects.WindowDays;

    /// <summary>
    /// PC health's line for a change that made things worse, when it's likely the change and still recent: the only
    /// result Home calls out on its own.
    /// </summary>
    internal static HomeLine? WorseLine(IReadOnlyDictionary<DateTime, Core.Reports.ChangeResult> results, DateTime today) =>
        results.Values.Where(r => r is { Tone: Core.Reports.EffectTone.Worse, Sure: Confidence.Likely } && r.Day >= today.AddDays(-PinDays)).MaxBy(r => r.Day) is { } r
            ? new HomeLine("\uE7BA", "WarmBrush", $"{ChangeName(r.Main)}: {r.Plain}") { ChangeDay = r.Day, TextBrush = "WarmBrush" }
            : null;

    /// <summary>
    /// "What changed lately" as Home reads it: each change in a few words with when at the right; the latest big change
    /// that made something better or worse stays on top for two weeks as what it did ("Driver 618.10: Crimson Desert runs
    /// 10° hotter on the GPU"), opening its before and after.
    /// </summary>
    internal static List<HomeLine> HomeChangeLines(IEnumerable<SystemChange> changes, DateTime today, IReadOnlyDictionary<DateTime, Core.Reports.ChangeResult> results)
    {
        var list = changes.ToList();
        var pinned = results.Values.Where(r => r.Day >= today.AddDays(-PinDays) && list.Contains(r.Main)).MaxBy(r => r.Day);
        // Shortened on the saved English, then shown in the language in use.
        var rest = ChangeLines(list.Where(c => c != pinned?.Main), pinned is null ? CardLines : CardLines - 1, c => DayWord(c.Time.Date, today),
                text: c => SavedText.Show(Brief(c.Title)))
            .Select((line, i) => line with { Detail = null, When = line.Detail }).ToList();
        if (pinned is null) return rest;
        var top = ChangeLines([pinned.Main], 1, c => DayWord(c.Time.Date, today))[0] with
        {
            Text = $"{ChangeName(pinned.Main)}: {pinned.Plain}", Detail = null, When = DayWord(pinned.Day, today), ChangeDay = pinned.Day,
            TextBrush = pinned.Tone == Core.Reports.EffectTone.Worse ? "WarmBrush" : "GoodBrush",
        };
        return [top, .. rest];
    }

    /// <summary>A change in a few words: an app update without its version ("Google Chrome updated").</summary>
    internal static string Brief(string title) =>
        title.IndexOf(" updated to ", StringComparison.Ordinal) is > 0 and var at ? title[..(at + " updated".Length)] : title;

    /// <summary>A big change by a short name, before what it did: "Driver 618.10", else its title.</summary>
    internal static string ChangeName(SystemChange c) => c.IsGraphicsDriver && c.Now is { Length: > 0 } version ? Lang.T($"Driver {version}") : SavedText.Show(Brief(c.Title));

    /// <summary>Opens a change's before and after on the Timeline.</summary>
    public Action<DateTime>? OpenChange { get; set; }

    /// <summary>Opens the page a line leads to (set by the window).</summary>
    public Action<HomeOpen>? Open { get; set; }

    // PC health speaks of the last 7 days (today's usual); today's lines of today; the recap's of its period.
    [RelayCommand]
    private void OpenLine(HomeLine line)
    {
        if (line.ChangeDay is { } day) OpenChange?.Invoke(day);
        else if (line.Opens != InsightOpens.None) Open?.Invoke(OpenOf(line.Opens, line.Key, Today) with { Game = line.Game });
    }

    [RelayCommand]
    private void OpenInsight(Insight line)
    {
        if (line.Opens != InsightOpens.None) Open?.Invoke(OpenOf(line.Opens, line.Key, Today) with { Game = line.Game });
    }

    [RelayCommand]
    private void OpenRecapInsight(Insight line)
    {
        if (line.Opens != InsightOpens.None) Open?.Invoke(OpenOf(line.Opens, line.Key, Recap) with { Game = line.Game });
    }

    /// <summary>
    /// Where a line of <paramref name="r"/> leads: a crash or an app's crashes on its own period; what a day says against
    /// the usual, or of the PC's problems, on the 7 days to its end (as PcUsual reads a day); a week's or month's on it.
    /// </summary>
    internal static HomeOpen OpenOf(InsightOpens opens, string? key, Report? r)
    {
        var (range, from, to) = r is null ? (ReportRange.Day, DateTime.Today, DateTime.Today.AddDays(1)) : (r.Range, r.From, r.To);
        if (key is not ("crash" or "app-crashes") && ReportBuilder.IsDayLike(range, from, to))
        {
            var end = to.TimeOfDay == TimeSpan.Zero ? to : to.Date.AddDays(1);
            (range, from, to) = (ReportRange.Custom, end.AddDays(-7), end);
        }
        return new HomeOpen(opens, range, from, to, key is "pc-problems" or "usual-problems" or "usual-power");
    }

    /// <summary>The latest <paramref name="count"/> changes, newest first, each with its icon, its colour and what <paramref name="detail"/> says under it.</summary>
    internal static List<HomeLine> ChangeLines(IEnumerable<SystemChange> changes, int count, Func<SystemChange, string> detail, Func<SystemChange, string>? text = null) =>
        [.. changes.OrderByDescending(c => c.Time).Take(count).Select(c =>
        {
            var (icon, brush) = c.Kind switch
            {
                ChangeKind.Driver => ("\uE950", "GpuBrush"),
                ChangeKind.WindowsUpdate or ChangeKind.Windows => ("\uE777", "GpuBrush"),
                ChangeKind.AppInstalled => ("\uE896", "CpuBrush"),
                ChangeKind.AppRemoved => ("\uE74D", "CpuBrush"),
                ChangeKind.AppUpdated => ("\uE72C", "CpuBrush"),
                ChangeKind.GameUpdated => ("\uE7FC", "CpuBrush"),
                ChangeKind.Hardware or ChangeKind.Firmware => ("\uE964", "PurpleBrush"),
                ChangeKind.Storage => ("\uEDA2", "PurpleBrush"),
                _ => ("\uE713", "MutedBrush"),
            };
            return new HomeLine(icon, brush, text?.Invoke(c) ?? c.Shown, detail(c));
        })];

    /// <summary>"Today", "Yesterday", the day's name within a week, else its date.</summary>
    internal static string DayWord(DateTime day, DateTime today) => (today - day).Days switch
    {
        <= 0 => Lang.T("Today"),
        1 => Lang.T("Yesterday"),
        < 7 => day.ToString("dddd"),
        _ => day.ToString("d MMM"),
    };

    /// <summary>The fixed drive that is fullest by share, in words ("the C drive is 58% full"); null if none can be read.</summary>
    private static string? FullestVolume()
    {
        try
        {
            var fullest = DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true, TotalSize: > 0 })
                .MaxBy(d => 1 - (double)d.TotalFreeSpace / d.TotalSize);
            return fullest is null ? null : FullText(fullest.Name, fullest.TotalSize - fullest.TotalFreeSpace, fullest.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string FullText(string root, long usedBytes, long totalBytes)
    {
        string letter = root.TrimEnd('\\').TrimEnd(':');
        double share = Math.Round(100.0 * usedBytes / totalBytes);
        return Lang.T($"the {letter} drive is {share:0}% full");
    }

    /// <summary>
    /// PC health, a statement a line: the PC's own parts against its usual, worded once in Core (UsualLines.Health, the
    /// same words as the insight list), then the drives. Where a line opens comes from InsightLinks alone. A change that
    /// made things worse is said here unless PC health already says what it did (a game hotter than usual): then the
    /// change itself is in "What changed lately", and saying it twice here would be the same fact again.
    /// </summary>
    /// <param name="mainsLikely">Of <paramref name="mains"/>, the likely power cuts (the rest were possibly the power); null: all of them.</param>
    internal static List<HomeLine> HealthLines(Report? today, IReadOnlyCollection<CrashKind> problems, int mains, IEnumerable<DriveSummary> drives, string? fullest, HomeLine? worseChange, int? mainsLikely = null)
    {
        var parts = UsualLines.Health(today, problems, mains, mainsLikely);

        // Drives: one that needs a look, else healthy and the fullest.
        var known = drives.Where(d => d.HasHealth).ToList();
        if (known.FirstOrDefault(d => d.HealthReason is not null) is { } unwell)
            parts.Add(new Insight("", Lang.T($"{unwell.Name} needs a look"), InsightTone.Warn, "drive-unwell"));
        else if (known.Count > 0)
            parts.Add(new Insight("", DrivesHealthy(known.Count, fullest), InsightTone.Good, "drives"));
        else if (fullest is not null)
            parts.Add(new Insight("", Wording.Capital(fullest), InsightTone.Neutral, "drives"));

        var lines = InsightLinks.Apply(parts).Select(i => new HomeLine(i.Icon, InsightBrushConverter.KeyOf(i), i.Line)
            { TextBrush = i.Tone is InsightTone.Warn or InsightTone.Hot ? "WarmBrush" : null, Opens = i.Opens, Key = i.Key, Game = i.Game }).ToList();
        if (worseChange is not null && !parts.Any(i => i is { Key: "usual-heat", Tone: InsightTone.Warn })) lines.Insert(0, worseChange);
        // What needs a look first, in the order above.
        return [.. lines.Where(l => l.TextBrush == "WarmBrush"), .. lines.Where(l => l.TextBrush != "WarmBrush")];
    }

    /// <summary>"The drive is healthy" / "Drives healthy", with the fullest drive after it when it's known.</summary>
    private static string DrivesHealthy(int count, string? fullest) => (count == 1, fullest) switch
    {
        (true, null) => Lang.T("The drive is healthy"),
        (true, _) => Lang.T($"The drive is healthy; {fullest}"),
        (false, null) => Lang.T("Drives healthy"),
        _ => Lang.T($"Drives healthy; {fullest}"),
    };

    /// <summary>The picked period's report, built once a day and kept; only the latest pick's result is shown.</summary>
    private async Task LoadRecapAsync()
    {
        var period = Period;
        int id = ++_recapLoad;
        if (!_recaps.TryGetValue(period, out var report))
        {
            // The card's title is the new period's already: its figures are still the last one's until this is read.
            RecapLoading = true;
            var (range, anchor) = Bounds(period, DateTime.Today);
            report = await reports.BuildAsync(range, anchor);
            // Nothing yesterday: the last day the PC was used, if there is one.
            if (period == RecapPeriod.Yesterday && report is not { HasData: true })
            {
                LastUsedDay = await reports.LastUsedDayAsync(DateTime.Today);
                if (LastUsedDay is { } day && await reports.BuildAsync(ReportRange.Day, day) is { HasData: true } last) report = last;
                else LastUsedDay = null;
            }
            _recaps[period] = report;
            if (period == RecapPeriod.Yesterday) YesterdayChanged();
        }
        if (id != _recapLoad) return;
        Recap = report;
        RecapLoading = false;
    }

    /// <summary>
    /// A period was just picked and its report isn't read yet: the card hides the last period's figures (they keep
    /// their room, so nothing moves) and shows a loader over them.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowRecapEmpty))] private bool _recapLoading;
}

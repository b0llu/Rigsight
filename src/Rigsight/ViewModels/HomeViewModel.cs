using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
}

/// <summary>
/// A row of the recap card's list: an app with its time, or a line about the period. The card shows as many as fit
/// its height, the lowest <see cref="Rank"/>s first; the <see cref="Essential"/> ones are the least it shows.
/// </summary>
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
    [NotifyPropertyChangedFor(nameof(TodayTopApps), nameof(TodayTopMax), nameof(TodayInsights), nameof(HasTodayData), nameof(ShowLearning),
        nameof(TodayInternet), nameof(TodayInternetLine), nameof(ShowNoInsights))]
    private Report? _today;

    /// <summary>What was downloaded today, and by what mostly ("Mostly Google Chrome"); a dash before anything is.</summary>
    public string TodayInternet => Today?.Net is { Down: > 0 } net ? Units.Data(net.Down) : "—";
    public string TodayInternetLine => Today is null ? "" : Today.Net is { Down: > 0 } net
        ? net.Apps.MaxBy(a => a.Use.Down) is { } top && top.Use.Down >= net.Down * 0.4 ? $"Mostly {top.Name}" : "Downloaded"
        : "Nothing downloaded yet";

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
        RecapPeriod.LastWeek => "Last week",
        RecapPeriod.LastMonth => "Last month",
        RecapPeriod.LastYear => "Last year",
        // A day of the last week by its name; an older one by its date.
        _ when StandIn is { } day => (DateTime.Today - day).TotalDays <= 6 ? day.ToString("dddd") : day.ToString("d MMMM"),
        _ => "Yesterday",
    };

    /// <summary>Under the title when another day stands in for yesterday: why ("Not used for 2 days.").</summary>
    public string? RecapNote => StandIn is not { } day ? null
        : (int)(DateTime.Today - day).TotalDays - 1 is var idle && idle <= 1 ? "Your PC wasn't used yesterday." : $"Your PC wasn't used for {idle} days.";

    /// <summary>What "Open full recap" asks the Reports page for (see ShellViewModel.Navigate).</summary>
    public string RecapArg => Period switch
    {
        RecapPeriod.LastWeek => "last-week",
        RecapPeriod.LastMonth => "last-month",
        RecapPeriod.LastYear => "last-year",
        _ when StandIn is { } day => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        _ => "yesterday",
    };

    public string RecapEmptyTitle => $"No history for {RecapTitle.ToLowerInvariant()} yet.";
    public string RecapEmptyText => Period == RecapPeriod.Yesterday
        ? "Yesterday's active time, most-used apps and highlights show here once there's a full day of history."
        : $"{RecapTitle}'s active time, most-used apps and highlights show here once there's history for it.";

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Up late",
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening",
    };

    public string DateText => DateTime.Now.ToString("dddd, d MMMM");

    public bool HasTodayData => Today is { HasData: true };
    public List<AppStat> TodayTopApps => Today?.Apps.Where(a => a.ActiveSec >= 30 && a.Category != AppCategory.System).Take(6).ToList() ?? [];
    public double TodayTopMax => Math.Max(1, TodayTopApps.FirstOrDefault()?.ActiveSec ?? 1);
    // Home already shows the time totals and the most-used apps, so those lines are left out here.
    private static readonly HashSet<string> ShownElsewhere = ["screen", "top-app"];

    // Today's internet in a line is for looking back (the recap), not news while the day goes on.
    public List<Insight> TodayInsights => Today?.Insights.Where(i => !ShownElsewhere.Contains(i.Key) && i.Key != "net-recap").Take(5).ToList() ?? [];

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
            // A shut-off that was most likely the mains is no fault of the PC's: counted, and said, apart.
            var found = CrashCases.Of(db, crashes, s);
            bool Mains(CrashEvent e) => IsPcDown(e) && found.GetValueOrDefault(e.Id) is { Outside: true };
            return (Changes: changes, Shutdowns: crashes.Count(e => IsPcDown(e) && !Mains(e)), Crashes: crashes.Count(e => !IsPcDown(e)), Mains: crashes.Count(Mains));
        });
        var results = await reports.ChangeResultsAsync();
        Changes = Kept.Or(Changes, ChangeLines(read.Changes ?? [], now.Date, results));
        var health = HealthLines(read.Shutdowns, read.Crashes, Live.Drives, FullestVolume(),
            Today?.Insights.FirstOrDefault(i => i.Key is "fan-stopped" or "fan-slower" or "fan-older")?.Text,
            Live.AllSensors.Count(x => x.Kind == SensorKind.Fan && x.Value >= FanCard.MinSpinRpm), read.Mains);
        if (WorseLine(results, now.Date) is { } worse) health.Insert(Math.Min(1, health.Count), worse);
        Health = Kept.Or(Health, health);
    }

    /// <summary>The PC itself went down (a blue screen, a sudden shut-off while running), not one app.</summary>
    private static bool IsPcDown(CrashEvent e) =>
        e.Kind == CrashKind.SystemCrash || e is { Kind: CrashKind.UnexpectedShutdown, Moment: PowerMoment.Running };

    internal static List<HomeLine> ChangeLines(IEnumerable<SystemChange> changes, DateTime today) =>
        ChangeLines(changes, CardLines, c => DayWord(c.Time.Date, today));

    /// <summary>How long the latest change that made a difference stays on top of "What changed lately".</summary>
    internal const int PinDays = Core.Reports.ChangeEffects.WindowDays;

    /// <summary>
    /// The latest changes, newest first; the latest big change that made something better or worse stays on top for its
    /// first two weeks, with what it did (an old driver that made games hotter is still the news after three app updates).
    /// </summary>
    internal static List<HomeLine> ChangeLines(IEnumerable<SystemChange> changes, DateTime today, IReadOnlyDictionary<DateTime, Core.Reports.ChangeResult> results)
    {
        var list = changes.ToList();
        var pinned = results.Values.Where(r => r.Day >= today.AddDays(-PinDays) && list.Contains(r.Main)).MaxBy(r => r.Day);
        if (pinned is null) return ChangeLines(list, today);
        var top = ChangeLines([pinned.Main], 1, c => DayWord(c.Time.Date, today))[0] with
        {
            Result = Capital(pinned.Short), ResultBrush = pinned.Tone == Core.Reports.EffectTone.Worse ? "WarmBrush" : "GoodBrush", ChangeDay = pinned.Day,
        };
        return [top, .. ChangeLines(list.Where(c => c != pinned.Main), CardLines - 1, c => DayWord(c.Time.Date, today))];
    }

    /// <summary>
    /// PC health's line for a change that made things worse, when it's likely the change and still recent: the only
    /// result Home calls out on its own.
    /// </summary>
    internal static HomeLine? WorseLine(IReadOnlyDictionary<DateTime, Core.Reports.ChangeResult> results, DateTime today) =>
        results.Values.Where(r => r is { Tone: Core.Reports.EffectTone.Worse, Sure: Confidence.Likely } && r.Day >= today.AddDays(-PinDays)).MaxBy(r => r.Day) is { } r
            ? new HomeLine("\uE7BA", "WarmBrush", r.Headline, r.HowSure) { ChangeDay = r.Day }
            : null;

    /// <summary>Opens a change's before and after on the Timeline.</summary>
    public Action<DateTime>? OpenChange { get; set; }

    [RelayCommand]
    private void OpenLine(HomeLine line)
    {
        if (line.ChangeDay is { } day) OpenChange?.Invoke(day);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The latest <paramref name="count"/> changes, newest first, each with its icon, its colour and what <paramref name="detail"/> says under it.</summary>
    internal static List<HomeLine> ChangeLines(IEnumerable<SystemChange> changes, int count, Func<SystemChange, string> detail) =>
        [.. changes.OrderByDescending(c => c.Time).Take(count).Select(c =>
        {
            var (icon, brush) = c.Kind switch
            {
                ChangeKind.Driver => ("\uE950", "GpuBrush"),
                ChangeKind.WindowsUpdate or ChangeKind.Windows => ("\uE777", "GpuBrush"),
                ChangeKind.AppInstalled => ("\uE896", "CpuBrush"),
                ChangeKind.AppRemoved => ("\uE74D", "CpuBrush"),
                ChangeKind.AppUpdated => ("\uE72C", "CpuBrush"),
                ChangeKind.Hardware or ChangeKind.Firmware => ("\uE964", "PurpleBrush"),
                ChangeKind.Storage => ("\uEDA2", "PurpleBrush"),
                _ => ("\uE713", "MutedBrush"),
            };
            return new HomeLine(icon, brush, c.Title, detail(c));
        })];

    /// <summary>"Today", "Yesterday", the day's name within a week, else its date.</summary>
    internal static string DayWord(DateTime day, DateTime today) => (today - day).Days switch
    {
        <= 0 => "Today",
        1 => "Yesterday",
        < 7 => day.ToString("dddd"),
        _ => day.ToString("d MMM"),
    };

    /// <summary>The fixed drive that is fullest by share, in words ("C drive: 272 of 465 GB used"); null if none can be read.</summary>
    private static string? FullestVolume()
    {
        try
        {
            var fullest = DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true, TotalSize: > 0 })
                .MaxBy(d => 1 - (double)d.TotalFreeSpace / d.TotalSize);
            return fullest is null ? null : VolumeText(fullest.Name, fullest.TotalSize - fullest.TotalFreeSpace, fullest.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string VolumeText(string root, long usedBytes, long totalBytes) =>
        $"{root.TrimEnd('\\').TrimEnd(':')} drive: {usedBytes / 1073741824.0:0} of {totalBytes / 1073741824.0:0} GB used";

    /// <param name="mains">Shut-offs that were most likely power cuts (not among <paramref name="shutdowns"/>).</param>
    internal static List<HomeLine> HealthLines(int shutdowns, int crashes, IEnumerable<DriveSummary> drives, string? fullest, string? fanWarning, int fansSpinning, int mains = 0)
    {
        const string Week = "In the last 7 days";
        var lines = new List<HomeLine>();
        static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
        if (shutdowns + crashes + mains == 0) lines.Add(new("\uE73E", "GoodBrush", Core.Wording.NoProblems, Week));
        else
            // Only likely power cuts: said, without the warning's colour.
            lines.Add(new(shutdowns + crashes == 0 ? "\uE7E8" : "\uE7BA", shutdowns + crashes == 0 ? "MutedBrush" : "WarmBrush", string.Join(", ", new[]
            {
                shutdowns > 0 ? Count(shutdowns, "blue screen or sudden shut-off", "blue screens or sudden shut-offs") : null,
                mains > 0 ? Count(mains, "likely power cut", "likely power cuts") : null,
                crashes > 0 ? Count(crashes, "app crash", "app crashes") : null,
            }.Where(t => t is not null)), Week));

        var known = drives.Where(d => d.HasHealth).ToList();
        if (known.FirstOrDefault(d => d.HealthReason is not null) is { } unwell)
            lines.Add(new("\uEDA2", "WarmBrush", $"{unwell.Name} needs a look", unwell.HealthReason));
        else if (known.Count > 0 || fullest is not null)
            lines.Add(new("\uEDA2", "GoodBrush", known.Count > 0 ? known.Count == 1 ? "The drive is healthy" : "All drives healthy" : "Drive space", fullest));

        if (fanWarning is not null) lines.Add(new("\uE7BA", "WarmBrush", fanWarning));
        else if (fansSpinning > 0) lines.Add(new("\uE73E", "GoodBrush", "Fans as usual", fansSpinning == 1 ? "1 spinning now" : $"{fansSpinning} spinning now"));
        return lines;
    }

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

using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>The period the recap card on Home shows: yesterday, or the last whole week, month or year.</summary>
public enum RecapPeriod { Yesterday, LastWeek, LastMonth, LastYear }

public sealed partial class HomeViewModel(ReportService reports, LiveData live) : ObservableObject
{
    public LiveData Live { get; } = live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayTopApps), nameof(TodayTopMax), nameof(TodayInsights), nameof(HasTodayData), nameof(ShowLearning))]
    private Report? _today;

    /// <summary>"Learning your day" only once history has loaded and really has no apps yet.</summary>
    public bool ShowLearning => Loaded && TodayTopApps.Count == 0;

    /// <summary>The recap card's report: the period picked (yesterday until another is).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecapTopApps), nameof(RecapHasData), nameof(RecapInsights), nameof(RecapEmptyTitle), nameof(RecapEmptyText))]
    private Report? _recap;

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

    private static List<AppStat> TopApps(Report? r) => r?.Apps.Where(a => a.ActiveSec >= 60 && a.Category != AppCategory.System).Take(3).ToList() ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLearning))]
    private bool _loaded;

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
    public List<AppStat> RecapTopApps => TopApps(Recap);
    /// <summary>The recap's top lines, always ending with the period's internet in a line when there is one.</summary>
    public List<Insight> RecapInsights
    {
        get
        {
            if (Recap is null) return [];
            var net = Recap.Insights.FirstOrDefault(i => i.Key == "net-recap");
            var top = Recap.Insights.Where(i => !ShownElsewhere.Contains(i.Key) && i.Key != "net-recap").Take(net is null ? 3 : 2).ToList();
            if (net is not null) top.Add(net);
            return top;
        }
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
        if (_recapsFor != DateTime.Today)
        {
            _recapsFor = DateTime.Today;
            _recaps.Clear();
            LastUsedDay = null;
            Periods = PeriodsFor(await reports.FirstDayAsync(), DateTime.Today);
            if (!Periods.Contains(Period)) Period = RecapPeriod.Yesterday;
            await LoadRecapAsync();
            YesterdayChanged();
        }
        Loaded = true;
    }

    /// <summary>The picked period's report, built once a day and kept; only the latest pick's result is shown.</summary>
    private async Task LoadRecapAsync()
    {
        var period = Period;
        int id = ++_recapLoad;
        if (!_recaps.TryGetValue(period, out var report))
        {
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
        if (id == _recapLoad) Recap = report;
    }
}

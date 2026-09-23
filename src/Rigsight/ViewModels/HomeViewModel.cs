using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

public sealed partial class HomeViewModel(ReportService reports, LiveData live) : ObservableObject
{
    public LiveData Live { get; } = live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayTopApps), nameof(TodayTopMax), nameof(TodayInsights), nameof(HasTodayData))]
    private Report? _today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YesterdayTopApps), nameof(YesterdayHasData), nameof(YesterdayInsights))]
    private Report? _yesterday;

    [ObservableProperty] private bool _loaded;

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
    public List<Insight> TodayInsights => Today?.Insights.Skip(1).Take(5).ToList() ?? [];

    public bool YesterdayHasData => Yesterday is { HasData: true };
    public List<AppStat> YesterdayTopApps => Yesterday?.Apps.Where(a => a.ActiveSec >= 60 && a.Category != AppCategory.System).Take(3).ToList() ?? [];
    public List<Insight> YesterdayInsights => Yesterday?.Insights.Skip(2).Take(3).ToList() ?? [];

    private DateTime _yesterdayLoadedFor;

    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(DateText));
        Today = await reports.BuildAsync(ReportRange.Day, DateTime.Today);
        if (_yesterdayLoadedFor != DateTime.Today)
        {
            Yesterday = await reports.BuildAsync(ReportRange.Day, DateTime.Today.AddDays(-1));
            _yesterdayLoadedFor = DateTime.Today;
        }
        Loaded = true;
    }
}

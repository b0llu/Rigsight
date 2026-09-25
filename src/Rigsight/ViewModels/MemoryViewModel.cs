using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core.Reports;
using Rigsight.Services;

namespace Rigsight.ViewModels;

public sealed partial class MemoryViewModel(ReportService reports, LiveData live) : ObservableObject
{
    public LiveData Live { get; } = live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayMemoryMax))]
    private List<AppStat> _todayTop = [];

    public double TodayMemoryMax => Math.Max(1, TodayTop.FirstOrDefault()?.MemMax ?? 1);

    public async Task RefreshAsync()
    {
        var today = await reports.BuildRangeAsync(DateTime.Today, DateTime.Today.AddDays(1));
        TodayTop = today?.Apps.Where(a => a.MemMax is not null).OrderByDescending(a => a.MemMax).Take(10).ToList() ?? [];
    }
}

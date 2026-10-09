using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core.Reports;
using Rigsight.Services;

namespace Rigsight.ViewModels;

public sealed partial class MemoryViewModel(ReportService reports, LiveData live) : ObservableObject
{
    public LiveData Live { get; } = live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayMemoryMax), nameof(ShowCollecting))]
    private List<AppStat> _todayTop = [];

    /// <summary>Today has been read: until then its card says it's loading, not that there is nothing yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollecting))]
    private bool _ready;

    public bool ShowCollecting => Ready && TodayTop.Count == 0;

    public double TodayMemoryMax => Math.Max(1, TodayTop.FirstOrDefault()?.MemMax ?? 1);

    public async Task RefreshAsync()
    {
        var today = await reports.BuildRangeAsync(DateTime.Today, DateTime.Today.AddDays(1));
        TodayTop = Models.Kept.Or(TodayTop, today?.Apps.Where(a => a.MemMax is not null).OrderByDescending(a => a.MemMax).Take(10).ToList() ?? [], Models.Kept.Values);
        Ready = true;
    }
}

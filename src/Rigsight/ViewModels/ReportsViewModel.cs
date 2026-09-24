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

    /// <summary>The first day with any history (the period picker starts there).</summary>
    [ObservableProperty] private DateTime? _firstDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(Apps), nameof(HasMoreApps), nameof(MoreAppsText), nameof(Peaks), nameof(TopSessions), nameof(HasData),
        nameof(MaxActive), nameof(BackgroundOnlyCount), nameof(BackgroundToggleText), nameof(CoverageNote), nameof(InsightsTitle))]
    private Report? _report;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverageNote))]
    private int _trackedDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Apps), nameof(HasMoreApps), nameof(MoreAppsText))]
    private bool _showBackgroundApps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrashesShown), nameof(HasMoreCrashes), nameof(MoreCrashesText))]
    private List<CrashRow> _crashes = [];

    // A month can list hundreds of apps and dozens of crashes. Both lists sit mid-page (more sections follow),
    // so they start short and grow on request rather than as the page scrolls.
    private const int AppsPage = 20, CrashesPage = 5;
    private int _appLimit = AppsPage, _crashLimit = CrashesPage;

    public List<CrashRow> CrashesShown => [.. Crashes.Take(_crashLimit)];
    public bool HasMoreCrashes => Crashes.Count > _crashLimit;
    public string MoreCrashesText => $"Show {Math.Min(20, Crashes.Count - _crashLimit)} more ({Crashes.Count - _crashLimit} not shown)";

    [RelayCommand]
    private void ShowMoreCrashes()
    {
        _crashLimit += 20;
        OnPropertyChanged(nameof(CrashesShown));
        OnPropertyChanged(nameof(HasMoreCrashes));
        OnPropertyChanged(nameof(MoreCrashesText));
    }

    private List<AppStat> AllApps => Report is null ? [] :
        [.. Report.Apps.Where(a => UsedActively(a) || (ShowBackgroundApps && a.OpenSec >= 30))];
    public bool HasMoreApps => AllApps.Count > _appLimit;
    public string MoreAppsText
    {
        get
        {
            int left = AllApps.Count - _appLimit;
            return $"Show {Math.Min(AppsPage * 2, left)} more ({left} not shown)";
        }
    }

    [RelayCommand]
    private void ShowMoreApps()
    {
        _appLimit += AppsPage * 2;
        OnPropertyChanged(nameof(Apps));
        OnPropertyChanged(nameof(HasMoreApps));
        OnPropertyChanged(nameof(MoreAppsText));
    }

    public bool IsDay => Range == ReportRange.Day;
    public bool IsMultiDay => !IsDay;
    /// <summary>A year: bars per month (built from the daily totals; see ReportBuilder.BuildLong).</summary>
    public bool IsYear => ReportBuilder.IsLong(Range);
    public string BarsTitle => IsYear ? "Active time per month" : "Active time per day";

    /// <summary>The period shown includes now, so it can still change.</summary>
    public bool IncludesNow => Report is { } r && r.From <= DateTime.Now && DateTime.Now < r.To;

    /// <summary>A period in words ("Today", "Last week", "August 2026"): see <see cref="Controls.PeriodPicker.Text"/>.</summary>
    public static string PeriodText(ReportRange range, DateTime anchor) => Controls.PeriodPicker.Text(range, anchor);

    public string InsightsTitle => Report is { } r && r.From <= DateTime.Now && DateTime.Now < r.To ? "What stands out so far" : "What stood out";
    public bool HasData => Report is { HasData: true };

    public string Title => Report?.Title ?? "";

    public string Subtitle => Report is null ? "" : Controls.PeriodPicker.Span(Range, Report.From);

    /// <summary>Explains short weeks/months while Rigsight is new.</summary>
    public string? CoverageNote
    {
        get
        {
            if (Report is null || Range == ReportRange.Day || TrackedDays <= 0) return null;
            int days = (int)((Report.To > DateTime.Today ? DateTime.Today.AddDays(1) : Report.To) - Report.From).TotalDays;
            if (TrackedDays >= days) return null;
            var since = FirstDay ?? DateTime.Today.AddDays(-(TrackedDays - 1));
            string period = Range switch { ReportRange.Week => "week", ReportRange.Year => "year", _ => "month" };
            return $"History starts on {since:d MMMM}, so this {period} has {TrackedDays} day{(TrackedDays == 1 ? "" : "s")} of data.";
        }
    }

    private static bool UsedActively(AppStat a) => a.ActiveSec >= 30;

    /// <summary>Apps you actually used; background-only apps are added when the toggle is on.</summary>
    public List<AppStat> Apps => [.. AllApps.Take(_appLimit)];

    public int BackgroundOnlyCount => Report?.Apps.Count(a => !UsedActively(a) && a.OpenSec >= 30) ?? 0;

    public string BackgroundToggleText => $"Show unused apps ({BackgroundOnlyCount})";

    public double MaxActive => Math.Max(1, Report?.Apps.FirstOrDefault()?.ActiveSec ?? 1);

    // Windows' own parts and Rigsight itself aren't what anyone means by a session.
    public List<SessionInfo> TopSessions => Report?.Sessions.Where(s => s.Category != Core.Settings.AppCategory.System).OrderByDescending(s => s.ActiveSec).Take(10).ToList() ?? [];

    public List<PeakRow> Peaks
    {
        get
        {
            var r = Report;
            if (r is null) return [];
            var rows = new List<PeakRow>();
            string When(Peak p) => IsDay ? p.Time.ToString("h:mm tt") : IsYear ? p.Time.ToString("d MMM, h:mm tt") : p.Time.ToString("ddd d MMM, h:mm tt");
            var tempBrush = new TempToBrushConverter();
            Brush TempBrush(double c) => (Brush)tempBrush.Convert(c, typeof(Brush), null, System.Globalization.CultureInfo.CurrentCulture);

            if (r.CpuTempPeak is { } a) rows.Add(new("CPU temperature", Units.TempShort(a.Value), When(a), a.App, TempBrush(a.Value)));
            if (r.GpuTempPeak is { } b) rows.Add(new("GPU temperature", Units.TempShort(b.Value), When(b), b.App, TempBrush(b.Value)));
            if (r.GpuHotPeak is { } c) rows.Add(new("GPU hot spot", Units.TempShort(c.Value), When(c), c.App, TempBrush(c.Value - 10)));
            if (r.CpuVoltPeak is { } d) rows.Add(new("CPU core voltage", $"{d.Value:0.000} V", When(d), d.App, TempToBrushConverter.Cool));
            if (r.GpuVoltPeak is { } e) rows.Add(new("GPU core voltage", $"{e.Value:0.000} V", When(e), e.App, TempToBrushConverter.Cool));
            if (r.CpuPowerPeak is { } f) rows.Add(new("CPU power (1-min avg)", $"{f.Value:0} W", When(f), f.App, TempToBrushConverter.Warm));
            if (r.GpuPowerPeak is { } g) rows.Add(new("GPU power (1-min avg)", $"{g.Value:0} W", When(g), g.App, TempToBrushConverter.Warm));
            return rows;
        }
    }

    partial void OnRangeChanged(ReportRange value) => _ = LoadAsync();
    partial void OnAnchorChanged(DateTime value) => _ = LoadAsync();

    private int _loadId;

    public async Task LoadAsync()
    {
        // Range and date can change together (e.g. "open yesterday"): only the latest load's results are shown.
        int id = ++_loadId;
        IsLoading = true;
        var range = Range;
        var anchor = Anchor;
        var tracked = await reports.TrackedDaysAsync();
        var first = await reports.FirstDayAsync();
        var report = await reports.BuildAsync(range, anchor);
        var crashes = report is null ? [] : await reports.CrashesAsync(report.From, report.To) ?? [];
        if (id != _loadId) return;
        // A different period starts with short lists again; the minute refresh of the same one keeps them open.
        if (report?.From != Report?.From || report?.To != Report?.To) (_appLimit, _crashLimit) = (AppsPage, CrashesPage);
        TrackedDays = tracked;
        FirstDay = first;
        Report = report;
        Crashes = crashes;
        IsLoading = false;
    }

    public void ShowDay(DateTime day)
    {
        Range = ReportRange.Day;
        Anchor = day.Date;
    }

}

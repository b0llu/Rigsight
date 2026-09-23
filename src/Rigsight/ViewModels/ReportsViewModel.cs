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
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(IsMultiDay))]
    private ReportRange _range = ReportRange.Day;

    [ObservableProperty] private DateTime _anchor = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(Apps), nameof(Peaks), nameof(TopSessions), nameof(HasData),
        nameof(MaxActive), nameof(CanGoNext), nameof(BackgroundOnlyCount), nameof(BackgroundToggleText), nameof(CoverageNote))]
    private Report? _report;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverageNote))]
    private int _trackedDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Apps))]
    private bool _showBackgroundApps;

    [ObservableProperty] private List<CrashRow> _crashes = [];

    public bool IsDay => Range == ReportRange.Day;
    public bool IsMultiDay => !IsDay;
    public bool HasData => Report is { HasData: true };

    public string Title => Report?.Title ?? "";

    public string Subtitle => Report is null ? "" : Range switch
    {
        ReportRange.Day => Report.From.ToString("dddd, d MMMM yyyy"),
        _ => $"{Report.From:d MMM} – {Report.To.AddDays(-1):d MMM yyyy}",
    };

    /// <summary>Explains short weeks/months while Rigsight is new.</summary>
    public string? CoverageNote
    {
        get
        {
            if (Report is null || Range == ReportRange.Day || TrackedDays <= 0) return null;
            int days = (int)(Report.To - Report.From).TotalDays;
            if (TrackedDays >= days) return null;
            var since = DateTime.Today.AddDays(-(TrackedDays - 1));
            return $"Rigsight started tracking on {since:d MMMM}, so this {(Range == ReportRange.Week ? "week" : "month")} only includes {TrackedDays} day{(TrackedDays == 1 ? "" : "s")} so far.";
        }
    }

    public bool CanGoNext => Report is not null && Report.To <= DateTime.Today;

    private static bool UsedActively(AppStat a) => a.ActiveSec >= 30;

    /// <summary>Apps you actually used; background-only apps are added when the toggle is on.</summary>
    public List<AppStat> Apps => Report is null ? [] :
        [.. Report.Apps.Where(a => UsedActively(a) || (ShowBackgroundApps && a.OpenSec >= 30))];

    public int BackgroundOnlyCount => Report?.Apps.Count(a => !UsedActively(a) && a.OpenSec >= 30) ?? 0;

    public string BackgroundToggleText => $"Show unused apps ({BackgroundOnlyCount})";

    public double MaxActive => Math.Max(1, Report?.Apps.FirstOrDefault()?.ActiveSec ?? 1);

    public List<SessionInfo> TopSessions => Report?.Sessions.OrderByDescending(s => s.ActiveSec).Take(10).ToList() ?? [];

    public List<PeakRow> Peaks
    {
        get
        {
            var r = Report;
            if (r is null) return [];
            var rows = new List<PeakRow>();
            string When(Peak p) => IsDay ? p.Time.ToString("h:mm tt") : p.Time.ToString("ddd d MMM, h:mm tt");
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

    public async Task LoadAsync()
    {
        IsLoading = true;
        TrackedDays = await reports.TrackedDaysAsync();
        var report = await reports.BuildAsync(Range, Anchor);
        Report = report;
        Crashes = report is null ? [] : await reports.CrashesAsync(report.From, report.To) ?? [];
        IsLoading = false;
    }

    public void ShowDay(DateTime day)
    {
        Range = ReportRange.Day;
        Anchor = day.Date;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void SetRange(string range) => Range = Enum.Parse<ReportRange>(range);

    [RelayCommand]
    private void Previous() => Anchor = ReportBuilder.Previous(Range, Anchor);

    [RelayCommand]
    private void Next()
    {
        var next = ReportBuilder.Next(Range, Anchor);
        if (ReportBuilder.Bounds(Range, next).From <= DateTime.Today) Anchor = next;
    }

    [RelayCommand]
    private void GoToday() => Anchor = DateTime.Today;
}

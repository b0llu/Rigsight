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
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(IsMultiDay), nameof(PeriodLabel), nameof(IsMonth), nameof(CanGoPrevious), nameof(InsightsTitle))]
    private ReportRange _range = ReportRange.Day;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodLabel), nameof(CanGoPrevious))]
    private DateTime _anchor = DateTime.Today;

    /// <summary>The first day with any history (the date picker starts there).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    private DateTime? _firstDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(Apps), nameof(Peaks), nameof(TopSessions), nameof(HasData),
        nameof(MaxActive), nameof(CanGoNext), nameof(BackgroundOnlyCount), nameof(BackgroundToggleText), nameof(CoverageNote), nameof(InsightsTitle))]
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
    public bool IsMonth => Range == ReportRange.Month;

    /// <summary>Which period is shown, in words: "Today", "Last week", "Mon, 21 Sep", "August 2026".</summary>
    public string PeriodLabel => PeriodText(Range, Anchor);

    public static string PeriodText(ReportRange range, DateTime anchor)
    {
        var (from, to) = ReportBuilder.Bounds(range, anchor);
        var (thisFrom, _) = ReportBuilder.Bounds(range, DateTime.Today);
        var (lastFrom, _) = ReportBuilder.Bounds(range, ReportBuilder.Previous(range, DateTime.Today));
        return range switch
        {
            ReportRange.Day when from == thisFrom => "Today",
            ReportRange.Day when from == lastFrom => "Yesterday",
            ReportRange.Day => from.Year == DateTime.Today.Year ? from.ToString("ddd, d MMM") : from.ToString("d MMM yyyy"),
            ReportRange.Week when from == thisFrom => "This week",
            ReportRange.Week when from == lastFrom => "Last week",
            ReportRange.Week => from.Month == to.AddDays(-1).Month
                ? $"{from:%d}–{to.AddDays(-1):d MMM}"
                : $"{from:d MMM} – {to.AddDays(-1):d MMM}",
            _ when from == thisFrom => "This month",
            _ when from == lastFrom => "Last month",
            _ => from.ToString("MMMM yyyy"),
        };
    }

    /// <summary>Nothing before the first recorded day to go back to.</summary>
    public bool CanGoPrevious => FirstDay is not { } first || ReportBuilder.Bounds(Range, Anchor).From > first;

    public string InsightsTitle => Report is { } r && r.From <= DateTime.Now && DateTime.Now < r.To ? "What stands out so far" : "What stood out";
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
            var since = FirstDay ?? DateTime.Today.AddDays(-(TrackedDays - 1));
            return $"History starts on {since:d MMMM}, so this {(Range == ReportRange.Week ? "week" : "month")} has {TrackedDays} day{(TrackedDays == 1 ? "" : "s")} of data.";
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

    // Windows' own parts and Rigsight itself aren't what anyone means by a session.
    public List<SessionInfo> TopSessions => Report?.Sessions.Where(s => s.Category != Core.Settings.AppCategory.System).OrderByDescending(s => s.ActiveSec).Take(10).ToList() ?? [];

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

    [RelayCommand]
    private void SetRange(string range) => Range = Enum.Parse<ReportRange>(range);

    [RelayCommand]
    private void Previous()
    {
        if (CanGoPrevious) Anchor = ReportBuilder.Previous(Range, Anchor);
    }

    [RelayCommand]
    private void Next()
    {
        var next = ReportBuilder.Next(Range, Anchor);
        if (ReportBuilder.Bounds(Range, next).From <= DateTime.Today) Anchor = next;
    }
}

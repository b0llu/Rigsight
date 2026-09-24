using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>An app in the list, with the value used for its bar under the current sort.</summary>
public sealed record AppListRow(AppStat Stat, double Bar, string Metric);

public sealed partial class AppsViewModel(ReportService reports, SettingsModel settings) : ObservableObject
{
    private List<AppStat> _all = [];
    private Report? _report;
    private string? _pendingSelection;

    public ObservableCollection<AppListRow> Apps { get; } = [];
    public IReadOnlyList<AppCategory> Categories { get; } = Enum.GetValues<AppCategory>();

    /// <summary>"day" (one day, <see cref="Day"/>), "7" or "30" days up to today, or "all".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(RangeNote))]
    private string _range = "7";

    /// <summary>The day shown when <see cref="Range"/> is "day".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DayLabel), nameof(CanGoNextDay), nameof(CanGoPreviousDay), nameof(RangeNote))]
    private DateTime _day = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPreviousDay), nameof(RangeNote))]
    private DateTime? _firstDay;

    public bool IsDay => Range == "day";

    /// <summary>Every range but an earlier single day includes today, so it can still change.</summary>
    public bool IncludesToday => !IsDay || Day >= DateTime.Today;
    public string DayLabel => ReportsViewModel.PeriodText(ReportRange.Day, Day);
    public bool CanGoNextDay => Day < DateTime.Today;
    public bool CanGoPreviousDay => FirstDay is not { } f || Day > f;

    /// <summary>Which dates the list covers, and for "All time" when tracking started.</summary>
    public string RangeNote
    {
        get
        {
            var today = DateTime.Today;
            string Span(DateTime from) => from.Year == today.Year ? $"{from:d MMM} – {today:d MMM}" : $"{from:d MMM yyyy} – {today:d MMM yyyy}";
            return Range switch
            {
                "day" => Day.ToString("dddd, d MMMM yyyy"),
                "30" => Span(today.AddDays(-29)),
                "all" when FirstDay is { } f => $"{Span(f)} ({(int)(today - f).TotalDays + 1} days)",
                "all" => "All history",
                _ => Span(today.AddDays(-6)),
            };
        }
    }

    [RelayCommand]
    private void PreviousDay()
    {
        if (CanGoPreviousDay) Day = Day.AddDays(-1);
    }

    [RelayCommand]
    private void NextDay()
    {
        if (CanGoNextDay) Day = Day.AddDays(1);
    }
    [ObservableProperty] private string _sort = "Active";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private double _maxValue = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedSessions), nameof(SelectedAlias), nameof(SelectedCategory), nameof(SelectedExcluded))]
    private AppStat? _selected;

    [ObservableProperty] private AppListRow? _selectedRow;

    partial void OnSelectedRowChanged(AppListRow? value)
    {
        if (value is not null) Selected = value.Stat;
    }

    [ObservableProperty] private List<DayBucket>? _selectedDaily;

    public bool HasSelection => Selected is not null;

    public List<SessionInfo> SelectedSessions => Selected is null || _report is null ? [] :
        [.. _report.Sessions.Where(s => s.AppId == Selected.Id).OrderByDescending(s => s.Start).Take(12)];

    public string SelectedAlias
    {
        get => Selected is null ? "" : settings.Current.AppNames.GetValueOrDefault(Selected.Exe, Selected.Name);
        set
        {
            if (Selected is null) return;
            var exe = Selected.Exe;
            var trimmed = value?.Trim();
            settings.Update(s =>
            {
                if (string.IsNullOrEmpty(trimmed)) s.AppNames.Remove(exe);
                else s.AppNames[exe] = trimmed;
            });
        }
    }

    public AppCategory SelectedCategory
    {
        get => Selected is null ? AppCategory.Other : settings.Current.AppCategories.GetValueOrDefault(Selected.Exe, Selected.Category);
        set
        {
            if (Selected is null) return;
            var exe = Selected.Exe;
            settings.Update(s => s.AppCategories[exe] = value);
        }
    }

    public bool SelectedExcluded
    {
        get => Selected is not null && settings.Current.Tracking.ExcludedApps.Contains(Selected.Exe, StringComparer.OrdinalIgnoreCase);
        set
        {
            if (Selected is null) return;
            var exe = Selected.Exe;
            settings.Update(s =>
            {
                s.Tracking.ExcludedApps.RemoveAll(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase));
                if (value) s.Tracking.ExcludedApps.Add(exe);
            });
            OnPropertyChanged();
        }
    }

    partial void OnRangeChanged(string value) => _ = LoadAsync();
    partial void OnDayChanged(DateTime value)
    {
        if (IsDay) _ = LoadAsync();
    }
    partial void OnSortChanged(string value) => ApplyView();
    partial void OnSearchChanged(string value) => ApplyView();

    partial void OnSelectedChanged(AppStat? oldValue, AppStat? newValue)
    {
        // The same app re-read by the minute refresh keeps its chart until the new one is ready (no flicker).
        if (oldValue?.Id != newValue?.Id) SelectedDaily = null;
        if (newValue is not null) _ = LoadDailyAsync(newValue);
    }

    private async Task LoadDailyAsync(AppStat app)
    {
        var days = await reports.AppDailyAsync(app.Id, app.Category, 14);
        if (Selected == app) SelectedDaily = days;
    }

    public void SelectExe(string exe)
    {
        _pendingSelection = exe;
        var match = Apps.FirstOrDefault(a => a.Stat.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            SelectedRow = match;
            _pendingSelection = null;
        }
    }

    private int _loadId;

    public async Task LoadAsync()
    {
        int id = ++_loadId;
        var today = DateTime.Today;
        if (Day > today) Day = today;
        var (from, to) = Range switch
        {
            "day" => (Day.Date, Day.Date.AddDays(1)),
            "30" => (today.AddDays(-29), today.AddDays(1)),
            "all" => (today.AddYears(-20), today.AddDays(1)),
            _ => (today.AddDays(-6), today.AddDays(1)),
        };
        var first = await reports.FirstDayAsync();
        var report = await reports.BuildRangeAsync(from, to);
        if (id != _loadId) return; // a newer range or day was picked meanwhile
        FirstDay = first;
        _report = report;
        _all = _report?.Apps.Where(a => a.OpenSec >= 30 || a.MemMax >= 150).ToList() ?? [];
        var keep = _pendingSelection ?? Selected?.Exe;
        ApplyView();
        if (keep is not null) SelectExe(keep);
        else if (Selected is null && Apps.Count > 0) SelectedRow = Apps[0];
        OnPropertyChanged(nameof(SelectedSessions));
    }

    private double Key(AppStat a) => Sort switch
    {
        "GpuTemp" => a.GpuTempAvg ?? -1,
        "CpuTemp" => a.CpuTempAvg ?? -1,
        "Memory" => a.MemMax ?? -1,
        "Background" => a.BackgroundSec + a.MinimizedSec,
        "Cpu" => a.CpuAvg ?? -1,
        _ => a.ActiveSec,
    };

    private void ApplyView()
    {
        var q = Search?.Trim() ?? "";
        var list = _all
            .Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Exe.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(a => Sort switch
            {
                "GpuTemp" or "CpuTemp" => a.ActiveSec >= 60,
                "Memory" or "Cpu" => true,
                // Time-based sorts: hide background services you never actually had open.
                _ => a.OpenSec >= 30,
            })
            .OrderByDescending(Key)
            .ToList();
        var selected = Selected;
        double max = Math.Max(1e-6, list.Count > 0 ? Key(list[0]) : 1);
        // Update rows in place rather than clearing the list, so a refresh doesn't scroll it back to the top.
        var rows = list.Select(a => new AppListRow(a, Math.Max(0, Key(a)) / max * 100, Metric(a))).ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < Apps.Count) Apps[i] = rows[i];
            else Apps.Add(rows[i]);
        }
        while (Apps.Count > rows.Count) Apps.RemoveAt(Apps.Count - 1);
        MaxValue = max;
        if (selected is not null && Apps.FirstOrDefault(r => r.Stat.Id == selected.Id) is { } row) SelectedRow = row;
    }

    private string Metric(AppStat a) => Sort switch
    {
        "GpuTemp" => Core.Units.TempShort(a.GpuTempAvg),
        "CpuTemp" => Core.Units.TempShort(a.CpuTempAvg),
        "Memory" => a.MemMax is double m ? Core.Units.Megabytes(m) : "—",
        "Background" => Core.Units.Duration(a.BackgroundSec + a.MinimizedSec),
        "Cpu" => a.CpuAvg is double c ? $"{c:0.0}%" : "—",
        _ => Core.Units.Duration(a.ActiveSec),
    };
}

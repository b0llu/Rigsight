using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    /// <summary>"1" today, "7", "30" days, or "all".</summary>
    [ObservableProperty] private string _range = "7";
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
    partial void OnSortChanged(string value) => ApplyView();
    partial void OnSearchChanged(string value) => ApplyView();

    partial void OnSelectedChanged(AppStat? value)
    {
        SelectedDaily = null;
        if (value is not null) _ = LoadDailyAsync(value);
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

    public async Task LoadAsync()
    {
        var to = DateTime.Today.AddDays(1);
        var from = Range switch
        {
            "1" => DateTime.Today,
            "30" => DateTime.Today.AddDays(-29),
            "all" => DateTime.Today.AddYears(-20),
            _ => DateTime.Today.AddDays(-6),
        };
        _report = await reports.BuildRangeAsync(from, to);
        _all = _report?.Apps.Where(a => a.OpenSec >= 30 || a.MemMax >= 150).ToList() ?? [];
        var keep = _pendingSelection ?? Selected?.Exe;
        ApplyView();
        if (keep is not null) SelectExe(keep);
        else if (Selected is null && Apps.Count > 0) SelectedRow = Apps[0];
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
        Apps.Clear();
        foreach (var a in list) Apps.Add(new AppListRow(a, Math.Max(0, Key(a)) / max * 100, Metric(a)));
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

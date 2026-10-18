using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>An app in the list, with the value used for its bar under the current sort.</summary>
public sealed record AppListRow(AppStat Stat, double Bar, string Metric);

/// <summary>A choice in the category list above the apps: every app (<see cref="Category"/> null) or one category.</summary>
public sealed record CategoryFilter(AppCategory? Category, string Label)
{
    public override string ToString() => Label; // its name for screen readers (and UI automation)
}

public sealed partial class AppsViewModel(ReportService reports, SettingsModel settings) : ObservableObject
{
    private List<AppStat> _all = [];
    private Report? _report;

    /// <summary>Each app's internet use over the period shown, as the Network page reads it for the same period.</summary>
    private AppInternet _net = AppInternet.None;

    /// <summary>
    /// Apps in the list only for their internet use (a command-line tool, a VPN: no window, so no time): listed under
    /// "Most internet use" alone, so the list holds every app the Network page does.
    /// </summary>
    private readonly HashSet<long> _netOnly = [];
    private string? _pendingSelection;

    public ObservableCollection<AppListRow> Apps { get; } = [];
    public IReadOnlyList<AppCategory> Categories { get; } = Enum.GetValues<AppCategory>();

    private static readonly CategoryFilter AllApps = new(null, "All categories");

    /// <summary>"All categories", then each category that has an app in the period (Other last).</summary>
    public ObservableCollection<CategoryFilter> Filters { get; } = [AllApps];

    [ObservableProperty] private CategoryFilter? _filter = AllApps;

    private bool _rebuildingFilters;

    partial void OnFilterChanged(CategoryFilter? value)
    {
        if (_rebuildingFilters) return;
        if (value is null) { Filter = AllApps; return; }
        ApplyView();
    }

    private AppCategory CategoryOf(AppStat a) => settings.Current.AppCategories.GetValueOrDefault(a.Exe, a.Category);

    /// <summary>
    /// The period shown (see <see cref="Controls.PeriodPicker"/>): today unless picked otherwise, like Home. A week by
    /// default put last week's highs beside Home's today's ones, and they looked like they disagreed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(RangeNote), nameof(IncludesToday))]
    private ReportRange _unit = ReportRange.Day;

    /// <summary>Any day in the period shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IncludesToday))]
    private DateTime _anchor = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote))]
    private DateTime? _firstDay;

    /// <summary>A custom range's start and end (whole hours), when <see cref="Unit"/> is Custom.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(RangeNote), nameof(IncludesToday), nameof(ChartTitle))]
    private DateTime _customFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(RangeNote), nameof(IncludesToday), nameof(ChartTitle))]
    private DateTime _customTo;

    private (DateTime From, DateTime To) Period => Controls.PeriodPicker.Bounds(Unit, Anchor, CustomFrom, CustomTo);

    public bool IsDay => ReportBuilder.IsDayLike(Unit, Period.From, Period.To);

    /// <summary>The period shown includes today, so it can still change.</summary>
    public bool IncludesToday => Period.To > DateTime.Today;

    /// <summary>Which dates the list covers ("All time" from when tracking started).</summary>
    public string RangeNote => Controls.PeriodPicker.Span(Unit, Anchor, FirstDay, CustomFrom, CustomTo);
    [ObservableProperty] private string _sort = "Active";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private double _maxValue = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowEmpty), nameof(EmptyText), nameof(SelectedAlias), nameof(SelectedCategory), nameof(SelectedExcluded), nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(InternetMain), nameof(InternetWord), nameof(InternetDown), nameof(InternetUp), nameof(InternetBackground))]
    private AppStat? _selected;

    /// <summary>The selected app's internet use over the period, or null when it moved nothing (or none was recorded).</summary>
    private NetAppUse? SelectedNet => Selected is { } a ? _net.Use.GetValueOrDefault(a.Id) : null;

    /// <summary>The Internet group's main figure: "3.5 GB" (then "used"), "None", or "Not recorded" when the period has no internet use on record.</summary>
    public string InternetMain => SelectedNet is { } u ? Core.Units.Data(u.Total) : _net.Recorded ? "None" : "Not recorded";

    /// <summary>The muted word after the main figure: "used" after an amount, nothing after "None".</summary>
    public string InternetWord => SelectedNet is null ? "" : "used";

    // The rows say nothing rather than a misleading 0 B when there is no amount to split.
    public string InternetDown => SelectedNet is { } u ? Core.Units.Data(u.Down) : "—";
    public string InternetUp => SelectedNet is { } u ? Core.Units.Data(u.Up) : "—";

    /// <summary>The share of it moved while another app was in front or nobody was there, worked out as the Network page does.</summary>
    public string InternetBackground => SelectedNet is { Total: > 0 } u ? $"{Math.Round(100.0 * u.Background / u.Total):0}%" : "—";

    [ObservableProperty] private AppListRow? _selectedRow;

    partial void OnSelectedRowChanged(AppListRow? value)
    {
        if (value is not null) Selected = value.Stat;
    }

    /// <summary>The selected app's active time across the period: per hour, per day or per month (see ChartTitle).</summary>
    [ObservableProperty] private List<DayBucket>? _selectedChart;

    public string ChartTitle => IsDay ? "Active time per hour"
        : ReportBuilder.IsLong(Unit, Period.From, Period.To) ? "Active time per month" : "Active time per day";

    /// <summary>"Games · 1h 59m this week": what the app is, and how much it was used in the period.</summary>
    public string Summary
    {
        get
        {
            if (Selected is null) return "";
            string period = Controls.PeriodPicker.Text(Unit, Anchor, CustomFrom, CustomTo);
            string when = period switch
            {
                "All time" => "in total",
                _ when period.StartsWith("This ") || period.StartsWith("Last ") || period is "Today" or "Yesterday" => period.ToLowerInvariant(),
                _ when Unit == ReportRange.Day => $"on {period}",
                _ => $"in {period}",
            };
            return $"{Converters.CategoryLabelConverter.Label(SelectedCategory)} · {Core.Units.Duration(Selected.ActiveSec)} {when}";
        }
    }

    /// <summary>Shows the app's .exe in File Explorer (from the "⋯" menu).</summary>
    [RelayCommand]
    private void OpenFileLocation()
    {
        if (Selected?.Path is not { } path || !File.Exists(path)) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose(); }
        catch (Exception ex) { Core.Log.Error("apps", ex); }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>The first period has been read: until then the page says it's loading, not that nothing was recorded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _ready;

    public bool ShowEmpty => Ready && !HasSelection;

    /// <summary>What the page says when the period has nothing ("No apps used today yet…").</summary>
    public string EmptyText => $"No apps used {Controls.PeriodPicker.Words(Unit, Anchor)} yet. Use your PC for a bit and come back.";

    /// <summary>The selected app's latest sessions in the range (read on their own: a range can hold thousands).</summary>
    [ObservableProperty] private List<SessionInfo> _selectedSessions = [];

    private DateTime _from, _to;

    private async Task LoadSessionsAsync(AppStat app)
    {
        var list = await reports.RecentSessionsAsync(app, _from, _to, 12) ?? [];
        if (Selected == app) SelectedSessions = list;
    }

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
            _ = LoadAsync(); // the list's own row takes the new name now (a past period isn't re-read by the minute refresh)
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
            OnPropertyChanged(nameof(Summary));
            ApplyView(); // the app may leave the category shown, or bring a new one to the list
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

    partial void OnUnitChanged(ReportRange value)
    {
        OnPropertyChanged(nameof(ChartTitle));
        _ = LoadAsync();
    }
    partial void OnAnchorChanged(DateTime value)
    {
        if (Unit is not (ReportRange.All or ReportRange.Custom)) _ = LoadAsync();
    }

    /// <summary>Midnight passed with the window open (see <see cref="Controls.PeriodPicker.AfterMidnight"/>).</summary>
    public void NewDay(DateTime was)
    {
        var anchor = Controls.PeriodPicker.AfterMidnight(Unit, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        OnPropertyChanged(nameof(RangeNote));
        OnPropertyChanged(nameof(IncludesToday));
        OnPropertyChanged(nameof(Summary));
        if (Unit == ReportRange.All) _ = LoadAsync(); // all time runs up to today
    }

    partial void OnCustomFromChanged(DateTime value) => CustomChanged();
    partial void OnCustomToChanged(DateTime value) => CustomChanged();

    // Both ends usually change together: one load for the pair.
    private bool _customPending;

    private void CustomChanged()
    {
        if (Unit != ReportRange.Custom || _customPending) return;
        _customPending = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            _customPending = false;
            _ = LoadAsync();
        });
    }
    partial void OnSortChanged(string value) => ApplyView();
    partial void OnSearchChanged(string value) => ApplyView();

    partial void OnSelectedChanged(AppStat? oldValue, AppStat? newValue)
    {
        // The same app re-read by the minute refresh keeps its chart until the new one is ready (no flicker).
        if (oldValue?.Id != newValue?.Id) { SelectedChart = null; SelectedSessions = []; }
        if (newValue is not null) { _ = LoadChartAsync(newValue); _ = LoadSessionsAsync(newValue); }
    }

    private async Task LoadChartAsync(AppStat app)
    {
        var chart = await reports.AppChartAsync(app.Id, SelectedCategory, Unit, _from, _to, FirstDay);
        if (Selected == app) SelectedChart = chart;
    }

    /// <summary>
    /// From a session summary notification: the app, on today (the session just ended), whatever range the
    /// page was left on.
    /// </summary>
    public void ShowToday(string exe)
    {
        _pendingSelection = exe;
        Anchor = DateTime.Today;
        Unit = ReportRange.Day;
        SelectExe(exe);
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

    /// <summary>The period is being read (the picker says so when it takes a moment).</summary>
    [ObservableProperty] private bool _isLoading;

    public async Task LoadAsync()
    {
        int id = ++_loadId;
        IsLoading = true;
        var (from, to) = Period;
        var first = await reports.FirstDayAsync();
        var netTask = reports.QueryAsync((db, s) => AppInternet.Of(db, from, to, db.LoadApps().ToDictionary(a => a.Id), s));
        var report = await reports.BuildRangeAsync(from, to);
        var net = await netTask ?? AppInternet.None;
        if (id != _loadId) return; // a newer range or day was picked meanwhile
        IsLoading = false;
        FirstDay = first;
        _report = report;
        (_from, _to) = (from, to);
        _all = _report?.Apps.Where(a => a.OpenSec >= 30 || a.MemMax >= 150).ToList() ?? [];
        _net = net;
        _netOnly.Clear();
        var listed = _all.Select(a => a.Id).ToHashSet();
        var own = _report?.Apps.ToDictionary(a => a.Id) ?? [];
        foreach (var a in net.Apps.Where(a => !listed.Contains(a.Id)))
        {
            // An app with a little time of its own keeps its own entry, so its time and temperatures come with it.
            _all.Add(own.GetValueOrDefault(a.Id) ?? a);
            _netOnly.Add(a.Id);
        }
        var keep = _pendingSelection ?? Selected?.Exe;
        ApplyView();
        if (keep is not null) SelectExe(keep);
        // The app picked before isn't in this period: the top one instead (or none), never the old period's figures.
        if (Selected is not null && !Apps.Any(r => r.Stat.Id == Selected.Id))
        {
            _pendingSelection = null;
            SelectedRow = null;
            Selected = null;
        }
        if (Selected is null && Apps.Count > 0) SelectedRow = Apps[0];
        Ready = true;
        OnPropertyChanged(nameof(Summary));
        foreach (var name in (string[])[nameof(InternetMain), nameof(InternetWord), nameof(InternetDown), nameof(InternetUp), nameof(InternetBackground)])
            OnPropertyChanged(name);
        if (Selected is not null) await Task.WhenAll(LoadSessionsAsync(Selected), LoadChartAsync(Selected));
    }

    private double Key(AppStat a) => Sort switch
    {
        "GpuTemp" => a.GpuTempAvg ?? -1,
        "CpuTemp" => a.CpuTempAvg ?? -1,
        "Memory" => a.MemMax ?? -1,
        "Background" => a.BackgroundSec + a.MinimizedSec,
        "Cpu" => a.CpuAvg ?? -1,
        // Download plus upload; an app that moved nothing goes last.
        "Internet" => _net.Use.TryGetValue(a.Id, out var u) ? u.Total : -1,
        _ => a.ActiveSec,
    };

    private void ApplyView()
    {
        UpdateFilters();
        var category = Filter?.Category;
        var q = Search?.Trim() ?? "";
        var list = _all
            .Where(a => category is null || CategoryOf(a) == category)
            .Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Exe.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(Listed)
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

    /// <summary>Whether the app belongs in the list under the current sort.</summary>
    private bool Listed(AppStat a) => Sort switch
    {
        // Every app that moved anything, then the ones open a while that moved nothing ("None"), so an app being
        // looked for is still there.
        "Internet" => _net.Use.ContainsKey(a.Id) || (a.OpenSec >= 30 && !_netOnly.Contains(a.Id)),
        _ when _netOnly.Contains(a.Id) => false,
        "GpuTemp" or "CpuTemp" => a.ActiveSec >= 60,
        "Memory" or "Cpu" => true,
        // Time-based sorts: hide background services you never actually had open.
        _ => a.OpenSec >= 30,
    };

    /// <summary>The categories in the period, in a fixed order; a category that's gone falls back to All categories.</summary>
    private void UpdateFilters()
    {
        var present = _all.Where(Listed).Select(CategoryOf).ToHashSet();
        var wanted = Enum.GetValues<AppCategory>()
            .Where(present.Contains)
            .OrderBy(c => c == AppCategory.Other) // Other last
            .ToList();
        var current = Filters.Skip(1).Select(f => f.Category!.Value).ToList();
        if (!current.SequenceEqual(wanted))
        {
            var keep = Filter?.Category;
            _rebuildingFilters = true;
            while (Filters.Count > 1) Filters.RemoveAt(1);
            foreach (var c in wanted) Filters.Add(new CategoryFilter(c, AppCatalog.Label(c)));
            // Re-pick the same category (the list was rebuilt), or all of them when it has no apps any more.
            Filter = Filters.FirstOrDefault(f => f.Category == keep) ?? AllApps;
            _rebuildingFilters = false;
        }
    }

    private string Metric(AppStat a) => Sort switch
    {
        "GpuTemp" => Core.Units.TempShort(a.GpuTempAvg),
        "CpuTemp" => Core.Units.TempShort(a.CpuTempAvg),
        "Memory" => a.MemMax is double m ? Core.Units.Megabytes(m) : "—",
        "Background" => Core.Units.Duration(a.BackgroundSec + a.MinimizedSec),
        "Cpu" => a.CpuAvg is double c ? $"{c:0.0}%" : "—",
        "Internet" => _net.Use.TryGetValue(a.Id, out var u) ? Core.Units.Data(u.Total) : _net.Recorded ? "None" : "Not recorded",
        _ => Core.Units.Duration(a.ActiveSec),
    };
}

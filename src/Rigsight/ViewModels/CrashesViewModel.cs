using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// A dashboard's Crashes tile: the last 30 days and the latest problem, whatever the Crashes page happens to show (the
/// tile used to read that page's counts, so it followed the period and filter the page was left on, with no label).
/// Muted apps' crashes aren't counted, as on the page.
/// </summary>
public sealed partial class RecentCrashes(ReportService reports, SettingsModel settings) : ObservableObject
{
    public const int Days = 30;

    [ObservableProperty] private int _appCrashCount;
    [ObservableProperty] private int _systemCount;
    [ObservableProperty] private int _driverResetCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashes))]
    private CrashRow? _latest;

    public bool HasCrashes => Latest is not null;
    public bool Loaded { get; private set; }

    private int _load;

    public async Task LoadAsync()
    {
        int id = ++_load;
        var rows = await reports.CrashesAsync(DateTime.Today.AddDays(-(Days - 1)), DateTime.Now.AddMinutes(1)) ?? [];
        if (id != _load) return;
        var muted = settings.Current;
        rows = [.. rows.Where(r => !muted.IsCrashMuted(r.Event.AppExe)).OrderByDescending(r => r.Time)];
        AppCrashCount = rows.Count(c => c.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang);
        SystemCount = CrashesViewModel.SystemCountOf(rows);
        DriverResetCount = rows.Count(c => c.Event.Kind == CrashKind.GpuDriverReset);
        Latest = rows.FirstOrDefault(); // a new row each time, so "Today, 3:10 PM" on it becomes "Yesterday" overnight
        Loaded = true;
    }
}

/// <summary>One of the times of a row that stands for several, as a tab on the open case.</summary>
public sealed partial class CaseTab(CrashRow row, Action<CaseTab> picked) : ObservableObject
{
    public CrashRow Row { get; } = row;
    public string Label => Row.TimeText;

    /// <summary>The one shown. Set by a click on the tab (which picks it), or by <see cref="Set"/> to follow what is shown.</summary>
    [ObservableProperty] private bool _isSelected;

    private bool _following;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !_following) picked(this);
    }

    /// <summary>Marks the tab as the one shown, or not, without asking for it to be opened.</summary>
    public void Set(bool on)
    {
        _following = true;
        IsSelected = on;
        _following = false;
    }
}

/// <summary>One line of what several problems have in common: its icon, the palette brush for it, and the sentence.</summary>
public sealed record CrashPattern(string Icon, string Brush, string Text);

public sealed partial class CrashesViewModel(ReportService reports, SettingsModel settings) : ObservableObject
{
    /// <summary>What a dashboard's Crashes tile shows (see <see cref="RecentCrashes"/>).</summary>
    public RecentCrashes Recent { get; } = new(reports, settings);

    /// <summary>The period shown (see <see cref="PeriodPicker"/>): this month unless picked otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IsDay), nameof(ShowTimeline), nameof(EmptyText), nameof(IncludesToday))]
    private Core.Reports.ReportRange _unit = Core.Reports.ReportRange.Month;

    /// <summary>Any day in the period shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IncludesToday), nameof(EmptyText))]
    private DateTime _anchor = DateTime.Today;

    /// <summary>First day with anything recorded (tracking, or crashes Windows logged before Rigsight was installed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote))]
    private DateTime? _since;

    /// <summary>A custom range's start and end (whole hours), when <see cref="Unit"/> is Custom.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IsDay), nameof(ShowTimeline), nameof(EmptyText), nameof(IncludesToday))]
    private DateTime _customFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IsDay), nameof(ShowTimeline), nameof(EmptyText), nameof(IncludesToday))]
    private DateTime _customTo;

    partial void OnCustomFromChanged(DateTime value) => CustomChanged();
    partial void OnCustomToChanged(DateTime value) => CustomChanged();

    // Both ends usually change together: one load for the pair.
    private bool _customPending;

    private void CustomChanged()
    {
        if (Unit != Core.Reports.ReportRange.Custom || _customPending) return;
        _customPending = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            _customPending = false;
            _limit = PageSize;
            _ = LoadAsync();
        });
    }

    /// <summary>One day, or a custom range of up to two days: listed as it happened, without the day strip.</summary>
    public bool IsDay => Core.Reports.ReportBuilder.IsDayLike(Unit, Bounds.From, Bounds.To);

    /// <summary>The period shown includes today, so new crashes can appear.</summary>
    public bool IncludesToday => Bounds.To > DateTime.Today;
    public bool ShowTimeline => !IsDay;

    /// <summary>The period shown, as it reads after "No problems": "today", "this month", "on Fri, 9 Oct", "on record" (see <see cref="PeriodPicker.Words"/>).</summary>
    private string When => PeriodPicker.Words(Unit, Anchor, CustomFrom, CustomTo);

    /// <summary>List every crash newest first (default), or group repeats of the same crash into one row.</summary>
    [ObservableProperty] private bool _grouped;

    partial void OnGroupedChanged(bool value)
    {
        _limit = PageSize;
        Regroup();
        ApplyView();
    }

    partial void OnAnchorChanged(DateTime value)
    {
        _limit = PageSize;
        if (Unit is not (Core.Reports.ReportRange.All or Core.Reports.ReportRange.Custom)) _ = LoadAsync();
    }

    /// <summary>Midnight passed with the window open (see <see cref="PeriodPicker.AfterMidnight"/>).</summary>
    public void NewDay(DateTime was)
    {
        var anchor = PeriodPicker.AfterMidnight(Unit, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        OnPropertyChanged(nameof(RangeNote));
        OnPropertyChanged(nameof(IncludesToday));
        _ = LoadAsync(); // "Today, 3:10 PM" on a row and "stable for N days" are read against the new day
    }

    [RelayCommand]
    private void OpenDay(DateTime day) => ShowDay(day);

    /// <summary>Shows one day's crashes (clicking a day on the timeline).</summary>
    public void ShowDay(DateTime day)
    {
        Anchor = day.Date;
        Unit = Core.Reports.ReportRange.Day;
    }

    /// <summary>Which dates are covered. Windows' crash log is read back 90 days when Rigsight is installed,
    /// so "All time" can reach further back than the rest of the app's history.</summary>
    public string RangeNote => PeriodPicker.Span(Unit, Anchor, Since, CustomFrom, CustomTo);

    /// <summary>The period's start and end; all time runs from the first recorded problem.</summary>
    private (DateTime From, DateTime To) Bounds => Unit == Core.Reports.ReportRange.All
        ? (Since ?? DateTime.Today, DateTime.Today.AddDays(1))
        : PeriodPicker.Bounds(Unit, Anchor, CustomFrom, CustomTo);

    private DateTime From => Bounds.From;

    /// <summary>"All", "Apps" (app and game crashes, freezes) or "Pc" (blue screens, sudden shutdowns, driver resets, freezes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private string _filter = "All";

    /// <summary>Show muted apps' crashes (faded) in the list.</summary>
    [ObservableProperty] private bool _showMuted;

    private List<CrashRow> _all = [];
    private List<CrashGroup> _groups = [];
    private List<SystemChange> _changes = [];

    /// <summary>Problems as they're listed: repeats and bursts grouped, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashes), nameof(EmptyText), nameof(ShowEmpty))]
    private List<CrashGroup> _groupsShown = [];

    // Years of history can hold thousands of crashes: build the newest cards, and more as the page scrolls.
    private const int PageSize = 20;
    private int _limit = PageSize;
    private List<CrashGroup> _matching = [];

    public bool HasMore => _matching.Count > GroupsShown.Count;
    /// <summary>Called by the page's infinite scroll near the bottom.</summary>
    [RelayCommand]
    private void ShowMore()
    {
        if (!HasMore) return;
        _limit += PageSize;
        ShowPage();
    }

    /// <summary>Back to the first page of cards (the newest), when more were loaded by scrolling.</summary>
    public void ShowFirstPage()
    {
        if (_limit == PageSize) return;
        _limit = PageSize;
        ShowPage();
    }

    private void ShowPage()
    {
        GroupsShown = [.. _matching.Take(_limit)];
        OnPropertyChanged(nameof(HasMore));
    }

    /// <summary>Individual crashes (not muted) for the current filter, newest first (dashboard tile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Latest))]
    private List<CrashRow> _crashes = [];

    /// <summary>The newest of <see cref="Crashes"/>, if any (dashboard tile).</summary>
    public CrashRow? Latest => Crashes.FirstOrDefault();

    [ObservableProperty] private List<CrashDay> _days = [];
    [ObservableProperty] private List<CrashPattern> _patterns = [];

    // ── A crash opened ──────────────────────────────────────────────────

    /// <summary>The crash that is open (its own page in place of the list), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCaseOpen))]
    private CrashCase? _case;

    public bool IsCaseOpen => Case is not null;

    private int _caseId;

    /// <summary>
    /// The times of the row that is open, newest first, where it stands for several (the same crash repeated): each
    /// its own case, picked like tabs. Empty for a row of one, and for several things that failed together (one moment).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaseTabs), nameof(CaseCount))]
    private List<CaseTab> _caseTabs = [];

    public bool HasCaseTabs => CaseTabs.Count > 1;

    /// <summary>"2 of 4": which of the row's times is shown.</summary>
    public string CaseCount => $"{CaseTabs.FindIndex(t => t.IsSelected) + 1} of {CaseTabs.Count}";

    [RelayCommand]
    private async Task OpenCase(CrashGroup g)
    {
        int id = ++_caseId;
        var opened = await reports.CaseAsync(g);
        if (id != _caseId || opened is null) return;
        CaseTabs = g.IsIncident || g.Count < 2 ? [] : [.. g.Rows.Select(r => new CaseTab(r, t => _ = PickTab(t)))];
        _shown = CaseTabs.FirstOrDefault();
        _shown?.Set(true);
        OnPropertyChanged(nameof(CaseCount));
        Case = opened;
    }

    private CaseTab? _shown;

    /// <summary>Another of the open row's times.</summary>
    [RelayCommand]
    private async Task PickTab(CaseTab tab)
    {
        if (Case is not { } open || tab == _shown) return;
        int id = ++_caseId;
        _shown = tab;
        foreach (var t in CaseTabs) t.Set(t == tab);
        OnPropertyChanged(nameof(CaseCount));
        var opened = await reports.CaseAsync(open.Group, tab.Row);
        if (id == _caseId && opened is not null) Case = opened;
    }

    /// <summary>The time before (-1, newer) or after (1, older) the one shown.</summary>
    [RelayCommand]
    private Task StepTab(string by)
    {
        int i = CaseTabs.FindIndex(t => t.IsSelected) + (by == "-1" ? -1 : 1);
        return i >= 0 && i < CaseTabs.Count ? PickTab(CaseTabs[i]) : Task.CompletedTask;
    }

    [RelayCommand]
    private void CloseCase()
    {
        _caseId++;
        Case = null;
        CaseTabs = [];
        _shown = null;
    }

    /// <summary>
    /// Opens the crash that happened at <paramref name="time"/> (from Home, Reports, the Timeline or a notification):
    /// its own case when it is found, else the page on that day.
    /// </summary>
    public async Task ShowCrashAsync(DateTime time)
    {
        int id = ++_caseId;
        var rows = await reports.CrashesAsync(time.AddMinutes(-6), time.AddMinutes(6), includeMuted: true) ?? [];
        var group = CrashGroup.Build(rows, groupRepeats: false).Where(g => g.Rows.Any(r => Math.Abs((r.Time - time).TotalSeconds) < 1))
            .OrderByDescending(g => g.IsIncident).FirstOrDefault();
        if (group is { AppExe: { } exe }) group.IsMuted = settings.Current.IsCrashMuted(exe);
        var opened = group is null ? null : await reports.CaseAsync(group);
        if (id != _caseId) return;
        CaseTabs = [];
        if (opened is null) ShowDay(time);
        else Case = opened;
    }

    [ObservableProperty] private int _appCrashCount;
    [ObservableProperty] private int _systemCount;
    [ObservableProperty] private int _driverResetCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMuted), nameof(MutedToggleText))]
    private int _mutedCount;

    public bool HasMuted => MutedCount > 0;
    public string MutedToggleText => $"Show muted ({MutedCount})";

    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusDetail = "";
    /// <summary>The palette brush for the status (a key, so it follows the dark or light theme).</summary>
    [ObservableProperty] private string _statusBrush = "MutedBrush";
    [ObservableProperty] private string _statusIcon = "";

    public bool HasCrashes => GroupsShown.Count > 0;

    public string EmptyText => Core.TextMatch.Words(SearchText).Length > 0 ? "Nothing matches"
        : Filter switch
    {
        "Apps" => $"No app or game crashes {When}",
        "Pc" => $"No PC crashes, driver resets or sudden shutdowns {When}",
        _ => $"No crashes {When}",
    };

    partial void OnFilterChanged(string value) { _limit = PageSize; ApplyView(); }

    /// <summary>
    /// What's typed in the search box: the list shows the problems that say it (the app, what went wrong, the file it
    /// crashed in, the error's code). The tiles and the timeline above stay the whole period's.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value) { _limit = PageSize; ApplyView(); }

    private static bool Says(CrashRow r, string[] words) =>
        Core.TextMatch.Has(words, r.Title, r.AppName, r.Event.AppExe, r.Event.Module, r.Event.Code, r.Explanation.Reason,
            // …and what its case says and names: the finding, a blue screen's driver, what a frozen app waited on.
            r.Finding?.Head, r.Finding?.Line, r.Finding?.Chip, r.Event.Facts?.Driver, r.Event.Facts?.DriverName, r.Event.Facts?.WaitedOn, r.Event.Facts?.Error);
    partial void OnShowMutedChanged(bool value) { _limit = PageSize; ApplyView(); }
    partial void OnUnitChanged(Core.Reports.ReportRange value) { _limit = PageSize; _ = LoadAsync(); }

    private int _loadId;

    /// <param name="onlyIfChanged">The minute refresh: leave the page alone unless a crash arrived.</param>
    public async Task LoadAsync(bool onlyIfChanged = false)
    {
        int id = ++_loadId;
        IsLoading = true;
        try
        {
            await ReadAsync(id, onlyIfChanged);
        }
        finally
        {
            if (id == _loadId) IsLoading = false;
        }
    }

    /// <summary>The period is being read (the picker says so when it takes a moment).</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>The agent hasn't read Windows' crash records yet on this version: the list waits with a loader.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowListLoader), nameof(ShowEmpty))]
    private bool _readingRecords;

    /// <summary>The list's loader: the first read, or the wait for Windows' records.</summary>
    public bool ShowListLoader => !Loaded || ReadingRecords;

    /// <summary>When the app started: the wait for the records is for just after an update, never longer than this.</summary>
    internal DateTime Started { get; set; } = AppStarted;
    private static readonly DateTime AppStarted = System.Diagnostics.Process.GetCurrentProcess().StartTime;
    internal const double WaitForRecordsMinutes = 3;
    private Task? _recordsWait;

    /// <summary>Whether the agent has read Windows' records yet (tests swap it).</summary>
    internal Func<Task<bool?>> RecordsRead { get; set; } = reports.CrashRecordsReadAsync;

    /// <summary>Looks every couple of seconds until the records are in (or the wait is over), then reads the page again.</summary>
    private async Task WaitForRecordsAsync()
    {
        while ((DateTime.Now - Started).TotalMinutes < WaitForRecordsMinutes && await RecordsRead() == false)
            await Task.Delay(2000);
        _recordsWait = null;
        await (System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => LoadAsync()).Task.Unwrap() ?? Task.CompletedTask);
    }

    private async Task ReadAsync(int id, bool onlyIfChanged)
    {
        // Just after an update, until the agent has read Windows' records once, the stored crashes lack what their
        // findings rest on: judged now, a power cut read as a serious shut-off for half a minute, then turned grey.
        // The page waits for the records instead, says so, and reloads by itself once they are in.
        if (await RecordsRead() == false && (DateTime.Now - Started).TotalMinutes < WaitForRecordsMinutes)
        {
            if (id != _loadId) return;
            ReadingRecords = true;
            (StatusTitle, StatusDetail, StatusIcon, StatusBrush) = ("Reading Windows' records",
                "Reading what Windows recorded about these crashes. This takes a few seconds after an update.", "\uE895", "MutedBrush");
            _recordsWait ??= Task.Run(WaitForRecordsAsync);
            return;
        }
        ReadingRecords = false;
        var since = await reports.FirstCrashDayAsync();
        var all = Unit == Core.Reports.ReportRange.All;
        var (from, to) = all ? (DateTime.Today.AddYears(-20), DateTime.Today.AddDays(1)) : Bounds;
        if (to > DateTime.Now) to = DateTime.Now.AddMinutes(1);
        var rows = await reports.CrashesAsync(from, to, includeMuted: true) ?? [];
        // The same crashes saying the same: nothing to rebuild. (One whose finding changed, as Windows wrote more about it, is a change.)
        if (onlyIfChanged && rows.Select(r => (r.Event.Id, r.Finding?.Chip, r.Finding?.Line)).SequenceEqual(_all.Select(r => (r.Event.Id, r.Finding?.Chip, r.Finding?.Line)))) return;
        // Drivers and updates from a week before the oldest problem, to explain what came after.
        var changesFrom = (rows.Count > 0 ? rows.Min(c => c.Time) : from).Date.AddDays(-7);
        if (!all && changesFrom > from.AddDays(-7)) changesFrom = from.AddDays(-7);
        var changes = await ReportService.ChangesAsync(changesFrom, to);
        if (id != _loadId) return;

        Since = since;
        _all = rows;
        _changes = changes;
        Regroup();
        ApplyView();
        Loaded = true;
    }

    /// <summary>Loaded once: a later visit only needs a rebuild if the crashes changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowListLoader))]
    private bool _loaded;

    /// <summary>"No crashes in this period": only once the period has been read (a loader until then).</summary>
    public bool ShowEmpty => Loaded && !HasCrashes && !ReadingRecords;

    private void Regroup()
    {
        // Cards the user opened ("All N times", "Details") stay open when the list is rebuilt.
        static string KeyOf(CrashGroup g) => $"{g.IsIncident}|{g.First.Event.Id}";
        var open = _groups.Where(g => g.IsExpanded || g.ShowDetails).ToDictionary(KeyOf, g => (g.IsExpanded, g.ShowDetails));
        _groups = CrashGroup.Build(_all, groupRepeats: Grouped);
        foreach (var g in _groups)
            if (open.TryGetValue(KeyOf(g), out var state)) (g.IsExpanded, g.ShowDetails) = state;
    }

    /// <summary>Re-applies mute state, counts, timeline, status and the visible list.</summary>
    private void ApplyView()
    {
        var s = settings.Current;
        foreach (var g in _groups) g.IsMuted = g.AppExe is { } exe && s.IsCrashMuted(exe);
        var counted = _groups.Where(g => !g.IsMuted).ToList();
        var rows = counted.SelectMany(g => g.Rows).OrderByDescending(r => r.Time).ToList();

        MutedCount = _groups.Where(g => g.IsMuted).Sum(g => g.Count);
        AppCrashCount = rows.Count(c => c.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang);
        SystemCount = SystemCountOf(rows);
        PowerCutCount = rows.Count(IsPowerCut);
        UnfinishedCount = rows.Count(c => c.Event is { Kind: CrashKind.UnexpectedShutdown, Moment: PowerMoment.ShuttingDown });
        DriverResetCount = rows.Count(c => c.Event.Kind == CrashKind.GpuDriverReset);

        bool Matches(CrashGroup g) => Filter switch
        {
            "Apps" => !g.IsIncident && !g.Latest.IsSystem,
            "Pc" => g.IsIncident || g.Latest.IsSystem,
            _ => true,
        };
        var words = Core.TextMatch.Words(SearchText);
        _matching = [.. _groups.Where(g => (ShowMuted || !g.IsMuted) && Matches(g) && (words.Length == 0 || g.Rows.Any(r => Says(r, words))))];
        ShowPage();
        Crashes = [.. counted.Where(Matches).SelectMany(g => g.Rows).OrderByDescending(r => r.Time)];
        Days = IsDay ? [] : CrashStrip.BuildDays(From, Bounds.To, counted, _changes);
        Patterns = BuildPatterns(rows, _changes);
        BuildStatus(rows, [.. counted.Where(g => g.IsIncident).SelectMany(g => g.Rows)]);
    }

    /// <summary>
    /// Blue screens and shut-offs, the third tile and the dashboard's "PC crashes": not a shutdown that didn't finish
    /// (usually harmless, and still listed). The likely power cuts are in it, and said under the number.
    /// </summary>
    internal static int SystemCountOf(IEnumerable<CrashRow> rows) => rows.Count(c => c.Event.Kind == CrashKind.SystemCrash
        || c.Event is { Kind: CrashKind.UnexpectedShutdown } && c.Event.Moment != PowerMoment.ShuttingDown);

    /// <summary>A shut-off whose cause was most likely outside the PC (the mains).</summary>
    private static bool IsPowerCut(CrashRow r) => r.Event is { Kind: CrashKind.UnexpectedShutdown, Moment: PowerMoment.Running } && r.Finding is { Outside: true };

    /// <summary>Under the third tile's number: how many of them were likely power cuts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerCutText), nameof(HasPowerCuts))]
    private int _powerCutCount;

    /// <summary>Shutdowns that didn't finish: listed, and not in the third tile's number.</summary>
    [ObservableProperty] private int _unfinishedCount;

    public bool HasPowerCuts => PowerCutCount > 0;
    public string PowerCutText => PowerCutCount == 1 ? "1 likely power cut" : $"{PowerCutCount} likely power cuts";

    // ── Status line ─────────────────────────────────────────────────────

    private void BuildStatus(List<CrashRow> rows, HashSet<CrashRow> inIncidents)
    {
        static string Ago(DateTime t)
        {
            int days = (int)(DateTime.Today - t.Date).TotalDays;
            return days switch { 0 => "today", 1 => "yesterday", _ => $"{days} days ago" };
        }
        var serious = rows.Where(r => CrashGroup.SeverityOf(r) >= CrashSeverity.Serious || inIncidents.Contains(r)).ToList();
        // The newest shut-off judged to be the mains: said in the same place, without the alarm.
        var mains = rows.Where(r => r.Event is { Kind: CrashKind.UnexpectedShutdown, Moment: PowerMoment.Running } && r.Finding is { Outside: true }).ToList();
        var bsod = rows.FirstOrDefault(r => r.Event.Kind == CrashKind.SystemCrash);
        var app = rows.FirstOrDefault(r => r.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang);
        var lastSerious = serious.FirstOrDefault();

        if (IsDay || !IncludesToday)
        {
            // One day, or a period that's over: say what happened in it rather than "stable for N days" (counted to today).
            string when = When;
            int worst = rows.Count == 0 ? -1 : rows.Max(r => (int)CrashGroup.SeverityOf(r));
            StatusTitle = rows.Count == 0 ? $"No problems {when}" : $"{rows.Count} problem{(rows.Count == 1 ? "" : "s")} {when}";
            StatusDetail = rows.Count == 0 ? "Nothing crashed, froze or shut down unexpectedly."
                : string.Join(" · ", rows.GroupBy(r => r.Event.Kind).Select(g => KindCount(g.Key, g.Count()))) + ".";
            (StatusIcon, StatusBrush) = worst switch
            {
                >= (int)CrashSeverity.Critical => ("", "HotBrush"),
                >= (int)CrashSeverity.Serious => ("", "WarmBrush"),
                >= (int)CrashSeverity.Minor => ("", "WarmBrush"),
                _ => ("", "GoodBrush"),
            };
            return;
        }
        int daysSince = lastSerious is null ? int.MaxValue : (int)(DateTime.Today - lastSerious.Time.Date).TotalDays;
        string powerCuts = mains.Count == 0 ? "" : mains.Count == 1 ? "1 likely power cut" : $"{mains.Count} likely power cuts";

        // A power cut leads only when nothing serious happened in the last week: a newer one hid a shut-off under load
        // that pointed at the power supply (the user: "what do you think is wrong with this?").
        if (mains.Count > 0 && (DateTime.Today - mains[0].Time.Date).TotalDays <= 7 && daysSince > 7)
        {
            StatusDetail = $"{mains[0].Finding!.Verdict.TrimEnd('.')}{(mains.Count > 1 ? $", like {mains.Count - 1} other{(mains.Count == 2 ? "" : "s")} {When}" : "")}.";
            (StatusTitle, StatusIcon, StatusBrush) = ($"Your PC lost power {Ago(mains[0].Time)}", "\uE7E8", "TextBrush");
            return;
        }

        // One short line: what most likely happened in the problem that leads, and the power cuts beside it. With
        // nothing serious in the last week: when the last ones were, and what the tile's shut-offs were if harmless
        // (a power loss while asleep, a shutdown that didn't finish), so a count is never left unexplained. Whether
        // there were blue screens is the tile's to say.
        if (daysSince <= 7 && lastSerious!.Finding is { } finding)
            StatusDetail = $"{finding.Verdict.TrimEnd('.')}{(powerCuts.Length > 0 ? $" · also {powerCuts} {When}" : "")}.";
        else
        {
            var parts = new List<string>();
            if (bsod is not null) parts.Add($"last blue screen {Ago(bsod.Time)}");
            parts.Add(app is null ? $"no app crashes {When}" : $"last app crash {Ago(app.Time)}");
            int asleep = rows.Count(r => r.Event.Kind == CrashKind.UnexpectedShutdown && r.Event.DuringSleep);
            if (asleep > 0) parts.Add($"{asleep} power loss{(asleep == 1 ? "" : "es")} while asleep (not a fault)");
            int unfinished = rows.Count(r => r.Event.Kind == CrashKind.UnexpectedShutdown && r.Event.Moment == PowerMoment.ShuttingDown);
            if (unfinished > 0) parts.Add($"{unfinished} shutdown{(unfinished == 1 ? "" : "s")} that didn't finish (usually harmless)");
            string line = string.Join(" · ", parts);
            StatusDetail = char.ToUpper(line[0]) + line[1..] + ".";
        }
        (StatusTitle, StatusIcon, StatusBrush) = daysSince switch
        {
            <= 7 when lastSerious!.Event.Kind is CrashKind.SystemCrash =>
                ($"Blue screen {Ago(lastSerious.Time)}", "", "HotBrush"),
            <= 7 when lastSerious!.Event.Kind is CrashKind.UnexpectedShutdown =>
                ($"Your PC shut off unexpectedly {Ago(lastSerious.Time)}", "", "HotBrush"),
            <= 7 when inIncidents.Contains(lastSerious!) => ($"Your PC froze {Ago(lastSerious.Time)}", "", "WarmBrush"),
            <= 7 => ($"Graphics trouble {Ago(lastSerious!.Time)}", "", "WarmBrush"),
            int.MaxValue => (rows.Count == 0 ? "All clear" : "No serious problems", "", "GoodBrush"),
            _ => ($"Stable for {daysSince} days", "", "GoodBrush"),
        };
    }

    internal static string KindCount(CrashKind kind, int n) => CrashWords.Count(kind, n);

    // ── Patterns ────────────────────────────────────────────────────────

    /// <summary>
    /// What several problems have in common, each line from what their findings rest on (repeats of one app are shown
    /// by the grouping itself).
    /// </summary>
    internal static List<CrashPattern> BuildPatterns(List<CrashRow> rows, List<SystemChange> changes)
    {
        var list = new List<CrashPattern>();
        var shutdowns = rows.Where(c => c.Event.Kind == CrashKind.UnexpectedShutdown).ToList();
        var running = shutdowns.Where(c => c.Event.Moment == PowerMoment.Running).ToList();

        // Shut-offs while running, set against what the PC was doing: at idle they point away from the PC.
        var read = running.Where(c => c.Input?.Context is { Minutes: > 0 }).ToList();
        int light = read.Count(c => (c.Input!.Context!.GpuLoad ?? 0) < CrashFindings.LightLoad && (c.Input.Context.CpuLoad ?? 0) < CrashFindings.LightLoad);
        var buttons = running.Where(c => c.Event.Facts?.PowerButton is not null).ToList();
        if (read.Count >= 2)
        {
            int held = buttons.Count(c => c.Event.Facts!.PowerButton == true);
            string button = buttons.Count < running.Count ? "" : held == 0 ? " Windows recorded a held power button in none of them." : $" Windows recorded a held power button in {held} of them.";
            string verdict = light >= 2 && light > read.Count - light && held == 0 ? " Shut-offs at idle point away from load and heat; the mains is the usual cause." : "";
            list.Add(new("\uE7E8", "CpuBrush", $"Of the {read.Count} shut-offs with readings, {light} came with the PC doing little and {read.Count - light} while it was working hard.{button}{verdict}"));
        }
        // A graphics driver that came in the middle of them, when they were as frequent before it.
        if (running.Count >= 2 && changes.Where(c => c.IsGraphicsDriver).OrderByDescending(c => c.Time).FirstOrDefault() is { } driver
            && running.Any(c => c.Time >= driver.Time) && running[0].Input?.Earlier is { } earlier)
        {
            long ts = Core.Data.TimeUtil.ToUnix(driver.Time);
            int before = earlier.Count(p => p.Ts < ts && p.Ts >= ts - 30L * 86400) + (running[0].Event.Ts < ts ? 1 : 0), since = running.Count(c => c.Time >= driver.Time);
            string name = driver.Title.EndsWith(" installed", StringComparison.Ordinal) ? driver.Title[..^" installed".Length] : driver.Title;
            if (before >= 2)
                list.Add(new("\uE73E", "GoodBrush", $"The {name} of {driver.Time:d MMM} did not start the shut-offs: {before} in the 30 days before it, {since} since."));
        }
        int asleep = shutdowns.Count(c => c.Event.DuringSleep);
        if (asleep >= 2)
            list.Add(new("\uE946", "MutedBrush", $"{asleep} of {shutdowns.Count} unexpected shutdowns happened while the PC was asleep. If you switch off power at the wall, shut down fully first; otherwise a BIOS or chipset driver update often fixes sleep problems."));
        int unfinished = shutdowns.Count(c => c.Event.Moment == PowerMoment.ShuttingDown);
        if (unfinished >= 2)
            list.Add(new("\uE946", "MutedBrush", $"{unfinished} more shutdowns didn't finish while Windows was shutting down. Turning off Fast Startup, or a BIOS or chipset driver update, usually stops them."));

        // An app that only went down because a driver was being installed under it says nothing about the driver.
        int gpuRelated = rows.Count(c => c.Finding is not { Outside: true } &&
                                        (c.Culprit.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                         c.Culprit.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                         c.Culprit.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                                         c.Event.Kind == CrashKind.GpuDriverReset ||
                                         c.Event.Code is "0x116" or "0x119" or "0x117"));
        if (gpuRelated >= 2)
            list.Add(new("\uE945", "WarmBrush", $"{gpuRelated} problems involved the graphics driver. A clean driver reinstall (and stock GPU clocks) is the first thing to try."));

        // Heat by the user's own alert limits, as each case judges it.
        int hot = rows.Count(c => c.Finding?.Points.Any(p => p.Title == CrashFindings.HotTitle) == true);
        if (hot >= 1)
            list.Add(new("\uE945", "WarmBrush", $"{hot} crash{(hot == 1 ? "" : "es")} happened while your hardware was over your alert limits. Check airflow and fan curves."));
        return list;
    }

    // ── Actions ─────────────────────────────────────────────────────────

    [RelayCommand]
    private static void ToggleExpand(CrashGroup g) => g.IsExpanded = !g.IsExpanded;

    [RelayCommand]
    private static void ToggleDetails(CrashGroup g) => g.ShowDetails = !g.ShowDetails;

    [RelayCommand]
    private void ToggleMute(CrashGroup g)
    {
        if (g.AppExe is not { } exe) return;
        bool mute = !g.IsMuted;
        settings.Update(s =>
        {
            s.MutedCrashApps.RemoveAll(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase));
            if (mute) s.MutedCrashApps.Add(exe);
        });
        g.IsMuted = mute; // a crash opened from another page isn't one of the list's rows: its own button says it at once
        ApplyView();
    }

    [RelayCommand]
    private static void Search(CrashGroup g) => Open("https://www.google.com/search?q=" + Uri.EscapeDataString(g.SearchQuery));

    [RelayCommand]
    private static void ShowDump(CrashGroup g)
    {
        if (g.DumpPath is not { } path) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private async Task Copy(CrashGroup g)
    {
        try
        {
            Clipboard.SetText(Report(g));
            g.Copied = true;
            await Task.Delay(2000);
            g.Copied = false;
        }
        catch
        {
            // The clipboard can be briefly locked by another app; the user can simply click again.
        }
    }

    /// <summary>
    /// A plain-text summary for forums or support: what, when, what most likely happened and how sure that is, what it
    /// rests on, what was ruled out, and this PC's hardware.
    /// </summary>
    public static string Report(CrashGroup g)
    {
        var sb = new StringBuilder();
        string kind = g.KindLabel.ToLowerInvariant();
        sb.AppendLine(g.Title.Contains(kind, StringComparison.OrdinalIgnoreCase) ? $"Problem: {g.Title}" : $"Problem: {g.Title} ({kind})");
        sb.AppendLine(g.IsIncident
            ? $"When: {g.First.Time:dddd d MMMM yyyy, h:mm tt} – {g.Latest.Time:h:mm tt}"
            : g.Count == 1
            ? $"When: {g.Latest.Time:dddd d MMMM yyyy, h:mm tt}"
            : $"When: {g.Count} times, most recently {g.Latest.Time:d MMM yyyy, h:mm tt} (first {g.First.Time:d MMM yyyy})");
        if (g.Finding is { } f)
        {
            sb.AppendLine($"What happened: {f.Head} {f.Text}");
            sb.AppendLine($"How sure: {f.Verdict}");
            if (f.Points.Count > 0) sb.AppendLine("What points to this:");
            foreach (var p in f.Points) sb.AppendLine($"  - {p.Title}{(p.Detail.Length > 0 ? ": " + p.Detail : "")}");
            if (f.RuledOut.Count > 0) sb.AppendLine("What it wasn't:");
            foreach (var p in f.RuledOut) sb.AppendLine($"  - {p.Title}: {p.Detail}");
            if (f.NotChecked.Count > 0) sb.AppendLine("Could not be checked:");
            foreach (var p in f.NotChecked) sb.AppendLine($"  - {p.Title}: {p.Detail}");
            if (f.CantTell is { } cant) sb.AppendLine($"Still unknown: {cant}");
        }
        else sb.AppendLine($"What happened: {g.Reason}");
        if (g.IsIncident)
            foreach (var r in g.Rows.OrderBy(r => r.Time))
                sb.AppendLine($"  {r.Time:h:mm:ss tt}  {(r.Event.Kind == CrashKind.GpuDriverReset ? "Graphics driver" : r.AppName ?? r.Event.AppExe)}: {r.KindLabel.ToLowerInvariant()}" +
                              (string.IsNullOrEmpty(r.TechnicalText) || r.Event.Kind == CrashKind.GpuDriverReset ? "" : $" ({r.TechnicalText})"));
        if (!g.IsIncident && !string.IsNullOrEmpty(g.TechnicalText)) sb.AppendLine($"Technical: {g.TechnicalText}");
        sb.AppendLine($"What to try: {g.Advice}");
        sb.AppendLine();
        sb.Append(PcInfo.Text);
        return sb.ToString();
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private static void OpenEventViewer()
    {
        try { Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true }); } catch { }
    }
}

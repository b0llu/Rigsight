using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Stability;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A kind of change to show, with how many there are ("Drivers (4)").</summary>
public sealed record TimelineFilter(string Key, string Name, int Count)
{
    public string Label => $"{Name} ({Count:N0})";
}

/// <summary>One line under a day: a change, a problem, or a day's app updates folded into one. Under a month: one kind
/// of change that month (its drivers, its app updates) folded into one.</summary>
public sealed partial class TimelineEntry : ObservableObject
{
    public string TimeText { get; init; } = "";

    /// <summary>Said on hover when the time isn't the exact moment ("~7:20 PM").</summary>
    public string? TimeTip { get; init; }
    public string Icon { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Detail { get; init; }

    /// <summary>Drivers, Windows and hardware stand out: they're what a PC-wide problem usually comes from.</summary>
    public bool IsKey { get; init; }

    /// <summary>The palette brush for the line: text for a change, warm or hot for a problem.</summary>
    public string Brush { get; init; } = "TextBrush";
    public string IconBrush { get; init; } = "MutedBrush";

    /// <summary>A problem's day, to open on the Crashes page.</summary>
    public DateTime? CrashDay { get; init; }
    public bool IsProblem => CrashDay is not null;

    /// <summary>What was different after this change (see <see cref="Core.Reports.ChangeEffects"/>), and the brush for it.</summary>
    public List<string> Effect { get; set; } = [];
    public string EffectBrush { get; set; } = "MutedBrush";

    /// <summary>The app updates folded into this line.</summary>
    public List<TimelineEntry> Children { get; init; } = [];
    public bool HasChildren => Children.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isExpanded;

    /// <summary>What opening and closing the folded lines is called (a month's line holds changes, not versions).</summary>
    public string ShowText { get; init; } = "Show versions";
    public string HideText { get; init; } = "Hide versions";
    public string ToggleText => IsExpanded ? HideText : ShowText;

    /// <summary>Which of a row's folded lines this is (a month has one per kind of change), to keep it open when rebuilt.</summary>
    public string Key { get; init; } = "";
}

/// <summary>A day on the timeline with everything that happened on it, newest first.</summary>
public sealed class TimelineDay
{
    public DateTime Day { get; init; }
    public string Title { get; init; } = "";
    public string SubTitle { get; init; } = "";

    /// <summary>"SEPTEMBER 2026" above the first day shown of each month (by month: "2026" above each year's first month).</summary>
    public string? Month { get; init; }
    public List<TimelineEntry> Entries { get; init; } = [];

    /// <summary>The dot on the line: blue for changes, red for a day (or a month) with only problems.</summary>
    public string NodeBrush { get; init; } = "CpuBrush";

    /// <summary>Not a day: a whole month in one row (grouped by month), a line for each kind of change in it.</summary>
    public bool IsSummary { get; init; }
}

/// <summary>
/// A day in the calendar (or a blank before the 1st). What's on it can change while the month stays (another filter):
/// the cell is kept and told, so the calendar isn't built again.
/// </summary>
public sealed partial class CalendarCell(DateTime? day) : ObservableObject
{
    public DateTime? Day { get; } = day;
    public string Text => Day?.Day.ToString() ?? "";
    /// <summary>For screen readers: the whole date.</summary>
    public string Label => Day?.ToString("D") ?? "";

    [ObservableProperty] private bool _hasAny;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DotBrush))]
    private bool _onlyProblems;

    [ObservableProperty] private bool _isToday;
    [ObservableProperty] private string? _tip;

    public string DotBrush => OnlyProblems ? "HotBrush" : "CpuBrush";
}

/// <summary>A month in the calendar's year view, with how much happened in it.</summary>
public sealed record CalendarMonthCell(DateTime Month, int Count, bool IsCurrent)
{
    public string Text => Month.ToString("MMM");
    public string Label => Month.ToString("MMMM yyyy");
    public bool HasAny => Count > 0;
    public string CountText => Count > 0 ? Count.ToString("N0") : "";
}

/// <summary>A line of "This PC now": what it runs, and since when where a change to it was seen.</summary>
public sealed record NowFact(string Label, string Value, string? Since);

/// <summary>
/// The Timeline page: what changed on the PC day by day (drivers, Windows, apps, startup programs, hardware, settings,
/// drive space) with the problems that happened in between, a calendar to jump around in, and the PC as it is now.
/// </summary>
/// <param name="scan">Has the agent look for new changes and problems now; done when it has (or it can't be reached).</param>
public sealed partial class TimelineViewModel(ReportService reports, Action<DateTime> openCrashes, Func<Task>? scan = null) : ObservableObject
{
    /// <summary>
    /// Days built at a time: the newest first (about a screenful, so opening the page or changing the filter is quick),
    /// older ones as the list is scrolled.
    /// </summary>
    internal const int PageSize = 6;

    /// <summary>From this many app updates in a day they fold into one line (when grouping is on).</summary>
    private const int GroupFrom = 3;

    private List<SystemChange> _changes = [];
    private List<TimelineProblem> _problems = [];
    private Dictionary<DateTime, Core.Reports.ChangeEffect> _effects = [];
    private string _signature = "";

    // Under the current filter: every row with something on it (newest first), and what's on each. A row is a day, or
    // grouped by month a month (its 1st); the calendar always goes by the days (_onDay).
    private List<DateTime> _dayOrder = [];
    private Dictionary<DateTime, (List<SystemChange> Changes, List<TimelineProblem> Problems)> _byDay = [];
    private Dictionary<DateTime, (List<SystemChange> Changes, List<TimelineProblem> Problems)> _onDay = [];
    private bool _byMonth;
    private readonly HashSet<(DateTime Row, string Line)> _expanded = [];

    private const string ProblemIcon = "";

    public ObservableCollection<TimelineDay> Days { get; } = [];

    [ObservableProperty] private List<TimelineFilter> _filters = [];
    [ObservableProperty] private TimelineFilter? _filter;

    /// <summary>
    /// How the list is grouped: "Day" (a row a day, three or more app updates on it one line; the default), "Month" (a
    /// row a month, each kind of change in it one line) or "Each" (a row a day, every change on its own line).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupUpdates))]
    private string _grouping = "Day";

    /// <summary>Changes are folded (by day or by month), not one line each.</summary>
    public bool GroupUpdates
    {
        get => Grouping != "Each";
        set => Grouping = value ? "Day" : "Each";
    }

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private List<NowFact> _now = [];

    /// <summary>Loaded once: later visits only rebuild if something changed.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Raised to scroll the list to a day (already built), or to the top (null).</summary>
    public event Action<TimelineDay?>? JumpRequested;

    private bool _applying;

    partial void OnFilterChanged(TimelineFilter? value)
    {
        if (!_applying) Rebuild(keepPlace: false);
    }

    partial void OnGroupingChanged(string value) => Rebuild(keepPlace: false);

    /// <summary>
    /// What's typed in the search box: only the changes (and problems) that say it are listed, over all of the history
    /// and within the kind picked. An app's name, a version old or new, a word of the line.
    /// </summary>
    [ObservableProperty] private string _search = "";

    partial void OnSearchChanged(string value) => Rebuild(keepPlace: false);

    /// <param name="onlyIfChanged">The minute refresh: leave the page alone unless something new was recorded.</param>
    public async Task LoadAsync(bool onlyIfChanged = false)
    {
        if (await reports.TimelineAsync() is { } data) Apply(data, onlyIfChanged);
    }

    /// <summary>The refresh button was pressed and the PC is being looked over.</summary>
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>
    /// Looks for changes now: the PC is otherwise checked every ten minutes, so something just installed isn't here yet.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            // Seen turning for a moment even when the check is over at once and finds nothing: something did happen.
            var seen = Task.Delay(600);
            if (scan is not null) await scan();
            await LoadAsync();
            await seen;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    internal void Apply(TimelineData data, bool onlyIfChanged = false)
    {
        string signature = $"{data.Changes.Count}|{data.Changes.Sum(c => c.Id)}|{data.Changes.Count(c => c.Kind == ChangeKind.Storage)}|{data.Problems.Count}|{DateTime.Today:yyyyMMdd}|{string.Join('|', data.Effects.OrderBy(e => e.Key).SelectMany(e => e.Value.Lines))}";
        bool same = signature == _signature;
        _signature = signature;
        Now = BuildNow(data);
        if (same && (onlyIfChanged || Loaded)) return;

        _changes = data.Changes;
        _problems = data.Problems;
        _effects = data.Effects;
        Loaded = true;
        Rebuild(keepPlace: Days.Count > 0);
    }

    // ── The list ────────────────────────────────────────────────────────

    private static readonly (string Key, string Name, ChangeKind[] Kinds)[] Kinds =
    [
        ("drivers", "Drivers", [ChangeKind.Driver]),
        ("windows", "Windows", [ChangeKind.WindowsUpdate, ChangeKind.Windows]),
        ("apps", "Apps", [ChangeKind.AppInstalled, ChangeKind.AppRemoved, ChangeKind.AppUpdated]),
        ("startup", "Startup programs", [ChangeKind.Startup]),
        ("hardware", "Hardware", [ChangeKind.Hardware, ChangeKind.Firmware]),
        ("settings", "Settings", [ChangeKind.Setting]),
        ("storage", "Drive space", [ChangeKind.Storage]),
    ];

    /// <summary>Works everything out again for the data, the filter and the grouping.</summary>
    private void Rebuild(bool keepPlace)
    {
        // The kinds there are, with their counts; the one picked stays picked.
        string key = Filter?.Key ?? "all";
        var filters = new List<TimelineFilter> { new("all", "Everything", _changes.Count) };
        filters.AddRange(Kinds.Select(k => new TimelineFilter(k.Key, k.Name, _changes.Count(c => k.Kinds.Contains(c.Kind)))).Where(f => f.Count > 0 || f.Key == key));
        // The same kinds and counts (only the filter or the grouping changed): the dropdown keeps its list.
        if (!filters.SequenceEqual(Filters) || Filter is null)
        {
            _applying = true;
            Filters = filters;
            Filter = filters.FirstOrDefault(f => f.Key == key) ?? filters[0];
            _applying = false;
        }

        bool all = Filter.Key == "all";
        var kinds = Kinds.FirstOrDefault(k => k.Key == Filter.Key).Kinds;
        var changes = all ? _changes : [.. _changes.Where(c => kinds.Contains(c.Kind))];
        // Problems are context for everything together; a list of one kind is just that kind.
        var problems = all ? _problems : [];
        var words = Core.TextMatch.Words(Search);
        if (words.Length > 0)
        {
            changes = [.. changes.Where(c => Core.TextMatch.Has(words, c.Title, c.Was, c.Now))];
            problems = [.. problems.Where(p => Core.TextMatch.Has(words, p.Title))];
        }

        _onDay = [];
        foreach (var c in changes) DayOf(c.Time.Date).Changes.Add(c);
        foreach (var p in problems) DayOf(p.Time.Date).Problems.Add(p);
        // By month: a row a month with everything that happened in it; otherwise a row a day.
        _byMonth = Grouping == "Month";
        _byDay = !_byMonth ? _onDay : _onDay.GroupBy(d => new DateTime(d.Key.Year, d.Key.Month, 1)).ToDictionary(g => g.Key,
            g => (Changes: g.SelectMany(d => d.Value.Changes).ToList(), Problems: g.SelectMany(d => d.Value.Problems).ToList()));
        _dayOrder = [.. _byDay.Keys.OrderByDescending(d => d)];

        int shown = keepPlace ? Math.Max(Days.Count, PageSize) : PageSize;
        Days.Clear();
        AddDays(shown);

        IsEmpty = _dayOrder.Count == 0;
        EmptyText = words.Length > 0 ? "Nothing matches" : all ? "No changes yet" : "Nothing of this kind has changed";
        CountText = changes.Count == 0 ? ""
            : words.Length > 0 ? $"{changes.Count:N0} change{(changes.Count == 1 ? "" : "s")} match{(changes.Count == 1 ? "es" : "")}"
            : $"{changes.Count:N0} change{(changes.Count == 1 ? "" : "s")} since {changes.Min(c => c.Time):d MMM yyyy}";

        if (!keepPlace)
        {
            CalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            IsYearView = false;
            JumpRequested?.Invoke(null);
        }
        BuildCalendar();

        (List<SystemChange> Changes, List<TimelineProblem> Problems) DayOf(DateTime day) =>
            _onDay.TryGetValue(day, out var d) ? d : _onDay[day] = ([], []);
    }

    public bool HasMore => Days.Count < _dayOrder.Count;

    /// <summary>Called by the list's infinite scroll near the bottom.</summary>
    [RelayCommand]
    private void ShowMore() => AddDays(PageSize);

    /// <summary>Back to the newest days, when more were built by scrolling (leaving the page).</summary>
    public void ShowFirstPage()
    {
        while (Days.Count > PageSize) Days.RemoveAt(Days.Count - 1);
        OnPropertyChanged(nameof(HasMore));
    }

    private void AddDays(int count)
    {
        for (int i = 0; i < count && Days.Count < _dayOrder.Count; i++)
        {
            var day = _dayOrder[Days.Count];
            // A heading above the first row of each month (by month: of each year).
            bool heading = Days.Count == 0 || Days[^1].Day.Year != day.Year || (!_byMonth && Days[^1].Day.Month != day.Month);
            Days.Add(_byMonth ? BuildMonth(day, heading) : BuildDay(day, heading));
        }
        OnPropertyChanged(nameof(HasMore));
    }

    private TimelineDay BuildDay(DateTime day, bool newMonth)
    {
        var (changes, problems) = _byDay[day];
        var entries = new List<(DateTime At, TimelineEntry Entry)>();

        // The same kind of problem several times in a day is one line ("3 graphics driver resets").
        foreach (var group in problems.GroupBy(p => p.Kind))
        {
            var first = group.MinBy(p => p.Time)!;
            int n = group.Count();
            string brush = first.IsCritical ? "HotBrush" : "WarmBrush";
            entries.Add((first.Time, new TimelineEntry
            {
                TimeText = first.Time.ToString("h:mm tt"), Icon = ProblemIcon, Brush = brush, IconBrush = brush, IsKey = true, CrashDay = day,
                Title = n == 1 ? first.Title : Capital(CrashesViewModel.KindCount(group.Key, n)),
            }));
        }

        var updates = changes.Where(c => c.Kind == ChangeKind.AppUpdated).OrderByDescending(c => c.Time).ToList();
        bool fold = Grouping == "Day" && updates.Count >= GroupFrom;
        // What was different afterwards goes under the day's biggest change (a graphics driver before a Windows update).
        var biggest = _effects.ContainsKey(day) ? changes.Where(Core.Reports.ChangeEffects.IsMajor).OrderBy(Rank).ThenBy(c => c.Time).FirstOrDefault() : null;
        foreach (var c in changes)
        {
            if (fold && c.Kind == ChangeKind.AppUpdated) continue;
            var entry = EntryOf(c);
            if (c == biggest)
            {
                entry.Effect = _effects[day].Lines;
                entry.EffectBrush = _effects[day].Tone switch { Core.Reports.EffectTone.Worse => "WarmBrush", Core.Reports.EffectTone.Better => "GoodBrush", _ => "MutedBrush" };
            }
            entries.Add((c.Time, entry));
        }
        if (fold)
        {
            entries.Add((updates[0].Time, new TimelineEntry
            {
                TimeText = TimeOf(updates[0]), TimeTip = TipOf(updates[0]), // when the last of them was updated
                Icon = IconOf(ChangeKind.AppUpdated), Title = $"{updates.Count} apps updated",
                Detail = string.Join(", ", updates.Select(NameOf)), Children = [.. updates.Select(c => EntryOf(c))], IsExpanded = _expanded.Contains((day, "")),
            }));
        }

        int ago = (int)(DateTime.Today - day).TotalDays;
        return new TimelineDay
        {
            Day = day,
            Title = ago switch { 0 => "Today", 1 => "Yesterday", _ => day.ToString("dddd d") },
            SubTitle = ago <= 1 ? day.ToString("dddd, d MMMM") : "",
            Month = newMonth ? day.ToString("MMMM yyyy").ToUpper(CultureInfo.CurrentCulture) : null,
            NodeBrush = changes.Count == 0 ? "HotBrush" : "CpuBrush",
            // Newest first; a drive's space is the whole day's (dated at its midnight) and comes last.
            Entries = [.. entries.OrderByDescending(e => e.At).Select(e => e.Entry)],
        };

        static int Rank(SystemChange c) => c.IsGraphicsDriver ? 0 : c.Kind switch { ChangeKind.Firmware => 1, ChangeKind.Hardware => 2, ChangeKind.Windows => 3, _ => 4 };
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    // "7-Zip (x64) updated to 26.01" is "7-Zip (x64)" in the folded line.
    private static string NameOf(SystemChange c) => c.Title.Split(" updated")[0];

    /// <summary>A month's lines, in this order under its problems: what a PC-wide problem usually comes from first.</summary>
    private static readonly (string Key, ChangeKind[] Kinds, string Many)[] MonthLines =
    [
        ("drivers", [ChangeKind.Driver], "driver updates"),
        ("windows", [ChangeKind.WindowsUpdate, ChangeKind.Windows], "Windows updates"),
        ("hardware", [ChangeKind.Hardware, ChangeKind.Firmware], "hardware changes"),
        ("installed", [ChangeKind.AppInstalled], "apps installed"),
        ("removed", [ChangeKind.AppRemoved], "apps removed"),
        ("updated", [ChangeKind.AppUpdated], "app updates"),
        ("startup", [ChangeKind.Startup], "startup changes"),
        ("settings", [ChangeKind.Setting], "settings changed"),
        ("storage", [ChangeKind.Storage], "drive space changes"),
    ];

    /// <summary>
    /// A month in one row: each kind of problem and each kind of change in it one line, with how many, which, and the
    /// days they span; every one with its date on request. One of a kind is just that change, with its date.
    /// </summary>
    private TimelineDay BuildMonth(DateTime month, bool newYear)
    {
        var (changes, problems) = _byDay[month];
        var entries = new List<TimelineEntry>();
        foreach (var group in problems.GroupBy(p => p.Kind))
        {
            var last = group.MaxBy(p => p.Time)!;
            int n = group.Count();
            string brush = group.Any(p => p.IsCritical) ? "HotBrush" : "WarmBrush";
            entries.Add(new TimelineEntry
            {
                TimeText = Span(group.Select(p => p.Time)), Icon = ProblemIcon, Brush = brush, IconBrush = brush, IsKey = true, CrashDay = last.Time.Date,
                Title = n == 1 ? last.Title : Capital(CrashesViewModel.KindCount(group.Key, n)),
            });
        }
        foreach (var (key, kinds, many) in MonthLines)
        {
            var of = changes.Where(c => kinds.Contains(c.Kind)).OrderByDescending(c => c.Time).ToList();
            if (of.Count == 0) continue;
            if (of.Count == 1)
            {
                entries.Add(EntryOf(of[0], Span([of[0].Time])));
                continue;
            }
            // App updates by app, the most updated first; the rest as they're worded, newest first.
            bool updates = key == "updated";
            var names = updates
                ? of.GroupBy(NameOf).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList()
                : [.. of.Select(c => c.Title)];
            int shown = updates ? 8 : 3;
            entries.Add(new TimelineEntry
            {
                Key = key, TimeText = Span(of.Select(c => c.Time)), Icon = IconOf(of[0].Kind), Title = $"{of.Count} {many}", IsKey = of[0].IsSystemLevel,
                Detail = string.Join(", ", names.Take(shown)) + (names.Count > shown ? $" and {names.Count - shown} more" : ""),
                Children = [.. of.Select(c => new TimelineEntry { Title = c.Title, Detail = $"{c.Time:d MMM}{(c.Was is null ? "" : $" · was {c.Was}")}" })],
                ShowText = "Show all", HideText = "Hide", IsExpanded = _expanded.Contains((month, key)),
            });
        }
        return new TimelineDay
        {
            Day = month, IsSummary = true, Title = month.ToString("MMMM"), Month = newYear ? month.ToString("yyyy") : null,
            NodeBrush = changes.Count == 0 ? "HotBrush" : "CpuBrush", Entries = entries,
        };

        // The days they fell on: "9 Sep", or "3–20 Sep".
        static string Span(IEnumerable<DateTime> times)
        {
            DateTime first = times.Min().Date, last = times.Max().Date;
            return first == last ? $"{first:d MMM}" : $"{first:%d}–{last:d MMM}";
        }
    }

    /// <param name="when">Said in place of its time of day (under a month: its date).</param>
    private static TimelineEntry EntryOf(SystemChange c, string? when = null) => new()
    {
        TimeText = when ?? TimeOf(c),
        TimeTip = TipOf(c),
        Icon = IconOf(c.Kind),
        Title = c.Title,
        // An app just installed has no "before": its version is the detail.
        Detail = c.Kind == ChangeKind.AppInstalled ? c.Now : c.Detail,
        IsKey = c.IsSystemLevel,
    };

    /// <summary>
    /// When it happened, as exactly as it's known: a drive's space is compared day to day ("All day"); a change found
    /// by a check of the PC happened in the minutes before that check ("~7:20 PM"); the rest is to the minute.
    /// </summary>
    private static string TimeOf(SystemChange c) =>
        c.Kind == ChangeKind.Storage ? "All day" : c.IsApproximate ? $"~{c.Time:h:mm tt}" : c.Time.ToString("h:mm tt");

    private static string? TipOf(SystemChange c) => c.Kind == ChangeKind.Storage ? "The space in use at the end of this day against the day before"
        : c.IsApproximate ? $"Between {Moment(c.Earliest, c.Time)} and {c.Time:h:mm tt}" : null;

    /// <summary>A time of the same day, or with its day when the check before was on an earlier one (the PC was off overnight).</summary>
    private static string Moment(DateTime t, DateTime sameDayAs) => t.Date == sameDayAs.Date ? t.ToString("h:mm tt") : t.ToString("ddd d MMM, h:mm tt");

    private static string IconOf(ChangeKind kind) => kind switch
    {
        ChangeKind.Driver => "",
        ChangeKind.WindowsUpdate or ChangeKind.Windows => "",
        ChangeKind.AppInstalled => "",
        ChangeKind.AppRemoved => "",
        ChangeKind.AppUpdated => "",
        ChangeKind.Startup => "",
        ChangeKind.Hardware or ChangeKind.Firmware => "",
        ChangeKind.Setting => "",
        _ => "",
    };

    [RelayCommand]
    private void ToggleVersions(TimelineEntry entry)
    {
        entry.IsExpanded = !entry.IsExpanded;
        if (Days.FirstOrDefault(d => d.Entries.Contains(entry)) is not { } day) return;
        if (entry.IsExpanded) _expanded.Add((day.Day, entry.Key)); else _expanded.Remove((day.Day, entry.Key));
    }

    [RelayCommand]
    private void OpenCrashes(TimelineEntry entry)
    {
        if (entry.CrashDay is { } day) openCrashes(day);
    }

    // ── The calendar ────────────────────────────────────────────────────

    /// <summary>The month the calendar shows (its 1st), or any month of the year it shows.</summary>
    [ObservableProperty] private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    /// <summary>Zoomed out: the twelve months of a year instead of a month's days.</summary>
    [ObservableProperty] private bool _isYearView;

    [ObservableProperty] private string _calendarTitle = "";
    [ObservableProperty] private List<string> _weekdays = [];
    [ObservableProperty] private List<CalendarCell> _calendarCells = [];
    [ObservableProperty] private List<CalendarMonthCell> _calendarMonths = [];
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;

    private DateTime FirstMonth => _dayOrder.Count > 0 ? new DateTime(_dayOrder[^1].Year, _dayOrder[^1].Month, 1) : ThisMonth;
    private static DateTime ThisMonth => new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private void BuildCalendar()
    {
        var m = CalendarMonth;
        if (IsYearView)
        {
            CalendarTitle = m.Year.ToString();
            CalendarMonths = [.. Enumerable.Range(1, 12).Select(i =>
            {
                var month = new DateTime(m.Year, i, 1);
                int n = _onDay.Where(d => d.Key.Year == m.Year && d.Key.Month == i).Sum(d => d.Value.Changes.Count + d.Value.Problems.Count);
                return new CalendarMonthCell(month, n, month == m);
            })];
            CanGoBack = m.Year > FirstMonth.Year;
            CanGoForward = m.Year < DateTime.Today.Year;
            return;
        }

        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        int firstDay = (int)format.FirstDayOfWeek;
        if (Weekdays.Count == 0) Weekdays = [.. Enumerable.Range(0, 7).Select(i => format.ShortestDayNames[(firstDay + i) % 7][..1].ToUpper(CultureInfo.CurrentCulture))];
        // The month already shown keeps its cells (they're told what changed); another month gets new ones.
        var cells = CalendarCells;
        if (cells.FirstOrDefault(c => c.Day is not null)?.Day != m)
        {
            cells = [];
            for (int i = ((int)m.DayOfWeek - firstDay + 7) % 7; i > 0; i--) cells.Add(new CalendarCell(null));
            for (var d = m; d.Month == m.Month; d = d.AddDays(1)) cells.Add(new CalendarCell(d));
        }
        foreach (var cell in cells)
        {
            if (cell.Day is not { } d) continue;
            _onDay.TryGetValue(d, out var day);
            cell.IsToday = d == DateTime.Today;
            cell.HasAny = day.Changes is not null;
            cell.OnlyProblems = day.Changes is { Count: 0 };
            if (day.Changes is null)
            {
                cell.Tip = null;
                continue;
            }
            // Hovering a day says what's on it.
            var lines = day.Problems.GroupBy(p => p.Kind).Select(g => g.Count() == 1 ? g.First().Title : CrashesViewModel.KindCount(g.Key, g.Count()))
                .Concat(day.Changes.OrderByDescending(c => c.IsSystemLevel).Take(5).Select(c => c.Title)).ToList();
            if (day.Changes.Count > 5) lines.Add($"and {day.Changes.Count - 5} more");
            cell.Tip = string.Join("\n", lines);
        }
        CalendarTitle = m.ToString("MMMM yyyy");
        if (!ReferenceEquals(cells, CalendarCells)) CalendarCells = cells;
        CanGoBack = m > FirstMonth;
        CanGoForward = m < ThisMonth;
    }

    [RelayCommand]
    private void GoBack() => Step(-1);

    [RelayCommand]
    private void GoForward() => Step(1);

    private void Step(int by)
    {
        if (by < 0 ? !CanGoBack : !CanGoForward) return;
        CalendarMonth = IsYearView ? CalendarMonth.AddYears(by) : CalendarMonth.AddMonths(by);
        BuildCalendar();
    }

    /// <summary>A click on the month's name: the whole year, to pick another month.</summary>
    [RelayCommand]
    private void ZoomOut()
    {
        IsYearView = true;
        BuildCalendar();
    }

    [RelayCommand]
    private void PickMonth(CalendarMonthCell cell)
    {
        CalendarMonth = cell.Month;
        IsYearView = false;
        BuildCalendar();
        JumpTo(cell.Month.AddMonths(1).AddTicks(-1));
    }

    [RelayCommand]
    private void PickDay(CalendarCell cell)
    {
        if (cell is { Day: { } day, HasAny: true }) JumpTo(day);
    }

    /// <summary>Scrolls the list to a day (or the nearest one before it with anything on it; by month, to its month),
    /// building down to it first.</summary>
    public void JumpTo(DateTime day)
    {
        int index = _dayOrder.FindIndex(d => d <= day);
        if (index < 0) return;
        if (index >= Days.Count) AddDays(index - Days.Count + PageSize);
        JumpRequested?.Invoke(Days[index]);
    }

    /// <summary>The list was scrolled: the calendar follows the month being read.</summary>
    public void OnTopDay(DateTime day)
    {
        var month = new DateTime(day.Year, day.Month, 1);
        if (IsYearView || month == CalendarMonth) return;
        CalendarMonth = month;
        BuildCalendar();
    }

    // ── This PC now ─────────────────────────────────────────────────────

    private static List<NowFact> BuildNow(TimelineData data)
    {
        var facts = new List<NowFact>();
        var items = data.Inventory.ToLookup(i => i.Kind);
        string? Since(Func<SystemChange, bool> match) =>
            data.Changes.LastOrDefault(match) is { } c ? $"since {c.Time.ToString(c.Time.Year == DateTime.Today.Year ? "d MMM" : "d MMM yyyy")}" : null;

        if (items[Inventory.Windows].FirstOrDefault() is { } windows)
            facts.Add(new("Windows", $"{windows.Name.Replace("Windows ", "")} {windows.Value}", Since(c => c.Kind == ChangeKind.Windows)));
        if (items[Inventory.GpuDriver].FirstOrDefault() is { } driver)
            facts.Add(new("Graphics driver", driver.Value, Since(c => c.IsGraphicsDriver)));
        if (items[Inventory.Bios].FirstOrDefault() is { } bios)
            facts.Add(new("BIOS", bios.Value, Since(c => c.Kind == ChangeKind.Firmware)));
        if (items[Inventory.Ram].FirstOrDefault() is { } ram) facts.Add(new("Memory", ram.Value, null));
        if (items[Inventory.Setting].FirstOrDefault(i => i.Key == Inventory.PowerPlan) is { } plan)
            facts.Add(new("Power plan", plan.Value, Since(c => c.Subject == $"{Inventory.Setting}:{Inventory.PowerPlan}")));
        if (items[Inventory.App].Any()) facts.Add(new("Apps installed", items[Inventory.App].Count().ToString("N0"), null));
        if (items[Inventory.Startup].Any()) facts.Add(new("Start with Windows", items[Inventory.Startup].Count(i => i.Value == Inventory.On).ToString("N0"), null));
        return facts;
    }
}

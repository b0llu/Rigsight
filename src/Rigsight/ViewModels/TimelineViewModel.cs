using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Stability;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A kind of change to show, with how many there are ("Drivers (4)").</summary>
public sealed record TimelineFilter(string Key, string Name, int Count)
{
    public string Label => $"{Name} ({Count:N0})";
}

/// <summary>A kind of line ticked or not under the page's Fold list: two or more of it on a day fold into one line.</summary>
public sealed partial class FoldChoice(string key, string name, bool on, Action changed) : ObservableObject
{
    public string Key => key;
    public string Name => Lang.T(name);

    [ObservableProperty] private bool _isOn = on;

    partial void OnIsOnChanged(bool value) => changed();
}

/// <summary>One line under a day: a change, a problem, or the kinds ticked under Fold folded into one line. Under a
/// month: one kind of change that month (its drivers, its app updates) folded into one.</summary>
public sealed partial class TimelineEntry : ObservableObject
{
    public string TimeText { get; init; } = "";

    /// <summary>Said on hover when the time isn't the exact moment ("Between 7:10 PM and 7:20 PM").</summary>
    public string? TimeTip { get; init; }
    public string Icon { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>A game's update: the game it's about (see <see cref="Core.Games.GameEntry.Key"/>), which the line opens.</summary>
    public string? GameKey { get; init; }
    public string? Detail { get; init; }

    /// <summary>A muted line under the title (a stretch of sound dropouts' possible cause, a marked problem's device file).</summary>
    public string? Note { get; init; }
    public bool HasNote => Note is { Length: > 0 };

    /// <summary>A driver keeping the CPU busy much longer than usual (issue #15 follow-up): the whole line opens what happened.</summary>
    public Core.Drivers.DelayStretch? Delay { get; init; }
    public bool IsDelay => Delay is not null;

    /// <summary>Drivers, Windows and hardware stand out: they're what a PC-wide problem usually comes from.</summary>
    public bool IsKey { get; init; }

    /// <summary>The palette brush for the line: text for a change, warm or hot for a problem.</summary>
    public string Brush { get; init; } = "TextBrush";
    public string IconBrush { get; init; } = "MutedBrush";

    /// <summary>A problem's day, to open on the Crashes page.</summary>
    public DateTime? CrashDay { get; init; }

    /// <summary>The first day of the problems a line folds together ("12–15 Oct"): the Crashes page opens on all of them.</summary>
    public DateTime? CrashFrom { get; init; }

    /// <summary>The problem's own time, where the line is one problem: it opens as its own case.</summary>
    public DateTime? CrashTime { get; init; }
    public string OpenText => CrashTime is null ? Lang.T("See on Crashes") : Lang.T("Open");
    public bool IsProblem => CrashDay is not null;

    /// <summary>What this change made better or worse (see <see cref="Core.Reports.ChangeEffects"/>); null for nearly every change.</summary>
    public Core.Reports.ChangeResult? Result { get; set; }
    public bool HasResult => Result is not null;
    public string ResultBrush => Result?.Tone == Core.Reports.EffectTone.Worse ? "WarmBrush" : "GoodBrush";
    public string ResultIcon => Result is null ? "" : Models.ChangeCase.ResultIcon(Result);
    public string ResultSure => Result?.Sure.ToString() ?? "";
    public List<ResultPair> ResultPairs => Result is null ? [] : [.. Result.Pairs.Select(ResultPair.Of)];
    /// <summary>A change that made something worse: its AI button asks whether the change is why ("Is it the driver?").</summary>
    public bool HasResultAsk => Result?.Tone == Core.Reports.EffectTone.Worse;
    public string ResultAskText => Result?.Changes is [{ IsGraphicsDriver: true }] ? Lang.T("Is it the driver?") : Lang.T("Did this cause it?");
    public AiQuestion? ResultAsk => Result is { Tone: Core.Reports.EffectTone.Worse } r
        ? new(Lang.T($"{r.Said}. Did {Core.Reports.ChangeEffects.InLine(r.Changes, r.Day)} cause this?"), Core.Ai.AiPages.Timeline) : null;

    // ── The before and after card (approved mock-up B, 2026-11-24): the figures in a soft panel, how sure under them ──

    /// <summary>
    /// The card's figures: only what moved. One reading: each side as a figure of its own under its window ("11 DAYS
    /// BEFORE" / "57°" / "4h 29m of play"). Several: each reading with its before and after ("LONGEST, USUALLY" / "1.1 → 2.9 ms").
    /// </summary>
    public List<CardFigure> CardFigures
    {
        get
        {
            if (Result is not { } r) return [];
            var moved = r.Pairs.Where(p => p.Tone != Core.Reports.EffectTone.Same).ToList();
            if (moved.Count == 0) moved = [.. r.Pairs.Take(1)];
            string Brush(Core.Reports.ChangeFigure f) => f.Tone switch { Core.Reports.EffectTone.Worse => "WarmBrush", Core.Reports.EffectTone.Better => "GoodBrush", _ => "TextBrush" };
            if (moved.Count > 1)
                return [.. moved.Select(f => new CardFigure(f.Label.ToUpper(System.Globalization.CultureInfo.CurrentCulture), f.Before, f.After, Brush(f), null))];
            var one = moved[0];
            // Each side's own amount: the play compared for a temperature, the hours of use for a count of problems.
            var before = r.Days.Where(d => d.Day < r.Day).ToList();
            var after = r.Days.Where(d => d.Day > r.Day).ToList();
            string? Sub(List<Core.Reports.ChangeDay> days) =>
                one.Problems ? Lang.T($"{days.Sum(d => d.Hours):0}h of use")
                : r.TempLabel is not null && days.Sum(d => d.Minutes) is int m and > 0 ? Lang.T($"{Core.Units.Duration(m * 60.0)} of play") : null;
            string was = r.DaysBefore == 1 ? Lang.T("1 DAY BEFORE") : Lang.T($"{r.DaysBefore} DAYS BEFORE");
            string now = r.Open ? Lang.T($"{r.DaysAfter} DAYS SINCE") : Lang.T($"{r.DaysAfter} DAYS AFTER");
            return [new(was, null, one.Before, "MutedBrush", Sub(before)), new(now, null, one.After, Brush(one), Sub(after))];
        }
    }

    /// <summary>How sure, its first sentence bold: "Possibly, not likely." and the reason after it.</summary>
    public string SureHead => Result?.SureLine is { Length: > 0 } s && s.IndexOf(". ", StringComparison.Ordinal) is int i and > 0 ? s[..(i + 1)] : Result?.SureLine ?? "";
    public string SureRest => Result?.SureLine is { Length: > 0 } s && s.IndexOf(". ", StringComparison.Ordinal) is int i and > 0 ? s[(i + 1)..] : "";

    /// <summary>The lines folded into this one, newest first: each at its own time when opened.</summary>
    public List<TimelineEntry> Children { get; init; } = [];
    public bool HasChildren => Children.Count > 0;

    /// <summary>A folded line's own line opens something (a crash, a driver mark, a game): lit on hover, with an arrow.</summary>
    public bool CanOpen => IsDelay || IsProblem || GameKey is not null || DriverKey is not null;

    /// <summary>What a screen reader says for a folded line: its title and its grey part.</summary>
    public string SpokenName => Detail is { Length: > 0 } d ? $"{Title}, {d}" : Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isExpanded;

    /// <summary>What opening and closing the folded lines is called (a month's line holds changes, not versions).</summary>
    public string ShowText { get; init; } = Lang.T("Show versions");
    public string HideText { get; init; } = Lang.T("Hide versions");
    public string ToggleText => IsExpanded ? HideText : ShowText;

    /// <summary>Which of a row's folded lines this is (a month has one per kind of change), to keep it open when rebuilt.</summary>
    public string Key { get; init; } = "";
}

/// <summary>
/// One figure in the before and after card's panel: a small caps label over the value (with the value before it and an
/// arrow, when <see cref="Was"/> is set), and a quieter line under it.
/// </summary>
public sealed record CardFigure(string Label, string? Was, string Now, string NowBrush, string? Sub)
{
    public bool HasWas => Was is not null;
    public bool HasSub => Sub is { Length: > 0 };
}

/// <summary>A reading before a change and since, for its card: "GPU in Crimson Desert  71° → 77°".</summary>
public sealed record ResultPair(string Label, string Before, string After, string AfterBrush)
{
    public static ResultPair Of(Core.Reports.ChangeFigure f) => new(f.Label, f.Before, f.After,
        f.Tone switch { Core.Reports.EffectTone.Worse => "WarmBrush", Core.Reports.EffectTone.Better => "GoodBrush", _ => "TextBrush" });
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
/// A day in the calendar (or a blank before the 1st). What's on it can change while the month stays (another filter),
/// and another month moves it to another day: the cell is kept and told, so the calendar's buttons aren't built again.
/// </summary>
public sealed partial class CalendarCell(DateTime? day) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    [NotifyPropertyChangedFor(nameof(Label))]
    private DateTime? _day = day;

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
    public string Label => TimeText.At(Month, "MMMM yyyy");
    public bool HasAny => Count > 0;
    public string CountText => Count > 0 ? Count.ToString("N0") : "";
}

/// <summary>
/// A line of "This PC now": what it runs, and since when where a change to it was seen; with a link to what that change
/// made better or worse, where it did.
/// </summary>
public sealed record NowFact(string Label, string Value, string? Since)
{
    public DateTime? ResultDay { get; init; }
    public string? ResultText { get; init; }
    public string ResultBrush { get; init; } = "MutedBrush";

    /// <summary>What the line opens inside Timeline: "drivers" (every driver) or "driver:" and a device (null: nothing).</summary>
    public string? Opens { get; init; }
    public bool CanOpen => Opens is not null;
    /// <summary>An arrow after the value (the list of every driver); a line opening one driver has none (approved mock-up v8).</summary>
    public bool HasArrow { get; init; }
}

/// <summary>
/// The Timeline page: what changed on the PC day by day (drivers, Windows, apps, startup programs, hardware, settings,
/// drive space) with the problems that happened in between, a calendar to jump around in, and the PC as it is now.
/// </summary>
/// <param name="scan">Has the agent look for new changes and problems now; done when it has (or it can't be reached).</param>
/// <param name="openProblems">Opens the Crashes page on a span of days with the PC's own problems only (see CrashesViewModel.ShowPcProblems).</param>
/// <param name="settings">Keeps the Group choice and the Fold list (none in tests: the defaults, kept for the visit).</param>
public sealed partial class TimelineViewModel(ReportService reports, Action<DateTime> openCrashes, Func<Task>? scan = null, Action<DateTime>? openCrash = null,
    Action<DateTime, DateTime>? openProblems = null, SettingsModel? settings = null) : ObservableObject
{
    /// <summary>
    /// Days built at first: the newest (about a screenful, so opening the page or changing the filter is quick); older
    /// ones are added one at a time as the list is scrolled (see ShowMore).
    /// </summary>
    internal const int PageSize = 6;

    /// <summary>
    /// The kinds of line that can fold into one on a day, as the Fold list names them. Anything else (drivers, Windows,
    /// installs, startup, hardware...) never folds: it's what people look through when something breaks.
    /// </summary>
    internal static readonly (string Key, string Name)[] FoldKinds =
    [
        ("sound", Lang.Key("Sound cut-outs")), ("apps", Lang.Key("App updates")), ("games", Lang.Key("Game updates")),
        ("problems", Lang.Key("Repeated problems")), ("delays", Lang.Key("Driver delays")), ("marks", Lang.Key("Problems you marked")),
    ];

    /// <summary>
    /// The Fold list: each kind ticked or not, as saved; sound cut-outs alone until someone changes it (the user,
    /// 2026-11-26: "by default, only the sound should be folded"). A change rebuilds the days and is kept for good.
    /// </summary>
    public List<FoldChoice> FoldChoices => _foldChoices ??= [.. FoldKinds.Select(k => new FoldChoice(k.Key, k.Name,
        settings?.Current.Timeline.Folds is { } saved ? saved.Contains(k.Key) : k.Key == "sound", OnFoldsChanged))];
    private List<FoldChoice>? _foldChoices;

    /// <summary>Beside "Fold" when not every kind is ticked: "1 of 6".</summary>
    public string FoldText
    {
        get
        {
            int on = FoldChoices.Count(c => c.IsOn), all = FoldChoices.Count;
            return on == all ? "" : Lang.T($"{on} of {all}");
        }
    }

    public bool HasFoldText => FoldText.Length > 0;

    private bool IsFolded(string kind) => FoldChoices.FirstOrDefault(c => c.Key == kind)?.IsOn == true;

    private void OnFoldsChanged()
    {
        OnPropertyChanged(nameof(FoldText));
        OnPropertyChanged(nameof(HasFoldText));
        settings?.Update(s => s.Timeline.Folds = [.. FoldChoices.Where(c => c.IsOn).Select(c => c.Key)]);
        Rebuild(keepPlace: true);
    }

    private List<SystemChange> _changes = [];
    private List<TimelineProblem> _problems = [];
    private List<TimelineMark> _marks = [];
    private Dictionary<DateTime, Core.Reports.ChangeResult> _results = [];
    private string _signature = "";

    // Under the current filter: every row with something on it (newest first), and what's on each. A row is a day, or
    // grouped by month a month (its 1st); the calendar always goes by the days (_onDay).
    private List<DateTime> _dayOrder = [];
    private Dictionary<DateTime, (List<SystemChange> Changes, List<TimelineProblem> Problems, List<TimelineMark> Marks)> _byDay = [];
    private Dictionary<DateTime, (List<SystemChange> Changes, List<TimelineProblem> Problems, List<TimelineMark> Marks)> _onDay = [];
    private bool _byMonth;
    private readonly HashSet<(DateTime Row, string Line)> _expanded = [];

    /// <summary>A problem marked from the tray's "Something just went wrong": a flag, in the palette's purple.</summary>
    private const string MarkIcon = "\uE7C1";

    /// <summary>A stretch of sound dropouts: a speaker, warm.</summary>
    private const string SoundIcon = "\uE995";

    /// <summary>A driver keeping the CPU busy much longer than usual: a processor, in the marks' purple.</summary>
    private const string DelayIcon = "\uE9D9";

    private const string ProblemIcon = "";

    public ObservableCollection<TimelineDay> Days { get; } = [];

    [ObservableProperty] private List<TimelineFilter> _filters = [];
    [ObservableProperty] private TimelineFilter? _filter;

    /// <summary>
    /// How the list is grouped: "Day" (a row a day, the kinds ticked under Fold one line each; the default), "Month" (a
    /// row a month, each kind of change in it one line) or "Each" (a row a day, every change on its own line). Kept from
    /// visit to visit (it went back to "Day" every time before 1.21.0).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupUpdates))]
    private string _grouping = settings?.Current.Timeline.Grouping is "Month" or "Each" ? settings.Current.Timeline.Grouping : "Day";

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

    /// <summary>Loaded once: later visits only rebuild if something changed. Until then the list says it's loading.</summary>
    [ObservableProperty] private bool _loaded;

    /// <summary>Raised to scroll the list to a day (already built), or to the top (null).</summary>
    public event Action<TimelineDay?>? JumpRequested;

    private bool _applying;

    partial void OnFilterChanged(TimelineFilter? value)
    {
        if (!_applying) Rebuild(keepPlace: false);
    }

    partial void OnGroupingChanged(string value)
    {
        settings?.Update(s => s.Timeline.Grouping = value);
        Rebuild(keepPlace: false);
    }

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
        string signature = $"{data.Changes.Count}|{data.Changes.Sum(c => c.Id)}|{data.Changes.Count(c => c.Kind == ChangeKind.Storage)}|{data.Problems.Count}|{data.Marks.Count}|{data.Marks.Sum(m => m.Count)}|{data.Marks.LastOrDefault()?.Time.Ticks}|{DateTime.Today:yyyyMMdd}|{string.Join('|', data.Results.OrderBy(e => e.Key).Select(e => e.Value.Headline + e.Value.Why))}"
            + $"|{data.Drivers?.Devices.Count}|{data.Drivers?.Steps.Count}|{data.Drivers?.Devices.Count(d => d.Gone)}";
        bool same = signature == _signature;
        _signature = signature;
        Now = Models.Kept.Or(Now, BuildNow(data));
        if (same && (onlyIfChanged || Loaded)) return;

        _changes = WithDrivers(data);
        _problems = data.Problems;
        _marks = data.Marks;
        _results = data.Results;
        Loaded = true;
        if (Opened is { } open) Opened = _results.TryGetValue(open.Day, out var still) ? CaseOf(still) : null;
        RefreshDriverViews();
        Rebuild(keepPlace: Days.Count > 0);
    }

    // ── The list ────────────────────────────────────────────────────────

    /// <summary>The filter that lists only the changes that made something better or worse.</summary>
    internal const string ResultsKey = "results";

    /// <summary>
    /// The filters that list only the problems marked from the tray, or only the drivers RigSight found keeping the CPU busy
    /// much longer than usual (the user, 2026-11-24: no way to see just the purple lines).
    /// </summary>
    internal const string MarkedKey = "marked", DelaysKey = "delays";

    private static bool IsMarked(TimelineMark m) => !m.Sound && m.Delay is null;

    private static readonly (string Key, string Name, ChangeKind[] Kinds)[] Kinds =
    [
        ("drivers", Lang.Key("Drivers"), [ChangeKind.Driver]),
        ("windows", "Windows", [ChangeKind.WindowsUpdate, ChangeKind.Windows]),
        ("apps", Lang.Key("Apps"), [ChangeKind.AppInstalled, ChangeKind.AppRemoved, ChangeKind.AppUpdated]),
        ("games", Lang.Key("Game updates"), [ChangeKind.GameUpdated]),
        ("startup", Lang.Key("Startup programs"), [ChangeKind.Startup]),
        ("hardware", Lang.Key("Hardware"), [ChangeKind.Hardware, ChangeKind.Firmware]),
        ("settings", Lang.Key("Settings"), [ChangeKind.Setting]),
        ("fans", Lang.Key("Fan speeds"), [ChangeKind.Fans]),
        ("storage", Lang.Key("Drive space"), [ChangeKind.Storage]),
        ("ended", Lang.Key("Ended from Processes"), [ChangeKind.TaskEnded]),
    ];

    /// <summary>The kinds of change a filter lists; null for one that isn't by kind ("all", "results").</summary>
    internal static IReadOnlyList<ChangeKind>? KindsOf(string key) => Kinds.FirstOrDefault(k => k.Key == key).Kinds;

    /// <summary>Works everything out again for the data, the filter and the grouping.</summary>
    private void Rebuild(bool keepPlace)
    {
        // The kinds there are, with their counts; the one picked stays picked.
        string key = Filter?.Key ?? "all";
        var filters = new List<TimelineFilter> { new("all", Lang.T("Everything"), _changes.Count) };
        // Only when a change made something better or worse: an empty "Before and after" would be a block for its own sake.
        if (_results.Count > 0 || key == ResultsKey) filters.Add(new(ResultsKey, Lang.T("Before and after"), _results.Count));
        // Only when there are some, like "Before and after".
        int marked = _marks.Count(IsMarked), delays = _marks.Count(m => m.Delay is not null);
        if (marked > 0 || key == MarkedKey) filters.Add(new(MarkedKey, Lang.T("Problems you marked"), marked));
        if (delays > 0 || key == DelaysKey) filters.Add(new(DelaysKey, Lang.T("Driver delays"), delays));
        filters.AddRange(Kinds.Select(k => new TimelineFilter(k.Key, Lang.T(k.Name), _changes.Count(c => k.Kinds.Contains(c.Kind)))).Where(f => f.Count > 0 || f.Key == key));
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
        bool marksOnly = Filter.Key is MarkedKey or DelaysKey;
        var changes = all ? _changes
            : marksOnly ? []
            : Filter.Key == ResultsKey ? [.. _changes.Where(c => _results.TryGetValue(c.Time.Date, out var r) && r.Changes.Contains(c))]
            : [.. _changes.Where(c => kinds.Contains(c.Kind))];
        // Problems, and the ones marked from the tray, are context for everything together; a list of one kind is just that
        // kind. A mark is never a change: it has no filter of its own, as problems have none.
        var problems = all ? _problems : [];
        var marks = all ? _marks
            : Filter.Key == MarkedKey ? [.. _marks.Where(IsMarked)]
            : Filter.Key == DelaysKey ? [.. _marks.Where(m => m.Delay is not null)] : [];
        var words = Core.TextMatch.Words(Search);
        if (words.Length > 0)
        {
            // Found by the words as saved or as shown (searching in Spanish finds "actualizado").
            changes = [.. changes.Where(c => Core.TextMatch.Has(words, c.Title, c.Was, c.Now) || Core.TextMatch.Has(words, c.Shown) || Core.TextMatch.Has(words, ShownOf(c)))];
            problems = [.. problems.Where(p => Core.TextMatch.Has(words, p.Title))];
            marks = [.. marks.Where(m => Core.TextMatch.Has(words, m.Text, m.Note))];
        }

        _onDay = [];
        foreach (var c in changes) DayOf(c.Time.Date).Changes.Add(c);
        foreach (var p in problems) DayOf(p.Time.Date).Problems.Add(p);
        foreach (var m in marks) DayOf(m.Time.Date).Marks.Add(m);
        // By month: a row a month with everything that happened in it; otherwise a row a day.
        _byMonth = Grouping == "Month";
        _byDay = !_byMonth ? _onDay : _onDay.GroupBy(d => new DateTime(d.Key.Year, d.Key.Month, 1)).ToDictionary(g => g.Key,
            g => (Changes: g.SelectMany(d => d.Value.Changes).ToList(), Problems: g.SelectMany(d => d.Value.Problems).ToList(), Marks: g.SelectMany(d => d.Value.Marks).ToList()));
        _dayOrder = [.. _byDay.Keys.OrderByDescending(d => d)];

        int shown = keepPlace ? Math.Max(Days.Count, PageSize) : PageSize;
        Days.Clear();
        AddDays(shown);

        IsEmpty = _dayOrder.Count == 0;
        EmptyText = words.Length > 0 ? Lang.T("Nothing matches") : all ? Lang.T("No changes yet") : Lang.T("Nothing of this kind has changed");
        int count = changes.Count;
        DateTime since = count == 0 ? default : changes.Min(c => c.Time);
        CountText = count == 0 ? ""
            : words.Length > 0 ? (count == 1 ? Lang.T("1 change matches") : Lang.T($"{count:N0} changes match"))
            : count == 1 ? Lang.T($"1 change since {since:d MMM yyyy}") : Lang.T($"{count:N0} changes since {since:d MMM yyyy}");

        if (!keepPlace)
        {
            CalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            IsYearView = false;
            JumpRequested?.Invoke(null);
        }
        BuildCalendar();

        (List<SystemChange> Changes, List<TimelineProblem> Problems, List<TimelineMark> Marks) DayOf(DateTime day) =>
            _onDay.TryGetValue(day, out var d) ? d : _onDay[day] = ([], [], []);
    }

    public bool HasMore => Days.Count < _dayOrder.Count;

    /// <summary>
    /// Called by the list's infinite scroll near the bottom: one older day. The list asks again after each until it's a
    /// screen ahead, so the window lays out a day at a time and draws and scrolls in between. Six at once paused scrolling
    /// for 30-130 ms at a time (measured 2026-11-08, a fresh visit on the real history).
    /// </summary>
    [RelayCommand]
    private void ShowMore() => AddDays(1);

    /// <summary>Back to the newest days, when more were built by scrolling (leaving the page).</summary>
    /// <param name="keep">The days to keep when a page of them isn't enough to fill the window: what it would only ask for again.</param>
    public void ShowFirstPage(int keep = PageSize)
    {
        while (Days.Count > Math.Max(PageSize, keep)) Days.RemoveAt(Days.Count - 1);
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
        var (changes, problems, marks) = _byDay[day];
        var entries = new List<(DateTime At, TimelineEntry Entry)>();

        // By day, two or more of a kind ticked under Fold are one line, opened to show each at its own time (the user,
        // 2026-11-26). Never while searching: each match is its own line. A line with something under it (a stretch's
        // possible cause, a change's before and after) never folds.
        bool folding = Grouping == "Day" && Search.Trim().Length == 0;
        bool Folds(string kind, int n) => folding && n >= 2 && IsFolded(kind);

        // The marks: sound cut-outs, driver delays and problems marked from the tray, each its own line at its own time
        // unless its kind folds.
        var sound = marks.Where(m => m.Sound && !(m.Note is { Length: > 0 })).ToList();
        var delays = marks.Where(m => m.Delay is not null).ToList();
        var marked = marks.Where(IsMarked).ToList();
        bool foldSound = Folds("sound", sound.Count), foldDelays = Folds("delays", delays.Count), foldMarked = Folds("marks", marked.Count);
        foreach (var m in marks)
        {
            if (foldSound && sound.Contains(m) || foldDelays && m.Delay is not null || foldMarked && IsMarked(m)) continue;
            entries.Add((m.Time, MarkEntry(m, TimeText.Time(m.Time))));
        }
        if (foldSound)
        {
            string span = Core.Sound.SoundDropouts.Span(sound.Min(m => m.Time), sound.Max(m => m.End > m.Time ? m.End : m.Time));
            entries.Add(Folded(day, "sound", SoundIcon, "WarmBrush", Core.Sound.SoundDropouts.Title(sound.Sum(m => m.Count), null),
                Core.Sound.SoundDropouts.DayDetail(span, [.. sound.Select(m => (m.App, m.Count))]),
                [.. sound.Select(m => (m.Time, new TimelineEntry
                {
                    TimeText = TimeText.Time(m.Time), IconBrush = "WarmBrush", Title = Core.Sound.SoundDropouts.Short(m.Count, m.App),
                    Detail = Core.Data.TimeUtil.LocalMinuteStart(m.End) > Core.Data.TimeUtil.LocalMinuteStart(m.Time) ? Lang.T($"to {TimeText.Time(m.End)}") : null,
                }))]));
        }
        if (foldDelays)
            entries.Add(Folded(day, "delays", DelayIcon, "PurpleBrush", Lang.T($"Drivers kept the CPU busy much longer than usual {delays.Count} times"),
                Span(delays.Select(m => m.Time)),
                [.. delays.Select(m => (m.Time, new TimelineEntry { TimeText = TimeText.Time(m.Time), IconBrush = "PurpleBrush", Title = m.Delay!.Who, Detail = m.Detail, Delay = m.Delay }))]));
        if (foldMarked)
            entries.Add(Folded(day, "marks", MarkIcon, "PurpleBrush", Lang.T($"{marked.Count} problems marked"), Span(marked.Select(m => m.Time)),
                [.. marked.Select(m => (m.Time, new TimelineEntry { TimeText = TimeText.Time(m.Time), IconBrush = "PurpleBrush", Title = m.Text, Detail = m.Detail }))]));

        // The same kind of problem several times in a day: one line when Repeated problems is ticked, each opening as its
        // own case; otherwise each one its own line.
        foreach (var group in problems.GroupBy(p => (p.Kind, p.Outside)))
        {
            var list = group.OrderBy(p => p.Time).ToList();
            var first = list[0];
            string brush = first.Outside ? "MutedBrush" : first.IsCritical ? "HotBrush" : "WarmBrush";
            string text = first.Outside ? "TextBrush" : brush;
            TimelineEntry One(TimelineProblem p) => new()
            {
                TimeText = TimeText.Time(p.Time), Icon = ProblemIcon, Brush = text, IconBrush = brush, IsKey = !p.Outside, CrashDay = day, CrashTime = p.Time, Title = p.Title,
            };
            if (Folds("problems", list.Count))
                entries.Add(Folded(day, $"problems:{group.Key.Kind}:{group.Key.Outside}", ProblemIcon, brush,
                    first.Outside ? Core.Wording.PowerCuts(list.Count) : Core.Wording.Capital(CrashesViewModel.KindCount(group.Key.Kind, list.Count)),
                    Span(list.Select(p => p.Time)), [.. list.Select(p => (p.Time, One(p)))], text, isKey: !first.Outside));
            else
                foreach (var p in list) entries.Add((p.Time, One(p)));
        }

        // What a day's big changes made better or worse goes under the one that names it (a graphics driver before a Windows update).
        var result = _results.GetValueOrDefault(day);
        var under = result?.Changes.FirstOrDefault(changes.Contains);
        var apps = changes.Where(c => c.Kind == ChangeKind.AppUpdated && c != under).ToList();
        var games = changes.Where(c => c.Kind == ChangeKind.GameUpdated && c != under).ToList();
        bool foldApps = Folds("apps", apps.Count), foldGames = Folds("games", games.Count);
        foreach (var c in changes)
        {
            if (foldApps && apps.Contains(c) || foldGames && games.Contains(c)) continue;
            var entry = EntryOf(c);
            if (c == under) entry.Result = result;
            entries.Add((c.Time, entry));
        }
        if (foldApps)
            entries.Add(Folded(day, "apps", IconOf(ChangeKind.AppUpdated), "MutedBrush", Lang.T($"{apps.Count} apps updated"),
                string.Join(", ", apps.OrderByDescending(c => c.Time).Select(NameOf)), [.. apps.Select(c => (c.Time, EntryOf(c)))]));
        if (foldGames)
            entries.Add(Folded(day, "games", IconOf(ChangeKind.GameUpdated), "MutedBrush", Lang.T($"{games.Count} game updates"),
                string.Join(", ", games.GroupBy(NameOf).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)),
                [.. games.Select(c => (c.Time, EntryOf(c)))]));

        int ago = (int)(DateTime.Today - day).TotalDays;
        return new TimelineDay
        {
            Day = day,
            Title = ago switch { 0 => Lang.T("Today"), 1 => Lang.T("Yesterday"), _ => day.ToString("dddd d") },
            SubTitle = ago <= 1 ? day.ToString("dddd, d MMMM") : "",
            Month = newMonth ? TimeText.At(day, "MMMM yyyy").ToUpper(CultureInfo.CurrentCulture) : null,
            NodeBrush = NodeOf(changes, problems, marks),
            // Newest first; a drive's space is the whole day's (dated at its midnight) and comes last.
            Entries = [.. entries.OrderByDescending(e => e.At).Select(e => e.Entry)],
        };

        // When the lines of a fold were: "1:22 PM to 8:42 PM".
        static string Span(IEnumerable<DateTime> times) => Core.Sound.SoundDropouts.Span(times.Min(), times.Max());
    }

    /// <summary>
    /// Lines of one kind on a day as one line: at the time of the newest (the list reads newest first), its own lines under
    /// it newest first, each with its own time and the thread's colour (<see cref="TimelineEntry.IconBrush"/>). Kept open
    /// across rebuilds by its key.
    /// </summary>
    private (DateTime At, TimelineEntry Entry) Folded(DateTime day, string key, string icon, string iconBrush, string title, string? detail,
        List<(DateTime At, TimelineEntry Line)> lines, string brush = "TextBrush", bool isKey = false)
    {
        var newest = lines.MaxBy(l => l.At);
        return (newest.At, new TimelineEntry
        {
            Key = key, TimeText = newest.Line.TimeText, TimeTip = newest.Line.TimeTip, Icon = icon, IconBrush = iconBrush, Brush = brush, IsKey = isKey,
            Title = title, Detail = detail, Children = [.. lines.OrderByDescending(l => l.At).Select(l => l.Line)],
            ShowText = Lang.T("Show all"), HideText = Lang.T("Hide"), IsExpanded = _expanded.Contains((day, key)),
        });
    }

    // "7-Zip (x64) updated to 26.01" is "7-Zip (x64)" in the folded line.
    private static string NameOf(SystemChange c) => c.Title.Split(" updated")[0];

    /// <summary>A month's lines, in this order under its problems: what a PC-wide problem usually comes from first.</summary>
    /// <remarks>
    /// Many: how many of them (always two or more), a whole text with the count as its hole. Every kind of change has a
    /// line here (a test checks): game updates were missing, so a month left out every game update a day showed (the
    /// user, 2026-11-05).
    /// </remarks>
    internal static readonly (string Key, ChangeKind[] Kinds, string Many)[] MonthLines =
    [
        ("drivers", [ChangeKind.Driver], Lang.Key("{0} driver updates")),
        ("windows", [ChangeKind.WindowsUpdate, ChangeKind.Windows], Lang.Key("{0} Windows updates")),
        ("hardware", [ChangeKind.Hardware, ChangeKind.Firmware], Lang.Key("{0} hardware changes")),
        ("installed", [ChangeKind.AppInstalled], Lang.Key("{0} apps installed")),
        ("removed", [ChangeKind.AppRemoved], Lang.Key("{0} apps removed")),
        ("updated", [ChangeKind.AppUpdated], Lang.Key("{0} app updates")),
        ("games", [ChangeKind.GameUpdated], Lang.Key("{0} game updates")),
        ("startup", [ChangeKind.Startup], Lang.Key("{0} startup changes")),
        ("settings", [ChangeKind.Setting], Lang.Key("{0} settings changed")),
        ("fans", [ChangeKind.Fans], Lang.Key("{0} fan speed changes")),
        ("storage", [ChangeKind.Storage], Lang.Key("{0} drive space changes")),
        ("ended", [ChangeKind.TaskEnded], Lang.Key("{0} apps ended from Processes")),
    ];

    /// <summary>
    /// A month in one row: each kind of problem and each kind of change in it one line, with how many, which, and the
    /// days they span; every one with its date on request. One of a kind is just that change, with its date.
    /// </summary>
    private TimelineDay BuildMonth(DateTime month, bool newYear)
    {
        var (changes, problems, marks) = _byDay[month];
        var entries = new List<TimelineEntry>();
        foreach (var group in problems.GroupBy(p => (p.Kind, p.Outside)))
        {
            var last = group.MaxBy(p => p.Time)!;
            int n = group.Count();
            string brush = last.Outside ? "MutedBrush" : group.Any(p => p.IsCritical) ? "HotBrush" : "WarmBrush";
            entries.Add(new TimelineEntry
            {
                TimeText = Span(group.Select(p => p.Time)), Icon = ProblemIcon, Brush = last.Outside ? "TextBrush" : brush, IconBrush = brush, IsKey = !last.Outside, CrashDay = last.Time.Date,
                CrashFrom = group.Min(p => p.Time).Date,
                CrashTime = n == 1 ? last.Time : null,
                Title = n == 1 ? last.Title : last.Outside ? Core.Wording.PowerCuts(n) : Core.Wording.Capital(CrashesViewModel.KindCount(group.Key.Kind, n)),
            });
        }
        // The problems marked from the tray: one is its own line; more fold into one, each with its date on request. The
        // stretches of sound dropouts the same, apart: "Sound cut out 23 times", each stretch with its date and time.
        var marked = marks.Where(m => !m.Sound && m.Delay is null).ToList();
        if (marked.Count == 1) entries.Add(MarkEntry(marked[0], Span([marked[0].Time])));
        else if (marked.Count > 1)
        {
            var newest = marked.OrderByDescending(m => m.Time).ToList();
            entries.Add(new TimelineEntry
            {
                Key = "marks", TimeText = Span(newest.Select(m => m.Time)), Icon = MarkIcon, IconBrush = "PurpleBrush", Title = Lang.T($"{marked.Count} problems marked"),
                Children = [.. newest.Select(m => new TimelineEntry { Title = m.Text, Detail = TimeText.At(m.Time, "d MMM, h:mm tt") })],
                ShowText = Lang.T("Show all"), HideText = Lang.T("Hide"), IsExpanded = _expanded.Contains((month, "marks")),
            });
        }
        var delays = marks.Where(m => m.Delay is not null).ToList();
        if (delays.Count == 1) entries.Add(MarkEntry(delays[0], Span([delays[0].Time])));
        else if (delays.Count > 1)
        {
            var newest = delays.OrderByDescending(m => m.Time).ToList();
            entries.Add(new TimelineEntry
            {
                Key = "delays", TimeText = Span(newest.Select(m => m.Time)), Icon = DelayIcon, IconBrush = "PurpleBrush",
                Title = Lang.T($"Drivers kept the CPU busy much longer than usual {delays.Count} times"),
                Children = [.. newest.Select(m => new TimelineEntry { Title = m.Text, Detail = TimeText.At(m.Time, "d MMM, h:mm tt") })],
                ShowText = Lang.T("Show all"), HideText = Lang.T("Hide"), IsExpanded = _expanded.Contains((month, "delays")),
            });
        }
        // A stretch with a possible cause keeps its own line, as by day (approved mock-up, 2026-11-25).
        foreach (var noted in marks.Where(m => m.Sound && m.Note is { Length: > 0 }).OrderByDescending(m => m.Time))
            entries.Add(MarkEntry(noted, Span([noted.Time])));
        var sound = marks.Where(m => m.Sound && !(m.Note is { Length: > 0 })).ToList();
        if (sound.Count == 1) entries.Add(MarkEntry(sound[0], Span([sound[0].Time])));
        else if (sound.Count > 1)
        {
            var newest = sound.OrderByDescending(m => m.Time).ToList();
            entries.Add(new TimelineEntry
            {
                Key = "sound", TimeText = Span(newest.Select(m => m.Time)), Icon = SoundIcon, IconBrush = "WarmBrush",
                Title = Core.Sound.SoundDropouts.Title(sound.Sum(m => m.Count), null),
                Children = [.. newest.Select(m => new TimelineEntry { Title = m.Text, Detail = TimeText.At(m.Time, "d MMM, h:mm tt") })],
                ShowText = Lang.T("Show all"), HideText = Lang.T("Hide"), IsExpanded = _expanded.Contains((month, "sound")),
            });
        }
        // A change that made something better or worse keeps its own line with the result under it, as its day shows it
        // (folded into "3 driver updates" its result had nowhere to go, and a month said nothing of it).
        var withResult = new Dictionary<SystemChange, Core.Reports.ChangeResult>();
        foreach (var day in changes.Select(c => c.Time.Date).Distinct())
            if (_results.GetValueOrDefault(day) is { } r && r.Changes.FirstOrDefault(changes.Contains) is { } named) withResult[named] = r;
        foreach (var (key, kinds, many) in MonthLines)
        {
            foreach (var c in withResult.Keys.Where(c => kinds.Contains(c.Kind)).OrderByDescending(c => c.Time))
            {
                var own = EntryOf(c, Span([c.Time]));
                own.Result = withResult[c];
                entries.Add(own);
            }
            var of = changes.Where(c => kinds.Contains(c.Kind) && !withResult.ContainsKey(c)).OrderByDescending(c => c.Time).ToList();
            if (of.Count == 0) continue;
            if (of.Count == 1)
            {
                entries.Add(EntryOf(of[0], Span([of[0].Time])));
                continue;
            }
            // App and game updates by name, the most updated first; the rest as they're worded, newest first.
            bool updates = key is "updated" or "games";
            var names = updates
                ? of.GroupBy(NameOf).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList()
                : [.. of.Select(ShownOf)];
            int shown = updates ? 8 : 3;
            string list = string.Join(", ", names.Take(shown));
            int more = names.Count - shown;
            entries.Add(new TimelineEntry
            {
                Key = key, TimeText = Span(of.Select(c => c.Time)), Icon = IconOf(of[0].Kind), Title = Lang.F(many, of.Count), IsKey = of[0].IsSystemLevel,
                Detail = more > 0 ? Lang.T($"{list} and {more} more") : list,
                Children = [.. of.Select(c => MonthDriverChild(c) ?? new TimelineEntry { Title = c.Shown, Detail = c.Was is null ? $"{c.Time:d MMM}" : Lang.T($"{c.Time:d MMM} · was {c.Was}"), GameKey = GameKeyOf(c) })],
                ShowText = Lang.T("Show all"), HideText = Lang.T("Hide"), IsExpanded = _expanded.Contains((month, key)),
            });
        }
        return new TimelineDay
        {
            Day = month, IsSummary = true, Title = month.ToString("MMMM"), Month = newYear ? month.ToString("yyyy") : null,
            NodeBrush = NodeOf(changes, problems, marks), Entries = entries,
        };

        // The days they fell on: "9 Sep", or "3–20 Sep".
        static string Span(IEnumerable<DateTime> times)
        {
            DateTime first = times.Min().Date, last = times.Max().Date;
            return first == last ? $"{first:d MMM}" : $"{first:%d}–{last:d MMM}";
        }
    }

    /// <param name="when">Said in place of its time of day (under a month: its date).</param>
    private TimelineEntry EntryOf(SystemChange c, string? when = null) => DriverEntryOf(c, when ?? TimeOf(c), TipOf(c)) ?? new()
    {
        TimeText = when ?? TimeOf(c),
        TimeTip = TipOf(c),
        Icon = IconOf(c.Kind),
        Title = c.Shown,
        // An app just installed has no "before": its version is the detail.
        Detail = c.Kind == ChangeKind.AppInstalled ? c.Now : c.Detail,
        GameKey = GameKeyOf(c),
        IsKey = c.IsSystemLevel,
    };

    /// <summary>
    /// A problem marked from the tray: its flag in purple, and what the look back found, as it was saved. A stretch of
    /// sound dropouts: a warm speaker, its time beside the title and its possible cause under it.
    /// </summary>
    private static TimelineEntry MarkEntry(TimelineMark m, string when) => m.Sound
        ? new() { TimeText = when, Icon = SoundIcon, IconBrush = "WarmBrush", Title = m.Text, Detail = m.Detail, Note = m.Note }
        : m.Delay is not null
            ? new() { TimeText = when, Icon = DelayIcon, IconBrush = "PurpleBrush", Title = m.Text, Detail = m.Detail, Delay = m.Delay }
            : new() { TimeText = when, Icon = MarkIcon, IconBrush = "PurpleBrush", Title = m.Text, Note = m.Detail };

    /// <summary>The dot on the line: blue for changes, red for problems only, purple for marked problems only, warm for sound dropouts only.</summary>
    private static string NodeOf(List<SystemChange> changes, List<TimelineProblem> problems, List<TimelineMark> marks) =>
        changes.Count > 0 ? "CpuBrush" : problems.Count > 0 ? "HotBrush" : marks.Any(m => !m.Sound) ? "PurpleBrush" : "WarmBrush";

    /// <summary>The game a game update opens on the Games page (null for any other change).</summary>
    private static string? GameKeyOf(SystemChange c) =>
        c.Kind == ChangeKind.GameUpdated && c.Subject is { } subject && subject.StartsWith(Inventory.Game + ":", StringComparison.Ordinal)
            ? subject[(Inventory.Game.Length + 1)..] : null;

    /// <summary>
    /// When it happened, as exactly as it's known: a drive's space is compared day to day ("All day"); a change found
    /// by a check of the PC happened in the minutes before that check, and shows the check's time with the window on hover
    /// (no "~" in front, the user 2026-11-13); the rest is to the minute.
    /// </summary>
    private static string TimeOf(SystemChange c) =>
        c.Kind == ChangeKind.Storage ? Lang.T("All day") : TimeText.Time(c.Time);

    private static string? TipOf(SystemChange c)
    {
        if (c.Kind == ChangeKind.Storage) return Lang.T("The space in use at the end of this day against the day before");
        if (!c.IsApproximate) return null;
        string from = Moment(c.Likely, c.Time);
        return Lang.T($"Between {from} and {c.Time:h:mm tt}");
    }

    /// <summary>A time of the same day, or with its day when the check before was on an earlier one (the PC was off overnight).</summary>
    private static string Moment(DateTime t, DateTime sameDayAs) => t.Date == sameDayAs.Date ? t.ToString(TimeText.Fit("h:mm tt")) : t.ToString(TimeText.Fit("ddd d MMM, h:mm tt"));

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
        ChangeKind.GameUpdated => "",
        ChangeKind.TaskEnded => "",
        ChangeKind.Fans => "",
        _ => "",
    };

    [RelayCommand]
    private void ToggleVersions(TimelineEntry entry)
    {
        entry.IsExpanded = !entry.IsExpanded;
        if (Days.FirstOrDefault(d => d.Entries.Contains(entry)) is not { } day) return;
        if (entry.IsExpanded) _expanded.Add((day.Day, entry.Key)); else _expanded.Remove((day.Day, entry.Key));
    }

    /// <summary>Opens a game on the Games page (set by the window).</summary>
    public Action<string>? OpenGameAt { get; set; }

    [RelayCommand]
    private void OpenGame(TimelineEntry entry)
    {
        if (entry.GameKey is { } key) OpenGameAt?.Invoke(key);
    }

    /// <summary>A line inside a folded one: opens what it would open on its own (a driver mark, a crash, a game).</summary>
    [RelayCommand]
    private void OpenChild(TimelineEntry entry)
    {
        if (entry.IsDelay) OpenDelay(entry);
        else if (entry.IsProblem) OpenCrashes(entry);
        else if (entry.GameKey is not null) OpenGame(entry);
        else if (entry.DriverKey is not null) OpenDriverEntry(entry);
    }

    [RelayCommand]
    private void OpenCrashes(TimelineEntry entry)
    {
        if (entry.CrashTime is { } time && openCrash is not null) openCrash(time);
        else if (entry.CrashDay is { } day && openProblems is not null) openProblems(entry.CrashFrom ?? day, day);
        else if (entry.CrashDay is { } day1) openCrashes(day1);
    }

    // ── The calendar ────────────────────────────────────────────────────

    /// <summary>The month the calendar shows (its 1st), or any month of the year it shows.</summary>
    [ObservableProperty] private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    /// <summary>Zoomed out: the twelve months of a year instead of a month's days.</summary>
    [ObservableProperty] private bool _isYearView;

    [ObservableProperty] private string _calendarTitle = "";
    [ObservableProperty] private List<string> _weekdays = [];
    /// <summary>The month's days (after a blank for each weekday before its 1st): the same cells from month to month.</summary>
    public ObservableCollection<CalendarCell> CalendarCells { get; } = [];
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
                int n = _onDay.Where(d => d.Key.Year == m.Year && d.Key.Month == i).Sum(d => d.Value.Changes.Count + d.Value.Problems.Count + d.Value.Marks.Count);
                return new CalendarMonthCell(month, n, month == m);
            })];
            CanGoBack = m.Year > FirstMonth.Year;
            CanGoForward = m.Year < DateTime.Today.Year;
            return;
        }

        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        int firstDay = (int)format.FirstDayOfWeek;
        if (Weekdays.Count == 0) Weekdays = [.. Enumerable.Range(0, 7).Select(i => format.ShortestDayNames[(firstDay + i) % 7][..1].ToUpper(CultureInfo.CurrentCulture))];
        // The month already shown keeps its cells (they're told what changed). Another month moves the same cells to its
        // days, adding or dropping only at the end, so the buttons stay and only what they say changes: new ones for
        // every month paused the window 33-51 ms each time the list was scrolled into another month (2026-11-08).
        var cells = CalendarCells;
        if (cells.FirstOrDefault(c => c.Day is not null)?.Day != m)
        {
            var days = new List<DateTime?>();
            for (int i = ((int)m.DayOfWeek - firstDay + 7) % 7; i > 0; i--) days.Add(null);
            for (var d = m; d.Month == m.Month; d = d.AddDays(1)) days.Add(d);
            while (cells.Count > days.Count) cells.RemoveAt(cells.Count - 1);
            for (int i = 0; i < days.Count; i++)
            {
                if (i < cells.Count) cells[i].Day = days[i];
                else cells.Add(new CalendarCell(days[i]));
            }
        }
        foreach (var cell in cells)
        {
            if (cell.Day is not { } d) continue;
            _onDay.TryGetValue(d, out var day);
            cell.IsToday = d == DateTime.Today;
            cell.HasAny = day.Changes is not null;
            cell.OnlyProblems = day.Changes is { Count: 0 } && day.Problems.Count > 0;
            if (day.Changes is null)
            {
                cell.Tip = null;
                continue;
            }
            // Hovering a day says what's on it.
            var lines = day.Problems.GroupBy(p => p.Kind).Select(g => g.Count() == 1 ? g.First().Title : CrashesViewModel.KindCount(g.Key, g.Count()))
                .Concat(Marked(day.Marks.Where(m => !m.Sound && m.Delay is null).ToList()))
                .Concat(Delays(day.Marks.Where(m => m.Delay is not null).ToList()))
                .Concat(Sound(day.Marks.Where(m => m.Sound).ToList()))
                .Concat(day.Changes.OrderByDescending(c => c.IsSystemLevel).Take(5).Select(ShownOf)).ToList();
            if (day.Changes.Count > 5) lines.Add(Lang.T($"and {day.Changes.Count - 5} more"));
            cell.Tip = string.Join("\n", lines);
        }

        // Problems marked, and stretches of sound dropouts: one says its line, more say how many.
        static string[] Marked(List<TimelineMark> marks) =>
            marks.Count switch { 0 => [], 1 => [marks[0].Text], var n => [Lang.T($"{n} problems marked")] };
        static string[] Sound(List<TimelineMark> marks) =>
            marks.Count switch { 0 => [], 1 => [marks[0].Text], _ => [Core.Sound.SoundDropouts.Title(marks.Sum(m => m.Count), null)] };
        static string[] Delays(List<TimelineMark> marks) =>
            marks.Count switch { 0 => [], 1 => [marks[0].Text], var n => [Lang.T($"Drivers kept the CPU busy much longer than usual {n} times")] };
        CalendarTitle = TimeText.At(m, "MMMM yyyy");
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

    // ── An opened change ────────────────────────────────────────────────

    /// <summary>The change opened from its result: the page shows it in place of the list until Back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangeOpen), nameof(IsListShown))]
    [NotifyPropertyChangedFor(nameof(IsDriverShown), nameof(IsEveryDriverShown))]
    private Models.ChangeCase? _opened;

    public bool IsChangeOpen => Opened is not null;

    /// <summary>The list, the calendar and This PC now: hidden while a change, a driver or every driver is open.</summary>
    public bool IsListShown => Opened is null && !IsDriverViewOpen;

    // ── A driver mark (issue #15 follow-up): opens on the Latency page ──

    /// <summary>Opens a driver mark on the Latency page (set by the shell); Back there returns here, at the same place.</summary>
    public Action<Core.Drivers.DelayStretch>? OpenDelayHere { get; set; }

    /// <summary>Set as a mark opens elsewhere: leaving keeps the list where it was, so Back finds it there.</summary>
    public bool KeepPlace { get; set; }

    /// <summary>Where the list was when a mark opened elsewhere (its scroll offset), put back on return.</summary>
    public double? KeptOffset { get; set; }

    [RelayCommand]
    private void OpenDelay(TimelineEntry entry)
    {
        if (entry.Delay is not { } stretch || OpenDelayHere is not { } go) return;
        KeepPlace = true;
        go(stretch);
    }

    /// <summary>Raised when a change opens, to scroll its page to the top.</summary>
    public event Action? ChangeOpened;

    [RelayCommand]
    private void OpenResult(TimelineEntry entry)
    {
        if (entry.Result is { } r) OpenChange(r.Day);
    }

    [RelayCommand]
    private void OpenNowResult(NowFact fact)
    {
        if (fact.ResultDay is { } day) OpenChange(day);
    }

    /// <summary>Opens the result of a day's big changes (from this page, Home, Reports or Crashes); false when there is none.</summary>
    public bool OpenChange(DateTime day)
    {
        if (!_results.TryGetValue(day.Date, out var result)) return false;
        Opened = CaseOf(result);
        ChangeOpened?.Invoke();
        return true;
    }

    [RelayCommand]
    private void CloseChange() => Opened = null;

    /// <summary>An earlier driver's result opens in place.</summary>
    [RelayCommand]
    private void OpenDriver(Models.DriverLine line)
    {
        if (line.CanOpen && !line.IsThis) OpenChange(line.Day);
    }

    /// <summary>A problem counted on either side opens as its own crash.</summary>
    [RelayCommand]
    private void OpenProblem(Models.ChangeProblemLine line)
    {
        if (openCrash is not null) openCrash(line.Time);
        else openCrashes(line.Time.Date);
    }

    [ObservableProperty] private bool _copied;

    [RelayCommand]
    private async Task CopyChange()
    {
        if (Opened is not { } open) return;
        try
        {
            System.Windows.Clipboard.SetText(open.Report());
            Copied = true;
            await Task.Delay(2000);
            Copied = false;
        }
        catch
        {
            // The clipboard can be briefly locked by another app; the user can simply click again.
        }
    }

    private Models.ChangeCase CaseOf(Core.Reports.ChangeResult result) => new(result, result.Main.IsGraphicsDriver ? Drivers(result) : []);

    /// <summary>Every graphics driver this PC had, newest first, each with what it did where it made a difference.</summary>
    private List<Models.DriverLine> Drivers(Core.Reports.ChangeResult opened)
    {
        var drivers = _changes.Where(c => c.IsGraphicsDriver).OrderByDescending(c => c.Time).ToList();
        var lines = new List<Models.DriverLine>();
        for (int i = 0; i < drivers.Count; i++)
        {
            var c = drivers[i];
            // A driver read from Windows' log has no version; the one after it says what it replaced.
            string? version = c.Now is { Length: > 0 } now ? now : i > 0 && drivers[i - 1].Was is { Length: > 0 } was ? was : null;
            if (version is null) continue;
            string dates = i == 0 ? Lang.T($"{c.Time:d MMM yyyy} to now") : Lang.T($"{c.Time:d MMM yyyy} to {drivers[i - 1].Time:d MMM yyyy}");
            var r = _results.GetValueOrDefault(c.Time.Date);
            bool has = r is not null && r.Changes.Contains(c);
            lines.Add(new(version, dates, has ? Core.Wording.Capital(r!.Short) : null, has && r!.Tone == Core.Reports.EffectTone.Worse ? "WarmBrush" : "GoodBrush",
                c.Time.Date, opened.Changes.Contains(c), has));
        }
        return lines;
    }

    // ── This PC now ─────────────────────────────────────────────────────

    private static List<NowFact> BuildNow(TimelineData data)
    {
        var facts = new List<NowFact>();
        var items = data.Inventory.ToLookup(i => i.Kind);
        // Since when, and what that last change made better or worse where it did: a link under the value.
        NowFact Fact(string label, string value, Func<SystemChange, bool> match)
        {
            if (data.Changes.LastOrDefault(match) is not { } c) return new(label, value, null);
            string date = c.Time.ToString(c.Time.Year == DateTime.Today.Year ? "d MMM" : "d MMM yyyy");
            var fact = new NowFact(label, value, Lang.T($"since {date}"));
            return data.Results.TryGetValue(c.Time.Date, out var r) && r.Changes.Contains(c)
                ? fact with { ResultDay = r.Day, ResultText = Core.Wording.Capital(r.Short), ResultBrush = r.Tone == Core.Reports.EffectTone.Worse ? "WarmBrush" : "GoodBrush" }
                : fact;
        }

        if (items[Inventory.Windows].FirstOrDefault() is { } windows)
            facts.Add(Fact("Windows", $"{windows.Name.Replace("Windows ", "")} {windows.Value}", c => c.Kind == ChangeKind.Windows));
        if (items[Inventory.GpuDriver].FirstOrDefault() is { } driver)
            facts.Add(Fact(Lang.T("Graphics driver"), driver.Value, c => c.IsGraphicsDriver) with { Opens = GraphicsOpens(data) });
        if (DriversFact(data) is { } drivers) facts.Add(drivers);
        if (items[Inventory.Bios].FirstOrDefault() is { } bios)
            facts.Add(Fact("BIOS", bios.Value, c => c.Kind == ChangeKind.Firmware));
        if (items[Inventory.Ram].FirstOrDefault() is { } ram) facts.Add(new(Lang.T("Memory"), ram.Value, null));
        if (items[Inventory.Setting].FirstOrDefault(i => i.Key == Inventory.PowerPlan) is { } plan)
            facts.Add(Fact(Lang.T("Power plan"), plan.Value, c => c.Subject == $"{Inventory.Setting}:{Inventory.PowerPlan}"));
        if (items[Inventory.App].Any()) facts.Add(new(Lang.T("Apps installed"), items[Inventory.App].Count().ToString("N0"), null));
        if (items[Inventory.Startup].Any()) facts.Add(new(Lang.T("Start with Windows"), items[Inventory.Startup].Count(i => i.Value == Inventory.On).ToString("N0"), null));
        return facts;
    }
}

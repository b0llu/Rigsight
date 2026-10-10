using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>One of the four totals above the list: the figure now, the app using the most of it, and its last minute.</summary>
public sealed partial class TotalTile(string key, string label, string brush, SensorKind kind) : ObservableObject
{
    /// <summary>The column it sorts the list by when clicked.</summary>
    public string Key { get; } = key;
    public string Label { get; } = label;
    /// <summary>The palette brush of its dot and its line: the only colour on the page.</summary>
    public string Brush { get; } = brush;
    /// <summary>What the line measures, for the reading under the pointer.</summary>
    public SensorKind Kind { get; } = kind;

    [ObservableProperty] private string _value = ProcRow.Nothing;
    /// <summary>Said small beside the figure ("of 32 GB").</summary>
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _most = "";
    [ObservableProperty] private HistoryBuffer? _history;
    [ObservableProperty] private long _version;
    /// <summary>The list is sorted by it.</summary>
    [ObservableProperty] private bool _isActive;
}

/// <summary>A column heading of the list: a button that sorts by it.</summary>
public sealed partial class SortHeading(string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    /// <summary>↓ or ↑ on the column sorted by, a faint ↕ on the others.</summary>
    [ObservableProperty] private string _arrow = "↕";
    [ObservableProperty] private bool _isActive;
}

public enum ProcMenuKind { Item, Separator, Header, Note }

/// <summary>A line of a row's menu, as the page builds it (the view turns these into a ContextMenu).</summary>
public sealed record ProcMenuEntry(ProcMenuKind Kind, string Text = "", string? Hint = null, string Glyph = "", bool Enabled = true, bool Danger = false, Func<Task>? Run = null);

/// <summary>Something to end: an app (every process of it), or one of its processes.</summary>
public sealed record EndTarget(ProcRow App, ProcChild? Process = null)
{
    public string Name => Process is null || Process.Label == App.Name ? App.Name : Process.Label;
    /// <summary>As the Timeline names it: a process says which app it was of.</summary>
    public string LongName => Process is null || Process.Label == App.Name ? App.Name : $"{App.Name} · {Process.Label}";
    public double MemMB => Process?.MemMB ?? App.MemMB;
    public EndRisk Risk => EndRisks.Classify(App.Exe, App.Path, wholeApp: Process is null, marked: Process?.Critical ?? App.Critical);
    /// <summary>A question comes first.</summary>
    public bool Asks => Risk is not (EndRisk.None or EndRisk.ByProcessOnly);
}

/// <summary>
/// The Processes page: everything running right now with its memory, CPU, internet and disk use, four totals above
/// it, what is on record about an app under its row, and ending apps and processes.
/// </summary>
public sealed partial class ProcessesViewModel : ObservableObject
{
    public const string ByName = "Name", ByMemory = "Memory", ByCpu = "Cpu", ByInternet = "Internet", ByDisk = "Disk";

    private readonly ReportService _reports;
    private readonly Action<string, string?> _send;
    private readonly ListCollectionView _view;
    private readonly HistoryBuffer _netHistory = new(LiveData.NetHistorySeconds * 2), _diskHistory = new(64);
    private string[] _words = [];
    private bool _shown;

    /// <param name="send">Sends the agent a command (a line for the Timeline once something was ended).</param>
    public ProcessesViewModel(ReportService reports, LiveData live, Action<string, string?> send)
    {
        _reports = reports;
        _send = send;
        Live = live;
        // A list of its own over the same rows as the Memory page's: its own order and its own search.
        _view = new ListCollectionView(live.Procs) { Filter = o => o is ProcRow p && Listed(p) };
        _view.LiveFilteringProperties.Add(nameof(ProcRow.HasWindow));
        Tiles =
        [
            new(ByMemory, "MEMORY", "PurpleBrush", SensorKind.Data), new(ByCpu, "CPU", "CpuBrush", SensorKind.Load),
            new(ByInternet, "INTERNET", "CoolBrush", SensorKind.Throughput), new(ByDisk, "DISK", "AccentBrush", SensorKind.Throughput),
        ];
        Tiles[2].History = _netHistory;
        Tiles[3].History = _diskHistory;
        ApplySort();
        live.ProcsApplied += OnProcsApplied;
        Question.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EndQuestionViewModel.IsOpen)) UpdateHold(); };
        Ask = Question.AskAsync;
        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            Message = null;
        };
    }

    public LiveData Live { get; }

    /// <summary>The running apps as the page lists them: searched, filtered and sorted.</summary>
    public ICollectionView Apps => _view;

    // ── Shown or not: nothing is worked out for a page that isn't on screen ──

    /// <summary>The page came on screen or left it: the list is kept sorted, and the totals kept up, only while it shows.</summary>
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        if (shown) Live.PropertyChanged += OnLiveChanged;
        else Live.PropertyChanged -= OnLiveChanged;
        _view.IsLiveSorting = _view.IsLiveFiltering = shown;
        if (!shown) return;
        Refresh();
        // In order again only if it moved while it wasn't kept (reading the list anew rebuilds every row on screen).
        if (Filtering || !InOrder()) _view.Refresh();
        // An app opened on the Memory page is open here too: its box is read now.
        foreach (var row in Live.Procs)
            if (row.IsExpanded) _ = LoadHistoryAsync(row);
    }

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveData.Tick)) Refresh();
    }

    private readonly List<(Action Reset, int Left)> _ending = [];

    private void OnProcsApplied()
    {
        if (Live.Disk is { } disk) _diskHistory.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), disk);
        // A row that was ended stays dimmed until the agent's list no longer has it; one still there two lists on wasn't ended after all.
        for (int i = _ending.Count - 1; i >= 0; i--)
        {
            var (reset, left) = _ending[i];
            if (left > 1) _ending[i] = (reset, left - 1);
            else
            {
                _ending.RemoveAt(i);
                reset();
            }
        }
        if (_shown) Refresh();
    }

    // ── The four totals, and each app's internet ──

    public IReadOnlyList<TotalTile> Tiles { get; }

    /// <summary>The figures of the totals, the app using the most of each, and each row's internet speed.</summary>
    internal void Refresh()
    {
        var net = Live.Net;
        if (net is not null)
            foreach (var n in Live.NetHistory)
                if (n.Time * 1000 > _netHistory.LastTime) _netHistory.Add(n.Time * 1000, n.Down + n.Up);

        // Under an open menu or question the rows hold still: their internet too (the list may be sorted by it).
        if (!Live.HoldProcs)
        {
            Dictionary<string, double>? speeds = null;
            if (net is { Apps.Count: > 0 })
            {
                speeds = new(StringComparer.OrdinalIgnoreCase);
                foreach (var app in net.Apps) speeds[app.Exe] = speeds.GetValueOrDefault(app.Exe) + app.Down + app.Up;
            }
            foreach (var row in Live.Procs) row.Net = speeds?.GetValueOrDefault(row.Exe) ?? 0;
            NoneMatch = Filtering && _view.IsEmpty;
        }

        var (memory, cpu, internet, disk) = (Tiles[0], Tiles[1], Tiles[2], Tiles[3]);
        memory.History = Live.RamUsed?.History;
        memory.Value = Live.RamUsed?.Value is { } used ? $"{used:0.0} GB" : ProcRow.Nothing;
        memory.Note = Live.RamUsed?.Value is null || Live.RamTotalText.Length == 0 ? "" : $"of {Live.RamTotalText}";
        memory.Most = Most(Live.Procs.MaxBy(p => p.MemMB), p => p.MemMB >= 1);
        cpu.History = Live.CpuLoad?.History;
        cpu.Value = Live.CpuLoad?.Value is { } load ? $"{load:0}%" : ProcRow.Nothing;
        cpu.Most = Most(Live.Procs.MaxBy(p => p.Cpu), p => p.Cpu >= 0.05);
        internet.Value = net is null ? ProcRow.Nothing : Units.Speed(net.Down + net.Up);
        internet.Most = net?.Apps.MaxBy(a => a.Down + a.Up) is { } fastest && fastest.Down + fastest.Up >= 1024 ? $"Most: {fastest.Name}" : NothingUsing;
        disk.Value = Live.Disk is { } bytes ? Units.Speed(bytes) : ProcRow.Nothing;
        disk.Most = Most(Live.Procs.MaxBy(p => p.Disk), p => p.Disk >= 1024);
        foreach (var tile in Tiles) tile.Version++;
    }

    private const string NothingUsing = "Nothing using it";

    private static string Most(ProcRow? row, Func<ProcRow, bool> uses) => row is not null && uses(row) ? $"Most: {row.Name}" : NothingUsing;

    // ── Search, and only the apps with windows: this page's own, apart from the Memory page's ──

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyWindowed;

    /// <summary>Something is searched for (or only windows are asked for) and no app running matches.</summary>
    [ObservableProperty] private bool _noneMatch;

    private bool Filtering => OnlyWindowed || _words.Length > 0;

    private bool Listed(ProcRow p) => (!OnlyWindowed || p.HasWindow)
        && (_words.Length == 0 || TextMatch.Has(_words, [p.Name, p.Exe, .. p.Children.Select(c => c.Label)]));

    partial void OnSearchChanged(string value)
    {
        _words = TextMatch.Words(value);
        Refilter();
    }

    partial void OnOnlyWindowedChanged(bool value) => Refilter();

    private void Refilter()
    {
        _view.Refresh();
        NoneMatch = Filtering && _view.IsEmpty;
    }

    // ── Sorting: every heading is a button, and so is every total ──

    public SortHeading NameHeading { get; } = new(ByName, "APP");
    public SortHeading MemoryHeading { get; } = new(ByMemory, "MEMORY");
    public SortHeading CpuHeading { get; } = new(ByCpu, "CPU");
    public SortHeading InternetHeading { get; } = new(ByInternet, "INTERNET");
    public SortHeading DiskHeading { get; } = new(ByDisk, "DISK");

    /// <summary>The column the list is sorted by: its figures are the ones in white.</summary>
    [ObservableProperty] private string _sortKey = ByMemory;

    /// <summary>The other way round: smallest first (names: Z to A).</summary>
    [ObservableProperty] private bool _sortFlipped;

    /// <summary>A heading was pressed: sorts by it, or the other way round when it already does.</summary>
    [RelayCommand]
    private void SortBy(string key)
    {
        if (key == SortKey) SortFlipped = !SortFlipped;
        else (SortKey, SortFlipped) = (key, false);
        ApplySort();
    }

    /// <summary>A total was clicked: its column, biggest first.</summary>
    [RelayCommand]
    private void SortByTotal(string key)
    {
        (SortKey, SortFlipped) = (key, false);
        ApplySort();
    }

    /// <summary>What a row is sorted by, for the column picked (a name sorts as text).</summary>
    private IComparable KeyOf(ProcRow p) => SortKey switch
    {
        ByName => p.Name, ByCpu => p.Cpu, ByInternet => p.Net, ByDisk => p.Disk, _ => p.MemMB,
    };

    // Names run A to Z, figures biggest first, unless flipped.
    private bool Descending => (SortKey != ByName) != SortFlipped;

    /// <summary>Whether the rows are still in the order asked for (by the column picked; rows level on it aside).</summary>
    private bool InOrder()
    {
        IComparable? last = null;
        foreach (ProcRow row in _view)
        {
            var key = KeyOf(row);
            int step = last is null ? 0 : key is string name ? string.Compare((string)last, name, StringComparison.CurrentCulture) : last.CompareTo(key);
            if (Descending ? step < 0 : step > 0) return false;
            last = key;
        }
        return true;
    }

    private void ApplySort()
    {
        string property = SortKey switch
        {
            ByName => nameof(ProcRow.Name), ByCpu => nameof(ProcRow.Cpu), ByInternet => nameof(ProcRow.Net), ByDisk => nameof(ProcRow.Disk),
            _ => nameof(ProcRow.MemMB),
        };
        // Apps level on a figure (most use no internet) go by their memory.
        bool descending = Descending;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(property, descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            if (property != nameof(ProcRow.MemMB)) _view.SortDescriptions.Add(new SortDescription(nameof(ProcRow.MemMB), ListSortDirection.Descending));
            _view.LiveSortingProperties.Clear();
            _view.LiveSortingProperties.Add(property);
        }
        foreach (var heading in new[] { NameHeading, MemoryHeading, CpuHeading, InternetHeading, DiskHeading })
        {
            heading.IsActive = heading.Key == SortKey;
            heading.Arrow = !heading.IsActive ? "↕" : SortFlipped ? "↑" : "↓";
        }
        foreach (var tile in Tiles) tile.IsActive = tile.Key == SortKey;
    }

    // ── An app opened: its processes, and what is on record about it ──

    /// <summary>A row was clicked: its processes and its box open under it, or close.</summary>
    [RelayCommand]
    private void Toggle(ProcRow row)
    {
        if (Live.HoldProcs) return;
        Live.SetExpanded(row, !row.IsExpanded);
        if (row.IsExpanded) _ = LoadHistoryAsync(row);
    }

    /// <summary>The slim line was clicked: the box opens into the chart and every fact, or closes.</summary>
    [RelayCommand]
    private void ToggleHistory(ProcRow row)
    {
        if (row.History is { } history) history.IsOpen = !history.IsOpen;
    }

    /// <summary>How long what was read about an app is good for before it is read again on opening.</summary>
    private static readonly TimeSpan HistoryAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reads what is on record about an app, the first time its box shows (and again when it is opened a while later).
    /// Until then the box says what the running app tells by itself, with a loader for the rest.
    /// </summary>
    internal async Task LoadHistoryAsync(ProcRow row)
    {
        var history = row.History ??= new ProcHistory();
        if (!history.IsLoading && DateTime.Now - history.ReadAt < HistoryAge) return;
        history.Fill(row, DateTime.Now);
        var past = await _reports.AppPastAsync(row.Exe);
        // Nothing read (no history yet, or it couldn't be opened): the box keeps to what the app itself says.
        history.Fill(row, past, DateTime.Now);
    }

    // ── A row's menu ──

    /// <summary>Puts text on the clipboard (tests replace this).</summary>
    internal Action<string> SetClipboard { get; set; } = text => System.Windows.Clipboard.SetText(text);

    /// <summary>Shows a program's file in File Explorer (tests replace this).</summary>
    internal Action<string> OpenLocation { get; set; } = path =>
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose(); }
        catch (Exception ex) { Log.Error("processes", ex); }
    };

    /// <summary>The menu of an app's row; of several rows when more than one is picked.</summary>
    public IReadOnlyList<ProcMenuEntry> MenuFor(IReadOnlyList<ProcRow> rows)
    {
        if (rows.Count == 0) return [];
        if (rows.Count == 1) return MenuFor(new EndTarget(rows[0]));

        var targets = rows.Select(r => new EndTarget(r)).ToList();
        string many = $"{rows.Count} apps";
        var menu = new List<ProcMenuEntry>
        {
            new(ProcMenuKind.Header, $"{many} picked"),
            new(ProcMenuKind.Item, "Copy names", Glyph: "", Run: () => Done(Copy(string.Join(Environment.NewLine, rows.Select(r => r.Name)), "Names copied"))),
            new(ProcMenuKind.Separator),
        };
        var blocked = targets.FirstOrDefault(t => t.Risk == EndRisk.ByProcessOnly);
        menu.Add(new(ProcMenuKind.Item, $"End {many}" + (targets.Any(t => t.Asks) ? "…" : ""), Units.Megabytes(rows.Sum(r => r.MemMB)), "",
            Enabled: blocked is null, Danger: true, Run: () => EndAsync(targets)));
        if (blocked is not null) menu.Add(new(ProcMenuKind.Note, $"{blocked.Name} can't be ended as one. {EndRisks.Sentence(EndRisk.ByProcessOnly)}"));
        return menu;
    }

    /// <summary>The menu of one of an app's processes.</summary>
    public IReadOnlyList<ProcMenuEntry> MenuFor(ProcRow app, ProcChild process) => MenuFor(new EndTarget(app, process));

    private List<ProcMenuEntry> MenuFor(EndTarget target)
    {
        var app = target.App;
        bool hasPath = app.Path is { Length: > 0 };
        var menu = new List<ProcMenuEntry>
        {
            new(ProcMenuKind.Item, "Open file location", Glyph: "", Enabled: hasPath, Run: () => Done(() => OpenLocation(app.Path!))),
            new(ProcMenuKind.Item, "Copy path", Glyph: "", Enabled: hasPath, Run: () => Done(Copy(app.Path!, "Path copied"))),
            new(ProcMenuKind.Separator),
        };
        string dots = target.Asks ? "…" : "";
        if (target.Process is { } process)
        {
            menu.Add(new(ProcMenuKind.Item, "End this process" + dots, $"PID {process.Pid}", "", Danger: true, Run: () => EndAsync([target])));
            return menu;
        }
        if (target.Risk == EndRisk.Explorer) menu.Add(new(ProcMenuKind.Item, "Restart", Glyph: "", Run: () => RestartAsync(target)));
        bool whole = target.Risk != EndRisk.ByProcessOnly;
        menu.Add(new(ProcMenuKind.Item, "End task" + dots, app.Count > 1 ? app.CountText : null, "", Enabled: whole, Danger: true, Run: () => EndAsync([target])));
        if (!whole) menu.Add(new(ProcMenuKind.Note, EndRisks.Sentence(EndRisk.ByProcessOnly)!));
        return menu;
    }

    private static Task Done(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private Action Copy(string text, string said) => () =>
    {
        try
        {
            SetClipboard(text);
            Say(said);
        }
        catch (Exception)
        {
            // The clipboard is held by another program for a moment.
            Say("Couldn't copy. Try again.", warns: true);
        }
    };

    // ── The list holds still under a menu, a question, or something being ended ──

    private int _menus, _busy;

    /// <summary>A row's menu opened or closed (the view says).</summary>
    public void SetMenuOpen(bool open)
    {
        _menus = Math.Max(0, _menus + (open ? 1 : -1));
        UpdateHold();
    }

    private void UpdateHold() => Live.HoldProcs = _menus > 0 || _busy > 0 || Question.IsOpen;

    // ── Ending ──

    /// <summary>The question before a part of Windows is ended, as a box over the window.</summary>
    public EndQuestionViewModel Question { get; } = new();

    /// <summary>Asks before ending (the tests answer for the user).</summary>
    internal Func<EndQuestion, Task<EndAnswer>> Ask { get; set; }

    /// <summary>What ends processes (the tests give one that ends nothing).</summary>
    internal TaskEnder Ender { get; set; } = new();

    /// <summary>The question for these targets; null when none of them is a part of Windows (ordinary apps end at once).</summary>
    internal static EndQuestion? QuestionFor(IReadOnlyList<EndTarget> targets)
    {
        var parts = targets.Where(t => t.Asks).ToList();
        if (parts.Count == 0) return null;
        bool critical = parts.Any(t => t.Risk == EndRisk.Critical);
        if (targets.Count == 1)
        {
            var one = targets[0];
            return new EndQuestion
            {
                Title = $"End {one.Name}?", Body = EndRisks.Sentence(one.Risk), NeedsTick = critical,
                CanRestart = one.Risk == EndRisk.Explorer && one.Process is null,
            };
        }
        return new EndQuestion
        {
            Title = $"End {targets.Count} apps?", NeedsTick = critical,
            Intro = parts.Count == 1 ? "One of them is part of Windows:" : "Some of them are part of Windows:",
            Lines = [.. parts.Select(t => new EndQuestionLine(t.Name, EndRisks.Sentence(t.Risk)!))],
        };
    }

    /// <summary>Ends apps or processes: after a question where one is a part of Windows, at once otherwise.</summary>
    internal async Task EndAsync(IReadOnlyList<EndTarget> targets)
    {
        if (targets.Count == 0 || targets.Any(t => t.Risk == EndRisk.ByProcessOnly)) return;
        if (QuestionFor(targets) is { } question)
        {
            // Held from here on, not only once the box shows: the menu that asked is closing.
            _busy++;
            UpdateHold();
            EndAnswer answer;
            try { answer = await Ask(question); }
            finally
            {
                _busy--;
                UpdateHold();
            }
            if (answer == EndAnswer.Cancel) return;
            if (answer == EndAnswer.Restart)
            {
                await RestartAsync(targets[0]);
                return;
            }
        }

        var outcomes = await RunAsync(targets, () => Ender.End([.. targets.Select(t => (t.App.Exe, t.Process?.Pid))]));
        var ended = new List<EndTarget>();
        for (int i = 0; i < targets.Count; i++)
        {
            if (outcomes[i] != EndOutcome.Ended) continue;
            ended.Add(targets[i]);
            _send(TaskEnded.Command, TaskEnded.Format(targets[i].LongName, targets[i].App.Exe, targets[i].MemMB));
        }
        Say(Outcome(targets, outcomes, ended));
    }

    /// <summary>Ends Windows Explorer and starts it again.</summary>
    internal async Task RestartAsync(EndTarget target)
    {
        var outcome = (await RunAsync([target], () => [Ender.Restart(target.App.Exe)], stays: true))[0];
        if (outcome == EndOutcome.Ended) Say($"Restarted {target.Name}");
        else Say(Refusal("restart", target.Name, outcome), warns: true);
    }

    /// <summary>
    /// Does the ending off the window's thread, the rows dimmed and the list held still meanwhile. A row that was
    /// ended stays dimmed until the agent's next list drops it; one that wasn't comes back as it was.
    /// </summary>
    /// <param name="stays">What was ended is back at once (a restart): its row doesn't wait for the agent's list.</param>
    private async Task<IReadOnlyList<EndOutcome>> RunAsync(IReadOnlyList<EndTarget> targets, Func<IReadOnlyList<EndOutcome>> work, bool stays = false)
    {
        foreach (var t in targets) SetEnding(t, true);
        _busy++;
        UpdateHold();
        IReadOnlyList<EndOutcome> outcomes;
        try
        {
            outcomes = await Task.Run(work);
        }
        catch (Exception ex)
        {
            Log.Error("processes", ex);
            outcomes = [.. targets.Select(_ => EndOutcome.Refused)];
        }
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (stays || outcomes[i] is EndOutcome.Refused or EndOutcome.Cancelled) SetEnding(target, false);
            else _ending.Add((() => SetEnding(target, false), 2));
        }
        _busy--;
        // The list kept aside meanwhile is from before: the agent is asked for one from now.
        UpdateHold();
        Live.RequestProcs();
        return outcomes;
    }

    private static void SetEnding(EndTarget target, bool ending)
    {
        if (target.Process is { } process) process.IsEnding = ending;
        else target.App.IsEnding = ending;
    }

    private (string Text, bool Warns) Outcome(IReadOnlyList<EndTarget> targets, IReadOnlyList<EndOutcome> outcomes, List<EndTarget> ended)
    {
        string freed = Units.Megabytes(ended.Sum(t => t.MemMB));
        int failedAt = Enumerable.Range(0, targets.Count).FirstOrDefault(i => outcomes[i] is EndOutcome.Refused or EndOutcome.Cancelled, -1);
        if (targets.Count == 1)
        {
            string name = targets[0].Name;
            return outcomes[0] switch
            {
                EndOutcome.Ended => ($"Ended {name} · {freed} freed", false),
                EndOutcome.Gone => ($"{name} had already closed.", false),
                _ => (Refusal("end", name, outcomes[0]), true),
            };
        }
        if (failedAt >= 0)
        {
            string refusal = Refusal("end", targets[failedAt].Name, outcomes[failedAt]);
            return (ended.Count > 0 ? $"Ended {ended.Count} of {targets.Count} apps. {refusal}" : refusal, true);
        }
        return ended.Count == 0 ? ("They had already closed.", false)
            : ($"Ended {(ended.Count == 1 ? ended[0].Name : $"{ended.Count} apps")} · {freed} freed", false);
    }

    private static string Refusal(string what, string name, EndOutcome outcome) =>
        outcome == EndOutcome.Cancelled ? $"Couldn't {what} {name}. Permission wasn't given." : $"Couldn't {what} {name}. Windows refused.";

    // ── What happened, said at the bottom of the page for a moment ──

    private readonly DispatcherTimer _messageTimer;

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageWarns;

    private void Say((string Text, bool Warns) said) => Say(said.Text, said.Warns);

    private void Say(string text, bool warns = false)
    {
        MessageWarns = warns;
        Message = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    /// <summary>The message has been up long enough (the tests don't wait for the clock).</summary>
    internal void ClearMessage()
    {
        _messageTimer.Stop();
        Message = null;
    }
}

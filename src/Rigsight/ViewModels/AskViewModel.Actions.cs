using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;
using Rigsight.Services;

namespace Rigsight.ViewModels;

public enum AskActionState { Ready, Confirming, Working, Done, Failed }

/// <summary>How a button looks in the Next step box: the main one solid, the next quieter, any third as a link.</summary>
public enum AskActionRole { Main, Second, Link }

/// <summary>
/// A paragraph's sources as its pill shows them: the first one's name and "+2", and the list on hover (RigSight's records
/// open their page, web pages open the site).
/// </summary>
public sealed partial class AskRefs(IReadOnlyList<AiSource> list) : ObservableObject
{
    public IReadOnlyList<AiSource> List { get; } = list;
    public AiSource First => List[0];
    public bool IsApp => First.IsApp;
    public string Mark => First.Mark;
    /// <summary>The web mark's colour, the same for a site every time (0 to 4, one of the palette's own).</summary>
    public int Tone => IsApp ? -1 : First.Site.Aggregate(0, (h, ch) => (h * 31 + ch) & 0xFFFF) % 5;
    public string Label => First.Site.Length > 15 ? First.Site[..14].TrimEnd() + "…" : First.Site;
    public string More => $"+{List.Count - 1}";
    public bool HasMore => List.Count > 1;
    public string Name => List.Count == 1 ? Lang.T($"Source: {First.Site}") : Lang.T($"{List.Count} sources");
    [ObservableProperty] private bool _isOpen;
}

/// <summary>
/// One button under an answer and how it's going: ready, asking first (Close only), at work, done or refused, said in
/// its own place so nothing below it moves.
/// </summary>
public sealed partial class AskAction(AiAction action) : ObservableObject
{
    public AiAction Action { get; } = action;
    public AskActionRole Role { get; init; }
    /// <summary>As a line: what it's for, after its name ("Open Processes · see which apps hold the other 24 GB").</summary>
    public string? Why { get; init; }
    public bool HasWhy => Why is not null;
    /// <summary>In the card of a step that changes something (Close): its title already names the app, so "Close it".</summary>
    public bool InCard { get; init; }
    public string ButtonLabel => InCard && Action.Kind == AiActionKind.CloseApp ? Lang.T("Close it") : Label;
    public AiActionKind Kind => Action.Kind;
    public bool IsMain => Role == AskActionRole.Main;
    public bool IsSecond => Role == AskActionRole.Second;
    public bool IsLink => Role == AskActionRole.Link;
    public string Label => Action.Label;
    public string Glyph => Action.Glyph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsConfirming), nameof(IsWorking), nameof(IsDone), nameof(IsFailed), nameof(IsSaid))]
    private AskActionState _state;
    [ObservableProperty] private string _said = "";

    public bool IsReady => State == AskActionState.Ready;
    public bool IsConfirming => State == AskActionState.Confirming;
    public bool IsWorking => State == AskActionState.Working;
    public bool IsDone => State == AskActionState.Done;
    public bool IsFailed => State == AskActionState.Failed;
    public bool IsSaid => State is AskActionState.Done or AskActionState.Failed;

    /// <summary>When this Close closed its app: a Check again from the same answer compares either side of it.</summary>
    public DateTime? ClosedAt { get; set; }
    /// <summary>The check this Check again started (moved to the close, if its answer's Close comes after it).</summary>
    public AskCheck? Started { get; set; }

    public string ConfirmTitle => Lang.T($"Close {Action.Focus}?");
    /// <summary>What will happen to this app: one with a window is asked (it can ask to save); one without is ended at once.</summary>
    public string ConfirmText => Action.Windowless
        ? Lang.T("It has no window to ask, so it's ended straight away.")
        : Lang.T("It's asked to close, so it can ask you to save first.");
}

/// <summary>
/// A line in the conversation for something a button did ("Closed Python at 10:42 PM"): kept with it, and shown once it
/// is opened again, where the buttons are gone. Live, the button itself says it, so it isn't said twice.
/// </summary>
public sealed record AskDone(string Text) : AskMessage
{
    public bool Live { get; init; }
}

/// <summary>
/// A Check again: the minutes either side of the moment it was pressed, compared once the after side has passed. Until
/// then it counts down; then it shows each reading before and after, from RigSight's records.
/// </summary>
/// <param name="At">The middle of the comparison: when the answer's Close closed its app (so a check pressed late, or early,
/// still compares before the app went with after), or else when Check again was pressed.</param>
/// <param name="Closed">The app that closed at <paramref name="At"/>, when it was a Close; null for a plain moment.</param>
public sealed record AskCheck(DateTime At, int Minutes, CheckWhat What, string? Closed = null) : AskMessage, INotifyPropertyChanged
{
    public DateTime Due => At.AddMinutes(Minutes);
    public string Title => Lang.T($"Checking {ActionCheck.Words(What)} again");
    public string Detail => Closed is { } app
        ? Lang.T($"{Minutes} minutes before {app} closed at {Time(At)} against the {Minutes} after")
        : Lang.T($"{Minutes} minutes before {Time(At)} against the {Minutes} after");

    private CheckResult? _result;
    public CheckResult? Result { get => _result; set { _result = value; Changed(nameof(Result), nameof(IsWaiting), nameof(HasResult), nameof(Rows), nameof(Note), nameof(HasNote), nameof(Left), nameof(Heading)); } }
    public bool IsWaiting => Result is null;
    public bool HasResult => Result is { HasRows: true };
    public IReadOnlyList<CheckRow> Rows => Result?.Rows ?? [];
    public string? Note => Result?.Note;
    public bool HasNote => Note is not null;
    public string Heading => Result is null ? Title : Closed is { } app
        ? Lang.T($"{char.ToUpper(ActionCheck.Words(What)[0], CultureInfo.CurrentCulture)}{ActionCheck.Words(What)[1..]}, before and after {app} closed")
        : Lang.T($"{char.ToUpper(ActionCheck.Words(What)[0], CultureInfo.CurrentCulture)}{ActionCheck.Words(What)[1..]}, before and after {Time(At)}");

    /// <summary>"Ready at 10:52 PM · 8 min left", kept up to date by the window's timer.</summary>
    public string Left
    {
        get
        {
            var left = Due - DateTime.Now;
            return left <= TimeSpan.Zero ? Lang.T("Comparing…") : Lang.T($"Ready at {Time(Due)} · {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min left");
        }
    }

    public void Tick() => Changed(nameof(Left));

    /// <summary>As saved: "1729615200 10 Fans", then the closed app's name when there was one ("… Memory DataCruncher").</summary>
    public string Saved => $"{TimeUtil.ToUnix(At)} {Minutes} {What}" + (Closed is { } app ? " " + app : "");

    public static AskCheck? FromSaved(string text)
    {
        var p = text.Split(' ', 4);
        return p.Length >= 3 && long.TryParse(p[0], CultureInfo.InvariantCulture, out long at) && int.TryParse(p[1], CultureInfo.InvariantCulture, out int minutes)
            && Enum.TryParse<CheckWhat>(p[2], out var what) ? new AskCheck(TimeUtil.FromUnix(at), minutes, what, p.Length == 4 ? p[3] : null) : null;
    }

    internal static string Time(DateTime t) => t.ToString(TimeText.Fit("h:mm tt"), CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new(n)); }
}

public sealed partial class AskViewModel
{
    /// <summary>Opens a page, with what to show on it: a drive on Storage ("I"), a game on Games (its key), an order on Processes.</summary>
    public Action<string, string?>? OpenPageAt { get; set; }
    public Action? StartAgent { get; set; }
    public Action? CopyReport { get; set; }
    /// <summary>Sends the agent a command (the Timeline's line for an app closed from an answer).</summary>
    public Action<string, string?>? SendAgent { get; set; }
    /// <summary>Whether RigSight's agent is running (a Start the agent button is only for when it isn't).</summary>
    public Func<bool> AgentRunning { get; set; } = () => true;

    /// <summary>What closes apps and opens Windows' screens (the tests give their own).</summary>
    internal AppCloser Closer { get; set; } = new();
    internal Action<string> Launch { get; set; } = target => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    /// <summary>Where Check again reads its minutes (the tests give their own history).</summary>
    internal string? CheckDb { get; set; }

    /// <summary>This answer's buttons, from its "[do: …]" lines, each checked against what it looked at and what is there.</summary>
    private Func<IReadOnlyList<string>, IReadOnlyList<AiAction>> Resolver(AiTools tools, IReadOnlyList<AiLookUp> lookUps) =>
        markers => AiActions.Resolve(markers, new AiActionContext(lookUps, tools.LastRunning, tools.Installed, AgentRunning()));

    [RelayCommand]
    private async Task Act(AskAction? a)
    {
        if (a is null || !a.IsReady) return;
        var action = a.Action;
        try
        {
            switch (action.Kind)
            {
                case AiActionKind.CloseApp:
                    a.State = AskActionState.Confirming;
                    return;
                case AiActionKind.CheckAgain:
                    StartCheck(a);
                    return;
                case AiActionKind.OpenPage:
                    OpenPageAt?.Invoke(action.Target, action.Focus);
                    break;
                case AiActionKind.OpenWindows:
                case AiActionKind.VerifyGame:
                    Launch(action.Target);
                    Log.Write("ask", $"Opened {action.Target} from an answer");
                    break;
                case AiActionKind.OpenSharing:
                    OpenSettings?.Invoke();
                    break;
                case AiActionKind.StartAgent:
                    StartAgent?.Invoke();
                    break;
                case AiActionKind.CopyReport:
                    CopyReport?.Invoke();
                    a.Said = Lang.T("Copied. Paste it in your message to support.");
                    a.State = AskActionState.Done;
                    return;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            Log.Write("ask", $"Couldn't open {action.Target} from an answer: {ex.Message}");
            a.Said = Lang.T("Windows couldn't open it on this PC.");
            a.State = AskActionState.Failed;
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelAct(AskAction? a) { if (a?.IsConfirming == true) a.State = AskActionState.Ready; }

    /// <summary>Close, after its question: off the window's thread (the app gets a few seconds to close by itself).</summary>
    [RelayCommand]
    private async Task ConfirmAct(AskAction? a)
    {
        if (a is null || !a.IsConfirming) return;
        var action = a.Action;
        a.State = AskActionState.Working;
        var outcome = await Task.Run(() => Closer.Close(action.Target));
        string name = action.Focus ?? action.Target, at = AskCheck.Time(DateTime.Now);
        (a.Said, a.State) = outcome switch
        {
            CloseOutcome.Closed or CloseOutcome.Ended => (action.MemMb >= 100 ? Lang.T($"Closed {name} at {at} · {Units.Megabytes(action.MemMb)} freed") : Lang.T($"Closed {name} at {at}"), AskActionState.Done),
            CloseOutcome.Gone => (Lang.T($"{name} had already closed."), AskActionState.Done),
            CloseOutcome.StillOpen => (Lang.T($"{name} is still open. It may be asking you to save: answer it, or close it yourself."), AskActionState.Failed),
            CloseOutcome.NeedsAdmin => (Lang.T($"{name} runs with admin rights, so it wasn't closed from here. Close it yourself if you want to."), AskActionState.Failed),
            _ => (Lang.T($"Windows wouldn't close {name}."), AskActionState.Failed),
        };
        Log.Write("ask", $"Close {action.Target} from an answer: {outcome}");
        if (a.IsDone && outcome != CloseOutcome.Gone)
        {
            a.ClosedAt = DateTime.Now;
            // A Check again from this answer pressed before the close, still waiting: its middle moves to the close.
            foreach (var other in Siblings(a))
            {
                if (other.Started is not { IsWaiting: true } early || Current is not { } talk || talk.Messages.IndexOf(early) is not (>= 0 and var slot)) continue;
                var moved = new AskCheck(a.ClosedAt.Value, early.Minutes, early.What, action.Focus);
                talk.Messages[slot] = moved;
                other.Started = moved;
                other.Said = Lang.T($"Checking at {AskCheck.Time(moved.Due)}");
            }
            SendAgent?.Invoke(TaskEnded.Command, TaskEnded.Format(name, action.Target, action.MemMb));
            Current?.Messages.Add(new AskDone(a.Said) { Live = true });
            Save();
        }
    }

    // ── Check again ──────────────────────────────────────────────────────
    private System.Windows.Threading.DispatcherTimer? _checks;

    /// <summary>The other buttons of the answer <paramref name="a"/> is in.</summary>
    private IReadOnlyList<AskAction> Siblings(AskAction a) =>
        Current?.Messages.OfType<AskAnswer>().SelectMany(m => m.Parts.OfType<AskNext>()).FirstOrDefault(n => n.Actions.Contains(a))?.Actions ?? [];

    private void StartCheck(AskAction a)
    {
        var talk = Current;
        if (talk is null) return;
        // Closed already from this answer: before and after the close, however long ago it was pressed (pressed later, both
        // sides would be after it and closing would look as if it did nothing).
        var close = Siblings(a).FirstOrDefault(x => x.Action.Kind == AiActionKind.CloseApp && x.ClosedAt is not null);
        var check = close is { ClosedAt: { } closedAt }
            ? new AskCheck(closedAt, a.Action.Minutes, a.Action.What, close.Action.Focus)
            : new AskCheck(DateTime.Now, a.Action.Minutes, a.Action.What);
        a.Started = check;
        a.Said = check.Due <= DateTime.Now ? Lang.T("Comparing…") : Lang.T($"Checking at {AskCheck.Time(check.Due)}");
        a.State = AskActionState.Done;
        talk.Messages.Add(check);
        if (check.Due <= DateTime.Now) _ = CompareAsync(check);
        Log.Write("ask", $"Check again: {check.What} over {check.Minutes} min either side of {check.At:HH:mm}");
        Watch();
        Save();
        ToEnd();
    }

    /// <summary>Every check still waiting, in any conversation opened this run: the countdowns tick, and each due one is compared.</summary>
    private void Watch()
    {
        if (_checks is not null) { _checks.Start(); return; }
        _checks = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _checks.Tick += (_, _) => TickChecks();
        _checks.Start();
    }

    private void TickChecks()
    {
        var waiting = Conversations.SelectMany(c => c.Messages.OfType<AskCheck>()).Where(c => c.IsWaiting).ToList();
        if (waiting.Count == 0) { _checks?.Stop(); return; }
        foreach (var check in waiting)
        {
            check.Tick();
            if (DateTime.Now >= check.Due) _ = CompareAsync(check);
        }
    }

    private readonly HashSet<AskCheck> _comparing = [];

    /// <summary>Reads the minutes either side and shows the result (off the window's thread).</summary>
    internal async Task CompareAsync(AskCheck check)
    {
        if (!_comparing.Add(check)) return;
        try
        {
            string? path = CheckDb;
            var result = await Task.Run(() =>
            {
                using var db = path is null ? RigsightDb.OpenReader() : RigsightDb.OpenReader(path);
                return db is null ? new CheckResult([], false, Lang.T("RigSight's history couldn't be opened to compare.")) : ActionCheck.Compare(db, check.At, check.Minutes, check.What);
            });
            if (result is null) return;
            check.Result = result;
            Log.Write("ask", $"Check again {check.What} at {check.At:HH:mm}: {(result.HasRows ? string.Join("; ", result.Rows.Select(r => $"{r.Label} {r.Before} -> {r.After}")) : result.Note)}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("ask", ex);
            check.Result = new CheckResult([], false, Lang.T("It couldn't be compared this time."));
        }
        finally { _comparing.Remove(check); }
    }

    /// <summary>A source in a paragraph's list: RigSight's page for a record, the site for a web page.</summary>
    [RelayCommand]
    private void OpenSource(AiSource? source)
    {
        if (source is null) return;
        if (source.IsApp && source.Page is { } page) OpenPageAt?.Invoke(page, null);
        else if (source.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            try { Launch(uri.AbsoluteUri); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Log.Write("ask", $"Couldn't open {uri.Host}: {ex.Message}"); }
        }
    }

    /// <summary>"What does this mean?" under a result: asked like a question (it is one, and counts as one).</summary>
    [RelayCommand]
    private void AskAboutCheck(AskCheck? check)
    {
        if (check?.Result is not { HasRows: true } r || IsAnswering || IsOutOfQuestions || ShowFirstQuestion) return;
        string rows = string.Join("; ", r.Rows.Select(x => $"{x.Label} {x.Before} before, {x.After} after ({x.Change})"));
        Ask(Lang.T($"I pressed Check again at {AskCheck.Time(check.At)}. Over the {check.Minutes} minutes before and after, RigSight found: {rows}. What does this tell me?"));
    }
}

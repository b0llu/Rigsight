using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
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
    /// <summary>
    /// RigSight's own record: the icon of the page it came from, the same as in the sidebar (a thermometer for Temperatures),
    /// so the mark says where it's from (the one chart sign for every page meant nothing, the person asked, 2026-10-23).
    /// </summary>
    public string? Icon => !IsApp ? null : PageSwitchesViewModel.Glyph(First.Page ?? (First.Site == Lang.T("Rewind") ? AiPages.Rewind
        : First.Site == Lang.T("Dashboards") ? AiPages.Dashboards : AiPages.Parts));
    /// <summary>The web mark's colour, the same for a site every time (0 to 4, one of the palette's own).</summary>
    public int Tone => IsApp ? -1 : First.Site.Aggregate(0, (h, ch) => (h * 31 + ch) & 0xFFFF) % 5;
    public string Label => First.Site.Length > 15 ? First.Site[..14].TrimEnd() + "…" : First.Site;
    public string More => $"+{List.Count - 1}";
    public bool HasMore => List.Count > 1;
    public string Name => List.Count == 1 ? Lang.T($"From {First.Site}") : Lang.T($"From {First.Site} and {List.Count - 1} more");
    [ObservableProperty] private bool _isOpen;
}

/// <summary>
/// One button under an answer and how it's going: ready, asking first (Close only), at work, done or refused, said in
/// its own place so nothing below it moves.
/// </summary>
public sealed partial class AskAction(AiAction action) : ObservableObject
{
    public override string ToString() => Label; // its name for screen readers (and UI automation)
    public AiAction Action { get; } = action;
    public AskActionRole Role { get; init; }
    /// <summary>An app's own icon on an "Open NVIDIA App" line, as the Start menu shows it; null keeps the plain sign.</summary>
    public System.Windows.Media.ImageSource? Icon => _icon ??= Action.Kind == AiActionKind.OpenApp && Action.Target.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase)
        ? ShellIcon.Of(Action.Target) : null;
    private System.Windows.Media.ImageSource? _icon;
    public bool HasIcon => Icon is not null;
    /// <summary>Try again after it failed: a Windows screen or an app that didn't open, a scan Windows stopped.</summary>
    public bool CanRetry => Action.Kind is AiActionKind.OpenWindows or AiActionKind.OpenApp or AiActionKind.OpenWeb or AiActionKind.VerifyGame or AiActionKind.ScanDrive;

    /// <summary>A drive scan's card (Scan only).</summary>
    public AskScan? Scan { get; } = action.Kind == AiActionKind.ScanDrive ? new AskScan(action.Target) : null;
    /// <summary>A widget, overlay or taskbar card's readings and what pressing it changed (those kinds only).</summary>
    public AskSetup? Setup { get; } = action.IsSetup ? new AskSetup(action) : null;
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

    /// <summary>A Check again still waiting for its after side: a clock beside "Checks at …", not a done tick.</summary>
    [ObservableProperty] private bool _pending;

    /// <summary>When Close was pressed and its app asked to close: the card's bar runs over the seconds it's given.</summary>
    public string ClosingText => Action.Windowless ? Lang.T("Closing…") : Lang.T($"Asking {Action.Focus} to close");
    public string ClosingWhy => Lang.T($"It gets up to {(int)AppCloser.DefaultGrace.TotalSeconds} seconds to save and close by itself");
    public bool ClosingWaits => !Action.Windowless;

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
/// Questions given from the admin site, told once above the question box: how many, on top of what, until when, and the
/// message written to them (the person's ask and approved mock-up, 2026-10-25). <paramref name="Ids"/>: every gift it tells.
/// </summary>
public sealed record AskGift(IReadOnlyList<long> Ids, string Title, string Text, string? Note)
{
    /// <summary>A free-trial note instead of a gift ("ending:…", "ended:…"): told once, with See plans beside Got it.</summary>
    public string? TrialKey { get; init; }
    public bool IsTrial => TrialKey is not null;
    public override string ToString() => Title; // its name for screen readers (and UI automation)
}

/// <summary>An answer's plan card, in its parts where it shows (it reads its answer's page, plan and question).</summary>
public sealed record AskLockedCard(AskAnswer Answer)
{
    public override string ToString() => Title; // its name for screen readers (and UI automation)
    public string Title => Answer.LockedTitle;
    public string Text => Answer.LockedText;
    public string Button => Answer.LockedButton;
    public bool HasPlan => Answer.HasLockedPlan;
    public AskPaying Pay => Answer.Pay;
    public string? PlanId => Answer.LockedPlanId;
}

/// <summary>A part of a bar in the chat: a drive scan's progress, or a folder's share of a finished scan.</summary>
public sealed record AskBarPart(double Weight, string Brush, string Name = "", string Size = "") : Models.IBarPart;

/// <summary>
/// A drive scan from an answer, as one card that changes in place (the approved artifact of 2026-10-24, after the
/// person's "after pressing it, the block is just gone, then a loader starts"): before, the drive and how full it is with
/// Scan; while scanning, a real bar (found so far against what the drive holds), the files counted, the folder being read
/// and Stop; after, the biggest folders as one bar with Open on Storage; failed, what happened and Try again.
/// </summary>
public sealed partial class AskScan : ObservableObject
{
    public AskScan(string letter)
    {
        Letter = letter;
        try
        {
            var d = new DriveInfo(letter);
            if (d.IsReady) (Total, Used) = (d.TotalSize, d.TotalSize - d.TotalFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public string Letter { get; }
    public long Total { get; }
    public long Used { get; }
    public bool HasSize => Total > 0;

    public string Title => Lang.T($"Scan the {Letter} drive");
    public string ReadyText => Lang.T("Finds which folders and files fill it. Only reads, deletes nothing.");
    public string UsedText => Lang.T($"{Units.Bytes(Used)} used");
    public string FreeText => Lang.T($"{Units.Bytes(Total - Used)} free of {Units.Bytes(Total)}");
    public IReadOnlyList<AskBarPart> UsedBar => [new(Used, "FaintBrush"), new(Math.Max(0, Total - Used), "HoverBrush")];

    // ── While scanning ──
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Percent), nameof(ScanningTitle), nameof(FoundText), nameof(FilesText), nameof(ProgressBar))] private long _found;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FilesText))] private long _files;
    [ObservableProperty] private string _folder = "";
    /// <summary>Set at the end, so the bar reaches the end before the result shows.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Percent), nameof(ScanningTitle), nameof(ProgressBar))] private bool _finishing;

    /// <summary>How far along: what was found against what the drive holds (a scan never quite knows the end, so 99 until it's done).</summary>
    public int Percent => Finishing ? 100 : Used > 0 ? (int)Math.Clamp(100.0 * Found / Used, 0, 99) : 0;
    public string ScanningTitle => Used > 0 ? Lang.T($"Scanning the {Letter} drive · {Percent}%") : Lang.T($"Scanning the {Letter} drive");
    public string FoundText => Used > 0 ? Lang.T($"{Units.Bytes(Found)} of {Units.Bytes(Used)}") : Units.Bytes(Found);
    public string FilesText => Lang.T($"{Files:N0} files");
    public IReadOnlyList<AskBarPart> ProgressBar => [new(Math.Max(Percent, 1), "SkyBrush"), new(Math.Max(0, 100 - Percent), "HoverBrush")];

    // ── Done ──
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DoneText), nameof(Folders), nameof(Top), nameof(HasFolders))] private ScanResult? _result;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DoneText))] private DateTime _doneAt;
    /// <summary>Said under the result when the AI couldn't be asked about it this time (no questions left, or a question running).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLater))] private string? _later;
    public bool HasLater => Later is not null;
    public string DoneTitle => Lang.T($"Scanned the {Letter} drive");
    public string DoneText => Result is not { } r ? ""
        : Lang.T($"{Units.Bytes(r.Root.Size)} in {r.FileCount:N0} files · took {Took(r.Elapsed)} · {AskCheck.Time(DoneAt)}");
    private static string Took(TimeSpan t) => t.TotalSeconds < 60 ? Lang.T($"{t.TotalSeconds:0.0} s") : Lang.T($"{t.TotalMinutes:0} min");

    private static readonly string[] Palette = ["CpuBrush", "PurpleBrush", "GpuBrush"];
    /// <summary>The biggest folders as one bar: the three biggest in the palette's own colours, the rest together in grey.</summary>
    public IReadOnlyList<AskBarPart> Folders
    {
        get
        {
            if (Result is not { } r || r.Root.Size <= 0) return [];
            var top = r.Root.Children.Where(c => c.Size > 0).OrderByDescending(c => c.Size).Take(3).ToList();
            var parts = top.Select((c, i) => new AskBarPart(c.Size, Palette[i], c.IsBucket ? c.Name : Name(c), Units.Bytes(c.Size))).ToList();
            long rest = r.Root.Size - top.Sum(c => c.Size);
            if (rest > 0) parts.Add(new AskBarPart(rest, "FaintBrush", Lang.T("Everything else"), Units.Bytes(rest)));
            return parts;
        }
    }
    public IReadOnlyList<AskBarPart> Top => [.. Folders.Where(f => f.Brush != "FaintBrush")];
    public bool HasFolders => Folders.Count > 0;
    private static string Name(FolderNode n) => string.IsNullOrEmpty(n.Name) ? n.Path : n.Name;

    // ── Failed ──
    [ObservableProperty] private string _failed = "";
    public string FailedTitle => Lang.T($"Couldn't scan the {Letter} drive");

    /// <summary>Stop, while scanning.</summary>
    internal CancellationTokenSource? Stop { get; set; }

    internal void Reset() { Found = 0; Files = 0; Folder = ""; Finishing = false; Result = null; Later = null; Failed = ""; }
}

/// <summary>A reading on a widget, overlay or taskbar card, as a chip: already there (a tick, not added twice), or with a note.</summary>
public sealed record AskChip(string Name, bool On, string? Note = null)
{
    public override string ToString() => Spoken; // its name for screen readers (and UI automation)
    public string Text => Note is null ? Name : $"{Name} {Note}";
    public string Spoken => On ? Lang.T($"{Name}, already on") : Text;
}

/// <summary>
/// Readings an answer suggests for a new widget, the in-game overlay or the taskbar, as one card (the approved board 7 of
/// 2026-10-26): the readings as chips, where they'd go, and one press to make or add them, with Undo. Nothing changes until
/// it's pressed; what pressing changed is kept here, so Undo puts back exactly what was there.
/// </summary>
public sealed partial class AskSetup(AiAction action) : ObservableObject
{
    public AiActionKind Kind { get; } = action.Kind;
    public IReadOnlyList<AiReading> Readings { get; } = action.Readings ?? [];
    public string Title => action.Label;
    public string Glyph => action.Glyph;

    public string MainLabel => Kind switch
    {
        AiActionKind.CreateWidget when ExistingName is { } name => Lang.T($"Turn on {name}"),
        AiActionKind.CreateWidget => Lang.T("Create and turn on"),
        AiActionKind.AddOverlay => Lang.T("Add to overlay"),
        _ => Lang.T("Add to taskbar"),
    };
    /// <summary>The quieter button: Customize for a widget, Open Taskbar for the taskbar; none for the overlay.</summary>
    public string? SecondLabel => Kind switch
    {
        AiActionKind.CreateWidget => Lang.T("Customize"),
        AiActionKind.AddTaskbar => Lang.T("Open Taskbar"),
        _ => null,
    };
    public bool HasSecond => SecondLabel is not null;
    public string OpenLabel => Kind switch
    {
        AiActionKind.CreateWidget => Lang.T("Open Widgets"),
        AiActionKind.AddOverlay => Lang.T("Open Overlay"),
        _ => Lang.T("Open Taskbar"),
    };
    public string Page => Kind switch { AiActionKind.CreateWidget => "widgets", AiActionKind.AddOverlay => "overlay", _ => "taskbar" };
    /// <summary>Everything is there already: said instead of the button.</summary>
    public string AllOnText => Kind switch
    {
        AiActionKind.CreateWidget => Lang.T($"All of these are on {ExistingName} already."),
        AiActionKind.AddOverlay => Lang.T("All of these are on your overlay already."),
        _ => Lang.T("All of these are in the taskbar already."),
    };

    /// <summary>
    /// A widget of theirs that already shows every one of these readings (made from an earlier answer, or by hand): turned
    /// on, or said to be on, instead of a second widget of the same readings (the user, 2026-10-26: one made earlier and
    /// switched off was offered again as a new one).
    /// </summary>
    internal string? ExistingId { get; private set; }
    public string? ExistingName { get; private set; }

    [ObservableProperty] private IReadOnlyList<AskChip> _chips = [];
    [ObservableProperty] private string _where = "";
    /// <summary>Something to make or add (or an overlay to turn on): otherwise the card says it's all there.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllOn))] private bool _hasNew;
    public bool AllOn => !HasNew;
    [ObservableProperty] private string _doneText = "";

    /// <summary>What pressing adds, worked out by <see cref="Refresh"/> from the settings now.</summary>
    internal IReadOnlyList<AiReading> ToAdd { get; private set; } = [];

    // What the press changed, for Undo.
    internal string? CreatedId { get; set; }
    internal string? TurnedOnId { get; set; }
    internal List<OverlayMetric>? MetricsBefore { get; set; }
    internal List<OverlaySensor>? SensorsBefore { get; set; }
    internal bool? EnabledBefore { get; set; }
    internal List<string>? TrayBefore { get; set; }

    /// <summary>
    /// The chips and the line under them from the settings now: a reading already there is a ticked chip and isn't added
    /// again; a frame reading says it needs RivaTuner while RivaTuner isn't running; an overlay sensor past the overlay's
    /// ten says there's no room.
    /// </summary>
    public void Refresh(RigsightSettings s, string rivaTuner)
    {
        bool noRiva = rivaTuner is not ("" or "running");
        var chips = new List<AskChip>();
        var add = new List<AiReading>();
        int room = OverlaySettings.MaxSensors - s.Overlay.Sensors.Count;
        foreach (var r in Readings)
        {
            bool on = Kind switch
            {
                AiActionKind.AddOverlay => r.Metric is { } m ? s.Overlay.Metrics.Contains(m) : s.Overlay.Sensors.Any(x => x.Id == r.SensorId),
                AiActionKind.AddTaskbar => r.SensorId is { } id && s.TraySensors.Contains(id),
                _ => false,
            };
            if (on) { chips.Add(new AskChip(r.Name, true)); continue; }
            if (Kind == AiActionKind.AddOverlay && r.Metric is null && room-- <= 0) { chips.Add(new AskChip(r.Name, false, Lang.T("(no room)"))); continue; }
            chips.Add(new AskChip(r.Name, false, r.IsFrame && noRiva ? Lang.T("(needs RivaTuner)") : null));
            add.Add(r);
        }
        // A widget showing all of these: one that's on, or one of their own that's off (a ready-made one they never turned on
        // wasn't made by them, so a new one is still offered). The one that's on first, then the one with the fewest other
        // readings, then theirs over a ready-made one (the Slim bar shows CPU and GPU temperature and RAM in use too).
        var keys = Readings.Select(r => r.Key).ToHashSet();
        var existing = Kind != AiActionKind.CreateWidget ? null : s.Widgets
            .Where(w => (w.Enabled || w.Style == WidgetStyle.Custom) && keys.IsSubsetOf(WidgetCatalog.ShownItems(w).Select(i => i.Id)))
            .OrderByDescending(w => w.Enabled).ThenBy(w => WidgetCatalog.ShownItems(w).Count).ThenBy(w => w.Style != WidgetStyle.Custom)
            .FirstOrDefault();
        if (existing?.Enabled == true) chips = [.. chips.Select(c => c with { On = true })];
        ToAdd = add;
        Chips = chips;
        string onNames = string.Join(", ", chips.Where(c => c.On).Select(c => c.Name));
        string already = onNames.Length > 0 ? " " + Lang.T($"{onNames} already on.") : "";
        (ExistingId, ExistingName) = (existing?.Id, existing is null ? null : WidgetCatalog.Title(existing));
        OnPropertyChanged(nameof(MainLabel));
        OnPropertyChanged(nameof(AllOnText));
        Where = Kind switch
        {
            AiActionKind.CreateWidget when existing is { Enabled: true } => "", // said by the card's line instead
            AiActionKind.CreateWidget when existing is not null => Lang.T($"{ExistingName} already shows these, but it's off."),
            AiActionKind.CreateWidget => Lang.T("As a slim bar, on your desktop, where you can drag it."),
            AiActionKind.AddOverlay => Lang.T($"Shown with {s.Overlay.Hotkey} in any game.") + already
                + (s.Overlay.Enabled ? "" : " " + Lang.T("Adding turns the overlay on.")),
            _ => Lang.T("In the taskbar, always in view.") + (s.TraySensors.Count == 0 ? " " + Lang.T("Nothing is shown there yet.") : already),
        };
        HasNew = Kind == AiActionKind.CreateWidget ? existing?.Enabled != true
            : add.Count > 0 || (Kind == AiActionKind.AddOverlay && !s.Overlay.Enabled && chips.Any(c => c.On));
    }
}

/// <summary>
/// A line in the conversation for something a button did ("Closed Python at 10:42 PM"): kept with it, and shown once it
/// is opened again, where the buttons are gone. Live, the button itself says it, so it isn't said twice.
/// </summary>
public sealed record AskDone(string Text) : AskMessage
{
    public override string ToString() => Text; // its name for screen readers (and UI automation)
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
    public override string ToString() => Title; // its name for screen readers (and UI automation)
    public DateTime Due => At.AddMinutes(Minutes);
    public string Title => Lang.T($"Checking {ActionCheck.Words(What)} again");
    public string Detail => Closed is { } app
        ? Lang.T($"{Minutes} minutes before {app} closed at {Time(At)} against the {Minutes} after")
        : Lang.T($"{Minutes} minutes before {Time(At)} against the {Minutes} after");

    private CheckResult? _result;
    public CheckResult? Result { get => _result; set { _result = value; Changed(nameof(Result), nameof(IsWaiting), nameof(HasResult), nameof(Rows), nameof(HasRows), nameof(Note), nameof(HasNote), nameof(Left), nameof(Heading), nameof(SubLine)); } }
    public bool IsWaiting => Result is null;
    public bool HasResult => Result is { HasRows: true };
    public IReadOnlyList<CheckRow> Rows => Result?.Rows ?? BeforeRows;
    public string? Note => Result?.Note;
    public bool HasNote => Note is not null;
    /// <summary>Under the heading: what's being measured while it waits, what was compared once done.</summary>
    public string SubLine => IsWaiting ? Measuring : Detail;
    public string Heading => Closed is { } app
        ? Lang.T($"{char.ToUpper(ActionCheck.Words(What)[0], CultureInfo.CurrentCulture)}{ActionCheck.Words(What)[1..]}, before and after {app} closed")
        : Lang.T($"{char.ToUpper(ActionCheck.Words(What)[0], CultureInfo.CurrentCulture)}{ActionCheck.Words(What)[1..]}, before and after {Time(At)}");

    /// <summary>"Ready at 10:52 PM · 8 min left", kept up to date by the window's timer.</summary>
    public string Left
    {
        get
        {
            var left = Due - DateTime.Now;
            return left <= TimeSpan.Zero ? Lang.T("Comparing…") : Lang.T($"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min left");
        }
    }

    public void Tick() => Changed(nameof(Left), nameof(Progress), nameof(Measuring), nameof(SubLine));

    /// <summary>The minutes before, read when the check started: the rows show from the start, "after" fills in at the end.</summary>
    private IReadOnlyList<CheckRow> _before = [];
    public IReadOnlyList<CheckRow> BeforeRows { get => _before; set { _before = value; Changed(nameof(BeforeRows), nameof(Rows), nameof(HasRows)); } }
    internal bool BeforeRead { get; set; }
    public bool HasRows => Rows.Count > 0;
    /// <summary>The after side so far, as a bar (a known end, so not a spinner: the 2026-10-24 audit).</summary>
    public IReadOnlyList<AskBarPart> Progress
    {
        get
        {
            double done = Math.Clamp((DateTime.Now - At).TotalMinutes / Minutes, 0, 1);
            return [new(Math.Max(done, 0.01), "CpuBrush"), new(Math.Max(0, 1 - done), "HoverBrush")];
        }
    }
    public string Measuring
    {
        get
        {
            int done = (int)Math.Clamp(Math.Floor((DateTime.Now - At).TotalMinutes), 0, Minutes);
            return Closed is { } app ? Lang.T($"Measuring the {Minutes} minutes after {app} closed · {done} of {Minutes} minutes")
                : Lang.T($"Measuring the {Minutes} minutes after {Time(At)} · {done} of {Minutes} minutes");
        }
    }
    public string DueText => Lang.T($"Result at {Time(Due)}");

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

    /// <summary>
    /// This answer's buttons, from its "[do: …]" lines, each checked against what it looked at and what is there, which
    /// pages the answers may read now (no Scan when the plan or Settings › AI keeps Storage from them), and the PC tools here.
    /// </summary>
    private Func<IReadOnlyList<string>, IReadOnlyList<AiAction>> Resolver(AiTools tools, IReadOnlyList<AiLookUp> lookUps)
    {
        var canRead = CanReadNow();
        // The sensors as they are now, read here on the window's thread: the answer is drawn off it.
        var (sensors, tray) = (SetupSensors?.Invoke(), TrayReadings?.Invoke());
        return markers => AiActions.Resolve(markers, new AiActionContext(lookUps, tools.LastRunning, tools.Installed, AgentRunning(), canRead, OpenableOnPc, StartMenu, sensors, tray));
    }

    /// <summary>The PC's sensors by the names they show, for "[do: widget …]" and the like (null: none known; the tests give their own).</summary>
    public Func<IReadOnlyList<AiReading>>? SetupSensors { get; set; }
    /// <summary>The sensor the taskbar shows for each overlay reading on this PC ("CPU temperature": the CPU's main temperature).</summary>
    public Func<IReadOnlyDictionary<OverlayMetric, AiReading>>? TrayReadings { get; set; }
    /// <summary>RivaTuner's state as the Overlay page knows it ("running", "stopped", "missing"…; "" not known yet).</summary>
    public Func<string> RivaTuner { get; set; } = () => "";

    /// <summary>The PC's sensors as pickers list them, by the names they show, with their part.</summary>
    public static IReadOnlyList<AiReading> SensorsOf(LiveData live) =>
        [.. live.PickableSensors.Select(x => new AiReading(x.DisplayName, SensorId: x.Id, Part: x.HardwareShown))];

    /// <summary>The sensors the taskbar shows for the overlay's readings, as the Taskbar page's quick picks find them.</summary>
    public static IReadOnlyDictionary<OverlayMetric, AiReading> TrayOf(LiveData live)
    {
        var map = new Dictionary<OverlayMetric, AiReading>();
        void Put(OverlayMetric m, Models.SensorItem? s) { if (s is not null) map[m] = new AiReading(s.DisplayName, SensorId: s.Id, Part: s.HardwareShown); }
        Put(OverlayMetric.CpuTemp, live.CpuTemp); Put(OverlayMetric.CpuLoad, live.CpuLoad); Put(OverlayMetric.CpuClock, live.CpuClock); Put(OverlayMetric.CpuPower, live.CpuPower);
        Put(OverlayMetric.GpuTemp, live.GpuTemp); Put(OverlayMetric.GpuHotSpot, live.GpuHotSpot); Put(OverlayMetric.GpuLoad, live.GpuLoad); Put(OverlayMetric.GpuClock, live.GpuClock);
        Put(OverlayMetric.GpuPower, live.GpuPower); Put(OverlayMetric.GpuMemory, live.GpuVramUsed); Put(OverlayMetric.Ram, live.RamLoad);
        return map;
    }

    /// <summary>The widget, overlay and taskbar cards of an answer, from the settings now (on the window's thread).</summary>
    private void PrepareSetups(AskAnswer answer)
    {
        foreach (var setup in answer.Parts.OfType<AskNext>().SelectMany(n => n.Actions).Select(a => a.Setup).OfType<AskSetup>())
            setup.Refresh(_settings.Current, RivaTuner());
    }

    // ── A widget, the overlay, the taskbar (board 7) ──────────────────────

    /// <summary>
    /// "Create and turn on", "Add to overlay", "Add to taskbar" (and Customize, which makes the widget off and opens it in
    /// Widgets' editor): RigSight's own settings only, through the same settings as the pages, and each change kept for Undo.
    /// </summary>
    private void ApplySetup(AskAction a, bool customize = false)
    {
        if (a.Setup is not { } setup || !a.IsReady) return;
        var now = _settings.Current;
        setup.Refresh(now, RivaTuner());
        string asked = string.Join(", ", setup.Readings.Select(r => r.Name));
        switch (setup.Kind)
        {
            case AiActionKind.CreateWidget when setup.ExistingId is { } existing:
            {
                // Theirs already shows these, switched off: turned on, not made again (Customize opens it as it is).
                _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == existing) is { } w) w.Enabled = true; });
                setup.TurnedOnId = existing;
                setup.DoneText = Lang.T($"{setup.ExistingName} turned on");
                Log.Write("ask", $"Widget from an answer: {existing} \"{setup.ExistingName}\" already shows {asked}; turned it on");
                break;
            }
            case AiActionKind.CreateWidget:
            {
                var custom = now.Widgets.Where(w => w.Style == WidgetStyle.Custom).ToList();
                if (custom.Count >= WidgetCatalog.MaxCustom)
                {
                    a.Said = Lang.T($"You have {WidgetCatalog.MaxCustom} widgets of your own already, the most RigSight keeps. Delete one in Widgets first.");
                    a.State = AskActionState.Failed;
                    Log.Write("ask", $"Widget from an answer not made: already {custom.Count} of the user's own");
                    return;
                }
                // Named as the Widgets page's Create widget names one.
                int n = custom.Count + 1;
                string name = Lang.T($"My widget {n}");
                while (custom.Any(w => WidgetCatalog.Title(w) == name)) name = Lang.T($"My widget {++n}");
                string id = WidgetCatalog.NewId();
                var items = setup.Readings.Select(r => new WidgetItem { Id = r.Key }).ToList();
                _settings.Update(s => s.Widgets.Add(new WidgetConfig
                {
                    Style = WidgetStyle.Custom, Id = id, Name = name, Layout = WidgetLayout.Bar, Items = items, Enabled = !customize, Grayscale = true,
                }));
                setup.CreatedId = id;
                setup.DoneText = customize ? Lang.T("Created and opened in Widgets") : Lang.T("Created and turned on");
                Log.Write("ask", $"Widget from an answer: made {id} \"{name}\" ({(customize ? "off, opened in the editor" : "on")}) with {string.Join(", ", items.Select(i => i.Id))}");
                break;
            }
            case AiActionKind.AddOverlay:
            {
                var o = now.Overlay;
                (setup.MetricsBefore, setup.SensorsBefore, setup.EnabledBefore) = ([.. o.Metrics], [.. o.Sensors.Select(x => new OverlaySensor { Id = x.Id, Label = x.Label })], o.Enabled);
                var add = setup.ToAdd;
                _settings.Update(s =>
                {
                    foreach (var m in add.Select(r => r.Metric).OfType<OverlayMetric>()) if (!s.Overlay.Metrics.Contains(m)) s.Overlay.Metrics.Add(m);
                    s.Overlay.Metrics.Sort();
                    foreach (var id in add.Select(r => r.SensorId).OfType<string>())
                        if (s.Overlay.Sensors.Count < OverlaySettings.MaxSensors && s.Overlay.Sensors.All(x => x.Id != id)) s.Overlay.Sensors.Add(new OverlaySensor { Id = id });
                    s.Overlay.Enabled = true;
                });
                setup.DoneText = Lang.T("Added to the overlay");
                Log.Write("ask", $"Overlay from an answer: asked {asked}; added {(add.Count > 0 ? string.Join(", ", add.Select(r => r.Key)) : "nothing new")}"
                    + $"{(setup.EnabledBefore == false ? "; turned the overlay on" : "")}; skipped {(string.Join(", ", setup.Chips.Where(c => c.On || c.Note == Lang.T("(no room)")).Select(c => c.Text)) is { Length: > 0 } skip ? skip : "none")}");
                break;
            }
            default:
            {
                setup.TrayBefore = [.. now.TraySensors];
                var add = setup.ToAdd;
                _settings.Update(s =>
                {
                    foreach (var id in add.Select(r => r.SensorId).OfType<string>()) if (!s.TraySensors.Contains(id)) s.TraySensors.Add(id);
                });
                setup.DoneText = Lang.T("Added to the taskbar");
                Log.Write("ask", $"Taskbar from an answer: asked {asked}; added {string.Join(", ", add.Select(r => r.SensorId))}; already there {(string.Join(", ", setup.Chips.Where(c => c.On).Select(c => c.Name)) is { Length: > 0 } on ? on : "none")}");
                break;
            }
        }
        a.State = AskActionState.Done;
    }

    /// <summary>The card's quieter button: Customize makes the widget (off) and opens it in Widgets' editor; Open Taskbar opens the page.</summary>
    [RelayCommand]
    private void SetupSecond(AskAction? a)
    {
        if (a?.Setup is not { } setup || !a.IsReady) return;
        if (setup.Kind == AiActionKind.CreateWidget)
        {
            if (setup.ExistingId is { } existing) { OpenPageAt?.Invoke("widgets", existing); return; }
            ApplySetup(a, customize: true);
            if (a.IsDone) OpenPageAt?.Invoke("widgets", setup.CreatedId);
        }
        else OpenPageAt?.Invoke(setup.Page, null);
    }

    /// <summary>Open Widgets, Overlay or Taskbar from a card (the widget it made open in the editor).</summary>
    [RelayCommand]
    private void OpenSetup(AskAction? a)
    {
        if (a?.Setup is { } setup) OpenPageAt?.Invoke(setup.Page, setup.Kind == AiActionKind.CreateWidget ? setup.CreatedId ?? setup.TurnedOnId ?? setup.ExistingId : null);
    }

    /// <summary>Undo: exactly what was there before the press (the widget it made gone; the overlay's or taskbar's readings as they were), and the card ready again.</summary>
    [RelayCommand]
    private void UndoSetup(AskAction? a)
    {
        if (a?.Setup is not { } setup || !a.IsDone) return;
        switch (setup.Kind)
        {
            case AiActionKind.CreateWidget when setup.CreatedId is { } id:
                _settings.Update(s => s.Widgets.RemoveAll(w => w.Id == id));
                Log.Write("ask", $"Undo from an answer: removed widget {id}");
                setup.CreatedId = null;
                break;
            case AiActionKind.CreateWidget when setup.TurnedOnId is { } id:
                _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == id) is { } w) w.Enabled = false; });
                Log.Write("ask", $"Undo from an answer: turned widget {id} off again");
                setup.TurnedOnId = null;
                break;
            case AiActionKind.AddOverlay when setup.MetricsBefore is { } metrics && setup.SensorsBefore is { } sensors && setup.EnabledBefore is { } enabled:
                _settings.Update(s =>
                {
                    s.Overlay.Metrics = [.. metrics];
                    s.Overlay.Sensors = [.. sensors.Select(x => new OverlaySensor { Id = x.Id, Label = x.Label })];
                    s.Overlay.Enabled = enabled;
                });
                Log.Write("ask", $"Undo from an answer: overlay back to {string.Join(", ", metrics)}; sensors {string.Join(", ", sensors.Select(x => x.Id))}; {(enabled ? "on" : "off")}");
                (setup.MetricsBefore, setup.SensorsBefore, setup.EnabledBefore) = (null, null, null);
                break;
            case AiActionKind.AddTaskbar when setup.TrayBefore is { } tray:
                _settings.Update(s => s.TraySensors = [.. tray]);
                Log.Write("ask", $"Undo from an answer: taskbar back to {string.Join(", ", tray)}");
                setup.TrayBefore = null;
                break;
        }
        a.State = AskActionState.Ready;
        setup.Refresh(_settings.Current, RivaTuner());
    }

    /// <summary>Whether the answers may read a page now: in the plan (RigSight AI) and switched on in Settings › AI.</summary>
    private Func<string, bool> CanReadNow()
    {
        var plan = PlanPagesNow();
        var ai = _settings.Current.Ai;
        return page => (plan is null || plan.Contains(page)) && ai.Sends(page);
    }

    /// <summary>The known PC tools on this PC, for an "Open NVIDIA App" button (the tests give their own).</summary>
    internal Func<IReadOnlyDictionary<string, StartApp>> OpenableOnPc { get; set; } = () => OpenableApps.Installed(StartApps.List());
    /// <summary>The whole Start menu, for a tool off the list named by the answer (matched by name on this PC; the tests give their own).</summary>
    internal Func<IReadOnlyList<StartApp>> StartMenu { get; set; } = StartApps.List;

    /// <summary>
    /// An answer with a "[locked: …]" line gets its plan card (the cheapest plan that reads the page, and whether that's a
    /// switch), placed where the approved artifact has it: just before the next step's lines, else at the end.
    /// </summary>
    private AskAnswer WithLockedPlan(AskAnswer answer)
    {
        if (answer.Locked is not { } page) return answer;
        // The plan they have now already reads the page (bought since, or the model said so wrongly): no card to buy what
        // they have (board 6, 2026-10-26).
        if (Standing is not null && PlanPagesNow() is { } open && open.Contains(page.Id))
            return answer with { Locked = null, Parts = [.. answer.Parts.Where(p => p is not AskLockedCard)] };
        var offer = (Standing?.Plans ?? []).Where(o => !o.IsFree && o.Pages.Contains(page.Id)).OrderBy(o => o.PriceCents).FirstOrDefault();
        var withPlan = offer is null ? answer : answer with
        {
            LockedPlanId = offer.Id, LockedPlanName = offer.Name, LockedSwitch = Standing?.Plan is { IsFree: false, CanManage: true },
        };
        var card = new AskLockedCard(withPlan);
        var parts = withPlan.Parts.Where(p => p is not AskLockedCard).ToList();
        int at = parts.FindIndex(p => p is AskNext);
        parts.Insert(at < 0 ? parts.Count : at, card);
        return withPlan with { Parts = parts };
    }

    /// <summary>The plan card's main button: that plan, then the answer's question again once it's on.</summary>
    [RelayCommand]
    private async Task UseLocked(AskLockedCard? card)
    {
        if (card?.Answer is not { LockedPlanId: { } plan, LockedPlanName: { } name } answer) return;
        Log.Write("ask", $"Plan card under an answer: {(answer.LockedSwitch ? "switch to" : "get")} {plan} for {answer.Locked?.Id}");
        await UseWall(new AskWall(answer.LockedTitle, answer.LockedText, plan, name, answer.Asked) { Switch = answer.LockedSwitch, FromAnswer = true, Pay = answer.Pay });
    }

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
                case AiActionKind.OpenApp:
                    Launch(action.Target);
                    Log.Write("ask", $"Opened {action.Target} from an answer");
                    break;
                case AiActionKind.OpenWeb:
                    // A maker's download page: https only, in the browser (never run from here).
                    if (!Uri.TryCreate(action.Target, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps) return;
                    Launch(page.AbsoluteUri);
                    Log.Write("ask", $"Opened {page.Host} from an answer ({action.Focus} isn't on this PC)");
                    break;
                case AiActionKind.OpenSharing:
                    OpenSettings?.Invoke();
                    break;
                case AiActionKind.StartAgent:
                    StartAgent?.Invoke();
                    break;
                case AiActionKind.ScanDrive:
                    await ScanDriveAsync(a);
                    return;
                case AiActionKind.CreateWidget:
                case AiActionKind.AddOverlay:
                case AiActionKind.AddTaskbar:
                    ApplySetup(a);
                    return;
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

    /// <summary>Scans a drive (off the window's thread), for the scan itself (tests give their own).</summary>
    internal Func<string, IProgress<(long Files, long Bytes)>, CancellationToken, IProgress<string>?, Task<ScanResult>> ScanDrive { get; set; } =
        (root, progress, ct, folder) => StorageScanner.ScanAsync(root, progress, ct, folder: folder);

    /// <summary>
    /// "Scan the E drive" under an answer (the person's idea, 2026-10-24): the drive is scanned where the button is, how
    /// far along said on it, and once done the AI is asked by itself what fills it and what could be freed, with the
    /// scan to look at (get_drive_scan). That question counts like any other; with none left, the scan stays for later.
    /// </summary>
    private async Task ScanDriveAsync(AskAction a)
    {
        string letter = a.Action.Target, root = letter + @":\";
        var scan = a.Scan ?? throw new InvalidOperationException("a Scan button without its card");
        // Pressed again from an old answer after the plan dropped Storage: the scan would be for nobody to read.
        if (!CanReadNow()(AiPages.Storage))
        {
            scan.Failed = Lang.T("Your plan doesn't include Storage now, so the AI couldn't read a scan. Storage on the sidebar scans it on any plan.");
            a.State = AskActionState.Failed;
            Log.Write("ask", $"Scan of {root} from an answer not started: the answers can't read Storage now");
            return;
        }
        scan.Reset();
        a.State = AskActionState.Working;
        using var stop = scan.Stop = new CancellationTokenSource();
        var progress = new Progress<(long Files, long Bytes)>(p => (scan.Files, scan.Found) = (p.Files, p.Bytes));
        var folder = new Progress<string>(f => scan.Folder = f);
        var talk = Current;
        var started = DateTime.Now;
        try
        {
            var result = await ScanDrive(root, progress, stop.Token, folder);
            // Shown for a second at least, the bar run to its end: a 1.5 s scan only flashed by.
            scan.Finishing = true;
            if (DateTime.Now - started < MinScanShown) await Task.Delay(MinScanShown - (DateTime.Now - started));
            DriveScans.Keep(root, result);
            (scan.Result, scan.DoneAt) = (result, DateTime.Now);
            a.State = AskActionState.Done;
            Log.Write("ask", $"Scanned {root} from an answer: {result.FileCount} files, {result.Root.Size} bytes in {result.Elapsed.TotalSeconds:0.0} s");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Log.Write("ask", $"Scan of {root} from an answer stopped after {scan.Files} files");
            scan.Reset();
            a.State = AskActionState.Ready;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write("ask", $"Couldn't scan {root} from an answer after {scan.Files} files: {ex.Message}");
            scan.Failed = scan.Files > 0
                ? Lang.T($"Windows stopped the scan after {scan.Files:N0} files. The drive may have been unplugged or gone to sleep.")
                : Lang.T("Windows wouldn't let RigSight read it. The drive may have been unplugged or gone to sleep.");
            a.State = AskActionState.Failed;
            return;
        }
        finally { scan.Stop = null; }
        // The question that offered the scan, answered again from it (the person's call, 2026-10-24: "after reading the
        // drive…", not a new question of RigSight's own): in that answer's place when it's the latest, else at the end.
        if (talk is null || Current != talk || ShowFirstQuestion) return;
        if (IsOutOfQuestions || IsAnswering)
        {
            // Kept for later: the card says so, and the scan is there for the next question about it.
            scan.Later = IsOutOfQuestions ? Lang.T("Saved. Ask about it when your questions come back.") : Lang.T("Saved. Ask about it once this answer is done.");
            Log.Write("ask", $"Scan of {root} kept; not asked about it now ({(IsOutOfQuestions ? "no questions left" : "a question is running")})");
            return;
        }
        var offered = talk.Messages.OfType<AskAnswer>().FirstOrDefault(m => m.Parts.OfType<AskNext>().Any(n => n.Actions.Contains(a)));
        string question = offered?.Asked is { Length: > 0 } asked ? asked : Lang.T($"What's using the space on {letter}:, and what could I free?");
        string note = $"(RigSight has just scanned the {letter}: drive for this question, and shows the scan's card above. Read the scan with get_drive_scan "
            + $"and answer the question from it. Don't offer to scan {letter}: again.)";
        ContinueAfter(talk, question, note, Lang.T($"Reading the scan of {letter}:…"));
    }

    /// <summary>How long a scan's card shows it scanning at the least, so a quick scan doesn't flash by.</summary>
    internal static TimeSpan MinScanShown { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Open on Storage, on a finished scan's card: the Storage page at that drive's scan.</summary>
    [RelayCommand]
    private void OpenScan(AskAction? a) { if (a?.Scan is { } scan) OpenPageAt?.Invoke(AiPages.Storage, scan.Letter); }

    /// <summary>Stop, on a scan's card.</summary>
    [RelayCommand]
    private void StopScan(AskAction? a) => a?.Scan?.Stop?.Cancel();

    /// <summary>
    /// The question answered again under what is already there (the approved artifact: the first answer and the scan's
    /// card stay, nothing disappears), with no second question bubble, the real step showing at once.
    /// </summary>
    private void ContinueAfter(AskConversation talk, string question, string note, string step)
    {
        talk.When = DateTime.Now;
        var thinking = new AskThinking();
        thinking.Pin(step);
        talk.Messages.Add(thinking);
        Changed();
        ToEnd();
        Log.Write("ask", "Answered again after a scan, under the first answer");
        Answer(talk, question, note);
    }

    /// <summary>Try again on a button that failed: back to ready, and pressed again.</summary>
    [RelayCommand]
    private async Task Retry(AskAction? a)
    {
        if (a is not { IsFailed: true, CanRetry: true }) return;
        a.State = AskActionState.Ready;
        await Act(a);
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
            CloseOutcome.StillOpen => (Lang.T($"{name} is still open. It may be asking you to save, so answer it or close it yourself."), AskActionState.Failed),
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
        a.Said = check.Due <= DateTime.Now ? Lang.T("Comparing…") : Lang.T($"Checks at {AskCheck.Time(check.Due)}");
        a.Pending = check.Due > DateTime.Now;
        a.State = AskActionState.Done;
        talk.Messages.Add(check);
        _ = ReadBeforeAsync(check);
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
            if (!check.BeforeRead) _ = ReadBeforeAsync(check);
            check.Tick();
            if (DateTime.Now >= check.Due) _ = CompareAsync(check);
        }
    }

    private readonly HashSet<AskCheck> _comparing = [];

    /// <summary>The before side's readings, off the window's thread, for the rows shown while the check waits.</summary>
    private async Task ReadBeforeAsync(AskCheck check)
    {
        if (check.BeforeRead) return;
        check.BeforeRead = true;
        string? path = CheckDb;
        try
        {
            var rows = await Task.Run(() =>
            {
                using var db = path is null ? RigsightDb.OpenReader() : RigsightDb.OpenReader(path);
                return db is null ? [] : ActionCheck.Before(db, check.At, check.Minutes, check.What);
            });
            if (check.IsWaiting) check.BeforeRows = rows;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("ask", $"Check again's before side couldn't be read: {ex.Message}"); }
    }

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
            // Its button in the step card: done now, so the tick (it was a clock while waiting).
            foreach (var a in Conversations.SelectMany(c => c.Messages.OfType<AskAnswer>()).SelectMany(m => m.Parts.OfType<AskNext>()).SelectMany(n => n.Actions).Where(a => a.Started == check))
                (a.Said, a.Pending) = (Lang.T($"Checked at {AskCheck.Time(check.Due)}"), false);
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
        if (source.IsApp && source.Page is { } page) OpenPageAt?.Invoke(page, source.Focus);
        else if (source.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            try { Launch(uri.AbsoluteUri); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Log.Write("ask", $"Couldn't open {uri.Host}: {ex.Message}"); }
        }
    }

    /// <summary>"What does this mean?" under a result: asked like a question (it is one, and counts as one).</summary>
    [RelayCommand]
    private void AskAboutCheck(AskCheck? check)
    {
        if (check?.Result is not { HasRows: true } r || ShowFirstQuestion) return;
        string rows = string.Join("; ", r.Rows.Select(x => $"{x.Label} {x.Before} before, {x.After} after ({x.Change})"));
        string question = Lang.T($"I pressed Check again at {AskCheck.Time(check.At)}. Over the {check.Minutes} minutes before and after, RigSight found: {rows}. What does this tell me?");
        // Busy, none left or offline: into the box, not a second answer (board 6, 2026-10-26).
        if (CantAskNow) Draft = question; else Ask(question);
    }
}

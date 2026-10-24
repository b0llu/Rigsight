using System.Collections.ObjectModel;
using System.Windows;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>One record an answer looked at: what the chip says, what was in it (on hover), and the page it opens.</summary>
public sealed record AskLook(string Icon, string Text, string Detail, string? Opens)
{
    public bool CanOpen => Opens is not null;
    /// <summary>The page it came from, for the folded line ("Looked at Crashes and Temperatures").</summary>
    public string Page { get; init; } = Text;
    /// <summary>Where on its page: the days read ("2026-10-16..2026-10-22"), for the page to open at them.</summary>
    public string? Focus { get; init; }
}

/// <summary>A line of an answer: a paragraph, or one point of a list (shown with a bullet), with the pill of what it used.</summary>
public sealed record AskLine(string Text, bool IsPoint, string? Lead = null)
{
    public AskRefs? Refs { get; init; }
}

/// <summary>A table the model wrote (parts, games or web facts side by side): its headings and rows, each as many cells.</summary>
public sealed record AskTable(IReadOnlyList<string> Headings, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int Columns => Headings.Count;
}

/// <summary>
/// The one thing to do next, in a box of its own, with its buttons in it (the approved look of 2026-10-23: the main one
/// solid, the next quieter, a third as a link). With buttons but no step, the box holds only the buttons.
/// </summary>
public sealed record AskNext(string Text, string? Opens, string? OpensLabel)
{
    public IReadOnlyList<AskAction> Actions { get; init; } = [];
    public bool HasText => Text.Length > 0;
    public bool HasActions => Actions.Count > 0;
    // The approved look C of 2026-10-23: a card for a step that changes something on the PC (Close), its title the action
    // and the step under it; a step that only opens something is one line a button each; a step with no button, words.
    public bool IsCard => Actions.Any(a => a.Action.Kind == AiActionKind.CloseApp);
    public bool IsLines => HasActions && !IsCard;
    public bool IsWords => !HasActions;
    public string CardTitle => Main?.Label ?? "";
    /// <summary>
    /// One short line under the title, from what RigSight measured ("Holds 3.0 GB with no window open"): the model's step
    /// ran to three lines and said the action again ("OMG THIS IS EVEN MORE BAD", 2026-10-23).
    /// </summary>
    public string CardText => Main?.Action is { } a
        ? a.Windowless ? Lang.T($"Holds {Units.Megabytes(a.MemMb)} with no window open") : Lang.T($"Holds {Units.Megabytes(a.MemMb)} right now")
        : "";
    /// <summary>The card's own button (Close), on the title's row; the others go in the row under it.</summary>
    public AskAction? Main => Actions.FirstOrDefault(a => a.Action.Kind == AiActionKind.CloseApp);
    public IReadOnlyList<AskAction> Rest => [.. Actions.Where(a => a != Main)];
    public IReadOnlyList<AskAction> MainOnly => Main is { } m ? [m] : [];
    /// <summary>The page button of before the buttons: with buttons, the page is one of them.</summary>
    public bool CanOpen => Opens is not null && !HasActions;
}

/// <summary>Something in a conversation: the person's question, or the helper's answer.</summary>
public abstract record AskMessage;

public sealed record AskQuestion(string Text) : AskMessage
{
    /// <summary>A pasted log or a long story: the bubble shows its first lines and a "Show all".</summary>
    public bool IsLong => Text.Length > 600 || Text.Count(c => c == '\n') > 9;
}

/// <summary>
/// An answer: one plain statement first, then its parts in order (paragraphs and points, RigSight's own numbers drawn
/// from its records, tables, the next step), what it found on the web if it looked, and the records it looked at (which
/// are what was sent).
/// </summary>
public sealed record AskAnswer(string Verdict, IReadOnlyList<object> Parts, string? FromWeb, IReadOnlyList<AskLook> Looked) : AskMessage
{
    /// <summary>What the opening sentence used, as a pill at its end.</summary>
    public AskRefs? VerdictRefs { get; init; }
    public bool HasWeb => FromWeb is not null;
    public bool HasLooked => Looked.Count > 0;
    /// <summary>The model that wrote it, kept with the saved answer.</summary>
    public string? Model { get; init; }
    /// <summary>The model's own text and what it looked at: what is saved, and what the answer is drawn again from.</summary>
    public string Raw { get; init; } = "";
    public IReadOnlyList<AiLookUp> LookUps { get; init; } = [];
    /// <summary>RigSight's numbers as drawn for each marker: saved with the answer, so it shows the same when opened again.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AiBlock>> Drawn { get; init; } = new Dictionary<string, IReadOnlyList<AiBlock>>();
    /// <summary>The conversation's name from the model's "Title:" line (its first answer only), or null.</summary>
    public string? Title { get; init; }
    /// <summary>Questions they might ask next, from the model's "Ask next:" line: shown under the latest answer only.</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];
    /// <summary>A page the person's plan doesn't open that would have helped (the model's "[locked: games]" line), or null.</summary>
    public AiPage? Locked { get; init; }
    public bool HasLocked => Locked is not null;
    public string LockedTitle => Locked is { } p ? Lang.T($"Couldn't read {p.Label} (not in your plan).") : "";
    public string LockedText => Locked is { } p ? Lang.T($"It would add: {p.Description}.") : "";

    /// <summary>The question it answers: what Ask again sends.</summary>
    public string? Asked { get; init; }
    /// <summary>How long it took, from the question going in to the answer being in (none for one opened from History).</summary>
    public TimeSpan? Took { get; init; }
    /// <summary>Helpful or Not helpful (RigSight AI only: the relay keeps it with the question).</summary>
    public AskFeedback Feedback { get; init; } = new(null);
    /// <summary>
    /// The one folded line above the answer ("Looked at Crashes and Temperatures · 17 s"): what was read, in page names,
    /// opened to the list of each record and its dates. Replaces the waiting words once the answer is in.
    /// </summary>
    public string LookedLine
    {
        get
        {
            var names = Looked.Select(l => l.Page).Distinct().ToList();
            string what = names.Count switch
            {
                0 => "",
                1 => names[0],
                2 => Lang.T($"{names[0]} and {names[1]}"),
                _ => Lang.T($"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"),
            };
            string took = Took is { } t ? Lang.T($" · {Math.Max(1, (int)Math.Round(t.TotalSeconds))} s") : "";
            return Lang.T($"Looked at {what}") + took;
        }
    }
}

/// <summary>
/// Helpful or Not helpful under an answer: one press for Helpful; Not helpful asks why (four plain reasons and their own
/// words, both optional) before it's sent. Only RigSight AI's answers have it, since only the relay keeps it.
/// </summary>
public sealed partial class AskFeedback(string? questionId) : ObservableObject
{
    public string? QuestionId { get; } = questionId;
    public bool CanRate => QuestionId is not null;
    /// <summary>What was said: true Helpful, false Not helpful, null nothing yet.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsHelpful), nameof(IsNotHelpful))] private bool? _rated;
    public bool IsHelpful => Rated == true;
    public bool IsNotHelpful => Rated == false;
    /// <summary>The reasons box under Not helpful, open until Send or Skip.</summary>
    [ObservableProperty] private bool _asking;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private string _words = "";
    /// <summary>"Thanks, sent." / "Couldn't send it. Try again in a minute.", in place of the box.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSaid))] private string _said = "";
    public bool HasSaid => Said.Length > 0;
    /// <summary>Copy was just pressed: its icon is a tick for a moment.</summary>
    [ObservableProperty] private bool _copied;

    public static readonly IReadOnlyList<(string Id, string Label)> Reasons =
        [("wrong_numbers", Lang.T("Wrong numbers")), ("didnt_answer", Lang.T("Didn't answer")), ("too_long", Lang.T("Too long")), ("other", Lang.T("Other"))];
    public IReadOnlyList<AskReason> ReasonButtons => _buttons ??= [.. Reasons.Select(r => new AskReason(this, r.Id, r.Label))];
    private IReadOnlyList<AskReason>? _buttons;

    [RelayCommand]
    private async Task Helpful()
    {
        if (QuestionId is null) return;
        (Rated, Asking, Reason, Words) = (true, false, null, "");
        await Send(true);
    }

    [RelayCommand]
    private void NotHelpful()
    {
        if (QuestionId is null) return;
        (Rated, Asking, Said) = (false, true, "");
    }

    [RelayCommand]
    private void Pick(string? reason)
    {
        Reason = Reason == reason ? null : reason;
        foreach (var b in ReasonButtons) b.Refresh();
    }

    [RelayCommand] private async Task SendWhy() { Asking = false; await Send(false); }

    private async Task Send(bool helpful)
    {
        Said = await Relay.FeedbackAsync(QuestionId!, helpful, helpful ? null : Reason, helpful ? null : Words)
            ? Lang.T("Thanks, sent.") : Lang.T("Couldn't send it. Try again in a minute.");
    }
}

/// <summary>One reason button under Not helpful, lit when picked.</summary>
public sealed partial class AskReason(AskFeedback owner, string id, string label) : ObservableObject
{
    public AskFeedback Owner { get; } = owner;
    public string Id { get; } = id;
    public string Label { get; } = label;
    public bool IsPicked => Owner.Reason == Id;
    public void Refresh() => OnPropertyChanged(nameof(IsPicked));
}

/// <summary>
/// The moment a limit or a page gets in the way (the plans mock-up): said in the chat, not as a pop-up, with the one step
/// that fixes it. <paramref name="Plan"/> is the plan that does; null when none would (Complete's day used up).
/// </summary>
public sealed record AskWall(string Title, string Text, string? Plan, string? PlanName, string? Question) : AskMessage
{
    public bool HasPlan => Plan is not null;
    /// <summary>Switching (already paying) rather than buying (on Free).</summary>
    public bool Switch { get; init; }
    public string MainText => Switch ? Lang.T($"Switch to {PlanName} and ask") : Lang.T($"Get {PlanName} and ask");
}

/// <summary>
/// An answer still being written, shown as it streams in. It changes in place (its first line, then each part as it
/// grows), so what is already on screen stays put; the finished <see cref="AskAnswer"/> takes its place, drawn the same.
/// </summary>
public sealed record AskWriting : AskMessage, System.ComponentModel.INotifyPropertyChanged
{
    private string _verdict = "";
    public string Verdict { get => _verdict; private set { if (_verdict == value) return; _verdict = value; PropertyChanged?.Invoke(this, new(nameof(Verdict))); } }
    public System.Collections.ObjectModel.ObservableCollection<object> Parts { get; } = [];
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Takes on the parts of the answer so far, replacing only those that changed.</summary>
    public void Show(AskAnswer now)
    {
        Verdict = now.Verdict;
        for (int i = 0; i < now.Parts.Count; i++)
        {
            if (i >= Parts.Count) Parts.Add(now.Parts[i]);
            // A part of another kind in this place (the Next step moved after a card as the answer finished) is taken out
            // and put in, not swapped: swapped, its view kept the old template a moment with the new part as its data, and
            // the Next step's button asked its command about a card, which threw ("Something went wrong", 2026-10-24).
            else if (Parts[i].GetType() != now.Parts[i].GetType()) { Parts.RemoveAt(i); Parts.Insert(i, now.Parts[i]); }
            else if (!Equals(Parts[i], now.Parts[i])) Parts[i] = now.Parts[i];
        }
        while (Parts.Count > now.Parts.Count) Parts.RemoveAt(Parts.Count - 1);
    }
}

/// <summary>No answer came: Ollama not running, the model gone, and so on, said plainly.</summary>
public sealed record AskProblem(string Text) : AskMessage;

/// <summary>A question to start a new chat with, and the page it reads (its icon and name show above the words).</summary>
public sealed record AskStarter(string Text, string Page)
{
    public string Glyph => PageSwitchesViewModel.Glyph(Page);
    public string PageName => AiPages.Get(Page) is { } page ? Lang.T(page.Name) : "";
}

/// <summary>The person stopped the answer: said quietly, since nothing went wrong.</summary>
public sealed record AskStopped(string Text) : AskMessage;

/// <summary>The helper is still working on an answer.</summary>
public sealed record AskThinking : AskMessage, System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>What it's doing now ("Reviewing your report…"), updated as it goes.</summary>
    public string Step { get; private set; } = Lang.T("Connecting the dots…");
    /// <summary>A fainter second line, only while the model is being loaded.</summary>
    public string? Note { get; private set; }
    public bool HasNote => Note is not null;
    /// <summary>When the stage last changed, for "Still working on your answer…".</summary>
    public DateTime Since { get; private set; } = DateTime.Now;
    public bool Loading { get; private set; }

    /// <summary>When the question went in: the bouncing dots show alone for the first moments.</summary>
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>
    /// Taking a while (past <see cref="DotsFor"/>, or a model being loaded): the dots give way to what it's doing,
    /// pulsing, so the wait makes sense.
    /// </summary>
    public bool Slow { get; private set; }
    public static readonly TimeSpan DotsFor = TimeSpan.FromSeconds(3);

    public void Show(string step, string? note = null, bool loading = false)
    {
        (Step, Note, Loading, Since) = (step, note, loading, DateTime.Now);
        foreach (var name in new[] { nameof(Step), nameof(Note), nameof(HasNote) }) PropertyChanged?.Invoke(this, new(name));
        // A real step ("Reading your crashes for 23 Oct…") shows at once, pulsing: the dots are only for the first moments
        // before anything is being read.
        if (loading || step != Lang.T("Connecting the dots…")) BeSlow();
    }

    /// <summary>Called by the window's timer: the dots step aside once the wait passes <see cref="DotsFor"/>.</summary>
    public void Tick() { if (!Slow && DateTime.Now - Started >= DotsFor) BeSlow(); }

    private void BeSlow()
    {
        if (Slow) return;
        Slow = true;
        PropertyChanged?.Invoke(this, new(nameof(Slow)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A conversation kept in History: its first question as the title (renamed by hand if wanted), when it was, what was
/// said, and whether it's pinned to the top or archived out of the way.
/// </summary>
public sealed partial class AskConversation : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    private string _title = "";
    public required string Title { get => _title; set => SetProperty(ref _title, value); }
    /// <summary>When it was last asked in: what History groups it by, and what "Keep conversations for" counts from.</summary>
    public DateTime When { get; set; } = DateTime.Now;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isArchived;
    /// <summary>What was typed in it and not sent yet, back in the box when it is opened again (this run only).</summary>
    public string Draft { get; set; } = "";
    /// <summary>Its ⋯ menu is open.</summary>
    [ObservableProperty] private bool _isMenuOpen;
    /// <summary>Its title is being typed over (Rename), with what's typed so far.</summary>
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    /// <summary>Loaded from the file but not opened yet: its messages are drawn when it is (RigSight's numbers read again).</summary>
    public IReadOnlyList<SavedMessage>? Unopened { get; set; }
    /// <summary>The crash or change it was opened from with an Explain button ("Looking at: …"), if any.</summary>
    public string? LookingAt { get; init; }
    public ObservableCollection<AskMessage> Messages { get; } = [];
    /// <summary>What was asked and answered, as sent to the model, so a follow-up question keeps its context.</summary>
    public List<AiTurn> Sent { get; } = [];
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>History's conversations under one heading: Today, Yesterday, Earlier.</summary>
public sealed record AskGroup(string Heading, IReadOnlyList<AskConversation> Conversations);

/// <summary>
/// The Ask window over every page: open or closed, stuck to the bottom-right corner (small or big) or popped out,
/// History beside or over the chat, the conversation shown, and the questions left on RigSight AI. Where it sits and
/// how big it is belongs to the window itself (AskWindow); this keeps what it shows.
/// </summary>
public sealed partial class AskViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private AiSettings Ai => _settings.Current.Ai;

    private readonly string? _file;

    /// <param name="file">Where conversations are kept; null keeps them only while the app is open (the tests).</param>
    public AskViewModel(SettingsModel settings, string? file = null)
    {
        _settings = settings;
        _file = file;
        Pages = new PageSwitchesViewModel(settings, PlanPagesNow) { OpenPlans = () => Plans?.Open() };
        _settings.Changed += OnSettingsChanged;
        if (file is not null)
            foreach (var saved in AiConversations.Load(file).OrderByDescending(c => c.When))
                Conversations.Add(FromSaved(saved));
        // A fresh conversation to start in, as when the window is first opened; the old ones wait in History.
        Current = NewConversation();
        Keep();
        Regroup();
    }

    /// <summary>
    /// Settings › Ask RigSight → "Keep conversations for": the ones not asked in for longer go (pinned ones stay, as
    /// pinning is keeping). Run at the start and whenever the choice changes.
    /// </summary>
    public void Keep()
    {
        int days = Ai.KeepConversationDays;
        if (days <= 0) return;
        var before = DateTime.Now.AddDays(-days);
        var old = Conversations.Where(c => !c.IsPinned && c != Current && c.When < before).ToList();
        if (old.Count == 0) return;
        foreach (var c in old) Conversations.Remove(c);
        Log.Write("ask", $"Removed {old.Count} conversation(s) not asked in for over {days} days");
        Changed();
    }

    /// <summary>Settings › Ask RigSight → "Clear all…": every conversation goes, pinned and archived ones too.</summary>
    public void ClearAll()
    {
        int n = Conversations.Count(c => c.Title.Length > 0);
        Conversations.Clear();
        ShowingArchived = false;
        Current = NewConversation();
        Log.Write("ask", $"Cleared all {n} conversation(s)");
        Changed();
    }

    private void OnSettingsChanged()
    {
        if (!IsEnabled) IsOpen = false;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(InTopBar));
        OnPropertyChanged(nameof(InSidebar));
        OnPropertyChanged(nameof(ShowQuestionsLeft));
        OnPropertyChanged(nameof(ShowQuestionsPill));
        OnPropertyChanged(nameof(IsOutOfQuestions));
        Pages.Refresh();
        PagesChanged++;
        OnPropertyChanged(nameof(PagesChanged));
        FirstQuestionChanged();
        Regroup();
    }

    /// <summary>An answer can come from somewhere: Ask and Explain buttons show. Off hides every one of them.</summary>
    /// <remarks>Hidden whatever the setting until the AI preview is on for this PC (<see cref="AiPreview"/>).</remarks>
    public bool IsEnabled => AiPreview.IsOn && Ai.Mode != AiMode.Off;
    public bool InTopBar => IsEnabled && Ai.Place == AskPlace.TopBar;
    public bool InSidebar => IsEnabled && Ai.Place == AskPlace.Sidebar;

    [ObservableProperty] private bool _isOpen;
    /// <summary>Floating anywhere over the window instead of stuck to its bottom-right corner.</summary>
    [ObservableProperty] private bool _isPoppedOut;
    /// <summary>How wide the window is now (the view says): what decides between the column and over the chat.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowHistoryColumn), nameof(ShowHistoryOver), nameof(HistoryPressed))] private double _panelWidth = 600;
    /// <summary>Stuck to the corner at the bigger size.</summary>
    [ObservableProperty] private bool _isBig;
    /// <summary>Narrow: the conversations lie over the chat while this is on.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowHistoryOver))] private bool _isHistoryOpen;
    /// <summary>Wide: the conversations are a column beside the chat, which its button hides and shows.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowHistoryColumn))] private bool _historyColumn = true;

    /// <summary>Wide (stuck or popped out): the conversations get a column of their own beside the chat. Narrower, the
    /// button at the top left opens them over the chat (as on a phone).</summary>
    private bool RoomForColumn => PanelWidth >= ColumnFrom;
    public bool ShowHistoryColumn => RoomForColumn && HistoryColumn;
    public bool ShowHistoryOver => !RoomForColumn && IsHistoryOpen;
    public bool HistoryPressed => RoomForColumn ? HistoryColumn : IsHistoryOpen;
    public const double ColumnFrom = 720;

    partial void OnIsPoppedOutChanged(bool value) { IsHistoryOpen = false; OnPropertyChanged(nameof(HistoryPressed)); }
    partial void OnIsHistoryOpenChanged(bool value) => OnPropertyChanged(nameof(HistoryPressed));
    partial void OnHistoryColumnChanged(bool value) => OnPropertyChanged(nameof(HistoryPressed));

    public ObservableCollection<AskConversation> Conversations { get; } = [];
    /// <summary>History shows the archived conversations instead of the rest.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ArchivedText))] private bool _showingArchived;
    partial void OnShowingArchivedChanged(bool value) => Regroup();
    public int ArchivedCount => Conversations.Count(c => c.IsArchived && c.Title.Length > 0);
    public bool HasArchived => ArchivedCount > 0 || ShowingArchived;
    public string ArchivedText => ShowingArchived ? Lang.T("Back to conversations") : Lang.T("Archived");
    [ObservableProperty] private IReadOnlyList<AskGroup> _groups = [];
    [ObservableProperty] private string _historySearch = "";
    partial void OnHistorySearchChanged(string value) => Regroup();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowChat), nameof(ShowStarters), nameof(LookingAt), nameof(HasLookingAt))] private AskConversation? _current;
    partial void OnCurrentChanged(AskConversation? oldValue, AskConversation? newValue)
    {
        if (oldValue is not null) { oldValue.IsCurrent = false; oldValue.Messages.CollectionChanged -= OnMessagesChanged; oldValue.Draft = Draft; }
        if (newValue is not null) { newValue.IsCurrent = true; newValue.Messages.CollectionChanged += OnMessagesChanged; }
        if (newValue is { Messages.Count: 0 }) RefreshStarters();
        // Each conversation keeps its own unsent words: what was typed in one never carries into another.
        Draft = newValue?.Draft ?? "";
    }
    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowChat));
        OnPropertyChanged(nameof(ShowStarters));
        OnPropertyChanged(nameof(NextQuestions));
        OnPropertyChanged(nameof(HasNextQuestions));
    }

    /// <summary>
    /// What they might ask next, under the conversation's latest answer once it has finished: none while one is on its
    /// way, under an older answer, or after a problem. A quiet note under the answer (web search not working) doesn't hide them.
    /// </summary>
    public IReadOnlyList<string> NextQuestions =>
        !IsAnswering && Current?.Messages.LastOrDefault(m => m is AskAnswer or AskQuestion or AskProblem) is AskAnswer last ? last.Suggestions : [];
    public bool HasNextQuestions => NextQuestions.Count > 0;

    public bool IsEmpty => Current is null || Current.Messages.Count == 0;

    /// <summary>
    /// Raised when the chat should show its end: the person asked something (their question shows), or a saved
    /// conversation finished opening. Answers coming in don't raise it, so they never scroll the chat.
    /// </summary>
    public int ShowEnd { get; private set; }

    private void ToEnd() { ShowEnd++; OnPropertyChanged(nameof(ShowEnd)); }
    public string? LookingAt => Current?.LookingAt is { } at ? Lang.T($"Looking at: {at}") : null;
    public bool HasLookingAt => Current?.LookingAt is not null;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _draft = "";

    /// <summary>
    /// Questions to start a new chat from (approved mock-up 2026-10-24): this PC's own (today's shut-off, this week's
    /// driver resets, the game played most, the fullest drive), each with the page it reads. The general four show
    /// until those are read, in the same cards, so nothing moves.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<AskStarter> _starters =
    [
        new(Lang.T("Why did my PC turn off by itself?"), AiPages.Crashes),
        new(Lang.T("Are my temperatures normal for my parts?"), AiPages.Temperatures),
        new(Lang.T("Which game is hardest on my PC?"), AiPages.Games),
        new(Lang.T("Did anything change before my crashes started?"), AiPages.Timeline),
    ];

    /// <summary>"Ryzen 7 5700X3D · RTX 3080 Ti · 16 GB" under "Ask about this PC".</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPcLine))] private string _pcLine = "";
    public bool HasPcLine => PcLine.Length > 0;

    /// <summary>Reads the starters and the parts line off the window's thread, for a new chat about to show.</summary>
    private void RefreshStarters()
    {
        if (SynchronizationContext.Current is null || ShowFirstQuestion) return;
        var tools = Tools();
        var open = PlanPagesNow();
        var now = DateTime.Now;
        Task.Run(() => (tools.Starters(now, open is null ? null : open.Contains), tools.PcLine()))
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Starters = [.. t.Result.Item1.Select(s => new AskStarter(s.Text, s.Page))];
                PcLine = t.Result.Item2;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── Questions left (RigSight AI only) ────────────────────────────────
    /// <summary>Every PC gets this many free questions to try RigSight AI before buying any.</summary>
    public const int FreeQuestions = 10;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuestionsLeftText), nameof(IsLow), nameof(IsOutOfQuestions), nameof(ShowQuestionsPill), nameof(IsLastQuestion), nameof(OutOffer), nameof(OutText))] private int _questionsLeft = FreeQuestions;
    /// <summary>A bought licence is on this PC; without one, the free questions are counted.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuestionsLeftText))] private bool _hasLicence;
    /// <summary>RigSight AI: the pill in the window's top bar says how many questions are left (free ones until a pack is bought).</summary>
    public bool ShowQuestionsLeft => Ai.Mode == AiMode.RigSight;
    /// <summary>
    /// The pill shows only what the relay said: before that (a first visit, or the relay not asked yet since start-up) a
    /// guessed "10 free questions left" was shown to a PC that had used all 10 (found 2026-10-23).
    /// </summary>
    // Quiet while plenty are left (the approved idea of 2026-10-23: like ChatGPT and Claude, no counter in the chat until it
    // matters): it shows from 3 left; the exact number is always in Settings and on the Plans screen.
    public bool ShowQuestionsPill => ShowQuestionsLeft && Standing is not null && (QuestionsLeft <= 3 || LicenceOff);
    /// <summary>One left: said over the box before it's used, so it isn't lost to a typo.</summary>
    public bool IsLastQuestion => ShowQuestionsLeft && Standing is not null && QuestionsLeft == 1 && !IsAnswering && !LicenceOff;
    public string LastQuestionTitle => Standing?.Plan is { Weekly: false }
        ? Lang.T("This is your last question today.") : Lang.T("This is your last free question this week.");
    public string LastQuestionText => Standing?.Plan is { Weekly: true, ResetsAt: { } at } ? Lang.T($"They're back on {TimeText.At(at, "dddd")}.")
        : Standing?.Plan is { Weekly: false } ? Lang.T("More at midnight.") : "";
    public string QuestionsLeftText => LicenceOff ? Lang.T("Licence turned off") : Standing?.Plan is { } plan
        ? plan is { IsFree: true, Cap: 0 } ? Lang.T("Free questions paused")
        : plan.Weekly ? Lang.T($"{plan.Left} of {plan.Cap} left this week") : Lang.T($"{plan.Left} of {plan.Cap} left today")
        : (HasLicence, QuestionsLeft) switch
    {
        (true, 1) => Lang.T("1 question left"),
        (true, _) => Lang.T($"{QuestionsLeft} questions left"),
        (false, 1) => Lang.T("1 free question left"),
        (false, _) => Lang.T($"{QuestionsLeft} free questions left"),
    };
    public bool IsLow => Standing?.Plan is { } plan ? plan.Left <= Math.Max(2, plan.Cap / 10) : QuestionsLeft <= 5;
    public bool IsOutOfQuestions => ShowQuestionsLeft && QuestionsLeft <= 0;

    /// <summary>The PC's licence turned off by support: nothing can be asked, free questions included.</summary>
    public bool LicenceOff => Standing is { HasLicence: true, LicenceStatus: "disabled" };

    /// <summary>Why nothing can be asked, in the box over the question field: turned off, refunded, or all used.</summary>
    public string OutTitle => Standing?.LicenceStatus switch
    {
        _ when LicenceOff => Lang.T("Your licence is turned off."),
        "refunded" when Standing.HasLicence => Lang.T("Your licence was refunded."),
        // Said as a state, not "that was your last": it also shows on opening, before anything was asked.
        _ when Standing?.Plan is { Weekly: true } => Lang.T("No free questions left this week."),
        _ when Standing?.Plan is not null => Lang.T("No questions left today."),
        _ => Lang.T("You've used all your questions."),
    };
    /// <summary>The out box's main button: the plan that fixes it (its card, with the question typed so far), or null.</summary>
    public AskWall? OutOffer => !IsOutOfQuestions || LicenceOff || Standing?.Plan is not { } p ? null
        : LimitWall(new AiLimit(p.Weekly ? "week" : "day", p.Id, p.ResetsAt), Draft) is { HasPlan: true } wall ? wall : null;

    public string OutText => LicenceOff
        ? Lang.T("Contact support from Settings › Ask RigSight.")
        // The plans' own numbers (it said "up to 500" beside a "Get Essentials" button that gives 100, 2026-10-24).
        : Standing?.Plan is { Weekly: true, ResetsAt: { } at } && OutOffer?.Plan is { } offered && Standing.Plans?.FirstOrDefault(o => o.Id == offered) is { } plan
            ? Lang.T($"They're back on {TimeText.At(at, "dddd")}. {plan.Name} gives up to {plan.Cap} questions a day.")
        : Standing?.Plan is { Weekly: true, ResetsAt: { } at2 } ? Lang.T($"They're back on {TimeText.At(at2, "dddd")}.")
        : Standing?.Plan is { } now && Bigger(now) is { } bigger ? Lang.T($"More at midnight. {bigger.Name} gives up to {bigger.Cap} a day.")
        : Standing?.Plan is not null ? Lang.T("More at midnight.")
        : Lang.T("Buy more in Settings › Ask RigSight, or switch to your API key there.");

    // ── Before the first question (each way of answering once) ───────────
    /// <summary>
    /// The first time someone asks with a way of answering, the window says what is sent and where before anything goes,
    /// with the same switches as Settings › Ask RigSight (approved mock-up 2026-10-21). Start asking closes it for that way.
    /// </summary>
    public bool ShowFirstQuestion => IsEnabled && !Ai.FirstQuestionSeen.Contains(Ai.Mode);
    public bool ShowChat => !IsEmpty && !ShowFirstQuestion;
    public bool ShowStarters => IsEmpty && !ShowFirstQuestion;

    /// <summary>An Explain pressed before Start asking: it goes once they have read it.</summary>
    private Action? _afterFirst;

    [RelayCommand]
    private void StartAsking()
    {
        var mode = Ai.Mode;
        _settings.Update(s => { if (!s.Ai.FirstQuestionSeen.Contains(mode)) s.Ai.FirstQuestionSeen = [.. s.Ai.FirstQuestionSeen, mode]; });
        Log.Write("ask", $"First question window read for {mode}");
        FirstQuestionChanged();
        var then = _afterFirst;
        _afterFirst = null;
        then?.Invoke();
        if (mode == AiMode.RigSight) _ = RefreshQuestionsAsync();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value) _ = RefreshQuestionsAsync();
    }

    private void FirstQuestionChanged()
    {
        foreach (var name in new[] { nameof(ShowFirstQuestion), nameof(ShowChat), nameof(ShowStarters), nameof(FirstIntro), nameof(FirstWhere),
                     nameof(FirstWhereText), nameof(FirstMore), nameof(FirstMoreText), nameof(FirstNever), nameof(FirstExtra), nameof(FirstTone), nameof(FirstKept), nameof(FirstExample) })
            OnPropertyChanged(name);
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>About what a question costs on the picked model at the service's prices ("0.3¢"), when it publishes them; set by the shell.</summary>
    public Func<string?>? PerQuestion { get; set; }

    private string Service => Ai.Service.Length > 0 ? Ai.Service : Lang.T("the service");

    public string FirstIntro => Ai.Mode switch
    {
        AiMode.OwnKey => Lang.T("With your API key, answers still come from your PC's records: RigSight sends your question and the records it needs to the service you picked."),
        AiMode.Ollama when OllamaHere => Lang.T("Ollama answers from your PC's records, right here on this PC."),
        AiMode.Ollama => Lang.T("Ollama answers from your PC's records. To do that, RigSight sends your question and the records it needs to the computer Ollama runs on."),
        _ => Lang.T("RigSight AI answers from your PC's records. To do that, RigSight sends your question and the records it needs."),
    };

    /// <summary>Where it goes, in bold, then what that means.</summary>
    public string FirstWhere => Ai.Mode switch
    {
        AiMode.OwnKey => Lang.T($"Goes to {Service}."),
        AiMode.Ollama when OllamaHere => Lang.T("Stays on this PC."),
        AiMode.Ollama => Lang.T($"Goes to the Ollama at {Ollama.Host(Ai.OllamaAddress)}."),
        _ => Lang.T("Goes to RigSight AI."),
    };
    public string FirstWhereText => Ai.Mode switch
    {
        AiMode.OwnKey => Ai.Model.Length > 0
            ? Lang.T($"Your question and these records go to {Ai.Model} with your key. What happens to them there is up to {Service}'s terms.")
            : Lang.T($"Your question and these records go to the model you pick there, with your key. What happens to them there is up to {Service}'s terms."),
        AiMode.Ollama when OllamaHere => Lang.T("Ollama runs on this PC, so your question and records never go over the internet."),
        AiMode.Ollama => Lang.T("That's another computer, so your question and these records leave this PC to get there. They don't go to RigSight."),
        _ => Lang.T("Your question and these records go through RigSight's server to the AI model."),
    };

    /// <summary>The second note: web search, what it costs, or the first answer's wait.</summary>
    public string FirstMore => Ai.Mode switch
    {
        AiMode.OwnKey => Lang.T("Paid with your key."),
        AiMode.Ollama => Lang.T("The first answer can be slow"),
        _ => Lang.T("It can also search the web"),
    };
    public string FirstMoreText => Ai.Mode switch
    {
        AiMode.OwnKey => PerQuestion?.Invoke() is { } each
            ? Lang.T($"About {each} a question at {Service}'s prices, charged to your {Service} account.")
            : Lang.T($"Each question is charged to your {Service} account at its prices."),
        AiMode.Ollama => Ai.OllamaModel.Length > 0
            ? Lang.T($"while Ollama loads {Ai.OllamaModel}. After that it's quicker.")
            : Lang.T("while Ollama loads the model. After that it's quicker."),
        _ => Lang.T("for your parts, drivers and games. A search never includes your name or files."),
    };

    public string? FirstNever => Ai.Mode switch
    {
        AiMode.OwnKey => Lang.T("Never sent: your Windows user name, or the names or contents of your files. Folder names and sizes go only for a drive you press Scan for."),
        AiMode.Ollama when OllamaHere => null,
        AiMode.Ollama => Lang.T("Never sent: your Windows user name, or the names or contents of your files. Folder names and sizes go only for a drive you press Scan for."),
        _ => Lang.T("Never sent: your Windows user name, the names or contents of your files, your licence key. Folder names and sizes go only for a drive you press Scan for."),
    };
    /// <summary>
    /// The real count, only once the relay has said it (before Start asking it isn't asked: no usage tracking), never a guess:
    /// "10 free questions this week", or when they're back.
    /// </summary>
    public string? FirstExtra => Ai.Mode != AiMode.RigSight || Standing?.Plan is not { } plan ? null
        : plan.Left <= 0 && plan.Weekly && plan.ResetsAt is { } at ? Lang.T($"Your free questions are back on {TimeText.At(at, "dddd")}.")
        : plan.IsFree ? Lang.T($"{plan.Left} free questions this week") : null;

    /// <summary>
    /// What RigSight AI's answers leave behind, said truly (OpenRouter's logging is on since 2026-10-23, and Not helpful's
    /// words are kept on the relay): replaces "Nothing is kept after the answer", which stopped being true.
    /// </summary>
    public string? FirstKept => Ai.Mode == AiMode.RigSight
        ? Lang.T("OpenRouter, the service that runs the AI model, keeps your question, the answer and the records it read for at least 3 months, and never uses them for training. Words you write under Not helpful are kept with the question on RigSight's server.")
        : null;

    /// <summary>What one question sends, as an example ("See what one question sends").</summary>
    public string? FirstExample => Ai.Mode == AiMode.Ollama && OllamaHere ? null
        : Lang.T("For \"Are my temperatures normal?\": your question, then what RigSight looks up for it, such as your CPU and GPU temperatures for the last 7 days (each hour's highest and average) and your parts' names. Never a file, a folder or your name.");

    [RelayCommand]
    private void OpenPrivacy()
    {
        try { Launch("https://rigsight.app/privacy.html"); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Write("ask", $"Couldn't open the privacy page: {ex.Message}"); }
    }

    /// <summary>The dot's colour: blue to RigSight AI, amber out to another service or computer, green staying on the PC.</summary>
    public string FirstTone => Ai.Mode switch { AiMode.OwnKey => "warm", AiMode.Ollama => OllamaHere ? "good" : "warm", _ => "accent" };

    /// <summary>Ollama's address is this PC: then, and only then, nothing leaves it.</summary>
    private bool OllamaHere => Ollama.IsOnThisPc(Ai.OllamaAddress);

    // The same page tiles as Settings › AI › What answers can read, for the first-question window.
    public PageSwitchesViewModel Pages { get; }

    /// <summary>Changes whenever a page is switched (here or in Settings), so the other place shows it too.</summary>
    public int PagesChanged { get; private set; }

    /// <summary>RigSight AI's plans (set by the shell): the cards at a limit open them, and buy or switch from here.</summary>
    public PlansViewModel? Plans { get; set; }

    /// <summary>Sets the look-ups' hooks into the app (readings now, drives); set by the shell.</summary>
    public Action<AiTools>? ConfigureTools { get; set; }

    /// <summary>The pages answers may use now: the plan's with RigSight AI (Free's until the relay has said), every page otherwise.</summary>
    public IReadOnlyCollection<string>? PlanPagesNow() => Ai.Mode != AiMode.RigSight ? null : Standing?.Plan?.Pages ?? AiPages.DefaultFor(0);

    /// <summary>The look-ups, made once, told the plan's pages before every question.</summary>
    private AiTools Tools()
    {
        if (_tools is null)
        {
            _tools = new AiTools(_settings.Current);
            ConfigureTools?.Invoke(_tools);
        }
        _tools.PlanPages = PlanPagesNow();
        return _tools;
    }

    /// <summary>Opens Settings › Ask RigSight (the questions-left pill, "Buy more"); set by the shell.</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>Opens a page by its key (a "Looked at" chip); set by the shell.</summary>
    public Action<string>? OpenPage { get; set; }

    [RelayCommand] private void OpenLook(AskLook? look) { if (look?.Opens is { } page) OpenPage?.Invoke(page); }

    // Where the window was and how big, kept while RigSight is open (AskWindow reads and writes these).
    public Size StuckSmall { get; set; } = new(420, 600);
    public Size StuckBig { get; set; } = new(600, 800);
    public Rect Floating { get; set; } = Rect.Empty;

    // ── Commands ─────────────────────────────────────────────────────────
    [RelayCommand] private void Toggle() => IsOpen = !IsOpen;
    [RelayCommand] private void Close() => IsOpen = false;
    [RelayCommand] private void ToggleSize() => IsBig = !IsBig;
    [RelayCommand] private void PopOut() => IsPoppedOut = !IsPoppedOut;

    [RelayCommand]
    private void ToggleHistory()
    {
        if (RoomForColumn) HistoryColumn = !HistoryColumn;
        else IsHistoryOpen = !IsHistoryOpen;
    }

    [RelayCommand]
    private void New()
    {
        // An empty conversation is reused instead of piling up blank ones in History.
        // A saved one not opened yet has no messages loaded: it isn't empty (reusing it once sent a question into an old chat).
        Current = Conversations.FirstOrDefault(c => c.Messages.Count == 0 && c.Unopened is null && c.Title.Length == 0) ?? NewConversation();
        // A new conversation starts with an empty box, also when the empty one in use is the one reused.
        Current.Draft = "";
        Draft = "";
        IsHistoryOpen = false;
    }

    /// <summary>The rail's search button: History opens with its search box ready (the window puts the cursor in it).</summary>
    [RelayCommand]
    private void SearchHistory()
    {
        ShowingArchived = false;
        if (RoomForColumn) HistoryColumn = true;
        else IsHistoryOpen = true;
        SearchWanted?.Invoke();
    }

    /// <summary>Set by the window: put the cursor in History's search box.</summary>
    public Action? SearchWanted { get; set; }

    // ── A conversation's ⋯ menu ──────────────────────────────────────────
    [RelayCommand]
    private void OpenMenu(AskConversation? talk)
    {
        foreach (var c in Conversations) c.IsMenuOpen = c == talk;
    }

    [RelayCommand]
    private void TogglePin(AskConversation? talk)
    {
        if (talk is null) return;
        talk.IsMenuOpen = false;
        talk.IsPinned = !talk.IsPinned;
        if (talk.IsPinned) talk.IsArchived = false;
        Changed();
    }

    [RelayCommand]
    private void ToggleArchive(AskConversation? talk)
    {
        if (talk is null) return;
        talk.IsMenuOpen = false;
        talk.IsArchived = !talk.IsArchived;
        if (talk.IsArchived) talk.IsPinned = false;
        if (ArchivedCount == 0) ShowingArchived = false;
        Changed();
    }

    [RelayCommand]
    private void Delete(AskConversation? talk)
    {
        if (talk is null) return;
        talk.IsMenuOpen = false;
        Conversations.Remove(talk);
        if (Current == talk) Current = Conversations.FirstOrDefault(c => c.Messages.Count == 0 && c.Unopened is null) ?? NewConversation();
        if (ArchivedCount == 0) ShowingArchived = false;
        Changed();
    }

    [RelayCommand]
    private void StartRename(AskConversation? talk)
    {
        if (talk is null) return;
        talk.IsMenuOpen = false;
        foreach (var c in Conversations) c.IsRenaming = false;
        talk.RenameText = talk.Title;
        talk.IsRenaming = true;
    }

    /// <summary>Enter, or the box losing the cursor: the typed title is kept (an emptied one goes back to what it was).</summary>
    public void FinishRename(AskConversation talk, bool keep)
    {
        if (!talk.IsRenaming) return;
        talk.IsRenaming = false;
        string typed = talk.RenameText.Trim();
        if (keep && typed.Length > 0 && typed != talk.Title)
        {
            talk.Title = typed;
            Changed();
        }
    }

    [RelayCommand] private void ToggleArchived() => ShowingArchived = !ShowingArchived;

    /// <summary>Something about the conversations changed: History again, and the file.</summary>
    private void Changed()
    {
        Regroup();
        Save();
    }

    [RelayCommand]
    private void Pick(AskConversation? conversation)
    {
        if (conversation is null) return;
        foreach (var c in Conversations) c.IsMenuOpen = false;
        if (conversation.Unopened is not null) Open(conversation);
        Current = conversation;
        IsHistoryOpen = false;
    }

    [RelayCommand] private void Starter(string? text) { if (!string.IsNullOrWhiteSpace(text) && !ShowFirstQuestion) Ask(text); }

    /// <summary>One of the questions under the latest answer, asked as if typed.</summary>
    [RelayCommand] private void AskSuggested(string? text) { if (!string.IsNullOrWhiteSpace(text) && !ShowFirstQuestion && !IsAnswering && !IsOutOfQuestions) Ask(text); }

    // Nothing goes before the first-question window is read: Start asking is the way past it.
    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft) && !IsOutOfQuestions && !IsAnswering && !ShowFirstQuestion;

    /// <summary>An answer is on its way: another question waits for it.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] [NotifyPropertyChangedFor(nameof(NextQuestions), nameof(HasNextQuestions), nameof(IsLastQuestion))] private bool _isAnswering;

    /// <summary>Stops the answer on its way (Stop, in the Send button's place while it works).</summary>
    private CancellationTokenSource? _answering;

    [RelayCommand] private void Stop() => _answering?.Cancel();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Draft.Trim();
        Draft = "";
        Ask(text);
    }

    [RelayCommand] private void Settings() => OpenSettings?.Invoke();

    [RelayCommand] private void ExplainCase(string? what) { if (!string.IsNullOrWhiteSpace(what)) Explain(what); }

    /// <summary>An Explain button: the window opens on a new conversation about that crash or change.</summary>
    public void Explain(string what)
    {
        if (ShowFirstQuestion)
        {
            // Opened on the first-question window: the Explain goes once they press Start asking.
            _afterFirst = () => Explain(what);
            IsHistoryOpen = false;
            IsOpen = true;
            return;
        }
        var talk = new AskConversation { Title = Lang.T($"Explain: {what}"), LookingAt = what };
        Conversations.Insert(0, talk);
        Current = talk;
        talk.Messages.Add(new AskThinking());
        IsHistoryOpen = false;
        IsOpen = true;
        Changed();
        // What it explains is a crash or shut-off: with those not shared, it isn't sent, and the window says where to turn them on.
        if (Ai.Mode == AiMode.RigSight && PlanPagesNow() is { } open && !open.Contains(AiPages.Crashes))
        {
            talk.Messages.Clear();
            talk.Messages.Add(LockedWall(AiPages.Get(AiPages.Crashes)!, $"Explain this from my PC's records: {what}"));
            return;
        }
        if (!Ai.Sends(AiPages.Crashes))
        {
            talk.Messages.Clear();
            talk.Messages.Add(new AskProblem(Lang.T("Explain needs the Crashes page switched on in Settings › AI.")));
            return;
        }
        Answer(talk, $"Explain this from my PC's records: {what}");
    }

    /// <summary>The question goes in, and the helper starts on it.</summary>
    private void Ask(string text)
    {
        var talk = Current ?? NewConversation();
        if (talk.Messages.Count == 0 && talk.Title.Length == 0) talk.Title = text;
        talk.When = DateTime.Now;
        // The conversation asked in is the newest: it moves to the top of its heading.
        if (Conversations.IndexOf(talk) is > 0 and var at) Conversations.Move(at, 0);
        foreach (var waiting in talk.Messages.OfType<AskThinking>().ToList()) talk.Messages.Remove(waiting);
        talk.Messages.Add(new AskQuestion(text));
        talk.Messages.Add(new AskThinking());
        Changed();
        ToEnd();
        Answer(talk, text);
    }

    /// <summary>The records it may look at, read the way the pages read them.</summary>
    private AiTools? _tools;
    private IReadOnlyList<OllamaModel>? _ollamaModels;
    private string _ollamaModelsFrom = "";

    /// <summary>Gets the answer (RigSight AI through the relay, my own key, or Ollama on my PC) and puts it in place of the "Thinking…" line.</summary>
    private async void Answer(AskConversation talk, string question)
    {
        IsAnswering = true;
        var took = System.Diagnostics.Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();
        _answering = stop;
        // One stage going on far longer than usual (but not the model loading, which says so): say it's still at it,
        // once, rather than rotating lines to fill the time.
        var longWait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        longWait.Tick += (_, _) =>
        {
            talk.Messages.OfType<AskThinking>().LastOrDefault()?.Tick();
            if (talk.Messages.OfType<AskThinking>().LastOrDefault() is { Loading: false } thinking && DateTime.Now - thinking.Since > StillWorkingAfter
                && thinking.Step != StillWorking)
                thinking.Show(StillWorking);
        };
        longWait.Start();
        try
        {
            var reply = Ai.Mode switch
            {
                AiMode.Ollama => await AskOllama(talk, question, stop.Token),
                AiMode.OwnKey => await AskOwnKey(talk, question, stop.Token),
                _ => await AskRelay(talk, question, stop.Token),
            };
            if (reply.Text is { } text)
            {
                talk.Sent.Add(new AiTurn(true, question));
                talk.Sent.Add(new AiTurn(false, text, reply.LookUps));
                // RigSight's own numbers are read from the history, so off the window's thread. The answer as it streamed
                // stays up meanwhile, and the finished one takes its place in one step: taking it away first left the chat
                // empty for a moment ("it flashes").
                var tools = _tools!;
                var buttons = Resolver(tools, reply.LookUps);
                var answer = await Task.Run(() => ToAnswer(text, reply.LookUps, marker => tools.Blocks(marker, reply.LookUps), actions: buttons)) with
                {
                    Model = reply.Model, Asked = question, Took = took.Elapsed,
                    Feedback = new AskFeedback(Ai.Mode == AiMode.RigSight ? _questionId : null),
                };
                int at = talk.Messages.IndexOf(talk.Messages.FirstOrDefault(m => m is AskWriting)!);
                if (at >= 0) talk.Messages[at] = answer;
                // Named by its first answer (leftover #10): only while it still has the question as its name, so a later
                // answer, or a name the person typed, never changes it.
                if (answer.Title is { } named && talk.Messages.OfType<AskQuestion>().Count() == 1 && talk.Title == talk.Messages.OfType<AskQuestion>().First().Text)
                {
                    talk.Title = named;
                    Log.Write("ask", $"Conversation named by its first answer: {named}");
                }
                Unwait(talk);
                if (at < 0) talk.Messages.Add(answer);
                // A quiet line under it when something was missing (web search wasn't working).
                if (reply.Note is { } note) talk.Messages.Add(new AskStopped(note));
            }
            else
            {
                Unwait(talk);
                talk.Messages.Add(reply.Limit is { } limit ? LimitWall(limit, question) : new AskProblem(reply.Problem ?? Lang.T("No answer came back. Ask again.")));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The question stays where it was; it isn't sent again with the next one, since it got no answer.
            // Stopped: whatever was written goes, and only the question stays (the person's word, 2026-10-23). It isn't sent
            // again with the next one, since it got no answer. RigSight AI gives the question back, whenever Stop was pressed.
            Log.Write("ask", "Stopped before the answer finished: the answer is hidden, the question stays");
            Unwait(talk);
            if (Ai.Mode == AiMode.RigSight && _questionId is { } id) _ = GiveBackAsync(id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("ask", ex);
            Unwait(talk);
            talk.Messages.Add(new AskProblem(Lang.T("Something went wrong while answering. Ask again.")));
        }
        finally { IsAnswering = false; _answering = null; _questionId = null; longWait.Stop(); Save(); }
    }

    /// <summary>Copy under an answer: its words as plain text, without the markers or RigSight's cards.</summary>
    [RelayCommand]
    private void CopyAnswer(AskAnswer? answer)
    {
        if (answer is null) return;
        var text = new System.Text.StringBuilder(Controls.Md.Plain(answer.Verdict));
        foreach (var part in answer.Parts)
        {
            string? line = part switch
            {
                AskLine l => (l.IsPoint ? "• " : "") + (l.Lead ?? "") + Controls.Md.Plain(l.Text),
                AskNext n when n.HasText => Lang.T($"Next step: {n.Text}"),
                AskTable t => string.Join("\n", new[] { string.Join(" | ", t.Headings.Select(Controls.Md.Plain)) }.Concat(t.Rows.Select(r => string.Join(" | ", r.Select(Controls.Md.Plain))))),
                _ => null,
            };
            if (line is { Length: > 0 }) text.Append("\n\n").Append(line);
        }
        try
        {
            Clipboard.SetText(text.ToString().Trim());
            Log.Write("ask", "Answer copied");
            answer.Feedback.Copied = true;
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            back.Tick += (_, _) => { back.Stop(); answer.Feedback.Copied = false; };
            back.Start();
        }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Write("ask", $"Couldn't copy the answer: {ex.Message}"); }
    }

    /// <summary>A record in the opened "Looked at" list: its page, at the days it read where the page can show them.</summary>
    [RelayCommand]
    private void OpenLooked(AskLook? look)
    {
        if (look?.Opens is { } page) OpenPageAt?.Invoke(page, look.Focus);
    }

    /// <summary>
    /// Ask again: the same question once more (it counts as one). The latest answer is written again in its own place,
    /// the old one gone, as chat apps do (the person's call, 2026-10-24); an older one is asked at the end, since what
    /// came after it was built on it.
    /// </summary>
    [RelayCommand]
    private void AskAgain(AskAnswer? answer)
    {
        if (answer?.Asked is not { Length: > 0 } q || IsAnswering || IsOutOfQuestions || ShowFirstQuestion) return;
        var talk = Current;
        int at = talk?.Messages.IndexOf(answer) ?? -1;
        bool latest = talk is not null && at >= 0 && !talk.Messages.Skip(at + 1).Any(m => m is AskQuestion or AskAnswer);
        if (!latest) { Ask(q); return; }
        // The old answer and anything under it (a note, a problem) go; the question above stays.
        while (talk!.Messages.Count > at) talk.Messages.RemoveAt(at);
        // The earlier turn isn't sent with the new one: the answer it got is the one being replaced.
        if (talk.Sent.Count >= 2 && talk.Sent[^2].FromUser && talk.Sent[^2].Text == q) talk.Sent.RemoveRange(talk.Sent.Count - 2, 2);
        Log.Write("ask", "Asked again: the latest answer is written again in its place");
        talk.When = DateTime.Now;
        talk.Messages.Add(new AskThinking());
        Changed();
        Answer(talk, q);
    }

    /// <summary>The relay's id for the question being answered (RigSight AI), for Stop to give it back.</summary>
    private string? _questionId;

    private async Task GiveBackAsync(string id)
    {
        if (await Relay.StopAsync(id)) await RefreshQuestionsAsync();
    }

    /// <summary>The next plan up from this one (more questions a day), or null on the biggest.</summary>
    private RelayPlanOffer? Bigger(RelayPlan now)
    {
        var offers = Standing?.Plans ?? [];
        int mine = offers.FirstOrDefault(o => o.Id == now.Id)?.PriceCents ?? int.MaxValue;
        return offers.Where(o => !o.IsFree && o.PriceCents > mine).OrderBy(o => o.PriceCents).FirstOrDefault();
    }

    /// <summary>The card at the moment the free week's or the plan's day runs out, offering the plan that fixes it.</summary>
    internal AskWall LimitWall(AiLimit limit, string question)
    {
        var offers = Standing?.Plans ?? [];
        var paid = offers.Where(o => !o.IsFree).ToList();
        if (limit.Kind == "week")
        {
            string day = limit.ResetsAt is { } at ? TimeText.At(at, "dddd") : Lang.T("Monday");
            var first = paid.FirstOrDefault();
            return new AskWall(Lang.T("That was your last free question this week."),
                first is null ? Lang.T($"They're back on {day}.")
                    : Lang.T($"They're back on {day}. {first.Name} gives up to {first.Cap} a day for {first.Price} a month, and sends this question the moment it's on."),
                first?.Id, first?.Name, question);
        }
        var current = Standing?.Plan;
        int mine = current is null ? int.MaxValue : offers.FirstOrDefault(o => o.Id == current.Id)?.PriceCents ?? int.MaxValue;
        var bigger = paid.FirstOrDefault(o => o.PriceCents > mine);
        return new AskWall(Lang.T($"That's all {current?.Cap ?? 0} questions for today."),
            bigger is null ? Lang.T("More at midnight. Your question stays in the box.")
                : Lang.T($"More at midnight. {bigger.Name} gives up to {bigger.Cap} a day and answers use more pages; switching sends this question straight away."),
            bigger?.Id, bigger?.Name, question) { Switch = current is { IsFree: false, CanManage: true } };
    }

    /// <summary>The card for something only a bigger plan reads (Explain on Free needs Crashes).</summary>
    private AskWall LockedWall(AiPage page, string question)
    {
        var offer = (Standing?.Plans ?? []).FirstOrDefault(o => !o.IsFree && o.Pages.Contains(page.Id));
        string name = offer?.Name ?? Lang.T("a plan");
        return new AskWall(Lang.T($"This needs the {page.Label} page."), Lang.T($"{page.Description}. It comes with {name}."), offer?.Id, offer?.Name, question)
            { Switch = Standing?.Plan is { IsFree: false, CanManage: true } };
    }

    /// <summary>A card's main button: buy (or switch to) its plan, then send the question that hit the limit.</summary>
    [RelayCommand]
    private async Task UseWall(AskWall? wall)
    {
        if (wall?.Plan is not { } plan || Plans is null) return;
        bool on = wall.Switch ? await Plans.SwitchNowAsync(plan) : await Plans.BuyAsync(plan);
        if (!on || Current is not { } talk) return;
        bool inChat = talk.Messages.Contains(wall);
        // The out box's card was made before the question was typed: what's in the box now is the question.
        talk.Messages.Remove(wall);
        string q = wall.Question is { Length: > 0 } asked && inChat ? asked : Draft.Trim();
        if (q.Length > 0 && !IsOutOfQuestions)
        {
            if (Draft.Trim() == q) Draft = "";
            // The question as typed, sent again now the plan is on (it got no answer before).
            if (talk.Messages.LastOrDefault() is AskQuestion last && last.Text == q) talk.Messages.Remove(last);
            Ask(q);
        }
    }

    [RelayCommand] private void SeePlans() => Plans?.Open();

    /// <summary>Takes the "Thinking…" line and any half-written answer away, for the finished answer or a problem.</summary>
    private static void Unwait(AskConversation talk)
    {
        foreach (var waiting in talk.Messages.Where(m => m is AskThinking or AskWriting).ToList()) talk.Messages.Remove(waiting);
    }

    /// <summary>
    /// The words so far, in place of the "Thinking…" line, drawn the way the finished answer will be: RigSight's numbers as
    /// soon as their marker line is complete (each read once), a line still being typed held back while it could turn
    /// into a marker, a table or the next step. At most every 120 ms; null puts "Thinking…" back (the words led into more
    /// look-ups).
    /// </summary>
    private Action<string?, IReadOnlyList<AiLookUp>> Writer(AskConversation talk)
    {
        var last = DateTime.MinValue;
        AskThinking? held = null;
        var drawn = new Dictionary<string, IReadOnlyList<AiBlock>>();
        var tools = _tools;
        return (text, lookUps) =>
        {
            var writing = talk.Messages.OfType<AskWriting>().FirstOrDefault();
            if (text is null)
            {
                if (writing is not null) talk.Messages.Remove(writing);
                if (held is not null && !talk.Messages.Contains(held)) talk.Messages.Add(held);
                return;
            }
            if (writing is not null && DateTime.Now - last < TimeSpan.FromMilliseconds(120)) return;
            last = DateTime.Now;
            var shown = ToAnswer(Settled(text), [], marker =>
            {
                if (!drawn.TryGetValue(marker, out var blocks)) drawn[marker] = blocks = tools?.Blocks(marker, lookUps) ?? [];
                return blocks;
            }, writing: true);
            if (writing is null)
            {
                writing = new AskWriting();
                held = talk.Messages.OfType<AskThinking>().LastOrDefault();
                if (held is not null) talk.Messages.Remove(held);
                talk.Messages.Add(writing);
            }
            writing.Show(shown);
        };
    }

    /// <summary>
    /// The text so far without a last line that is still being typed and could become something else: a marker
    /// ("[show: te"), a table row, or the next step ("Next st"). Shown once it is finished, so it never flashes up as words.
    /// </summary>
    internal static string Settled(string text)
    {
        int cut = text.LastIndexOf('\n');
        string tail = text[(cut + 1)..].TrimStart();
        if (tail.Length == 0) return text;
        // "…normal for that load. [src: w2 cra": a source still being typed waits, so it never flashes up as words.
        text = AiSources.Unfinished().Replace(text, "");
        cut = text.LastIndexOf('\n');
        tail = text[(cut + 1)..].TrimStart();
        if (tail.Length == 0) return text;
        string start = tail.TrimStart('-', '*', ' ').ToLowerInvariant();
        bool becoming = tail.StartsWith('[') || tail.StartsWith('|')
            || (tail.Length < 12 && ("next step:".StartsWith(start, StringComparison.Ordinal) || "ask next:".StartsWith(start, StringComparison.Ordinal)));
        return becoming ? (cut < 0 ? "" : text[..cut]) : text;
    }

    private static readonly TimeSpan StillWorkingAfter = TimeSpan.FromSeconds(40);
    private static string StillWorking => Lang.T("Still working on your answer…");

    private async Task<AiReply> AskOllama(AskConversation talk, string question, CancellationToken ct)
    {
        // Which model, and whether it can look at the records: asked of Ollama once per address.
        if (_ollamaModels is null || _ollamaModelsFrom != Ai.OllamaAddress)
        {
            _ollamaModels = await Ollama.ModelsAsync(Ai.OllamaAddress, ct);
            _ollamaModelsFrom = Ai.OllamaAddress;
        }
        if (_ollamaModels is null)
            return new AiReply(null, Lang.T($"Ollama isn't answering at {Ollama.Normalise(Ai.OllamaAddress)}. Check that it's running, then ask again."), []);
        var model = _ollamaModels.FirstOrDefault(m => m.Name == Ai.OllamaModel) ?? _ollamaModels.FirstOrDefault(m => m.CanLookUp) ?? _ollamaModels.FirstOrDefault();
        if (model is null) return new AiReply(null, Lang.T("Ollama has no models yet. Download one in Ollama, then pick it in Settings › Ask RigSight."), []);
        Tools();
        return await Ollama.AskAsync(Ai.OllamaAddress, model.Name, model.CanLookUp, [.. talk.Sent], question, _tools,
            step => talk.Messages.OfType<AskThinking>().LastOrDefault()?.Show(step.Text, step.Note, step.Loading), ct);
    }

    /// <summary>RigSight AI: the relay counts the question; afterwards the pill shows what's left.</summary>
    private async Task<AiReply> AskRelay(AskConversation talk, string question, CancellationToken ct)
    {
        Tools();
        try
        {
            return await Relay.AskAsync([.. talk.Sent], question, _tools!,
                step => talk.Messages.OfType<AskThinking>().LastOrDefault()?.Show(step.Text, step.Note, step.Loading), Writer(talk), ct, asking: id => _questionId = id);
        }
        finally { _ = RefreshQuestionsAsync(); }
    }

    /// <summary>
    /// Questions left and the licence, from the relay. Only once the person has chosen RigSight AI (pressed Start asking
    /// on its first-question window): then when the Ask window opens, after each question, and in Settings. Never at
    /// start-up on its own, so an install nobody asks from never reaches the relay (no usage tracking, the plan's rule).
    /// <paramref name="theyAsked"/>: a button they pressed themselves (Buy), which may ask the relay before that.
    /// </summary>
    /// <returns>Why it couldn't be refreshed (the relay unreachable, offline), or null when it was or wasn't asked.</returns>
    public async Task<string?> RefreshQuestionsAsync(bool theyAsked = false)
    {
        if (Ai.Mode != AiMode.RigSight) return null;
        if (!theyAsked && !Ai.FirstQuestionSeen.Contains(AiMode.RigSight)) return null;
        var (status, problem) = await Relay.StatusAsync();
        if (status is null) { Log.Write("ask", $"Questions left not refreshed: {problem}"); return problem; }
        Show(status);
        return null;
    }

    /// <summary>What the relay said about this PC, shown in the pill and Settings.</summary>
    public void Show(RelayStatus status)
    {
        Standing = status;
        OnPropertyChanged(nameof(OutOffer));
        HasLicence = status.HasLicence && status.LicenceStatus == "active";
        // A relay with plans counts this week's free ones or today's; an older one, what's left in all.
        QuestionsLeft = status.Plan?.Left ?? status.QuestionsLeft;
        OnPropertyChanged(nameof(QuestionsLeftText));
        OnPropertyChanged(nameof(IsLow));
    }

    /// <summary>The relay's latest word on this PC's questions and licence; null until it answers.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LicenceOff), nameof(OutTitle), nameof(OutText), nameof(QuestionsLeftText), nameof(ShowQuestionsPill), nameof(IsLastQuestion), nameof(LastQuestionTitle), nameof(LastQuestionText), nameof(FirstExtra))] private RelayStatus? _standing;

    private async Task<AiReply> AskOwnKey(AskConversation talk, string question, CancellationToken ct)
    {
        if (KeyStore.Load() is not { Length: > 0 } key)
            return new AiReply(null, Lang.T("Add your API key in Settings › Ask RigSight and press Test."), []);
        if (Ai.Model.Length == 0)
            return new AiReply(null, Lang.T("Pick a model in Settings › Ask RigSight."), []);
        Tools();
        return await OwnKey.AskAsync(Ai.Service, OwnKey.Root(Ai.Service, Ai.CustomAddress), key, Ai.Model, [.. talk.Sent], question, _tools,
            step => talk.Messages.OfType<AskThinking>().LastOrDefault()?.Show(step.Text, step.Note, step.Loading), Writer(talk), ct);
    }

    /// <summary>Past this many characters a first line is a paragraph, not an opening sentence.</summary>
    private const int LongestVerdict = 160;

    private static readonly Regex Point = new(@"^\s*([-*•]|\d+[.)])\s+");
    private static readonly Regex LeadIn = new(@"^(?<lead>[^:.!?|]{3,28}:\s)(?<rest>.+)$");
    private static readonly Regex InlineMarker = new(@"[ \t]*\[\s*show\s*:\s*(?<what>[a-z]+)\s*\][ \t]*", RegexOptions.IgnoreCase);
    private static readonly Regex Marker = new(@"^\[\s*show\s*:\s*(?<what>[a-z]+)\s*\]$", RegexOptions.IgnoreCase);
    // "Next step: …", also as small models write it at times: in brackets, or as a point ("[next step: …]", "- Next step: …").
    private static readonly Regex InlineNextStep = new(@"(?<end>[.!?])[ \t]+(?:\*\*)?(?<word>Next step|Siguiente paso|Pr[oó]ximo paso)\s*:(?:\*\*)?", RegexOptions.IgnoreCase);

    /// <summary>A line RigSight acts on rather than shows as words (never the opening paragraph).</summary>
    private static bool IsControl(string words) => NextStep.IsMatch(words) || AskNextLine.IsMatch(words) || TitleLine.IsMatch(words)
        || LockedLine.IsMatch(words) || AiActions.Marker(words) is not null;
    private static readonly Regex NextStep = new(@"^(?:[-*•]\s*)?\[?\s*(next step|siguiente paso|pr[oó]ximo paso)\s*:\s*(?<rest>.+?)\s*\]?$", RegexOptions.IgnoreCase);
    private static readonly Regex TableRule = new(@"^\|?\s*:?-{2,}");
    private static readonly Regex LockedLine = new(@"^\[\s*locked\s*:\s*(?<page>[a-z]+)\s*\]$", RegexOptions.IgnoreCase);
    // "Ask next: Was it the same on Monday? | Which game heats the GPU most?": questions to offer under the answer.
    // "Title: Sudden shut-off at 4:44 PM": the conversation's name, on its first answer only.
    private static readonly Regex TitleLine = new(@"^(?:[-*•]\s*)?\[?\s*(title|t[ií]tulo)\s*:\s*(?<rest>.+?)\s*\]?$", RegexOptions.IgnoreCase);
    private static readonly Regex AskNextLine = new(@"^(?:[-*•]\s*)?\[?\s*(ask next|preguntas? siguientes?|siguientes preguntas)(?:\s*\([^)]*\))?\s*:\s*(?<rest>.+?)\s*\]?$", RegexOptions.IgnoreCase);
    /// <summary>At most this many questions under an answer, each at most this long (a longer one is a sentence, not a question to tap).</summary>
    private const int MostSuggestions = 3, LongestSuggestion = 90;
    // *Risk of Rain 2*: the stars go, the words stay (a "* " point keeps its star).
    // "Tell me the app's name and I'll look": the model asking back, not a step to take.
    // ...or "Next step: none." where it has none to give.
    private static readonly Regex AsksBack = new(@"^(tell me|ask me|let me know|dime|pregúntame)\b|^(none|n/?a|nothing|no next step|ninguno|nada)\W*$", RegexOptions.IgnoreCase);
    private static readonly Regex Italic = new(@"(?<![*\w])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![*\w])");

    /// <summary>
    /// The model's text as an answer: its first line as the plain statement, then paragraphs and points, RigSight's own
    /// numbers where a marker asks for them (<paramref name="blocks"/> reads them; a marker for a record it didn't look at
    /// shows nothing), its tables, the next step in a box, and the chips.
    /// </summary>
    /// <param name="actions">Works out the buttons from the answer's "[do: …]" lines; null (an answer opened again, or still
    /// being written) shows none: a button is for the moment the answer was given, not days later.</param>
    public static AskAnswer ToAnswer(string text, IReadOnlyList<AiLookUp> lookUps, Func<string, IReadOnlyList<AiBlock>>? blocks = null, bool writing = false,
        Func<IReadOnlyList<string>, IReadOnlyList<AiAction>>? actions = null)
    {
        text = AiActions.Inline.Replace(text, m => $"\n[do: {m.Groups["what"].Value}]\n");
        // A marker written inside a sentence ("…the power supply. [show: crashes]", Haiku, 2026-10-22) gets a line of its
        // own, so its card is drawn instead of the code showing as words.
        text = InlineMarker.Replace(text, m => $"\n[show: {m.Groups["what"].Value.ToLowerInvariant()}]\n");
        // "…as a reset. Next step: check whether…": the step written inside a paragraph gets a line of its own, so it's the
        // Next step card, not words in the paragraph (a shut-off answer, 2026-10-23).
        text = InlineNextStep.Replace(text, m => $"{m.Groups["end"].Value}\n{m.Groups["word"].Value}:");
        // Each line keeps its Markdown marks (**bold**, *italic*, `code`, links), drawn by Controls.Md; the parts RigSight
        // checks (tags, Next step, Ask next, Title) are recognised on the words alone, so "**Next step:**" is still the card.
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.TrimStart('#').Trim())
            // The note RigSight adds to earlier answers when it sends them again, copied by a model into its own answer.
            .Where(l => l.Length > 0 && !l.StartsWith("(Records you looked at", StringComparison.OrdinalIgnoreCase)).ToList();
        // "[src: w2 crashes]" off each line, kept as that line's sources; one alone on a line belongs to the line above.
        var refsOf = new List<string?>();
        for (int k = 0; k < lines.Count; k++)
        {
            var words = AiSources.Marker().Matches(lines[k]).Select(m => m.Groups["what"].Value).ToList();
            if (words.Count == 0) { refsOf.Add(null); continue; }
            lines[k] = AiSources.Marker().Replace(lines[k], "").Trim();
            string joined = string.Join(" ", words);
            if (lines[k].Length == 0)
            {
                lines.RemoveAt(k);
                if (refsOf.Count > 0) refsOf[^1] = ((refsOf[^1] ?? "") + " " + joined).Trim();
                k--;
                continue;
            }
            refsOf.Add(joined);
        }
        AskRefs? RefsFor(string? words) => words is null ? null : AiSources.Resolve(words, lookUps, log: !writing) is { Count: > 0 } found ? new AskRefs(found) : null;
        static string W(string line) => Controls.Md.Plain(line).Trim();
        string verdict = lines.Count > 0 && !Marker.IsMatch(W(lines[0])) && !IsControl(W(lines[0])) && !lines[0].StartsWith('|') ? Point.Replace(lines[0], "") : "";
        AskRefs? verdictRefs = null;
        if (verdict.Length > 0) { verdictRefs = RefsFor(refsOf[0]); lines.RemoveAt(0); refsOf.RemoveAt(0); }
        var parts = new List<object>();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drawnBy = new Dictionary<string, IReadOnlyList<AiBlock>>();
        var suggestions = new List<string>();
        string? title = null;
        var markers = new List<string>();
        AiPage? locked = null;
        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i], l = W(raw);
            // "[locked: games]": a page the plan doesn't open would have helped; said once, under the answer.
            if (LockedLine.Match(l) is { Success: true } lockedLine)
            {
                locked ??= AiPages.Get(lockedLine.Groups["page"].Value.ToLowerInvariant());
                continue;
            }
            if (AiActions.Marker(l) is { } marker)
            {
                markers.Add(marker);
                continue;
            }
            if (TitleLine.Match(l) is { Success: true } named)
            {
                string t = named.Groups["rest"].Value.Trim().Trim('"', '“', '”', '\'').TrimEnd('.').Trim();
                if (t.Length is > 1 and <= 60) title ??= t;
                continue;
            }
            if (AskNextLine.Match(l) is { Success: true } ask)
            {
                foreach (var q in ask.Groups["rest"].Value.Split('|').Select(q => q.Trim().Trim('"', '“', '”', '\'').Trim()))
                    if (q.Length is > 3 and <= LongestSuggestion && suggestions.Count < MostSuggestions && !suggestions.Contains(q, StringComparer.OrdinalIgnoreCase))
                        suggestions.Add(q);
                continue;
            }
            if (Marker.Match(l) is { Success: true } m)
            {
                string what = m.Groups["what"].Value.ToLowerInvariant();
                if (shown.Add(what) && blocks is not null)
                {
                    var drawn = blocks(what);
                    if (drawn.Count == 0) Log.Write("ask", $"[show: {what}] drew nothing (not looked at, or nothing to show)");
                    else drawnBy[what] = drawn;
                    parts.AddRange(drawn);
                }
                continue;
            }
            if (raw.StartsWith('|'))
            {
                var rows = new List<string[]>();
                for (; i < lines.Count && lines[i].StartsWith('|'); i++)
                    if (!TableRule.IsMatch(lines[i])) rows.Add([.. lines[i].Trim('|').Split('|').Select(c => c.Trim())]);
                i--;
                if (rows.Count >= 2)
                {
                    int n = Math.Min(rows[0].Length, 4);
                    string[] Fit(string[] r) => [.. Enumerable.Range(0, n).Select(k => k < r.Length ? r[k] : "")];
                    parts.Add(new AskTable(Fit(rows[0]), [.. rows.Skip(1).Take(8).Select(Fit)]));
                }
                else if (rows.Count == 1) parts.Add(new AskLine(string.Join(" · ", rows[0].Where(c => c.Length > 0)), false));
                continue;
            }
            if (NextStep.Match(l) is { Success: true } next)
            {
                string step = next.Groups["rest"].Value;
                // "Next step: want to know more about a game?" is an offer to keep talking, not a step.
                if (step.EndsWith('?') || AsksBack.IsMatch(step)) { Log.Write("ask", $"Dropped a next step that was a question back: {step}"); continue; }
                var (opens, label) = Opens(step);
                parts.Add(new AskNext(char.ToUpper(step[0], System.Globalization.CultureInfo.CurrentCulture) + step[1..], opens, label));
                continue;
            }
            bool isPoint = Point.IsMatch(raw);
            string body = Point.Replace(raw, "");
            var refs = RefsFor(refsOf[i]);
            parts.Add(LeadIn.Match(body) is { Success: true } lead ? new AskLine(lead.Groups["rest"].Value, isPoint, lead.Groups["lead"].Value) { Refs = refs } : new AskLine(body, isPoint) { Refs = refs });
        }
        // The first line stands out only as an opening sentence with more after it: a short reply alone, or a whole
        // paragraph first, read as all bold ("text feels so bold", 2026-10-21).
        if (verdict.Length > 0 && ((parts.Count == 0 && !writing) || verdict.Length > LongestVerdict))
        {
            parts.Insert(0, new AskLine(verdict, false) { Refs = verdictRefs });
            verdict = "";
            verdictRefs = null;
        }
        // The model named no sources: everything this answer looked at and found, on its last paragraph (never a line of
        // its own under the answer: the approved look, 2026-10-23).
        if (!writing && verdictRefs is null && !parts.OfType<AskLine>().Any(p => p.Refs is not null) && AiSources.All(lookUps) is { Count: > 0 } all)
        {
            int last = parts.FindLastIndex(p => p is AskLine);
            if (last >= 0) parts[last] = ((AskLine)parts[last]) with { Refs = new AskRefs(all) };
            else if (verdict.Length > 0) verdictRefs = new AskRefs(all);
        }
        // The next step closes the answer, whatever order the model wrote it in: its numbers and charts came after it
        // ("why is UI of graph and numbers coming after next steps?", 2026-10-23).
        if (parts.FindIndex(p => p is AskNext) is >= 0 and var stepAt && stepAt < parts.Count - 1)
        {
            var step = parts[stepAt];
            parts.RemoveAt(stepAt);
            parts.Add(step);
        }
        var buttons = actions is not null && markers.Count > 0 ? actions(markers) : [];
        int stepIndex = parts.FindIndex(p => p is AskNext);
        var stepOpens = stepIndex >= 0 ? OpensAt(((AskNext)parts[stepIndex]).Text) : null;
        if (!writing && (buttons.Count > 0 || stepOpens is not null))
        {
            // In the Next step box: the step, then its buttons; the page the step names is one of them, "[do: …]" or not.
            int at = stepIndex;
            var next = at >= 0 ? (AskNext)parts[at] : new AskNext("", null, null);
            var list = buttons.ToList();
            if (stepOpens is { } o && list.Count < AiActions.Most && !list.Any(b => b.Kind == AiActionKind.OpenPage && b.Target == o.Page))
                list.Add(new AiAction(AiActionKind.OpenPage, o.Label, o.Page == "processes" ? "\uE9D9" : "\uE8A7", o.Page, o.Focus));
            // Close first: it's the card's own button ("Close it"); the rest after it.
            list = [.. list.OrderBy(b => b.Kind == AiActionKind.CloseApp ? 0 : 1)];
            bool card = list.Any(b => b.Kind == AiActionKind.CloseApp);
            next = next with { Actions = [.. list.Select((b, k) => new AskAction(b)
            {
                Role = k == 0 ? AskActionRole.Main : k == 1 ? AskActionRole.Second : AskActionRole.Link,
                // As a line, the first one says what it's for (the step), so the step isn't said twice.
                Why = !card && k == 0 && next.Text.Length > 0 ? next.Text : null,
                InCard = card,
            })] };
            if (at >= 0) parts[at] = next; else parts.Add(next);
        }
        return new AskAnswer(verdict, parts, null, Chips(lookUps)) { Raw = text, LookUps = lookUps, Drawn = drawnBy, Suggestions = suggestions, VerdictRefs = verdictRefs, Locked = writing ? null : locked, Title = title };
    }

    /// <summary>The page the next step points to: where the records the answer leaned on are shown.</summary>
    /// <summary>
    /// The page the next step sends them to, when it names one ("open Crashes and…"): a button for a page the step
    /// doesn't mention read as unrelated ("Open Reports" under "check AMD's spec page", 2026-10-21).
    /// </summary>
    private static (string? Page, string? Label) Opens(string step) => OpensAt(step) is { } at ? (at.Page, at.Label) : (null, null);

    private static readonly (string Page, string Name)[] StepPages =
    [
        ("crashes", "Crashes"), ("timeline", "Timeline"), ("games", "Games"), ("reports", "Reports"), ("processes", "Processes"),
        ("memory", "Memory"), ("storage", "Storage"), ("temperatures", "Temperatures"), ("fans", "Fans"), ("network", "Network"), ("apps", "Apps"),
    ];

    /// <summary>
    /// The page a step names, said as a page ("open Crashes", "the processes page", "Processes sorted by memory"), with what
    /// to show there; a common word alone ("memory is full") isn't a page. A step that named one got no button when only four
    /// pages were known ("open the processes page sorted by memory", 2026-10-23).
    /// </summary>
    private static (string Page, string Label, string? Focus)? OpensAt(string step)
    {
        foreach (var (page, english) in StepPages)
        {
            string name = Lang.T(english);
            bool named = new[] { english, name }.Distinct().Any(n =>
                Regex.IsMatch(step, $@"\b(open|opens|opening)\s+(the\s+)?(RigSight's\s+)?{Regex.Escape(n)}\b|\b{Regex.Escape(n)}\s+page\b", RegexOptions.IgnoreCase)
                || (english is "Crashes" or "Timeline" or "Reports" && Regex.IsMatch(step, $@"\b{Regex.Escape(n)}\b")));
            if (!named) continue;
            if (page == "processes" && Regex.IsMatch(step, @"\b(by|sorted by)\s+(memory|cpu)\b", RegexOptions.IgnoreCase))
            {
                bool cpu = Regex.IsMatch(step, @"\bby\s+cpu\b", RegexOptions.IgnoreCase);
                return (page, cpu ? Lang.T("Open Processes by CPU") : Lang.T("Open Processes by memory"), cpu ? "cpu" : "memory");
            }
            return (page, Lang.T($"Open {name}"), null);
        }
        return null;
    }

    [RelayCommand] private void OpenNext(AskNext? next) { if (next?.Opens is { } page) OpenPage?.Invoke(page); }

    /// <summary>
    /// What the answer looked at, for the folded "Looked at" line over it: each record in product words with its dates
    /// ("Crashes, 16 Oct – 22 Oct"), what was in it, and its page; a web search says what it searched for.
    /// </summary>
    private static List<AskLook> Chips(IReadOnlyList<AiLookUp> lookUps)
    {
        var chips = new List<AskLook>();
        foreach (var l in lookUps)
        {
            AskLook? chip = l.Tool == "search_web"
                ? (string?)l.Args["query"] is { Length: > 0 } query ? new AskLook("", Lang.T($"Searched the web for \"{query}\""), "", null) { Page = Lang.T("the web") } : null
                : AiSources.Record(l) is { } r ? new AskLook("", r.Title, "", r.Page) { Page = r.Site, Focus = r.Focus } : null;
            if (chip is not null && !chips.Any(c => c.Text == chip.Text)) chips.Add(chip);
        }
        return chips;
    }

    // ── Kept between runs ────────────────────────────────────────────────
    private static AskConversation FromSaved(SavedConversation c) => new()
    {
        Id = c.Id, Title = c.Title, When = c.When, LookingAt = c.LookingAt, IsPinned = c.Pinned, IsArchived = c.Archived, Unopened = c.Messages,
    };

    /// <summary>
    /// A saved conversation opened for the first time this run: its questions and answers back in place (answers drawn
    /// again, RigSight's numbers read from the same records over the same periods), and what was said, for follow-ups.
    /// </summary>
    private async void Open(AskConversation talk)
    {
        if (talk.Unopened is not { } saved) return;
        talk.Unopened = null;
        foreach (var pair in saved.Zip(saved.Skip(1)))
            if (pair.First.Kind == SavedMessage.Question && pair.Second.Kind == SavedMessage.Answer)
                talk.Sent.AddRange([new AiTurn(true, pair.First.Text), new AiTurn(false, pair.Second.Text, pair.Second.LookUps?.Select(l => l.ToLookUp()).ToList())]);
        Tools();
        var tools = _tools;
        List<AskMessage> messages;
        try
        {
            messages = await Task.Run(() =>
            {
                var list = new List<AskMessage>();
                foreach (var m in saved)
                {
                    switch (m.Kind)
                    {
                        case SavedMessage.Question: list.Add(new AskQuestion(m.Text)); break;
                        case SavedMessage.Problem: list.Add(new AskProblem(m.Text)); break;
                        case SavedMessage.Stopped: list.Add(new AskStopped(m.Text)); break;
                        case SavedMessage.Done: list.Add(new AskDone(m.Text)); break;
                        case SavedMessage.Check when AskCheck.FromSaved(m.Text) is { } check: list.Add(check); break;
                        case SavedMessage.Answer:
                            var looks = (m.LookUps ?? []).Select(l => l.ToLookUp()).ToList();
                            // The numbers as they were shown then; an answer saved before they were kept is drawn again.
                            Func<string, IReadOnlyList<AiBlock>> draw = m.Drawn is { } kept
                                ? marker => kept.TryGetValue(marker, out var b) ? b : []
                                : marker => tools.Blocks(marker, looks);
                            list.Add(ToAnswer(m.Text, looks, draw, actions: markers => AiActions.Resolve(markers, new AiActionContext(looks, null, tools.Installed, true))
                                .Where(a => a.Kind is not (AiActionKind.CloseApp or AiActionKind.CheckAgain or AiActionKind.StartAgent)).ToList())
                                with { Model = m.Model, Asked = list.OfType<AskQuestion>().LastOrDefault()?.Text });
                            break;
                    }
                }
                return list;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("ask", ex);
            messages = [new AskProblem(Lang.T("This conversation couldn't be opened."))];
        }
        foreach (var m in messages) talk.Messages.Add(m);
        // A check from before: compared now if its time has passed, counted down otherwise.
        var checks = messages.OfType<AskCheck>().ToList();
        if (checks.Count > 0)
        {
            foreach (var check in checks.Where(c => DateTime.Now >= c.Due)) _ = CompareAsync(check);
            if (checks.Any(c => DateTime.Now < c.Due)) Watch();
        }
        if (talk == Current) ToEnd();
    }

    /// <summary>
    /// Writes every conversation with something in it to the file, off the window's thread. Saves can overlap (an answer
    /// ending while a rename is saved): each is numbered as it's taken, and one taken before the last written is skipped,
    /// so an older picture never lands over a newer one (a rename was lost that way).
    /// </summary>
    internal Task Save()
    {
        if (_file is null) return Task.CompletedTask;
        var snapshot = Conversations.Where(c => c.Title.Length > 0).Select(ToSaved).ToList();
        string file = _file;
        long taken = Interlocked.Increment(ref _saveTaken);
        return Task.Run(() =>
        {
            lock (_saveGate)
            {
                if (taken < _saveWritten) return;
                AiConversations.Save(snapshot, file);
                _saveWritten = taken;
            }
        });
    }

    private long _saveTaken, _saveWritten;
    private readonly object _saveGate = new();

    private static SavedConversation ToSaved(AskConversation c) => new(c.Id, c.Title, c.When, c.LookingAt, c.IsPinned, c.IsArchived,
        c.Unopened ?? [.. c.Messages.Select(m => m switch
        {
            AskQuestion q => new SavedMessage(SavedMessage.Question, q.Text),
            AskAnswer a => new SavedMessage(SavedMessage.Answer, a.Raw, a.Model, [.. a.LookUps.Select(SavedLookUp.From)], a.Drawn),
            AskProblem p => new SavedMessage(SavedMessage.Problem, p.Text),
            AskStopped x => new SavedMessage(SavedMessage.Stopped, x.Text),
            AskDone d => new SavedMessage(SavedMessage.Done, d.Text),
            AskCheck k => new SavedMessage(SavedMessage.Check, k.Saved),
            _ => null,
        }).OfType<SavedMessage>()]);

    private AskConversation NewConversation()
    {
        var talk = new AskConversation { Title = "" };
        Conversations.Insert(0, talk);
        Regroup();
        return talk;
    }

    /// <summary>
    /// History's headings: Pinned, then Today, Yesterday, Earlier, newest first (or only Archived, while that's open);
    /// blank conversations and non-matches left out.
    /// </summary>
    private void Regroup()
    {
        var shown = Conversations.Where(c => c.Title.Length > 0 && c.IsArchived == ShowingArchived
            && (HistorySearch.Length == 0 || TextMatch.Has(TextMatch.Words(HistorySearch), c.Title))).ToList();
        var today = DateTime.Today;
        var rest = shown.Where(c => !c.IsPinned).ToList();
        Groups = ShowingArchived
            ? [.. new[] { new AskGroup(Lang.T("ARCHIVED"), shown) }.Where(g => g.Conversations.Count > 0)]
            : [
                .. new[]
                {
                    new AskGroup(Lang.T("PINNED"), [.. shown.Where(c => c.IsPinned)]),
                    new AskGroup(Lang.T("TODAY"), [.. rest.Where(c => c.When.Date == today)]),
                    new AskGroup(Lang.T("YESTERDAY"), [.. rest.Where(c => c.When.Date == today.AddDays(-1))]),
                    new AskGroup(Lang.T("EARLIER"), [.. rest.Where(c => c.When.Date < today.AddDays(-1))]),
                }.Where(g => g.Conversations.Count > 0),
            ];
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(HasArchived));
    }

    public bool HasHistory => Groups.Count > 0;


}

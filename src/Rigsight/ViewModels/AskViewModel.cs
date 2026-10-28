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
    public override string ToString() => Text; // its name for screen readers (and UI automation)
    public bool CanOpen => Opens is not null;
    /// <summary>The page it came from, for the folded line ("Looked at Crashes and Temperatures").</summary>
    public string Page { get; init; } = Text;
    /// <summary>Where on its page: the days read ("2026-10-16..2026-10-22"), for the page to open at them.</summary>
    public string? Focus { get; init; }
}

/// <summary>A line of an answer: a paragraph, or one point of a list (shown with a bullet), with the pill of what it used.</summary>
public sealed record AskLine(string Text, bool IsPoint, string? Lead = null)
{
    public override string ToString() => (Lead ?? "") + Controls.Md.Plain(Text); // its name for screen readers (and UI automation)
    public AskRefs? Refs { get; init; }
}

/// <summary>A table the model wrote (parts, games or web facts side by side): its headings and rows, each as many cells.</summary>
public sealed record AskTable(IReadOnlyList<string> Headings, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public override string ToString() => string.Join(", ", Headings.Select(Controls.Md.Plain)); // its name for screen readers (and UI automation)
    public int Columns => Headings.Count;
}

/// <summary>
/// The one thing to do next, in a box of its own, with its buttons in it (the approved look of 2026-10-23: the main one
/// solid, the next quieter, a third as a link). With buttons but no step, the box holds only the buttons.
/// </summary>
public sealed record AskNext(string Text, string? Opens, string? OpensLabel)
{
    public override string ToString() => HasText ? Text : string.Join(", ", Actions.Select(a => a.Label)); // its name for screen readers (and UI automation)
    public IReadOnlyList<AskAction> Actions { get; init; } = [];
    public bool HasText => Text.Length > 0;
    public bool HasActions => Actions.Count > 0;
    // The approved look C of 2026-10-23: a card for a step that changes something on the PC (Close), its title the action
    // and the step under it; a step that only opens something is one line a button each; a step with no button, words.
    public bool IsCard => Actions.Any(a => a.Action.Kind == AiActionKind.CloseApp);
    /// <summary>A drive scan's own card first (it changes in place), the step's other buttons as lines under it.</summary>
    public AskAction? ScanAction => IsCard ? null : Actions.FirstOrDefault(a => a.Action.Kind == AiActionKind.ScanDrive);
    public bool IsScan => ScanAction is not null;
    /// <summary>Widget, overlay and taskbar cards (board 7), each its own card under the step's other buttons.</summary>
    public IReadOnlyList<AskAction> SetupActions => [.. Actions.Where(a => a.Action.IsSetup)];
    public bool HasSetups => SetupActions.Count > 0;
    public IReadOnlyList<AskAction> LineActions => [.. Actions.Where(a => a != ScanAction && !a.Action.IsSetup)];
    public bool HasLineActions => LineActions.Count > 0;
    public bool IsLines => !IsCard && Actions.Any(a => !a.Action.IsSetup);
    /// <summary>
    /// The step in words, only with no buttons: a widget, overlay or taskbar card is the step itself, and the model's words
    /// beside it told them to do by hand what the card's button does ("Add the widget from the Widgets page", 2026-10-26).
    /// </summary>
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
    public IReadOnlyList<AskAction> Rest => [.. Actions.Where(a => a != Main && !a.Action.IsSetup)];
    public IReadOnlyList<AskAction> MainOnly => Main is { } m ? [m] : [];
    /// <summary>The page button of before the buttons: with buttons, the page is one of them.</summary>
    public bool CanOpen => Opens is not null && !HasActions;
}

/// <summary>Something in a conversation: the person's question, or the helper's answer.</summary>
public abstract record AskMessage;

public sealed record AskQuestion(string Text) : AskMessage
{
    public override string ToString() => Text; // its name for screen readers (and UI automation)
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
    public override string ToString() => Controls.Md.Plain(Verdict); // its name for screen readers (and UI automation)
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
    /// <summary>What this question would get with the page ("See what fills the E drive"), from the model's line; null from older answers.</summary>
    public string? LockedGain { get; init; }
    /// <summary>The plan card's payment, while it's under way.</summary>
    public AskPaying Pay { get; init; } = new();
    /// <summary>The plan that has the page and whether it's a switch (already paying), worked out by the window from the relay's plans.</summary>
    public string? LockedPlanId { get; init; }
    public string? LockedPlanName { get; init; }
    public bool LockedSwitch { get; init; }
    public bool HasLockedPlan => LockedPlanId is not null;
    /// <summary>
    /// The card in place of the old one-line footer (the 2026-10-24 audit, approved artifact): what this question gets as
    /// its title, which plan reads the page, the plan's button and See plans.
    /// </summary>
    public string LockedTitle => Locked is not { } p ? "" : LockedGain is { Length: > 0 } gain ? gain : Lang.T($"Get answers from {p.Label}");
    public string LockedText => Locked is not { } p ? ""
        : LockedPlanName is { } plan ? Lang.T($"{plan} lets me read {p.Label}, which covers {Lower(Lang.T(p.Description))}.")
        : Lang.T($"Your plan doesn't include {p.Label}.");
    public string LockedButton => LockedSwitch ? Lang.T($"Switch to {LockedPlanName} and ask") : Lang.T($"Get {LockedPlanName} and ask");
    internal static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

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

    /// <summary>Skip on the reasons box (board 6, 2026-10-26): it closes and nothing is sent, so nothing shows as said.</summary>
    [RelayCommand] private void Skip() { (Asking, Rated, Reason, Words) = (false, null, null, ""); foreach (var b in ReasonButtons) b.Refresh(); }

    private async Task Send(bool helpful)
    {
        Said = await Relay.FeedbackAsync(QuestionId!, helpful, helpful ? null : Reason, helpful ? null : Words)
            ? Lang.T("Thanks, sent.") : Lang.T("Couldn't send it. Try again in a minute.");
    }
}

/// <summary>One reason button under Not helpful, lit when picked.</summary>
public sealed partial class AskReason(AskFeedback owner, string id, string label) : ObservableObject
{
    public override string ToString() => Label; // its name for screen readers (and UI automation)
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
/// <summary>A plan card's payment under way in the browser: the card says so in place, with the checkout again and Cancel.</summary>
public sealed partial class AskPaying : ObservableObject
{
    [ObservableProperty] private bool _waiting;
}

public sealed record AskWall(string Title, string Text, string? Plan, string? PlanName, string? Question) : AskMessage
{
    public override string ToString() => Title; // its name for screen readers (and UI automation)
    public AskPaying Pay { get; init; } = new();
    public bool HasPlan => Plan is not null;
    /// <summary>Switching (already paying) rather than buying (on Free).</summary>
    public bool Switch { get; init; }
    public string MainText => Switch ? Lang.T($"Switch to {PlanName} and ask") : Lang.T($"Get {PlanName} and ask");
    /// <summary>From an answer's plan card: its question goes again once the plan is on (it isn't a card in the chat).</summary>
    public bool FromAnswer { get; init; }
}

/// <summary>
/// An answer still being written, shown as it streams in. It changes in place (its first line, then each part as it
/// grows), so what is already on screen stays put; the finished <see cref="AskAnswer"/> takes its place, drawn the same.
/// </summary>
public sealed record AskWriting : AskMessage, System.ComponentModel.INotifyPropertyChanged
{
    public override string ToString() => Controls.Md.Plain(Verdict); // its name for screen readers (and UI automation)
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

/// <summary>
/// No answer came: Ollama not running, the model gone, and so on, said plainly. With a way forward where there is one (the
/// approved board 6, 2026-10-26): Try again asks the same question once more in its place, Open Settings goes where the
/// words send them. One opened from History has neither: the moment has passed.
/// </summary>
public sealed record AskProblem(string Text) : AskMessage
{
    public override string ToString() => Text; // its name for screen readers (and UI automation)
    /// <summary>The question it was for, which Try again asks again; null where trying again can't help.</summary>
    public string? Question { get; init; }
    /// <summary>What went with the question unseen (a drive just scanned), and the button's page, given again with it.</summary>
    public string? Aside { get; init; }
    public string? FromPage { get; init; }
    public bool CanRetry => Question is not null;
    public bool CanOpenSettings { get; init; }
    public bool HasActions => CanRetry || CanOpenSettings;
}

/// <summary>
/// The person pressed Stop: a faint line where the answer was, with Ask again (board 6, 2026-10-26). It replaced the
/// question standing alone, which read as if nothing had happened. Kept as a plain stopped line when saved.
/// </summary>
public sealed record AskStoppedAnswer(string Text, string Question) : AskMessage
{
    public override string ToString() => Text; // its name for screen readers (and UI automation)
    public string? Aside { get; init; }
    public string? FromPage { get; init; }
}

/// <summary>A question to start a new chat with, and the page it reads (its icon and name show above the words).</summary>
public sealed record AskStarter(string Text, string Page)
{
    public override string ToString() => Text; // its name for screen readers (and UI automation)
    public string Glyph => PageSwitchesViewModel.Glyph(Page);
    public string PageName => AiPages.Get(Page) is { } page ? Lang.T(page.Name) : "";
}

/// <summary>The person stopped the answer: said quietly, since nothing went wrong.</summary>
public sealed record AskStopped(string Text) : AskMessage
{
    public override string ToString() => Text; // its name for screen readers (and UI automation)
}

/// <summary>The helper is still working on an answer.</summary>
public sealed record AskThinking : AskMessage, System.ComponentModel.INotifyPropertyChanged
{
    public override string ToString() => Step; // its name for screen readers (and UI automation)
    /// <summary>
    /// Between real steps, a thinking word that changes every few seconds, as Claude Code does (the person's call,
    /// 2026-10-24: "Connecting the dots…" alone read oddly). Only words for thinking, never a claim of something it
    /// isn't doing; what it really reads ("Reading your crashes for 16 Oct – 22 Oct…") always shows instead.
    /// </summary>
    public static readonly string[] Words =
    [
        Lang.Key("Thinking…"), Lang.Key("Pondering…"), Lang.Key("Mulling it over…"), Lang.Key("Piecing it together…"),
        Lang.Key("Weighing it up…"), Lang.Key("Working it out…"), Lang.Key("Sifting through it…"), Lang.Key("Puzzling it out…"),
        Lang.Key("Crunching the numbers…"), Lang.Key("Considering…"), Lang.Key("Noodling on it…"), Lang.Key("Cross-checking…"),
    ];
    public static readonly TimeSpan WordFor = TimeSpan.FromSeconds(2.5);
    private bool _thinking = true;
    private DateTime _wordSince = DateTime.Now;
    private int _word = Random.Shared.Next(Words.Length);

    /// <summary>What it's doing now ("Reviewing your report…"), updated as it goes.</summary>
    public string Step { get; private set; } = Lang.T(Words[0]);
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

    /// <param name="step">What it's doing; empty while it's only thinking (a word from <see cref="Words"/> shows).</param>
    public void Show(string step, string? note = null, bool loading = false)
    {
        // A step set before the question went ("Reading the scan of D:…") stays until a real one takes over: the relay's
        // client starts each question with an empty step, which put a thinking word in its place (2026-10-25, live test).
        if (_pinned && string.IsNullOrEmpty(step)) return;
        _pinned = false;
        _thinking = string.IsNullOrEmpty(step);
        if (_thinking) { step = Lang.T(Words[_word]); _wordSince = DateTime.Now; }
        (Step, Note, Loading, Since) = (step, note, loading, DateTime.Now);
        foreach (var name in new[] { nameof(Step), nameof(Note), nameof(HasNote) }) PropertyChanged?.Invoke(this, new(name));
        // A real step ("Reading your crashes for 23 Oct…") shows at once, pulsing: the dots are only for the first moments
        // before anything is being read.
        if (loading || !_thinking) BeSlow();
    }

    private bool _pinned;

    /// <summary>What it's about to read, shown at once and kept until the first real step arrives.</summary>
    public void Pin(string step)
    {
        Show(step);
        _pinned = true;
    }

    /// <summary>
    /// Called by the window's timer: the dots step aside once the wait passes <see cref="DotsFor"/>; while it's only
    /// thinking, the next word every <see cref="WordFor"/> (never the same twice in a row).
    /// </summary>
    public void Tick()
    {
        if (!Slow && DateTime.Now - Started >= DotsFor) BeSlow();
        if (!_thinking || DateTime.Now - _wordSince < WordFor) return;
        _word = (_word + 1 + Random.Shared.Next(Words.Length - 1)) % Words.Length;
        _wordSince = DateTime.Now;
        Step = Lang.T(Words[_word]);
        PropertyChanged?.Invoke(this, new(nameof(Step)));
    }

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
    public override string ToString() => Title; // its name for screen readers (and UI automation)
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
    /// <summary>The crash or change it was opened from with an Explain button ("Looking at …"), if any.</summary>
    public string? LookingAt { get; init; }
    public ObservableCollection<AskMessage> Messages { get; } = [];
    /// <summary>What was asked and answered, as sent to the model, so a follow-up question keeps its context.</summary>
    public List<AiTurn> Sent { get; } = [];
    /// <summary>The summary of the older part of a long chat (RigSight AI only), sent instead of those messages.</summary>
    public string? Summary { get; set; }
    /// <summary>How many of <see cref="Sent"/>'s first messages the summary stands for.</summary>
    public int SummaryCovers { get; set; }
    /// <summary>A summary being made right now, which a question asked meanwhile waits for.</summary>
    public Task<string?>? Summarising { get; set; }
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// The line in a long chat where its summary starts (the approved mock-up "Long chats and big pastes", 2026-10-26): a
/// spinner while it's made, then "Earlier messages summarised to keep this chat going", which opens to show what RigSight
/// AI reads instead of the messages above it. The messages themselves stay on screen as they were.
/// </summary>
public sealed record AskSummary : AskMessage, System.ComponentModel.INotifyPropertyChanged
{
    public override string ToString() => IsBusy ? Lang.T("Summarising earlier messages…") : Lang.T("Earlier messages summarised to keep this chat going"); // its name for screen readers (and UI automation)
    private bool _busy = true;
    public bool IsBusy { get => _busy; set { _busy = value; Changed(nameof(IsBusy), nameof(IsDone)); } }
    public bool IsDone => !IsBusy;
    private bool _open;
    public bool IsOpen { get => _open; set { _open = value; Changed(nameof(IsOpen)); } }
    private string _text = "";
    public string Text { get => _text; set { _text = value; Changed(nameof(Text), nameof(Points)); } }
    /// <summary>How many messages above it the summary stands for.</summary>
    public int Replaced { get; init; }
    public string Heading => Lang.T($"What RigSight AI now reads instead of the {Replaced} messages above:");
    /// <summary>The summary's points, one a line, without their "- ".</summary>
    public IReadOnlyList<string> Points => [.. Text.Split('\n').Select(l => l.Trim().TrimStart('-', '•', '*').Trim()).Where(l => l.Length > 0).Select(Controls.Md.Plain)];

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n)); }
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

    /// <summary>Which starters were shown and picked (beside the chats); none in the tests that keep no chats.</summary>
    private readonly AiStarterLog? _starterLog;

    /// <summary>Home's lines for today and the day before, which the starters are drawn from (set by the shell).</summary>
    public Func<StarterLines?>? Lines { get; init; }

    /// <param name="file">Where conversations are kept; null keeps them only while the app is open (the tests).</param>
    public AskViewModel(SettingsModel settings, string? file = null)
    {
        _settings = settings;
        _file = file;
        if (file is not null) _starterLog = new AiStarterLog(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? "", "ask-starters.json"));
        Pages = new PageSwitchesViewModel(settings, PlanPagesNow) { OpenPlans = () => Plans?.Open() };
        _settings.Changed += OnSettingsChanged;
        Gate();
        WatchNetwork();
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

    /// <summary>Every AI button on every page follows the AI, its switch in Settings and what's shared, at once (<see cref="AiGate"/>).</summary>
    private void Gate() => AiGate.Update(IsEnabled && Ai.PageButtons, Ai.Sends, q => { if (q.Explain) Explain(q.Text); else AskNew(q.Text, q.Page); });

    private AiMode _answeringMode;

    private void OnSettingsChanged()
    {
        if (!IsEnabled) IsOpen = false;
        // Off, or another way of answering, stops what was under way: an answer being written stops, and a question still
        // waiting (for the plan, or a summary) never goes out (the 2026-10-27 audit: it could still reach the relay after Off).
        if (_answering is { } going && (!IsEnabled || Ai.Mode != _answeringMode))
        {
            Log.Write("ask", $"The answer under way was stopped: the AI is now {(IsEnabled ? Ai.Mode.ToString() : "off")}");
            going.Cancel();
        }
        if (!IsEnabled || Ai.Mode != AiMode.RigSight) Plans?.StopWaiting();
        Gate();
        ShortcutChanged();
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(InTopBar));
        OnPropertyChanged(nameof(InSidebar));
        OnPropertyChanged(nameof(ShowQuestionsLeft));
        OnPropertyChanged(nameof(ShowQuestionsLine));
        OnPropertyChanged(nameof(ShowUnderBox));
        OnPropertyChanged(nameof(ShowSeePlansUnder));
        OnPropertyChanged(nameof(ShowNoteUnder));
        OnPropertyChanged(nameof(IsOutOfQuestions));
        OnPropertyChanged(nameof(ShowOutBox));
        OnPropertyChanged(nameof(ShowOffline));
        SendCommand.NotifyCanExecuteChanged();
        Pages.Refresh();
        PagesChanged++;
        OnPropertyChanged(nameof(PagesChanged));
        FirstQuestionChanged();
        Regroup();
    }

    /// <summary>An answer can come from somewhere: Ask and Explain buttons show. Off hides every one of them.</summary>
    public bool IsEnabled => Ai.Mode != AiMode.Off;
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

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowChat), nameof(ShowStarters), nameof(LookingAt), nameof(HasLookingAt), nameof(ShowOutBox))] private AskConversation? _current;
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
        OnPropertyChanged(nameof(ShowOutBox));
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
    public string? LookingAt => Current?.LookingAt is { } at ? Lang.T($"Looking at {at}") : null;
    public bool HasLookingAt => Current?.LookingAt is not null;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsTooLong), nameof(ShowDraftCount), nameof(DraftCount), nameof(ShowUnderBox), nameof(ShowSeePlansUnder), nameof(ShowNoteUnder))] private string _draft = "";

    /// <summary>
    /// The most a question may hold (2026-10-26, the approved mock-up "Long chats and big pastes"): an Event Viewer entry, a
    /// crash log or a DxDiag report fits whole. It was 4,000, and a longer paste was cut off without a word.
    /// </summary>
    public const int MaxQuestion = 100_000;
    /// <summary>Past the most: said above the box, and Send greyed out, instead of cutting the text off.</summary>
    public bool IsTooLong => Draft.Length > MaxQuestion;
    /// <summary>Near the most (from 80,000): the count under the box, in amber.</summary>
    public bool ShowDraftCount => Draft.Length >= 80_000;
    public string DraftCount => Lang.T($"{Draft.Length:N0} of {MaxQuestion:N0} characters");
    /// <summary>The line under the box: questions left (RigSight AI), or the count near the most.</summary>
    public bool ShowUnderBox => ShowQuestionsLine || ShowDraftCount;
    /// <summary>See plans and when questions come back, on the right of the line, unless the count is there.</summary>
    public bool ShowSeePlansUnder => ShowSeePlans && !ShowDraftCount;
    public bool ShowNoteUnder => QuestionsNote.Length > 0 && !ShowDraftCount;

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
        var lines = Lines?.Invoke();
        var asked = Conversations.SelectMany(c => c.Messages.OfType<AskQuestion>()).Select(q => q.Text).ToList();
        var log = _starterLog;
        // Counted as shown only with the window open: read while it's closed (Home reloading), nobody saw them.
        bool seen = IsOpen;
        Task.Run(() =>
            {
                var starters = tools.Starters(now, open is null ? null : open.Contains, lines, asked, log);
                if (seen) log?.Shown(starters.Select(s => s.Topic), now);
                return (starters, tools.PcLine());
            })
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Starters = [.. t.Result.starters.Select(s => new AskStarter(s.Text, s.Page))];
                PcLine = t.Result.Item2;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Home's lines changed (today's report read again): a new chat on screen shows starters from them.</summary>
    public void LinesChanged() { if (ShowStarters) RefreshStarters(); }

    // ── Questions left (RigSight AI only) ────────────────────────────────
    /// <summary>Every PC gets this many free questions to try RigSight AI before buying any.</summary>
    public const int FreeQuestions = 10;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuestionsLeftText), nameof(IsLow), nameof(IsOutOfQuestions), nameof(ShowOutBox), nameof(IsLastQuestion), nameof(OutOffer), nameof(OutText), nameof(QuestionsLine), nameof(QuestionsNote), nameof(ShowSeePlans), nameof(ShowSeePlansUnder), nameof(ShowNoteUnder))] private int _questionsLeft = FreeQuestions;
    partial void OnQuestionsLeftChanged(int value) => SendCommand.NotifyCanExecuteChanged();
    /// <summary>A bought licence is on this PC; without one, the free questions are counted.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuestionsLeftText), nameof(QuestionsLine))] private bool _hasLicence;
    /// <summary>RigSight AI: questions are counted, so what's left is said (the line under the question box).</summary>
    public bool ShowQuestionsLeft => Ai.Mode == AiMode.RigSight;

    /// <summary>
    /// The quiet line under the question box, always there with RigSight AI (board 6, 2026-10-26): it replaced the top bar's
    /// pill, which showed only from 3 left, so people found out at the limit. What's left on the left, the reset or See plans on
    /// the right. Only what the relay said: before that it says it's checking, never a guess (a guessed "10 free questions
    /// left" was shown to a PC that had used all 10, 2026-10-23).
    /// </summary>
    public bool ShowQuestionsLine => ShowQuestionsLeft;
    public string QuestionsLine => Standing is null ? Lang.T("Checking your questions…")
        : LicenceOff ? Lang.T("Licence turned off")
        : Standing.Plan is not { } plan ? QuestionsLeftText
        : plan is { IsFree: true, Cap: 0 } ? Lang.T("Free questions paused")
        : plan is { Left: 0, Given: > 0 } ? plan.Given == 1 ? Lang.T("1 given question left") : Lang.T($"{plan.Given} given questions left")
        // The free trial counts days, said first; then today's questions (the approved mock-up, 2026-10-28).
        : plan is { IsTrial: true, EndsAt: { } ends } ? TrialLeft(plan.Name, ends) + " · " + Lang.T($"{plan.Left} of {plan.Cap} left today")
        : (plan.IsFree, plan.Weekly, plan.Given > 0) switch
        {
            (true, true, false) => Lang.T($"{plan.Left} of {plan.Cap} free questions left this week"),
            (true, true, true) => Lang.T($"{plan.Left} of {plan.Cap} free questions left this week + {plan.Given} given"),
            (true, false, false) => Lang.T($"{plan.Left} of {plan.Cap} free questions left today"),
            (true, false, true) => Lang.T($"{plan.Left} of {plan.Cap} free questions left today + {plan.Given} given"),
            (false, true, false) => Lang.T($"{plan.Left} of {plan.Cap} left this week"),
            (false, true, true) => Lang.T($"{plan.Left} of {plan.Cap} left this week + {plan.Given} given"),
            (false, false, false) => Lang.T($"{plan.Left} of {plan.Cap} left today"),
            (false, false, true) => Lang.T($"{plan.Left} of {plan.Cap} left today + {plan.Given} given"),
        };
    /// <summary>"Complete trial · 6 days left", "… ends today at 10:12" on its last day.</summary>
    internal static string TrialLeft(string plan, DateTime ends, DateTime? now = null)
    {
        var left = ends - (now ?? DateTime.Now);
        int days = (int)Math.Ceiling(left.TotalDays);
        return left.TotalHours <= 24 && ends.Date == (now ?? DateTime.Now).Date ? Lang.T($"{plan} trial ends today at {TimeText.At(ends, "HH:mm")}")
            : days <= 1 ? Lang.T($"{plan} trial ends tomorrow at {TimeText.At(ends, "HH:mm")}")
            : Lang.T($"{plan} trial · {days} days left");
    }

    /// <summary>The right side when See plans isn't: when they come back ("Back Monday", "Resets at midnight"); empty when unknown.</summary>
    public string QuestionsNote => ShowSeePlans || LicenceOff || Standing?.Plan is not { } plan || plan is { IsFree: true, Cap: 0 } ? ""
        : plan is { Weekly: true, ResetsAt: { } at } ? Lang.T($"Back {TimeText.At(at, "dddd")}")
        : plan.Weekly ? "" : Lang.T("Resets at midnight");
    /// <summary>Few left and a plan would give more: See plans on the line's right.</summary>
    public bool ShowSeePlans => IsLow && !LicenceOff && Standing?.Plan is { } plan
        && (plan.IsFree ? (Standing.Plans ?? []).Any(o => !o.IsFree) : Bigger(plan) is not null);
    /// <summary>One left: said over the box before it's used, so it isn't lost to a typo.</summary>
    public bool IsLastQuestion => ShowQuestionsLeft && Standing is not null && QuestionsLeft == 1 && !IsAnswering && !LicenceOff;
    public string LastQuestionTitle => Standing?.Plan is { Given: > 0 } ? Lang.T("This is the last of the questions given to you.")
        : Standing?.Plan is { Weekly: false } ? Lang.T("This is your last question today.") : Lang.T("This is your last free question this week.");
    // The last given one: the plan's own come back, never the given ones (the live gift run, 2026-10-25, said "They're back").
    public string LastQuestionText => Standing?.Plan is { Given: > 0, Weekly: true, ResetsAt: { } back } p ? Lang.T($"Your {p.Cap} free ones are back on {TimeText.At(back, "dddd")}.")
        : Standing?.Plan is { Given: > 0, Weekly: false } ? Lang.T("Your plan's are back at midnight.")
        : Standing?.Plan is { Weekly: true, ResetsAt: { } at } ? Lang.T($"They're back on {TimeText.At(at, "dddd")}.")
        : Standing?.Plan is { Weekly: false } ? Lang.T("More at midnight.") : "";
    public string QuestionsLeftText => LicenceOff ? Lang.T("Licence turned off") : Standing?.Plan is { } plan
        ? plan is { IsFree: true, Cap: 0 } ? Lang.T("Free questions paused")
        // The plan's own used up, the given ones going (the pill shows from 3 left in all).
        : plan is { Left: 0, Given: > 0 } ? plan.Given == 1 ? Lang.T("1 given question left") : Lang.T($"{plan.Given} given questions left")
        // Some of the plan's own and some given: both said (it read "1 of 10 left this week" with 2 given, 2026-10-25).
        : plan is { Given: > 0 } ? plan.Weekly ? Lang.T($"{plan.Left} of {plan.Cap} left this week + {plan.Given} given") : Lang.T($"{plan.Left} of {plan.Cap} left today + {plan.Given} given")
        : plan.Weekly ? Lang.T($"{plan.Left} of {plan.Cap} left this week") : Lang.T($"{plan.Left} of {plan.Cap} left today")
        : (HasLicence, QuestionsLeft) switch
    {
        (true, 1) => Lang.T("1 question left"),
        (true, _) => Lang.T($"{QuestionsLeft} questions left"),
        (false, 1) => Lang.T("1 free question left"),
        (false, _) => Lang.T($"{QuestionsLeft} free questions left"),
    };
    public bool IsLow => Standing?.Plan is { } plan ? plan.AllLeft <= Math.Max(2, plan.Cap / 10) : QuestionsLeft <= 5;
    public bool IsOutOfQuestions => ShowQuestionsLeft && QuestionsLeft <= 0;
    /// <summary>
    /// The out box over the question field, except while the chat's own limit card is the last thing in it: both offered
    /// to buy, one above the other (board 6, 2026-10-26).
    /// </summary>
    public bool ShowOutBox => IsOutOfQuestions && Current?.Messages.LastOrDefault() is not AskWall;
    /// <summary>Free questions switched off on the relay for now (cap 0): said as paused, never as a limit reached.</summary>
    private bool FreePaused => Standing?.Plan is { IsFree: true, Cap: 0 };

    /// <summary>The PC's licence turned off by support: nothing can be asked, free questions included.</summary>
    public bool LicenceOff => Standing is { HasLicence: true, LicenceStatus: "disabled" };

    /// <summary>Why nothing can be asked, in the box over the question field: turned off, refunded, or all used.</summary>
    public string OutTitle => Standing?.LicenceStatus switch
    {
        _ when LicenceOff => Lang.T("Your licence is turned off."),
        "refunded" when Standing.HasLicence => Lang.T("Your licence was refunded."),
        _ when FreePaused => Lang.T("Free questions are paused right now."),
        // Said as a state, not "that was your last": it also shows on opening, before anything was asked.
        _ when Standing?.Plan is { Weekly: true } => Lang.T("No free questions left this week."),
        _ when Standing?.Plan is not null => Lang.T("No questions left today."),
        _ => Lang.T("You've used all your questions."),
    };
    /// <summary>The out box's main button: the plan that fixes it (its card, with the question typed so far), or null.</summary>
    public AskWall? OutOffer => !IsOutOfQuestions || LicenceOff || Standing?.Plan is not { } p ? null
        : LimitWall(new AiLimit(FreePaused ? "paused" : p.Weekly ? "week" : "day", p.Id, p.ResetsAt), Draft) is { HasPlan: true } wall ? wall : null;

    public string OutText => LicenceOff
        ? Lang.T("Contact support from Settings › Ask RigSight.")
        : FreePaused ? PausedText
        // The plans' own numbers (it said "up to 500" beside a "Get Essentials" button that gives 100, 2026-10-24).
        : Standing?.Plan is { Weekly: true, ResetsAt: { } at } && OutOffer?.Plan is { } offered && Standing.Plans?.FirstOrDefault(o => o.Id == offered) is { } plan
            ? Lang.T($"They're back on {TimeText.At(at, "dddd")}. {plan.Name} gives up to {plan.Cap} questions a day.")
        : Standing?.Plan is { Weekly: true, ResetsAt: { } at2 } ? Lang.T($"They're back on {TimeText.At(at2, "dddd")}.")
        : Standing?.Plan is { } now && Bigger(now) is { } bigger ? Lang.T($"More at midnight. {bigger.Name} gives up to {bigger.Cap} a day.")
        : Standing?.Plan is not null ? Lang.T("More at midnight.")
        : Lang.T("See plans in Settings › Ask RigSight, or switch to your API key there.");

    private static string PausedText => Lang.T("They'll be back. You can still get a plan.");

    // ── Offline (board 6, 2026-10-26) ────────────────────────────────────
    /// <summary>Windows says there's no network at all (the same check a question makes before it goes, <see cref="Internet.IsOff"/>).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowOffline))] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isOffline;

    /// <summary>The way of answering needs the internet: RigSight AI, an API key, or an Ollama on another computer.</summary>
    private bool NeedsInternet => Ai.Mode switch { AiMode.RigSight or AiMode.OwnKey => true, AiMode.Ollama => !OllamaHere, _ => false };

    /// <summary>
    /// The banner over the question box while offline, and Send off meanwhile: questions sent anyway each came back as a
    /// problem line. It goes as soon as Windows says the network is back.
    /// </summary>
    public bool ShowOffline => IsEnabled && IsOffline && NeedsInternet;

    /// <summary>Windows' word that the network came or went (any thread): checked again on the window's thread.</summary>
    private void WatchNetwork()
    {
        IsOffline = Internet.IsOff();
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) =>
            ui.BeginInvoke(() => NetworkChanged(Internet.IsOff()));
    }

    internal void NetworkChanged(bool off)
    {
        if (off == IsOffline) return;
        IsOffline = off;
        Log.Write("ask", off ? "Offline: questions wait until the network is back" : "Back online");
    }

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
        FocusWanted?.Invoke();
        if (mode == AiMode.RigSight) _ = RefreshQuestionsAsync();
    }

    partial void OnIsOpenChanged(bool value)
    {
        // Not now (or any close) on the first-question window drops the Explain that was waiting: a later Start asking,
        // from the top bar say, must not send it unasked.
        if (!value) { _afterFirst = null; return; }
        FocusWanted?.Invoke();
        _ = RefreshQuestionsAsync();
        // Read again as the window opens: what Home said since, and only now are they counted as seen.
        if (ShowStarters) RefreshStarters();
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
        AiMode.OwnKey => Lang.T("With your API key, answers still come from your PC's records. RigSight sends your question and the records it needs to the service you picked."),
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
        // Free answers from the PC's records only (2026-10-27): said plainly, with the plans that check the web.
        _ when !PlanSearches => Lang.T("It answers from this PC's records."),
        _ => Lang.T("It can also search the web"),
    };

    /// <summary>The plan now checks the web: Free doesn't (the relay's plans say which, 2026-10-27); a trial on offer is its plan's.</summary>
    private bool PlanSearches => Standing is not { Plan: { } now } standing
        || (TrialOffer ?? standing.Plans.FirstOrDefault(p => p.Id == now.Id))?.Search == true
        || (standing.Plans.All(p => p.Id != now.Id) && !now.IsFree);

    /// <summary>The trial's plan while the trial is on offer: the first question starts it, so the consent shows that plan.</summary>
    private RelayPlanOffer? TrialOffer => Standing is { Trial: { Offered: true } t, Plan.IsFree: true } s ? s.Plans.FirstOrDefault(p => p.Id == t.Plan) : null;
    public string FirstMoreText => Ai.Mode switch
    {
        AiMode.OwnKey => PerQuestion?.Invoke() is { } each
            ? Lang.T($"About {each} a question at {Service}'s prices, charged to your {Service} account.")
            : Lang.T($"Each question is charged to your {Service} account at its prices."),
        AiMode.Ollama => Ai.OllamaModel.Length > 0
            ? Lang.T($"while Ollama loads {Ai.OllamaModel}. After that it's quicker.")
            : Lang.T("while Ollama loads the model. After that it's quicker."),
        _ when !PlanSearches => Lang.T("Essentials and Complete also search the web for your parts, drivers and games."),
        _ => Lang.T("for your parts, drivers and games. A search never includes your name or files."),
    };

    // What's never sent, and what is (2026-10-27: it said file names were never sent, while app and program file names go,
    // "RiskOfRain2.exe", "UnityPlayer.dll" in a crash, a game's "UserSettings.json"; your own files' names never do).
    public string? FirstNever => Ai.Mode switch
    {
        AiMode.OwnKey => Lang.T("Your Windows user name and your own files, what they're called and what's in them, are never sent. App and game names are, with their program files' names in a crash record. Folder names and sizes go only for a drive you press Scan for."),
        AiMode.Ollama when OllamaHere => null,
        AiMode.Ollama => Lang.T("Your Windows user name and your own files, what they're called and what's in them, are never sent. App and game names are, with their program files' names in a crash record. Folder names and sizes go only for a drive you press Scan for."),
        _ => Lang.T("Your Windows user name, your licence key and your own files, what they're called and what's in them, are never sent. App and game names are, with their program files' names in a crash record. Folder names and sizes go only for a drive you press Scan for."),
    };
    /// <summary>
    /// The real count, only once the relay has said it (before Start asking it isn't asked: no usage tracking), never a guess:
    /// "10 free questions this week", or when they're back.
    /// </summary>
    public string? FirstExtra => Ai.Mode != AiMode.RigSight || Standing?.Plan is not { } plan ? null
        : TrialOffer is { } offer ? Lang.T($"{offer.Name} free for {Standing.Trial!.Days} days, from your first question")
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

    /// <summary>
    /// Sets the look-ups' hooks into the app (readings now, drives, drive scans); set by the shell. Applied to look-ups
    /// already made too: the new chat's starters make them before the shell sets this, and a drive's scan was then never
    /// read ("hasn't been scanned" right after its own Scan button, 2026-10-24).
    /// </summary>
    public Action<AiTools>? ConfigureTools
    {
        get => _configureTools;
        set { _configureTools = value; if (_tools is not null) value?.Invoke(_tools); }
    }
    private Action<AiTools>? _configureTools;

    /// <summary>The pages answers may use now: the plan's with RigSight AI (Free's until the relay has said), every page otherwise.</summary>
    public IReadOnlyCollection<string>? PlanPagesNow() => Ai.Mode != AiMode.RigSight ? null : TrialOffer?.Pages ?? Standing?.Plan?.Pages ?? AiPages.DefaultFor(0);

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
    // Never opened with AI off, whatever button asked (2026-10-26: a Games button left on screen could open it).
    [RelayCommand] private void Toggle() => IsOpen = !IsOpen && IsEnabled;
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
        FocusWanted?.Invoke();
    }

    /// <summary>
    /// Set by the window: the cursor into the question box, as the window opens and on a new conversation (board 6,
    /// 2026-10-26), so typing starts at once. The window leaves it while the first-question window shows.
    /// </summary>
    public Action? FocusWanted { get; set; }

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
        int at = Conversations.IndexOf(talk);
        bool wasCurrent = Current == talk;
        Conversations.Remove(talk);
        if (wasCurrent) Current = Conversations.FirstOrDefault(c => c.Messages.Count == 0 && c.Unopened is null) ?? NewConversation();
        if (ArchivedCount == 0) ShowingArchived = false;
        Changed();
        // A slip of the menu lost a whole conversation: "Conversation deleted" with Undo for a few seconds (board 6, 2026-10-26).
        _deletedTalk = (talk, at, wasCurrent);
        Deleted = true;
        _undoTimer ??= NewUndoTimer();
        _undoTimer.Stop();
        _undoTimer.Start();
        Log.Write("ask", "Conversation deleted (Undo offered)");
    }

    /// <summary>The line in History after Delete, with Undo, for <see cref="UndoFor"/>.</summary>
    [ObservableProperty] private bool _deleted;
    public static readonly TimeSpan UndoFor = TimeSpan.FromSeconds(6);
    private (AskConversation Talk, int At, bool WasCurrent)? _deletedTalk;
    private System.Windows.Threading.DispatcherTimer? _undoTimer;

    private System.Windows.Threading.DispatcherTimer NewUndoTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = UndoFor };
        timer.Tick += (_, _) => { timer.Stop(); Deleted = false; _deletedTalk = null; };
        return timer;
    }

    /// <summary>Undo: the conversation back where it was in History (and on screen, if it was the one open).</summary>
    [RelayCommand]
    private void UndoDelete()
    {
        _undoTimer?.Stop();
        Deleted = false;
        if (_deletedTalk is not var (talk, at, wasCurrent)) return;
        _deletedTalk = null;
        if (Conversations.Contains(talk)) return;
        Conversations.Insert(Math.Clamp(at, 0, Conversations.Count), talk);
        if (wasCurrent) Current = talk;
        Changed();
        Log.Write("ask", "Conversation deleted, then put back with Undo");
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

    /// <summary>
    /// Esc while a name is being typed over: the old name back, and true so the window stays open (the main window
    /// handles Esc first, and it closed the whole window with the rename, board 6, 2026-10-26).
    /// </summary>
    public bool CancelRename()
    {
        if (Conversations.FirstOrDefault(c => c.IsRenaming) is not { } talk) return false;
        FinishRename(talk, keep: false);
        return true;
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

    [RelayCommand]
    private void Starter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || ShowFirstQuestion) return;
        if (CantAskNow) { Draft = text; return; }
        if (_starterLog is { } log) { var (topic, now) = (AiStarterLog.TopicOf(text), DateTime.Now); Task.Run(() => log.Picked(topic, now)); }
        Ask(text);
    }

    /// <summary>One of the questions under the latest answer, asked as if typed (into the box while it can't go now).</summary>
    [RelayCommand]
    private void AskSuggested(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || ShowFirstQuestion) return;
        if (CantAskNow) Draft = text; else Ask(text);
    }

    // Nothing goes before the first-question window is read: Start asking is the way past it.
    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft) && !IsTooLong && !CantAskNow && !ShowFirstQuestion;

    /// <summary>
    /// A question can't go now: one is being answered, none are left, or the PC is offline. A button that asks (a starter,
    /// Ask next, Ask again, Explain) then puts its question in the box instead of starting a second answer (board 6, 2026-10-26).
    /// </summary>
    private bool CantAskNow => IsAnswering || IsOutOfQuestions || ShowOffline;

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

    /// <summary>An Explain button: the window opens on a new conversation about that crash or change.</summary>
    public void Explain(string what)
    {
        if (!IsEnabled) return;
        if (ShowFirstQuestion)
        {
            // Opened on the first-question window: the Explain goes once they press Start asking.
            _afterFirst = () => Explain(what);
            IsHistoryOpen = false;
            IsOpen = true;
            return;
        }
        // "Explain the PC shut off suddenly…", but "Explain Rocket League crashed…": only a leading article is lowered.
        string said = what.StartsWith("The ", StringComparison.Ordinal) || what.StartsWith("A ", StringComparison.Ordinal) ? AskAnswer.Lower(what) : what;
        // An answer already running, none left or offline: no second answer, the question waits in the box (board 6, 2026-10-26).
        if (CantAskNow)
        {
            IsHistoryOpen = false;
            IsOpen = true;
            Draft = Lang.T($"Explain {said}");
            return;
        }
        var talk = new AskConversation { Title = Lang.T($"Explain {said}"), LookingAt = what };
        Conversations.Insert(0, talk);
        Current = talk;
        talk.Messages.Add(new AskThinking());
        IsHistoryOpen = false;
        IsOpen = true;
        Changed();
        // What it explains is a crash or shut-off: with those not shared, it isn't sent, and the window says where to turn them on.
        // Crashes not in the plan: asked all the same, as a typed question is. The answer says what it could find and that
        // the plan doesn't include Crashes, with the plan's card under it (the user, 2026-10-26: AI buttons on every page).
        if (!Ai.Sends(AiPages.Crashes))
        {
            talk.Messages.Clear();
            talk.Messages.Add(new AskProblem(Lang.T("Explain needs Crashes switched on in Settings › Ask RigSight.")) { CanOpenSettings = true });
            return;
        }
        Answer(talk, $"Explain this from my PC's records: {what}", fromPage: AiPages.Crashes);
    }

    /// <summary>
    /// A page's own question ("How can I make REMATCH run better?" on a game's page): the window opens on a new
    /// conversation asking it. A page the plan doesn't open shows its card; one not shared, where to switch it on; with
    /// no questions left, the question waits in the box above the card that says so.
    /// </summary>
    public void AskNew(string question, string page)
    {
        if (!IsEnabled) return;
        if (ShowFirstQuestion)
        {
            _afterFirst = () => AskNew(question, page);
            IsHistoryOpen = false;
            IsOpen = true;
            return;
        }
        IsHistoryOpen = false;
        IsOpen = true;
        if (CantAskNow) { Draft = question; return; }
        var talk = new AskConversation { Title = question };
        Conversations.Insert(0, talk);
        Current = talk;
        // A page the plan doesn't include is asked all the same, as a typed question is: the answer gives what it could find,
        // says once that the plan doesn't include that page, and the plan's card comes under it (2026-10-26).
        if (!Ai.Sends(page) && AiPages.Get(page) is { } off)
        {
            talk.Messages.Add(new AskProblem(Lang.T($"This needs {off.Label} switched on in Settings › Ask RigSight.")) { CanOpenSettings = true });
            Changed();
            return;
        }
        Ask(question, page);
    }

    /// <summary>The question goes in, and the helper starts on it.</summary>
    private void Ask(string text, string? fromPage = null)
    {
        // Asking closes the card about questions given, as "Got it" does (approved mock-up, 2026-10-25).
        if (Gift is not null) CloseGift();
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
        Answer(talk, text, fromPage: fromPage);
    }

    /// <summary>
    /// A button's page the RigSight AI plan doesn't include (null: it does, or another helper answers). Its question says
    /// so to the AI, unseen in the chat, and its answer gets that page's card even if the AI leaves it out (2026-10-26).
    /// </summary>
    /// <remarks>Only from the plan the relay said, never a guess: an answer waits for it (see <see cref="Answer"/>).</remarks>
    private AiPage? LockedFor(string page) =>
        Ai.Mode == AiMode.RigSight && Standing?.Plan?.Pages is { } open && !open.Contains(page) ? AiPages.Get(page) : null;

    private static string LockedAside(AiPage page) => $"(Asked from RigSight's {page.Label} page, which my plan doesn't include.)";

    /// <summary>The records it may look at, read the way the pages read them.</summary>
    private AiTools? _tools;
    private IReadOnlyList<OllamaModel>? _ollamaModels;
    private string _ollamaModelsFrom = "";

    /// <summary>Gets the answer (RigSight AI through the relay, my own key, or Ollama on my PC) and puts it in place of the "Thinking…" line.</summary>
    /// <param name="aside">Said to the AI with the question but never shown or kept as its words (a drive just scanned for it).</param>
    /// <param name="fromPage">The page of the AI button it came from, which may be one the plan doesn't include.</param>
    private async void Answer(AskConversation talk, string question, string? aside = null, string? fromPage = null)
    {
        IsAnswering = true;
        // What Try again and Ask again give again: the aside as it came (the locked page's note is added again from fromPage).
        string? givenAside = aside;
        var took = System.Diagnostics.Stopwatch.StartNew();
        // Made before the wait for the plan, so Stop works there too: nothing has gone yet (board 6, 2026-10-26).
        using var stop = new CancellationTokenSource();
        _answering = stop;
        _answeringMode = Ai.Mode;
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
            // Never sent before the plan has come (opened and asked straight away): the look-ups it opens and a button's page
            // come from it, and guessing either way is wrong (2026-10-26: guessed Free, a Complete PC was answered as if Reports
            // were locked). The question and the thinking line show meanwhile, saying what it waits for (board 6); two
            // tries, then it says so and sends nothing.
            if (Ai.Mode == AiMode.RigSight && Standing is null)
            {
                talk.Messages.OfType<AskThinking>().LastOrDefault()?.Show(Lang.T("Checking your plan…"), Lang.T("Your question goes as soon as it's known."));
                for (int attempt = 0; attempt < 2 && Standing is null; attempt++)
                {
                    if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                    // Stop doesn't wait for the relay's reply: the plan still lands when it comes, for the next question.
                    await Task.WhenAny(RefreshQuestionsAsync(theyAsked: true), Task.Delay(Timeout.Infinite, stop.Token));
                    stop.Token.ThrowIfCancellationRequested();
                }
                if (Standing is null)
                {
                    Log.Write("ask", $"Question not sent: the plan couldn't be read from the relay ({PlanUnknown})");
                    Unwait(talk);
                    talk.Messages.Add(Problem(Lang.T("Your plan couldn't be checked, so the question wasn't sent. Ask again in a moment."), question, givenAside, fromPage));
                    return;
                }
            }
            // A summary of the chat being made right now: the question waits for it (a few seconds), saying so, as it waits
            // for the plan above; Stop works meanwhile. Not done in time, it goes with the chat as it was.
            if (Ai.Mode == AiMode.RigSight && talk.Summarising is { IsCompleted: false } making)
            {
                talk.Messages.OfType<AskThinking>().LastOrDefault()?.Show(Lang.T("Summarising earlier messages…"), Lang.T("Your question goes as soon as it's done."));
                await Task.WhenAny(making, Task.Delay(TimeSpan.FromSeconds(45), stop.Token));
                stop.Token.ThrowIfCancellationRequested();
                talk.Messages.OfType<AskThinking>().LastOrDefault()?.Show("");
            }
            var lockedFrom = fromPage is null ? null : LockedFor(fromPage);
            if (lockedFrom is not null) aside = aside is null ? LockedAside(lockedFrom) : aside + "\n" + LockedAside(lockedFrom);
            string sent = aside is null ? question : question + "\n\n" + aside;
            var reply = Ai.Mode switch
            {
                AiMode.Ollama => await AskOllama(talk, sent, stop.Token),
                AiMode.OwnKey => await AskOwnKey(talk, sent, stop.Token),
                _ => await AskRelay(talk, sent, stop.Token),
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
                if (answer.Locked is null && lockedFrom is not null) answer = answer with { Locked = lockedFrom };
                answer = WithLockedPlan(answer);
                PrepareSetups(answer);
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
                if (Ai.Mode == AiMode.RigSight) Summarise(talk);
            }
            else
            {
                Unwait(talk);
                if (reply.Limit is { } limit)
                {
                    talk.Messages.Add(LimitWall(limit, question));
                    // The card says the question stays: it does, in the box (board 6, 2026-10-26).
                    ToBox(talk, question);
                }
                else talk.Messages.Add(Problem(reply.Problem ?? Lang.T("No answer came back. Ask again."), question, givenAside, fromPage));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped: whatever was written goes (the person's word, 2026-10-23), and a faint line says so with Ask again,
            // the question back in the box (board 6, 2026-10-26). It isn't sent again with the next one, since it got no
            // answer. RigSight AI gives the question back, whenever Stop was pressed.
            bool sentYet = _questionId is not null;
            Log.Write("ask", sentYet || Ai.Mode != AiMode.RigSight ? "Stopped before the answer finished: the answer is hidden, the question stays" : "Stopped while waiting for the plan: nothing was sent");
            Unwait(talk);
            if (Ai.Mode == AiMode.RigSight && _questionId is { } id) _ = GiveBackAsync(id);
            talk.Messages.Add(new AskStoppedAnswer(Ai.Mode == AiMode.RigSight ? Lang.T("You stopped this answer. It wasn't counted.") : Lang.T("You stopped this answer."), question)
            {
                Aside = givenAside, FromPage = fromPage,
            });
            ToBox(talk, question);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("ask", ex);
            Unwait(talk);
            talk.Messages.Add(Problem(Lang.T("Something went wrong while answering. Ask again."), question, givenAside, fromPage));
        }
        finally { IsAnswering = false; _answering = null; _questionId = null; longWait.Stop(); Save(); }
    }

    /// <summary>
    /// After an answer, a chat too long to send whole gets a summary of its older part (RigSight AI only), in the
    /// background: the line shows where it starts with a spinner, then opens to show it. The latest messages
    /// (<see cref="AiMemory.KeepRecent"/>) stay out of it. A summary already there is summarised again with the rest. If
    /// it can't be made, the line goes (or the one before comes back) and the chat is sent as before.
    /// </summary>
    private async void Summarise(AskConversation talk)
    {
        if (talk.Summarising is { IsCompleted: false } || !AiMemory.Needs(talk.Sent, talk.SummaryCovers, talk.Summary)) return;
        int keep = AiMemory.Keep(talk.Sent);
        int upTo = talk.Sent.Count - keep;
        var turns = talk.Sent.Skip(talk.SummaryCovers).Take(upTo - talk.SummaryCovers).ToList();
        var before = talk.Messages.OfType<AskSummary>().FirstOrDefault();
        int beforeAt = before is null ? -1 : talk.Messages.IndexOf(before);
        if (before is not null) talk.Messages.Remove(before);
        var line = new AskSummary { Replaced = upTo };
        talk.Messages.Insert(KeptStart(talk, keep / 2), line);
        Log.Write("ask", $"Summarising a long chat: {turns.Count} messages{(talk.Summary is null ? "" : " and its summary")}, keeping the latest {talk.Sent.Count - upTo} as they are");
        var making = Relay.SummariseAsync(turns, talk.Summary);
        talk.Summarising = making;
        string? summary;
        try { summary = await making; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error("ask", ex); summary = null; }
        if (summary is not null)
        {
            (talk.Summary, talk.SummaryCovers) = (summary, upTo);
            line.Text = summary;
            line.IsBusy = false;
            _ = Save();
        }
        else
        {
            talk.Messages.Remove(line);
            if (before is not null) talk.Messages.Insert(Math.Clamp(beforeAt, 0, talk.Messages.Count), before);
        }
    }

    /// <summary>Where the messages kept as they are begin: the question before the <paramref name="exchanges"/>-th last answer.</summary>
    private static int KeptStart(AskConversation talk, int exchanges)
    {
        var answers = talk.Messages.Select((m, i) => (m, i)).Where(x => x.m is AskAnswer).Select(x => x.i).ToList();
        if (answers.Count < exchanges) return 0;
        int at = answers[^exchanges];
        while (at > 0 && talk.Messages[at] is not AskQuestion) at--;
        return at;
    }

    /// <summary>The summary line opens to show the summary, and closes again.</summary>
    [RelayCommand]
    private void ToggleSummary(AskSummary? line) { if (line is not null) line.IsOpen = !line.IsOpen; }

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
                AskNext n when n.HasText => Lang.T($"Next step · {n.Text}"),
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
        if (answer?.Asked is not { Length: > 0 } q || ShowFirstQuestion) return;
        if (CantAskNow) { Draft = q; return; }
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

    /// <summary>
    /// A problem line with its way forward (board 6, 2026-10-26): Open Settings where the words send them there, Try again
    /// unless only Settings can fix it (no key, no model, no Ollama model).
    /// </summary>
    private static AskProblem Problem(string text, string question, string? aside, string? fromPage)
    {
        bool settingsOnly = text == Lang.T("Add your API key in Settings › Ask RigSight and press Connect.") || text == Lang.T("Pick a model in Settings › Ask RigSight.")
            || text == Lang.T("Ollama has no models yet. Download one in Ollama, then pick it in Settings › Ask RigSight.");
        return new AskProblem(text) { Question = settingsOnly ? null : question, Aside = aside, FromPage = fromPage, CanOpenSettings = SendsToSettings(text) };
    }

    /// <summary>The words send them to Settings ("… in Settings › Ask RigSight"), in English or the language pack's words.</summary>
    internal static bool SendsToSettings(string text) =>
        text.Contains("Settings ›", StringComparison.Ordinal) || text.Contains(Lang.T("Settings") + " ›", StringComparison.Ordinal);

    /// <summary>
    /// A question that didn't get its answer (stopped, or refused at a limit) goes back in the box (board 6, 2026-10-26):
    /// only one the person asked (shown in the chat), and never over something they have typed since.
    /// </summary>
    private void ToBox(AskConversation talk, string question)
    {
        if (!talk.Messages.OfType<AskQuestion>().Any(q => q.Text == question)) return;
        if (talk == Current) { if (string.IsNullOrWhiteSpace(Draft)) Draft = question; }
        else if (string.IsNullOrWhiteSpace(talk.Draft)) talk.Draft = question;
    }

    /// <summary>
    /// Try again under a problem, or Ask again under a stopped answer (board 6, 2026-10-26): the same question once more
    /// in the same conversation, that line gone, with no second question bubble. While a question can't go, it waits in
    /// the box instead.
    /// </summary>
    private void Rerun(AskMessage line, string question, string? aside, string? fromPage)
    {
        if (Current is not { } talk || !talk.Messages.Contains(line) || ShowFirstQuestion) return;
        if (CantAskNow) { Draft = question; return; }
        talk.Messages.Remove(line);
        if (Draft.Trim() == question) Draft = "";
        talk.When = DateTime.Now;
        if (Conversations.IndexOf(talk) is > 0 and var at) Conversations.Move(at, 0);
        talk.Messages.Add(new AskThinking());
        Changed();
        ToEnd();
        Log.Write("ask", line is AskProblem ? "Try again after a problem: the same question in its place" : "Ask again after Stop: the same question in its place");
        Answer(talk, question, aside, fromPage);
    }

    [RelayCommand] private void TryAgain(AskProblem? problem) { if (problem?.Question is { } q) Rerun(problem, q, problem.Aside, problem.FromPage); }

    [RelayCommand] private void AskStoppedAgain(AskStoppedAnswer? stopped) { if (stopped is not null) Rerun(stopped, stopped.Question, stopped.Aside, stopped.FromPage); }

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
        // Free questions switched off on the relay for now ("paused", or an older relay's "free"): said as paused, not as a
        // week or a day used up (board 6, 2026-10-26), with the first plan, which still answers.
        if (limit.Kind is "paused" or "free" || FreePaused)
        {
            var first = paid.FirstOrDefault();
            return new AskWall(Lang.T("Free questions are paused right now."), PausedText, first?.Id, first?.Name, question);
        }
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
                : Lang.T($"More at midnight. {bigger.Name} gives up to {bigger.Cap} a day and answers read more; switching sends this question straight away."),
            bigger?.Id, bigger?.Name, question) { Switch = current is { IsFree: false, CanManage: true } };
    }

    /// <summary>A card's main button: buy (or switch to) its plan, then send the question that hit the limit.</summary>
    [RelayCommand]
    private async Task UseWall(AskWall? wall)
    {
        if (wall?.Plan is not { } plan || Plans is null) return;
        // Buying happens in the browser and can take minutes: the card says it's waiting, in place (it said nothing before).
        if (!wall.Switch) wall.Pay.Waiting = true;
        bool on;
        try { on = wall.Switch ? await Plans.SwitchNowAsync(plan) : await Plans.BuyAsync(plan); }
        finally { wall.Pay.Waiting = false; }
        if (!on || Current is not { } talk) return;
        talk.Messages.Add(new AskDone(Lang.T($"{wall.PlanName} is on. Asking your question now.")));
        bool inChat = talk.Messages.Contains(wall) || wall.FromAnswer;
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

    /// <summary>The checkout again, while a card waits for the payment (the tab was closed, or went behind).</summary>
    [RelayCommand]
    private void ReopenCheckout(string? plan)
    {
        if ((Standing?.Plans ?? []).FirstOrDefault(o => o.Id == plan) is { } offer && Uri.TryCreate(offer.Url, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
            Browser.Open(url.AbsoluteUri);
    }

    /// <summary>Cancel on a waiting card: stops waiting for the payment (nothing is bought or cancelled by it).</summary>
    [RelayCommand] private void StopPaying() => Plans?.StopWaitingCommand.Execute(null);

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
            return await Relay.AskAsync([.. AiMemory.History(talk.Sent, talk.SummaryCovers, talk.Summary)], question, _tools!,
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
        if (!IsEnabled || Ai.Mode != AiMode.RigSight) return null;
        if (!theyAsked && !Ai.FirstQuestionSeen.Contains(AiMode.RigSight)) return null;
        // Nothing known yet: the plan card waits with a spinner instead of showing Free until the relay answers
        // (it showed Free, then the real plan a few seconds later, 2026-10-24).
        bool first = Standing is null;
        if (first) { CheckingPlan = true; PlanUnknown = null; }
        try
        {
            var (status, problem) = await Relay.StatusAsync();
            if (status is null)
            {
                Log.Write("ask", $"Questions left not refreshed: {problem}");
                if (Standing is null) PlanUnknown = problem ?? Lang.T("RigSight AI isn't answering right now.");
                return problem;
            }
            PlanUnknown = null;
            Show(status);
            return null;
        }
        finally { if (first) CheckingPlan = false; }
    }

    /// <summary>Questions given from the admin site not yet told: the card above the question box; null when none.</summary>
    [ObservableProperty] private AskGift? _gift;

    /// <summary>
    /// The card for gifts not yet seen, as one: "You've got 50 extra questions", used after the plan's own and kept until
    /// used (the person's rule, 2026-10-25). Without a message, it says who gave them.
    /// </summary>
    internal static AskGift? GiftFor(RelayStatus status, IReadOnlyCollection<long> seen)
    {
        var fresh = status.Gifts.Where(g => !seen.Contains(g.Id)).ToList();
        if (fresh.Count == 0) return null;
        int got = fresh.Sum(g => g.N);
        string title = got == 1 ? Lang.T("You've got 1 extra question") : Lang.T($"You've got {got} extra questions");
        string text = status.Plan switch
        {
            { IsFree: true, Cap: > 0 } p => Lang.T($"They're used after your {p.Cap} free a week, and they don't run out."),
            { IsFree: false } p => Lang.T($"They're used once {p.Name}'s {p.Cap} for the day are gone, and they don't run out."),
            _ => Lang.T("They're used after your plan's own, and they don't run out."),
        };
        string? note = fresh.Select(g => g.Note).FirstOrDefault(n => n is not null);
        if (note is null) text = Lang.T("Given by RigSight.") + " " + text;
        return new AskGift([.. fresh.Select(g => g.Id)], title, text, note);
    }

    /// <summary>
    /// The free trial's two notes, each told once above the question box: the day before it ends (what happens then, and
    /// the plan to keep it), and once it has ended. The key carries the trial's end, so a later trial would be told again.
    /// </summary>
    internal static AskGift? TrialNoteFor(RelayStatus status, IReadOnlyCollection<string> seen, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        if (status.Trial is not { Ends: { } ends } trial) return null;
        string name = status.Plans.FirstOrDefault(p => p.Id == trial.Plan)?.Name ?? status.Plan?.Name ?? trial.Plan;
        int free = status.Plans.FirstOrDefault(p => p.IsFree)?.Cap ?? 10;
        if (trial.Running && ends - at <= TimeSpan.FromDays(1) && !seen.Contains($"ending:{ends:o}"))
            return new AskGift([], ends.Date == at.Date ? Lang.T($"Your {name} trial ends today at {TimeText.At(ends, "HH:mm")}.") : Lang.T($"Your {name} trial ends tomorrow at {TimeText.At(ends, "HH:mm")}."),
                Lang.T($"Then you're on Free: {free} questions a week. Your conversations stay."), null) { TrialKey = $"ending:{ends:o}" };
        if (trial.Ended && at - ends <= TimeSpan.FromDays(14) && !seen.Contains($"ended:{ends:o}"))
            return new AskGift([], Lang.T($"Your {name} trial has ended"),
                Lang.T($"You're on Free now: {free} questions a week. Everything from the trial is kept."), null) { TrialKey = $"ended:{ends:o}" };
        return null;
    }

    /// <summary>"Got it", or a question asked: the card goes and isn't shown again for these gifts (or this trial note).</summary>
    [RelayCommand]
    private void CloseGift()
    {
        if (Gift is { TrialKey: { } key })
        {
            Log.Write("ask", $"Trial note told and closed: {key}");
            _settings.Update(s => s.Ai.TrialNotesSeen = [.. s.Ai.TrialNotesSeen.Append(key).Distinct().TakeLast(10)]);
            Gift = null;
            return;
        }
        if (Gift is not { } gift) return;
        Log.Write("ask", $"Questions given told and closed: {gift.Title} (gifts {string.Join(", ", gift.Ids)})");
        // The newest 50 kept: a gift runs a week at most, so older ids are never sent again.
        _settings.Update(s => s.Ai.GiftsSeen = [.. s.Ai.GiftsSeen.Concat(gift.Ids).Distinct().TakeLast(50)]);
        Gift = null;
    }

    /// <summary>Asking the relay where this PC stands while nothing is known yet.</summary>
    [ObservableProperty] private bool _checkingPlan;
    /// <summary>Why the plan couldn't be checked, while nothing is known yet; null otherwise.</summary>
    [ObservableProperty] private string? _planUnknown;

    /// <summary>What the relay said about this PC, shown in the pill and Settings.</summary>
    public void Show(RelayStatus status)
    {
        Standing = status;
        Gift = GiftFor(status, Ai.GiftsSeen) ?? TrialNoteFor(status, Ai.TrialNotesSeen);
        OnPropertyChanged(nameof(OutOffer));
        HasLicence = status.HasLicence && status.LicenceStatus == "active";
        // A relay with plans counts this week's free ones or today's; an older one, what's left in all.
        QuestionsLeft = status.Plan?.AllLeft ?? status.QuestionsLeft;
        foreach (var name in new[] { nameof(QuestionsLeftText), nameof(IsLow), nameof(QuestionsLine), nameof(QuestionsNote), nameof(ShowSeePlans), nameof(ShowSeePlansUnder), nameof(ShowNoteUnder), nameof(IsOutOfQuestions), nameof(ShowOutBox) })
            OnPropertyChanged(name);
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The relay's latest word on this PC's questions and licence; null until it answers.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LicenceOff), nameof(OutTitle), nameof(OutText), nameof(QuestionsLeftText), nameof(QuestionsLine), nameof(QuestionsNote), nameof(ShowSeePlans), nameof(ShowSeePlansUnder), nameof(ShowNoteUnder), nameof(IsLastQuestion), nameof(LastQuestionTitle), nameof(LastQuestionText), nameof(FirstExtra))] private RelayStatus? _standing;

    /// <summary>
    /// The plan known (or changed): a new chat's starters are read again for its pages. Until the relay answers, Free's
    /// are assumed, which left one starter of four on a Complete plan (2026-10-24). Answers' plan cards follow the plan too:
    /// one for a page the plan now includes goes (board 6, 2026-10-26).
    /// </summary>
    partial void OnStandingChanged(RelayStatus? oldValue, RelayStatus? newValue)
    {
        if (SamePages(oldValue?.Plan?.Pages, newValue?.Plan?.Pages)) return;
        if (ShowStarters) RefreshStarters();
        foreach (var talk in Conversations)
            for (int i = 0; i < talk.Messages.Count; i++)
                if (talk.Messages[i] is AskAnswer { Locked: { } page } answer && newValue?.Plan?.Pages.Contains(page.Id) == true)
                    talk.Messages[i] = WithLockedPlan(answer);
    }

    private static bool SamePages(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.Order().SequenceEqual(b.Order());

    private async Task<AiReply> AskOwnKey(AskConversation talk, string question, CancellationToken ct)
    {
        if (KeyStore.Load() is not { Length: > 0 } key)
            return new AiReply(null, Lang.T("Add your API key in Settings › Ask RigSight and press Connect."), []);
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
    // "[locked: storage]", and since actions-2 "[locked: storage | See what fills the E drive]": what this question would get.
    private static readonly Regex LockedLine = new(@"^\[\s*locked\s*:\s*(?<page>[a-z]+)\s*(?:\|\s*(?<gain>[^\]]{2,80}?))?\s*\]$", RegexOptions.IgnoreCase);
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
        string? lockedGain = null;
        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i], l = W(raw);
            // "[locked: games]": a page the plan doesn't open would have helped; said once, under the answer.
            if (LockedLine.Match(l) is { Success: true } lockedLine)
            {
                if (locked is null && AiPages.Get(lockedLine.Groups["page"].Value.ToLowerInvariant()) is { } page)
                {
                    locked = page;
                    lockedGain = lockedLine.Groups["gain"].Success ? lockedLine.Groups["gain"].Value.Trim().TrimEnd('.') : null;
                }
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
            parts.Add(LeadIn.Match(body) is { Success: true } lead ? new AskLine(lead.Groups["rest"].Value, isPoint, Controls.Md.Plain(lead.Groups["lead"].Value)) { Refs = refs } : new AskLine(body, isPoint) { Refs = refs });
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
            // Close first: it's the card's own button ("Close it"); the rest after it, and the widget, overlay and taskbar
            // cards last (they're cards of their own, so the first line still says what the step is for).
            list = [.. list.OrderBy(b => b.Kind == AiActionKind.CloseApp ? 0 : b.IsSetup ? 2 : 1)];
            bool card = list.Any(b => b.Kind == AiActionKind.CloseApp);
            next = next with { Actions = [.. list.Select((b, k) => new AskAction(b)
            {
                Role = k == 0 ? AskActionRole.Main : k == 1 ? AskActionRole.Second : AskActionRole.Link,
                // As a line, the first one says what it's for (the step), so the step isn't said twice.
                // A maker's page says which site it opens ("· amd.com"), as the approved artifact draws it.
                Why = !card && k == 0 && next.Text.Length > 0 ? next.Text
                    : b.Kind == AiActionKind.OpenWeb && Uri.TryCreate(b.Target, UriKind.Absolute, out var site) ? site.Host.Replace("www.", "") : null,
                InCard = card,
            })] };
            if (at >= 0) parts[at] = next; else parts.Add(next);
        }
        return new AskAnswer(verdict, parts, null, Chips(lookUps)) { Raw = text, LookUps = lookUps, Drawn = drawnBy, Suggestions = suggestions, VerdictRefs = verdictRefs, Locked = writing ? null : locked, LockedGain = writing ? null : lockedGain, Title = title };
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
        // Its summary, if it has one: it stands for the questions and answers before it.
        int pairsBefore = 0, covers = 0;
        for (int i = 0; i < saved.Count; i++)
        {
            if (i > 0 && saved[i - 1].Kind == SavedMessage.Question && saved[i].Kind == SavedMessage.Answer) pairsBefore++;
            if (saved[i].Kind == SavedMessage.Summary) { covers = Math.Min(2 * pairsBefore, talk.Sent.Count); talk.Summary = saved[i].Text; }
        }
        talk.SummaryCovers = talk.Summary is null ? 0 : covers;
        Tools();
        var tools = _tools;
        var canRead = CanReadNow();
        var (sensors, tray) = (SetupSensors?.Invoke(), TrayReadings?.Invoke());
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
                        case SavedMessage.Summary: list.Add(new AskSummary { Text = m.Text, IsBusy = false, Replaced = covers }); break;
                        case SavedMessage.Check when AskCheck.FromSaved(m.Text) is { } check: list.Add(check); break;
                        case SavedMessage.Answer:
                            var looks = (m.LookUps ?? []).Select(l => l.ToLookUp()).ToList();
                            // The numbers as they were shown then; an answer saved before they were kept is drawn again.
                            Func<string, IReadOnlyList<AiBlock>> draw = m.Drawn is { } kept
                                ? marker => kept.TryGetValue(marker, out var b) ? b : []
                                : marker => tools.Blocks(marker, looks);
                            list.Add(WithLockedPlan(ToAnswer(m.Text, looks, draw, actions: markers => AiActions.Resolve(markers, new AiActionContext(looks, null, tools.Installed, true, canRead, OpenableOnPc, StartMenu, sensors, tray))
                                .Where(a => a.Kind is not (AiActionKind.CloseApp or AiActionKind.CheckAgain or AiActionKind.StartAgent)).ToList())
                                with { Model = m.Model, Asked = list.OfType<AskQuestion>().LastOrDefault()?.Text }));
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
        foreach (var answer in messages.OfType<AskAnswer>()) PrepareSetups(answer);
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
            AskStoppedAnswer x => new SavedMessage(SavedMessage.Stopped, x.Text),
            AskDone d => new SavedMessage(SavedMessage.Done, d.Text),
            AskCheck k => new SavedMessage(SavedMessage.Check, k.Saved),
            AskSummary { IsDone: true } y => new SavedMessage(SavedMessage.Summary, y.Text),
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
        OnPropertyChanged(nameof(HistoryEmptyText));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(HasArchived));
    }

    public bool HasHistory => Groups.Count > 0;
    /// <summary>Nothing in History: none yet, or none matching the search (it said "No conversations yet." to a search, board 6).</summary>
    public string HistoryEmptyText => HistorySearch.Trim().Length > 0 ? Lang.T("No conversations match") : Lang.T("No conversations yet.");


}

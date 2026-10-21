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
}

/// <summary>A line of an answer: a paragraph, or one point of a list (shown with a bullet).</summary>
public sealed record AskLine(string Text, bool IsPoint, string? Lead = null);

/// <summary>A table the model wrote (parts, games or web facts side by side): its headings and rows, each as many cells.</summary>
public sealed record AskTable(IReadOnlyList<string> Headings, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int Columns => Headings.Count;
}

/// <summary>The one thing to do next, in a box of its own, with the page that shows more.</summary>
public sealed record AskNext(string Text, string? Opens, string? OpensLabel)
{
    public bool CanOpen => Opens is not null;
}

/// <summary>Something in a conversation: the person's question, or the helper's answer.</summary>
public abstract record AskMessage;

public sealed record AskQuestion(string Text) : AskMessage;

/// <summary>
/// An answer: one plain statement first, then its parts in order (paragraphs and points, RigSight's own numbers drawn
/// from its records, tables, the next step), what it found on the web if it looked, and the records it looked at (which
/// are what was sent).
/// </summary>
public sealed record AskAnswer(string Verdict, IReadOnlyList<object> Parts, string? FromWeb, IReadOnlyList<AskLook> Looked) : AskMessage
{
    public bool HasWeb => FromWeb is not null;
    public bool HasLooked => Looked.Count > 0;
    /// <summary>The model that wrote it, kept with the saved answer.</summary>
    public string? Model { get; init; }
    /// <summary>The model's own text and what it looked at: what is saved, and what the answer is drawn again from.</summary>
    public string Raw { get; init; } = "";
    public IReadOnlyList<AiLookUp> LookUps { get; init; } = [];
    /// <summary>RigSight's numbers as drawn for each marker: saved with the answer, so it shows the same when opened again.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AiBlock>> Drawn { get; init; } = new Dictionary<string, IReadOnlyList<AiBlock>>();
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
            else if (!Equals(Parts[i], now.Parts[i])) Parts[i] = now.Parts[i];
        }
        while (Parts.Count > now.Parts.Count) Parts.RemoveAt(Parts.Count - 1);
    }
}

/// <summary>No answer came: Ollama not running, the model gone, and so on, said plainly.</summary>
public sealed record AskProblem(string Text) : AskMessage;

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
        if (loading) BeSlow();
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
        OnPropertyChanged(nameof(IsOutOfQuestions));
        foreach (var name in new[] { nameof(ShareCrashes), nameof(ShareHeat), nameof(ShareApps), nameof(ShareChanges), nameof(ShareMemory), nameof(ShareNetwork), nameof(ShareUsage), nameof(ShareParts) })
            OnPropertyChanged(name);
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

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowChat), nameof(ShowStarters), nameof(LookingAt), nameof(HasLookingAt))] private AskConversation? _current;
    partial void OnCurrentChanged(AskConversation? oldValue, AskConversation? newValue)
    {
        if (oldValue is not null) { oldValue.IsCurrent = false; oldValue.Messages.CollectionChanged -= OnMessagesChanged; oldValue.Draft = Draft; }
        if (newValue is not null) { newValue.IsCurrent = true; newValue.Messages.CollectionChanged += OnMessagesChanged; }
        // Each conversation keeps its own unsent words: what was typed in one never carries into another.
        Draft = newValue?.Draft ?? "";
    }
    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowChat));
        OnPropertyChanged(nameof(ShowStarters));
    }

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

    /// <summary>Questions to start from on an empty conversation (ones the helper can answer from RigSight's records).</summary>
    public IReadOnlyList<string> Starters { get; } =
    [
        Lang.T("Why did my PC turn off by itself?"),
        Lang.T("Are my temperatures normal for my parts?"),
        Lang.T("Which game is hardest on my PC?"),
        Lang.T("Did anything change before my crashes started?"),
    ];

    // ── Questions left (RigSight AI only) ────────────────────────────────
    /// <summary>Every PC gets this many free questions to try RigSight AI before buying any.</summary>
    public const int FreeQuestions = 10;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuestionsLeftText), nameof(IsLow), nameof(IsOutOfQuestions))] private int _questionsLeft = FreeQuestions;
    /// <summary>A bought licence is on this PC; without one, the free questions are counted.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuestionsLeftText))] private bool _hasLicence;
    /// <summary>RigSight AI: the pill in the window's top bar says how many questions are left (free ones until a pack is bought).</summary>
    public bool ShowQuestionsLeft => Ai.Mode == AiMode.RigSight;
    public string QuestionsLeftText => (HasLicence, QuestionsLeft) switch
    {
        (true, 1) => Lang.T("1 question left"),
        (true, _) => Lang.T($"{QuestionsLeft} questions left"),
        (false, 1) => Lang.T("1 free question left"),
        (false, _) => Lang.T($"{QuestionsLeft} free questions left"),
    };
    public bool IsLow => QuestionsLeft <= 5;
    public bool IsOutOfQuestions => ShowQuestionsLeft && QuestionsLeft <= 0;

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
    }

    private void FirstQuestionChanged()
    {
        foreach (var name in new[] { nameof(ShowFirstQuestion), nameof(ShowChat), nameof(ShowStarters), nameof(FirstIntro), nameof(FirstWhere),
                     nameof(FirstWhereText), nameof(FirstMore), nameof(FirstMoreText), nameof(FirstNever), nameof(FirstExtra), nameof(FirstTone) })
            OnPropertyChanged(name);
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>About what a question costs on the picked model at the service's prices ("0.3¢"), when it publishes them; set by the shell.</summary>
    public Func<string?>? PerQuestion { get; set; }

    private string Service => Ai.Service.Length > 0 ? Ai.Service : Lang.T("the service");

    public string FirstIntro => Ai.Mode switch
    {
        AiMode.OwnKey => Lang.T("Your own key answers from your PC's records. To do that, RigSight sends your question and the records it needs to the service you picked."),
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
        _ => Lang.T("Your question and these records go through RigSight's server to the AI model. Nothing is kept after the answer."),
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
        AiMode.OwnKey => Lang.T("Never sent: your Windows user name, and file and folder paths."),
        AiMode.Ollama when OllamaHere => null,
        AiMode.Ollama => Lang.T("Never sent: your Windows user name, and file and folder paths."),
        _ => Lang.T("Never sent: your Windows user name, file and folder paths, and your licence key."),
    };
    public string? FirstExtra => Ai.Mode == AiMode.RigSight && !HasLicence ? Lang.T($"You have {FreeQuestions} free questions to start.") : null;

    /// <summary>The dot's colour: blue to RigSight AI, amber out to another service or computer, green staying on the PC.</summary>
    public string FirstTone => Ai.Mode switch { AiMode.OwnKey => "warm", AiMode.Ollama => OllamaHere ? "good" : "warm", _ => "accent" };

    /// <summary>Ollama's address is this PC: then, and only then, nothing leaves it.</summary>
    private bool OllamaHere => Ollama.IsOnThisPc(Ai.OllamaAddress);

    // The same switches as Settings › Ask RigSight › What RigSight sends.
    public bool ShareCrashes { get => Ai.ShareCrashes; set => _settings.Update(s => s.Ai.ShareCrashes = value); }
    public bool ShareHeat { get => Ai.ShareHeat; set => _settings.Update(s => s.Ai.ShareHeat = value); }
    public bool ShareApps { get => Ai.ShareApps; set => _settings.Update(s => s.Ai.ShareApps = value); }
    public bool ShareChanges { get => Ai.ShareChanges; set => _settings.Update(s => s.Ai.ShareChanges = value); }
    public bool ShareMemory { get => Ai.ShareMemory; set => _settings.Update(s => s.Ai.ShareMemory = value); }
    public bool ShareNetwork { get => Ai.ShareNetwork; set => _settings.Update(s => s.Ai.ShareNetwork = value); }
    public bool ShareUsage { get => Ai.ShareUsage; set => _settings.Update(s => s.Ai.ShareUsage = value); }
    public bool ShareParts { get => Ai.ShareParts; set => _settings.Update(s => s.Ai.ShareParts = value); }

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

    // Nothing goes before the first-question window is read: Start asking is the way past it.
    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft) && !IsOutOfQuestions && !IsAnswering && !ShowFirstQuestion;

    /// <summary>An answer is on its way: another question waits for it.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isAnswering;

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
        if (!Ai.ShareCrashes)
        {
            talk.Messages.Clear();
            talk.Messages.Add(new AskProblem(Lang.T("Explain needs “Crashes and shut-offs” turned on in Settings › Ask RigSight.")));
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

    /// <summary>
    /// Gets the answer and puts it in place of the "Thinking…" line. Ollama on my PC and my own key answer now; RigSight
    /// AI is wired with the relay.
    /// </summary>
    private async void Answer(AskConversation talk, string question)
    {
        // A test copy with a search key stands in for RigSight AI until the relay is built: the saved key and model,
        // with RigSight AI's instructions and web search.
        bool standIn = Ai.Mode == AiMode.RigSight && WebSearch.TestKey is not null;
        if (Ai.Mode is not (AiMode.Ollama or AiMode.OwnKey) && !standIn)
        {
            // RigSight AI answers once the relay is built; until then it says so rather than leaving "Thinking…" up.
            Log.Write("ask", $"Not asked: {Ai.Mode} can't answer in this build");
            Unwait(talk);
            talk.Messages.Add(new AskProblem(Lang.T("RigSight AI can't answer yet. Pick your own key or Ollama in Settings › Ask RigSight.")));
            _ = Save();
            return;
        }
        IsAnswering = true;
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
            var reply = Ai.Mode == AiMode.Ollama ? await AskOllama(talk, question, stop.Token) : await AskOwnKey(talk, question, stop.Token, asRigSightAi: standIn);
            if (reply.Text is { } text)
            {
                talk.Sent.Add(new AiTurn(true, question));
                talk.Sent.Add(new AiTurn(false, text, reply.LookUps));
                // RigSight's own numbers are read from the history, so off the window's thread. The answer as it streamed
                // stays up meanwhile, and the finished one takes its place in one step: taking it away first left the chat
                // empty for a moment ("it flashes").
                var tools = _tools!;
                var answer = await Task.Run(() => ToAnswer(text, reply.LookUps, marker => tools.Blocks(marker, reply.LookUps))) with { Model = reply.Model };
                int at = talk.Messages.IndexOf(talk.Messages.FirstOrDefault(m => m is AskWriting)!);
                if (at >= 0) talk.Messages[at] = answer;
                Unwait(talk);
                if (at < 0) talk.Messages.Add(answer);
            }
            else
            {
                Unwait(talk);
                talk.Messages.Add(new AskProblem(reply.Problem ?? Lang.T("No answer came back. Ask again.")));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The question stays where it was; it isn't sent again with the next one, since it got no answer.
            Log.Write("ask", "Stopped before the answer came");
            Unwait(talk);
            talk.Messages.Add(new AskStopped(Lang.T("Stopped. Ask again whenever you like.")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("ask", ex);
            Unwait(talk);
            talk.Messages.Add(new AskProblem(Lang.T("Something went wrong while answering. Ask again.")));
        }
        finally { IsAnswering = false; _answering = null; longWait.Stop(); Save(); }
    }

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
        bool becoming = tail.StartsWith('[') || tail.StartsWith('|')
            || (tail.Length < 12 && "next step:".StartsWith(tail.TrimStart('-', '*', ' ').ToLowerInvariant(), StringComparison.Ordinal));
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
        _tools ??= new AiTools(_settings.Current);
        return await Ollama.AskAsync(Ai.OllamaAddress, model.Name, model.CanLookUp, [.. talk.Sent], question, _tools,
            step => talk.Messages.OfType<AskThinking>().LastOrDefault()?.Show(step.Text, step.Note, step.Loading), ct);
    }

    private async Task<AiReply> AskOwnKey(AskConversation talk, string question, CancellationToken ct, bool asRigSightAi = false)
    {
        if (KeyStore.Load() is not { Length: > 0 } key)
            return new AiReply(null, Lang.T("Add your API key in Settings › Ask RigSight and press Test."), []);
        if (Ai.Model.Length == 0)
            return new AiReply(null, Lang.T("Pick a model in Settings › Ask RigSight."), []);
        _tools ??= new AiTools(_settings.Current);
        return await OwnKey.AskAsync(Ai.Service, OwnKey.Root(Ai.Service, Ai.CustomAddress), key, Ai.Model, [.. talk.Sent], question, _tools,
            step => talk.Messages.OfType<AskThinking>().LastOrDefault()?.Show(step.Text, step.Note, step.Loading), Writer(talk), ct, asRigSightAi);
    }

    /// <summary>Past this many characters a first line is a paragraph, not an opening sentence.</summary>
    private const int LongestVerdict = 160;

    private static readonly Regex Point = new(@"^\s*([-*•]|\d+[.)])\s+");
    private static readonly Regex LeadIn = new(@"^(?<lead>[^:.!?|]{3,28}:\s)(?<rest>.+)$");
    private static readonly Regex Marker = new(@"^\[\s*show\s*:\s*(?<what>[a-z]+)\s*\]$", RegexOptions.IgnoreCase);
    // "Next step: …", also as small models write it at times: in brackets, or as a point ("[next step: …]", "- Next step: …").
    private static readonly Regex NextStep = new(@"^(?:[-*•]\s*)?\[?\s*(next step|siguiente paso|pr[oó]ximo paso)\s*:\s*(?<rest>.+?)\s*\]?$", RegexOptions.IgnoreCase);
    private static readonly Regex TableRule = new(@"^\|?\s*:?-{2,}");
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
    public static AskAnswer ToAnswer(string text, IReadOnlyList<AiLookUp> lookUps, Func<string, IReadOnlyList<AiBlock>>? blocks = null, bool writing = false)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => Italic.Replace(l.Replace("**", "").Replace("__", "").Replace("`", ""), "$1").TrimStart('#').Trim())
            // The note RigSight adds to earlier answers when it sends them again, copied by a model into its own answer.
            .Where(l => l.Length > 0 && !l.StartsWith("(Records you looked at", StringComparison.OrdinalIgnoreCase)).ToList();
        string verdict = lines.Count > 0 && !Marker.IsMatch(lines[0]) && !lines[0].StartsWith('|') ? Point.Replace(lines[0], "") : "";
        if (verdict.Length > 0) lines.RemoveAt(0);
        var parts = new List<object>();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drawnBy = new Dictionary<string, IReadOnlyList<AiBlock>>();
        for (int i = 0; i < lines.Count; i++)
        {
            string l = lines[i];
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
            if (l.StartsWith('|'))
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
            bool isPoint = Point.IsMatch(l);
            string body = Point.Replace(l, "");
            parts.Add(LeadIn.Match(body) is { Success: true } lead ? new AskLine(lead.Groups["rest"].Value, isPoint, lead.Groups["lead"].Value) : new AskLine(body, isPoint));
        }
        // The first line stands out only as an opening sentence with more after it: a short reply alone, or a whole
        // paragraph first, read as all bold ("text feels so bold", 2026-10-21).
        if (verdict.Length > 0 && ((parts.Count == 0 && !writing) || verdict.Length > LongestVerdict))
        {
            parts.Insert(0, new AskLine(verdict, false));
            verdict = "";
        }
        return new AskAnswer(verdict, parts, null, Chips(lookUps)) { Raw = text, LookUps = lookUps, Drawn = drawnBy };
    }

    /// <summary>The page the next step points to: where the records the answer leaned on are shown.</summary>
    /// <summary>
    /// The page the next step sends them to, when it names one ("open Crashes and…"): a button for a page the step
    /// doesn't mention read as unrelated ("Open Reports" under "check AMD's spec page", 2026-10-21).
    /// </summary>
    private static (string? Page, string? Label) Opens(string step)
    {
        bool Names(params string[] words) => words.Any(w => Regex.IsMatch(step, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase));
        if (Names("Crashes", Lang.T("Crashes"))) return ("crashes", Lang.T("Open Crashes"));
        if (Names("Timeline", Lang.T("Timeline"))) return ("timeline", Lang.T("Open Timeline"));
        if (Names("Games page", "Games", Lang.T("Games"))) return ("games", Lang.T("Open Games"));
        if (Names("Reports", Lang.T("Reports"))) return ("reports", Lang.T("Open Reports"));
        return (null, null);
    }

    [RelayCommand] private void OpenNext(AskNext? next) { if (next?.Opens is { } page) OpenPage?.Invoke(page); }

    /// <summary>The "Looked at" chips: one for each record read, in product words, each opening its page.</summary>
    private static List<AskLook> Chips(IReadOnlyList<AiLookUp> lookUps)
    {
        static string Range(AiLookUp l)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!DateTime.TryParseExact((string?)l.Args["from"], "yyyy-MM-dd", inv, System.Globalization.DateTimeStyles.None, out var from)
                || !DateTime.TryParseExact((string?)l.Args["to"], "yyyy-MM-dd", inv, System.Globalization.DateTimeStyles.None, out var to)) return "";
            if (to < from) (from, to) = (to, from);
            return from == to ? Lang.T($"{from:d MMM}") : Lang.T($"{from:d MMM} – {to:d MMM}");
        }
        var chips = new List<AskLook>();
        foreach (var l in lookUps)
        {
            AskLook? chip = l.Tool switch
            {
                "get_pc_info" => new("", Lang.T("Your PC's parts"), Lang.T("CPU, graphics card, motherboard, RAM"), null),
                "get_report" => new("", Lang.T($"Report, {Range(l)}"), Lang.T("Time on, peaks, what stood out and apps used"), "reports"),
                "get_crashes" => new("", Lang.T($"Crashes, {Range(l)}"), Lang.T("Crashes, hangs and shut-offs with their readings"), "crashes"),
                "get_changes" => new("", Lang.T($"Changes, {Range(l)}"), Lang.T("Drivers, updates, settings and apps that changed"), "timeline"),
                "list_games" => new("", Lang.T("Your games"), Lang.T("Hours played and how hard each works the graphics card"), "games"),
                "get_game" => new("", (string?)l.Args["name"] ?? Lang.T("A game"), Lang.T("Every play's loads, temperatures and how it ended"), "games"),
                "search_web" when (string?)l.Args["query"] is { Length: > 0 } query => new("", Lang.T("The web"), Lang.T($"Searched: {query}"), null),
                _ => null,
            };
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
        _tools ??= new AiTools(_settings.Current);
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
                        case SavedMessage.Answer:
                            var looks = (m.LookUps ?? []).Select(l => l.ToLookUp()).ToList();
                            // The numbers as they were shown then; an answer saved before they were kept is drawn again.
                            Func<string, IReadOnlyList<AiBlock>> draw = m.Drawn is { } kept
                                ? marker => kept.TryGetValue(marker, out var b) ? b : []
                                : marker => tools.Blocks(marker, looks);
                            list.Add(ToAnswer(m.Text, looks, draw) with { Model = m.Model });
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
        if (talk == Current) ToEnd();
    }

    /// <summary>Writes every conversation with something in it to the file, off the window's thread.</summary>
    internal Task Save()
    {
        if (_file is null) return Task.CompletedTask;
        var snapshot = Conversations.Where(c => c.Title.Length > 0).Select(ToSaved).ToList();
        string file = _file;
        return Task.Run(() => AiConversations.Save(snapshot, file));
    }

    private static SavedConversation ToSaved(AskConversation c) => new(c.Id, c.Title, c.When, c.LookingAt, c.IsPinned, c.IsArchived,
        c.Unopened ?? [.. c.Messages.Select(m => m switch
        {
            AskQuestion q => new SavedMessage(SavedMessage.Question, q.Text),
            AskAnswer a => new SavedMessage(SavedMessage.Answer, a.Raw, a.Model, [.. a.LookUps.Select(SavedLookUp.From)], a.Drawn),
            AskProblem p => new SavedMessage(SavedMessage.Problem, p.Text),
            AskStopped x => new SavedMessage(SavedMessage.Stopped, x.Text),
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

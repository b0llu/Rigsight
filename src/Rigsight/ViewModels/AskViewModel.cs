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
public sealed record AskLook(string Icon, string Text, string Detail, string? Opens);

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
    /// <summary>Which model wrote it, where the person picked one themselves ("Answered by gemma4:12b on this PC"); null otherwise.</summary>
    public string? By { get; init; }
    /// <summary>The model that wrote it, so the next answer only says "Answered by" again when another one did.</summary>
    public string? Model { get; init; }
    public bool HasBy => By is not null;
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

    public void Show(string step, string? note = null, bool loading = false)
    {
        (Step, Note, Loading, Since) = (step, note, loading, DateTime.Now);
        foreach (var name in new[] { nameof(Step), nameof(Note), nameof(HasNote) }) PropertyChanged?.Invoke(this, new(name));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A conversation kept in History: its first question as the title, when it was, and what was said.</summary>
public sealed partial class AskConversation : ObservableObject
{
    public required string Title { get; init; }
    public DateTime When { get; init; } = DateTime.Now;
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

    public AskViewModel(SettingsModel settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
        Current = Conversations.FirstOrDefault() ?? NewConversation();
        Regroup();
    }

    private void OnSettingsChanged()
    {
        if (!IsEnabled) IsOpen = false;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(InTopBar));
        OnPropertyChanged(nameof(InSidebar));
        OnPropertyChanged(nameof(ShowQuestionsLeft));
        OnPropertyChanged(nameof(IsOutOfQuestions));
        Regroup();
    }

    /// <summary>An answer can come from somewhere: Ask and Explain buttons show. Off hides every one of them.</summary>
    public bool IsEnabled => Ai.Mode != AiMode.Off;
    public bool InTopBar => IsEnabled && Ai.Place == AskPlace.TopBar;
    public bool InSidebar => IsEnabled && Ai.Place == AskPlace.Sidebar;

    [ObservableProperty] private bool _isOpen;
    /// <summary>Floating anywhere over the window instead of stuck to its bottom-right corner.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowHistoryColumn), nameof(ShowHistoryOver))] private bool _isPoppedOut;
    /// <summary>Stuck to the corner at the bigger size.</summary>
    [ObservableProperty] private bool _isBig;
    /// <summary>Stuck: History slides over the chat while this is on.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowHistoryOver))] private bool _isHistoryOpen;
    /// <summary>Popped out: History is a column beside the chat, which the History button hides and shows.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowHistoryColumn))] private bool _historyColumn = true;

    public bool ShowHistoryColumn => IsPoppedOut && HistoryColumn;
    public bool ShowHistoryOver => !IsPoppedOut && IsHistoryOpen;
    public bool HistoryPressed => IsPoppedOut ? HistoryColumn : IsHistoryOpen;

    partial void OnIsPoppedOutChanged(bool value) { IsHistoryOpen = false; OnPropertyChanged(nameof(HistoryPressed)); }
    partial void OnIsHistoryOpenChanged(bool value) => OnPropertyChanged(nameof(HistoryPressed));
    partial void OnHistoryColumnChanged(bool value) => OnPropertyChanged(nameof(HistoryPressed));

    public ObservableCollection<AskConversation> Conversations { get; } = [];
    [ObservableProperty] private IReadOnlyList<AskGroup> _groups = [];
    [ObservableProperty] private string _historySearch = "";
    partial void OnHistorySearchChanged(string value) => Regroup();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(LookingAt), nameof(HasLookingAt))] private AskConversation? _current;
    partial void OnCurrentChanged(AskConversation? oldValue, AskConversation? newValue)
    {
        if (oldValue is not null) { oldValue.IsCurrent = false; oldValue.Messages.CollectionChanged -= OnMessagesChanged; }
        if (newValue is not null) { newValue.IsCurrent = true; newValue.Messages.CollectionChanged += OnMessagesChanged; }
    }
    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(IsEmpty));

    public bool IsEmpty => Current is null || Current.Messages.Count == 0;
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
    public const int FreeQuestions = 5;

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
        if (IsPoppedOut) HistoryColumn = !HistoryColumn;
        else IsHistoryOpen = !IsHistoryOpen;
    }

    [RelayCommand]
    private void New()
    {
        // An empty conversation is reused instead of piling up blank ones in History.
        Current = Conversations.FirstOrDefault(c => c.Messages.Count == 0) ?? NewConversation();
        IsHistoryOpen = false;
    }

    [RelayCommand]
    private void Pick(AskConversation? conversation)
    {
        if (conversation is null) return;
        Current = conversation;
        IsHistoryOpen = false;
    }

    [RelayCommand] private void Starter(string? text) { if (!string.IsNullOrWhiteSpace(text)) Ask(text); }

    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft) && !IsOutOfQuestions && !IsAnswering;

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
        var talk = new AskConversation { Title = Lang.T($"Explain: {what}"), LookingAt = what };
        Conversations.Insert(0, talk);
        Current = talk;
        talk.Messages.Add(new AskThinking());
        IsHistoryOpen = false;
        IsOpen = true;
        Regroup();
        Answer(talk, $"Explain this from my PC's records: {what}");
    }

    /// <summary>The question goes in, and the helper starts on it.</summary>
    private void Ask(string text)
    {
        var talk = Current ?? NewConversation();
        if (talk.Messages.Count == 0 && talk.Title.Length == 0)
        {
            var titled = new AskConversation { Title = text };
            Conversations.Remove(talk);
            Conversations.Insert(0, titled);
            talk = titled;
            Current = titled;
        }
        foreach (var waiting in talk.Messages.OfType<AskThinking>().ToList()) talk.Messages.Remove(waiting);
        talk.Messages.Add(new AskQuestion(text));
        talk.Messages.Add(new AskThinking());
        Regroup();
        Answer(talk, text);
    }

    /// <summary>The records it may look at, read the way the pages read them.</summary>
    private AiTools? _tools;
    private IReadOnlyList<OllamaModel>? _ollamaModels;
    private string _ollamaModelsFrom = "";

    /// <summary>
    /// Gets the answer and puts it in place of the "Thinking…" line. Ollama on my PC answers now; RigSight AI and
    /// my own key are wired with the relay and the services.
    /// </summary>
    private async void Answer(AskConversation talk, string question)
    {
        if (Ai.Mode != AiMode.Ollama) return;
        IsAnswering = true;
        using var stop = new CancellationTokenSource();
        _answering = stop;
        // One stage going on far longer than usual (but not the model loading, which says so): say it's still at it,
        // once, rather than rotating lines to fill the time.
        var longWait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        longWait.Tick += (_, _) =>
        {
            if (talk.Messages.OfType<AskThinking>().LastOrDefault() is { Loading: false } thinking && DateTime.Now - thinking.Since > StillWorkingAfter
                && thinking.Step != StillWorking)
                thinking.Show(StillWorking);
        };
        longWait.Start();
        try
        {
            var reply = await AskOllama(talk, question, stop.Token);
            foreach (var waiting in talk.Messages.OfType<AskThinking>().ToList()) talk.Messages.Remove(waiting);
            if (reply.Text is { } text)
            {
                talk.Sent.Add(new AiTurn(true, question));
                talk.Sent.Add(new AiTurn(false, text));
                // RigSight's own numbers are read from the history, so off the window's thread.
                var tools = _tools!;
                var answer = await Task.Run(() => ToAnswer(text, reply.LookUps, marker => tools.Blocks(marker, reply.LookUps)));
                // Said under the first answer, and again only where another model took over.
                string? before = talk.Messages.OfType<AskAnswer>().LastOrDefault()?.Model;
                talk.Messages.Add(answer with
                {
                    Model = reply.Model,
                    By = reply.Model is { } model && model != before ? Lang.T($"Answered by {model} on this PC") : null,
                });
            }
            else talk.Messages.Add(new AskProblem(reply.Problem ?? Lang.T("No answer came back. Ask again.")));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The question stays where it was; it isn't sent again with the next one, since it got no answer.
            Log.Write("ask", "Stopped before the answer came");
            foreach (var waiting in talk.Messages.OfType<AskThinking>().ToList()) talk.Messages.Remove(waiting);
            talk.Messages.Add(new AskStopped(Lang.T("Stopped. Ask again whenever you like.")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("ask", ex);
            foreach (var waiting in talk.Messages.OfType<AskThinking>().ToList()) talk.Messages.Remove(waiting);
            talk.Messages.Add(new AskProblem(Lang.T("Something went wrong while answering. Ask again.")));
        }
        finally { IsAnswering = false; _answering = null; longWait.Stop(); }
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

    private static readonly Regex Point = new(@"^\s*([-*•]|\d+[.)])\s+");
    private static readonly Regex LeadIn = new(@"^(?<lead>[^:.!?|]{3,28}:\s)(?<rest>.+)$");
    private static readonly Regex Marker = new(@"^\[\s*show\s*:\s*(?<what>[a-z]+)\s*\]$", RegexOptions.IgnoreCase);
    // "Next step: …", also as small models write it at times: in brackets, or as a point ("[next step: …]", "- Next step: …").
    private static readonly Regex NextStep = new(@"^(?:[-*•]\s*)?\[?\s*(next step|siguiente paso|pr[oó]ximo paso)\s*:\s*(?<rest>.+?)\s*\]?$", RegexOptions.IgnoreCase);
    private static readonly Regex TableRule = new(@"^\|?\s*:?-{2,}");

    /// <summary>
    /// The model's text as an answer: its first line as the plain statement, then paragraphs and points, RigSight's own
    /// numbers where a marker asks for them (<paramref name="blocks"/> reads them; a marker for a record it didn't look at
    /// shows nothing), its tables, the next step in a box, and the chips.
    /// </summary>
    public static AskAnswer ToAnswer(string text, IReadOnlyList<AiLookUp> lookUps, Func<string, IReadOnlyList<AiBlock>>? blocks = null)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Replace("**", "").Replace("__", "").TrimStart('#').Trim()).Where(l => l.Length > 0).ToList();
        string verdict = lines.Count > 0 && !Marker.IsMatch(lines[0]) && !lines[0].StartsWith('|') ? Point.Replace(lines[0], "") : "";
        if (verdict.Length > 0) lines.RemoveAt(0);
        var parts = new List<object>();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                var (opens, label) = Opens(lookUps);
                string step = next.Groups["rest"].Value;
                parts.Add(new AskNext(char.ToUpper(step[0], System.Globalization.CultureInfo.CurrentCulture) + step[1..], opens, label));
                continue;
            }
            bool isPoint = Point.IsMatch(l);
            string body = Point.Replace(l, "");
            parts.Add(LeadIn.Match(body) is { Success: true } lead ? new AskLine(lead.Groups["rest"].Value, isPoint, lead.Groups["lead"].Value) : new AskLine(body, isPoint));
        }
        return new AskAnswer(verdict, parts, null, Chips(lookUps));
    }

    /// <summary>The page the next step points to: where the records the answer leaned on are shown.</summary>
    private static (string? Page, string? Label) Opens(IReadOnlyList<AiLookUp> lookUps)
    {
        var tools = lookUps.Select(l => l.Tool).ToHashSet();
        if (tools.Contains("get_crashes")) return ("crashes", Lang.T("Open Crashes"));
        if (tools.Contains("get_changes")) return ("timeline", Lang.T("Open Timeline"));
        if (tools.Contains("get_game") || tools.Contains("list_games")) return ("games", Lang.T("Open Games"));
        if (tools.Contains("get_report")) return ("reports", Lang.T("Open Reports"));
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
                _ => null,
            };
            if (chip is not null && !chips.Any(c => c.Text == chip.Text)) chips.Add(chip);
        }
        return chips;
    }

    private AskConversation NewConversation()
    {
        var talk = new AskConversation { Title = "" };
        Conversations.Insert(0, talk);
        Regroup();
        return talk;
    }

    /// <summary>History's headings: Today, Yesterday, Earlier, newest first; blank conversations and non-matches left out.</summary>
    private void Regroup()
    {
        var shown = Conversations.Where(c => c.Title.Length > 0 && (HistorySearch.Length == 0 || TextMatch.Has(TextMatch.Words(HistorySearch), c.Title))).ToList();
        var today = DateTime.Today;
        Groups =
        [
            .. new[]
            {
                new AskGroup(Lang.T("TODAY"), [.. shown.Where(c => c.When >= today)]),
                new AskGroup(Lang.T("YESTERDAY"), [.. shown.Where(c => c.When >= today.AddDays(-1) && c.When < today)]),
                new AskGroup(Lang.T("EARLIER"), [.. shown.Where(c => c.When < today.AddDays(-1))]),
            }.Where(g => g.Conversations.Count > 0),
        ];
        OnPropertyChanged(nameof(HasHistory));
    }

    public bool HasHistory => Groups.Count > 0;


}

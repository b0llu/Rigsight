using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Ask;
using Rigsight.Core.Data;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A topic to pick when an answer wasn't what was meant.</summary>
public sealed record AskTopic(string Label, AskIntent Intent);

/// <summary>A question offered while typing: as it is asked, and with the words that match what's typed marked strong.</summary>
public sealed record AskSuggestion(string Text, string Rich)
{
    public static AskSuggestion Of(string text, string typed)
    {
        var words = typed.ToLowerInvariant().Split([' ', '?', ',', '.'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).ToList();
        var parts = text.Split(' ').Select(word =>
        {
            string bare = word.Trim('?', ',', '.', ':').ToLowerInvariant();
            return bare.Length > 0 && words.Any(w => bare.StartsWith(w, StringComparison.Ordinal)) ? $"**{word}**" : word;
        });
        // Neighbouring strong words read as one stretch.
        return new(text, string.Join(' ', parts).Replace("** **", " "));
    }
}

/// <summary>An answer's evidence under one heading ("Points to", "Ruled out"), or all of it under the answer's own.</summary>
public sealed record AskPointGroup(string? Title, IReadOnlyList<AskPoint> Points);

/// <summary>An app offered under an answer that didn't recognise a name.</summary>
public sealed record AskAppPick(AskMessage Message, AskAppChoice Choice);

/// <summary>A topic offered under one answer.</summary>
public sealed record AskChoice(AskMessage Message, AskTopic Topic);

/// <summary>One turn in the chat: what the user typed, or Riggy's answer to it.</summary>
public sealed partial class AskMessage : ObservableObject
{
    public bool IsUser { get; init; }
    public string Text { get; init; } = "";
    public AskAnswer? Answer { get; init; }

    /// <summary>The question this answers (asked again, as another topic, when corrected).</summary>
    public string Question { get; init; } = "";

    /// <summary>Under a taught answer: "Got it. Next time I'll know what you mean."</summary>
    public string? Learned { get; init; }

    public bool IsAnswer => Answer is not null;
    public bool HasFacts => Answer is { Facts.Count: > 0 };
    public bool HasPoints => Answer is { Points.Count: > 0 };
    public bool HasLinks => Answer is { Links.Count: > 0 };
    public bool HasFollowUps => Answer is { FollowUps.Count: > 0 };
    public bool HasRead => Answer is { Read.Length: > 0 };

    /// <summary>The answer can be corrected: it was read as something (not small talk, not "I didn't get that").</summary>
    public bool CanCorrect => Answer is { Understood: true, Query: not null };

    public RiggyMood Mood => Answer is { Understood: false, Query.Intent: AskIntent.None or AskIntent.Other } ? RiggyMood.Unsure : RiggyMood.Idle;

    public string? PointsTitle => Answer?.PointsTitle?.ToUpperInvariant();

    /// <summary>The evidence by what it says about the cause, in the answer's order; one group with the answer's title when it isn't sorted that way.</summary>
    public IReadOnlyList<AskPointGroup> PointGroups => field ??= Answer is null ? []
        : [.. Answer.Points.GroupBy(p => p.Group).Select((g, i) => new AskPointGroup(g.Key.Length > 0 ? g.Key.ToUpperInvariant() : i == 0 ? PointsTitle : null, [.. g]))];

    public bool HasTrail => Answer is { Trail.Count: > 0 };
    public bool HasCarried => Answer is { Carried.Length: > 0 };

    /// <summary>The apps to pick from when a name wasn't recognised.</summary>
    public IReadOnlyList<AskAppPick> AppPicks => field ??= Answer is null ? [] : [.. Answer.AppChoices.Select(c => new AskAppPick(this, c))];
    public bool HasAppPicks => Answer is { AppChoices.Count: > 0 };
    public string? TrailTitle => Answer?.TrailTitle?.ToUpperInvariant();

    /// <summary>The topics to pick from when this answer wasn't what was meant.</summary>
    public IReadOnlyList<AskChoice> Choices => field ??= [.. AskViewModel.Topics.Select(t => new AskChoice(this, t))];

    /// <summary>The topics are showing ("Not what I meant" was clicked).</summary>
    [ObservableProperty] private bool _correcting;
}

/// <summary>
/// Ask: Riggy's bubble in the corner of the window and the chat that opens from it. Questions are answered by
/// <see cref="AskEngine"/> from the PC's own records. The language model that reads a question is loaded when the
/// first one is sent, not before, and let go when the chat hasn't been used for a few minutes. What the user teaches
/// it (see <see cref="AskRouter.Learn"/>) is kept in a file on this PC.
/// </summary>
public sealed partial class AskViewModel : ObservableObject
{
    /// <summary>After this long without a question the model is unloaded (the next question loads it again).</summary>
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(5);

    /// <summary>How long Riggy "thinks" at least: an answer in 5 ms reads as a canned one.</summary>
    private static readonly TimeSpan MinThinking = TimeSpan.FromMilliseconds(420);

    private readonly SettingsModel _settings;
    private readonly Action<AskLink> _open;
    private readonly DispatcherTimer _idle;
    private AskEmbedder? _embedder;
    private AskEngine? _engine;
    private AskMemory? _memory;
    private AskContext? _context;
    /// <summary>The last question that wasn't understood: the next suggestion picked says what it meant.</summary>
    private AskQuery? _unread;

    public AskViewModel(SettingsModel settings, Action<AskLink> open)
    {
        (_settings, _open) = (settings, open);
        _idle = new DispatcherTimer { Interval = IdleUnload };
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            _embedder?.Unload();
        };
    }

    public ObservableCollection<AskMessage> Messages { get; } = [];

    public static IReadOnlyList<AskTopic> Topics { get; } =
    [
        new("Crashes", AskIntent.Crashes), new("What changed", AskIntent.Changes), new("Temperatures", AskIntent.Temps), new("Time on the PC", AskIntent.Usage),
        new("Most used apps", AskIntent.TopApps), new("Slowdowns", AskIntent.Slow), new("Memory", AskIntent.Memory), new("Internet", AskIntent.Network),
        new("Storage", AskIntent.Storage), new("Fans", AskIntent.Fans), new("How the PC is doing", AskIntent.Health), new("What's in this PC", AskIntent.Specs),
    ];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private List<string> _starters = [];

    /// <summary>Questions that fit what's being typed (see <see cref="AskSuggest"/>), and the one the arrow keys are on (-1: none).</summary>
    [ObservableProperty] private List<AskSuggestion> _suggestions = [];
    [ObservableProperty] private int _selectedSuggestion = -1;
    private List<AskApp> _apps = [];

    partial void OnInputChanged(string value)
    {
        Suggestions = IsThinking ? [] : [.. AskSuggest.For(value, _apps, DateTime.Now).Select(s => AskSuggestion.Of(s, value))];
        SelectedSuggestion = -1;
    }

    /// <summary>Up and down through the suggestions (past either end: back to what was typed).</summary>
    public bool MoveSuggestion(int by)
    {
        if (Suggestions.Count == 0) return false;
        int next = SelectedSuggestion + by;
        SelectedSuggestion = next < -1 ? Suggestions.Count - 1 : next >= Suggestions.Count ? -1 : next;
        return true;
    }

    /// <summary>The suggestion the arrow keys are on (or the first), put in the box to finish by hand.</summary>
    public bool TakeSuggestion()
    {
        if (Suggestions.Count == 0) return false;
        Input = Suggestions[Math.Max(0, SelectedSuggestion)].Text;
        return true;
    }

    /// <summary>
    /// Said in the chat when the model can't be used on this PC (the Microsoft runtime it needs is missing or old):
    /// Riggy still answers, reading questions by their words alone, and says what would make it understand more.
    /// </summary>
    public string? SimpleModeNote { get; } = AskEmbedder.FilesPresent && !AskEmbedder.RuntimeReady
        ? "Riggy needs a free Microsoft add-on to work better."
        : null;

    /// <summary>The bubble is shown (Settings can hide it).</summary>
    public bool IsEnabled => _settings.Current.ShowAsk;

    public bool IsEmpty => Messages.Count == 0;

    public RiggyMood Mood => IsThinking ? RiggyMood.Thinking : Messages.LastOrDefault(m => m.IsAnswer)?.Mood ?? RiggyMood.Idle;

    /// <summary>Raised when a message is added, so the view can scroll to it.</summary>
    public event Action? Added;

    /// <summary>Raised when the chat opens, so the view can put the cursor in the box.</summary>
    public event Action? Opened;

    public void Refresh() => OnPropertyChanged(nameof(IsEnabled));

    partial void OnIsThinkingChanged(bool value) => OnPropertyChanged(nameof(Mood));

    partial void OnIsOpenChanged(bool value)
    {
        if (!value) return;
        Opened?.Invoke();
        if (Starters.Count == 0) _ = LoadStartersAsync();
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Starts over: an empty chat, nothing to lean a follow-up on.</summary>
    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
        _context = null;
        _unread = null;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Mood));
    }

    [RelayCommand]
    private Task Send() => AskAsync(SelectedSuggestion >= 0 && SelectedSuggestion < Suggestions.Count ? Suggestions[SelectedSuggestion].Text : Input);

    /// <summary>A suggestion or a follow-up was clicked.</summary>
    [RelayCommand]
    private Task Suggest(string question) => AskAsync(question, picked: true);

    [RelayCommand]
    private void OpenLink(AskLink link) => _open(link);

    /// <summary>The link in <see cref="SimpleModeNote"/>: Settings, at Riggy's part, with the chat out of its way.</summary>
    [RelayCommand]
    private void GetRuntime()
    {
        IsOpen = false;
        _open(new AskLink("Settings", SettingsRiggy));
    }

    /// <summary>The page name that means "Settings, opened at Riggy's part" (see ShellViewModel.OpenFromAsk).</summary>
    public const string SettingsRiggy = "settings:riggy";

    /// <summary>
    /// "I can't find an app called X", then one picked: X is that app's name from now on (kept with what was taught),
    /// and the question is answered for it.
    /// </summary>
    [RelayCommand]
    private async Task PickApp(AskAppPick pick)
    {
        if (pick.Message.Answer?.Query?.UnknownApp is not { Length: > 0 } name || IsThinking) return;
        var (engine, memory) = Engine();
        memory.Aliases[name.ToLowerInvariant()] = pick.Choice.Exe;
        memory.Save();
        engine.Router.SetAliases(memory.Aliases);
        await AskAsync(pick.Message.Question, show: false, note: $"Got it: \u201C{name}\u201D is {pick.Choice.Name} from now on.");
    }

    [RelayCommand]
    private void Correct(AskMessage message) => message.Correcting = !message.Correcting;

    /// <summary>"Not what I meant", then a topic: the question is answered as that, and its wording remembered for next time.</summary>
    [RelayCommand]
    private async Task Teach(AskChoice choice)
    {
        if (choice.Message.Answer?.Query is not { } query) return;
        choice.Message.Correcting = false;
        await AskAsync(choice.Message.Question, show: false, meaning: choice.Topic.Intent, teach: query);
    }

    private (AskEngine Engine, AskMemory Memory) Engine()
    {
        if (_engine is not null && _memory is not null) return (_engine, _memory);
        _memory = AskMemory.Load();
        // The model's files ship with the app; without them (or if it can't load) questions are read by their words alone.
        _embedder = AskEmbedder.Available ? new AskEmbedder() : null;
        var router = new AskRouter(_embedder);
        router.SetLearned(_memory.Learned);
        router.SetAliases(_memory.Aliases);
        return (_engine = new AskEngine(router), _memory);
    }

    private async Task LoadStartersAsync()
    {
        var settings = _settings.Current.Clone();
        // No model needed for these: they're made from what's in the records.
        var (list, apps) = await Task.Run(() =>
        {
            try
            {
                using var db = RigsightDb.OpenReader();
                var engine = new AskEngine(new AskRouter(null));
                return db is null ? ([], []) : (engine.Starters(db, settings), engine.Apps(db, settings));
            }
            catch (Exception ex)
            {
                Log.Error("ask", ex);
                return (new List<string>(), new List<AskApp>());
            }
        });
        Starters = [.. list.Take(4)];
        _apps = apps;
    }

    /// <param name="show">Whether the question is added to the chat (not when an answer is being corrected: it is there already).</param>
    /// <param name="picked">The question was a suggestion clicked, not typed.</param>
    /// <param name="meaning">What the user said the question means (a correction).</param>
    /// <param name="teach">The question as it was first read, to remember against <paramref name="meaning"/>.</param>
    /// <param name="note">What to say under the answer about something just learned, when the caller knows it.</param>
    private async Task AskAsync(string text, bool show = true, bool picked = false, AskIntent meaning = AskIntent.None, AskQuery? teach = null, string? note = null)
    {
        text = text.Trim();
        if (text.Length == 0 || IsThinking) return;
        if (text.Length > 300) text = text[..300];
        Suggestions = [];
        if (show)
        {
            Input = "";
            Add(new AskMessage { IsUser = true, Text = text });
        }

        IsThinking = true;
        Added?.Invoke();
        _idle.Stop();
        var settings = _settings.Current.Clone();
        var context = meaning == AskIntent.None ? _context : null;
        var started = DateTime.UtcNow;
        var (answers, learned) = await Task.Run(() =>
        {
            try
            {
                var (engine, memory) = Engine();
                using var db = RigsightDb.OpenReader();
                if (db is null) return ([new AskAnswer { Lead = "The records can't be opened right now. Is the Rigsight agent running?", Understood = false }], (string?)null);
                // Several questions in one message are each answered; a correction or a clicked suggestion is one question.
                var all = meaning != AskIntent.None || picked ? [engine.Ask(db, settings, text, context, meaning: meaning)] : engine.AskAll(db, settings, text, context);
                return (all, Learn(engine, memory, all[^1], picked, meaning, teach));
            }
            catch (Exception ex)
            {
                Log.Error("ask", ex);
                return (new List<AskAnswer> { new() { Lead = "Something went wrong while I was working that out.", Understood = false } }, (string?)null);
            }
        });
        var answer = answers[^1];
        var left = MinThinking - (DateTime.UtcNow - started);
        if (left > TimeSpan.Zero) await Task.Delay(left);

        IsThinking = false;
        if (answer.Understood) _context = answer.Context;
        foreach (var each in answers)
            Add(new AskMessage { Answer = each, Question = each.Query?.Text ?? text, Learned = ReferenceEquals(each, answer) ? note ?? learned : null });
        _idle.Start();
    }

    /// <summary>
    /// What this exchange teaches: a correction is remembered as it is; a suggestion picked right after a question
    /// that wasn't understood says what that question meant. A question nothing could be made of is noted, so it can
    /// be seen (and one day answered).
    /// </summary>
    private string? Learn(AskEngine engine, AskMemory memory, AskAnswer answer, bool picked, AskIntent meaning, AskQuery? teach)
    {
        string? note = null;
        var router = engine.Router;
        var from = teach ?? (picked && answer.Understood ? _unread : null);
        if (from is not null && answer.Query is { Intent: not AskIntent.None } read && answer.Understood)
        {
            if (router.Learn(from, meaning != AskIntent.None ? meaning : read.Intent, read.Why) is not null)
            {
                memory.Learned = [.. router.Learned];
                note = teach is not null ? "Noted. Next time I'll read that the way you meant." : "Got it. Next time I'll know what you meant.";
            }
        }
        bool unread = answer is { Understood: false, Query.Intent: AskIntent.None };
        _unread = unread ? answer.Query : picked || answer.Understood ? null : _unread;
        if (unread || answer.Query is { Intent: AskIntent.Other }) memory.AddUnanswered(answer.Query!.Text);
        if (note is not null || unread || answer.Query is { Intent: AskIntent.Other }) memory.Save();
        return note;
    }

    private void Add(AskMessage message)
    {
        Messages.Add(message);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Mood));
        Added?.Invoke();
    }

    /// <summary>Forgets everything taught (Settings).</summary>
    public void Forget()
    {
        var (engine, memory) = Engine();
        engine.Router.Forget();
        engine.Router.SetAliases(new Dictionary<string, string>());
        memory.Learned = [];
        memory.Aliases.Clear();
        memory.Unanswered = [];
        memory.Save();
    }

    /// <summary>Questions nothing could be made of, newest last (for Settings: to copy and send on).</summary>
    public IReadOnlyList<string> Unanswered => (_memory ?? AskMemory.Load()).Unanswered;

    /// <summary>The names given to apps (for Settings).</summary>
    public IReadOnlyDictionary<string, string> Aliases => (_memory ?? AskMemory.Load()).Aliases;

    /// <summary>The wordings taught so far (for Settings).</summary>
    public IReadOnlyList<AskLearned> Learned => (_memory ?? AskMemory.Load()).Learned;

    /// <summary>Forgets one wording.</summary>
    public void Forget(AskLearned item)
    {
        var (engine, memory) = Engine();
        engine.Router.SetLearned(memory.Learned = [.. memory.Learned.Where(l => l != item)]);
        memory.Save();
    }
}

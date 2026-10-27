using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.ViewModels;

/// <summary>
/// The ask box in the title bar (approved mock-up "The ask box grows into a place to type", 2026-10-27): "Ask about your
/// PC…" in the middle of the bar; a click or the Ask shortcut grows it into a panel to type in, several lines if wanted,
/// with questions to start from and the last chats under it. Enter asks in a new chat and opens Ask, and the box empties:
/// it hands questions off, as a launcher does (2026-10-27: it kept showing the chat's name, which Ask's own header already
/// says). It always starts something new; Ask's own box carries on the chat that's open. What's typed and not asked stays,
/// shown in the box, for the next time.
/// </summary>
public sealed partial class AskViewModel
{
    /// <summary>What's typed in the panel, kept while it's closed until it's asked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarText), nameof(BarHasDraft), nameof(CanAskFromBar), nameof(BarShowCount), nameof(BarCount), nameof(BarSuggests))]
    private string _barDraft = "";

    /// <summary>The words in the box: no questions left, what's typed and not asked yet, or the invitation.</summary>
    public string BarText =>
        BarLocked ? Lang.T("No questions left · See plans")
        : BarDraft.Length > 0 ? BarDraft.ReplaceLineEndings(" ⏎ ")
        : Lang.T("Ask about your PC…");

    /// <summary>The panel's own invitation: a new chat beside the one open in Ask, or the usual words.</summary>
    public string BarPlaceholder => IsOpen ? Lang.T("Start a new chat…") : Lang.T("Ask about your PC…");

    /// <summary>The box's words are something typed (brighter than the invitation).</summary>
    public bool BarHasDraft => BarDraft.Length > 0 && !BarLocked;

    /// <summary>No questions left: the box says so and opens Plans instead of the panel.</summary>
    public bool BarLocked => IsEnabled && IsOutOfQuestions && Standing is not null;

    /// <summary>The panel's arrow: something to ask, not over the most a question may hold.</summary>
    public bool CanAskFromBar => BarDraft.Trim().Length > 0 && BarDraft.Length <= MaxQuestion;

    /// <summary>A long question's length, from 2,000 characters (as Ask's own box shows it near the most).</summary>
    public bool BarShowCount => BarDraft.Length >= 2_000;
    public string BarCount => BarDraft.Length > MaxQuestion
        ? Lang.T($"{BarDraft.Length:N0} characters, more than RigSight AI can read at once ({MaxQuestion:N0})")
        : Lang.T($"{BarDraft.Length:N0} characters");

    /// <summary>Questions and chats under the box: only while nothing is typed.</summary>
    public bool BarSuggests => BarDraft.Length == 0;

    /// <summary>The last two chats, to go back to from the panel.</summary>
    public IReadOnlyList<AskConversation> BarRecent => [.. Conversations.Where(c => c.Title.Length > 0 && !c.IsArchived).Take(2)];
    public bool HasBarRecent => BarRecent.Count > 0;

    /// <summary>The Ask shortcut from anywhere (Settings › Ask RigSight), for the panel's foot; empty while none is set.</summary>
    public string BarShortcut => Ai.Shortcut;
    public bool HasBarShortcut => Ai.Shortcut.Length > 0;

    /// <summary>In RigSight, Ctrl+K always opens the box (as a command bar's is in most apps).</summary>
    public const string InAppKey = "Ctrl+K";

    /// <summary>The key on the box's right: the shortcut from anywhere when one is set, else Ctrl+K (2026-10-27: none showed).</summary>
    public string BarKey => (HasBarShortcut ? Ai.Shortcut : InAppKey).Replace("+", " ");

    /// <summary>
    /// The questions left, asked of the relay when the panel is about to show them and nobody has yet (2026-10-27: the panel
    /// said "Checking your questions…" while only Ask itself asked, when it opened).
    /// </summary>
    public void ReadyBar()
    {
        if (IsEnabled && Ai.Mode == AiMode.RigSight && Standing is null && !CheckingPlan) _ = RefreshQuestionsAsync();
    }

    /// <summary>The window opens the panel (the Ask shortcut was pressed); it decides what that means for its own state.</summary>
    public event Action? BarOpenRequested;

    /// <summary>The Ask shortcut: the panel, or Ask's own box while Ask is open (ShellViewModel.Navigate).</summary>
    public void RequestBar() => BarOpenRequested?.Invoke();

    /// <summary>Opens Settings at the Ask shortcut (the panel's foot), to set or change it.</summary>
    public Action? OpenShortcutSetting { get; set; }
    [RelayCommand] private void SetBarShortcut() => (OpenShortcutSetting ?? OpenSettings)?.Invoke();

    /// <summary>Asks what's typed in a new chat, and opens Ask with it.</summary>
    [RelayCommand]
    private void AskFromBar()
    {
        if (!CanAskFromBar) return;
        string question = BarDraft.Trim();
        BarDraft = "";
        AskNew(question, "");
    }

    /// <summary>One of the questions under the box, asked in a new chat.</summary>
    [RelayCommand]
    private void AskStarterFromBar(AskStarter? starter)
    {
        if (starter is null) return;
        BarDraft = "";
        AskNew(starter.Text, starter.Page);
    }

    /// <summary>A recent chat from the panel, opened in Ask.</summary>
    [RelayCommand]
    private void OpenFromBar(AskConversation? talk)
    {
        if (talk is null || !IsEnabled) return;
        IsOpen = true;
        PickCommand.Execute(talk);
    }

    private static readonly HashSet<string> BarInputs =
        [nameof(IsOpen), nameof(Current), nameof(Standing), nameof(IsEnabled), nameof(QuestionsLeft), nameof(IsOutOfQuestions), nameof(ShowQuestionsLine), ""];
    private AskConversation? _barTalk;

    // The box follows what it shows: Ask opening or closing, the chat and its name, questions left, the shortcut.
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not { } name || !BarInputs.Contains(name)) return;
        if (!ReferenceEquals(_barTalk, Current))
        {
            if (_barTalk is not null) _barTalk.PropertyChanged -= BarTalkChanged;
            _barTalk = Current;
            if (_barTalk is not null) _barTalk.PropertyChanged += BarTalkChanged;
        }
        foreach (var n in new[] { nameof(BarText), nameof(BarHasDraft), nameof(BarLocked), nameof(BarRecent), nameof(HasBarRecent), nameof(BarShortcut), nameof(HasBarShortcut), nameof(BarKey), nameof(BarPlaceholder) })
            base.OnPropertyChanged(new PropertyChangedEventArgs(n));
    }

    private void BarTalkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AskConversation.Title)) return;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(BarText)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(BarHasDraft)));
    }

    /// <summary>The shortcut changed in Settings: the box and the panel's foot show the new one.</summary>
    public void ShortcutChanged()
    {
        OnPropertyChanged(nameof(BarShortcut));
        OnPropertyChanged(nameof(HasBarShortcut));
        OnPropertyChanged(nameof(BarKey));
    }
}

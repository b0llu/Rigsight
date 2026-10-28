using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A pack of RigSight AI questions on sale: how many, the price, and the price per question.</summary>
public sealed record AiPack(int Questions, string Price, string PerQuestion, bool IsBestValue = false)
{
    public string Name => Lang.T($"{Questions:N0} questions");
    public string Each => Lang.T($"{PerQuestion} a question");
}

/// <summary>A RigSight AI licence on this PC: none yet, in use here, or locked to another PC.</summary>
public enum AiLicenceState { None, Active, OtherPc }

/// <summary>
/// Settings › Ask RigSight: how the helper answers (Off, RigSight AI, my own key, Ollama on my PC), each choice's own settings,
/// what may be sent, the Ask button's place and shortcut, and how long conversations are kept.
/// </summary>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly AskViewModel _ask;
    private AiSettings Ai => _settings.Current.Ai;

    public AiSettingsViewModel(SettingsModel settings, AskViewModel ask)
    {
        _settings = settings;
        _ask = ask;
        Pages = new PageSwitchesViewModel(settings, PlanPages);
        Editor.Saved += InstructionsChanged;
        _ask.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AskViewModel.QuestionsLeft) or nameof(AskViewModel.HasLicence) or nameof(AskViewModel.Standing)) { ShowStanding(); Pages.Refresh(); }
            // A page switched in the Ask window's first-question window shows here too.
            else if (e.PropertyName == nameof(AskViewModel.PagesChanged)) Pages.Refresh();
        };
        // Ollama chosen before: its models are listed straight away, as if Connect had been pressed.
        if (Ai.Mode == AiMode.Ollama) _ = ConnectOllama();
        // Keys saved before: listed, and the models of the one in use asked for straight away.
        if (Ai.Mode == AiMode.OwnKey) ShowOwnKey();
    }

    private void Set(Action<AiSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        _settings.Update(s => change(s.Ai));
        OnPropertyChanged(property);
    }

    // ── How it answers ───────────────────────────────────────────────────
    /// <summary>"off", "rigsight", "own" or "ollama", for the segments.</summary>
    public string Mode
    {
        get => Ai.Mode switch { AiMode.RigSight => "rigsight", AiMode.OwnKey => "own", AiMode.Ollama => "ollama", _ => "off" };
        set
        {
            var mode = value switch { "rigsight" => AiMode.RigSight, "own" => AiMode.OwnKey, "ollama" => AiMode.Ollama, _ => AiMode.Off };
            if (mode == Ai.Mode) return;
            Log.Write("settings", $"Ask RigSight now answers with: {mode} (was {Ai.Mode})");
            ClosePickers();
            _settings.Update(s => s.Ai.Mode = mode);
            Pages.Refresh();
            OnPropertyChanged(string.Empty);
            // What was under way for the way of answering left behind stops with it: a purchase being waited for, an
            // Ollama download or connect (the 2026-10-27 audit: they went on for up to 10 minutes after Off).
            if (mode != AiMode.RigSight) StopWaiting();
            if (mode != AiMode.Ollama) StopOllamaWork();
            if (mode == AiMode.RigSight) _ = _ask.RefreshQuestionsAsync();
            if (mode == AiMode.OwnKey) ShowOwnKey();
            // Ollama picked: its models and the picks for this PC straight away, as if Connect had been pressed.
            if (mode == AiMode.Ollama) _ = ConnectOllama();
        }
    }

    public bool IsOff => Ai.Mode == AiMode.Off;
    public bool IsOn => !IsOff;
    public bool IsRigSight => Ai.Mode == AiMode.RigSight;
    public bool IsOwnKey => Ai.Mode == AiMode.OwnKey;
    public bool IsOllama => Ai.Mode == AiMode.Ollama;

    // ── The two notes ────────────────────────────────────────────────────
    /// <summary>Off sends nothing, so the note about what is sent waits until a way to answer is picked.</summary>
    public bool ShowSendingNote => !Ai.SendingNoteClosed && !IsOff;
    /// <summary>With Ollama everything stays on the PC; otherwise records go to the model that writes the answer.</summary>
    public string SendingWhere => IsOllama
        ? Ollama.IsOnThisPc(Ai.OllamaAddress)
            ? Lang.T("With Ollama on your PC, all of it stays on this PC.")
            : Lang.T($"With Ollama at {Ollama.Host(Ai.OllamaAddress)}, it goes to that computer, not to RigSight.")
        // True since OpenRouter's logging went on (2026-10-23): "keeps none of them" no longer was.
        : Lang.T("They go through RigSight's server to OpenRouter, the AI service, which keeps them for at least 3 months.");
    [RelayCommand] private void CloseSendingNote() { _settings.Update(s => s.Ai.SendingNoteClosed = true); OnPropertyChanged(nameof(ShowSendingNote)); }

    /// <summary>Ollama: what it misses without RigSight AI, until closed. Not on Your API key (the user's ruling, 2026-10-27).</summary>
    public bool ShowWebNote => IsOllama && !Ai.WebSearchNoteClosed;
    [RelayCommand] private void CloseWebNote() { _settings.Update(s => s.Ai.WebSearchNoteClosed = true); OnPropertyChanged(nameof(ShowWebNote)); }
    [RelayCommand] private void UseRigSight() => Mode = "rigsight";

    /// <summary>This PC's CPU and graphics card by their short names, for the web search note's first example.</summary>
    public string PartsLine => Lang.T($"The temperatures your {Parts.Cpu} and {Parts.Gpu} are built to run at.");

    // ── RigSight AI ──────────────────────────────────────────────────────
    /// <summary>
    /// This tab shown with RigSight AI picked: the licence card asks the relay where it stands, so a key bought or put on
    /// this PC shows without opening Ask first. Only once the person has started asking with RigSight AI: looking at the
    /// card alone never reaches the relay (the user's ruling, 2026-10-22: a PC nobody asks from leaves no trace there).
    /// </summary>
    public async void Shown()
    {
        // The relay out of reach: said on the card, so the questions shown aren't taken for what the relay says now.
        if (await _ask.RefreshQuestionsAsync() is { } problem && !WaitingForPurchase) { LicenceProblem = problem; LicenceProblemNeedsSupport = false; }
        // An old "couldn't check" goes once the relay answers; why the licence stopped (set from that answer) stays.
        else if (LicenceProblem is not null && _ask.Standing is not null && !LicenceProblemNeedsSupport && LicenceProblem != LicenceStopped) LicenceProblem = null;
    }

    /// <summary>The relay's word on this PC: its licence (in use here, or none) and the key to show; "another PC" comes from Activate.</summary>
    private void ShowStanding()
    {
        var s = _ask.Standing;
        if (s?.HasLicence == true && s.LicenceStatus == "active") Licence = AiLicenceState.Active;
        else if (Licence == AiLicenceState.Active) Licence = AiLicenceState.None;
        LicenceShown = s?.Key is { Length: >= 9 } key ? $"{key[..7]}-••••-{key[^4..]}" : "";
        // A licence turned off (by support) or refunded: said on the card, not left looking like no licence at all.
        string? was = LicenceStopped;
        LicenceStopped = s?.HasLicence == true ? s.LicenceStatus switch
        {
            "disabled" => Lang.T("Your licence is turned off, so this PC can't ask questions. Contact support with your key."),
            "refunded" => Lang.T("Your plan was refunded, so it has ended. See plans to start a new one."),
            _ => null,
        } : null;
        if (LicenceStopped is not null) { LicenceProblem = LicenceStopped; LicenceProblemNeedsSupport = s?.LicenceStatus == "disabled"; }
        else if (was is not null && LicenceProblem == was) { LicenceProblem = null; LicenceProblemNeedsSupport = false; }
        OnPropertyChanged(string.Empty);
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NoLicence), nameof(LicenceActive), nameof(LicenceElsewhere), nameof(ShowFreeLeft))] private AiLicenceState _licence;
    public bool NoLicence => Licence == AiLicenceState.None;
    public bool LicenceActive => Licence == AiLicenceState.Active;
    public bool LicenceElsewhere => Licence == AiLicenceState.OtherPc;

    [ObservableProperty] private string _licenceKey = "";
    /// <summary>The key as shown once it's in use here: its first and last groups, the rest hidden.</summary>
    [ObservableProperty] private string _licenceShown = "";

    public int QuestionsLeft => _ask.QuestionsLeft;
    /// <summary>What the total is made of while free questions remain ("100 bought · 9 free"); they're used first.</summary>
    public string QuestionsMadeOf => Lang.T($"{_ask.Standing?.LicenceQuestions ?? 0} bought · {_ask.Standing?.FreeLeft ?? 0} free");
    public bool HasQuestionsMadeOf => LicenceActive && _ask.Standing?.FreeLeft > 0;
    public bool IsOutOfQuestions => QuestionsLeft <= 0;
    private int Free => _ask.Standing?.FreeLeft ?? QuestionsLeft;
    /// <summary>The free questions line: hidden while the relay can't be asked, since the number would only be a guess.</summary>
    public bool ShowFreeLeft => NoLicence && (_ask.Standing is not null || !HasLicenceProblem) && _ask.Standing?.LicenceStatus != "disabled";

    /// <summary>Why the PC's own licence stopped (turned off, refunded), from the relay; null while it works or there's none.</summary>
    private string? LicenceStopped { get; set; }
    public string FreeLeft => Free == 1 ? Lang.T("1 free question left to try RigSight AI.") : Lang.T($"{Free} free questions left to try RigSight AI.");

    /// <summary>Why the key typed in can't be used, or why buying can't open; under the key.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLicenceProblem), nameof(ShowFreeLeft))] private string? _licenceProblem;
    public bool HasLicenceProblem => LicenceProblem is not null;
    /// <summary>The relay turned the key down (not found, refunded, turned off): only support can sort that out.</summary>
    [ObservableProperty] private bool _licenceProblemNeedsSupport;

    /// <summary>One-time packs that never run out (the user's pick, 2026-10-21): a bigger pack is cheaper a question.</summary>
    public IReadOnlyList<AiPack> Packs { get; } = [new(100, "$2.99", "3¢"), new(300, "$6.99", "2.3¢"), new(1000, "$17.99", "1.8¢", IsBestValue: true)];

    /// <summary>Activating checks the key with the relay and puts it on this PC; one in use on another PC is refused.</summary>
    [RelayCommand]
    private async Task Activate()
    {
        LicenceProblem = null;
        LicenceProblemNeedsSupport = false;
        if (LicenceKey.Trim().Length == 0) { LicenceProblem = Lang.T("Type or paste your licence key first."); return; }
        var (status, problem, reason) = await Relay.ActivateAsync(LicenceKey);
        if (status is not null)
        {
            LicenceKey = "";
            _ask.Show(status);
            return;
        }
        if (reason == "other_pc") Licence = AiLicenceState.OtherPc;
        LicenceProblem = reason == "other_pc" ? null : problem;
        LicenceProblemNeedsSupport = reason is not null and not "other_pc";
    }

    /// <summary>Buying opens the pack's checkout, which carries this PC's id, so the questions land here with no key to type.</summary>
    [RelayCommand(CanExecute = nameof(CanBuy))]
    private async Task Buy(AiPack? pack)
    {
        LicenceProblem = null;
        LicenceProblemNeedsSupport = false;
        if (pack is null) return;
        if (_ask.Standing is null && await _ask.RefreshQuestionsAsync(theyAsked: true) is { } unreachable)
        {
            // Offline, or the relay down: that's why, not "buying isn't open".
            Log.Write("ai-settings", $"Buy {pack.Questions}: the relay couldn't be asked for the checkout: {unreachable}");
            LicenceProblem = unreachable;
            return;
        }
        var url = _ask.Standing?.Packs.FirstOrDefault(p => p.Questions == pack.Questions)?.Url;
        if (string.IsNullOrEmpty(url))
        {
            Log.Write("ai-settings", $"Buy {pack.Questions}: the relay has no checkout for it yet");
            LicenceProblem = Lang.T("Buying isn't open yet. Try again later.");
            return;
        }
        Log.Write("ai-settings", $"Opening the checkout for {pack.Questions} questions");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("ai-settings", ex); return; }
        _ = WaitForPurchaseAsync(pack.Questions);
    }

    private bool CanBuy(AiPack? pack) => !WaitingForPurchase;

    /// <summary>After Buy, until the purchase lands here, the person cancels, or <see cref="PurchaseWait"/> passes.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(BuyCommand))] private bool _waitingForPurchase;
    private CancellationTokenSource? _purchaseWait;

    /// <summary>Paying happens in the browser, often slowly (a card form, a bank's check), so the app waits this long.</summary>
    private static readonly TimeSpan PurchaseWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PurchaseCheckEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The checkout carries this PC's id, so a paid order adds its questions to this PC's licence on the relay. The app
    /// asks the relay every few seconds and shows the new standing as soon as the licence holds more than before.
    /// </summary>
    private async Task WaitForPurchaseAsync(int questions)
    {
        _purchaseWait?.Cancel();
        var cts = _purchaseWait = new CancellationTokenSource(PurchaseWait);
        var before = _ask.Standing?.LicenceQuestions ?? 0;
        WaitingForPurchase = true;
        Log.Write("ai-settings", $"Waiting for the {questions}-question purchase: the licence holds {before} now");
        try
        {
            while (true)
            {
                await Task.Delay(PurchaseCheckEvery, cts.Token);
                var (status, problem) = await Relay.StatusAsync(cts.Token);
                if (status is null) { Log.Write("ai-settings", $"Purchase check: {problem}"); continue; }
                if (status.LicenceQuestions <= before) continue;
                Log.Write("ai-settings", $"Purchase landed: the licence went from {before} to {status.LicenceQuestions} questions");
                _ask.Show(status);
                break;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Write("ai-settings", cts.IsCancellationRequested && _purchaseWait != cts ? "Purchase wait replaced by a new one"
                : $"Purchase wait ended without a purchase (cancelled or {PurchaseWait.TotalMinutes:0} minutes passed)");
        }
        finally
        {
            if (_purchaseWait == cts) { _purchaseWait = null; WaitingForPurchase = false; }
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void StopWaiting() => _purchaseWait?.Cancel();

    [RelayCommand]
    private void CopyLicence()
    {
        if (_ask.Standing?.Key is not { } key) return;
        try { System.Windows.Clipboard.SetText(key); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Log.Error("ai-settings", ex); }
    }

    /// <summary>Where a licence is moved to a new PC (by hand, on request).</summary>
    public const string SupportEmail = "support@rigsight.app";

    /// <summary>Opens a new email to support in the person's mail app, its subject saying what it's about.</summary>
    [RelayCommand]
    private void EmailSupport()
    {
        // What it's about: a key in use elsewhere, a licence turned off or refunded, a key that wasn't found.
        var subject = Uri.EscapeDataString(_ask.Standing?.LicenceStatus switch
        {
            "disabled" => Lang.T("My RigSight AI licence is turned off"),
            _ when LicenceElsewhere || LicenceActive => Lang.T("Move my RigSight AI licence to this PC"),
            _ => Lang.T("My RigSight AI licence key isn't working"),
        });
        // The key typed in (one locked to another PC, or not found) or the PC's own, whole: support looks it up.
        var key = LicenceKey.Trim().Length > 0 ? LicenceKey.Trim() : _ask.Standing?.Key ?? LicenceShown;
        var body = Uri.EscapeDataString(Lang.T($"My licence key is {key}"));
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://mail.google.com/mail/?view=cm&fs=1&to={Uri.EscapeDataString(SupportEmail)}&su={subject}&body={body}") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("ai-settings", ex); }
    }

    // ── Ollama on my PC ──────────────────────────────────────────────────
    public string OllamaAddress
    {
        get => Ai.OllamaAddress;
        set
        {
            if ((value ?? "") == Ai.OllamaAddress) return;
            Set(a => a.OllamaAddress = value ?? "");
            OnPropertyChanged(nameof(SendingWhere));
            // Another address: checked by itself (the box already waits for typing to stop).
            OllamaConnected = false;
            OllamaAnswering = false;
            OllamaMissing = false;
            ShowRows(null);
            _ = ConnectOllama();
        }
    }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OllamaModel))] private bool _ollamaConnected;

    /// <summary>The model in use, for the Ask window's wait text and the log.</summary>
    public string OllamaModel => ChosenOllama;

    // ── Instructions (my own key and Ollama) ─────────────────────────────
    /// <summary>The box where they change them, over the window; the shell shows it.</summary>
    public InstructionsEditorViewModel Editor { get; } = new();

    private AiMode EditMode => IsOllama ? AiMode.Ollama : AiMode.OwnKey;

    /// <summary>Only my own key and Ollama: RigSight AI's instructions live on the relay.</summary>
    public bool CanEditInstructions => IsOwnKey || IsOllama;
    public bool HasOwnInstructions => CanEditInstructions && AiInstructions.Custom(EditMode) is not null;
    public bool InstructionsOutdated => CanEditInstructions && AiInstructions.DefaultChangedSince(EditMode);

    public string InstructionsNote => HasOwnInstructions && AiInstructions.SavedOn(EditMode) is { } on
        ? Lang.T($"Your own full instructions, saved {on:d MMM}.")
        : Lang.T("RigSight's own instructions.");

    [RelayCommand] private void EditInstructions() => Editor.Open(EditMode);

    [RelayCommand] private void ResetInstructions() { AiInstructions.Reset(EditMode); InstructionsChanged(); }

    [RelayCommand] private void KeepInstructions() { AiInstructions.Keep(EditMode); InstructionsChanged(); }

    private void InstructionsChanged()
    {
        foreach (var name in new[] { nameof(HasOwnInstructions), nameof(InstructionsOutdated), nameof(InstructionsNote) })
            OnPropertyChanged(name);
    }

    // ── What answers can read ────────────────────────────────────────────
    /// <summary>A tile for each page answers may use (the plan's, with RigSight AI; every page with own key or Ollama).</summary>
    public PageSwitchesViewModel Pages { get; }

    /// <summary>RigSight AI's plans (set by the shell): your plan here, the Plans screen, the dialogs.</summary>
    public PlansViewModel? Plans
    {
        get => _plans;
        set { _plans = value; Pages.OpenPlans = () => _plans?.Open(); OnPropertyChanged(); }
    }
    private PlansViewModel? _plans;

    /// <summary>The pages the plan opens: the relay's word, Free's until it has said; null (every page) off RigSight AI.</summary>
    private IReadOnlyCollection<string>? PlanPages() => Ai.Mode != AiMode.RigSight ? null : _ask.Standing?.Plan?.Pages ?? AiPages.DefaultFor(0);

    // ── The Ask button and conversations ─────────────────────────────────
    /// <summary>The AI buttons on the pages, shown by default; off hides every one at once (AiGate).</summary>
    public bool PageButtons { get => Ai.PageButtons; set => Set(a => a.PageButtons = value); }
    public string Place { get => Ai.Place == AskPlace.Sidebar ? "sidebar" : "top"; set => Set(a => a.Place = value == "sidebar" ? AskPlace.Sidebar : AskPlace.TopBar); }
    // The Ask shortcut: opens RigSight on the Ask window from anywhere (the agent holds it, only while the AI is on).
    // Set listens for the next keys pressed on the page (SettingsView), as the overlay's shortcut does (2026-10-26).
    public string Shortcut => Ai.Shortcut.Length > 0 ? Ai.Shortcut : Lang.T("Not set");
    public bool HasShortcut => Ai.Shortcut.Length > 0;
    public string ShortcutText => IsRecordingShortcut ? Lang.T("Press the new shortcut…") : Shortcut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutText))]
    private bool _isRecordingShortcut;

    /// <summary>How to press one while listening, or why the last press wasn't taken.</summary>
    [ObservableProperty] private string _shortcutHint = "";

    /// <summary>Another program already holds the shortcut (the agent says so), so it does nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutWarning))]
    private bool _shortcutTaken;

    public string ShortcutWarning => ShortcutTaken && HasShortcut ? Lang.T($"Another program already uses {Ai.Shortcut}, so it does nothing here. Pick another.") : "";

    [RelayCommand]
    private void SetShortcut()
    {
        IsRecordingShortcut = true;
        ShortcutHint = Lang.T("Hold Ctrl, Alt or Shift and press a key. Esc cancels.");
    }

    public void CancelShortcut()
    {
        IsRecordingShortcut = false;
        ShortcutHint = "";
    }

    /// <summary>Each key pressed while listening (a Windows virtual-key code); modifier-only presses never reach here.</summary>
    public void RecordShortcut(int virtualKey, HotkeyModifiers modifiers)
    {
        var hotkey = new Hotkey(modifiers, virtualKey);
        if (!Hotkey.IsSupportedKey(virtualKey)) { ShortcutHint = Lang.T("That key can't be used. Try a letter, a number or an F-key."); return; }
        if (!hotkey.IsValid) { ShortcutHint = Lang.T("Add Ctrl, Alt or Shift, so the shortcut doesn't clash with your games."); return; }
        if (_settings.Current.Overlay is { Enabled: true } overlay && string.Equals(overlay.Hotkey, hotkey.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            ShortcutHint = Lang.T("That's the overlay's shortcut. Pick another.");
            return;
        }
        ShortcutTaken = false;
        Set(a => a.Shortcut = hotkey.ToString());
        CancelShortcut();
        ShortcutChanged();
    }

    [RelayCommand]
    private void ClearShortcut()
    {
        ShortcutTaken = false;
        // Cleared on purpose: Ctrl+K is never handed back (SettingsStore.Normalize).
        Set(a => { a.Shortcut = ""; a.ShortcutGiven = true; });
        CancelShortcut();
        ShortcutChanged();
    }

    private void ShortcutChanged()
    {
        foreach (var name in new[] { nameof(Shortcut), nameof(HasShortcut), nameof(ShortcutText), nameof(ShortcutWarning) }) OnPropertyChanged(name);
    }

    public int KeepConversationDays
    {
        get => Ai.KeepConversationDays;
        set { Set(a => a.KeepConversationDays = value); _ask.Keep(); }
    }

    [RelayCommand]
    private void ClearConversations()
    {
        if (!_ask.HasHistory) return;
        var answer = System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,
            Lang.T("This permanently deletes every Ask RigSight conversation on this PC, pinned and archived ones too.\n\nDelete all conversations?"),
            Lang.T("Clear conversations"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (answer == System.Windows.MessageBoxResult.Yes) _ask.ClearAll();
    }

}

/// <summary>This PC's CPU and graphics card by the short names people know ("Ryzen 7 5700X3D", "RTX 3080 Ti"), read once.</summary>
public static class Parts
{
    private static readonly Lazy<(string Cpu, string Gpu)> Names = new(Read);
    public static string Cpu => Names.Value.Cpu;
    public static string Gpu => Names.Value.Gpu;

    private static (string, string) Read()
    {
        string cpu = Lang.T("CPU"), gpu = Lang.T("graphics card");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name) cpu = Short(name);
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
            {
                using var adapter = cls!.OpenSubKey(sub);
                if (adapter?.GetValue("DriverDesc") is string desc && !desc.Contains("Basic") && !desc.Contains("Virtual") && !desc.Contains("Remote") && !desc.Contains("Indirect"))
                {
                    gpu = Short(desc);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return (cpu, gpu);
    }

    /// <summary>"AMD Ryzen 7 5700X3D 8-Core Processor" → "Ryzen 7 5700X3D"; "NVIDIA GeForce RTX 3080 Ti" → "RTX 3080 Ti".</summary>
    internal static string Short(string name)
    {
        var words = name.Replace("(R)", "").Replace("(TM)", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        words.RemoveAll(w => w is "AMD" or "Intel" or "NVIDIA" or "GeForce" or "Radeon" or "Processor" or "CPU" or "Graphics" || w.EndsWith("-Core", StringComparison.OrdinalIgnoreCase));
        if (words.IndexOf("@") is var at and >= 0) words = words[..at];
        return words.Count > 0 ? string.Join(' ', words) : name;
    }
}

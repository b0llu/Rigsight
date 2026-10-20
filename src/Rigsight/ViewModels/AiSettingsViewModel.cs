using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A pack of RigSight AI questions on sale: how many, the price, and the price per question.</summary>
public sealed record AiPack(int Questions, string Price)
{
    public string Name => Lang.T($"{Questions} questions");
    public string Each => Lang.T("10¢ a question");
}

/// <summary>A model a service offers for the person's key, with about what a question costs where the service says so.</summary>
public sealed record AiModelOption(string Name, string? PerQuestion, bool Tested)
{
    public string? PriceText => PerQuestion is { } p ? Lang.T($"about {p} a question") : null;
}

/// <summary>A model Ollama has, its size, and whether it can ask RigSight for records (models that can't are greyed out).</summary>
public sealed record OllamaModelOption(string Name, string Size, bool CanLookUp);

/// <summary>A heading in a model list ("TESTED WITH RIGSIGHT", "OTHER MODELS (NOT TESTED)") and its models.</summary>
public sealed record AiModelGroup(string Heading, IReadOnlyList<AiModelOption> Models);

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
        _ask.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AskViewModel.QuestionsLeft) or nameof(AskViewModel.HasLicence)) OnPropertyChanged(string.Empty); };
        // Ollama chosen before: its models are listed straight away, as if Connect had been pressed.
        if (Ai.Mode == AiMode.Ollama) _ = ConnectOllamaCommand.ExecuteAsync(null);
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
            ClosePickers();
            _settings.Update(s => s.Ai.Mode = mode);
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>
    /// Closes the model lists. Each is a window of its own, so one stays on screen when the box it opened from goes
    /// (another way to answer picked, another tab or page).
    /// </summary>
    public void ClosePickers()
    {
        IsPickingModel = false;
        IsPickingOllama = false;
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
        ? Lang.T("With Ollama on your PC, all of it stays on this PC.")
        : Lang.T("They go to the AI model that writes the answer, and RigSight AI keeps none of them.");
    [RelayCommand] private void CloseSendingNote() { _settings.Update(s => s.Ai.SendingNoteClosed = true); OnPropertyChanged(nameof(ShowSendingNote)); }

    /// <summary>My own key or Ollama: what they miss without RigSight AI, until closed.</summary>
    public bool ShowWebNote => (IsOwnKey || IsOllama) && !Ai.WebSearchNoteClosed;
    [RelayCommand] private void CloseWebNote() { _settings.Update(s => s.Ai.WebSearchNoteClosed = true); OnPropertyChanged(nameof(ShowWebNote)); }
    [RelayCommand] private void UseRigSight() => Mode = "rigsight";

    /// <summary>This PC's CPU and graphics card by their short names, for the web search note's first example.</summary>
    public string PartsLine => Lang.T($"The temperatures your {Parts.Cpu} and {Parts.Gpu} are built to run at.");

    // ── RigSight AI ──────────────────────────────────────────────────────
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NoLicence), nameof(LicenceActive), nameof(LicenceElsewhere))] private AiLicenceState _licence;
    public bool NoLicence => Licence == AiLicenceState.None;
    public bool LicenceActive => Licence == AiLicenceState.Active;
    public bool LicenceElsewhere => Licence == AiLicenceState.OtherPc;

    [ObservableProperty] private string _licenceKey = "";
    /// <summary>The key as shown once it's in use here: its first and last groups, the rest hidden.</summary>
    [ObservableProperty] private string _licenceShown = "";

    public int QuestionsLeft => _ask.QuestionsLeft;
    public bool IsOutOfQuestions => QuestionsLeft <= 0;
    public string FreeLeft => QuestionsLeft == 1 ? Lang.T("1 free question left to try RigSight AI.") : Lang.T($"{QuestionsLeft} free questions left to try RigSight AI.");

    public IReadOnlyList<AiPack> Packs { get; } = [new(20, "$1.99"), new(50, "$4.99"), new(100, "$9.99")];

    /// <summary>Activating checks the key with RigSight AI and locks it to this PC; wired with the relay.</summary>
    [RelayCommand] private void Activate() { }
    /// <summary>Buying opens the pack's page on rigsight.app; wired with the shop.</summary>
    [RelayCommand] private void Buy(AiPack? pack) { }
    [RelayCommand] private void CopyLicence() { }

    /// <summary>Where a licence is moved to a new PC (by hand, on request).</summary>
    public const string SupportEmail = "samantdhruv@gmail.com";

    /// <summary>Opens a new email to support in the person's mail app, its subject saying what it's about.</summary>
    [RelayCommand]
    private void EmailSupport()
    {
        var subject = Uri.EscapeDataString(Lang.T("RigSight AI: move my licence to this PC"));
        // The key typed in (a key locked to another PC) or, once in use here, the key on this PC.
        var key = LicenceKey.Trim().Length > 0 ? LicenceKey.Trim() : LicenceShown;
        var body = Uri.EscapeDataString(Lang.T($"My licence key: {key}"));
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"mailto:{SupportEmail}?subject={subject}&body={body}") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("ai-settings", ex); }
    }

    // ── My own key ───────────────────────────────────────────────────────
    public IReadOnlyList<string> Services { get; } = ["OpenRouter", "OpenAI", "Anthropic", "Google Gemini", "DeepSeek", "xAI", "Mistral", "Groq", Lang.T("Custom (OpenAI-compatible)")];

    public string Service
    {
        get => Ai.Service;
        set
        {
            if (value is null || value == Ai.Service) return;
            _settings.Update(s => { s.Ai.Service = value; s.Ai.Model = ""; });
            // Another service: its key has to be tested before any of its models show.
            _models = [];
            KeyChecked = false;
            ModelSearch = "";
            OnPropertyChanged(string.Empty);
        }
    }

    public bool IsCustom => Ai.Service == Services[^1];
    public string CustomAddress { get => Ai.CustomAddress; set => Set(a => a.CustomAddress = value ?? ""); }

    /// <summary>The key goes to Windows' own protected store when tested; never into settings.json.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(TestKeyCommand))] private string _apiKey = "";

    /// <summary>A different key hasn't been tested: its models wait for Test.</summary>
    partial void OnApiKeyChanged(string value)
    {
        if (!KeyChecked) return;
        _models = [];
        KeyChecked = false;
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ModelPrice));
    }
    /// <summary>The key was tried and the service listed its models.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Model), nameof(ModelPrice))] private bool _keyChecked;
    public string KeyWorks => Lang.T("Works · its models are loaded");
    private bool CanTestKey() => ApiKey.Trim().Length > 0;

    /// <summary>Tests the key with the service, which then lists the models it offers for it; wired with the services.</summary>
    [RelayCommand(CanExecute = nameof(CanTestKey))]
    private void TestKey() { }

    private List<AiModelOption> _models = [];

    /// <summary>The model picked, or, until the key is tested, what has to happen before its models can be listed.</summary>
    public string Model => !KeyChecked ? Lang.T("Test your API key to see the models it supports")
        : Ai.Model.Length > 0 ? Ai.Model : _models.FirstOrDefault()?.Name ?? "";
    public bool HasModels => _models.Count > 0;
    /// <summary>OpenRouter publishes its prices: about what a question costs on the picked model.</summary>
    public string? ModelPrice => KeyChecked && _models.FirstOrDefault(m => m.Name == Model)?.PerQuestion is { } p
        ? Lang.T($"About {p} a question at {Ai.Service}'s prices.") : null;

    [ObservableProperty] private bool _isPickingModel;
    [ObservableProperty] private string _modelSearch = "";
    partial void OnModelSearchChanged(string value) => OnPropertyChanged(nameof(ModelGroups));

    public IReadOnlyList<AiModelGroup> ModelGroups
    {
        get
        {
            var words = TextMatch.Words(ModelSearch);
            var shown = _models.Where(m => TextMatch.Has(words, m.Name)).ToList();
            return [.. new[]
            {
                new AiModelGroup(Lang.T("TESTED WITH RIGSIGHT"), [.. shown.Where(m => m.Tested)]),
                new AiModelGroup(Lang.T("OTHER MODELS (NOT TESTED)"), [.. shown.Where(m => !m.Tested)]),
            }.Where(g => g.Models.Count > 0)];
        }
    }

    [RelayCommand] private void OpenModels() { if (HasModels) IsPickingModel = true; }

    [RelayCommand]
    private void PickModel(AiModelOption? model)
    {
        if (model is null) return;
        Set(a => a.Model = model.Name, nameof(Model));
        OnPropertyChanged(nameof(ModelPrice));
        IsPickingModel = false;
        ModelSearch = "";
    }

    // ── Ollama on my PC ──────────────────────────────────────────────────
    public string OllamaAddress
    {
        get => Ai.OllamaAddress;
        set
        {
            if ((value ?? "") == Ai.OllamaAddress) return;
            Set(a => a.OllamaAddress = value ?? "");
            // Another address: Connect again before its models show.
            _ollamaModels = [];
            OllamaConnected = false;
            OllamaProblem = null;
        }
    }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OllamaStatus), nameof(OllamaModel))] private bool _ollamaConnected;
    private List<OllamaModelOption> _ollamaModels = [];
    public string OllamaStatus => _ollamaModels.Count == 1 ? Lang.T("Connected · 1 model on it") : Lang.T($"Connected · {_ollamaModels.Count} models on it");
    /// <summary>Why Connect found no models: Ollama not answering at the address, or nothing downloaded yet.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOllamaProblem))] private string? _ollamaProblem;
    public bool HasOllamaProblem => OllamaProblem is not null;

    /// <summary>Asks Ollama at the address for its models and what each can do.</summary>
    [RelayCommand]
    private async Task ConnectOllama()
    {
        var found = await Ollama.ModelsAsync(Ai.OllamaAddress);
        _ollamaModels = found is null ? [] : [.. found
            .OrderByDescending(m => m.CanLookUp).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new OllamaModelOption(m.Name, Units.Bytes(m.Bytes), m.CanLookUp))];
        OllamaProblem = found is null ? Lang.T($"Ollama isn't answering at {Ollama.Normalise(Ai.OllamaAddress)}. Check that it's running and the address is right.")
            : found.Count == 0 ? Lang.T("Ollama is running but has no models yet. Download one in Ollama, then press Connect again.")
            : null;
        OllamaConnected = _ollamaModels.Count > 0;
        // A model picked earlier that Ollama no longer has: the first one that can look things up instead.
        if (OllamaConnected && Ai.OllamaModel.Length > 0 && !_ollamaModels.Any(m => m.Name == Ai.OllamaModel && m.CanLookUp))
            Set(a => a.OllamaModel = "", nameof(OllamaModel));
        OnPropertyChanged(nameof(OllamaModel));
        OnPropertyChanged(nameof(OllamaShown));
        OnPropertyChanged(nameof(OllamaStatus));
    }

    /// <summary>The model picked (shown in the search box while nothing is typed), or what has to happen first.</summary>
    public string OllamaModel => !OllamaConnected ? Lang.T("Connect to see the models on it")
        : Ai.OllamaModel.Length > 0 ? Ai.OllamaModel : _ollamaModels.FirstOrDefault(m => m.CanLookUp)?.Name ?? "";
    [ObservableProperty] private bool _isPickingOllama;
    [ObservableProperty] private string _ollamaSearch = "";
    partial void OnOllamaSearchChanged(string value) => OnPropertyChanged(nameof(OllamaShown));
    public IReadOnlyList<OllamaModelOption> OllamaShown => [.. _ollamaModels.Where(m => TextMatch.Has(TextMatch.Words(OllamaSearch), m.Name))];

    [RelayCommand] private void OpenOllamaModels() { if (_ollamaModels.Count > 0) IsPickingOllama = true; }

    [RelayCommand]
    private void PickOllama(OllamaModelOption? model)
    {
        if (model is not { CanLookUp: true }) return;
        Set(a => a.OllamaModel = model.Name, nameof(OllamaModel));
        IsPickingOllama = false;
        OllamaSearch = "";
    }

    // ── What RigSight sends ──────────────────────────────────────────────
    public bool ShareCrashes { get => Ai.ShareCrashes; set => Set(a => a.ShareCrashes = value); }
    public bool ShareHeat { get => Ai.ShareHeat; set => Set(a => a.ShareHeat = value); }
    public bool ShareApps { get => Ai.ShareApps; set => Set(a => a.ShareApps = value); }
    public bool ShareChanges { get => Ai.ShareChanges; set => Set(a => a.ShareChanges = value); }
    public bool ShareMemory { get => Ai.ShareMemory; set => Set(a => a.ShareMemory = value); }
    public bool ShareNetwork { get => Ai.ShareNetwork; set => Set(a => a.ShareNetwork = value); }
    public bool ShareParts { get => Ai.ShareParts; set => Set(a => a.ShareParts = value); }

    // ── The Ask button and conversations ─────────────────────────────────
    public string Place { get => Ai.Place == AskPlace.Sidebar ? "sidebar" : "top"; set => Set(a => a.Place = value == "sidebar" ? AskPlace.Sidebar : AskPlace.TopBar); }
    public string Shortcut => Ai.Shortcut.Length > 0 ? Ai.Shortcut : Lang.T("Not set");
    public bool HasShortcut => Ai.Shortcut.Length > 0;
    /// <summary>Setting a shortcut listens for the next keys pressed; wired with the window's key handling.</summary>
    [RelayCommand] private void SetShortcut() { }

    public int KeepConversationDays { get => Ai.KeepConversationDays; set => Set(a => a.KeepConversationDays = value); }
    [RelayCommand] private void ClearConversations() { }

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

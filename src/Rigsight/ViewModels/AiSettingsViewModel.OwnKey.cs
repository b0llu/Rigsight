using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;

namespace Rigsight.ViewModels;

/// <summary>A saved key in Settings › Ask RigSight: its name, its last few characters, a fact, and whether answers use it.</summary>
public sealed partial class SavedKeyRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Service { get; init; }
    public required string Name { get; init; }
    /// <summary>"sk-or-…a41f", or the address of a server.</summary>
    public required string Mask { get; init; }
    public bool IsServer { get; init; }
    /// <summary>The letters on its badge ("OR"), and the badge's colour.</summary>
    public string Badge => ServiceLook.Badge(Service);
    public string BadgeColor => ServiceLook.Color(Service);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Line))] private string _fact = "";
    public required string Used { get; init; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUse), nameof(ShowInUse))] private bool _inUse;
    /// <summary>Connect just added it: "Added just now" where "In use" goes, for a few seconds (approved mock-up 2026-10-28).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowInUse))] private bool _justAdded;
    public bool ShowInUse => InUse && !JustAdded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotAsking))] private bool _askingRemove;
    public bool NotAsking => !AskingRemove;
    public bool CanUse => !InUse;
    /// <summary>What sits after the key's characters: a fact and when it was last used.</summary>
    public string Line => Fact.Length > 0 ? $"{Fact} · {Used}" : Used;
    public string RemoveQuestion => Lang.T($"Remove {Name} ({Mask}) from this PC? This can't be undone.");
}

/// <summary>One model in the table: what the key's own model list says about it, and nothing else (the user's ruling, 2026-10-27).</summary>
public sealed partial class ModelRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>"Best answers", "Balanced" or "Cheapest", in the person's language; empty for the rest.</summary>
    public required string Tier { get; init; }
    public bool HasTier => Tier.Length > 0;
    public required string Maker { get; init; }
    public required string Context { get; init; }
    public required string Cost { get; init; }
    /// <summary>"Oct 2026": when the service says it came out (Anthropic and OpenAI do).</summary>
    public required string Released { get; init; }
    internal DateTime? ReleasedAt { get; init; }
    internal int? ContextTokens { get; init; }
    internal double? Cents { get; init; }
    internal int TierOrder { get; init; }
    [ObservableProperty] private bool _picked;
    /// <summary>A model on the person's own server: whether one tiny question showed it can use RigSight's look-ups.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCheck))] private string _check = "";
    [ObservableProperty] private bool _checkBad;
    public bool HasCheck => Check.Length > 0;
}

/// <summary>Where to get a key for one service: what it's for, and its own key page.</summary>
public sealed record KeyHelp(string Service, string Line, string Url, string Site)
{
    public string Badge => ServiceLook.Badge(Service);
    public string BadgeColor => ServiceLook.Color(Service);
}

/// <summary>A service's badge on a saved key: its initials and colour (the services' own colours, as small badges).</summary>
public static class ServiceLook
{
    public static string Badge(string service) => service switch
    {
        "OpenRouter" => "OR", "OpenAI" => "AI", "Anthropic" => "A", "Google Gemini" => "G", "DeepSeek" => "DS",
        "xAI" => "x", "Mistral" => "M", "Groq" => "GQ", _ => "…",
    };
    public static string Color(string service) => service switch
    {
        "OpenRouter" => "#6467F2", "OpenAI" => "#10A37F", "Anthropic" => "#D97757", "Google Gemini" => "#4285F4", "DeepSeek" => "#4D6BFE",
        "xAI" => "#3A3A3A", "Mistral" => "#FA520F", "Groq" => "#F55036", _ => "#4B5563",
    };
}

/// <summary>
/// Your API key, the "Just paste" way (approved mock-up 2026-10-27): one box takes a key or a server's address, RigSight
/// says which service it's from, Connect checks it and saves it. Every key pasted stays in "Your keys", one in use; the
/// models of the key in use are a table of real facts (model, maker, context, cost) with Best answers, Balanced and
/// Cheapest marked. The key in use is copied into the settings Ask reads (service, model, address), so asking is unchanged.
/// </summary>
public sealed partial class AiSettingsViewModel
{
    /// <summary>The services a key can be from, for "Not OpenAI? Pick the service" and keys RigSight can't place.</summary>
    public IReadOnlyList<string> Services { get; } = ["OpenRouter", "OpenAI", "Anthropic", "Google Gemini", "DeepSeek", "xAI", "Mistral", "Groq"];

    /// <summary>Settings' old name for a server's address; still read, never written.</summary>
    private const string CustomService = "Custom";

    // ── The paste box ────────────────────────────────────────────────────
    /// <summary>What's in the box: a key or a server's address. Never saved until Connect works.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasteIsAddress), nameof(HasPaste), nameof(DetectedLine), nameof(DetectedService), nameof(ShowServicePick), nameof(ShowNotRightLink), nameof(PasteBadge), nameof(PasteBadgeColor))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _apiKey = "";

    partial void OnApiKeyChanged(string value) { KeyProblem = null; NotPrivate = false; NoModelList = false; IsOllamaAddress = false; PickedService = null; }

    public bool HasPaste => ApiKey.Trim().Length > 0;
    public bool PasteIsAddress => KeyDetect.IsAddress(ApiKey.Trim());

    /// <summary>A service picked by hand when the key's start says nothing, or says the wrong thing (DeepSeek keys start "sk-" too).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectedLine), nameof(DetectedService), nameof(ShowServicePick), nameof(ShowNotRightLink), nameof(PasteBadge), nameof(PasteBadgeColor))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string? _pickedService;

    /// <summary>The service the key will be sent to: picked by hand, else read from its start; null when nobody knows.</summary>
    public string? DetectedService => PasteIsAddress ? CustomService : PickedService ?? KeyDetect.Service(ApiKey.Trim());
    public string PasteBadge => ServiceLook.Badge(DetectedService ?? "");
    public string PasteBadgeColor => ServiceLook.Color(DetectedService ?? "");

    /// <summary>"That's an OpenRouter key." / "That's a server address." / which service, when it can't be told.</summary>
    public string DetectedLine => !HasPaste ? ""
        : PasteIsAddress ? Lang.T("That's a server address.")
        : DetectedService is { } s ? (StartsWithVowel(s) ? Lang.T($"That's an {s} key.") : Lang.T($"That's a {s} key."))
        : KeyDetect.IsPlainSk(ApiKey) ? Lang.T("Keys from DeepSeek and some other services start the same way. Pick which one it's from, so the key goes only there.")
        : Lang.T("RigSight can't tell which service this key is from. Pick it, so the key goes only there.");

    private static bool StartsWithVowel(string s) => "AEIOU".Contains(char.ToUpperInvariant(s[0]));

    /// <summary>The service list shows when nothing could be read from the key, or when they open it to correct a guess.</summary>
    public bool ShowServicePick => HasPaste && !PasteIsAddress && (DetectedService is null || ChoosingService);
    /// <summary>"Not right? Pick the service" beside a guess that can be corrected.</summary>
    public bool ShowNotRightLink => HasPaste && !PasteIsAddress && DetectedService is not null && !ShowServicePick;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowServicePick), nameof(ShowNotRightLink))] private bool _choosingService;
    [RelayCommand] private void ChooseService() => ChoosingService = true;
    partial void OnPickedServiceChanged(string? value) { if (value is not null) ChoosingService = false; }

    /// <summary>A server's key, which servers on the person's own network usually don't ask for.</summary>
    [ObservableProperty] private string _serverKey = "";
    /// <summary>An optional name for the key ("Work"), shown in Your keys.</summary>
    [ObservableProperty] private string _newKeyName = "";
    /// <summary>The box shows what was typed (Show), for checking a key before Connect; a saved key is never shown again.</summary>
    [ObservableProperty] private bool _showPaste;
    [RelayCommand] private void TogglePaste() => ShowPaste = !ShowPaste;

    /// <summary>Why Connect didn't work, in plain words, under the box.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasKeyProblem))] private string? _keyProblem;
    public bool HasKeyProblem => KeyProblem is not null;

    /// <summary>An http:// address outside the person's own network: asked before the key and records go there unprotected.</summary>
    [ObservableProperty] private bool _notPrivate;
    private bool _notPrivateAccepted;
    [RelayCommand] private async Task UseAnyway() { _notPrivateAccepted = true; NotPrivate = false; await Connect(); }

    /// <summary>The address is Ollama's (port 11434): its own setting does more (downloads, sizes for the graphics card).</summary>
    [ObservableProperty] private bool _isOllamaAddress;
    [RelayCommand] private void SwitchToOllama() { string address = ApiKey.Trim(); ClearPaste(); Mode = "ollama"; OllamaAddress = address; }

    /// <summary>The server answered but doesn't list its models: the model's name is typed instead.</summary>
    [ObservableProperty] private bool _noModelList;
    [ObservableProperty] private string _typedModel = "";
    private string _pendingRoot = "";

    /// <summary>Kept on the connected screen until closed: RigSight put /v1 on the end of the address because only that answered.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasV1Note))] private string? _v1Note;
    public bool HasV1Note => V1Note is not null;
    [RelayCommand] private void CloseV1Note() => V1Note = null;

    private bool CanConnect() => HasPaste && DetectedService is not null && !ConnectCommand.IsRunning;

    /// <summary>
    /// Checks what was pasted with the service it's from (or the server at that address), and when it works saves it in
    /// Your keys, in use, with the Balanced model picked where there is one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        string text = ApiKey.Trim();
        string? service = DetectedService;
        if (text.Length == 0 || service is null) return;
        KeyProblem = null;
        if (NoModelList && TypedModel.Trim().Length > 0) { SaveAndUse(CustomService, ServerKey.Trim(), _pendingRoot, TypedModel.Trim(), null); return; }
        bool address = service == CustomService;
        var result = await OwnKey.ConnectAsync(address ? CustomService : service, address ? ServerKey.Trim() : text, address ? text : "");
        if (result.IsOllama) { IsOllamaAddress = true; return; }
        if (result.NotPrivate && !_notPrivateAccepted) { NotPrivate = true; return; }
        if (result.NoModelList) { NoModelList = true; _pendingRoot = result.Root; return; }
        if (result.Problem is not null || result.Models is null) { KeyProblem = result.Problem ?? Lang.T("Nothing came back. Check what you pasted, then press Connect again."); return; }
        string model = result.Models.FirstOrDefault(m => m.Tier == "Balanced")?.Id ?? result.Models.FirstOrDefault(m => m.Tier is not null)?.Id ?? result.Models[0].Id;
        SaveAndUse(service, address ? ServerKey.Trim() : text, address ? result.Root : "", model, result.Models);
        if (result.AddedV1) V1Note = Lang.T($"Your server didn't answer at {text}, but did with /v1 on the end, so RigSight saved it that way.");
    }

    private void SaveAndUse(string service, string secret, string address, string model, IReadOnlyList<ServiceModel>? models)
    {
        string name = NewKeyName.Trim().Length > 0 ? NewKeyName.Trim() : service == CustomService ? Lang.T("Your own server") : service;
        try
        {
            var saved = SavedKeys.Add(service, name, secret, address, model);
            Log.Write("settings", $"Ask RigSight key added and in use: {service}{(address.Length > 0 ? " at " + address : "")}, model {model}");
            ClearPaste();
            Adding = false;
            UseSaved(saved, models);
            ShowAdded();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyStore.CryptographicException)
        {
            Log.Error("ai-settings", ex);
            KeyProblem = Lang.T("The key works, but Windows couldn't keep it safely on this PC. Press Connect again.");
        }
    }

    /// <summary>How long "Added just now" and the Model card's outline stay before settling (approved mock-up 2026-10-28).</summary>
    internal static TimeSpan AddedFor = TimeSpan.FromSeconds(6);

    /// <summary>
    /// A key just added: said on its own row ("Added just now"), not in a banner that stays, and the Model card outlined and
    /// brought into view with the model picked for them named, so they see where to change it (the user, 2026-10-28).
    /// </summary>
    private async void ShowAdded()
    {
        KeyToast = null;
        var row = Keys.FirstOrDefault(k => k.InUse);
        if (row is not null) row.JustAdded = true;
        ModelForNow = true;
        ModelCardOutlined = true;
        ModelCardWanted?.Invoke();
        await Task.Delay(AddedFor);
        if (row is not null) row.JustAdded = false;
        ModelForNow = false;
        ModelCardOutlined = false;
    }

    /// <summary>The view brings the Model card into view.</summary>
    public event Action? ModelCardWanted;
    [ObservableProperty] private bool _modelCardOutlined;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ModelLine))] private bool _modelForNow;

    /// <summary>"Answers use Claude Sonnet 5.5", and right after a key is added "…for now. Pick another any time."</summary>
    public string ModelLine => Ai.Model.Length == 0 ? ""
        : (_models.FirstOrDefault(m => m.Id == Ai.Model)?.Name ?? Ai.Model) is var name && ModelForNow
            ? Lang.T($"Answers use {name} for now. Pick another any time.")
            : Lang.T($"Answers use {name}");

    private void ClearPaste()
    {
        ApiKey = ""; ServerKey = ""; NewKeyName = ""; TypedModel = ""; ShowPaste = false;
        NoModelList = false; NotPrivate = false; IsOllamaAddress = false; _notPrivateAccepted = false; PickedService = null; ChoosingService = false;
        PasteCleared?.Invoke();
    }

    /// <summary>The view empties its password boxes (a PasswordBox can't be bound).</summary>
    public event Action? PasteCleared;

    // ── Your keys ────────────────────────────────────────────────────────
    public ObservableCollection<SavedKeyRow> Keys { get; } = [];
    public bool HasKeys => Keys.Count > 0;
    /// <summary>The paste box shows with no key saved, or once "Add another key" is pressed.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPasteBox), nameof(ShowAddAnother))] private bool _adding;
    public bool ShowPasteBox => !HasKeys || Adding;
    public bool ShowAddAnother => HasKeys && !Adding;
    [RelayCommand] private void AddAnother() { ClearPaste(); KeyToast = null; Adding = true; }
    [RelayCommand] private void CancelAdd() { ClearPaste(); Adding = false; }

    /// <summary>What just happened to the keys (switched, removed), in plain grey (the user, 2026-10-27); a key added says so on its own row.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasKeyToast))] private string? _keyToast;
    public bool HasKeyToast => KeyToast is not null;
    private void ShowToast(string text) => KeyToast = text;

    private void LoadKeys()
    {
        SavedKeys.Migrate(Ai.Service, Ai.Model, Ai.CustomAddress);
        var active = SavedKeys.Active();
        Keys.Clear();
        foreach (var k in SavedKeys.All())
            Keys.Add(new SavedKeyRow
            {
                Id = k.Id, Service = k.Address.Length > 0 && !Services.Contains(k.Service) ? CustomService : k.Service, Name = k.Name, Mask = SavedKeys.Mask(k),
                IsServer = k.Address.Length > 0 && !Services.Contains(k.Service),
                Fact = k.Address.Length > 0 && k.Secret.Length == 0 ? Lang.T("no key needed") : "",
                Used = k.LastUsed is { } used ? UsedWhen(used) : Lang.T("Never used"),
                InUse = k.Id == active?.Id,
            });
        OnPropertyChanged(nameof(HasKeys));
        OnPropertyChanged(nameof(ShowPasteBox));
        OnPropertyChanged(nameof(ShowAddAnother));
        OnPropertyChanged(nameof(HasOtherKey));
        OnPropertyChanged(nameof(OtherKeyName));
        OnPropertyChanged(nameof(OutOfCreditText));
        OnPropertyChanged(nameof(OutOfCreditTitle));
    }

    private static string UsedWhen(DateTime used)
    {
        var days = (DateTime.Now.Date - used.ToLocalTime().Date).Days;
        return days <= 0 ? Lang.T("Used today") : days == 1 ? Lang.T("Used yesterday") : Lang.T($"Used {days} days ago");
    }

    [RelayCommand]
    private void UseKey(SavedKeyRow? row)
    {
        if (row is null || row.InUse) return;
        SavedKeys.Use(row.Id);
        if (SavedKeys.Active() is { } k) UseSaved(k, null);
        ShowToast(Lang.T($"Answers now use {row.Name}."));
    }

    [RelayCommand] private void AskRemoveKey(SavedKeyRow? row) { foreach (var k in Keys) k.AskingRemove = k == row; KeyToast = null; }
    [RelayCommand] private void CancelRemoveKey(SavedKeyRow? row) { if (row is not null) row.AskingRemove = false; }

    [RelayCommand]
    private void RemoveKey(SavedKeyRow? row)
    {
        if (row is null) return;
        bool wasInUse = row.InUse;
        SavedKeys.Remove(row.Id);
        Log.Write("settings", $"Ask RigSight key removed: {row.Service}{(wasInUse ? " (it was in use)" : "")}");
        if (SavedKeys.Active() is { } next)
        {
            if (wasInUse) { UseSaved(next, null); ShowToast(Lang.T($"Removed {row.Name}. Answers now use {next.Name}.")); }
            else { LoadKeys(); ShowToast(Lang.T($"Removed {row.Name} from this PC.")); }
            return;
        }
        // The last one: back to the empty box, and nothing for Ask to use.
        _settings.Update(s => { s.Ai.Model = ""; s.Ai.CustomAddress = ""; });
        _models = [];
        LoadKeys();
        ShowModels();
        ShowToast(Lang.T("Key removed from this PC. To turn it off completely, delete it on the service's site too."));
    }

    /// <summary>
    /// Makes a saved key the one Ask uses: its service, model and address go into the settings Ask reads, then its models
    /// are listed (from <paramref name="models"/> when Connect just fetched them, else asked for again).
    /// </summary>
    private void UseSaved(SavedKey key, IReadOnlyList<ServiceModel>? models)
    {
        bool server = key.Address.Length > 0 && !Services.Contains(key.Service);
        _settings.Update(s => { s.Ai.Service = server ? CustomService : key.Service; s.Ai.Model = key.Model; s.Ai.CustomAddress = key.Address; });
        V1Note = null;
        LoadKeys();
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(SendingWhere));
        if (models is not null) { SetModels(models, server); _ = LoadCredit(key); }
        else _ = RefreshModels();
    }

    /// <summary>The key in use asked again for its models (on opening the tab, or after switching keys), and its credit.</summary>
    private async Task RefreshModels()
    {
        if (SavedKeys.Active() is not { } key) { _models = []; ShowModels(); return; }
        ModelsLoading = true;
        bool server = key.Address.Length > 0 && !Services.Contains(key.Service);
        var result = await OwnKey.ConnectAsync(server ? CustomService : key.Service, key.Secret, server ? key.Address : "");
        ModelsLoading = false;
        if (SavedKeys.Active()?.Id != key.Id) return;
        ModelsProblem = result.Problem;
        SetModels(result.Models ?? (result.NoModelList && key.Model.Length > 0 ? [new ServiceModel(key.Model, null, false)] : []), server);
        _ = LoadCredit(key);
    }

    /// <summary>OpenRouter's credit left and this month's spend on the key's row; no credit left shows the problem box.</summary>
    private async Task LoadCredit(SavedKey key)
    {
        if (key.Service != "OpenRouter") { OutOfCredit = false; return; }
        var (left, month) = await OwnKey.CreditAsync(key.Secret);
        var row = Keys.FirstOrDefault(r => r.Id == key.Id);
        if (row is null) return;
        var parts = new List<string>();
        if (left is double l) parts.Add(Lang.T($"{Dollars(l)} credit left"));
        if (month is double m && m > 0) parts.Add(Lang.T($"{Dollars(m)} this month"));
        row.Fact = string.Join(" · ", parts);
        OutOfCredit = row.InUse && left is double z && z <= 0.005;
        OnPropertyChanged(nameof(OtherKeyName));
    }

    private static string Dollars(double d) => d.ToString("$0.00", CultureInfo.InvariantCulture);

    /// <summary>The key in use is out of credit: the box offers another saved key, or adding credit.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OutOfCreditTitle), nameof(HasOtherKey), nameof(OtherKeyName))] private bool _outOfCredit;
    public string OutOfCreditTitle => Lang.T($"No credit left on your {Ai.Service} account");
    private SavedKeyRow? OtherKey => Keys.FirstOrDefault(k => !k.InUse && !k.IsServer) ?? Keys.FirstOrDefault(k => !k.InUse);
    public bool HasOtherKey => OtherKey is not null;
    public string OtherKeyName => OtherKey is { } o ? Lang.T($"Use {o.Name}") : "";
    public string OutOfCreditText => HasOtherKey
        ? Lang.T($"Questions stop until you add some, or answer with your {OtherKey!.Name} key for now.")
        : Lang.T("Questions stop until you add some. Your key and model are kept.");
    [RelayCommand] private void UseOtherKey() { if (OtherKey is { } o) UseKey(o); }
    [RelayCommand]
    private void AddCredit()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://openrouter.ai/settings/credits") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("ai-settings", ex); }
    }

    // ── The model table ──────────────────────────────────────────────────
    private List<ModelRow> _models = [];
    private bool _modelsOnServer;
    public ObservableCollection<ModelRow> ModelRows { get; } = [];
    public bool HasModels => _models.Count > 0;
    [ObservableProperty] private bool _modelsLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasModelsProblem))] private string? _modelsProblem;
    public bool HasModelsProblem => ModelsProblem is not null;
    [ObservableProperty] private string _modelSearch = "";
    partial void OnModelSearchChanged(string value) => ShowModels();
    public string ModelSearchHint => Lang.T($"Search {_models.Count} models");
    /// <summary>Which column the table is sorted by: tier (the suggested three first), model, maker, context or cost.</summary>
    [ObservableProperty] private string _sortBy = "tier";
    [RelayCommand] private void SortModels(string? by) { if (by is { Length: > 0 }) { SortBy = by; ShowModels(); } }
    public bool NoModelMatch => HasModels && ModelRows.Count == 0;
    /// <summary>
    /// A column shows only when the key's list fills it for some model: OpenRouter's has maker, context and price; Anthropic's
    /// no prices; most others little but names. Real facts only (the user's ruling), so an empty column is left out, not guessed.
    /// </summary>
    public bool ShowMakerColumn => _models.Select(m => m.Maker).Where(m => m.Length > 0).Distinct().Count() > 1;
    /// <summary>In Maker's place when one maker makes them all (a key from Anthropic or OpenAI): the newest is easy to spot.</summary>
    public bool ShowReleasedColumn => !ShowMakerColumn && _models.Any(m => m.ReleasedAt is not null);
    public bool ShowContextColumn => _models.Any(m => m.ContextTokens is not null);
    public bool ShowCostColumn => !_modelsOnServer && _models.Any(m => m.Cents is not null);
    /// <summary>A server's models have no prices or suggestions: the table drops the cost column's meaning and checks each picked one instead.</summary>
    public bool ModelsOnServer => _modelsOnServer;

    private void SetModels(IReadOnlyList<ServiceModel> models, bool server)
    {
        _modelsOnServer = server;
        _models = [.. models.Select(m => new ModelRow
        {
            Id = m.Id,
            Name = m.Name,
            Tier = m.Tier switch { "Best answers" => Lang.T("Best answers"), "Balanced" => Lang.T("Balanced"), "Cheapest" => Lang.T("Cheapest"), _ => "" },
            TierOrder = m.Tier switch { "Best answers" => 0, "Balanced" => 1, "Cheapest" => 2, _ => 3 },
            Maker = m.Maker ?? "",
            ContextTokens = m.ContextTokens,
            Context = m.ContextTokens is int c ? ContextText(c) : "",
            Cents = m.CentsPerQuestion,
            Cost = server ? "" : m.CentsPerQuestion is double cents ? Lang.T($"about {PerQuestion(cents)}") : "",
            ReleasedAt = m.Released,
            Released = m.Released is DateTime at ? at.ToLocalTime().ToString("MMM yyyy", CultureInfo.CurrentCulture) : "",
        })];
        // A model picked before that the key can't use any more: the Balanced one, or the first.
        if (_models.Count > 0 && !_models.Any(m => m.Id == Ai.Model))
        {
            string pick = (_models.FirstOrDefault(m => m.TierOrder == 1) ?? _models.FirstOrDefault(m => m.TierOrder < 3) ?? _models[0]).Id;
            _settings.Update(s => s.Ai.Model = pick);
            if (SavedKeys.Active() is { } k) SavedKeys.SetModel(k.Id, pick);
        }
        ModelSearch = "";
        ShowModels();
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(ModelsOnServer));
        OnPropertyChanged(nameof(ShowMakerColumn));
        OnPropertyChanged(nameof(ShowContextColumn));
        OnPropertyChanged(nameof(ShowCostColumn));
        OnPropertyChanged(nameof(ShowReleasedColumn));
        OnPropertyChanged(nameof(ModelLine));
        OnPropertyChanged(nameof(ModelSearchHint));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ModelPerQuestion));
        // A server's model is checked when picked; the one already in use is checked on showing it.
        if (server && _models.FirstOrDefault(m => m.Id == Ai.Model) is { } current) _ = ProbeModel(current);
    }

    private void ShowModels()
    {
        var words = TextMatch.Words(ModelSearch);
        IEnumerable<ModelRow> shown = _models.Where(m => TextMatch.Has(words, m.Name + " " + m.Maker + " " + m.Id));
        shown = SortBy switch
        {
            "name" => shown.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            "maker" => shown.OrderBy(m => m.Maker.Length == 0).ThenBy(m => m.Maker, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            "context" => shown.OrderByDescending(m => m.ContextTokens ?? -1).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            "cost" => shown.OrderBy(m => m.Cents ?? double.MaxValue).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            "released" => shown.OrderByDescending(m => m.ReleasedAt ?? DateTime.MinValue).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => shown.OrderBy(m => m.TierOrder).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        ModelRows.Clear();
        foreach (var m in shown) { m.Picked = m.Id == Ai.Model; ModelRows.Add(m); }
        OnPropertyChanged(nameof(NoModelMatch));
        OnPropertyChanged(nameof(SortBy));
    }

    /// <summary>"128k", "1M": what the model can read at once, as the services write it.</summary>
    internal static string ContextText(int tokens) => tokens >= 1_000_000
        ? (tokens / 1_000_000.0).ToString(tokens % 1_000_000 == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + "M"
        : (tokens / 1000).ToString(CultureInfo.CurrentCulture) + "k";

    [RelayCommand]
    private void PickModel(ModelRow? row)
    {
        if (row is null) return;
        Log.Write("settings", $"Ask RigSight model picked (own key): {row.Id}");
        _settings.Update(s => s.Ai.Model = row.Id);
        if (SavedKeys.Active() is { } k) SavedKeys.SetModel(k.Id, row.Id);
        foreach (var m in _models) m.Picked = m == row;
        ModelForNow = false;
        OnPropertyChanged(nameof(ModelLine));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ModelPerQuestion));
        if (_modelsOnServer) _ = ProbeModel(row);
    }

    /// <summary>
    /// A model on the person's own server asked one tiny question with a look-up attached: there's no standard way to ask a
    /// server whether a model can use tools, so trying is the only sure test (and costs nothing there).
    /// </summary>
    private async Task ProbeModel(ModelRow row)
    {
        if (SavedKeys.Active() is not { } key || row.CheckBad || row.Check == Lang.T("Can read your PC's records")) return;
        row.Check = Lang.T("Checking…");
        row.CheckBad = false;
        bool? can = await OwnKey.ProbeToolsAsync(key.Address, key.Secret, row.Id);
        row.Check = can switch { true => Lang.T("Can read your PC's records"), false => Lang.T("Can't read your PC's records"), _ => Lang.T("Couldn't check") };
        row.CheckBad = can == false;
        OnPropertyChanged(nameof(PickedCantLookUp));
        OnPropertyChanged(nameof(PickedCantLookUpTitle));
    }

    /// <summary>The picked server model answered without the look-up: the box says so and how to fix it.</summary>
    public bool PickedCantLookUp => _modelsOnServer && _models.FirstOrDefault(m => m.Id == Ai.Model) is { CheckBad: true };
    public string PickedCantLookUpTitle => Lang.T($"{Ai.Model} can't read your PC's records");
    [RelayCommand] private async Task CheckAgain() { if (_models.FirstOrDefault(m => m.Id == Ai.Model) is { } row) { row.CheckBad = false; row.Check = ""; await ProbeModel(row); } }

    /// <summary>"0.4¢" under a dollar's tenth, "$0.07" from there.</summary>
    private static string PerQuestion(double cents) => cents < 10
        ? (cents < 0.1 ? "0.1¢" : $"{cents.ToString(cents < 1 ? "0.0" : "0", CultureInfo.CurrentCulture)}¢")
        : (cents / 100).ToString("$0.00", CultureInfo.InvariantCulture);

    /// <summary>The model in use, for the Ask window's first-question note.</summary>
    public string Model => Ai.Model;
    /// <summary>About what a question costs on the model in use ("0.3¢"), for the Ask window's first-question note.</summary>
    public string? ModelPerQuestion => !_modelsOnServer && _models.FirstOrDefault(m => m.Id == Ai.Model)?.Cents is double c ? PerQuestion(c) : null;

    // ── Where to get a key ───────────────────────────────────────────────
    /// <summary>"Where do I get a key?": OpenRouter as the suggested start, then the others, each with its own key page.</summary>
    [ObservableProperty] private bool _showKeyHelp;
    [RelayCommand] private void ToggleKeyHelp() => ShowKeyHelp = !ShowKeyHelp;
    public string KeyHelpLabel => ShowKeyHelp ? Lang.T("Hide where to get a key") : Lang.T("Where do I get a key?");
    partial void OnShowKeyHelpChanged(bool value) => OnPropertyChanged(nameof(KeyHelpLabel));
    public IReadOnlyList<KeyHelp> OtherKeyHelps { get; } =
    [
        new("OpenAI", Lang.T("GPT models. A ChatGPT subscription doesn't pay for it."), "https://platform.openai.com/api-keys", "platform.openai.com"),
        new("Anthropic", Lang.T("Claude models. A Claude subscription doesn't pay for it."), "https://console.anthropic.com/settings/keys", "console.anthropic.com"),
        new("Google Gemini", Lang.T("Gemini models. Has a free allowance."), "https://aistudio.google.com/apikey", "aistudio.google.com"),
        new("DeepSeek", Lang.T("DeepSeek models, low prices"), "https://platform.deepseek.com/api_keys", "platform.deepseek.com"),
        new("xAI", Lang.T("Grok models"), "https://console.x.ai", "console.x.ai"),
        new("Mistral", Lang.T("Mistral models"), "https://console.mistral.ai/api-keys", "console.mistral.ai"),
        new("Groq", Lang.T("Fast open models"), "https://console.groq.com/keys", "console.groq.com"),
    ];
    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (url is not { Length: > 0 }) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("ai-settings", ex); }
    }

    /// <summary>Closes what floats over the page; nothing does any more (the table is on the page), kept for the shell's calls.</summary>
    public void ClosePickers() { ChoosingService = false; }

    /// <summary>Your API key picked, or the tab opened with it: the saved keys, and the models of the one in use.</summary>
    private void ShowOwnKey()
    {
        LoadKeys();
        if (SavedKeys.Active() is not null && _models.Count == 0 && !ModelsLoading) _ = RefreshModels();
    }
}

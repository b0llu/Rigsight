using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.ViewModels;

/// <summary>A model already on Ollama: its size, how it fits, and whether it can be used (it has to look things up).</summary>
public sealed partial class OllamaRow(string name, string size, bool canLookUp, string fit, OllamaFit fitKind) : ObservableObject
{
    public string Name { get; } = name;
    public string Size { get; } = size;
    public bool CanLookUp { get; } = canLookUp;

    /// <summary>How much of it Ollama really has on the card, when it's loaded (0 to 1); null before.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Fit), nameof(HasFit), nameof(FitIsGood), nameof(FitWhy), nameof(HasFitWhy))] private double? _loaded;
    private bool PartlyOff => Loaded is < 0.95;

    public string Fit => PartlyOff ? Lang.T("Partly on your card") : fit;
    public bool HasFit => Fit.Length > 0;
    public bool FitIsGood => Loaded is { } l ? l >= 0.95 : fitKind == OllamaFit.Fits;
    public string FitWhy => PartlyOff ? Lang.T($"Ollama has {Loaded:P0} of it on your graphics card and the rest on the processor, so answers take longer.") : "";
    public bool HasFitWhy => FitWhy.Length > 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUse))] private bool _inUse;
    public bool CanUse => CanLookUp && !InUse;

    /// <summary>Remove pressed: the row asks "Remove … ? Frees …" with Remove and Cancel in place of its buttons.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotAsking))] private bool _askingRemove;
    public bool NotAsking => !AskingRemove;
    [ObservableProperty] private bool _removing;
    public string RemoveQuestion => Lang.T($"Remove {Name}? Frees {Size}.");
}

public enum PickState { Idle, Downloading }

/// <summary>One model picked for this PC (best, smaller, bigger): download it after agreeing to its size, then use it.</summary>
public sealed partial class OllamaPickCard(OllamaPick pick, string label, OllamaFit fit, string? card) : ObservableObject
{
    public OllamaPick Pick { get; } = pick;
    public string Label { get; } = label;
    public string Name => Pick.Name;
    public string Tag => Pick.Tag;
    public string Why => Lang.T(Pick.Why);
    public string SizeText => Pick.SizeText;
    public string FitText { get; } = OllamaPicks.FitText(fit, card);
    public string FitWhy { get; } = OllamaPicks.FitWhy(fit);
    public bool HasFit => FitText.Length > 0;
    public bool HasFitWhy => FitWhy.Length > 0;
    public bool FitIsGood { get; } = fit == OllamaFit.Fits;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsDownloading), nameof(ShowSize))] private PickState _state;
    public bool IsIdle => State == PickState.Idle;
    public bool IsDownloading => State == PickState.Downloading;

    /// <summary>Already on Ollama: Use (or In use) in place of Download.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUse), nameof(NotOnOllama), nameof(ShowSize), nameof(CanDownloadHere), nameof(NoRoom))] private bool _onOllama;
    /// <summary>The download size, only while there is something to download.</summary>
    public bool ShowSize => IsIdle && !OnOllama;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUse))] private bool _inUse;
    public bool NotOnOllama => !OnOllama;
    public bool CanUse => OnOllama && !InUse;

    /// <summary>"**6.6 GB** to C: · 205 GB free", or "**14 GB** won't fit on C: · 9 GB free" (Markdown: the size in bold).</summary>
    [ObservableProperty] private string _facts = "";
    /// <summary>"about 4 min", once a download here has shown this connection's speed; empty before.</summary>
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownloadHere), nameof(NoRoom))] private bool _hasRoom = true;
    public bool CanDownloadHere => NotOnOllama && HasRoom;
    public bool NoRoom => NotOnOllama && !HasRoom;
    [ObservableProperty] private double _progress;
    /// <summary>For the log: "3.1 of 6.6 GB · 18 MB/s · about 4 min left".</summary>
    [ObservableProperty] private string _progressText = "";
    /// <summary>"**3.1** of 6.6 GB", over the bar's left; time left on its right; speed under it.</summary>
    [ObservableProperty] private string _progressDone = "";
    [ObservableProperty] private string _progressLeft = "";
    [ObservableProperty] private string _progressSpeed = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] private string? _problem;
    public bool HasProblem => Problem is not null;

    internal CancellationTokenSource? Stop;
}

/// <summary>
/// Settings › Ask RigSight → Ollama (approved mock-up 2026-10-24): the models already on Ollama first, each saying if it
/// can be used, then three picked for this PC's graphics card that download through Ollama itself, with the size agreed
/// first, progress, speed and Cancel. Without Ollama the downloads are off and say "Get Ollama first".
/// </summary>
public sealed partial class AiSettingsViewModel
{
    private const string OllamaDownloadPage = "https://ollama.com/download";

    private double? _vramGb;
    private string? _card;
    private string? _ram;
    private bool _pcRead;

    /// <summary>The graphics card, its memory and the PC's memory, read once from the records.</summary>
    private void ReadPc()
    {
        if (_pcRead) return;
        _pcRead = true;
        try
        {
            using var db = RigsightDb.OpenReader();
            var inventory = db.GetInventory();
            _card = OllamaPicks.MainCard(inventory.Where(i => i.Kind == Inventory.Gpu).Select(i => i.Name)) is { } c ? OllamaPicks.ShortCard(c) : null;
            _ram = inventory.FirstOrDefault(i => i.Kind == Inventory.Ram)?.Value is { Length: > 0 } ram ? ram : null;
            _vramGb = db.GetVramTotalMb() is { } mb and > 0 ? Math.Round(mb / 1024, 1) : null;
        }
        catch (Exception ex) { Log.Error("ai-settings", ex); }
        Log.Write("ai-settings", $"Ollama picks: card {_card ?? "unknown"}, {(_vramGb is { } v ? $"{v} GB" : "memory unknown")}, PC memory {_ram ?? "unknown"}");
    }

    /// <summary>Ollama answered at the address (models or none).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DownloadOff))] [NotifyCanExecuteChangedFor(nameof(DownloadCommand))] private bool _ollamaAnswering;
    /// <summary>The address is this PC, Ollama doesn't answer, and it isn't installed: "Ollama isn't on this PC."</summary>
    [ObservableProperty] private bool _ollamaMissing;
    public bool OllamaHere => Ollama.IsOnThisPc(Ai.OllamaAddress);
    public bool DownloadOff => !OllamaAnswering;
    public string DownloadOffText => OllamaMissing ? Lang.T("Get Ollama first") : Lang.T("Connect first");

    public IReadOnlyList<OllamaRow> OllamaRows { get; private set; } = [];
    public bool HasOllamaRows => OllamaRows.Count > 0;
    /// <summary>Models on it, but none can look things up: say so above the picks.</summary>
    public bool NoneCanLookUp => OllamaRows.Count > 0 && !OllamaRows.Any(r => r.CanLookUp);
    /// <summary>
    /// Over three, the list scrolls in its own box with the fourth row cut in half (so it shows there's more), and a
    /// search box finds a model by name (the person's call, 2026-10-24).
    /// </summary>
    public bool ManyOllamaRows => OllamaRows.Count > 3;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OllamaShownRows), nameof(NoOllamaMatch))] private string _ollamaSearch = "";
    public IReadOnlyList<OllamaRow> OllamaShownRows => OllamaSearch.Trim().Length == 0 ? OllamaRows
        : [.. OllamaRows.Where(r => TextMatch.Has(TextMatch.Words(OllamaSearch), r.Name))];
    public bool NoOllamaMatch => OllamaRows.Count > 0 && OllamaShownRows.Count == 0;

    public IReadOnlyList<OllamaPickCard> Picks { get; private set; } = [];

    /// <summary>The section's heading: "Models for this PC", or for Ollama on another computer.</summary>
    public string PicksHeading => OllamaHere ? Lang.T("Models for this PC") : Lang.T($"Models for Ollama on {Ollama.Host(Ai.OllamaAddress)}");

    /// <summary>
    /// Under the heading: what they were picked for and where downloads go ("Picked for your RTX 3080 Ti (12 GB) and
    /// 16 GB of memory · downloads go to C:\Users\…\.ollama\models").
    /// </summary>
    public string PicksLine
    {
        get
        {
            if (!OllamaHere) return Lang.T($"Picked by size; how they fit there can't be checked from here · downloads go to Ollama on {Ollama.Host(Ai.OllamaAddress)}");
            string card = _card is null ? "" : _vramGb is { } v ? Lang.T($"your {_card} ({v.ToString("0.#", CultureInfo.CurrentCulture)} GB)") : Lang.T($"your {_card}");
            string picked = card.Length > 0 && _ram is not null ? Lang.T($"Picked for {card} and {_ram} of memory")
                : card.Length > 0 ? Lang.T($"Picked for {card}")
                : _ram is not null ? Lang.T($"Picked for {_ram} of memory")
                : Lang.T("Picked for this PC");
            return picked + " · " + Lang.T($"downloads go to {OllamaPicks.ModelsFolder()}");
        }
    }

    /// <summary>The three cards for this PC (or, for another computer, by size alone), with what's on Ollama marked.</summary>
    private void ShowPicks()
    {
        if (OllamaHere) ReadPc();
        double? vram = OllamaHere ? _vramGb : null;
        var (best, smaller, bigger) = OllamaPicks.For(OllamaHere ? _vramGb : 12);
        string? card = OllamaHere ? _card : null;
        // A download running keeps its card (and its progress) when the list is read again.
        OllamaPickCard Card(OllamaPick p, string label) => Picks.FirstOrDefault(c => c.Tag == p.Tag && (c.Label == label || !c.IsIdle))
            ?? new OllamaPickCard(p, label, OllamaPicks.Fit(p.Gb, vram), card);
        var cards = new List<OllamaPickCard> { Card(best, OllamaHere ? Lang.T("Best for this PC") : Lang.T("A good start")) };
        if (smaller is not null) cards.Add(Card(smaller, Lang.T("Smaller and quicker")));
        if (bigger is not null) cards.Add(Card(bigger, Lang.T("Bigger")));
        bool same = cards.Count == Picks.Count && cards.Zip(Picks).All(x => ReferenceEquals(x.First, x.Second));
        foreach (var c in cards) SetFacts(c);
        Picks = cards;
        MarkInUse();
        if (!same) OnPropertyChanged(nameof(Picks));
        OnPropertyChanged(nameof(PicksHeading));
        OnPropertyChanged(nameof(PicksLine));
    }

    /// <summary>The model in use: the one picked, or the first that can look things up.</summary>
    private string ChosenOllama => Ai.OllamaModel.Length > 0 && OllamaRows.Any(r => r.Name == Ai.OllamaModel && r.CanLookUp)
        ? Ai.OllamaModel : OllamaRows.FirstOrDefault(r => r.CanLookUp)?.Name ?? "";

    private void MarkInUse()
    {
        string chosen = ChosenOllama;
        foreach (var r in OllamaRows) r.InUse = r.CanLookUp && r.Name == chosen;
        foreach (var c in Picks)
        {
            c.OnOllama = OllamaRows.Any(r => Same(r.Name, c.Tag));
            c.InUse = c.OnOllama && Same(chosen, c.Tag);
        }
    }

    /// <summary>"gemma4:12b" is the same as "gemma4:12b"; a bare name means its ":latest".</summary>
    private static bool Same(string a, string b) =>
        string.Equals(a.EndsWith(":latest", StringComparison.Ordinal) ? a[..^7] : a, b.EndsWith(":latest", StringComparison.Ordinal) ? b[..^7] : b, StringComparison.OrdinalIgnoreCase);

    private void ShowRows(IReadOnlyList<OllamaModel>? found)
    {
        if (OllamaHere) ReadPc();
        double? vram = OllamaHere ? _vramGb : null;
        var ordered = (found ?? []).OrderByDescending(m => m.CanLookUp).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // Nothing changed (Check again, the same models): the rows and cards stay as they are, so nothing flashes.
        if (Picks.Count > 0 && OllamaRows.Count == ordered.Count
            && OllamaRows.Zip(ordered).All(x => x.First.Name == x.Second.Name && x.First.Size == Units.Bytes(x.Second.Bytes) && x.First.CanLookUp == x.Second.CanLookUp))
            return;
        OllamaRows = [.. ordered
            .Select(m =>
            {
                var fit = OllamaPicks.Fit(m.Bytes / (double)(1L << 30), vram);
                return new OllamaRow(m.Name, Units.Bytes(m.Bytes), m.CanLookUp, m.CanLookUp ? OllamaPicks.FitText(fit, OllamaHere ? _card : null) : "", fit);
            })];
        OnPropertyChanged(nameof(OllamaRows));
        OnPropertyChanged(nameof(OllamaShownRows));
        OnPropertyChanged(nameof(NoOllamaMatch));
        OnPropertyChanged(nameof(HasOllamaRows));
        OnPropertyChanged(nameof(NoneCanLookUp));
        OnPropertyChanged(nameof(ManyOllamaRows));
        ShowPicks();
    }

    [RelayCommand]
    private void UseOllama(string? name)
    {
        if (name is null || OllamaRows.FirstOrDefault(r => Same(r.Name, name)) is not { CanLookUp: true } row) return;
        Log.Write("settings", $"Ask RigSight model picked (Ollama): {row.Name}");
        Set(a => a.OllamaModel = row.Name, nameof(OllamaModel));
        MarkInUse();
    }

    // ── The line under the address, and the box for a problem (approved mock-up 2026-10-24) ──────────────────
    // Under the field, an icon as well as a colour, only the dot green when it works, the cause and its fix with one
    // button when it doesn't, and the line always in its place so nothing below moves.

    public enum OllamaLink { Checking, Connected, BadAddress, Stopped, Missing, NotAnswering, Offline }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineChecking), nameof(LineOk), nameof(LineWarn), nameof(OllamaLine), nameof(OllamaAddressBad),
        nameof(HasOllamaBox), nameof(OllamaBoxTitle), nameof(OllamaBoxText), nameof(OllamaBoxCalm), nameof(OllamaBoxAction), nameof(HasOllamaBoxAction))]
    private OllamaLink _ollamaState = OllamaLink.Checking;

    private string? _ollamaVersion;
    private int _check;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OllamaLine))] private bool _startingOllama;

    /// <summary>
    /// A check that takes over 0.3 s shows "Checking…"; a quick one goes straight to its answer, and the line, the box and
    /// the lists keep what they showed meanwhile (Check again flashed the whole card, 2026-10-24).
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LineChecking), nameof(LineOk), nameof(LineWarn), nameof(OllamaLine))] private bool _checkingShown;
    private static readonly TimeSpan ShowCheckingAfter = TimeSpan.FromMilliseconds(300);

    public bool LineChecking => CheckingShown || OllamaState == OllamaLink.Checking;
    public bool LineOk => !LineChecking && OllamaState == OllamaLink.Connected;
    public bool LineWarn => !LineChecking && !LineOk;
    public bool OllamaAddressBad => OllamaState == OllamaLink.BadAddress;

    public string OllamaLine => LineChecking ? (StartingOllama ? Lang.T("Starting Ollama…") : Lang.T($"Checking {Ollama.Where(Ai.OllamaAddress)}…")) : OllamaState switch
    {
        OllamaLink.Checking => StartingOllama ? Lang.T("Starting Ollama…") : Lang.T($"Checking {Ollama.Where(Ai.OllamaAddress)}…"),
        OllamaLink.Connected => Connected(),
        OllamaLink.BadAddress => Lang.T("That isn't an address. It looks like http://localhost:11434"),
        OllamaLink.Missing => Lang.T("Ollama isn't on this PC"),
        OllamaLink.NotAnswering when !OllamaHere => Lang.T($"Nothing answers at {Ollama.Host(Ai.OllamaAddress)}"),
        _ => Lang.T("Not answering"),
    };

    private string Connected()
    {
        string who = _ollamaVersion is { Length: > 0 } v ? Lang.T($"Ollama {v}") : "Ollama";
        string where = OllamaHere ? Lang.T($"Connected to {who} on this PC") : Lang.T($"Connected to {who} on {Ollama.Host(Ai.OllamaAddress)}");
        string models = OllamaRows.Count switch { 0 => Lang.T("no models yet"), 1 => Lang.T("1 model"), int n => Lang.T($"{n} models") };
        return $"{where} · {models}";
    }

    /// <summary>A problem, or a step left (no models yet): what it is and how to fix it, under the line.</summary>
    public bool HasOllamaBox => OllamaState is OllamaLink.Stopped or OllamaLink.Missing or OllamaLink.NotAnswering or OllamaLink.Offline
        || (OllamaState == OllamaLink.Connected && OllamaRows.Count == 0);
    /// <summary>No models yet isn't a problem: the box is grey, not amber.</summary>
    public bool OllamaBoxCalm => OllamaState == OllamaLink.Connected;

    public string OllamaBoxTitle => OllamaState switch
    {
        OllamaLink.Connected => Lang.T("Pick a model to download below"),
        OllamaLink.Stopped => Lang.T("Ollama is installed but not running"),
        OllamaLink.Missing => Lang.T("Get Ollama to answer on this PC"),
        OllamaLink.Offline => Lang.T("This PC has no network connection"),
        OllamaLink.NotAnswering when !OllamaHere => Lang.T($"Ollama on {Ollama.Host(Ai.OllamaAddress)} isn't answering"),
        OllamaLink.NotAnswering => Lang.T($"Ollama isn't answering at {Ollama.Where(Ai.OllamaAddress)}"),
        _ => "",
    };

    public string OllamaBoxText => OllamaState switch
    {
        OllamaLink.Connected => Lang.T("Ollama is running but has none yet. The first one under Picked for this PC is the one to start with."),
        OllamaLink.Stopped => Lang.T("Start it and RigSight connects by itself."),
        OllamaLink.Missing => Lang.T("It's free, and runs the models on your own graphics card. Install it, then press Check again."),
        OllamaLink.Offline => Lang.T("Ollama on another computer needs the network. Connect, then press Try again."),
        OllamaLink.NotAnswering when !OllamaHere => Lang.T("On that computer, Ollama has to be running and set to let other computers in (OLLAMA_HOST=0.0.0.0), then restarted."),
        OllamaLink.NotAnswering => Lang.T("Check that the address is the one Ollama uses. Its own is http://localhost:11434."),
        _ => "",
    };

    public string OllamaBoxAction => OllamaState switch
    {
        OllamaLink.Stopped => Lang.T("Start Ollama"),
        OllamaLink.Missing => Lang.T("Get Ollama"),
        OllamaLink.NotAnswering or OllamaLink.Offline => Lang.T("Try again"),
        _ => "",
    };
    public bool HasOllamaBoxAction => OllamaBoxAction.Length > 0;
    public bool OllamaBoxOpensWeb => OllamaState == OllamaLink.Missing;

    [RelayCommand]
    private Task OllamaBoxDo() => OllamaState switch
    {
        OllamaLink.Stopped => StartOllama(),
        OllamaLink.Missing => Task.Run(GetOllama),
        _ => ConnectOllama(),
    };

    /// <summary>
    /// Asks Ollama at the address for its version and models. The newest check wins: typing a new address while one
    /// runs drops the old one's answer.
    /// </summary>
    [RelayCommand]
    private async Task ConnectOllama()
    {
        int check = ++_check;
        string address = Ai.OllamaAddress;
        if (!Ollama.IsAddress(address))
        {
            Log.Write("ai-settings", $"Ollama address \"{address}\" isn't an address; not checked");
            OllamaAnswering = false;
            OllamaConnected = false;
            OllamaMissing = false;
            ShowRows(null);
            OllamaState = OllamaLink.BadAddress;
            return;
        }
        // A wrong address fixed: its red line goes at once; otherwise what shows stays until the answer.
        if (OllamaState == OllamaLink.BadAddress) OllamaState = OllamaLink.Checking;
        var models = Ollama.ModelsAsync(address);
        var version = Ollama.VersionAsync(address);
        var both = Task.WhenAll(models, version);
        if (await Task.WhenAny(both, Task.Delay(ShowCheckingAfter)) != both && check == _check) CheckingShown = true;
        await both;
        var found = models.Result;
        string? v = version.Result;
        if (check != _check) return;
        CheckingShown = false;
        _ollamaVersion = v;
        OllamaAnswering = found is not null;
        OllamaMissing = found is null && OllamaHere && !OllamaPicks.Installed();
        ShowRows(found);
        OllamaConnected = OllamaRows.Count > 0;
        // A model picked earlier that Ollama no longer has (removed, even the last one): forgotten, so nothing names it
        // ("while Ollama loads gemma4:12b" with no models left, 2026-10-24); the first that can look things up is used instead.
        if (found is not null && Ai.OllamaModel.Length > 0 && !OllamaRows.Any(m => m.Name == Ai.OllamaModel && m.CanLookUp))
            Set(a => a.OllamaModel = "", nameof(OllamaModel));
        MarkInUse();
        if (found is not null) await MarkLoaded(address);
        if (check != _check) return;
        OllamaState = found is not null ? OllamaLink.Connected
            : OllamaMissing ? OllamaLink.Missing
            : OllamaHere && OllamaPicks.Installed() ? OllamaLink.Stopped
            : !OllamaHere && Internet.IsOff() ? OllamaLink.Offline
            : OllamaLink.NotAnswering;
        // The line and box words depend on the rows and version as well as the state.
        foreach (var name in new[] { nameof(OllamaLine), nameof(HasOllamaBox), nameof(OllamaBoxTitle), nameof(OllamaBoxText), nameof(OllamaBoxCalm), nameof(OllamaBoxOpensWeb), nameof(DownloadOffText) })
            OnPropertyChanged(name);
        Log.Write("ai-settings", $"Ollama at {Ollama.Normalise(address)}: {OllamaState}{(v is null ? "" : $", version {v}")}, {OllamaRows.Count} models");
    }

    /// <summary>
    /// The models Ollama has loaded right now: how much of each really sits on the card, which is better than the estimate.
    /// Part of it off the card shows "Partly on your card" with why on hover.
    /// </summary>
    private async Task MarkLoaded(string address)
    {
        foreach (var row in OllamaRows.Where(r => r.CanLookUp))
            if (await Ollama.OnCardAsync(address, row.Name) is { } onCard)
                row.Loaded = onCard;
    }

    /// <summary>Starts Ollama, then waits for it to answer (up to 20 seconds) and connects.</summary>
    private async Task StartOllama()
    {
        if (!OllamaPicks.Start()) { await ConnectOllama(); return; }
        StartingOllama = true;
        CheckingShown = true;
        try
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                if (await Ollama.VersionAsync(Ai.OllamaAddress) is not null) break;
            }
        }
        finally { StartingOllama = false; }
        await ConnectOllama();
    }

    /// <summary>A problem removing a model, under the list.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRemoveProblem))] private string? _removeProblem;
    public bool HasRemoveProblem => RemoveProblem is not null;

    [RelayCommand]
    private void AskRemove(OllamaRow? row)
    {
        if (row is null) return;
        foreach (var other in OllamaRows) other.AskingRemove = false;
        RemoveProblem = null;
        row.AskingRemove = true;
    }

    [RelayCommand] private static void CancelRemove(OllamaRow? row) { if (row is not null) row.AskingRemove = false; }

    /// <summary>Removes it through Ollama, then reads the list again (the one in use gone: the next that can look things up).</summary>
    [RelayCommand]
    private async Task Remove(OllamaRow? row)
    {
        if (row is not { AskingRemove: true, Removing: false }) return;
        row.Removing = true;
        Log.Write("ai-settings", $"Removing {row.Name} from Ollama at {Ollama.Normalise(Ai.OllamaAddress)}");
        RemoveProblem = await Ollama.RemoveAsync(Ai.OllamaAddress, row.Name);
        row.Removing = false;
        row.AskingRemove = false;
        await ConnectOllama();
    }

    [RelayCommand]
    private static void GetOllama()
    {
        Log.Write("ai-settings", "Opening Ollama's download page");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OllamaDownloadPage) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("ai-settings", ex); }
    }

    /// <summary>A pick's line over its button: its size, where it goes and the room there, and how long when known.</summary>
    private void SetFacts(OllamaPickCard card)
    {
        string size = card.Pick.Gb.ToString("0.#", CultureInfo.CurrentCulture);
        card.HasRoom = true;
        card.TimeText = _lastSpeed is { } speed ? Lang.T($"about {Minutes(card.Pick.Gb * 1e9 / speed)}") : "";
        if (!OllamaHere) { card.Facts = Lang.T($"**{size} GB** to Ollama on {Ollama.Host(Ai.OllamaAddress)}"); return; }
        if (OllamaPicks.FreeSpace() is not { } space) { card.Facts = Lang.T($"**{size} GB**"); return; }
        string free = Units.Bytes(space.Free);
        card.HasRoom = space.Free > card.Pick.Gb * 1e9 * 1.1;
        card.Facts = card.HasRoom ? Lang.T($"**{size} GB** to {space.Drive} · {free} free") : Lang.T($"**{size} GB** won't fit on {space.Drive} · {free} free");
        if (!card.HasRoom) card.TimeText = "";
    }

    /// <summary>Not enough room for a model: the Storage page, to see what fills the drive.</summary>
    [RelayCommand] private void FreeUpSpace() => _ask.OpenPageAt?.Invoke(AiPages.Storage, null);

    /// <summary>One download at a time, and only with Ollama answering.</summary>
    private bool CanAskDownload(OllamaPickCard? card) => OllamaAnswering && !Picks.Any(c => c.IsDownloading);

    /// <summary>The speed the last download here ran at, for how long the next one takes; null until one has run.</summary>
    private static double? _lastSpeed;

    private static string Minutes(double seconds) => seconds < 90 ? Lang.T("a minute") : Lang.T($"{Math.Ceiling(seconds / 60):0} min");

    /// <summary>Ollama left for another way of answering: its downloads stop and a connect under way keeps its result to itself.</summary>
    private void StopOllamaWork()
    {
        foreach (var card in Picks) card.Stop?.Cancel();
        _check++;
    }

    /// <summary>Download pressed: it starts at once (its size and room were on the card already).</summary>
    [RelayCommand(CanExecute = nameof(CanAskDownload))]
    private async Task Download(OllamaPickCard? card)
    {
        if (card is not { IsIdle: true, HasRoom: true, OnOllama: false } || !CanAskDownload(card)) return;
        card.Problem = null;
        card.State = PickState.Downloading;
        DownloadCommand.NotifyCanExecuteChanged();
        card.Progress = 0;
        card.ProgressText = card.ProgressDone = Lang.T("Starting…");
        card.ProgressLeft = card.ProgressSpeed = "";
        using var stop = new CancellationTokenSource();
        card.Stop = stop;
        var samples = new Queue<(DateTime At, long Done)>();
        var progress = new Progress<OllamaPull>(p =>
        {
            // Ollama fetches a model in parts and says each part's size only as it starts: measured against the whole
            // model from the start, so the bar only goes forward ("0.9 GB" then "6.1 GB" read as two downloads, 2026-10-24).
            long whole = Math.Max(p.Total, (long)(card.Pick.Gb * 1e9));
            if (p.Status.StartsWith("verifying", StringComparison.OrdinalIgnoreCase) || p.Status.StartsWith("writing", StringComparison.OrdinalIgnoreCase))
            {
                card.Progress = 1;
                card.ProgressText = card.ProgressDone = Lang.T("Checking the download…");
                card.ProgressLeft = card.ProgressSpeed = "";
                return;
            }
            if (p.Total <= 0) { card.ProgressText = card.ProgressDone = Lang.T("Starting…"); return; }
            var now = DateTime.UtcNow;
            samples.Enqueue((now, p.Done));
            while (samples.Count > 2 && (now - samples.Peek().At).TotalSeconds > 6) samples.Dequeue();
            var first = samples.Peek();
            double seconds = (now - first.At).TotalSeconds;
            double speed = seconds >= 1 ? (p.Done - first.Done) / seconds : 0;
            if (speed > 0 && seconds >= 3) _lastSpeed = speed;
            card.Progress = Math.Clamp((double)p.Done / whole, 0, 1);
            card.ProgressText = DownloadText(p.Done, whole, speed);
            card.ProgressDone = Lang.T($"**{(p.Done / 1e9).ToString("0.0", CultureInfo.CurrentCulture)}** of {(whole / 1e9).ToString("0.#", CultureInfo.CurrentCulture)} GB");
            card.ProgressSpeed = speed >= 1 ? (speed / 1e6).ToString("0", CultureInfo.CurrentCulture) + " MB/s" : "";
            card.ProgressLeft = speed >= 1 ? ((whole - p.Done) / speed < 60 ? Lang.T("less than a minute left") : Lang.T($"about {Math.Ceiling((whole - p.Done) / speed / 60):0} min left")) : "";
        });
        string? problem;
        try { problem = await Ollama.PullAsync(Ai.OllamaAddress, card.Tag, progress, stop.Token); }
        catch (OperationCanceledException)
        {
            Log.Write("ai-settings", $"Download of {card.Tag} cancelled at {card.ProgressText}");
            card.Stop = null;
            card.State = PickState.Idle;
            DownloadCommand.NotifyCanExecuteChanged();
            return;
        }
        card.Stop = null;
        card.State = PickState.Idle;
        DownloadCommand.NotifyCanExecuteChanged();
        if (problem is not null) { card.Problem = problem; return; }
        // Done: read the list again, and use it.
        await ConnectOllama();
        UseOllama(card.Tag);
    }

    [RelayCommand] private static void StopDownload(OllamaPickCard? card) => card?.Stop?.Cancel();

    /// <summary>
    /// "3.1 of 6.6 GB · 18 MB/s · about 4 min left"; the speed and time once there are a few seconds to go on. In the
    /// same GB as the card's "6.6 GB download" (Ollama's own, 1,000,000,000 bytes), so the two sizes match.
    /// </summary>
    internal static string DownloadText(long done, long total, double bytesPerSecond)
    {
        string of = Lang.T($"{(done / 1e9).ToString("0.0", CultureInfo.CurrentCulture)} of {(total / 1e9).ToString("0.#", CultureInfo.CurrentCulture)} GB");
        if (bytesPerSecond < 1) return of;
        string speed = (bytesPerSecond / 1e6).ToString("0", CultureInfo.CurrentCulture) + " MB/s";
        double left = (total - done) / bytesPerSecond;
        string time = left < 60 ? Lang.T("less than a minute left") : Lang.T($"about {Math.Ceiling(left / 60):0} min left");
        return $"{of} · {speed} · {time}";
    }
}

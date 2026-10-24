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
    public string Fit { get; } = fit;
    public bool HasFit => Fit.Length > 0;
    public bool FitIsGood => fitKind == OllamaFit.Fits;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUse))] private bool _inUse;
    public bool CanUse => CanLookUp && !InUse;
}

public enum PickState { Idle, Asking, Downloading }

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

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsAsking), nameof(IsDownloading))] private PickState _state;
    public bool IsIdle => State == PickState.Idle;
    public bool IsAsking => State == PickState.Asking;
    public bool IsDownloading => State == PickState.Downloading;

    /// <summary>Already on Ollama: Use (or In use) in place of Download.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUse), nameof(NotOnOllama))] private bool _onOllama;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUse))] private bool _inUse;
    public bool NotOnOllama => !OnOllama;
    public bool CanUse => OnOllama && !InUse;

    /// <summary>"Download 8 GB? It goes to C: (412 GB free)."</summary>
    [ObservableProperty] private string _askText = "";
    /// <summary>"Download 8 GB": the button says what it does.</summary>
    [ObservableProperty] private string _confirmText = "";
    [ObservableProperty] private bool _hasRoom = true;
    [ObservableProperty] private double _progress;
    /// <summary>"3.1 of 8 GB · 18.4 MB/s · about 4 min left"</summary>
    [ObservableProperty] private string _progressText = "";
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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DownloadOff))] [NotifyCanExecuteChangedFor(nameof(AskDownloadCommand))] private bool _ollamaAnswering;
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
        OllamaPickCard Card(OllamaPick p, string label) => Picks.FirstOrDefault(c => c.Tag == p.Tag && !c.IsIdle)
            ?? new OllamaPickCard(p, label, OllamaPicks.Fit(p.Gb, vram), card);
        var cards = new List<OllamaPickCard> { Card(best, OllamaHere ? Lang.T("Best for this PC") : Lang.T("A good start")) };
        if (smaller is not null) cards.Add(Card(smaller, Lang.T("Smaller and quicker")));
        if (bigger is not null) cards.Add(Card(bigger, Lang.T("Bigger")));
        Picks = cards;
        MarkInUse();
        OnPropertyChanged(nameof(Picks));
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
        OllamaRows = found is null ? [] : [.. found
            .OrderByDescending(m => m.CanLookUp).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
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

    public bool LineChecking => OllamaState == OllamaLink.Checking;
    public bool LineOk => OllamaState == OllamaLink.Connected;
    public bool LineWarn => !LineChecking && !LineOk;
    public bool OllamaAddressBad => OllamaState == OllamaLink.BadAddress;

    public string OllamaLine => OllamaState switch
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
        OllamaState = OllamaLink.Checking;
        var models = Ollama.ModelsAsync(address);
        var version = Ollama.VersionAsync(address);
        var found = await models;
        string? v = await version;
        if (check != _check) return;
        _ollamaVersion = v;
        OllamaAnswering = found is not null;
        OllamaMissing = found is null && OllamaHere && !OllamaPicks.Installed();
        ShowRows(found);
        OllamaConnected = OllamaRows.Count > 0;
        // A model picked earlier that Ollama no longer has: the first one that can look things up instead.
        if (OllamaConnected && Ai.OllamaModel.Length > 0 && !OllamaRows.Any(m => m.Name == Ai.OllamaModel && m.CanLookUp))
            Set(a => a.OllamaModel = "", nameof(OllamaModel));
        MarkInUse();
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

    /// <summary>Starts Ollama, then waits for it to answer (up to 20 seconds) and connects.</summary>
    private async Task StartOllama()
    {
        if (!OllamaPicks.Start()) { await ConnectOllama(); return; }
        StartingOllama = true;
        OllamaState = OllamaLink.Checking;
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

    [RelayCommand]
    private static void GetOllama()
    {
        Log.Write("ai-settings", "Opening Ollama's download page");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OllamaDownloadPage) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("ai-settings", ex); }
    }

    /// <summary>Download: first the size and where it goes, to agree to. One download at a time.</summary>
    [RelayCommand(CanExecute = nameof(CanAskDownload))]
    private void AskDownload(OllamaPickCard? card)
    {
        if (card is null || !CanAskDownload(card)) return;
        foreach (var other in Picks.Where(c => c.IsAsking)) other.State = PickState.Idle;
        string size = card.Pick.Gb.ToString("0.#", CultureInfo.CurrentCulture);
        card.Problem = null;
        card.HasRoom = true;
        card.ConfirmText = Lang.T($"Download {size} GB");
        if (!OllamaHere) card.AskText = Lang.T($"Download {size} GB? It goes to Ollama on **{Ollama.Host(Ai.OllamaAddress)}**.");
        else if (OllamaPicks.FreeSpace() is { } space)
        {
            card.HasRoom = space.Free > card.Pick.Gb * (1L << 30) * 1.1;
            card.AskText = !card.HasRoom
                ? Lang.T($"There isn't room: it needs {size} GB and **{space.Drive}** has {Units.Bytes(space.Free)} free.")
                : _lastSpeed is { } speed
                    ? Lang.T($"Download {size} GB? It goes to **{space.Drive}** ({Units.Bytes(space.Free)} free) and takes about {Minutes(card.Pick.Gb * (1L << 30) / speed)} on your connection.")
                    : Lang.T($"Download {size} GB? It goes to **{space.Drive}** ({Units.Bytes(space.Free)} free).");
        }
        else card.AskText = Lang.T($"Download {size} GB?");
        card.State = PickState.Asking;
    }

    private bool CanAskDownload(OllamaPickCard? card) => OllamaAnswering && !Picks.Any(c => c.IsDownloading);

    /// <summary>The speed the last download here ran at, for how long the next one takes; null until one has run.</summary>
    private static double? _lastSpeed;

    private static string Minutes(double seconds) => seconds < 90 ? Lang.T("about a minute") : Lang.T($"{Math.Ceiling(seconds / 60):0} minutes");

    [RelayCommand] private static void CancelAsk(OllamaPickCard? card) { if (card is { IsAsking: true }) card.State = PickState.Idle; }

    [RelayCommand]
    private async Task Download(OllamaPickCard? card)
    {
        if (card is not { IsAsking: true, HasRoom: true }) return;
        card.State = PickState.Downloading;
        AskDownloadCommand.NotifyCanExecuteChanged();
        card.Progress = 0;
        card.ProgressText = Lang.T("Starting…");
        using var stop = new CancellationTokenSource();
        card.Stop = stop;
        var samples = new Queue<(DateTime At, long Done)>();
        var progress = new Progress<OllamaPull>(p =>
        {
            if (p.Total <= 0) { card.ProgressText = Lang.T("Starting…"); return; }
            var now = DateTime.UtcNow;
            samples.Enqueue((now, p.Done));
            while (samples.Count > 2 && (now - samples.Peek().At).TotalSeconds > 6) samples.Dequeue();
            var first = samples.Peek();
            double seconds = (now - first.At).TotalSeconds;
            double speed = seconds >= 1 ? (p.Done - first.Done) / seconds : 0;
            if (speed > 0 && seconds >= 3) _lastSpeed = speed;
            card.Progress = Math.Clamp((double)p.Done / p.Total, 0, 1);
            card.ProgressText = DownloadText(p.Done, p.Total, speed);
        });
        string? problem;
        try { problem = await Ollama.PullAsync(Ai.OllamaAddress, card.Tag, progress, stop.Token); }
        catch (OperationCanceledException)
        {
            Log.Write("ai-settings", $"Download of {card.Tag} cancelled at {card.ProgressText}");
            card.Stop = null;
            card.State = PickState.Idle;
            AskDownloadCommand.NotifyCanExecuteChanged();
            return;
        }
        card.Stop = null;
        card.State = PickState.Idle;
        AskDownloadCommand.NotifyCanExecuteChanged();
        if (problem is not null) { card.Problem = problem; return; }
        // Done: read the list again, and use it.
        await ConnectOllama();
        UseOllama(card.Tag);
    }

    [RelayCommand] private static void StopDownload(OllamaPickCard? card) => card?.Stop?.Cancel();

    /// <summary>"3.1 of 8 GB · 18.4 MB/s · about 4 min left"; the speed and time once there are a few seconds to go on.</summary>
    internal static string DownloadText(long done, long total, double bytesPerSecond)
    {
        string of = Lang.T($"{(done / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture)} of {(total / (double)(1L << 30)).ToString("0.#", CultureInfo.CurrentCulture)} GB");
        if (bytesPerSecond < 1) return of;
        string speed = Units.Bytes(bytesPerSecond) + "/s";
        double left = (total - done) / bytesPerSecond;
        string time = left < 60 ? Lang.T("less than a minute left") : Lang.T($"about {Math.Ceiling(left / 60):0} min left");
        return $"{of} · {speed} · {time}";
    }
}

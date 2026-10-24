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
    /// <summary>Over five, the list scrolls in its own box.</summary>
    public bool ManyOllamaRows => OllamaRows.Count > 5;

    public IReadOnlyList<OllamaPickCard> Picks { get; private set; } = [];

    /// <summary>"PICKED FOR THIS PC · RTX 3080 TI, 12 GB · 32 GB MEMORY" (upper case by the view's style).</summary>
    public string PicksHeading
    {
        get
        {
            if (!OllamaHere) return Lang.T($"Picked for Ollama at {Ollama.Host(Ai.OllamaAddress)}").ToUpper(CultureInfo.CurrentCulture);
            var bits = new List<string>();
            if (_card is not null) bits.Add(_vramGb is { } v ? $"{_card}, {v.ToString("0.#", CultureInfo.CurrentCulture)} GB" : _card);
            if (_ram is not null) bits.Add(Lang.T($"{_ram} memory"));
            return (bits.Count > 0 ? Lang.T("Picked for this PC") + " · " + string.Join(" · ", bits) : Lang.T("Picked for this PC")).ToUpper(CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Where downloads go: Ollama's own folder, never RigSight's.</summary>
    public string DownloadsGo => OllamaHere
        ? Lang.T($"Downloads go into Ollama's own models folder ({OllamaPicks.ModelsFolder()}), as \"ollama pull\" does, not into RigSight.")
        : Lang.T($"Downloads go to Ollama on {Ollama.Host(Ai.OllamaAddress)}. How a model fits there can't be checked from here.");

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
        OnPropertyChanged(nameof(DownloadsGo));
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
        if (!OllamaHere) card.AskText = Lang.T($"Download {size} GB? It goes to Ollama on {Ollama.Host(Ai.OllamaAddress)}.");
        else if (OllamaPicks.FreeSpace() is { } space)
        {
            card.HasRoom = space.Free > card.Pick.Gb * (1L << 30) * 1.1;
            card.AskText = card.HasRoom
                ? Lang.T($"Download {size} GB? It goes to {space.Drive} ({Units.Bytes(space.Free)} free).")
                : Lang.T($"There isn't room: it needs {size} GB and {space.Drive} has {Units.Bytes(space.Free)} free.");
        }
        else card.AskText = Lang.T($"Download {size} GB?");
        card.State = PickState.Asking;
    }

    private bool CanAskDownload(OllamaPickCard? card) => OllamaAnswering && !Picks.Any(c => c.IsDownloading);

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

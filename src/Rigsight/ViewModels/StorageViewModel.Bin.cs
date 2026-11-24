using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Services;

namespace Rigsight.ViewModels;

// Every drive's Recycle Bin as its own line on the Storage overview, emptied from there (approved mock-up B,
// 2026-11-23): its count and size, Open (File Explorer's Recycle Bin, to look or restore first) and Empty, which asks in
// a small box of RigSight's own that names how many, how much and on which drives. Windows' own question and progress
// box are off for it: RigSight has asked and shows its own spinner. After, "Recycle Bin emptied, 2.8 GB freed" for a
// few seconds, then the line goes.
public sealed partial class StorageViewModel
{
    /// <summary>Every drive's Recycle Bin, as "Space you could free" read it; null when it's empty.</summary>
    public CleanupItem? Bin => Cleanup.FirstOrDefault(i => i.IsRecycleBin && i.BinRoot is null);

    /// <summary>The line shows while the bin holds something and isn't being said as just emptied.</summary>
    public bool HasBin => Bin is not null && BinDoneText.Length == 0;
    public string BinCountText => Bin is { } b ? (b.Count == 1 ? Lang.T("1 deleted item") : Lang.T($"{b.Count:N0} deleted items")) : "";
    public string BinSizeText => Bin?.SizeText ?? "";

    /// <summary>Emptying, until Windows is done: the Empty button shows the spinner in place of its word.</summary>
    [ObservableProperty] private bool _binBusy;

    /// <summary>The question beside Empty.</summary>
    [ObservableProperty] private bool _confirmBinOpen;
    [ObservableProperty] private string _binAskTitle = "";
    [ObservableProperty] private string _binAskText = "";

    /// <summary>"Recycle Bin emptied, 2.8 GB freed" for a few seconds after emptying; empty otherwise.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBin), nameof(HasBinDone))] private string _binDoneText = "";
    public bool HasBinDone => BinDoneText.Length > 0;
    /// <summary>The emptied line is fading out.</summary>
    [ObservableProperty] private bool _binFading;

    /// <summary>Empties every drive's Recycle Bin without Windows' own box; true once emptied (tests swap it).</summary>
    internal Func<IntPtr, bool> EmptyBinQuiet { get; set; } = StorageScanner.EmptyRecycleBinQuiet;
    /// <summary>One drive's Recycle Bin, for naming the drives in the question (tests swap it).</summary>
    internal Func<string, (long Size, long Items)> BinOn { get; set; } = root => StorageScanner.RecycleBin(root);

    /// <summary>How long "Recycle Bin emptied" stays before it fades (tests set it to zero).</summary>
    internal TimeSpan BinDoneFor { get; set; } = TimeSpan.FromSeconds(4);

    private void BinChanged()
    {
        foreach (var name in new[] { nameof(Bin), nameof(HasBin), nameof(BinCountText), nameof(BinSizeText) }) OnPropertyChanged(name);
    }

    /// <summary>Open: the Recycle Bin in File Explorer, where things can be looked at or restored first.</summary>
    [RelayCommand]
    private static void OpenBin()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("storage: opening the Recycle Bin (expected File Explorer to open it)", ex); }
    }

    /// <summary>Empty: the question, naming how many, how much and on which drives.</summary>
    [RelayCommand]
    private async Task AskEmptyBin()
    {
        if (Bin is not { } bin || BinBusy) return;
        var roots = Volumes.Select(v => v.Root).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        var on = await Task.Run(() => roots.Where(r => BinOn(r).Size > 0).Select(r => r.TrimEnd('\\', ':')).ToList());
        BinAskTitle = bin.Count == 1 ? Lang.T("Delete 1 item permanently?") : Lang.T($"Delete {bin.Count:N0} items permanently?");
        string size = bin.SizeText;
        string where = on.Count switch
        {
            0 => Lang.T($"{size} in the Recycle Bin."),
            1 => Lang.T($"{size} on the {on[0]} drive."),
            _ => Lang.T($"{size} on the {Wording.JoinAnd(on)} drives."),
        };
        BinAskText = where + " " + Lang.T("Once the Recycle Bin is emptied, they can't be restored.");
        Log.Write("storage", $"Asked to empty the Recycle Bin: {bin.Count} items, {size}, found on {(on.Count == 0 ? "no fixed drive" : string.Join(", ", on))}");
        ConfirmBinOpen = true;
    }

    [RelayCommand] private void CancelEmptyBin() => ConfirmBinOpen = false;

    /// <summary>
    /// The question's Empty Recycle Bin. Like the bin inside a drive, it never runs on the window's own thread and a scan
    /// under way waits meanwhile (see <see cref="CleanupAction"/>).
    /// </summary>
    [RelayCommand]
    private async Task EmptyBinNow()
    {
        ConfirmBinOpen = false;
        if (Bin is not { } bin || BinBusy) return;
        var owner = Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        BinBusy = true;
        _hold.Reset();
        bool emptied;
        try { emptied = await OnOwnThread(() => EmptyBinQuiet(owner)); }
        catch (Exception ex) { Log.Error("storage: emptying the Recycle Bin", ex); emptied = false; }
        finally
        {
            _hold.Set();
            BinBusy = false;
        }
        // What Windows says is left: an empty bin is the proof, whatever the call returned (it fails on a bin already empty).
        var left = await Task.Run(() => StorageScanner.RecycleBin());
        Log.Write("storage", $"Emptied the Recycle Bin: expected {bin.Count} items, {bin.SizeText} gone; Windows {(emptied ? "said done" : "said it failed")}, {left.Items} items, {Units.Bytes(left.Size)} left");
        if (!emptied && left.Size > 0) return;
        long freed = Math.Max(0, bin.Size - left.Size);
        Cleanup.Remove(bin);
        _binsRead.Clear();
        BinDoneText = Lang.T($"Recycle Bin emptied, {Units.Bytes(freed)} freed");
        CleanupChanged();
        await LoadVolumesAsync();
        await Task.Delay(BinDoneFor);
        BinFading = true;
        if (BinDoneFor > TimeSpan.Zero) await Task.Delay(300);
        BinDoneText = "";
        BinFading = false;
    }
}

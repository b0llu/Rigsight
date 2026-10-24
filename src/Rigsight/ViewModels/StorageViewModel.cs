using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A line of the map's key: a kind of file and its colour.</summary>
public sealed record KindKey(string Name, System.Windows.Media.Brush Brush);

public sealed record VolumeRow(string Root, string Label, long Total, long Free, string Growth, bool GrowthIsUp) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>"fixed", "usb" or "network": USB and network drives are a small group of their own under the others.</summary>
    public string Kind { get; init; } = "fixed";
    /// <summary>What sort of drive, under its name: "USB drive", the network path.</summary>
    public string Sub { get; init; } = "";
    public bool HasSub => Sub.Length > 0;
    /// <summary>"E:", the drive tabs' word.</summary>
    public string Letter => Root.TrimEnd('\\');
    /// <summary>"91% full", beside its bar.</summary>
    public string FullText => Lang.T($"{UsedPercent:0}% full");
    /// <summary>Nearly full: its bar in the warning colour.</summary>
    public bool NearlyFull => UsedPercent >= 90;
    private string? _scanText;

    /// <summary>While this drive is being scanned: how far along, going by the space it has in use ("40%"). Null otherwise.</summary>
    public string? ScanText
    {
        get => _scanText;
        set
        {
            if (_scanText == value) return;
            bool was = IsScanning;
            _scanText = value;
            PropertyChanged?.Invoke(this, new(nameof(ScanText)));
            if (was != IsScanning) PropertyChanged?.Invoke(this, new(nameof(IsScanning)));
        }
    }

    public bool IsScanning => _scanText is not null;

    /// <summary>What a scan that has read <paramref name="bytes"/> shows: never 100 before it's done (some files can't be read, so it may never add up).</summary>
    public string ScanProgress(long bytes) => $"{(Used > 0 ? Math.Clamp(100 * bytes / Used, 0, 99) : 0)}%";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public long Used => Total - Free;
    public double UsedPercent => Total > 0 ? 100.0 * Used / Total : 0;
    public string Title => string.IsNullOrWhiteSpace(Label) ? Lang.T($"Local disk ({Root.TrimEnd('\\')})") : $"{Label} ({Root.TrimEnd('\\')})";
    public string UsageText => Lang.T($"{Units.Bytes(Used)} used of {Units.Bytes(Total)}");
    public string FreeText => Lang.T($"{Units.Bytes(Free)} free");
    /// <summary>What its scan button is called for a screen reader: each drive's button says the same words.</summary>
    public string ScanName => Lang.T($"See what's inside {Root.TrimEnd('\\')}");

    /// <summary>This drive's tab is the one shown in "What fills a drive".</summary>
    private bool _isPicked;
    public bool IsPicked
    {
        get => _isPicked;
        set { if (_isPicked == value) return; _isPicked = value; PropertyChanged?.Invoke(this, new(nameof(IsPicked))); }
    }

    /// <summary>This drive's tab is the one shown in "Space you could free".</summary>
    private bool _isCleanupPicked;
    public bool IsCleanupPicked
    {
        get => _isCleanupPicked;
        set { if (_isCleanupPicked == value) return; _isCleanupPicked = value; PropertyChanged?.Invoke(this, new(nameof(IsCleanupPicked))); }
    }

    /// <summary>The same drive with the same figures (read again every few minutes: an unchanged row isn't redrawn).</summary>
    public bool SameAs(VolumeRow other) => Root == other.Root && Label == other.Label && Total == other.Total && Free == other.Free && Growth == other.Growth && Kind == other.Kind;
}

public sealed record CleanupItem(string Title, string Description, long Size, string? Path, bool IsRecycleBin) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _busy;

    public string SizeText => Units.Bytes(Size);

    /// <summary>A drive's own Recycle Bin ("E:\"); null for every drive's.</summary>
    public string? BinRoot { get; init; }
    /// <summary>Found by a scan ("big files untouched for a year"): Review shows that drive's largest files.</summary>
    public string? ReviewRoot { get; init; }

    /// <summary>What its button says: the Recycle Bin is emptied, a scan's finding reviewed, anything else opened to look at.</summary>
    public string ActionText => IsRecycleBin ? Lang.T("Empty") : ReviewRoot is not null ? Lang.T("Review") : Lang.T("Open");

    /// <summary>Its button was pressed and Windows is still at it (the Recycle Bin being emptied).</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            PropertyChanged?.Invoke(this, new(nameof(Busy)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class StorageViewModel(ReportService reports, LiveData live) : ObservableObject
{
    private CancellationTokenSource? _scanCts;

    public LiveData Live { get; } = live;
    /// <summary>The drives inside the PC, the fullest first (approved mock-up 2026-10-24: one row each, not two-column cards).</summary>
    public ObservableCollection<VolumeRow> Volumes { get; } = [];
    /// <summary>USB and network drives, a small group under the others (they never showed before).</summary>
    public ObservableCollection<VolumeRow> OtherVolumes { get; } = [];
    public bool HasOtherVolumes => OtherVolumes.Count > 0;

    /// <summary>The first five, the rest behind "Show N more" (ten drives flooded the top of the page).</summary>
    public const int DrivesShown = 5;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShownVolumes), nameof(MoreDrivesText), nameof(HasMoreDrives))] private bool _allDrives;
    public IReadOnlyList<VolumeRow> ShownVolumes => AllDrives ? [.. Volumes] : [.. Volumes.Take(DrivesShown)];
    public bool HasMoreDrives => Volumes.Count > DrivesShown;
    public string MoreDrivesText => AllDrives ? Lang.T("Show fewer") : Volumes.Count - DrivesShown == 1 ? Lang.T("Show 1 more") : Lang.T($"Show {Volumes.Count - DrivesShown} more");
    public string DrivesSummary => Volumes.Count == 0 ? "" : Lang.T($"{Volumes.Count + OtherVolumes.Count} drives · {Units.Bytes(Volumes.Sum(v => v.Used))} of {Units.Bytes(Volumes.Sum(v => v.Total))} used");
    [RelayCommand] private void ToggleDrives() => AllDrives = !AllDrives;

    /// <summary>Every drive's tab above the scan: the ones inside, then USB and network.</summary>
    public IReadOnlyList<VolumeRow> DriveTabs => [.. Volumes.OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase), .. OtherVolumes];

    /// <summary>The drive whose scan is shown ("E:\"); null for a folder picked with Pick a folder.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotScannedText), nameof(ShowNotScanned), nameof(PickedLetter), nameof(ScanPickedText))] private string? _pickedRoot;
    public string PickedLetter => PickedRoot?.TrimEnd('\\') ?? "";
    public string ScanPickedText => Lang.T($"Scan {PickedLetter}");
    /// <summary>The drive picked hasn't been scanned yet this run: a line and its Scan button where the result goes.</summary>
    public bool ShowNotScanned => PickedRoot is not null && Result is null && !IsScanning;
    public string NotScannedText => Lang.T($"{PickedLetter} hasn't been scanned yet. A scan reads only file sizes; on a big hard drive it can take a few minutes.");

    /// <summary>While scanning: how far along (by the space in use), how much was read, and the folder it's in now.</summary>
    [ObservableProperty] private double _scanPercent;
    [ObservableProperty] private string _scanCounts = "";
    [ObservableProperty] private string _scanFolder = "";

    /// <summary>The item pointed at, in the list or on the map: the other one lights it too.</summary>
    [ObservableProperty] private FolderNode? _hot;
    partial void OnHotChanged(FolderNode? oldValue, FolderNode? newValue)
    {
        if (oldValue is not null) oldValue.IsHot = false;
        if (newValue is not null) newValue.IsHot = true;
    }

    /// <summary>The kinds of files in the folder shown, for the map's key: only those it has.</summary>
    public IReadOnlyList<KindKey> Kinds => Current is null ? [] :
        [.. Enumerable.Range(0, FolderNode.KindCount).Where(k => Current.Kinds[k] > 0).OrderByDescending(k => Current.Kinds[k])
            .Select(k => new KindKey(FileKinds.Name((FileKind)k), Controls.Treemap.KindBrush((FileKind)k)))];

    [RelayCommand] private void ShowFiles() => ScanView = ScanView == "Files" ? "Map" : "Files";
    public bool ShowingFiles => ScanView == "Files";
    partial void OnScanViewChanged(string value) => OnPropertyChanged(nameof(ShowingFiles));
    public ObservableCollection<CleanupItem> Cleanup { get; } = [];
    public ObservableCollection<FolderNode> Breadcrumbs { get; } = [];

    [ObservableProperty] private bool _cleanupLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResult), nameof(ShowScanLoader), nameof(ShowNotScanned))] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResult), nameof(ShowScanLoader), nameof(FilesTitle), nameof(ShowNotScanned))] private ScanResult? _result;

    /// <summary>What the scan now running reads, while it isn't what is on screen (another drive, another folder).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResult), nameof(ShowScanLoader))] private bool _scanningElsewhere;

    /// <summary>
    /// The last scan is on screen: not while a scan of something else runs (the user: scanning E: with C:'s folders
    /// still listed under "Scanning E:"). It comes back if that scan is cancelled; the Refresh button's scan of the
    /// same place keeps it up.
    /// </summary>
    public bool ShowResult => Result is not null && !(IsScanning && ScanningElsewhere);

    /// <summary>
    /// While scanning something not on screen (a first scan, another drive), the progress (percent, files, size, folder)
    /// is where the result goes; a rescan of what's shown keeps it up, with its progress in the status line.
    /// </summary>
    public bool ShowScanLoader => IsScanning && (Result is null || ScanningElsewhere);

    /// <summary>Above the largest files, where the folders' path is in the other two views: they are of the whole scan.</summary>
    public string FilesTitle => Result is { } r ? Lang.T($"The {r.LargestFiles.Count} largest files in {r.Root.Path}") : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentItems), nameof(CanGoUp), nameof(Kinds))]
    private FolderNode? _current;

    [ObservableProperty] private string _scanView = "Map";

    /// <summary>"Space you could free" has been worked out at least once; until then its card shows the loader.</summary>
    [ObservableProperty] private bool _cleanupRead;

    public IReadOnlyList<FolderNode> CurrentItems => Current?.Children ?? [];
    public bool CanGoUp => Current?.Parent is not null;

    public async Task RefreshAsync()
    {
        await LoadVolumesAsync();
        if (Cleanup.Count == 0 && !CleanupLoading) _cleanupLoad = LoadCleanupAsync();
    }

    private Task? _cleanupLoad;

    /// <summary>The space-to-free list, looked for if it hasn't been yet (the page's Export box waits for it rather than save an empty list).</summary>
    internal async Task CleanupReadyAsync()
    {
        if (Cleanup.Count == 0 && !CleanupLoading) _cleanupLoad = LoadCleanupAsync();
        while (CleanupLoading) await Task.Delay(200);
    }

    private async Task LoadVolumesAsync()
    {
        var history = await reports.DriveHistoryAsync(8) ?? [];
        // Read off the window's thread: a network drive that's gone can take seconds to answer.
        var rows = await Task.Run(() => ReadDrives(history));
        Sync(Volumes, [.. rows.Where(r => r.Kind == "fixed").OrderByDescending(r => r.UsedPercent)]);
        Sync(OtherVolumes, [.. rows.Where(r => r.Kind != "fixed").OrderBy(r => r.Root, StringComparer.OrdinalIgnoreCase)]);
        foreach (var name in new[] { nameof(ShownVolumes), nameof(HasMoreDrives), nameof(MoreDrivesText), nameof(DrivesSummary), nameof(DriveTabs), nameof(HasOtherVolumes), nameof(CleanupTabs) })
            OnPropertyChanged(name);
        if (PickedRoot is null && Result is null && !IsScanning && Volumes.FirstOrDefault() is { } fullest) PickedRoot = fullest.Root;
        foreach (var v in DriveTabs) v.IsPicked = string.Equals(v.Root, PickedRoot, StringComparison.OrdinalIgnoreCase);
        CleanupChanged();
    }

    /// <summary>The list made like <paramref name="rows"/>, changing only what changed (it was cleared and rebuilt every five minutes, which flickered).</summary>
    private static void Sync(ObservableCollection<VolumeRow> list, List<VolumeRow> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            int at = -1;
            for (int j = i; j < list.Count; j++) if (list[j].Root == rows[i].Root) { at = j; break; }
            if (at < 0) { list.Insert(i, rows[i]); continue; }
            if (at != i) list.Move(at, i);
            if (!list[i].SameAs(rows[i])) { rows[i].ScanText = list[i].ScanText; list[i] = rows[i]; }
        }
        while (list.Count > rows.Count) list.RemoveAt(list.Count - 1);
    }

    private static List<VolumeRow> ReadDrives(IReadOnlyList<DriveDay> history)
    {
        var rows = new List<VolumeRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            string kind = d.DriveType switch { DriveType.Fixed => "fixed", DriveType.Removable => "usb", DriveType.Network => "network", _ => "" };
            if (kind.Length == 0) continue;
            long total, free;
            try
            {
                if (!d.IsReady) continue;
                (total, free) = (d.TotalSize, d.TotalFreeSpace);
            }
            catch (IOException) { continue; } // gone between "ready" and reading its size (a drive unplugged): left out
            catch (UnauthorizedAccessException) { continue; }
            if (total <= 0) continue;
            string growth = "";
            bool up = false;
            var weekAgo = history.Where(h => h.Drive == d.Name && TimeUtil.FromUnix(h.Day) <= DateTime.Today.AddDays(-6)).MinBy(h => h.Day)
                          ?? history.Where(h => h.Drive == d.Name && TimeUtil.FromUnix(h.Day) < DateTime.Today).MinBy(h => h.Day);
            if (weekAgo is not null)
            {
                double usedGb = (total - free) / 1073741824.0;
                double diff = usedGb - weekAgo.UsedGb;
                int days = (int)(DateTime.Today - TimeUtil.FromUnix(weekAgo.Day)).TotalDays;
                if (Math.Abs(diff) >= 0.1)
                {
                    up = diff > 0;
                    string change = $"{(diff > 0 ? "+" : "−")}{Units.Bytes(Math.Abs(diff) * 1073741824)}";
                    growth = days == 1 ? Lang.T($"{change} in 1 day") : Lang.T($"{change} in {days} days");
                }
                else
                {
                    growth = days == 1 ? Lang.T("No change in 1 day") : Lang.T($"No change in {days} days");
                }
            }
            string label;
            try { label = d.VolumeLabel; } catch (IOException) { label = ""; } catch (UnauthorizedAccessException) { label = ""; }
            string sub = kind switch
            {
                "usb" => Lang.T("USB drive"),
                "network" => NetworkPath(d.Name) is { } unc ? unc : Lang.T("Network drive"),
                _ => "",
            };
            rows.Add(new VolumeRow(d.Name, label, total, free, growth, up) { Kind = kind, Sub = sub });
        }
        return rows;
    }

    /// <summary>"\\nas\share" for a mapped drive letter; null when Windows doesn't say.</summary>
    private static string? NetworkPath(string root)
    {
        try
        {
            var sb = new System.Text.StringBuilder(512);
            int size = sb.Capacity;
            return WNetGetConnection(root.TrimEnd('\\'), sb, ref size) == 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [System.Runtime.InteropServices.DllImport("mpr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, System.Text.StringBuilder remoteName, ref int length);

    /// <summary>What goes in "Space you could free" (tests swap it: the real one walks the user's own folders).</summary>
    internal Func<List<CleanupItem>> FindCleanup { get; set; } = FindCleanupItems;

    /// <summary>How long after the page opens the cleanup list starts being worked out: the page is drawn first.</summary>
    internal TimeSpan CleanupDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Adding up the folders (some hold hundreds of thousands of files) is never in the page's way: it starts a moment
    /// after the page is up, on a thread of its own below normal priority, and the list shows its loader until then.
    /// </summary>
    private async Task LoadCleanupAsync()
    {
        CleanupLoading = true;
        try
        {
            if (CleanupDelay > TimeSpan.Zero) await Task.Delay(CleanupDelay);
            var items = await Task.Factory.StartNew(() =>
            {
                var thread = Thread.CurrentThread;
                thread.Priority = ThreadPriority.BelowNormal;
                return FindCleanup();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Cleanup.Clear();
            foreach (var i in items) Cleanup.Add(i);
            CleanupChanged();
        }
        catch (Exception ex)
        {
            Log.Error("storage: Space you could free (expected the list of caches)", ex);
        }
        finally
        {
            // The loader goes either way: a failed read must not spin for ever.
            CleanupLoading = false;
            CleanupRead = true;
            OnPropertyChanged(nameof(CleanupEmpty));
        }
    }

    /// <summary>The page's "Space you could free" list, read again (for the AI's look at a scanned drive).</summary>
    public static List<CleanupItem> CleanupItems() => FindCleanupItems();

    private static List<CleanupItem> FindCleanupItems()
    {
        var list = new List<CleanupItem>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        void Add(string title, string description, string path, Func<DiskEntry, bool>? filter = null)
        {
            long size = StorageScanner.FolderSize(path, filter);
            if (size >= 50L << 20) list.Add(new CleanupItem(title, description, size, path, false));
        }

        Add(Lang.T("Temporary files"), Lang.T("Leftovers from installers and apps. Safe to delete anything not currently in use."), Path.GetTempPath());
        Add(Lang.T("Windows temporary files"), Lang.T("System temp folder."), Path.Combine(windows, "Temp"));
        Add(Lang.T("Old downloads"), Lang.T("Files in Downloads you haven't touched in 90+ days."), downloads,
            f => f.LastWrite < DateTime.Now.AddDays(-90) && f.LastAccess < DateTime.Now.AddDays(-90));
        Add(Lang.T("Windows Update cache"), Lang.T("Already-installed update files. Disk Cleanup can remove these."), Path.Combine(windows, "SoftwareDistribution", "Download"));
        Add(Lang.T("NVIDIA shader cache"), Lang.T("Rebuilt automatically by games; can be cleared if it gets large."), Path.Combine(local, "NVIDIA", "DXCache"));
        Add(Lang.T("DirectX shader cache"), Lang.T("Rebuilt automatically; Disk Cleanup can clear it."), Path.Combine(local, "D3DSCache"));
        Add(Lang.T("AMD shader cache"), Lang.T("Rebuilt automatically by games."), Path.Combine(local, "AMD", "DxCache"));
        Add(Lang.T("Crash dumps"), Lang.T("Memory dumps from crashed apps."), Path.Combine(local, "CrashDumps"));
        Add(Lang.T("Previous Windows installation"), Lang.T("Left after a Windows upgrade. Remove it with Disk Cleanup."), Path.Combine(Path.GetPathRoot(windows)!, "Windows.old"));

        var (binSize, binItems) = StorageScanner.RecycleBin();
        if (binSize > 0)
            list.Add(new CleanupItem(Lang.T("Recycle Bin"), binItems == 1 ? Lang.T("1 deleted item.") : Lang.T($"{binItems:N0} deleted items."), binSize, null, true));
        return list.OrderByDescending(i => i.Size).ToList();
    }

    /// <summary>
    /// A scan the user asked for is done: the page goes to what it found, which is below the drives (a user who pressed
    /// "See what's inside" saw nothing happen and thought the app had crashed). Until then the drive's own card says how
    /// far along it is. Not for the Refresh button's scan, nor one that was cancelled or replaced.
    /// </summary>
    public event Action? ScanDone;
    /// <summary>Review pressed on a scan's finding: the page goes up to the scan, showing it.</summary>
    public event Action? ReviewRequested;
    private bool _refreshing;

    [RelayCommand]
    private async Task Scan(string? root)
    {
        if (string.IsNullOrEmpty(root)) return;
        _scanCts?.Cancel();
        bool asked = !_refreshing;
        // A scan started while another runs takes over: the one it replaced ends without touching what's on screen
        // (it used to say "Scan cancelled." and hide the progress and Cancel button of the scan still going).
        var scan = _scanCts = new CancellationTokenSource();
        ScanningElsewhere = Result is { } shown && !string.Equals(shown.Root.Path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        // The drive's tab is picked (a folder picks none).
        _picking = true;
        PickedRoot = DriveScans.Letter(root) is { } l ? l + @":\" : null;
        _picking = false;
        foreach (var v in DriveTabs) v.IsPicked = string.Equals(v.Root, PickedRoot, StringComparison.OrdinalIgnoreCase);
        ScanPercent = 0;
        ScanCounts = "";
        ScanFolder = root;
        IsScanning = true;
        ScanStatus = Lang.T($"Scanning {root}…");
        // A drive's own card says so too (the drives may be read again meanwhile: the card is found by its drive each time).
        VolumeRow? Card() => Volumes.FirstOrDefault(v => string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase));
        foreach (var v in Volumes) v.ScanText = null;
        Card()?.ScanText = "0%";
        var progress = new Progress<(long Files, long Bytes)>(p =>
        {
            if (_scanCts != scan) return;
            ScanStatus = Lang.T($"Scanning {root}…  {p.Files:N0} files · {Units.Bytes(p.Bytes)}");
            ScanCounts = Lang.T($"{p.Files:N0} files · {Units.Bytes(p.Bytes)}");
            if (Card() is { } card)
            {
                card.ScanText = card.ScanProgress(p.Bytes);
                ScanPercent = card.Used > 0 ? Math.Clamp(100.0 * p.Bytes / card.Used, 0, 99) : 0;
            }
        });
        var where = new Progress<string>(f => { if (_scanCts == scan) ScanFolder = f; });
        try
        {
            var result = await StorageScanner.ScanAsync(root, progress, scan.Token, _hold, where);
            if (_scanCts != scan) return;
            Result = result;
            // A whole drive's scan is there for the AI too ("what's using the space on E?").
            DriveScans.Keep(root, result);
            Breadcrumbs.Clear();
            Open(Result.Root);
            ScanStatus = Lang.T($"{Units.Bytes(Result.Root.Size)} in {Result.FileCount:N0} files · scanned in {Result.Elapsed.TotalSeconds:0.0}s");
            CleanupChanged();
            if (asked) ScanDone?.Invoke();
        }
        catch (OperationCanceledException)
        {
            if (_scanCts == scan) ScanStatus = Lang.T("Scan cancelled.");
        }
        finally
        {
            if (_scanCts == scan)
            {
                IsScanning = false;
                foreach (var v in Volumes) v.ScanText = null;
                // Cancelled with nothing kept: the drive picked shows its kept scan again, or its Scan line.
                if (Result is null || ScanningElsewhere) ShowPicked();
            }
        }
    }

    private bool _picking;

    partial void OnPickedRootChanged(string? value)
    {
        if (_picking) return;
        ShowPicked();
    }

    [RelayCommand] private void PickDrive(string? root) { if (root is not null && !IsScanning) PickedRoot = root; }

    /// <summary>One of the smaller folders, kept as a total only: scanned now (it's small), then opened.</summary>
    private async Task OpenScannedAsync(FolderNode node)
    {
        try
        {
            var scan = await StorageScanner.ScanAsync(node.Path, new Progress<(long, long)>(_ => { }), CancellationToken.None);
            node.Children = scan.Root.Children;
            foreach (var c in node.Children) c.Parent = node;
            node.NeedsScan = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Write("storage", $"Couldn't scan {node.Path} to open it: {ex.Message}");
            node.NeedsScan = false;
        }
        Open(node);
    }

    /// <summary>The drive picked: its scan kept this run (no rescan to switch drives), or nothing yet.</summary>
    private void ShowPicked()
    {
        foreach (var v in DriveTabs) v.IsPicked = string.Equals(v.Root, PickedRoot, StringComparison.OrdinalIgnoreCase);
        if (PickedRoot is null || DriveScans.Letter(PickedRoot) is not { } letter) return;
        if (DriveScans.Get(letter) is { } kept)
        {
            Result = kept.Result;
            ScanningElsewhere = false;
            Breadcrumbs.Clear();
            Open(kept.Result.Root);
            ScanStatus = Lang.T($"Scanned {Ago(kept.At)} · {kept.Result.FileCount:N0} files");
        }
        else
        {
            Result = null;
            Current = null;
            Breadcrumbs.Clear();
            ScanStatus = "";
        }
        OnPropertyChanged(nameof(ShowNotScanned));
    }

    private static string Ago(DateTime at)
    {
        var gone = DateTime.Now - at;
        return gone.TotalMinutes < 1 ? Lang.T("just now") : gone.TotalMinutes < 60 ? Lang.T($"{gone.TotalMinutes:0} min ago") : Lang.T($"at {TimeText.Time(at)}");
    }

    /// <summary>"Scan again" for what's shown: the picked drive, or the folder scanned.</summary>
    [RelayCommand]
    private Task ScanAgain() => Scan(PickedRoot ?? Result?.Root.Path);

    [RelayCommand]
    private async Task ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("Choose a folder to scan") };
        if (dialog.ShowDialog() == true) await Scan(dialog.FolderName);
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    public void Open(FolderNode? node)
    {
        if (node is null) return;
        if (node.IsFile) { Reveal(node.Path); return; }
        if (!node.CanOpen) return;
        if (node.NeedsScan) { _ = OpenScannedAsync(node); return; }
        // A folder of files only, or "Files in this folder": its files are read now (the scan keeps folders, not files).
        if (node.Children.Count == 0 && node.Files > 0)
        {
            try { node.Children = StorageScanner.FilesIn(node); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("storage", $"Couldn't list {node.Path}: {ex.Message}"); }
            if (node.Children.Count == 0) return;
        }
        Hot = null;
        Current = node;
        Breadcrumbs.Clear();
        for (var n = node; n is not null; n = n.Parent) Breadcrumbs.Insert(0, n);
    }

    [RelayCommand]
    private void Up()
    {
        if (Current?.Parent is { } p) Open(p);
    }

    [RelayCommand]
    private static void Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { }
    }

    // ── Space you could free, for the drive picked (approved mock-up 2026-10-24) ────────────────────────────────
    /// <summary>The system drive's places (temp files, caches, the Recycle Bin), as read by <see cref="FindCleanup"/>.</summary>
    private static string SystemRoot => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    /// <summary>The drive whose cleanup is shown; the system drive until another is picked.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CleanupShown), nameof(CleanupTitle), nameof(CleanupHint), nameof(HasCleanupHint), nameof(CleanupEmpty))]
    private string _cleanupRoot = SystemRoot;
    partial void OnCleanupRootChanged(string value) => CleanupChanged();

    public IReadOnlyList<VolumeRow> CleanupTabs => [.. Volumes.OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase)];
    [RelayCommand] private void PickCleanup(string? root) { if (root is not null) CleanupRoot = root; }

    public string CleanupTitle => Lang.T($"Space you could free on {CleanupRoot.TrimEnd('\\')}");

    /// <summary>What the drive picked has to free: the system drive's places, the drive's Recycle Bin, and what its scan found.</summary>
    public IReadOnlyList<CleanupItem> CleanupShown { get; private set; } = [];
    public bool CleanupEmpty => CleanupRead && CleanupShown.Count == 0 && !HasCleanupHint;

    /// <summary>A drive not scanned yet: its big old files show once it is.</summary>
    public string CleanupHint => DriveScans.Letter(CleanupRoot) is { } l && !DriveScans.Has(l)
        ? Lang.T($"Scan {CleanupRoot.TrimEnd('\\')} to also see the big files on it you haven't changed in a year.") : "";
    public bool HasCleanupHint => CleanupHint.Length > 0;
    [RelayCommand] private Task ScanCleanupDrive() => Scan(CleanupRoot);

    private void CleanupChanged()
    {
        var list = new List<CleanupItem>();
        bool system = string.Equals(CleanupRoot, SystemRoot, StringComparison.OrdinalIgnoreCase);
        if (system) list.AddRange(Cleanup);
        else if (!_binsRead.TryGetValue(CleanupRoot, out var bin)) _ = ReadBinAsync(CleanupRoot);
        else if (bin.Size > 0)
            list.Add(new CleanupItem(Lang.T("Recycle Bin"), bin.Items == 1 ? Lang.T($"1 deleted item on {CleanupRoot.TrimEnd('\\')}.") : Lang.T($"{bin.Items:N0} deleted items on {CleanupRoot.TrimEnd('\\')}."),
                bin.Size, null, true) { BinRoot = CleanupRoot });
        if (DriveScans.Letter(CleanupRoot) is { } letter && DriveScans.Get(letter) is { } kept)
        {
            var old = kept.Result.LargestFiles.Where(f => f.LastWrite > DateTime.MinValue && f.LastWrite < DateTime.Now.AddYears(-1)).ToList();
            if (old.Count > 0 && old.Sum(f => f.Size) >= 50L << 20)
                list.Add(new CleanupItem(Lang.T("Big files unchanged for a year"),
                    old.Count == 1 ? Lang.T("1 file · from the scan") : Lang.T($"{old.Count} files · from the scan"), old.Sum(f => f.Size), null, false) { ReviewRoot = CleanupRoot });
        }
        CleanupShown = [.. list.OrderByDescending(i => i.Size)];
        foreach (var v in Volumes) v.IsCleanupPicked = string.Equals(v.Root, CleanupRoot, StringComparison.OrdinalIgnoreCase);
        foreach (var name in new[] { nameof(CleanupShown), nameof(CleanupHint), nameof(HasCleanupHint), nameof(CleanupEmpty), nameof(CleanupTitle) }) OnPropertyChanged(name);
    }

    private readonly Dictionary<string, (long Size, long Items)> _binsRead = new(StringComparer.OrdinalIgnoreCase);

    private async Task ReadBinAsync(string root)
    {
        var bin = await Task.Run(() => StorageScanner.RecycleBin(root));
        _binsRead[root] = bin;
        if (string.Equals(root, CleanupRoot, StringComparison.OrdinalIgnoreCase)) CleanupChanged();
    }

    /// <summary>A running scan waits while this is reset (see <see cref="CleanupAction"/>).</summary>
    private readonly ManualResetEventSlim _hold = new(true);

    /// <summary>Empties the Recycle Bin, with Windows' own question and progress; true once emptied (tests swap it).</summary>
    internal Func<IntPtr, bool> EmptyBin { get; set; } = StorageScanner.EmptyRecycleBin;

    /// <summary>
    /// A cleanup row's button. Emptying the Recycle Bin can take minutes, so it never runs on the window's own thread
    /// (the window stopped answering until Windows was done), and a scan under way waits meanwhile: four threads
    /// reading the same drive made the deleting crawl. After it the drives' free space is read again.
    /// </summary>
    [RelayCommand]
    private async Task CleanupAction(CleanupItem? item)
    {
        if (item is null || item.Busy) return;
        if (item.ReviewRoot is { } review)
        {
            // The scan's big old files: that drive's largest files, in the scan above.
            PickedRoot = review;
            ScanView = "Files";
            ReviewRequested?.Invoke();
            return;
        }
        if (!item.IsRecycleBin)
        {
            Reveal(item.Path);
            return;
        }
        var owner = Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        item.Busy = true;
        _hold.Reset();
        bool emptied;
        try { emptied = await OnOwnThread(() => item.BinRoot is { } binRoot ? StorageScanner.EmptyRecycleBin(owner, binRoot) : EmptyBin(owner)); }
        catch { emptied = false; }
        finally
        {
            _hold.Set();
            item.Busy = false;
        }
        if (!emptied) return;
        Cleanup.Remove(item);
        if (item.BinRoot is { } emptiedRoot) _binsRead.Remove(emptiedRoot);
        CleanupChanged();
        await LoadVolumesAsync();
    }

    /// <summary>Runs shell work on a thread of its own, set up as the shell wants one (single-threaded apartment).</summary>
    private static Task<T> OnOwnThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Rigsight shell work" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>
    /// The page's Refresh button (after deleting something, say): the drives, the cleanup list and, if a drive or folder
    /// was scanned, that scan again, back in the folder that was open.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAll()
    {
        var again = Result is { } r && !IsScanning ? (Root: r.Root.Path, Folder: Current?.Path) : default;
        await Task.WhenAll(LoadVolumesAsync(), LoadCleanupAsync());
        if (again.Root is null) return;
        _refreshing = true;
        try { await Scan(again.Root); }
        finally { _refreshing = false; }
        if (again.Folder is not null && Find(Result?.Root, again.Folder) is { } folder) Open(folder);
    }

    /// <summary>The folder at <paramref name="path"/> in a scan, if it's still there.</summary>
    internal static FolderNode? Find(FolderNode? node, string path)
    {
        if (node is null) return null;
        if (!node.IsBucket && string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var child in node.Children)
            if (!child.IsBucket && path.StartsWith(child.Path, StringComparison.OrdinalIgnoreCase) && Find(child, path) is { } found)
                return found;
        return null;
    }
}

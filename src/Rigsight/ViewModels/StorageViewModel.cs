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

public sealed record VolumeRow(string Root, string Label, long Total, long Free, string Growth, bool GrowthIsUp) : System.ComponentModel.INotifyPropertyChanged
{
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
    public string Title => string.IsNullOrWhiteSpace(Label) ? $"Local disk ({Root.TrimEnd('\\')})" : $"{Label} ({Root.TrimEnd('\\')})";
    public string UsageText => $"{Units.Bytes(Used)} used of {Units.Bytes(Total)}";
    public string FreeText => $"{Units.Bytes(Free)} free";
    /// <summary>What its scan button is called for a screen reader: each drive's button says the same words.</summary>
    public string ScanName => $"See what's inside {Root.TrimEnd('\\')}";
}

public sealed record CleanupItem(string Title, string Description, long Size, string? Path, bool IsRecycleBin) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _busy;

    public string SizeText => Units.Bytes(Size);

    /// <summary>What its button says: the Recycle Bin is emptied, anything else is opened for the user to look at.</summary>
    public string ActionText => IsRecycleBin ? "Empty" : "Open";

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
    public ObservableCollection<VolumeRow> Volumes { get; } = [];
    public ObservableCollection<CleanupItem> Cleanup { get; } = [];
    public ObservableCollection<FolderNode> Breadcrumbs { get; } = [];

    [ObservableProperty] private bool _cleanupLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResult), nameof(ShowScanLoader))] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResult), nameof(ShowScanLoader), nameof(FilesTitle))] private ScanResult? _result;

    /// <summary>What the scan now running reads, while it isn't what is on screen (another drive, another folder).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResult), nameof(ShowScanLoader))] private bool _scanningElsewhere;

    /// <summary>
    /// The last scan is on screen: not while a scan of something else runs (the user: scanning E: with C:'s folders
    /// still listed under "Scanning E:"). It comes back if that scan is cancelled; the Refresh button's scan of the
    /// same place keeps it up.
    /// </summary>
    public bool ShowResult => Result is not null && !(IsScanning && ScanningElsewhere);

    /// <summary>The box holds a loader: a scan of something else is running where a result was.</summary>
    public bool ShowScanLoader => Result is not null && IsScanning && ScanningElsewhere;

    /// <summary>Above the largest files, where the folders' path is in the other two views: they are of the whole scan.</summary>
    public string FilesTitle => Result is { } r ? $"The {r.LargestFiles.Count} largest files in {r.Root.Path}" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentItems), nameof(CanGoUp))]
    private FolderNode? _current;

    [ObservableProperty] private string _scanView = "Map";

    /// <summary>"Space you could free" has been worked out at least once; until then its card shows the loader.</summary>
    [ObservableProperty] private bool _cleanupRead;

    public IReadOnlyList<FolderNode> CurrentItems => Current?.Children ?? [];
    public bool CanGoUp => Current?.Parent is not null;

    public async Task RefreshAsync()
    {
        await LoadVolumesAsync();
        if (Cleanup.Count == 0 && !CleanupLoading) _ = LoadCleanupAsync();
    }

    private async Task LoadVolumesAsync()
    {
        var history = await reports.DriveHistoryAsync(8) ?? [];
        Volumes.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            long total, free;
            try { (total, free) = (d.TotalSize, d.TotalFreeSpace); }
            catch (IOException) { continue; } // gone between "ready" and reading its size (a drive unplugged): left out
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
                    growth = $"{(diff > 0 ? "+" : "−")}{Units.Bytes(Math.Abs(diff) * 1073741824)} in {days} day{(days == 1 ? "" : "s")}";
                }
                else
                {
                    growth = $"No change in {days} day{(days == 1 ? "" : "s")}";
                }
            }
            string label;
            try { label = d.VolumeLabel; } catch (IOException) { label = ""; }
            Volumes.Add(new VolumeRow(d.Name, label, total, free, growth, up));
        }
    }

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
        }
    }

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

        Add("Temporary files", "Leftovers from installers and apps. Safe to delete anything not currently in use.", Path.GetTempPath());
        Add("Windows temporary files", "System temp folder.", Path.Combine(windows, "Temp"));
        Add("Old downloads", "Files in Downloads you haven't touched in 90+ days.", downloads,
            f => f.LastWrite < DateTime.Now.AddDays(-90) && f.LastAccess < DateTime.Now.AddDays(-90));
        Add("Windows Update cache", "Already-installed update files. Disk Cleanup can remove these.", Path.Combine(windows, "SoftwareDistribution", "Download"));
        Add("NVIDIA shader cache", "Rebuilt automatically by games; can be cleared if it gets large.", Path.Combine(local, "NVIDIA", "DXCache"));
        Add("DirectX shader cache", "Rebuilt automatically; Disk Cleanup can clear it.", Path.Combine(local, "D3DSCache"));
        Add("AMD shader cache", "Rebuilt automatically by games.", Path.Combine(local, "AMD", "DxCache"));
        Add("Crash dumps", "Memory dumps from crashed apps.", Path.Combine(local, "CrashDumps"));
        Add("Previous Windows installation", "Left after a Windows upgrade. Remove it with Disk Cleanup.", Path.Combine(Path.GetPathRoot(windows)!, "Windows.old"));

        var (binSize, binItems) = StorageScanner.RecycleBin();
        if (binSize > 0)
            list.Add(new CleanupItem("Recycle Bin", $"{binItems:N0} deleted item{(binItems == 1 ? "" : "s")}.", binSize, null, true));
        return list.OrderByDescending(i => i.Size).ToList();
    }

    /// <summary>
    /// A scan the user asked for is done: the page goes to what it found, which is below the drives (a user who pressed
    /// "See what's inside" saw nothing happen and thought the app had crashed). Until then the drive's own card says how
    /// far along it is. Not for the Refresh button's scan, nor one that was cancelled or replaced.
    /// </summary>
    public event Action? ScanDone;
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
        IsScanning = true;
        ScanStatus = $"Scanning {root}…";
        // A drive's own card says so too (the drives may be read again meanwhile: the card is found by its drive each time).
        VolumeRow? Card() => Volumes.FirstOrDefault(v => string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase));
        foreach (var v in Volumes) v.ScanText = null;
        Card()?.ScanText = "0%";
        var progress = new Progress<(long Files, long Bytes)>(p =>
        {
            if (_scanCts != scan) return;
            ScanStatus = $"Scanning {root}…  {p.Files:N0} files · {Units.Bytes(p.Bytes)}";
            if (Card() is { } card) card.ScanText = card.ScanProgress(p.Bytes);
        });
        try
        {
            var result = await StorageScanner.ScanAsync(root, progress, scan.Token, _hold);
            if (_scanCts != scan) return;
            Result = result;
            Breadcrumbs.Clear();
            Open(Result.Root);
            ScanStatus = $"{Units.Bytes(Result.Root.Size)} in {Result.FileCount:N0} files · scanned in {Result.Elapsed.TotalSeconds:0.0}s";
            if (asked) ScanDone?.Invoke();
        }
        catch (OperationCanceledException)
        {
            if (_scanCts == scan) ScanStatus = "Scan cancelled.";
        }
        finally
        {
            if (_scanCts == scan)
            {
                IsScanning = false;
                foreach (var v in Volumes) v.ScanText = null;
            }
        }
    }

    [RelayCommand]
    private async Task ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dialog.ShowDialog() == true) await Scan(dialog.FolderName);
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    public void Open(FolderNode? node)
    {
        if (node is null || !node.CanOpen) return;
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
        if (!item.IsRecycleBin)
        {
            Reveal(item.Path);
            return;
        }
        var owner = Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        item.Busy = true;
        _hold.Reset();
        bool emptied;
        try { emptied = await OnOwnThread(() => EmptyBin(owner)); }
        catch { emptied = false; }
        finally
        {
            _hold.Set();
            item.Busy = false;
        }
        if (!emptied) return;
        Cleanup.Remove(item);
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

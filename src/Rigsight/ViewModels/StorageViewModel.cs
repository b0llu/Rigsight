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

public sealed record VolumeRow(string Root, string Label, long Total, long Free, string Growth, bool GrowthIsUp)
{
    public long Used => Total - Free;
    public double UsedPercent => Total > 0 ? 100.0 * Used / Total : 0;
    public string Title => string.IsNullOrWhiteSpace(Label) ? $"Local disk ({Root.TrimEnd('\\')})" : $"{Label} ({Root.TrimEnd('\\')})";
    public string UsageText => $"{Units.Bytes(Used)} used of {Units.Bytes(Total)}";
    public string FreeText => $"{Units.Bytes(Free)} free";
}

public sealed record CleanupItem(string Title, string Description, long Size, string? Path, bool IsRecycleBin)
{
    public string SizeText => Units.Bytes(Size);
}

public sealed partial class StorageViewModel(ReportService reports, LiveData live) : ObservableObject
{
    private CancellationTokenSource? _scanCts;

    public LiveData Live { get; } = live;
    public ObservableCollection<VolumeRow> Volumes { get; } = [];
    public ObservableCollection<CleanupItem> Cleanup { get; } = [];
    public ObservableCollection<FolderNode> Breadcrumbs { get; } = [];

    [ObservableProperty] private bool _cleanupLoading;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private ScanResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentItems), nameof(CanGoUp))]
    private FolderNode? _current;

    [ObservableProperty] private string _scanView = "Map";

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
            string growth = "";
            bool up = false;
            var weekAgo = history.Where(h => h.Drive == d.Name && TimeUtil.FromUnix(h.Day) <= DateTime.Today.AddDays(-6)).MinBy(h => h.Day)
                          ?? history.Where(h => h.Drive == d.Name && TimeUtil.FromUnix(h.Day) < DateTime.Today).MinBy(h => h.Day);
            if (weekAgo is not null)
            {
                double usedGb = (d.TotalSize - d.TotalFreeSpace) / 1073741824.0;
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
            Volumes.Add(new VolumeRow(d.Name, d.VolumeLabel, d.TotalSize, d.TotalFreeSpace, growth, up));
        }
    }

    private async Task LoadCleanupAsync()
    {
        CleanupLoading = true;
        var items = await Task.Run(() =>
        {
            var list = new List<CleanupItem>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            void Add(string title, string description, string path, Func<FileInfo, bool>? filter = null)
            {
                long size = StorageScanner.FolderSize(path, filter);
                if (size >= 50L << 20) list.Add(new CleanupItem(title, description, size, path, false));
            }

            Add("Temporary files", "Leftovers from installers and apps. Safe to delete anything not currently in use.", Path.GetTempPath());
            Add("Windows temporary files", "System temp folder.", Path.Combine(windows, "Temp"));
            Add("Old downloads", "Files in Downloads you haven't touched in 90+ days.", downloads,
                f => f.LastWriteTime < DateTime.Now.AddDays(-90) && f.LastAccessTime < DateTime.Now.AddDays(-90));
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
        });
        Cleanup.Clear();
        foreach (var i in items) Cleanup.Add(i);
        CleanupLoading = false;
    }

    [RelayCommand]
    private async Task Scan(string? root)
    {
        if (string.IsNullOrEmpty(root)) return;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanStatus = $"Scanning {root}…";
        var progress = new Progress<(long Files, long Bytes)>(p => ScanStatus = $"Scanning {root}…  {p.Files:N0} files · {Units.Bytes(p.Bytes)}");
        try
        {
            Result = await StorageScanner.ScanAsync(root, progress, _scanCts.Token);
            Breadcrumbs.Clear();
            Open(Result.Root);
            ScanStatus = $"{Units.Bytes(Result.Root.Size)} in {Result.FileCount:N0} files · scanned in {Result.Elapsed.TotalSeconds:0.0}s";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan cancelled.";
        }
        finally
        {
            IsScanning = false;
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
        if (node is null || node.Children.Count == 0) return;
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

    [RelayCommand]
    private void CleanupAction(CleanupItem? item)
    {
        if (item is null) return;
        if (item.IsRecycleBin)
        {
            var owner = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            if (StorageScanner.EmptyRecycleBin(owner)) Cleanup.Remove(item);
            return;
        }
        Reveal(item.Path);
    }

    [RelayCommand]
    private Task RefreshCleanup() => LoadCleanupAsync();
}

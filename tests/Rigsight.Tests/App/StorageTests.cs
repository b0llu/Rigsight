using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Storage page. Scans run on folders the tests make (never a whole drive); the drive list reads the PC's drives
/// (sizes only) and the seeded drive history. The cleanup suggestions, which walk the user's temp and download
/// folders, are left out.
/// </summary>
[Collection("UI")]
public sealed class StorageTests
{
    /// <summary>A folder tree with known sizes: 200 B at the top, a/ 3 × 1000 B, b/c/ 5000 B, empty/ nothing.</summary>
    private static string Tree()
    {
        var root = TestEnvironment.NewFolder("scan");
        File.WriteAllBytes(Path.Combine(root, "top.bin"), new byte[200]);
        Directory.CreateDirectory(Path.Combine(root, "a"));
        for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(root, "a", $"f{i}.bin"), new byte[1000]);
        Directory.CreateDirectory(Path.Combine(root, "b", "c"));
        File.WriteAllBytes(Path.Combine(root, "b", "c", "big.bin"), new byte[5000]);
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        return root;
    }

    private static StorageViewModel Page()
    {
        SharedData.EnsureSeeded();
        var (settings, live) = Kit.Greeted();
        return Ui.Run(() => new StorageViewModel(new ReportService(settings), live));
    }

    [Fact]
    public void Scanning_a_folder_totals_every_level()
    {
        var root = Tree();
        var vm = Page();
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root));
        Ui.Run(() =>
        {
            Assert.False(vm.IsScanning);
            var r = vm.Result!;
            Assert.Equal(8200, r.Root.Size);
            Assert.Equal(5, r.Root.Files);
            Assert.Equal(5, r.FileCount);
            // Biggest first; the empty folder is left out; loose files get a bucket of their own.
            Assert.Equal(["b", "a", "Files in this folder"], r.Root.Children.Select(c => c.Name));
            Assert.Equal([5000L, 3000L, 200L], r.Root.Children.Select(c => c.Size));
            Assert.True(r.Root.Children[2].IsBucket);
            Assert.All(r.Root.Children, c => Assert.Same(r.Root, c.Parent));
            Assert.Equal(5000.0 / 8200 * 100, r.Root.Children[0].Share, 6);
            Assert.Equal(100, r.Root.Share);
            Assert.Equal("big.bin", r.LargestFiles[0].Name);
            Assert.Equal(Path.Combine(root, "b", "c"), r.LargestFiles[0].Folder);
            Assert.Equal(r.LargestFiles.OrderByDescending(f => f.Size).Select(f => f.Size), r.LargestFiles.Select(f => f.Size));
            Assert.Matches(@"^8 KB in 5 files · scanned in \d+\.\ds$", vm.ScanStatus);
            Assert.Same(r.Root, vm.Current);
            Assert.Equal([r.Root], vm.Breadcrumbs);
            Assert.False(vm.CanGoUp);
            Assert.Equal(r.Root.Children, vm.CurrentItems);
        });
    }

    [Fact]
    public void Opening_folders_and_going_back_up()
    {
        var root = Tree();
        var vm = Page();
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root));
        Ui.Run(() =>
        {
            var b = vm.Result!.Root.Children[0];
            vm.Open(b);
            Assert.Same(b, vm.Current);
            Assert.Equal(["c"], vm.CurrentItems.Select(n => n.Name));
            Assert.Equal(2, vm.Breadcrumbs.Count);
            Assert.True(vm.CanGoUp);
            var c = b.Children[0];
            Assert.Equal("c", c.Name);
            // A folder with nothing under it doesn't open.
            vm.Open(c);
            Assert.Same(b, vm.Current);
            vm.OpenCommand.Execute(null);
            Assert.Same(b, vm.Current);
            vm.UpCommand.Execute(null);
            Assert.Same(vm.Result.Root, vm.Current);
            Assert.Single(vm.Breadcrumbs);
            vm.UpCommand.Execute(null); // at the top
            Assert.Same(vm.Result.Root, vm.Current);
        });
    }

    [Fact]
    public void A_folder_with_many_subfolders_groups_the_smallest()
    {
        var root = TestEnvironment.NewFolder("many");
        for (int i = 0; i < 65; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"d{i:00}"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "x.bin"), new byte[100 + i]);
        }
        var vm = Page();
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root));
        Ui.Run(() =>
        {
            var children = vm.Result!.Root.Children;
            Assert.Equal(61, children.Count);
            var bucket = Assert.Single(children, c => c.IsBucket);
            Assert.Equal("5 smaller folders", bucket.Name);
            Assert.Equal(100 + 101 + 102 + 103 + 104, bucket.Size);
            Assert.Equal(vm.Result.Root.Size, children.Sum(c => c.Size));
        });
    }

    [Fact]
    public void Empty_and_missing_folders_scan_to_nothing()
    {
        var vm = Page();
        var empty = TestEnvironment.NewFolder("empty");
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(empty));
        Ui.Run(() =>
        {
            Assert.Equal(0, vm.Result!.Root.Size);
            Assert.Empty(vm.Result.Root.Children);
            Assert.Null(vm.Current); // nothing to open
        });
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(Path.Combine(empty, "not-there")));
        Ui.Run(() => Assert.Equal(0, vm.Result!.FileCount));
    }

    [Fact]
    public void No_folder_no_scan()
    {
        var vm = Page();
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(null));
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(""));
        Ui.Run(() =>
        {
            Assert.Null(vm.Result);
            Assert.Equal("", vm.ScanStatus);
            Assert.False(vm.IsScanning);
            vm.CancelScanCommand.Execute(null); // nothing running: harmless
        });
    }

    [Fact]
    public async Task A_cancelled_scan_stops()
    {
        var root = Tree();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StorageScanner.ScanAsync(root, new Progress<(long, long)>(), cts.Token));
    }

    [Fact]
    public async Task Scan_progress_is_reported()
    {
        var root = Tree();
        var reports = new List<(long Files, long Bytes)>();
        var progress = new SyncProgress(reports);
        var result = await StorageScanner.ScanAsync(root, progress, TestContext.Current.CancellationToken);
        Assert.Equal((5L, 8200L), reports[^1]);
        Assert.Equal(Path.GetFileName(root), result.Root.Name);
        Assert.Equal(root, result.Root.Path);
    }

    private sealed class SyncProgress(List<(long, long)> into) : IProgress<(long Files, long Bytes)>
    {
        public void Report((long Files, long Bytes) value)
        {
            lock (into) into.Add(value);
        }
    }

    [Fact]
    public void Folder_sizes_for_cleanup_suggestions()
    {
        var root = Tree();
        Assert.Equal(8200, StorageScanner.FolderSize(root));
        Assert.Equal(5000, StorageScanner.FolderSize(root, f => f.Length > 1000));
        Assert.Equal(0, StorageScanner.FolderSize(Path.Combine(root, "nope")));
        Assert.Equal(0, StorageScanner.FolderSize(Path.Combine(root, "empty")));
    }

    [Fact]
    public void Drive_list_has_every_fixed_drive_with_its_growth()
    {
        var vm = Page();
        // Skip the cleanup suggestions (they walk the user's own folders): they only load while the list is empty.
        Ui.Run(() => vm.Cleanup.Add(new CleanupItem("x", "y", 1, null, false)));
        Kit.Wait(() => vm.RefreshAsync());
        var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name).ToList();
        Ui.Run(() =>
        {
            Assert.Equal(fixedDrives, vm.Volumes.Select(v => v.Root));
            Assert.False(vm.CleanupLoading);
            foreach (var v in vm.Volumes)
            {
                Assert.InRange(v.UsedPercent, 0, 100);
                Assert.Equal(v.Total - v.Free, v.Used);
                Assert.EndsWith(" free", v.FreeText);
                Assert.Contains(" used of ", v.UsageText);
                Assert.EndsWith($"({v.Root.TrimEnd('\\')})", v.Title);
                // The generated history covers C: and D: (other drives have none).
                if (SeedData.Drives.Contains(v.Root))
                    Assert.Matches(@"^([+−].+ in \d+ days?|No change in \d+ days?)$", v.Growth);
                else Assert.Equal("", v.Growth);
            }
        });
        // Refreshing replaces the list rather than adding to it.
        Kit.Wait(() => vm.RefreshAsync());
        Ui.Run(() => Assert.Equal(fixedDrives.Count, vm.Volumes.Count));
    }

    [Fact]
    public void Volume_and_cleanup_texts()
    {
        var v = new VolumeRow(@"E:\", "", 1L << 40, 1L << 39, "", false);
        Assert.Equal(@"Local disk (E:)", v.Title);
        Assert.Equal(50, v.UsedPercent);
        Assert.Equal("512.0 GB used of 1.00 TB", v.UsageText);
        Assert.Equal("512.0 GB free", v.FreeText);
        Assert.Equal("Games (F:)", (v with { Root = @"F:\", Label = "Games" }).Title);
        Assert.Equal(0, new VolumeRow(@"G:\", "", 0, 0, "", false).UsedPercent);
        Assert.Equal("50 MB", new CleanupItem("Temp", "", 50L << 20, null, false).SizeText);
    }

    [Fact]
    public void Cleanup_for_a_folder_reveals_it_and_nothing_for_nothing()
    {
        var vm = Page();
        Ui.Run(() =>
        {
            // A missing path opens nothing (no Explorer window in a test).
            vm.CleanupActionCommand.Execute(new CleanupItem("x", "", 1, Path.Combine(TestEnvironment.DataDir, "no-such-folder"), false));
            vm.CleanupActionCommand.Execute(null);
            vm.RevealCommand.Execute(null);
            vm.RevealCommand.Execute("");
        });
        // The Recycle Bin one empties the real Recycle Bin: never run here.
    }

    [Fact]
    public void The_map_view_is_the_default()
    {
        var vm = Page();
        Ui.Run(() =>
        {
            Assert.Equal("Map", vm.ScanView);
            Assert.Empty(vm.CurrentItems);
            Assert.False(vm.CanGoUp);
        });
    }
}

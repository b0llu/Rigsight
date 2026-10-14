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
    /// <summary>A cluster on the test drive (NTFS's usual): files of whole clusters take exactly their size.</summary>
    private const int K = 4096;

    /// <summary>
    /// A folder tree with known sizes, in whole 4 KB clusters so the space each file takes is its size: 8 KB at the top,
    /// a/ 3 × 4 KB, b/c/ 20 KB, empty/ nothing.
    /// </summary>
    private static string Tree()
    {
        var root = TestEnvironment.NewFolder("scan");
        File.WriteAllBytes(Path.Combine(root, "top.bin"), new byte[2 * K]);
        Directory.CreateDirectory(Path.Combine(root, "a"));
        for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(root, "a", $"f{i}.bin"), new byte[K]);
        Directory.CreateDirectory(Path.Combine(root, "b", "c"));
        File.WriteAllBytes(Path.Combine(root, "b", "c", "big.bin"), new byte[5 * K]);
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
    public void A_scan_the_user_started_says_when_its_done_and_the_refresh_buttons_doesnt()
    {
        // The page goes to what the scan found, below the drives (a user thought the app had crashed); Refresh scans where they already are.
        var root = Tree();
        var vm = Page();
        int done = 0;
        bool hadResult = false;
        vm.ScanDone += () => { done++; hadResult = vm.Result is not null; };
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root));
        Assert.Equal(1, done);
        Assert.True(hadResult); // said once there is something to show
        Kit.Wait(() => vm.RefreshAllCommand.ExecuteAsync(null));
        Assert.Equal(1, done);
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(null)); // nothing to scan
        Assert.Equal(1, done);
    }

    [Fact]
    public void A_drives_card_says_how_far_its_scan_is_by_the_space_in_use()
    {
        var drive = new VolumeRow(@"C:\", "", Total: 1000, Free: 600, "", false); // 400 in use
        var said = new List<string?>();
        drive.PropertyChanged += (_, e) => said.Add(e.PropertyName);
        Assert.False(drive.IsScanning);
        Assert.Equal("0%", drive.ScanProgress(0));
        Assert.Equal("40%", drive.ScanProgress(160));
        Assert.Equal("99%", drive.ScanProgress(400)); // never 100 before it's done
        Assert.Equal("99%", drive.ScanProgress(5000));
        Assert.Equal("0%", new VolumeRow(@"D:\", "", 0, 0, "", false).ScanProgress(10)); // an empty or unread drive

        drive.ScanText = "0%";
        drive.ScanText = "40%";
        drive.ScanText = null;
        Assert.False(drive.IsScanning);
        Assert.Equal(["ScanText", "IsScanning", "ScanText", "ScanText", "IsScanning"], said);
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
            Assert.Equal(10 * K, r.Root.Size);
            Assert.Equal(5, r.Root.Files);
            Assert.Equal(5, r.FileCount);
            // Biggest first; the empty folder is left out; loose files get a bucket of their own.
            Assert.Equal(["b", "a", "Files in this folder"], r.Root.Children.Select(c => c.Name));
            Assert.Equal([5L * K, 3L * K, 2L * K], r.Root.Children.Select(c => c.Size));
            Assert.True(r.Root.Children[2].IsBucket);
            Assert.All(r.Root.Children, c => Assert.Same(r.Root, c.Parent));
            Assert.Equal(50, r.Root.Children[0].Share, 6);
            Assert.Equal(100, r.Root.Share);
            Assert.Equal("big.bin", r.LargestFiles[0].Name);
            Assert.Equal(Path.Combine(root, "b", "c"), r.LargestFiles[0].Folder);
            Assert.Equal(r.LargestFiles.OrderByDescending(f => f.Size).Select(f => f.Size), r.LargestFiles.Select(f => f.Size));
            Assert.Matches(@"^40 KB in 5 files · scanned in \d+\.\ds$", vm.ScanStatus);
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
    public void Refresh_after_deleting_a_file_drops_it_and_stays_in_the_open_folder()
    {
        var root = Tree();
        File.WriteAllBytes(Path.Combine(root, "b", "c", "keep.bin"), new byte[K]);
        var vm = Page();
        int cleanupRuns = 0;
        Ui.Run(() => vm.FindCleanup = () => { cleanupRuns++; return [new CleanupItem("Temp", "", 1, null, false)]; });
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root));
        string b = Path.Combine(root, "b");
        Ui.Run(() =>
        {
            Assert.Equal("big.bin", vm.Result!.LargestFiles[0].Name);
            vm.Open(vm.Result.Root.Children.Single(n => n.Name == "b"));
        });

        // The biggest file, found on the page and deleted in Explorer.
        File.Delete(Path.Combine(root, "b", "c", "big.bin"));
        Kit.Wait(() => vm.RefreshAllCommand.ExecuteAsync(null));
        Ui.Run(() =>
        {
            Assert.DoesNotContain(vm.Result!.LargestFiles, f => f.Name == "big.bin");
            Assert.Equal(6 * K, vm.Result.Root.Size);
            Assert.Equal(b, vm.Current!.Path);                 // still in the folder that was open
            Assert.Equal(K, vm.Current.Size);
            Assert.Equal(2, vm.Breadcrumbs.Count);             // root > b
            Assert.NotEmpty(vm.Volumes);                        // the drives were read again
            Assert.Equal("Temp", Assert.Single(vm.Cleanup).Title);
        });
        Assert.Equal(1, cleanupRuns);

        // A folder deleted altogether: back at the top of the scan.
        Directory.Delete(b, recursive: true);
        Kit.Wait(() => vm.RefreshAllCommand.ExecuteAsync(null));
        Ui.Run(() => Assert.Same(vm.Result!.Root, vm.Current));
    }

    [Fact]
    public void Refresh_without_a_scan_only_reads_the_drives_and_cleanup()
    {
        var vm = Page();
        Ui.Run(() => vm.FindCleanup = () => []);
        Kit.Wait(() => vm.RefreshAllCommand.ExecuteAsync(null));
        Ui.Run(() =>
        {
            Assert.Null(vm.Result);
            Assert.False(vm.IsScanning);
            Assert.NotEmpty(vm.Volumes);
            Assert.Empty(vm.Cleanup);
        });
    }

    [Fact]
    public void A_folder_with_many_subfolders_groups_the_smallest()
    {
        var root = TestEnvironment.NewFolder("many");
        for (int i = 0; i < 65; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"d{i:00}"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "x.bin"), new byte[(i + 1) * K]);
        }
        var vm = Page();
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root));
        Ui.Run(() =>
        {
            var children = vm.Result!.Root.Children;
            Assert.Equal(61, children.Count);
            var bucket = Assert.Single(children, c => c.IsBucket);
            Assert.Equal("5 smaller folders", bucket.Name);
            Assert.Equal((1 + 2 + 3 + 4 + 5) * K, bucket.Size);
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
        Assert.Equal((5L, 10L * K), reports[^1]);
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
        Assert.Equal(10 * K, StorageScanner.FolderSize(root));
        Assert.Equal(7 * K, StorageScanner.FolderSize(root, f => f.Length > K));
        Assert.Equal(0, StorageScanner.FolderSize(Path.Combine(root, "nope")));
        Assert.Equal(0, StorageScanner.FolderSize(Path.Combine(root, "empty")));

        // Only sizes are wanted: no file's name is made on the way (a tree of 300,000 files cost a hundred megabytes
        // of them), though everything else about each file is there to choose by.
        var seen = new List<DiskEntry>();
        Assert.Equal(10 * K, StorageScanner.FolderSize(root, f => { seen.Add(f); return true; }));
        Assert.NotEmpty(seen);
        Assert.All(seen, f => Assert.Equal("", f.Name));
        Assert.All(seen, f => Assert.True(f.Length > 0 && f.LastWrite > DateTime.Now.AddDays(-1)));
        // Listing a folder still names its files.
        Assert.All(StorageScanner.Entries(root), e => Assert.NotEqual("", e.Name));
    }

    [Fact]
    public void The_cleanup_list_is_worked_out_after_the_page_is_up_and_out_of_its_way()
    {
        var vm = Page();
        ThreadPriority? priority = null;
        Ui.Run(() =>
        {
            vm.CleanupDelay = TimeSpan.FromMilliseconds(1500);
            vm.FindCleanup = () =>
            {
                priority = Thread.CurrentThread.Priority;
                return [new CleanupItem("Temp", "", 1, null, false)];
            };
        });
        // The page has its drives and is done opening: the list hasn't been started, and says it is loading.
        Kit.Wait(() => vm.RefreshAsync());
        Ui.Run(() =>
        {
            Assert.NotEmpty(vm.Volumes);
            Assert.True(vm.CleanupLoading);
            Assert.Empty(vm.Cleanup);
        });
        Assert.Null(priority);
        Assert.True(Ui.WaitFor(() => Ui.Run(() => vm.Cleanup.Count == 1 && !vm.CleanupLoading)));
        Assert.Equal(ThreadPriority.BelowNormal, priority);
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

    [Fact]
    public void Emptying_the_bin_is_off_the_windows_thread_and_a_scan_waits_for_it_with_a_loader_where_another_scan_was()
    {
        // The user: emptying the bin while a drive was being scanned left the window not answering and the deleting
        // crawling; and while E: was being scanned the box still listed C:'s folders.
        string first = Tree(), second = Tree();
        var vm = Page();
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(first));
        var bin = new CleanupItem("Recycle Bin", "3 deleted items.", 1, null, true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int uiThread = Ui.Run(() => Environment.CurrentManagedThreadId), ranOn = 0;
        ApartmentState apartment = ApartmentState.Unknown;
        Task emptying = Task.CompletedTask, scanning = Task.CompletedTask;
        Ui.Run(() =>
        {
            vm.Cleanup.Add(bin);
            vm.EmptyBin = _ =>
            {
                ranOn = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
                entered.Set();
                release.Wait(TestContext.Current.CancellationToken);
                return true;
            };
            emptying = vm.CleanupActionCommand.ExecuteAsync(bin);
        });
        Assert.True(entered.Wait(10_000, TestContext.Current.CancellationToken));
        Ui.Run(() =>
        {
            Assert.True(bin.Busy);
            // Pressed again while Windows is at it: nothing more.
            vm.CleanupActionCommand.Execute(bin);
            scanning = vm.ScanCommand.ExecuteAsync(second);
        });
        Thread.Sleep(400);
        Ui.Run(() =>
        {
            // The scan of the other folder hasn't moved, and what was on screen has made way for a loader.
            Assert.True(vm.IsScanning);
            Assert.Equal(first, vm.Result!.Root.Path);
            Assert.False(vm.ShowResult);
            Assert.True(vm.ShowScanLoader);
        });
        release.Set();
        Kit.Wait(() => Task.WhenAll(emptying, scanning));
        Assert.NotEqual(uiThread, ranOn);
        Assert.Equal(ApartmentState.STA, apartment);
        Ui.Run(() =>
        {
            Assert.False(bin.Busy);
            Assert.DoesNotContain(bin, vm.Cleanup);
            Assert.False(vm.IsScanning);
            Assert.Equal(second, vm.Result!.Root.Path);
            Assert.True(vm.ShowResult);
            Assert.False(vm.ShowScanLoader);
        });
    }

    [Fact]
    public void A_bin_the_user_chose_not_to_empty_stays_listed_and_the_same_place_scanned_again_stays_on_screen()
    {
        var root = Tree();
        var vm = Page();
        var bin = new CleanupItem("Recycle Bin", "", 1, null, true);
        Ui.Run(() =>
        {
            vm.Cleanup.Add(bin);
            vm.EmptyBin = _ => false; // "No" in Windows' question
        });
        Kit.Wait(() => vm.CleanupActionCommand.ExecuteAsync(bin));
        Ui.Run(() =>
        {
            Assert.Contains(bin, vm.Cleanup);
            Assert.False(bin.Busy);
            Assert.Equal("Empty", bin.ActionText);
            Assert.Equal("Open", new CleanupItem("Temp", "", 1, null, false).ActionText);
            Assert.False(vm.ShowResult);
            Assert.False(vm.ShowScanLoader);
        });
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root));
        // Refresh scans the same place: what is on screen stays up meanwhile.
        bool hidden = false;
        Ui.Run(() => vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsScanning) && vm.IsScanning) hidden |= !vm.ShowResult; });
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root + "\\"));
        Assert.False(hidden);
        Ui.Run(() =>
        {
            Assert.True(vm.ShowResult);
            Assert.Equal($"The {vm.Result!.LargestFiles.Count} largest files in {vm.Result.Root.Path}", vm.FilesTitle);
            // Only a folder with something under it opens; the buckets never do.
            Assert.True(vm.Result.Root.CanOpen);
            Assert.All(vm.Result.Root.Children.Where(c => c.IsBucket), c => Assert.False(c.CanOpen));
        });
    }

    [Fact]
    public async Task A_scan_that_is_held_waits_and_goes_on_when_let_go()
    {
        var root = Tree();
        using var hold = new ManualResetEventSlim(false);
        var scan = StorageScanner.ScanAsync(root, new Progress<(long, long)>(), TestContext.Current.CancellationToken, hold);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(scan.IsCompleted);
        hold.Set();
        Assert.Equal(5, (await scan).FileCount);
    }

    [Fact]
    public void The_list_has_its_rows_after_a_folder_was_opened_on_another_view_and_its_box_keeps_one_height()
    {
        // The user: on Largest files, one level back in the path, then List: nothing shown (the rows were there with no
        // width, only their hover shade). And the box grew and shrank with every folder opened.
        var root = Tree();
        var vm = Page();
        Kit.Wait(() => vm.ScanCommand.ExecuteAsync(root));
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var window = new System.Windows.Window { Content = new Views.StorageView { DataContext = vm }, Left = -32000, Top = -32000, Width = 1200, Height = 900, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                var b = vm.Result!.Root.Children[0];
                vm.ScanView = "List";
                vm.Open(b);
                Ui.Pump(200);
                var box = Visuals.Descendants<System.Windows.Controls.ScrollViewer>(window).Single(s => s.Name == "ListBox");
                double height = box.ActualHeight;
                Assert.True(height >= 420);
                vm.ScanView = "Files";
                Ui.Pump(200);
                Assert.False(box.IsVisible);
                vm.Open(vm.Result.Root); // one level back, by the path
                Ui.Pump(100);
                vm.ScanView = "List";
                Ui.Pump(300);
                var rows = Visuals.Descendants<System.Windows.Controls.Button>(box).Where(r => r.DataContext is FolderNode).ToList();
                Assert.Equal(vm.CurrentItems, rows.Select(r => (FolderNode)r.DataContext));
                Assert.All(rows, r => Assert.True(r.ActualWidth > 600, $"row {((FolderNode)r.DataContext).Name} is {r.ActualWidth} wide"));
                var names = rows.Select(r => Visuals.Descendants<System.Windows.Controls.TextBlock>(r).Single(x => x.Text == ((FolderNode)r.DataContext).Name)).ToList();
                Assert.All(names, n => Assert.True(n.ActualWidth > 0));
                // Three things or one: the same box.
                Assert.Equal(height, box.ActualHeight);
                // A bucket has nothing to open.
                Assert.False(rows.Single(r => ((FolderNode)r.DataContext).IsBucket).IsEnabled);
                Assert.True(rows[0].IsEnabled);
            }
            finally { window.Close(); }
        });
        Ui.AssertNoProblems("Storage list");
    }
}

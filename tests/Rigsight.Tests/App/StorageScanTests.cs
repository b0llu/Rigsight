using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// The storage scan counts the space files take on the drive, never more than the drive holds: a user's 512 GB laptop
/// showed 708 GB and counting (GitHub issue #1). Files under two names, sparse and compressed files, and files with a
/// size but no space (as OneDrive's cloud-only ones) are where the two differ.
/// </summary>
public sealed class StorageScanTests
{
    private const int K = 4096;

    private static Task<ScanResult> Scan(string root) => StorageScanner.ScanAsync(root, new Progress<(long, long)>(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_file_under_two_names_is_counted_once()
    {
        // As Windows keeps its own files (WinSxS and System32): one file, two folders.
        var root = TestEnvironment.NewFolder("hardlink");
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.WriteAllBytes(Path.Combine(root, "a", "one.bin"), new byte[3 * K]);
        Assert.True(CreateHardLinkW(Path.Combine(root, "b", "same.bin"), Path.Combine(root, "a", "one.bin"), IntPtr.Zero));

        var r = await Scan(root);
        Assert.Equal(3 * K, r.Root.Size);
        Assert.Equal(2, r.FileCount); // both names are files in their folders
        Assert.Equal(3 * K, StorageScanner.FolderSize(root));
    }

    [Fact]
    public async Task A_sparse_file_counts_the_space_it_takes_not_its_size()
    {
        var root = TestEnvironment.NewFolder("sparse");
        var path = Path.Combine(root, "disk.vhdx");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            Assert.True(DeviceIoControl(fs.SafeFileHandle, 0x900C4 /* FSCTL_SET_SPARSE */, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
            fs.SetLength(1L << 30); // 1 GB "big", nothing written
        }
        var entry = Assert.Single(StorageScanner.Entries(root));
        Assert.Equal(1L << 30, entry.Length);
        Assert.True(entry.OnDisk < 1 << 20, $"{entry.OnDisk} bytes on disk");
        Assert.True((await Scan(root)).Root.Size < 1 << 20);
    }

    [Fact]
    public async Task A_compressed_file_counts_what_it_takes_after_compression()
    {
        var root = TestEnvironment.NewFolder("compressed");
        var path = Path.Combine(root, "zeros.bin");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            short format = 1; // COMPRESSION_FORMAT_DEFAULT
            var p = Marshal.AllocHGlobal(2);
            Marshal.WriteInt16(p, format);
            try { Assert.True(DeviceIoControl(fs.SafeFileHandle, 0x9C040 /* FSCTL_SET_COMPRESSION */, p, 2, IntPtr.Zero, 0, out _, IntPtr.Zero)); }
            finally { Marshal.FreeHGlobal(p); }
            fs.Write(new byte[1 << 20]);
            fs.Flush(flushToDisk: true); // compressed as it reaches the drive
        }
        var entry = Assert.Single(StorageScanner.Entries(root));
        Assert.Equal(1 << 20, entry.Length);
        Assert.True(entry.OnDisk < 1 << 19, $"{entry.OnDisk} bytes on disk");
        Assert.True((await Scan(root)).Root.Size < 1 << 19);
    }

    [Fact]
    public async Task A_junction_is_not_followed()
    {
        var root = TestEnvironment.NewFolder("junction");
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        File.WriteAllBytes(Path.Combine(real, "x.bin"), new byte[2 * K]);
        // A junction to the same folder, as Windows' own "Application Data" and "Documents and Settings" are.
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "again")}\" \"{real}\"")
               { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(root, "again")));
        var r = await Scan(root);
        Assert.Equal(2 * K, r.Root.Size);
        Assert.Equal(["real"], r.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void Entries_list_files_and_folders_with_their_times()
    {
        var root = TestEnvironment.NewFolder("entries");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var file = Path.Combine(root, "f.bin");
        File.WriteAllBytes(file, new byte[K]);
        File.SetLastWriteTime(file, new DateTime(2025, 3, 4, 5, 6, 7));
        var entries = StorageScanner.Entries(root).OrderBy(e => e.Name).ToList();
        Assert.Equal(["f.bin", "sub"], entries.Select(e => e.Name));
        Assert.False(entries[0].IsFolder);
        Assert.True(entries[1].IsFolder);
        Assert.Equal(K, entries[0].OnDisk);
        Assert.Equal(new DateTime(2025, 3, 4, 5, 6, 7), entries[0].LastWrite);
        Assert.Empty(StorageScanner.Entries(Path.Combine(root, "missing")));
    }

    [Fact]
    public void A_folder_deeper_than_260_characters_is_read()
    {
        var root = TestEnvironment.NewFolder("deep");
        var deep = root;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "x.bin"), new byte[K]);
        Assert.Single(StorageScanner.Entries(deep));
        Assert.Equal(K, StorageScanner.FolderSize(root));
    }

    private static List<FolderNode> Folder(params long[] sizes)
    {
        var parent = new FolderNode { Name = "C:\\", Path = "C:\\" };
        parent.Children = [.. sizes.Select((s, i) => new FolderNode { Name = $"f{i}", Path = $"C:\\f{i}", Size = s, Files = 1, Parent = parent })];
        return parent.Children;
    }

    [Fact]
    public void The_map_puts_the_blocks_too_small_to_read_together_as_Others()
    {
        // Three big folders and five slivers (each under 1.5% of the map).
        var items = Folder(500, 300, 150, 10, 8, 6, 4, 2);
        var shown = Controls.Treemap.Grouped(items);
        Assert.Equal(["f0", "f1", "f2", "Others"], shown.Select(n => n.Name));
        var others = shown[^1];
        Assert.True(others.IsBucket);
        Assert.Equal(30, others.Size);
        Assert.Equal(5, others.Files);
        Assert.Equal(["f3", "f4", "f5", "f6", "f7"], others.Children.Select(n => n.Name)); // it opens to show them
        Assert.Same(items[0].Parent, others.Parent);                                      // and Up leads back
    }

    [Fact]
    public void One_small_block_stays_itself_and_nothing_is_lost()
    {
        Assert.Equal(["f0", "f1", "f2"], Controls.Treemap.Grouped(Folder(500, 300, 5)).Select(n => n.Name));
        Assert.Equal(["f1", "f2"], Controls.Treemap.Grouped(Folder(0, 500, 300)).Select(n => n.Name)); // an empty folder takes no room
        var all = Folder(1000, 9, 9, 9);
        Assert.Equal(all.Sum(n => n.Size), Controls.Treemap.Grouped(all).Sum(n => n.Size));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);
}

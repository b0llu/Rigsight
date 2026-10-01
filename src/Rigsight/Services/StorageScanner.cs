using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace Rigsight.Services;

/// <summary>A folder (or a grouped bucket) and its total size.</summary>
public sealed class FolderNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public FolderNode? Parent { get; set; }
    public long Size { get; set; }
    public long Files { get; set; }
    public List<FolderNode> Children { get; set; } = [];
    /// <summary>True for "files in this folder" and "smaller items" buckets (not real folders).</summary>
    public bool IsBucket { get; init; }
    public double Share => Parent is { Size: > 0 } p ? 100.0 * Size / p.Size : 100;
}

public sealed record LargeFile(string Path, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed class ScanResult
{
    public required FolderNode Root { get; init; }
    public required List<LargeFile> LargestFiles { get; init; }
    public long FileCount { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// One entry of a folder as Windows lists it: a file's size, the space it really takes on the drive (smaller for a
/// compressed or sparse file, next to nothing for one kept only in the cloud, a whole cluster for a few bytes), and its
/// ID, which is the same for every name a hard-linked file goes by.
/// </summary>
public readonly record struct DiskEntry(string Name, FileAttributes Attributes, long Length, long OnDisk, long Id, DateTime LastWrite, DateTime LastAccess)
{
    public bool IsFolder => (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>
/// Walks a drive or folder and totals the space each folder takes on the drive, keeping the biggest items. The space a
/// file really takes, not its size: OneDrive's cloud-only files, compressed files and the many Windows files kept under
/// two names (hard links, counted once) would otherwise add up past the drive's own size.
/// </summary>
public static class StorageScanner
{
    private const int KeepChildren = 60;
    private const int KeepLargestFiles = 40;

    public static Task<ScanResult> ScanAsync(string root, IProgress<(long Files, long Bytes)> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var started = DateTime.Now;
            long files = 0, bytes = 0;
            var largest = new ConcurrentBag<LargeFile>();
            long largestFloor = 0;
            var lastReport = Environment.TickCount64;
            // Every file seen so far, by ID: a second name for the same file adds nothing.
            var seen = new HashSet<long>();

            FolderNode Walk(string path, string name, FolderNode? parent, int depth)
            {
                ct.ThrowIfCancellationRequested();
                var node = new FolderNode { Name = name, Path = path, Parent = parent };
                long directSize = 0, directFiles = 0;
                var subdirs = new List<string>();
                foreach (var entry in Entries(path))
                {
                    if (entry.IsFolder)
                    {
                        // Junctions and links lead somewhere already counted (or round in a loop): not followed.
                        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) subdirs.Add(entry.Name);
                        continue;
                    }
                    directFiles++;
                    if (entry.OnDisk <= 0) continue;
                    lock (seen)
                        if (!seen.Add(entry.Id)) continue;
                    directSize += entry.OnDisk;
                    if (entry.OnDisk > Interlocked.Read(ref largestFloor)) OfferLargest(System.IO.Path.Combine(path, entry.Name), entry.OnDisk);
                }

                Interlocked.Add(ref files, directFiles);
                Interlocked.Add(ref bytes, directSize);
                if (Environment.TickCount64 - Interlocked.Read(ref lastReport) > 150)
                {
                    Interlocked.Exchange(ref lastReport, Environment.TickCount64);
                    progress.Report((Interlocked.Read(ref files), Interlocked.Read(ref bytes)));
                }

                // Parallelize the top levels; deeper levels are walked sequentially.
                FolderNode[] children;
                if (depth < 2 && subdirs.Count > 1)
                {
                    children = new FolderNode[subdirs.Count];
                    Parallel.For(0, subdirs.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                        i => children[i] = Walk(System.IO.Path.Combine(path, subdirs[i]), subdirs[i], node, depth + 1));
                }
                else
                {
                    children = [.. subdirs.Select(d => Walk(System.IO.Path.Combine(path, d), d, node, depth + 1))];
                }

                node.Size = directSize + children.Sum(c => c.Size);
                node.Files = directFiles + children.Sum(c => c.Files);

                var list = children.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
                if (list.Count > KeepChildren)
                {
                    var rest = list.Skip(KeepChildren).ToList();
                    list = list.Take(KeepChildren).ToList();
                    list.Add(new FolderNode
                    {
                        Name = $"{rest.Count} smaller folders", Path = path, Parent = node, IsBucket = true,
                        Size = rest.Sum(r => r.Size), Files = rest.Sum(r => r.Files),
                    });
                }
                if (directSize > 0 && list.Count > 0)
                {
                    list.Add(new FolderNode
                    {
                        Name = "Files in this folder", Path = path, Parent = node, IsBucket = true,
                        Size = directSize, Files = directFiles,
                    });
                }
                node.Children = [.. list.OrderByDescending(c => c.Size)];
                return node;
            }

            void OfferLargest(string path, long size)
            {
                largest.Add(new LargeFile(path, size));
                if (largest.Count > KeepLargestFiles * 4)
                {
                    lock (largest)
                    {
                        if (largest.Count > KeepLargestFiles * 4)
                        {
                            var keep = largest.OrderByDescending(l => l.Size).Take(KeepLargestFiles).ToList();
                            largest.Clear();
                            foreach (var k in keep) largest.Add(k);
                            Interlocked.Exchange(ref largestFloor, keep[^1].Size);
                        }
                    }
                }
            }

            var rootInfo = new DirectoryInfo(root);
            var result = Walk(root, rootInfo.Name, null, 0);
            if (string.IsNullOrEmpty(result.Name) || result.Name == rootInfo.Root.Name)
                result = new FolderNode { Name = root, Path = root, Size = result.Size, Files = result.Files, Children = result.Children };
            foreach (var c in result.Children) c.Parent = result;

            progress.Report((files, bytes));
            return new ScanResult
            {
                Root = result,
                LargestFiles = [.. largest.OrderByDescending(l => l.Size).DistinctBy(l => l.Path).Take(KeepLargestFiles)],
                FileCount = files,
                Elapsed = DateTime.Now - started,
            };
        }, ct);

    /// <summary>
    /// The space the files under a folder take on the drive (for cleanup suggestions), those <paramref name="filter"/>
    /// keeps; 0 if it doesn't exist. Junctions aren't followed, and a file under two names counts once.
    /// </summary>
    public static long FolderSize(string path, Func<DiskEntry, bool>? filter = null)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var seen = new HashSet<long>();
        var folders = new Stack<string>([path]);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            foreach (var e in Entries(folder))
            {
                if (e.IsFolder)
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) == 0) folders.Push(System.IO.Path.Combine(folder, e.Name));
                }
                else if (e.OnDisk > 0 && (filter is null || filter(e)) && seen.Add(e.Id)) total += e.OnDisk;
            }
        }
        return total;
    }

    // ── Reading a folder ──────────────────────────────────────────────────

    /// <summary>
    /// A folder's entries, straight from the file system in large batches (one call per few hundred entries, as Explorer
    /// lists them): with each file's space on the drive and its ID, which no per-file call has to fetch. Nothing for a
    /// folder that can't be opened (protected, gone).
    /// </summary>
    public static unsafe List<DiskEntry> Entries(string folder)
    {
        var list = new List<DiskEntry>();
        string full = System.IO.Path.GetFullPath(folder);
        // The long-path form, so folders deeper than 260 characters are read too.
        string name = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        using var handle = CreateFileW(name, FileListDirectory, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return list;

        const int size = 64 * 1024;
        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            bool first = true;
            while (true)
            {
                int status = NtQueryDirectoryFile(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _, buffer, size,
                    FileIdFullDirectoryInformation, false, IntPtr.Zero, first);
                first = false;
                if (status != 0) break; // no more files (or an error: what was read is what there is)
                byte* p = buffer;
                while (true)
                {
                    // FILE_ID_FULL_DIR_INFORMATION: times from 8, end of file 40, allocation 48, attributes 56,
                    // name length 60, file ID 72, name 80.
                    int next = *(int*)p;
                    int nameLength = *(int*)(p + 60);
                    var entryName = new string((char*)(p + 80), 0, nameLength / 2);
                    if (entryName is not "." and not "..")
                    {
                        var attributes = (FileAttributes)(*(uint*)(p + 56));
                        long onDisk = *(long*)(p + 48);
                        // The listing gives a compressed file's space before compression. For those (and sparse files,
                        // Windows' own compacted system files, cloud files: all rare) the space they really take is asked
                        // for, as Explorer's "Size on disk" does. It reads no data: a cloud-only file stays in the cloud.
                        if ((attributes & FileAttributes.Directory) == 0 && (attributes & Special) != 0)
                            onDisk = CompressedSize(System.IO.Path.Combine(name, entryName)) ?? onDisk;
                        list.Add(new DiskEntry(entryName, attributes, *(long*)(p + 40), onDisk, *(long*)(p + 72),
                            FromFileTime(*(long*)(p + 24)), FromFileTime(*(long*)(p + 16))));
                    }
                    if (next == 0) break;
                    p += next;
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
        return list;
    }

    private const FileAttributes Special = FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint;

    private static long? CompressedSize(string path)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0) return null;
        return ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);

    private static DateTime FromFileTime(long t)
    {
        try { return t > 0 ? DateTime.FromFileTime(t) : DateTime.MinValue; }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    private const uint FileListDirectory = 0x1, FileShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
    private const int FileIdFullDirectoryInformation = 38;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtQueryDirectoryFile(Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext,
        out IoStatusBlock ioStatus, byte* buffer, int length, int infoClass, [MarshalAs(UnmanagedType.U1)] bool singleEntry, IntPtr fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    public static (long Size, long Items) RecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin (Windows shows its own confirmation and progress).</summary>
    public static bool EmptyRecycleBin(IntPtr owner) => SHEmptyRecycleBin(owner, null, 0) == 0;
}

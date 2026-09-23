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

/// <summary>Walks a drive or folder and totals sizes per folder, keeping the biggest items.</summary>
public static class StorageScanner
{
    private const int KeepChildren = 60;
    private const int KeepLargestFiles = 40;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint, // don't follow junctions/symlinks (loops, double counting)
        ReturnSpecialDirectories = false,
    };

    public static Task<ScanResult> ScanAsync(string root, IProgress<(long Files, long Bytes)> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var started = DateTime.Now;
            long files = 0, bytes = 0;
            var largest = new ConcurrentBag<LargeFile>();
            long largestFloor = 0;
            var lastReport = Environment.TickCount64;

            FolderNode Walk(DirectoryInfo dir, FolderNode? parent, int depth)
            {
                ct.ThrowIfCancellationRequested();
                var node = new FolderNode { Name = dir.Name, Path = dir.FullName, Parent = parent };
                long directSize = 0, directFiles = 0;
                var subdirs = new List<DirectoryInfo>();
                try
                {
                    foreach (var entry in dir.EnumerateFileSystemInfos("*", Options))
                    {
                        if (entry is FileInfo f)
                        {
                            long len;
                            try { len = f.Length; } catch { continue; }
                            directSize += len;
                            directFiles++;
                            if (len > Interlocked.Read(ref largestFloor)) OfferLargest(f.FullName, len);
                        }
                        else if (entry is DirectoryInfo d)
                        {
                            subdirs.Add(d);
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    // Protected folder: count what we could read.
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
                        i => children[i] = Walk(subdirs[i], node, depth + 1));
                }
                else
                {
                    children = [.. subdirs.Select(d => Walk(d, node, depth + 1))];
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
                        Name = $"{rest.Count} smaller folders", Path = dir.FullName, Parent = node, IsBucket = true,
                        Size = rest.Sum(r => r.Size), Files = rest.Sum(r => r.Files),
                    });
                }
                if (directSize > 0 && list.Count > 0)
                {
                    list.Add(new FolderNode
                    {
                        Name = "Files in this folder", Path = dir.FullName, Parent = node, IsBucket = true,
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
            var result = Walk(rootInfo, null, 0);
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

    /// <summary>Total size of a folder (for cleanup suggestions). Returns 0 if it doesn't exist.</summary>
    public static long FolderSize(string path, Func<FileInfo, bool>? filter = null)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try
                {
                    if (filter is null || filter(f)) total += f.Length;
                }
                catch { }
            }
        }
        catch { }
        return total;
    }

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

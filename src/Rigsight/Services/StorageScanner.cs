using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>What kind of files take a folder's space, by their extension: the map's colours and its key.</summary>
public enum FileKind { GameData, Video, PhotosAndMusic, Programs, Archives, Other }

/// <summary>A folder (or a grouped bucket, or one file once a folder's files are listed) and its total size.</summary>
public sealed class FolderNode : System.ComponentModel.INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public FolderNode? Parent { get; set; }
    public long Size { get; set; }
    public long Files { get; set; }
    public List<FolderNode> Children { get; set; } = [];
    /// <summary>True for "files in this folder" and "smaller items" buckets (not real folders).</summary>
    public bool IsBucket { get; init; }
    /// <summary>One file (listed when its folder is opened): nothing inside; pressed, it's shown in Explorer.</summary>
    public bool IsFile { get; init; }
    /// <summary>"Files in this folder": its files are listed when it's opened, not kept by the scan.</summary>
    public bool IsLooseFiles { get; init; }
    /// <summary>
    /// One of a folder's smaller folders, kept as its total only (what's under it would cost memory on a big drive for
    /// something rarely opened): opening it scans just that folder, which takes moments.
    /// </summary>
    public bool NeedsScan { get; set; }
    /// <summary>The space each <see cref="FileKind"/> takes in it, everything below added in.</summary>
    public long[] Kinds { get; } = new long[KindCount];
    public const int KindCount = 6;
    /// <summary>Its kind's colour, for the list's swatch (the map's own).</summary>
    public System.Windows.Media.Brush KindBrush => Controls.Treemap.KindBrush(Kind);
    /// <summary>The kind taking most of its space, for its colour.</summary>
    public FileKind Kind => IsFile ? FileKinds.Of(Name) : Kinds.All(k => k == 0) ? FileKind.Other : (FileKind)Array.IndexOf(Kinds, Kinds.Max());
    /// <summary>
    /// There is something inside to show: folders, or files to list. Every folder opens down to its files, and the
    /// buckets open too (their folders, or the loose files), so nothing on a drive is out of reach.
    /// </summary>
    public bool CanOpen => !IsFile && (Children.Count > 0 || Files > 0 || NeedsScan);
    public string FilesText => IsFile ? "" : Files == 1 ? Lang.T("1 file") : Lang.T($"{Files:N0} files");
    public double Share => Parent is { Size: > 0 } p ? 100.0 * Size / p.Size : 100;

    private bool _isHot;
    /// <summary>Pointed at in the list or the map: the other one lights it too.</summary>
    public bool IsHot
    {
        get => _isHot;
        set { if (_isHot == value) return; _isHot = value; PropertyChanged?.Invoke(this, new(nameof(IsHot))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Which <see cref="FileKind"/> a file is, by its extension.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var d = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string list) { foreach (var e in list.Split(' ')) d["." + e] = kind; }
        Add(FileKind.GameData, "pak pck bundle assets resource resources big vpk arc forge rpf bnk wem ucas utoc bdt bhd sb cas toc gpk upk uasset umap xnb wad pk3 pk4 cpk dat bin cache");
        Add(FileKind.Video, "mp4 mkv mov avi webm m4v wmv ts mts m2ts flv mpg mpeg bik bk2 usm");
        Add(FileKind.PhotosAndMusic, "jpg jpeg png gif bmp webp heic heif raw cr2 nef arw dng tif tiff psd mp3 flac wav ogg m4a aac opus wma aiff");
        Add(FileKind.Programs, "exe dll sys msi msix appx cab cat mui pdb lib obj py js ts jar class so node pyc");
        Add(FileKind.Archives, "zip rar 7z tar gz bz2 xz zst iso img vhd vhdx vmdk qcow2 wim esd");
        return d;
    }

    public static FileKind Of(string name) => ByExtension.TryGetValue(System.IO.Path.GetExtension(name), out var kind) ? kind : FileKind.Other;

    /// <summary>The key's words for each kind.</summary>
    public static string Name(FileKind kind) => kind switch
    {
        FileKind.GameData => Lang.T("Game data"),
        FileKind.Video => Lang.T("Video"),
        FileKind.PhotosAndMusic => Lang.T("Photos and music"),
        FileKind.Programs => Lang.T("Programs and code"),
        FileKind.Archives => Lang.T("Archives and disk images"),
        _ => Lang.T("Other files"),
    };
}

public sealed record LargeFile(string Path, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    /// <summary>When it was last changed, for "big files untouched for a year".</summary>
    public DateTime LastWrite { get; init; }
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
    private const int MaxDepth = 200;
    private const int KeepLargestFiles = 40;

    /// <param name="hold">While reset, the scan waits (between folders): the drive is left to something the user is
    /// waiting on, such as the Recycle Bin being emptied.</param>
    /// <param name="folder">The folder being read now, for the page's progress line (about every 150 ms).</param>
    public static Task<ScanResult> ScanAsync(string root, IProgress<(long Files, long Bytes)> progress, CancellationToken ct, ManualResetEventSlim? hold = null,
        IProgress<string>? folder = null) =>
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
                hold?.Wait(ct);
                var node = new FolderNode { Name = name, Path = path, Parent = parent };
                long directSize = 0, directFiles = 0;
                var directKinds = new long[FolderNode.KindCount];
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
                    directKinds[(int)FileKinds.Of(entry.Name)] += entry.OnDisk;
                    if (entry.OnDisk > Interlocked.Read(ref largestFloor)) OfferLargest(System.IO.Path.Combine(path, entry.Name), entry.OnDisk, entry.LastWrite);
                }

                Interlocked.Add(ref files, directFiles);
                Interlocked.Add(ref bytes, directSize);
                if (Environment.TickCount64 - Interlocked.Read(ref lastReport) > 150)
                {
                    Interlocked.Exchange(ref lastReport, Environment.TickCount64);
                    progress.Report((Interlocked.Read(ref files), Interlocked.Read(ref bytes)));
                    folder?.Report(path);
                }

                // Parallelize the top levels; deeper levels are walked sequentially.
                FolderNode[] children;
                if (depth >= MaxDepth && subdirs.Count > 0)
                {
                    // Folders nested this deep (a copy of a folder inside itself, over and over): what's below is added up
                    // here in a loop, not walked one call per level, which would run out of stack and end the app.
                    var below = new Stack<string>(subdirs.Select(d => System.IO.Path.Combine(path, d)));
                    long deepSize = 0, deepFiles = 0;
                    while (below.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var dir = below.Pop();
                        foreach (var entry in Entries(dir))
                        {
                            if (entry.IsFolder)
                            {
                                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) below.Push(System.IO.Path.Combine(dir, entry.Name));
                                continue;
                            }
                            deepFiles++;
                            if (entry.OnDisk <= 0) continue;
                            lock (seen)
                                if (!seen.Add(entry.Id)) continue;
                            deepSize += entry.OnDisk;
                            directKinds[(int)FileKinds.Of(entry.Name)] += entry.OnDisk;
                        }
                    }
                    Interlocked.Add(ref files, deepFiles);
                    Interlocked.Add(ref bytes, deepSize);
                    directSize += deepSize;
                    directFiles += deepFiles;
                    children = [];
                }
                else if (depth < 2 && subdirs.Count > 1)
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
                for (int k = 0; k < FolderNode.KindCount; k++) node.Kinds[k] = directKinds[k] + children.Sum(c => c.Kinds[k]);

                var list = children.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
                if (list.Count > KeepChildren)
                {
                    var rest = list.Skip(KeepChildren).ToList();
                    list = list.Take(KeepChildren).ToList();
                    // Kept, not dropped: the bucket opens to them (they were out of reach before).
                    var smaller = new FolderNode
                    {
                        Name = Lang.T($"{rest.Count} smaller folders"), Path = path, Parent = node, IsBucket = true,
                        Size = rest.Sum(r => r.Size), Files = rest.Sum(r => r.Files), Children = rest,
                    };
                    // Each kept as its total only: what's under it goes, as before, and opening it scans it then.
                    foreach (var r in rest)
                    {
                        r.Parent = smaller;
                        if (r.Children.Count > 0) { r.Children = []; r.NeedsScan = true; }
                    }
                    for (int k = 0; k < FolderNode.KindCount; k++) smaller.Kinds[k] = rest.Sum(r => r.Kinds[k]);
                    list.Add(smaller);
                }
                if (directSize > 0 && list.Count > 0)
                {
                    var loose = new FolderNode
                    {
                        Name = Lang.T("Files in this folder"), Path = path, Parent = node, IsBucket = true, IsLooseFiles = true,
                        Size = directSize, Files = directFiles,
                    };
                    Array.Copy(directKinds, loose.Kinds, FolderNode.KindCount);
                    list.Add(loose);
                }
                node.Children = [.. list.OrderByDescending(c => c.Size)];
                return node;
            }

            void OfferLargest(string path, long size, DateTime lastWrite)
            {
                largest.Add(new LargeFile(path, size) { LastWrite = lastWrite });
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
            {
                var renamed = new FolderNode { Name = root, Path = root, Size = result.Size, Files = result.Files, Children = result.Children };
                Array.Copy(result.Kinds, renamed.Kinds, FolderNode.KindCount);
                result = renamed;
            }
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
    /// The files right in <paramref name="node"/>'s folder, biggest first, as nodes under it (the scan keeps folders and
    /// their totals, not every file): read when a folder or "Files in this folder" is opened. Space on the drive, as the
    /// scan counts it.
    /// </summary>
    public static List<FolderNode> FilesIn(FolderNode node)
    {
        var list = new List<FolderNode>();
        foreach (var e in Entries(node.Path))
        {
            if (e.IsFolder || e.OnDisk <= 0) continue;
            list.Add(new FolderNode { Name = e.Name, Path = System.IO.Path.Combine(node.Path, e.Name), Parent = node, Size = e.OnDisk, Files = 1, IsFile = true });
        }
        return [.. list.OrderByDescending(f => f.Size)];
    }

    /// <summary>
    /// The space the files under a folder take on the drive (for cleanup suggestions), those <paramref name="filter"/>
    /// keeps; 0 if it doesn't exist. Junctions aren't followed, and a file under two names counts once.
    /// Only sizes are wanted, so the files' names are never made (a file <paramref name="filter"/> sees has an empty
    /// name) and one list is used for every folder: a folder tree of hundreds of thousands of files (Windows' update
    /// cache during an update) used to cost about a hundred megabytes of short-lived strings.
    /// </summary>
    public static long FolderSize(string path, Func<DiskEntry, bool>? filter = null)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var seen = new HashSet<long>();
        var folders = new Stack<string>([path]);
        var entries = new List<DiskEntry>();
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            entries.Clear();
            Read(folder, entries, fileNames: false);
            foreach (var e in entries)
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
    public static List<DiskEntry> Entries(string folder)
    {
        var list = new List<DiskEntry>();
        Read(folder, list, fileNames: true);
        return list;
    }

    /// <summary>Adds a folder's entries to <paramref name="list"/>; without <paramref name="fileNames"/> a file's name is left empty (a folder's never is).</summary>
    private static unsafe void Read(string folder, List<DiskEntry> list, bool fileNames)
    {
        string full = System.IO.Path.GetFullPath(folder);
        // The long-path form, so folders deeper than 260 characters are read too.
        string name = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        using var handle = CreateFileW(name, FileListDirectory, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return;

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
                    char* chars = (char*)(p + 80);
                    bool dots = (nameLength == 2 && chars[0] == '.') || (nameLength == 4 && chars[0] == '.' && chars[1] == '.');
                    if (!dots)
                    {
                        var attributes = (FileAttributes)(*(uint*)(p + 56));
                        long onDisk = *(long*)(p + 48);
                        // A name is a string each: made for folders (to go into them), for the few files whose real
                        // size has to be asked for by path, and for every file only when the caller wants names.
                        bool isFolder = (attributes & FileAttributes.Directory) != 0;
                        string entryName = fileNames || isFolder || (attributes & Special) != 0 ? new string(chars, 0, nameLength / 2) : "";
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

    /// <summary>The Recycle Bin: every drive's, or one drive's (<paramref name="root"/>, "E:\").</summary>
    public static (long Size, long Items) RecycleBin(string? root = null)
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(root, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin, every drive's or one drive's (Windows shows its own confirmation and progress).</summary>
    public static bool EmptyRecycleBin(IntPtr owner) => SHEmptyRecycleBin(owner, null, 0) == 0;
    public static bool EmptyRecycleBin(IntPtr owner, string? root) => SHEmptyRecycleBin(owner, root, 0) == 0;
}

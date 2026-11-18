using System.IO;
using System.Text;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>
/// The last scan of each drive (from a Scan button under an answer, or the Storage page's own scan), said for
/// the AI's get_drive_scan: the biggest folders two levels deep, the biggest files by kind and folder, and what could be
/// freed on that drive. The person's Windows user name never goes: their own folder is "your user folder", and a file is
/// said by its kind and folder, never its name.
/// </summary>
public static class DriveScans
{
    private static readonly Dictionary<string, (ScanResult Result, DateTime At)> Last = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static bool _loaded;

    /// <summary>
    /// Where each drive's last scan is saved (the person's call, 2026-10-24: kept across restarts, so the Storage page
    /// and the AI have it without scanning again). On this PC only, beside the history; a file per drive, compressed.
    /// </summary>
    internal static string Folder { get; set; } = Path.Combine(RigsightPaths.DataDir, "drive-scans");

    /// <summary>Clear history: every drive's saved scan goes, from memory and from the disk. Returns how many files went.</summary>
    public static int RemoveAll()
    {
        lock (Gate) Last.Clear();
        return RigsightPaths.DeleteFiles(Folder, "*.json.gz", "storage");
    }

    /// <summary>Keeps a whole drive's scan ("E:\"), and saves it off the caller's thread; a folder's scan isn't a drive's.</summary>
    public static void Keep(string root, ScanResult result) => _ = Keep(root, result, DateTime.Now);

    internal static Task Keep(string root, ScanResult result, DateTime at)
    {
        if (Letter(root) is not { } letter) return Task.CompletedTask;
        lock (Gate) Last[letter] = (result, at);
        return Task.Run(() => Save(letter, result, at));
    }

    public static bool Has(string letter)
    {
        lock (Gate) if (Last.ContainsKey(letter)) return true;
        return File.Exists(FileOf(letter));
    }

    /// <summary>A drive's kept scan and when it was made, for the Storage page's drive tabs; null when it was never scanned.</summary>
    public static (ScanResult Result, DateTime At)? Get(string letter)
    {
        LoadSaved();
        lock (Gate) return Last.TryGetValue(letter, out var scan) ? scan : null;
    }

    /// <summary>
    /// Reads the saved scans once (the Storage page does it off the window's thread before it shows): one made this
    /// run stays over the one saved.
    /// </summary>
    public static void LoadSaved()
    {
        lock (Gate) { if (_loaded) return; _loaded = true; }
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.GetFiles(Folder, "*.json.gz"))
        {
            string letter = Path.GetFileName(file)[..1].ToUpperInvariant();
            try
            {
                SavedScan? saved;
                using (var zip = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress))
                    saved = System.Text.Json.JsonSerializer.Deserialize<SavedScan>(zip);
                if (saved is not { V: 1 }) continue;
                var result = new ScanResult
                {
                    Root = saved.Root.ToNode(null), FileCount = saved.Files, Elapsed = TimeSpan.FromSeconds(saved.Seconds),
                    LargestFiles = [.. saved.Largest.Select(f => new LargeFile(f.P, f.S) { LastWrite = f.W })],
                };
                lock (Gate) Last.TryAdd(letter, (result, saved.At));
                Log.Write("storage", $"Saved scan of {letter}: read ({saved.Files} files, from {saved.At:d MMM HH:mm})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                Log.Write("storage", $"Saved scan of {letter}: couldn't be read ({ex.Message}); a new scan replaces it");
            }
        }
    }

    /// <summary>Forgets every scan this run, so the saved ones are read again (the tests).</summary>
    internal static void Clear()
    {
        lock (Gate) { Last.Clear(); _loaded = false; }
    }

    private static string FileOf(string letter) => Path.Combine(Folder, $"{letter}.json.gz");

    private static void Save(string letter, ScanResult r, DateTime at)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string file = FileOf(letter), temp = file + ".tmp";
            using (var zip = new System.IO.Compression.GZipStream(File.Create(temp), System.IO.Compression.CompressionLevel.Fastest))
                System.Text.Json.JsonSerializer.Serialize(zip, new SavedScan(1, at, r.FileCount, r.Elapsed.TotalSeconds, SavedNode.Of(r.Root),
                    [.. r.LargestFiles.Select(f => new SavedFile(f.Path, f.Size, f.LastWrite))]));
            File.Move(temp, file, overwrite: true);
            Log.Write("storage", $"Scan of {letter}: saved ({new FileInfo(file).Length / 1024} KB)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("storage", $"Scan of {letter}: couldn't be saved ({ex.Message})"); }
    }

    private sealed record SavedScan(int V, DateTime At, long Files, double Seconds, SavedNode Root, List<SavedFile> Largest);
    private sealed record SavedFile(string P, long S, DateTime W);

    /// <summary>A folder as saved: short names, since a drive's tree is thousands of them.</summary>
    private sealed record SavedNode(string N, string P, long S, long F, int T, long[] K, List<SavedNode>? C)
    {
        public static SavedNode Of(FolderNode n) => new(n.Name, n.Path, n.Size, n.Files,
            (n.IsBucket ? 1 : 0) | (n.IsLooseFiles ? 2 : 0) | (n.NeedsScan ? 4 : 0) | (n.IsFile ? 8 : 0),
            n.Kinds, n.Children.Count == 0 ? null : [.. n.Children.Select(Of)]);

        public FolderNode ToNode(FolderNode? parent)
        {
            var node = new FolderNode
            {
                Name = N, Path = P, Size = S, Files = F, Parent = parent,
                IsBucket = (T & 1) != 0, IsLooseFiles = (T & 2) != 0, NeedsScan = (T & 4) != 0, IsFile = (T & 8) != 0,
            };
            for (int k = 0; k < Math.Min(K.Length, FolderNode.KindCount); k++) node.Kinds[k] = K[k];
            if (C is not null) node.Children = [.. C.Select(c => c.ToNode(node))];
            return node;
        }
    }

    /// <summary>"E" from "E:\" or "E:"; null for a folder.</summary>
    public static string? Letter(string root)
    {
        string r = root.TrimEnd('\\', '/');
        return r.Length == 2 && r[1] == ':' && char.IsLetter(r[0]) ? char.ToUpperInvariant(r[0]).ToString() : null;
    }

    /// <summary>The text for the AI, or null when the drive was never scanned.</summary>
    public static string? Text(string letter)
    {
        LoadSaved();
        (ScanResult Result, DateTime At) scan;
        lock (Gate) { if (!Last.TryGetValue(letter, out scan)) return null; }
        return Describe(letter, scan.Result, scan.At, DateTime.Now, CleanupOn(letter));
    }

    /// <summary>
    /// For a drive not scanned: what the Storage page's "Space you could free" lists for it all the same (the system drive's
    /// temporary files, caches, old downloads, the Recycle Bin), or null when it lists nothing. A person asked about the
    /// "Old downloads (4.2 GB)" card before scanning and was told its figure couldn't be confirmed (audit Q076).
    /// </summary>
    public static string? CleanupText(string letter)
    {
        var free = CleanupOn(letter).Where(i => i.Size > 0).ToList();
        return free.Count == 0 ? null : FreeText(letter, free, scanned: false);
    }

    /// <summary>
    /// What could be freed on this drive, the Storage page's "Space you could free" cards for it with their sizes, from the
    /// page's own calculation (<see cref="ViewModels.StorageViewModel.ItemsOn"/>): on the system drive its places, on
    /// another its own Recycle Bin, and on a scanned drive the big files unchanged for a year.
    /// </summary>
    internal static Func<string, IReadOnlyList<(string Title, string Why, long Size)>> CleanupOn { get; set; } = letter =>
    {
        string root = letter + @":\";
        bool system = ViewModels.StorageViewModel.IsSystemRoot(root);
        var scan = Get(letter)?.Result;
        var items = ViewModels.StorageViewModel.ItemsOn(root, system, system ? ViewModels.StorageViewModel.CleanupItems() : [],
            system ? null : StorageScanner.RecycleBin(root), scan, DateTime.Now);
        return [.. items.Select(i => (i.Title, i.ReviewRoot is null ? i.Description : OldFilesWhy(scan!, DateTime.Now), i.Size))];
    };

    /// <summary>The "Big files unchanged for a year" card for the AI: how many, and where in the scan's text they are.</summary>
    private static string OldFilesWhy(ScanResult scan, DateTime now)
    {
        int n = ViewModels.StorageViewModel.UnchangedForAYear(scan, now).Count;
        return $"{(n == 1 ? "1 file" : $"{n} files")} among the scan's biggest files, not changed in over a year: each is in the biggest files above, marked \"not changed since\". The card's Review button shows them on the Storage page.";
    }

    /// <summary>The free list as the AI reads it: every card with its size and the page's own words.</summary>
    private static string FreeText(string letter, IReadOnlyList<(string Title, string Why, long Size)> free, bool scanned)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Space that could be freed on {letter}: (as RigSight's Storage page lists it{(scanned ? "" : ", without a scan")}; all {Units.Bytes(free.Sum(i => i.Size))}). "
            + "These are our own cards with their sizes: take a card's group and size as true.");
        foreach (var (title, why, size) in free.OrderByDescending(i => i.Size)) sb.AppendLine($"  {title}: {Units.Bytes(size)}. {why}");
        return sb.ToString();
    }

    private static string SystemLetter => (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:")[..1].ToUpperInvariant();

    internal static string Describe(string letter, ScanResult r, DateTime at, DateTime now, IReadOnlyList<(string Title, string Why, long Size)> free)
    {
        var sb = new StringBuilder();
        var age = now - at;
        string ago = age.TotalMinutes < 2 ? "just now" : age.TotalHours < 1 ? $"{age.TotalMinutes:0} minutes ago" : age.TotalDays < 1 ? $"{age.TotalHours:0} hours ago" : $"{age.TotalDays:0} days ago";
        sb.AppendLine($"Scan of {letter}: finished {ago}: {Units.Bytes(r.Root.Size)} in {r.FileCount:N0} files.");
        if (age.TotalDays >= 1) sb.AppendLine($"This scan is over a day old: offer [do: scan {letter}] to scan again if it matters.");

        sb.AppendLine("Biggest folders (with the biggest inside each):");
        foreach (var top in r.Root.Children.OrderByDescending(c => c.Size).Take(8))
        {
            if (top.Size <= 0) continue;
            sb.AppendLine($"  {Shown(top.Path, top.IsBucket ? top.Name : null)}: {Units.Bytes(top.Size)} ({Share(top.Size, r.Root.Size)}), {top.Files:N0} files");
            foreach (var inner in top.Children.Where(c => !c.IsBucket).OrderByDescending(c => c.Size).Take(3))
                if (inner.Size >= r.Root.Size / 50) sb.AppendLine($"    {Shown(inner.Path)}: {Units.Bytes(inner.Size)}");
        }

        // By label (approved mock-up "File names in the chat, without sending them", 2026-10-27): the names stay on this PC,
        // and RigSight shows them where the answer writes the label (see FileAt and Controls.Md).
        // Each label carries this scan's code, so an answer read after a rescan never shows another file's name (2026-10-27).
        var files = Biggest(r, now);
        if (files.Count > 0)
        {
            string code = ScanCode(at);
            sb.AppendLine($"Biggest files (their names stay on this PC: to name one, write its whole label exactly as shown, square brackets and all, and RigSight shows its name there; never guess a name):");
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                string kind = Path.GetExtension(f.Path) is { Length: > 1 } ext ? ext.ToLowerInvariant() + " file" : "file with no extension";
                sb.AppendLine($"  [file {letter}{i + 1} {code}]: {Units.Bytes(f.Size)}, a {kind} in {Shown(f.Folder)}"
                    + (f.LastWrite > DateTime.MinValue && f.LastWrite < now.AddYears(-1) ? $", not changed since {f.LastWrite.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}" : ""));
            }
        }

        var freeable = free.Where(i => i.Size > 0).ToList();
        if (freeable.Count > 0) sb.Append(FreeText(letter, freeable, scanned: true));
        else sb.AppendLine($"Nothing on RigSight's list of space that is safe to free (temporary files, caches, old downloads, the Recycle Bin) is on {letter}:.");

        sb.AppendLine("Folder names are the person's own: say what a folder probably is only from its name (a Steam library, videos) and never invent what is inside. Suggest deleting only what the free list names, or what can be got back (a game installed again from its launcher, uninstalled from Installed apps); never a file of their own. [do: open storage <letter>] opens this scan on the Storage page; [do: windows storage-sense] or disk-cleanup clears Windows' own files.");
        return sb.ToString();
    }

    private static string Share(long part, long whole) => whole > 0 ? $"{100.0 * part / whole:0}%" : "";

    /// <summary>
    /// A scan's short code in its files' labels ("k3x"): from when it finished, to the minute, so a label from an earlier
    /// scan of the same drive doesn't match the scan kept now.
    /// </summary>
    internal static string ScanCode(DateTime at)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        long n = (long)(at - DateTime.UnixEpoch).TotalMinutes % (36 * 36 * 36);
        if (n < 0) n += 36 * 36 * 36;
        return new string([digits[(int)(n / 1296)], digits[(int)(n / 36 % 36)], digits[(int)(n % 36)]]);
    }

    /// <summary>
    /// The files in the order the AI reads them ("file C1" is the first): the 8 biggest, then the rest of the "Big files
    /// unchanged for a year" card's, so each file the card counts has a label (audit Q072: its 2 files of 6.6 GB weren't
    /// among the 8 biggest, and the answer said the card didn't add up).
    /// </summary>
    private static List<LargeFile> Biggest(ScanResult r, DateTime now)
    {
        var top = r.LargestFiles.OrderByDescending(f => f.Size).Take(8).ToList();
        return [.. top, .. ViewModels.StorageViewModel.UnchangedForAYear(r, now).OrderByDescending(f => f.Size).Where(f => !top.Contains(f)).Take(8)];
    }

    /// <summary>
    /// A file an answer names by its label ("[file C1 k3x]"), from the scan kept on this PC: its name, where it is and how
    /// big, for showing only (it never goes back to the AI). Null when there's no such file, or the label is from another
    /// scan than the one kept now (rescanned since the answer).
    /// </summary>
    public static (string Name, string Path, string Folder, long Size)? FileAt(string letter, int n, string code)
    {
        LoadSaved();
        ScanResult result;
        lock (Gate)
        {
            if (!Last.TryGetValue(letter.ToUpperInvariant(), out var scan) || ScanCode(scan.At) != code) return null;
            result = scan.Result;
        }
        var files = Biggest(result, DateTime.Now);
        if (n < 1 || n > files.Count) return null;
        var f = files[n - 1];
        return (f.Name, f.Path, Shown(f.Folder), f.Size);
    }

    /// <summary>A path as sent: the person's own folder as "your user folder", their user name nowhere.</summary>
    internal static string Shown(string path, string? bucket = null)
    {
        if (bucket is not null) return bucket;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        string shown = path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "your user folder" + path[home.Length..] : path;
        string user = Environment.UserName;
        return user.Length > 1 ? shown.Replace(user, "(you)", StringComparison.OrdinalIgnoreCase) : shown;
    }
}

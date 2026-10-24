using System.IO;
using System.Text;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>
/// The last scan of each drive this run (from a Scan button under an answer, or the Storage page's own scan), said for
/// the AI's get_drive_scan: the biggest folders two levels deep, the biggest files by kind and folder, and what could be
/// freed on that drive. The person's Windows user name never goes: their own folder is "your user folder", and a file is
/// said by its kind and folder, never its name.
/// </summary>
public static class DriveScans
{
    private static readonly Dictionary<string, (ScanResult Result, DateTime At)> Last = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Keeps a whole drive's scan ("E:\"); a folder's scan isn't a drive's.</summary>
    public static void Keep(string root, ScanResult result)
    {
        if (Letter(root) is not { } letter) return;
        lock (Gate) Last[letter] = (result, DateTime.Now);
    }

    public static bool Has(string letter)
    {
        lock (Gate) return Last.ContainsKey(letter);
    }

    /// <summary>A drive's kept scan and when it was made, for the Storage page's drive tabs; null when none this run.</summary>
    public static (ScanResult Result, DateTime At)? Get(string letter)
    {
        lock (Gate) return Last.TryGetValue(letter, out var scan) ? scan : null;
    }

    /// <summary>"E" from "E:\" or "E:"; null for a folder.</summary>
    public static string? Letter(string root)
    {
        string r = root.TrimEnd('\\', '/');
        return r.Length == 2 && r[1] == ':' && char.IsLetter(r[0]) ? char.ToUpperInvariant(r[0]).ToString() : null;
    }

    /// <summary>The text for the AI, or null when the drive hasn't been scanned this run.</summary>
    public static string? Text(string letter)
    {
        (ScanResult Result, DateTime At) scan;
        lock (Gate) { if (!Last.TryGetValue(letter, out scan)) return null; }
        return Describe(letter, scan.Result, scan.At, DateTime.Now, CleanupOn(letter));
    }

    /// <summary>What could be freed on this drive, as the Storage page lists it (temp files, caches, old downloads, the Recycle Bin).</summary>
    internal static Func<string, IReadOnlyList<(string Title, string Why, long Size)>> CleanupOn { get; set; } = letter =>
        [.. ViewModels.StorageViewModel.CleanupItems()
            .Where(i => i.Path is null ? letter == SystemLetter : string.Equals(Path.GetPathRoot(i.Path)?.TrimEnd('\\'), letter + ":", StringComparison.OrdinalIgnoreCase))
            .Select(i => (i.Title, i.Description, i.Size))];

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

        var files = r.LargestFiles.OrderByDescending(f => f.Size).Take(8).ToList();
        if (files.Count > 0)
        {
            sb.AppendLine("Biggest files (by kind and folder; their names aren't sent):");
            foreach (var f in files)
            {
                string kind = Path.GetExtension(f.Path) is { Length: > 1 } ext ? ext.ToLowerInvariant() + " file" : "file with no extension";
                sb.AppendLine($"  {Units.Bytes(f.Size)}: a {kind} in {Shown(f.Folder)}");
            }
        }

        var freeable = free.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        if (freeable.Count > 0)
        {
            sb.AppendLine($"Space that could be freed on {letter}: (as RigSight's Storage page lists it; all {Units.Bytes(freeable.Sum(i => i.Size))}):");
            foreach (var (title, why, size) in freeable) sb.AppendLine($"  {title}: {Units.Bytes(size)}. {why}");
        }
        else sb.AppendLine($"Nothing on RigSight's list of space that is safe to free (temporary files, caches, old downloads, the Recycle Bin) is on {letter}:.");

        sb.AppendLine("Folder names are the person's own: say what a folder probably is only from its name (a Steam library, videos) and never invent what is inside. Suggest deleting only what the free list names, or what can be got back (a game installed again from its launcher, uninstalled from Installed apps); never a file of their own. [do: open storage <letter>] opens this scan on the Storage page; [do: windows storage-sense] or disk-cleanup clears Windows' own files.");
        return sb.ToString();
    }

    private static string Share(long part, long whole) => whole > 0 ? $"{100.0 * part / whole:0}%" : "";

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

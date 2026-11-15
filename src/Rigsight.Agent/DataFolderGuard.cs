using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rigsight.Agent;

/// <summary>
/// The agent with admin rights works in a folder the person owns (%LocalAppData%\Rigsight). A folder there swapped for a
/// link elsewhere (a junction, or a mount point into Windows' object names with a link for each file) would have it create,
/// move and delete files where only an admin may. So before anything is written:
/// <list type="bullet">
/// <item>the data folder must be a folder, or a link to a folder on a drive (moved to another drive);</item>
/// <item>a link standing in place of one of the folders the agent writes to is removed (the link only, never what it points
/// at) and the folder made again;</item>
/// <item>every one of them is held open for as long as the agent runs, without letting anyone delete or rename it, and is
/// checked to be where it should be, so none can be swapped while it runs.</item>
/// </list>
/// </summary>
internal static class DataFolderGuard
{
    /// <summary>The data folder's folders the agent writes to (updates, widget previews, frame times, rewinds, language packs).</summary>
    internal static readonly string[] Folders = ["updates", "previews", "frames", "rewinds", "languages"];

    private static readonly List<SafeFileHandle> Held = [];

    /// <summary>Makes the data folder safe to work in with admin rights and holds it.</summary>
    /// <returns>What to log once the log may be written; null when it can't be made safe (the agent mustn't use it).</returns>
    public static List<string>? Secure(string dataDir)
    {
        var notes = new List<string>();
        var root = new DirectoryInfo(dataDir);
        if (root.Exists && root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (!IsDrivePath(root.LinkTarget)) return null;
            notes.Add($"The data folder is a link to {root.LinkTarget}");
        }
        Directory.CreateDirectory(dataDir);
        if (Hold(dataDir) is not { } rootReally) return null;

        foreach (var name in Folders)
        {
            string path = Path.Combine(dataDir, name);
            var folder = new DirectoryInfo(path);
            if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                notes.Add($"\"{name}\" in the data folder was a link to {folder.LinkTarget ?? "somewhere else"}: the link was removed and the folder made again");
                Directory.Delete(path); // a link goes itself, never what it points at
            }
            Directory.CreateDirectory(path);
            // Held, then checked to be the data folder's own: swapped for a link in between, it's somewhere else.
            if (Hold(path) is not { } really || !string.Equals(really, Path.Combine(rootReally, name), StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return notes;
    }

    /// <summary>Tests: lets go of every folder held (the agent holds them until it ends).</summary>
    internal static void Release()
    {
        lock (Held)
        {
            foreach (var handle in Held) handle.Dispose();
            Held.Clear();
        }
    }

    /// <summary>A link's target that is a folder on a drive ("D:\..."), not Windows' object names or anything else.</summary>
    internal static bool IsDrivePath(string? target) =>
        target is { Length: >= 3 } t && char.IsAsciiLetter(t[0]) && t[1] == ':' && t[2] == '\\';

    /// <summary>Opens <paramref name="folder"/> for as long as the agent runs, letting others read and write in it but not delete
    /// or rename it; returns where it really is (links followed), or null when it can't be opened.</summary>
    private static string? Hold(string folder)
    {
        var handle = CreateFileW(folder, FileListDirectory, FileShare.Read | FileShare.Write, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            handle.Dispose();
            return null;
        }
        lock (Held) Held.Add(handle);
        string really = new(buffer, 0, (int)length);
        // "\\?\C:\..." for a drive; a share would be "\\?\UNC\server\...".
        return really.StartsWith(@"\\?\", StringComparison.Ordinal) && !really.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? really[4..] : really;
    }

    private const uint FileListDirectory = 0x0001, BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
}

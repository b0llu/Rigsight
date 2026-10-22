using System.Collections.Concurrent;

namespace Rigsight.Core.Apps;

/// <summary>
/// Whether a program's path is on a local drive that's there now, checked before RigSight looks at its files. A
/// network path, or a drive that's unplugged or has no disk in it, can make Windows wait seconds for each look; the
/// agent looks at every program it knows when it starts (AppCatalog.EngineOf), so those are skipped, not waited on.
/// A drive's answer is kept a minute: a USB drive plugged in later is seen soon after.
/// </summary>
public static class ReadyDisk
{
    private static readonly ConcurrentDictionary<string, (bool Ready, long At)> Drives = new(StringComparer.OrdinalIgnoreCase);
    private const long KeepMs = 60_000;

    /// <summary>For tests: how a drive's state is read (letter root such as "E:\" → local and ready).</summary>
    internal static Func<string, bool> ReadDrive { get; set; } = Read;

    public static bool Holds(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        // \\server\share\… and \\?\UNC\…: the network, never waited on.
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        long now = Environment.TickCount64;
        if (Drives.TryGetValue(root, out var had) && now - had.At < KeepMs) return had.Ready;
        bool ready = ReadDrive(root);
        Drives[root] = (ready, now);
        return ready;
    }

    internal static void Forget() => Drives.Clear();

    private static bool Read(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            // A mapped network drive is the network too; a removable or optical drive only when a disk is in it.
            return drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram && drive.IsReady;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
}

using System.IO.Compression;

namespace Rigsight.Core.Stability;

/// <summary>
/// The files Windows saves when one of RigSight's own programs crashes (Windows Error Reporting's "local dumps", set up
/// for both programs by the installer and by the agent when it has admin rights). A crash inside .NET itself ends the
/// program before it can log a word (issue #10: ".NET Runtime internal error" at memcpy in coreclr.dll, twice, with
/// nothing in the log), and Event Viewer only says where inside .NET it stopped. The crash file holds the thread stacks,
/// which say which part of RigSight led there. Nothing is sent anywhere: the person shares the file if they want to.
/// </summary>
public static class CrashFiles
{
    /// <summary>The programs whose crashes are saved: Windows matches them by file name, wherever RigSight is installed.</summary>
    public static readonly string[] Programs = ["Rigsight.Agent.exe", "Rigsight.exe"];

    /// <summary>One folder for the whole PC (the setting is the PC's, not one person's): C:\ProgramData\Rigsight\Crash files.</summary>
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rigsight", "Crash files");

    /// <summary>
    /// How Windows is asked to save them. A small dump with the thread stacks and the memory they point at: enough to see
    /// which code made the call that crashed, a few MB rather than the whole process (hundreds of MB, and everything it
    /// held). Type 0 means "custom": MiniDumpWithHandleData 0x4 | WithUnloadedModules 0x20 | WithIndirectlyReferencedMemory
    /// 0x40 | WithProcessThreadData 0x100 | WithFullMemoryInfo 0x800 | WithThreadInfo 0x1000.
    /// </summary>
    public const int DumpType = 0, CustomDumpFlags = 0x1964, DumpCount = 3;

    /// <summary>Where the setting lives for one program, under HKEY_LOCAL_MACHINE.</summary>
    public static string KeyFor(string program) => $@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\{program}";

    /// <summary>The crash files there are, newest first; none when the folder isn't there or can't be read.</summary>
    public static IReadOnlyList<FileInfo> All(string? folder = null)
    {
        try
        {
            var dir = new DirectoryInfo(folder ?? Folder);
            if (!dir.Exists) return [];
            return dir.GetFiles("*.dmp").Where(f => f.Length > 0).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>The newest crash file, or null.</summary>
    public static FileInfo? Newest(string? folder = null) => All(folder).FirstOrDefault();

    /// <summary>Which program a crash file is of, from the name Windows gives it ("Rigsight.Agent.exe.13588.dmp").</summary>
    public static string ProgramOf(FileInfo file) =>
        Programs.FirstOrDefault(p => file.Name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)) ?? file.Name;

    /// <summary>One line for the copied logs: how many there are and the newest ("Crash files: 2, newest Rigsight.Agent.exe on 2026-11-21 22:10, 3.1 MB").</summary>
    public static string ReportLine(string? folder = null)
    {
        var all = All(folder);
        if (all.Count == 0) return "Crash files: none";
        var newest = all[0];
        return $"Crash files: {all.Count}, newest {ProgramOf(newest)} on {newest.LastWriteTime:yyyy-MM-dd HH:mm}, {newest.Length / 1048576.0:0.0} MB";
    }

    /// <summary>
    /// The newest crash file zipped, ready to attach (GitHub takes .zip but not .dmp, and a dump packs down to a fraction):
    /// RigSight-crash-2026-11-21-2210.zip in <paramref name="into"/>, replacing one of the same name. Null with none.
    /// </summary>
    public static string? ZipNewest(string into, string? folder = null)
    {
        if (Newest(folder) is not { } newest) return null;
        Directory.CreateDirectory(into);
        string zip = Path.Combine(into, $"RigSight-crash-{newest.LastWriteTime:yyyy-MM-dd-HHmm}.zip");
        string part = zip + ".part";
        File.Delete(part);
        using (var archive = ZipFile.Open(part, ZipArchiveMode.Create))
        using (var source = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var entry = archive.CreateEntry(newest.Name, CompressionLevel.Optimal);
            entry.LastWriteTime = newest.LastWriteTime;
            using var target = entry.Open();
            source.CopyTo(target);
        }
        File.Move(part, zip, overwrite: true);
        return zip;
    }
}

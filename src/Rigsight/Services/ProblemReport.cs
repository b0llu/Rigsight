using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Services;

/// <summary>
/// A plain-text report someone can paste where they ask for help or report a bug: the version, whether the agent runs,
/// the hardware, what isn't being read and why, and the end of the log. Only ever copied to the clipboard by the user;
/// the app sends nothing. The user's folder name is taken out of every path in it (see <see cref="Scrub"/>).
/// </summary>
public static class ProblemReport
{
    public const int LogLines = 150;

    /// <summary>Where a bug is reported: a form in the browser, which the report is pasted into. The app only opens it.</summary>
    public const string BugFormUrl = "https://docs.google.com/forms/d/e/1FAIpQLSeatRubFiR1Jtjf1yIVL5dimhPVogILB8W3fucdiddmNfCIjA/viewform";

    /// <summary>
    /// Takes who it is out of a report before it leaves the PC in someone's paste: the folder under C:\Users in any
    /// path becomes "(user)". Only paths are touched. That folder is where the name is (a log line is a path far more
    /// often than a sentence), and it isn't always the Windows user name; and swapping a name wherever its letters
    /// appear would damage the report: a user called Sam would send "(user)sung SSD 980", one called Max "CPU Core (user)".
    /// </summary>
    public static string Scrub(string text) => UsersFolder.Replace(text, "$1(user)");

    private static readonly System.Text.RegularExpressions.Regex UsersFolder =
        // The folder up to the next slash, spaces included ("Sam Smith"), so no part of a name is left behind.
        new(@"([\\/]Users[\\/])[^\\/""'<>|:*?\r\n]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public sealed record Facts(
        string AppVersion,
        bool AgentConnected,
        bool AgentIsAdmin,
        string Windows,
        IReadOnlyList<(string Type, string Name)> Hardware,
        SensorStatus? Sensors,
        IReadOnlyList<string> Log);

    public static string Build(Facts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Rigsight {f.AppVersion} problem report");
        sb.AppendLine($"Windows: {f.Windows}");
        sb.AppendLine("Agent: " + (!f.AgentConnected ? "not running" : f.AgentIsAdmin ? "running with admin rights" : "running without admin rights"));
        if (f.Hardware.Count > 0)
        {
            sb.AppendLine("Hardware:");
            // Drives and the per-core or virtual parts add nothing here; the chips and cards say what the PC is.
            foreach (var (type, name) in f.Hardware.Where(h => h.Type is not ("Storage" or "Network")).DistinctBy(h => h.Name))
                sb.AppendLine($"  {type}: {name}");
        }
        if (f.Sensors is { } s)
        {
            foreach (var p in s.Paused) sb.AppendLine($"Not read: {p.Part} (left to {string.Join(", ", p.Because)})");
            if (s.SafeMode) sb.AppendLine("Safe mode: the last hardware scan didn't finish");
            if (s.StoppedForMemory) sb.AppendLine("Stopped: kernel memory grew during the hardware scan");
        }
        sb.AppendLine();
        sb.AppendLine($"Last {f.Log.Count} log lines:");
        foreach (var line in f.Log) sb.AppendLine(line);
        return sb.ToString();
    }

    /// <summary>The last lines of the shared log (open for writing by the agent meanwhile, so shared read-write).</summary>
    public static List<string> ReadLogTail(string path, int lines = LogLines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var tail = new Queue<string>(lines + 1);
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > lines) tail.Dequeue();
            }
            return [.. tail];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string WindowsVersion() => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>The report for this PC, with the log from <see cref="RigsightPaths.LogFile"/>.</summary>
    public static string ForThisPc(string appVersion, bool connected, bool admin, IReadOnlyList<(string, string)> hardware, SensorStatus? sensors) =>
        Scrub(Build(new Facts(appVersion, connected, admin, WindowsVersion(), hardware, sensors, ReadLogTail(RigsightPaths.LogFile))));
}

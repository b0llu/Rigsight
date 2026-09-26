using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Services;

/// <summary>
/// A plain-text report someone can paste where they ask for help: the version, whether the agent runs, the hardware,
/// what isn't being read and why, and the end of the log. Only ever copied to the clipboard by the user; nothing is sent.
/// </summary>
public static class ProblemReport
{
    public const int LogLines = 150;

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
        Build(new Facts(appVersion, connected, admin, WindowsVersion(), hardware, sensors, ReadLogTail(RigsightPaths.LogFile)));
}

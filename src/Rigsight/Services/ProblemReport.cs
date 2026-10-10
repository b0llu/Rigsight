using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;

namespace Rigsight.Services;

/// <summary>
/// A plain-text report someone can paste where they ask for help or report a bug: the version, whether the agent runs,
/// the hardware, what isn't being read and why, and the end of the log. Only ever copied to the clipboard by the user;
/// the app sends nothing. The user's folder name is taken out of every path in it (see <see cref="Scrub"/>).
/// </summary>
public static class ProblemReport
{
    /// <summary>
    /// How much of the log goes along. The log is all there will ever be to go on (nobody gets to look at the PC), so
    /// it is generous: with repeats folded, weeks of an ordinary PC. Still short enough to paste where help is asked.
    /// </summary>
    public const int LogLines = 600;

    /// <summary>Lines kept from the agent's latest start when that is further back than <see cref="LogLines"/>.</summary>
    public const int StartLines = 40;

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
        IReadOnlyList<string> Log)
    {
        /// <summary>The agent's own version ("0.19.3"), which can differ from the window's right after an update.</summary>
        public string? AgentVersion { get; init; }

        /// <summary>More about this PC and this install, a line each ("PawnIO driver: not installed"), under the agent's line.</summary>
        public IReadOnlyList<string> Details { get; init; } = [];

        /// <summary>The main readings at this moment, a line per part ("CPU: temperature none, load 12.0 %…"): a sensor
        /// that is listed and still reads nothing shows here and nowhere else.</summary>
        public IReadOnlyList<string> Readings { get; init; } = [];
    }

    public static string Build(Facts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Rigsight {f.AppVersion} problem report");
        sb.AppendLine($"Windows: {f.Windows}");
        sb.AppendLine("Agent: " + (!f.AgentConnected ? "not running" : f.AgentIsAdmin ? "running with admin rights" : "running without admin rights")
            + (f.AgentConnected && f.AgentVersion is not null ? $", version {f.AgentVersion}" : ""));
        foreach (var line in f.Details) sb.AppendLine(line);
        if (f.Hardware.Count > 0)
        {
            sb.AppendLine("Hardware:");
            // Drives and the per-core or virtual parts add nothing here; the chips and cards say what the PC is.
            foreach (var (type, name) in f.Hardware.Where(h => h.Type is not ("Storage" or "Network")).DistinctBy(h => h.Name))
                sb.AppendLine($"  {type}: {name}");
        }
        if (f.Readings.Count > 0)
        {
            sb.AppendLine("Readings now:");
            foreach (var line in f.Readings) sb.AppendLine("  " + line);
        }
        if (f.Sensors is { } s)
        {
            if (s.Driver is not null) sb.AppendLine($"Driver warning shown: {s.Driver}" + (s.DriverInstall is null ? "" : $" (install: {s.DriverInstall})"));
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
            var tail = new Queue<string>(Math.Min(lines, 1024) + 1);
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

    /// <summary>
    /// The log as it goes into the report: the file before it was last moved aside and the one being written, as one;
    /// a line said over and over folded into one with a count; the last <paramref name="lines"/> of that; and, when the
    /// agent's latest start is further back, that start's own lines first (version, hardware found, what wasn't,
    /// the driver), since they say more about a PC than any other.
    /// </summary>
    public static List<string> LogForReport(string path, int lines = LogLines)
    {
        var all = Fold([.. ReadLogTail(path + ".old", int.MaxValue), .. ReadLogTail(path, int.MaxValue)]);
        if (all.Count <= lines) return all;
        var tail = all.GetRange(all.Count - lines, lines);
        int start = all.FindLastIndex(l => l.Contains("] [agent] Starting", StringComparison.Ordinal));
        if (start < 0 || start >= all.Count - lines) return tail;
        int count = Math.Min(StartLines, all.Count - lines - start);
        return [.. all.GetRange(start, count), "(lines between left out)", .. tail];
    }

    /// <summary>Lines that say the same thing one after another (the time aside) become the first, and how often it came again and until when.</summary>
    internal static List<string> Fold(List<string> lines)
    {
        var folded = new List<string>(lines.Count);
        string? said = null, lastTime = null;
        int again = 0;
        void Close()
        {
            if (again > 0) folded.Add($"    (the same line {again} more time{(again == 1 ? "" : "s")}, the last at {lastTime})");
            again = 0;
        }
        foreach (var line in lines)
        {
            // "[2026-10-09 07.42.20] [update] Check failed…": what follows the time is what is compared.
            int end = line.StartsWith('[') ? line.IndexOf("] ", StringComparison.Ordinal) : -1;
            string? message = end > 0 ? line[(end + 2)..] : null;
            if (message is not null && message == said)
            {
                again++;
                lastTime = line[1..end];
                continue;
            }
            Close();
            said = message;
            folded.Add(line);
        }
        Close();
        return folded;
    }

    public static string WindowsVersion() => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// What this PC and this install say for themselves, a line each: when and in which language and time zone, where
    /// Rigsight runs from, the sensor driver, how big the history is, the screens. Each is its own try: one that can't
    /// be read is left out, never the report.
    /// </summary>
    public static List<string> ThisPc()
    {
        var lines = new List<string>();
        void Add(Func<string> line)
        {
            try { lines.Add(line()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        Add(() =>
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return $"Copied: {DateTime.Now:yyyy-MM-dd HH:mm} UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}, {System.Globalization.CultureInfo.CurrentCulture.Name}, PC on for {(int)up.TotalHours} h {up.Minutes} min";
        });
        Add(() => $"Installed in: {AppContext.BaseDirectory.TrimEnd('\\')}" + (RigsightPaths.IsTestInstance ? " (a test copy)" : ""));
        Add(() => $"PawnIO driver: {PawnIoDriver.Describe()}");
        Add(() => $"Memory integrity: {PawnIoDriver.MemoryIntegrity switch { true => "on", false => "off", null => "not set" }}");
        Add(() => $"Data: history {Size(RigsightPaths.Database)}, log {Size(RigsightPaths.LogFile)}");
        Add(() => $"Screens: main {System.Windows.SystemParameters.PrimaryScreenWidth:0}x{System.Windows.SystemParameters.PrimaryScreenHeight:0}, "
            + $"all together {System.Windows.SystemParameters.VirtualScreenWidth:0}x{System.Windows.SystemParameters.VirtualScreenHeight:0}");
        return lines;

        static string Size(string path) => new FileInfo(path) is { Exists: true } file ? $"{file.Length / 1048576.0:0.0} MB" : "none";
    }

    /// <summary>The settings that change what Rigsight does, in one line. No app names, labels or anything else typed by the person.</summary>
    public static string SettingsLine(RigsightSettings s) =>
        $"Settings: readings every {s.LiveRefreshMs / 1000.0:0.#} s open and {s.Tracking.SensorIntervalMs / 1000.0:0.#} s closed, "
        + $"overlay {(s.Overlay.Enabled ? "on" : "off")}, {s.Widgets.Count(w => w.Enabled)} widgets on, {s.TraySensors.Count} taskbar readings, "
        + $"step aside for RGB and fan apps {(s.YieldToHardwareApps ? "on" : "off")}, tracking {(s.Tracking.IsPaused(Core.Data.TimeUtil.NowUnix()) ? "paused" : "on")}, "
        + $"history kept {(s.Tracking.KeepHistoryDays == 0 ? "for ever" : s.Tracking.KeepHistoryDays + " days")}, updates {(s.AutoUpdate ? "automatic" : "by hand")}, "
        + $"{(s.UseFahrenheit ? "°F" : "°C")}, {s.Theme} theme";

    /// <summary>The report for this PC, with the log from <see cref="RigsightPaths.LogFile"/>.</summary>
    public static string ForThisPc(string appVersion, bool connected, bool admin, IReadOnlyList<(string, string)> hardware, SensorStatus? sensors,
        string? agentVersion = null, IReadOnlyList<string>? readings = null, RigsightSettings? settings = null)
    {
        var details = ThisPc();
        if (settings is not null) details.Add(SettingsLine(settings));
        return Scrub(Build(new Facts(appVersion, connected, admin, WindowsVersion(), hardware, sensors, LogForReport(RigsightPaths.LogFile))
        {
            AgentVersion = agentVersion,
            Details = details,
            Readings = readings ?? [],
        }));
    }
}

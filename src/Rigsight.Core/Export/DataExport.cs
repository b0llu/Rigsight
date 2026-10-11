using System.Globalization;
using System.IO.Compression;
using System.Text;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Export;

/// <summary>The tables of history that can be saved as a file each.</summary>
public enum ExportTable { AppUsage, Sessions, Temperatures, Fans, Internet, Memory, DriveSpace, Crashes, Timeline }

/// <summary>A column of an exported table: its heading, and what it holds (for the read-me).</summary>
public sealed record ExportColumn(string Name, string What);

/// <summary>
/// The history as files other programs open: each table a CSV (comma-separated, UTF-8, dates and numbers written one
/// way whatever the PC's language, so Excel and a script read the same thing), and everything at once as a zip with
/// the database itself. Times are the PC's local time; temperatures are in °C whatever the app shows.
/// </summary>
public static class DataExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Table(string File, string About, ExportColumn[] Columns, string CountTable, string CountColumn, int BytesPerRow,
        Func<RigsightDb, long, long, Names, IEnumerable<object?[]>> Rows);

    /// <summary>Apps by their id, named as the app names them (the user's own names first).</summary>
    private sealed class Names(RigsightDb db, RigsightSettings s)
    {
        private readonly Dictionary<long, AppRow> _apps = db.LoadApps().ToDictionary(a => a.Id);
        private readonly Dictionary<string, AppRow> _byExe = db.LoadApps().GroupBy(a => a.Exe, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public string? Of(long? id) => id is long i && _apps.TryGetValue(i, out var a) ? Name(a) : null;
        public string? Exe(long? id) => id is long i && _apps.TryGetValue(i, out var a) ? a.Exe : null;
        public string? Category(long? id) => id is long i && _apps.TryGetValue(i, out var a)
            ? AppCatalog.Label(s.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category) : null;
        public string? OfExe(string exe) => exe.Length == 0 ? null : _byExe.TryGetValue(exe, out var a) ? Name(a) : AppCatalog.KnownName(exe) ?? AppCatalog.FallbackName(exe);
        private string Name(AppRow a) => s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;
    }

    private static ExportColumn C(string name, string what) => new(name, what);
    private static string Day(DateTime t) => t.ToString("yyyy-MM-dd", Inv);
    private static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", Inv);
    private static string Time(long unix) => Time(TimeUtil.FromUnix(unix));
    private static double? R(double? v, int digits = 1) => v is double d ? Math.Round(d, digits) : null;
    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 2);
    private static double Min(double seconds) => Math.Round(seconds / 60, 1);

    private static readonly Dictionary<ExportTable, Table> Tables = new()
    {
        [ExportTable.AppUsage] = new("App usage.csv", "Each app on each day it was open.",
            [C("Day", "The day"), C("App", "The app's name"), C("Program", "Its program file"), C("Category", "Games, Browsing…"),
             C("In use (min)", "In front and being used"), C("Away (min)", "In front while nobody was at the PC"),
             C("Background (min)", "Open with a window, another app in front"), C("Minimized (min)", "Open with every window minimized"),
             C("Peak memory (MB)", "The most memory it held"), C("Peak CPU (°C)", "The CPU's highest while it was in front"),
             C("Peak GPU (°C)", "The GPU's highest while it was in front")],
            "app_hour", "ts", 78,
            (db, from, to, n) => db.GetAppHours(from, to)
                .GroupBy(h => (Day: TimeUtil.FromUnix(h.Ts).Date, h.AppId))
                .OrderBy(g => g.Key.Day).ThenByDescending(g => g.Sum(h => h.FgSec))
                .Select(g => new object?[]
                {
                    Day(g.Key.Day), n.Of(g.Key.AppId), n.Exe(g.Key.AppId), n.Category(g.Key.AppId),
                    Min(g.Sum(h => h.FgSec)), Min(g.Sum(h => h.IdleSec)), Min(g.Sum(h => h.BgSec)), Min(g.Sum(h => h.MinSec)),
                    R(g.Max(h => h.MemMax), 0), R(g.Max(h => h.CpuTempMax)), R(g.Max(h => h.GpuTempMax)),
                })),

        [ExportTable.Sessions] = new("Sessions.csv", "Each stretch an app was in front, from when it came to the front until it left.",
            [C("Start", "When it started"), C("End", "When it ended"), C("App", "The app's name"), C("Program", "Its program file"),
             C("In use (min)", "Time in front and being used"), C("Game", "Yes for a game"), C("Peak CPU (°C)", "The CPU's highest in it"),
             C("Peak GPU (°C)", "The GPU's highest in it")],
            "sessions", "start", 92,
            (db, from, to, n) => db.GetSessions(from, to).Select(x => new object?[]
            {
                Time(x.Start), Time(x.End), n.Of(x.AppId), n.Exe(x.AppId), Min(x.ActiveSec), x.IsGame ? "Yes" : "No", R(x.CpuTempMax), R(x.GpuTempMax),
            })),

        [ExportTable.Temperatures] = new("Temperatures, load and power.csv", "Every minute the PC was on.",
            [C("Time", "The minute"), C("CPU (°C)", "Its average over the minute"), C("CPU highest (°C)", "Its highest reading"), C("CPU lowest (°C)", "Its lowest reading"),
             C("GPU (°C)", "Its average over the minute"), C("GPU highest (°C)", "Its highest reading"), C("GPU lowest (°C)", "Its lowest reading"),
             C("GPU hot spot highest (°C)", "The card's hottest point"), C("GPU memory highest (°C)", "The card's memory chips"),
             C("CPU load (%)", "Average"), C("GPU load (%)", "Average"), C("CPU power (W)", "Average"), C("GPU power (W)", "Average"),
             C("CPU speed (MHz)", "Average"), C("GPU speed (MHz)", "Average"), C("Memory in use (GB)", "At the end of the minute"),
             C("In use (s)", "Seconds somebody was at the PC"), C("Away (s)", "Seconds nobody was"), C("App in front", "The app in front most of the minute"),
             C("Working the CPU", "The app doing clearly the most of its work, if one was"), C("Working the GPU", "The same for the GPU")],
            "system_minute", "ts", 118,
            (db, from, to, n) => db.GetMinutes(from, to).Select(m => new object?[]
            {
                Time(m.Ts), R(m.CpuTemp), R(m.CpuTempMax), R(m.CpuTempMin), R(m.GpuTemp), R(m.GpuTempMax), R(m.GpuTempMin), R(m.GpuHotMax), R(m.GpuMemMax),
                R(m.CpuLoad), R(m.GpuLoad), R(m.CpuPower), R(m.GpuPower), R(m.CpuClock, 0), R(m.GpuClock, 0), R(m.RamUsed, 2),
                m.ActiveSec, m.IdleSec, n.Of(m.FgApp), n.Of(m.CpuApp), n.Of(m.GpuApp),
            })),

        [ExportTable.Fans] = new("Fans.csv", "Every fan, every minute the PC was on.",
            [C("Time", "The minute"), C("Fan", "The fan's name"), C("On", "The part it is read from"), C("Speed (rpm)", "Its average over the minute"),
             C("Fastest (rpm)", "Its highest reading")],
            "fan_minute", "ts", 56,
            (db, from, to, n) =>
            {
                var fans = db.GetFans().ToDictionary(f => f.Id);
                return db.GetFanMinutes(from, to).Select(m => new object?[]
                {
                    Time(m.Ts), fans.TryGetValue(m.Fan, out var f) ? f.Name : null, f?.Hardware, m.RpmAvg, m.RpmMax,
                });
            }),

        [ExportTable.Internet] = new("Internet.csv", "What each app downloaded and uploaded on each day.",
            [C("Day", "The day"), C("App", "The app's name"), C("Program", "Its program file"), C("Downloaded (MB)", "From the internet"),
             C("Uploaded (MB)", "To the internet"), C("Downloaded in the background (MB)", "While another app was in front"),
             C("Uploaded in the background (MB)", "While another app was in front"), C("Downloaded while away (MB)", "While nobody was at the PC"),
             C("Local network (MB)", "To and from other devices at home, not counted above")],
            "net_app_day", "day", 70,
            (db, from, to, n) => db.GetNetAppDays(from, to).OrderBy(u => u.Ts).ThenByDescending(u => u.Down + u.Up).Select(u => new object?[]
            {
                Day(TimeUtil.FromUnix(u.Ts)), n.Of(u.App), n.Exe(u.App), Mb(u.Down), Mb(u.Up), Mb(u.BgDown), Mb(u.BgUp), Mb(u.AwayDown), Mb(u.Lan),
            })),

        [ExportTable.Memory] = new("Memory.csv", "The memory each app held, hour by hour.",
            [C("Hour", "The start of the hour"), C("App", "The app's name"), C("Program", "Its program file"), C("Memory (MB)", "Its average over the hour"),
             C("Peak memory (MB)", "The most it held")],
            "app_hour", "ts", 62,
            (db, from, to, n) => db.GetAppHours(from, to).Where(h => h.MemN > 0).OrderBy(h => h.Ts).ThenByDescending(h => h.MemSum / h.MemN).Select(h => new object?[]
            {
                Time(h.Ts), n.Of(h.AppId), n.Exe(h.AppId), Math.Round(h.MemSum / h.MemN), R(h.MemMax, 0),
            })),

        [ExportTable.DriveSpace] = new("Drive space.csv", "How full each drive was on each day.",
            [C("Day", "The day"), C("Drive", "Its letter"), C("Used (GB)", "Space in use"), C("Size (GB)", "The drive's size")],
            "drive_day", "day", 34,
            (db, from, to, n) => db.GetDriveDays(from).Where(d => d.Day < to).Select(d => new object?[]
            {
                Day(TimeUtil.FromUnix(d.Day)), d.Drive.TrimEnd('\\'), Math.Round(d.UsedGb, 1), Math.Round(d.TotalGb, 1),
            })),

        [ExportTable.Crashes] = new("Crashes.csv", "Apps that crashed or stopped responding, blue screens, unexpected shutdowns and graphics driver resets.",
            [C("Time", "When it happened"), C("What", "In a sentence"), C("Kind", "The kind of problem"), C("App", "The app, if it was one"),
             C("Program", "Its program file"), C("Module", "The file the fault was in"), C("Code", "Windows' error code"), C("Detail", "What Windows wrote down")],
            "crashes", "ts", 180,
            (db, from, to, n) => db.GetCrashes(from, to).OrderBy(e => e.Ts).Select(e => new object?[]
            {
                Time(e.Ts), CrashExplainer.Explain(e, n.OfExe(e.AppExe)).Title, e.Kind.ToString(), n.OfExe(e.AppExe), e.AppExe, e.Module, e.Code, e.Detail,
            })),

        [ExportTable.Timeline] = new("Timeline changes.csv", "What changed on the PC: drivers, Windows updates, apps installed, removed and updated, startup programs, settings, parts, drive space.",
            [C("Time", "When it changed (a change found by a check is dated when it was found)"), C("Kind", "The kind of change"), C("What", "In a sentence"),
             C("Before", "What it was"), C("After", "What it became")],
            "changes", "ts", 96,
            (db, from, to, n) => db.GetChanges(from, to)
                .Concat(StorageChanges.From(db.GetDriveDays(0)).Where(c => TimeUtil.ToUnix(c.Time) >= from && TimeUtil.ToUnix(c.Time) < to))
                .OrderBy(c => c.Time).Select(c => new object?[] { Time(c.Time), c.Kind.ToString(), c.Title, c.Was, c.Now })),
    };

    public static string FileName(ExportTable table) => Tables[table].File;
    public static IReadOnlyList<ExportColumn> Columns(ExportTable table) => Tables[table].Columns;

    /// <summary>Writes one table for [from, to) as CSV; returns how many rows it had.</summary>
    public static int Write(RigsightDb db, ExportTable table, DateTime from, DateTime to, RigsightSettings settings, TextWriter writer)
    {
        var t = Tables[table];
        writer.WriteLine(string.Join(",", t.Columns.Select(c => Cell(c.Name))));
        int rows = 0;
        foreach (var row in t.Rows(db, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to), new Names(db, settings)))
        {
            writer.WriteLine(string.Join(",", row.Select(Cell)));
            rows++;
        }
        return rows;
    }

    /// <summary>
    /// Writes one table for [from, to) as text laid out to be read as it is (Notepad, a forum post): what it is and the
    /// period on top, then the columns lined up under their headings, numbers to the right. Returns how many rows it had.
    /// </summary>
    public static int WriteText(RigsightDb db, ExportTable table, DateTime from, DateTime to, RigsightSettings settings, TextWriter writer)
    {
        var t = Tables[table];
        var rows = t.Rows(db, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to), new Names(db, settings)).ToList();
        writer.WriteLine(Path.GetFileNameWithoutExtension(t.File));
        writer.WriteLine(t.About);
        writer.WriteLine($"{Day(from)} to {Day(to.AddDays(-1))}  ·  {(rows.Count == 1 ? "1 row" : $"{rows.Count:N0} rows")}  ·  times are this PC's local time, temperatures in °C");
        writer.WriteLine();
        foreach (var line in TextTable([.. t.Columns.Select(c => c.Name)], rows)) writer.WriteLine(line);
        return rows.Count;
    }

    /// <summary>
    /// Rows as lines of aligned text under their headings and a rule: a column of numbers is set to the right, any
    /// other to the left; an empty cell is a dash, so a gap isn't taken for a missing column.
    /// </summary>
    public static IEnumerable<string> TextTable(IReadOnlyList<string> headings, IReadOnlyList<object?[]> rows)
    {
        int n = headings.Count;
        var text = rows.Select(r => Enumerable.Range(0, n).Select(i => i < r.Length ? Plain(r[i]) : "–").ToArray()).ToList();
        var numeric = Enumerable.Range(0, n).Select(i => rows.Count > 0 && rows.All(r => i >= r.Length || r[i] is null or double or int or long) && rows.Any(r => i < r.Length && r[i] is not null)).ToArray();
        var width = Enumerable.Range(0, n).Select(i => Math.Max(headings[i].Length, text.Count == 0 ? 0 : text.Max(r => r[i].Length))).ToArray();
        string Line(IReadOnlyList<string> cells) =>
            string.Join("  ", Enumerable.Range(0, n).Select(i => numeric[i] ? cells[i].PadLeft(width[i]) : cells[i].PadRight(width[i]))).TrimEnd();
        yield return Line(headings);
        yield return string.Join("  ", width.Select(w => new string('-', w)));
        foreach (var row in text) yield return Line(row);
    }

    /// <summary>A value as it stands in a text table: on one line, numbers with a point, a dash for nothing.</summary>
    private static string Plain(object? value) => value switch
    {
        null => "–",
        string s => s.Length == 0 ? "–" : s.ReplaceLineEndings(" "),
        double d => d.ToString("0.##", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => value.ToString() ?? "–",
    };

    /// <summary>A table into a file, as CSV or as laid-out text (both UTF-8 with its mark, which is what makes Excel and
    /// Notepad read the degree sign and names right).</summary>
    public static int WriteFile(RigsightDb db, ExportTable table, DateTime from, DateTime to, RigsightSettings settings, string path, bool asText)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return asText ? WriteText(db, table, from, to, settings, writer) : Write(db, table, from, to, settings, writer);
    }

    /// <summary>About how big a table's file will be (bytes), from how many rows it has in the period: said before exporting.</summary>
    public static long EstimateBytes(RigsightDb db, ExportTable table, DateTime from, DateTime to)
    {
        var t = Tables[table];
        long rows = db.CountRows(t.CountTable, t.CountColumn, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        // A day of an app's hours becomes one line of app usage.
        if (table == ExportTable.AppUsage) rows = rows / 5 + 1;
        return 200 + rows * t.BytesPerRow;
    }

    /// <summary>A value as a CSV cell: quoted when it holds a comma, a quote or a line break; numbers with a point.</summary>
    internal static string Cell(object? value)
    {
        string text = value switch
        {
            null => "",
            string s => s,
            double d => d.ToString("0.##", Inv),
            IFormattable f => f.ToString(null, Inv),
            _ => value.ToString() ?? "",
        };
        return text.AsSpan().IndexOfAny(",\"\r\n") < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
    }

    /// <summary>The read-me of the full copy: what each file is and what each column holds.</summary>
    public static string ReadMe(DateTime made, string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Rigsight full copy, made {made.ToString("d MMMM yyyy, HH:mm", Inv)} by Rigsight {version}.");
        sb.AppendLine();
        sb.AppendLine("rigsight.db is the history itself, a SQLite database: everything Rigsight has recorded on this PC. Putting it");
        sb.AppendLine(@"back in %LocalAppData%\Rigsight (with Rigsight closed from its tray icon) restores the history on this or another PC.");
        sb.AppendLine("settings.json holds the settings.");
        sb.AppendLine();
        sb.AppendLine("The other files are the same history as tables (CSV: comma-separated, UTF-8), which Excel and most other");
        sb.AppendLine("programs open. Times are this PC's local time. Temperatures are in °C. An empty cell means nothing was recorded.");
        foreach (var table in Enum.GetValues<ExportTable>())
        {
            var t = Tables[table];
            sb.AppendLine();
            sb.AppendLine(t.File);
            sb.AppendLine("  " + t.About);
            foreach (var c in t.Columns) sb.AppendLine($"  {c.Name}: {c.What}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Everything in one zip: the database as it is now, every table for all time, the settings and a read-me. The
    /// database copy is made beside the zip and removed again.
    /// </summary>
    public static void WriteFullCopy(RigsightDb db, RigsightSettings settings, string zipPath, string? settingsFile, string version)
    {
        string copy = zipPath + ".db.tmp";
        File.Delete(copy);
        File.Delete(zipPath);
        try
        {
            db.CopyTo(copy);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(copy, "rigsight.db", CompressionLevel.Optimal);
            if (settingsFile is not null && File.Exists(settingsFile)) zip.CreateEntryFromFile(settingsFile, "settings.json");
            using (var readMe = new StreamWriter(zip.CreateEntry("Read me.txt").Open(), new UTF8Encoding(true)))
                readMe.Write(ReadMe(DateTime.Now, version));
            var (from, to) = (new DateTime(2000, 1, 1), DateTime.Today.AddDays(2));
            foreach (var table in Enum.GetValues<ExportTable>())
            {
                using var writer = new StreamWriter(zip.CreateEntry(FileName(table), CompressionLevel.Optimal).Open(), new UTF8Encoding(true));
                Write(db, table, from, to, settings, writer);
            }
        }
        finally
        {
            File.Delete(copy);
        }
    }
}

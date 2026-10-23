using System.Globalization;
using System.IO.Compression;
using System.Text;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Export;

/// <summary>The tables of history that can be saved as a file each.</summary>
public enum ExportTable { AppUsage, Sessions, Temperatures, Fans, Internet, Memory, DriveSpace, Crashes, Timeline, InternetHours }

/// <summary>A column of an exported table: its heading, and what it holds (for the read-me).</summary>
public sealed record ExportColumn(string Name, string What);

/// <summary>
/// The history as files other programs open: each table a CSV (comma-separated, UTF-8, dates and numbers written one
/// way whatever the PC's language, so Excel and a script read the same thing), and everything at once as a zip with
/// the database itself. Times are the PC's local time; temperatures are in °C whatever the app shows. The words (file
/// names, headings, the read-me and what the cells say) are in the language in use; the tables keep their English in
/// <see cref="Tables"/> and are translated as they're written.
/// </summary>
public static class DataExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Table(string File, string About, ExportColumn[] Columns, string CountTable, string CountColumn, int BytesPerRow,
        Func<RigsightDb, long, long, Names, ExportFilter, IEnumerable<object?[]>> Rows)
    {
        /// <summary>For a table whose columns are readings: the reading each column is (None: always there).</summary>
        public ExportReadings[]? Readings { get; init; }

        /// <summary>Which columns the filter keeps.</summary>
        public bool[] Keep(ExportFilter f) => [.. Columns.Select((_, i) => Readings is null || Readings[i] == ExportReadings.None || (f.Readings & Readings[i]) != 0)];
    }

    /// <summary>Apps by their id, named as the app names them (the user's own names first).</summary>
    private sealed class Names(RigsightDb db, RigsightSettings s)
    {
        public RigsightSettings Settings => s;
        private readonly Dictionary<long, AppRow> _apps = db.LoadApps().ToDictionary(a => a.Id);
        private readonly Dictionary<string, AppRow> _byExe = db.LoadApps().GroupBy(a => a.Exe, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public string? Of(long? id) => id is long i && _apps.TryGetValue(i, out var a) ? Name(a) : null;
        public string? Exe(long? id) => id is long i && _apps.TryGetValue(i, out var a) ? a.Exe : null;
        public string? Category(long? id) => id is long i && _apps.TryGetValue(i, out var a)
            ? AppCatalog.Label(s.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category) : null;
        /// <summary>Whether a filter keeps an app (by its id): its program, its category (the user's own first) and its name.</summary>
        public bool Has(ExportFilter f, long? id)
        {
            if (f.Apps is null && f.Category is null && f.AppWords is not { Count: > 0 }) return true;
            if (id is not long i || !_apps.TryGetValue(i, out var a)) return false;
            return f.HasApp(a.Exe, s.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category, Name(a));
        }

        public string? OfExe(string exe) => exe.Length == 0 ? null : _byExe.TryGetValue(exe, out var a) ? Name(a) : AppCatalog.KnownName(exe) ?? AppCatalog.FallbackName(exe);
        private string Name(AppRow a) => s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;
    }

    private static ExportColumn C(string name, string what) => new(name, what);

    /// <summary>A column as written: its heading and what it holds, in the language in use.</summary>
    private static ExportColumn Shown(ExportColumn c) => new(Lang.T(c.Name), Lang.T(c.What));

    private static string YesNo(bool yes) => yes ? Lang.T("Yes") : Lang.T("No");

    /// <summary>
    /// A kind as a cell: its code name in English ("AppCrash"), which sheets may filter on; in another language a word
    /// people read, since the whole file is in that language.
    /// </summary>
    private static string Kind(CrashKind k) => !Lang.Translating ? k.ToString() : Lang.T(k switch
    {
        CrashKind.AppCrash => Lang.Key("App crash"),
        CrashKind.AppHang => Lang.Key("App froze"),
        CrashKind.GpuDriverReset => Lang.Key("Graphics driver reset"),
        CrashKind.SystemCrash => Lang.Key("Blue screen"),
        _ => Lang.Key("Sudden shut-off"),
    });

    /// <summary>A change's kind as a cell (see <see cref="Kind(CrashKind)"/>).</summary>
    private static string Kind(ChangeKind k) => !Lang.Translating ? k.ToString() : Lang.T(k switch
    {
        ChangeKind.Driver => Lang.Key("Driver"),
        ChangeKind.WindowsUpdate => Lang.Key("Windows update"),
        ChangeKind.Windows => Lang.Key("New Windows version"),
        ChangeKind.AppInstalled => Lang.Key("App installed"),
        ChangeKind.AppRemoved => Lang.Key("App removed"),
        ChangeKind.AppUpdated => Lang.Key("App updated"),
        ChangeKind.Startup => Lang.Key("Startup"),
        ChangeKind.Hardware => Lang.Key("Hardware"),
        ChangeKind.Firmware => Lang.Key("BIOS"),
        ChangeKind.Setting => Lang.Key("Setting"),
        ChangeKind.Storage => Lang.Key("Drive space"),
        ChangeKind.TaskEnded => Lang.Key("Task ended"),
        ChangeKind.Fans => Lang.Key("Fans"),
        ChangeKind.GameUpdated => Lang.Key("Game updated"),
        _ => k.ToString(),
    });
    private static string Day(DateTime t) => t.ToString("yyyy-MM-dd", Inv);
    private static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", Inv);
    private static string Time(long unix) => Time(TimeUtil.FromUnix(unix));
    private static double? R(double? v, int digits = 1) => v is double d ? Math.Round(d, digits) : null;
    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 2);
    private static double Min(double seconds) => Math.Round(seconds / 60, 1);

    private static readonly Dictionary<ExportTable, Table> Tables = new()
    {
        [ExportTable.AppUsage] = new(Lang.Key("App usage.csv"), Lang.Key("Each app on each day it was open."),
            [C(Lang.Key("Day"), Lang.Key("The day")), C(Lang.Key("App"), Lang.Key("The app's name")), C(Lang.Key("Program"), Lang.Key("Its program file")), C(Lang.Key("Category"), Lang.Key("Games, Browsing…")),
             C(Lang.Key("In use (min)"), Lang.Key("In front and being used")), C(Lang.Key("Away (min)"), Lang.Key("In front while nobody was at the PC")),
             C(Lang.Key("Background (min)"), Lang.Key("Open with a window, another app in front")), C(Lang.Key("Minimized (min)"), Lang.Key("Open with every window minimized")),
             C(Lang.Key("Memory peak (MB)"), Lang.Key("The most memory it held")), C(Lang.Key("CPU peak (°C)"), Lang.Key("The CPU's highest while it was in front")),
             C(Lang.Key("GPU peak (°C)"), Lang.Key("The GPU's highest while it was in front"))],
            "app_hour", "ts", 78,
            (db, from, to, n, f) => db.GetAppHours(from, to).Where(h => n.Has(f, h.AppId))
                .GroupBy(h => (Day: TimeUtil.FromUnix(h.Ts).Date, h.AppId))
                .OrderBy(g => g.Key.Day).ThenByDescending(g => g.Sum(h => h.FgSec))
                .Select(g => new object?[]
                {
                    Day(g.Key.Day), n.Of(g.Key.AppId), n.Exe(g.Key.AppId), n.Category(g.Key.AppId),
                    Min(g.Sum(h => h.FgSec)), Min(g.Sum(h => h.IdleSec)), Min(g.Sum(h => h.BgSec)), Min(g.Sum(h => h.MinSec)),
                    R(g.Max(h => h.MemMax), 0), R(g.Max(h => h.CpuTempMax)), R(g.Max(h => h.GpuTempMax)),
                })),

        [ExportTable.Sessions] = new(Lang.Key("Sessions.csv"), Lang.Key("Each stretch an app was in front, from when it came to the front until it left."),
            [C(Lang.Key("Start"), Lang.Key("When it started")), C(Lang.Key("End"), Lang.Key("When it ended")), C(Lang.Key("App"), Lang.Key("The app's name")), C(Lang.Key("Program"), Lang.Key("Its program file")),
             C(Lang.Key("In use (min)"), Lang.Key("Time in front and being used")), C(Lang.Key("Game"), Lang.Key("Yes for a game")), C(Lang.Key("CPU peak (°C)"), Lang.Key("The CPU's highest in it")),
             C(Lang.Key("GPU peak (°C)"), Lang.Key("The GPU's highest in it"))],
            "sessions", "start", 92,
            (db, from, to, n, f) => db.GetSessions(from, to).Where(x => n.Has(f, x.AppId) && (!f.GamesOnly || x.IsGame)).Select(x => new object?[]
            {
                Time(x.Start), Time(x.End), n.Of(x.AppId), n.Exe(x.AppId), Min(x.ActiveSec), YesNo(x.IsGame), R(x.CpuTempMax), R(x.GpuTempMax),
            })),

        [ExportTable.Temperatures] = new(Lang.Key("Temperatures, load and power.csv"), Lang.Key("Every minute the PC was on."),
            [C(Lang.Key("Time"), Lang.Key("The minute")), C(Lang.Key("CPU (°C)"), Lang.Key("Its average over the minute")), C(Lang.Key("CPU highest (°C)"), Lang.Key("Its highest reading")), C(Lang.Key("CPU lowest (°C)"), Lang.Key("Its lowest reading")),
             C(Lang.Key("GPU (°C)"), Lang.Key("Its average over the minute")), C(Lang.Key("GPU highest (°C)"), Lang.Key("Its highest reading")), C(Lang.Key("GPU lowest (°C)"), Lang.Key("Its lowest reading")),
             C(Lang.Key("GPU hot spot highest (°C)"), Lang.Key("The card's hottest point")), C(Lang.Key("GPU memory highest (°C)"), Lang.Key("The card's memory chips")),
             C(Lang.Key("CPU load (%)"), Lang.Key("Average")), C(Lang.Key("GPU load (%)"), Lang.Key("Average")), C(Lang.Key("CPU power (W)"), Lang.Key("Average")), C(Lang.Key("GPU power (W)"), Lang.Key("Average")),
             C(Lang.Key("CPU speed (MHz)"), Lang.Key("Average")), C(Lang.Key("GPU speed (MHz)"), Lang.Key("Average")), C(Lang.Key("Memory in use (GB)"), Lang.Key("At the end of the minute")), C(Lang.Key("Video memory in use (GB)"), Lang.Key("The graphics card's, every app together, its average over the minute")),
             C(Lang.Key("In use (s)"), Lang.Key("Seconds somebody was at the PC")), C(Lang.Key("Away (s)"), Lang.Key("Seconds nobody was")), C(Lang.Key("App in front"), Lang.Key("The app in front most of the minute")),
             C(Lang.Key("Working the CPU"), Lang.Key("The app doing clearly the most of its work, if one was")), C(Lang.Key("Working the GPU"), Lang.Key("The same for the GPU"))],
            "system_minute", "ts", 118,
            (db, from, to, n, f) => db.GetMinutes(from, to).Select(m => new object?[]
            {
                Time(m.Ts), R(m.CpuTemp), R(m.CpuTempMax), R(m.CpuTempMin), R(m.GpuTemp), R(m.GpuTempMax), R(m.GpuTempMin), R(m.GpuHotMax), R(m.GpuMemMax),
                R(m.CpuLoad), R(m.GpuLoad), R(m.CpuPower), R(m.GpuPower), R(m.CpuClock, 0), R(m.GpuClock, 0), R(m.RamUsed, 2), m.VramUsed is double vram ? Math.Round(vram / 1024, 2) : null,
                m.ActiveSec, m.IdleSec, n.Of(m.FgApp), n.Of(m.CpuApp), n.Of(m.GpuApp),
            }))
        {
            Readings =
            [
                ExportReadings.None, ExportReadings.CpuTemp, ExportReadings.CpuTemp, ExportReadings.CpuTemp, ExportReadings.GpuTemp, ExportReadings.GpuTemp, ExportReadings.GpuTemp,
                ExportReadings.GpuHotSpot, ExportReadings.GpuMemory, ExportReadings.Load, ExportReadings.Load, ExportReadings.Power, ExportReadings.Power,
                ExportReadings.Load, ExportReadings.Load, ExportReadings.None, ExportReadings.None, ExportReadings.None, ExportReadings.None, ExportReadings.None, ExportReadings.None, ExportReadings.None,
            ],
        },

        [ExportTable.Fans] = new(Lang.Key("Fans.csv"), Lang.Key("Every fan, every minute the PC was on."),
            [C(Lang.Key("Time"), Lang.Key("The minute")), C(Lang.Key("Fan"), Lang.Key("The fan's name")), C(Lang.Key("On"), Lang.Key("The part it is read from")), C(Lang.Key("Speed (rpm)"), Lang.Key("Its average over the minute")),
             C(Lang.Key("Fastest (rpm)"), Lang.Key("Its highest reading"))],
            "fan_minute", "ts", 56,
            (db, from, to, n, f) =>
            {
                var fans = db.GetFans().ToDictionary(f => f.Id);
                return db.GetFanMinutes(from, to).Select(m => new object?[]
                {
                    Time(m.Ts), fans.TryGetValue(m.Fan, out var f) ? f.Name : null, f?.Hardware, m.RpmAvg, m.RpmMax,
                });
            }),

        [ExportTable.Internet] = new(Lang.Key("Internet.csv"), Lang.Key("What each app downloaded and uploaded on each day."),
            [C(Lang.Key("Day"), Lang.Key("The day")), C(Lang.Key("App"), Lang.Key("The app's name")), C(Lang.Key("Program"), Lang.Key("Its program file")), C(Lang.Key("Downloaded (MB)"), Lang.Key("From the internet")),
             C(Lang.Key("Uploaded (MB)"), Lang.Key("To the internet")), C(Lang.Key("Downloaded in the background (MB)"), Lang.Key("While another app was in front")),
             C(Lang.Key("Uploaded in the background (MB)"), Lang.Key("While another app was in front")), C(Lang.Key("Downloaded while away (MB)"), Lang.Key("While nobody was at the PC")),
             C(Lang.Key("Local network (MB)"), Lang.Key("To and from other devices at home, not counted above"))],
            "net_app_day", "day", 70,
            (db, from, to, n, f) => db.GetNetAppDays(from, to).Where(u => n.Has(f, u.App)).OrderBy(u => u.Ts).ThenByDescending(u => u.Down + u.Up).Select(u => new object?[]
            {
                Day(TimeUtil.FromUnix(u.Ts)), n.Of(u.App), n.Exe(u.App), Mb(u.Down), Mb(u.Up), Mb(u.BgDown), Mb(u.BgUp), Mb(u.AwayDown), Mb(u.Lan),
            })),

        [ExportTable.InternetHours] = new(Lang.Key("Internet each hour.csv"), Lang.Key("What each app downloaded and uploaded in each hour."),
            [C(Lang.Key("Hour"), Lang.Key("The start of the hour")), C(Lang.Key("App"), Lang.Key("The app's name")), C(Lang.Key("Program"), Lang.Key("Its program file")), C(Lang.Key("Downloaded (MB)"), Lang.Key("From the internet")),
             C(Lang.Key("Uploaded (MB)"), Lang.Key("To the internet")), C(Lang.Key("Downloaded in the background (MB)"), Lang.Key("While another app was in front")),
             C(Lang.Key("Uploaded in the background (MB)"), Lang.Key("While another app was in front")), C(Lang.Key("Downloaded while away (MB)"), Lang.Key("While nobody was at the PC"))],
            "net_app_hour", "ts", 64,
            (db, from, to, n, f) => db.GetNetAppHours(from, to).Where(u => n.Has(f, u.App)).OrderBy(u => u.Ts).ThenByDescending(u => u.Down + u.Up).Select(u => new object?[]
            {
                Time(u.Ts), n.Of(u.App), n.Exe(u.App), Mb(u.Down), Mb(u.Up), Mb(u.BgDown), Mb(u.BgUp), Mb(u.AwayDown),
            })),

        [ExportTable.Memory] = new(Lang.Key("Memory.csv"), Lang.Key("The memory each app held, hour by hour."),
            [C(Lang.Key("Hour"), Lang.Key("The start of the hour")), C(Lang.Key("App"), Lang.Key("The app's name")), C(Lang.Key("Program"), Lang.Key("Its program file")), C(Lang.Key("Memory (MB)"), Lang.Key("Its average over the hour")),
             C(Lang.Key("Memory peak (MB)"), Lang.Key("The most it held"))],
            "app_hour", "ts", 62,
            (db, from, to, n, f) => db.GetAppHours(from, to).Where(h => h.MemN > 0 && n.Has(f, h.AppId)).OrderBy(h => h.Ts).ThenByDescending(h => h.MemSum / h.MemN).Select(h => new object?[]
            {
                Time(h.Ts), n.Of(h.AppId), n.Exe(h.AppId), Math.Round(h.MemSum / h.MemN), R(h.MemMax, 0),
            })),

        [ExportTable.DriveSpace] = new(Lang.Key("Drive space.csv"), Lang.Key("How full each drive was on each day."),
            [C(Lang.Key("Day"), Lang.Key("The day")), C(Lang.Key("Drive"), Lang.Key("Its letter")), C(Lang.Key("Used (GB)"), Lang.Key("Space in use")), C(Lang.Key("Size (GB)"), Lang.Key("The drive's size"))],
            "drive_day", "day", 34,
            (db, from, to, n, f) => db.GetDriveDays(from).Where(d => d.Day < to && f.HasDrive(d.Drive)).Select(d => new object?[]
            {
                Day(TimeUtil.FromUnix(d.Day)), d.Drive.TrimEnd('\\'), Math.Round(d.UsedGb, 1), Math.Round(d.TotalGb, 1),
            })),

        [ExportTable.Crashes] = new(Lang.Key("Crashes.csv"), Lang.Key("Apps that crashed or froze, blue screens, sudden shut-offs and graphics driver resets."),
            [C(Lang.Key("Time"), Lang.Key("When it happened")), C(Lang.Key("What"), Lang.Key("In a sentence")), C(Lang.Key("Kind"), Lang.Key("The kind of problem")), C(Lang.Key("App"), Lang.Key("The app, if it was one")),
             C(Lang.Key("Program"), Lang.Key("Its program file")), C(Lang.Key("Module"), Lang.Key("The file the fault was in")), C(Lang.Key("Code"), Lang.Key("Windows' error code")), C(Lang.Key("Detail"), Lang.Key("What Windows wrote down")),
             C(Lang.Key("Most likely"), Lang.Key("What most likely happened")), C(Lang.Key("How sure"), Lang.Key("How sure the cause is: confirmed (by Windows' own record), likely, possibly, or not enough to say")),
             C(Lang.Key("App version"), Lang.Key("The program's version")), C(Lang.Key("Driver"), Lang.Key("The driver a blue screen names"))],
            "crashes", "ts", 260,
            (db, from, to, n, f) =>
            {
                var crashes = db.GetCrashes(from, to).Where(f.HasCrash).OrderBy(e => e.Ts).ToList();
                var found = CrashCases.Of(db, crashes, n.Settings);
                return crashes.Select(e => new object?[]
                {
                    Time(e.Ts), CrashExplainer.Explain(e, n.OfExe(e.AppExe)).Title, Kind(e.Kind), n.OfExe(e.AppExe), e.AppExe, e.Module, e.Code,
                    e.Detail is null ? null : SavedText.Show(e.Detail),
                    found.GetValueOrDefault(e.Id)?.Head, found.GetValueOrDefault(e.Id)?.HowSure, e.Facts?.AppVersion, e.Facts?.Driver,
                });
            }),

        [ExportTable.Timeline] = new(Lang.Key("Timeline changes.csv"), Lang.Key("What changed on the PC: drivers, Windows updates, apps installed, removed and updated, startup programs, settings, parts, drive space."),
            [C(Lang.Key("Time"), Lang.Key("When it changed (a change found by a check is dated when it was found)")), C(Lang.Key("Kind"), Lang.Key("The kind of change")), C(Lang.Key("What"), Lang.Key("In a sentence")),
             C(Lang.Key("Before"), Lang.Key("What it was")), C(Lang.Key("After"), Lang.Key("What it became")),
             C(Lang.Key("Result"), Lang.Key("What a big change made better or worse, measured on this PC; empty where it changed nothing measurable")),
             C(Lang.Key("How sure"), Lang.Key("How sure it was the change: likely or possibly")), C(Lang.Key("Compared"), Lang.Key("The days compared before and after"))],
            "changes", "ts", 120,
            (db, from, to, n, f) =>
            {
                var results = ChangeEffects.Read(db, n.Settings);
                return db.GetChanges(from, to)
                    .Concat(StorageChanges.From(db.GetDriveDays(0)).Where(c => TimeUtil.ToUnix(c.Time) >= from && TimeUtil.ToUnix(c.Time) < to))
                    .Where(c => f.HasChange(c) && (!f.ResultsOnly || results.TryGetValue(c.Time.Date, out var hit) && hit.Changes.Contains(c)))
                    .OrderBy(c => c.Time).Select(c => results.TryGetValue(c.Time.Date, out var r) && r.Main == c
                        ? new object?[] { Time(c.Time), Kind(c.Kind), SavedText.Show(c.Title), c.Was, c.Now, r.Headline, r.HowSure,
                            Lang.T($"{r.From:d MMM yyyy} to {r.Day.AddDays(-1):d MMM yyyy} against {r.Day.AddDays(1):d MMM yyyy} to {r.To.AddDays(-1):d MMM yyyy}") }
                        : new object?[] { Time(c.Time), Kind(c.Kind), SavedText.Show(c.Title), c.Was, c.Now, null, null, null });
            }),
    };

    public static string FileName(ExportTable table) => Lang.T(Tables[table].File);
    public static IReadOnlyList<ExportColumn> Columns(ExportTable table) => [.. Tables[table].Columns.Select(Shown)];

    /// <summary>Writes one table for [from, to) as CSV; returns how many rows it had.</summary>
    public static int Write(RigsightDb db, ExportTable table, DateTime from, DateTime to, RigsightSettings settings, TextWriter writer, ExportFilter? filter = null)
    {
        var t = Tables[table];
        var f = filter ?? ExportFilter.None;
        var keep = t.Keep(f);
        writer.WriteLine(string.Join(",", Kept(t.Columns, keep).Select(c => Cell(Lang.T(c.Name)))));
        int rows = 0;
        foreach (var row in t.Rows(db, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to), new Names(db, settings), f))
        {
            writer.WriteLine(string.Join(",", Kept(row, keep).Select(Cell)));
            rows++;
        }
        return rows;
    }

    private static IEnumerable<T> Kept<T>(IEnumerable<T> cells, bool[] keep) => cells.Where((_, i) => i >= keep.Length || keep[i]);

    /// <summary>
    /// Writes one table for [from, to) as text laid out to be read as it is (Notepad, a forum post): what it is and the
    /// period on top, then the columns lined up under their headings, numbers to the right. Returns how many rows it had.
    /// </summary>
    public static int WriteText(RigsightDb db, ExportTable table, DateTime from, DateTime to, RigsightSettings settings, TextWriter writer, ExportFilter? filter = null)
    {
        var t = Tables[table];
        var f = filter ?? ExportFilter.None;
        var keep = t.Keep(f);
        var rows = t.Rows(db, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to), new Names(db, settings), f).Select(r => Kept(r, keep).ToArray()).ToList();
        writer.WriteLine(Path.GetFileNameWithoutExtension(Lang.T(t.File)));
        writer.WriteLine(Lang.T(t.About));
        writer.WriteLine(Heading(from, to, rows.Count));
        writer.WriteLine();
        foreach (var line in TextTable([.. Kept(t.Columns, keep).Select(c => Lang.T(c.Name))], rows)) writer.WriteLine(line);
        return rows.Count;
    }

    /// <summary>The line under a text file's title: its period (to the minute when it doesn't start and end at midnight) and how many rows.</summary>
    private static string Heading(DateTime from, DateTime to, int count)
    {
        bool days = from.TimeOfDay == TimeSpan.Zero && to.TimeOfDay == TimeSpan.Zero;
        string first = days ? Day(from) : Time(from), last = days ? Day(to.AddDays(-1)) : Time(to);
        // All time: from whenever the history starts.
        if (from.Year <= 2000)
            return count == 1
                ? Lang.T($"Everything up to {last}  ·  1 row  ·  times are this PC's local time, temperatures in °C")
                : Lang.T($"Everything up to {last}  ·  {count:N0} rows  ·  times are this PC's local time, temperatures in °C");
        return count == 1
            ? Lang.T($"{first} to {last}  ·  1 row  ·  times are this PC's local time, temperatures in °C")
            : Lang.T($"{first} to {last}  ·  {count:N0} rows  ·  times are this PC's local time, temperatures in °C");
    }

    /// <summary>A file made from what a page shows (a list, a summary), as CSV or as laid-out text, like a table of history.</summary>
    public static int WriteSheet(ExportSheet sheet, string path, bool asText)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (asText)
        {
            writer.WriteLine(sheet.Title);
            writer.WriteLine(sheet.About);
            writer.WriteLine(sheet.Rows.Count == 1 ? Lang.T($"{sheet.When}  ·  1 row") : Lang.T($"{sheet.When}  ·  {sheet.Rows.Count:N0} rows"));
            writer.WriteLine();
            foreach (var line in TextTable(sheet.Columns, sheet.Rows)) writer.WriteLine(line);
        }
        else
        {
            writer.WriteLine(string.Join(",", sheet.Columns.Select(Cell)));
            foreach (var row in sheet.Rows) writer.WriteLine(string.Join(",", row.Select(Cell)));
        }
        return sheet.Rows.Count;
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
    public static int WriteFile(RigsightDb db, ExportTable table, DateTime from, DateTime to, RigsightSettings settings, string path, bool asText, ExportFilter? filter = null)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return asText ? WriteText(db, table, from, to, settings, writer, filter) : Write(db, table, from, to, settings, writer, filter);
    }

    /// <summary>What a table holds, in a line (the language in use): said under its name in a page's Export box.</summary>
    public static string About(ExportTable table) => Lang.T(Tables[table].About);

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
        // English keeps its own month names whatever the PC's region; another language writes the month in its own words.
        string when = made.ToString("d MMMM yyyy, HH:mm", Lang.Translating ? CultureInfo.CurrentCulture : Inv);
        sb.AppendLine(Lang.T($"RigSight full copy, made {when} by RigSight {version}."));
        sb.AppendLine();
        // A paragraph at a time, its English line breaks kept (the key holds them).
        void Paragraph(string text) => sb.AppendLine(text.Replace("\n", Environment.NewLine));
        Paragraph(Lang.T("rigsight.db is the history itself, a SQLite database: everything RigSight has recorded on this PC. Putting it\nback in %LocalAppData%\\Rigsight (with RigSight closed from its tray icon) restores the history on this or another PC."));
        Paragraph(Lang.T("settings.json holds the settings."));
        sb.AppendLine();
        Paragraph(Lang.T("The other files are the same history as tables (CSV: comma-separated, UTF-8), which Excel and most other\nprograms open. Times are this PC's local time. Temperatures are in °C. An empty cell means nothing was recorded."));
        foreach (var table in Enum.GetValues<ExportTable>())
        {
            var t = Tables[table];
            sb.AppendLine();
            sb.AppendLine(Lang.T(t.File));
            sb.AppendLine("  " + Lang.T(t.About));
            foreach (var c in t.Columns.Select(Shown)) sb.AppendLine($"  {c.Name}: {c.What}");
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
            using (var readMe = new StreamWriter(zip.CreateEntry(Lang.T("Read me.txt")).Open(), new UTF8Encoding(true)))
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

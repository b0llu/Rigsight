using Microsoft.Data.Sqlite;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

// Every insight a database gives: each day, week and month it has (and the year), built exactly as the app builds
// them, one line each with its key, rank, tone and the detail shown on hover. For reading them all at once after a
// change to the insights: on a copy of real history they show what users would see; on a test copy, what a year of
// generated history makes of them. Reads the database read-only (safe while Rigsight is running).
//
//   Rigsight.InsightAudit [<rigsight.db>] [--settings <settings.json>] [--days <n>] [--out <file>]
//     (no database: the installed Rigsight's, with its settings; --days: only the last n days, weeks and months)
var arg = (string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rigsight");
string db = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : Path.Combine(data, "rigsight.db");
string? settingsFile = arg("--settings") ?? (db == Path.Combine(data, "rigsight.db") ? Path.Combine(data, "settings.json") : null);
int? days = int.TryParse(arg("--days"), out int d) ? d : null;

var settings = settingsFile is not null && File.Exists(settingsFile) ? SettingsStore.Load(settingsFile) : new RigsightSettings();

// --changes: what each big change made better or worse instead (Timeline's "Before and after"), worked out exactly as the
// app does, every reading and check with it; a database copy is read, never written. Nothing printed for a change means
// it made no difference that could be measured, which is what the app shows too.
//     Rigsight.InsightAudit [<rigsight.db>] --changes [--out <file>]
if (args.Contains("--changes"))
{
    string copy = Path.Combine(Path.GetTempPath(), $"rigsight-audit-{Guid.NewGuid():N}.db");
    using (var source = new SqliteConnection($"Data Source={db};Mode=ReadOnly"))
    using (var target = new SqliteConnection($"Data Source={copy};Pooling=False"))
    {
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }
    var lines = new List<string>();
    using (var w = RigsightDb.OpenWriter(copy))
    {
        var changes = w.GetChanges(0, long.MaxValue / 2);
        var results = ChangeEffects.Read(w, settings, changes);
        lines.Add($"# {changes.Count(ChangeEffects.IsMajor)} big changes, {results.Count} with a result");
        foreach (var r in results.Values.OrderBy(r => r.Day))
        {
            lines.Add($"{r.Day:yyyy-MM-dd} {string.Join(" + ", r.Changes.Select(c => c.Title))}");
            lines.Add($"  {r.Tone} · {r.Chip} · {r.Headline}");
            lines.Add($"  {r.Why}");
            foreach (var x in r.Rows) lines.Add($"    {x.Label}: {x.Before} -> {x.After} ({x.Meaning}){(x.Counted ? "" : " [not counted]")}");
            foreach (var c in r.Checks) lines.Add($"    {(c.Ok ? "ok" : "!!")} {c.Title}: {c.Detail}");
        }
    }
    SqliteConnection.ClearAllPools();
    File.Delete(copy);
    if (arg("--out") is { } changeFile) File.WriteAllLines(changeFile, lines); else foreach (var changeLine in lines) Console.WriteLine(changeLine);
    return;
}

// --crashes: every crash's finding instead (what it says, how sure, what it rests on, what was ruled out), worked out
// exactly as the app does. The database is copied first and the copy given what this PC's Windows logs still hold about
// its crashes, as the agent does at its first start after an update; the database itself is never written.
//     Rigsight.InsightAudit [<rigsight.db>] --crashes [--out <file>]
if (args.Contains("--crashes"))
{
    string copy = Path.Combine(Path.GetTempPath(), $"rigsight-audit-{Guid.NewGuid():N}.db");
    using (var source = new SqliteConnection($"Data Source={db};Mode=ReadOnly"))
    using (var target = new SqliteConnection($"Data Source={copy};Pooling=False"))
    {
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }
    var lines = new List<string>();
    using (var w = RigsightDb.OpenWriter(copy))
    {
        if (w.FirstCrashTime() is long firstCrash)
        {
            var from = TimeUtil.FromUnix(firstCrash).AddDays(-1);
            var reading = System.Diagnostics.Stopwatch.StartNew();
            var read = Rigsight.Core.Stability.CrashLogReader.ReadSince(from);
            long crashMs = reading.ElapsedMilliseconds;
            var signs = Rigsight.Core.Stability.CrashLogReader.ReadSigns(from);
            lines.Add($"# Windows' logs since {from:yyyy-MM-dd}: {read.Count} crashes read in {crashMs} ms, {signs.Count} signs in {reading.ElapsedMilliseconds - crashMs} ms");
            reading.Restart();
            Rigsight.Core.Stability.CrashLogReader.ReadSince(DateTime.Now.AddMinutes(-30));
            Rigsight.Core.Stability.CrashLogReader.ReadSigns(DateTime.Now.AddMinutes(-30));
            lines.Add($"# the ten-minute scan (last 30 minutes): {reading.ElapsedMilliseconds} ms");
            // As the agent does: the signs first (a hang's own time places the shut-off after it), then what is stored
            // put right, then the graphics driver resets from Windows' hang reports, which earlier versions didn't read.
            w.InsertSigns(signs);
            w.InsertCrashes(read, knownOnly: true);
            var resets = w.InsertCrashes(Rigsight.Core.Stability.CrashLogReader.ReadResets(from));
            lines.Add($"# graphics driver resets added from Windows' hang reports: {resets.Count}");
        }
        var crashes = w.GetCrashes(0, long.MaxValue / 2);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var findings = Rigsight.Core.Stability.CrashCases.Of(w, crashes, settings);
        lines.Add($"# {db}: {crashes.Count} crashes, findings in {watch.ElapsedMilliseconds} ms");
        foreach (var c in crashes)
        {
            var f = findings[c.Id];
            lines.Add($"## {c.Time:ddd yyyy-MM-dd HH:mm:ss}  {c.Kind} {c.AppExe}  [{f.Chip}]{(f.Outside ? " (outside)" : "")}");
            lines.Add($"   {f.Head} {f.Text}");
            lines.Add($"   line: {f.Said}");
            lines.Add($"   try:  {f.Advice}");
            foreach (var p in f.Points) lines.Add($"   + {p.Title}: {p.Detail}");
            foreach (var p in f.RuledOut) lines.Add($"   - {p.Title}: {p.Detail}");
            if (f.CantTell is { } cant) lines.Add($"   ? {cant}");
        }
        lines.Add("");
        lines.AddRange(findings.Values.GroupBy(f => f.Chip).OrderByDescending(g => g.Count()).Select(g => $"{g.Count(),5}  {g.Key}"));
    }
    SqliteConnection.ClearAllPools();
    try { File.Delete(copy); } catch (IOException) { }
    if (arg("--out") is { } crashFile) File.WriteAllLines(crashFile, lines); else foreach (var crashLine in lines) Console.WriteLine(crashLine);
    return;
}

using var reader = RigsightDb.OpenReader(db) ?? throw new Exception($"Can't open {db}");
var (first, last) = Span(db);
if (days is int n && last.Date.AddDays(1 - n) > first) first = last.Date.AddDays(1 - n);

var output = new List<string> { $"# {db}: {first:yyyy-MM-dd} to {last:yyyy-MM-dd}" };
void Run(ReportRange range, DateTime anchor, string label)
{
    Report r;
    try { r = ReportBuilder.Build(reader, range, anchor, settings); }
    catch (Exception ex) { output.Add($"## {label}  !! {ex.GetType().Name}: {ex.Message}"); return; }
    if (!r.HasData) return;
    output.Add($"## {label}  ({r.ActiveSec / 3600:0.0} h in use)");
    foreach (var i in r.Insights)
        output.Add($"  [{i.Key}|{i.Priority}|{i.Tone}] {i.Text}{(i.Detail is { } detail ? "   {" + detail + "}" : "")}");
}
for (var day = first.Date; day <= last.Date; day = day.AddDays(1)) Run(ReportRange.Day, day.AddHours(12), $"DAY {day:ddd yyyy-MM-dd}");
for (var week = first.Date.AddDays(-(((int)first.DayOfWeek + 6) % 7)); week <= last.Date; week = week.AddDays(7)) Run(ReportRange.Week, week.AddHours(12), $"WEEK {week:yyyy-MM-dd}");
for (var month = new DateTime(first.Year, first.Month, 1); month <= last.Date; month = month.AddMonths(1)) Run(ReportRange.Month, month.AddDays(14), $"MONTH {month:yyyy-MM}");
Run(ReportRange.Year, last, $"YEAR {last:yyyy}");

// Then how often each kind of line came up: a line said on most days is a line nobody reads.
output.Add("# Lines by kind");
foreach (var g in output.Where(l => l.StartsWith("  [", StringComparison.Ordinal)).GroupBy(l => l[3..l.IndexOf('|')]).OrderByDescending(g => g.Count()))
    output.Add($"  {g.Count(),5}  {g.Key}");

if (arg("--out") is { } file) File.WriteAllLines(file, output);
else foreach (var line in output) Console.WriteLine(line);

static (DateTime First, DateTime Last) Span(string path)
{
    using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT min(ts), max(ts) FROM system_minute";
    using var r = cmd.ExecuteReader();
    if (!r.Read() || r.IsDBNull(0)) throw new Exception("No minutes recorded in this database");
    return (DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)).LocalDateTime);
}

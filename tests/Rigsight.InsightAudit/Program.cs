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

using var reader = RigsightDb.OpenReader(db) ?? throw new Exception($"Can't open {db}");
var settings = settingsFile is not null && File.Exists(settingsFile) ? SettingsStore.Load(settingsFile) : new RigsightSettings();
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

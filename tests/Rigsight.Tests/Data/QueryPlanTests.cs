using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Tests.Support;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>
/// Every statement Rigsight runs against the history tables must find its rows through a key or index, never by
/// reading a whole table: that is what keeps two years of history fast. The statements are captured as SQLite runs
/// them (parameters filled in) and each one's query plan is checked, so a dropped index or a query that stops using
/// one fails here directly, however fast this machine is.
/// </summary>
public sealed partial class QueryPlanTests
{
    // What may be read whole: SQLite's own catalogue and table info, the apps list (one row per app, read whole by design), settings
    // kept in meta, one constant row and subquery results. Queries name tables by their alias ("SCAN c"), so
    // anything else scanned counts as a history table.
    private static readonly string[] SmallTables = ["sqlite_master", "pragma_table_info", "apps", "a", "meta", "CONSTANT"];

    [GeneratedRegex(@"^(SCAN|SEARCH) (\(?[\w-]+\)?)(.*)$")]
    private static partial Regex PlanLine();

    /// <summary>Runs <paramref name="work"/> and returns every SQL statement it executed on <paramref name="db"/>, parameters expanded.</summary>
    public static List<string> Capture(RigsightDb db, Action work)
    {
        var conn = (SqliteConnection)typeof(RigsightDb).GetField("_conn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(db)!;
        var statements = new List<string>();
        SQLitePCL.raw.sqlite3_trace(conn.Handle, (SQLitePCL.strdelegate_trace)((_, sql) => statements.Add(sql)), null);
        try { work(); }
        finally { SQLitePCL.raw.sqlite3_trace(conn.Handle, (SQLitePCL.strdelegate_trace)null!, null); }
        return statements;
    }

    /// <summary>Full scans of history tables in the plans of <paramref name="statements"/>, with the statement and its plan.</summary>
    public static List<string> FullScans(string dbPath, IEnumerable<string> statements)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        var problems = new List<string>();
        foreach (var sql in statements.Distinct())
        {
            var trimmed = sql.Trim();
            if (trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("VACUUM", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("--"))
                continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + trimmed;
            var plan = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) plan.Add(r.GetString(3));
            foreach (var line in plan)
            {
                var m = PlanLine().Match(line.Trim());
                if (!m.Success || SmallTables.Contains(m.Groups[2].Value) || m.Groups[2].Value.StartsWith('(')) continue;
                // "SCAN t" reads the whole table, and so does "SCAN t USING INDEX" (every entry of the index); a
                // "SEARCH" narrows to a key range.
                if (m.Groups[1].Value == "SCAN")
                    problems.Add($"{line.Trim()}\n    in: {Regex.Replace(trimmed, @"\s+", " ")}\n    plan: {string.Join(" | ", plan)}");
            }
        }
        return problems;
    }

    private static void AssertNoFullScans(string path, List<string> statements, string what)
    {
        Assert.NotEmpty(statements);
        var problems = FullScans(path, statements);
        Assert.True(problems.Count == 0, $"{what} reads whole tables:\n" + string.Join("\n", problems));
    }

    public static TheoryData<string> Readers => new()
    {
        "GetMinutes", "GetAppHours", "GetAppTotals", "GetSystemDays", "GetSessions", "GetLongestSessions", "GetSessionStats", "GetRecentSessions",
        "GetCrashes", "GetCrashContext", "TempRange", "AverageTemps", "FindMinute", "GetAppMonths", "GetAppTime", "PeakTempsBefore", "FrontAppAt",
        "FirstTimes", "GetDriveDays",
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void A_range_read_uses_keys_and_indexes(string method)
    {
        using var db = Seeds.Open("small");
        var now = Seeds.SmallNow;
        long day = U(now.Date.AddDays(-1)), from = U(now.Date.AddDays(-10)) + 1234, to = U(now.Date.AddDays(1));
        Action run = method switch
        {
            "GetMinutes" => () => db.GetMinutes(day, day + 86400),
            "GetAppHours" => () => db.GetAppHours(from, to),
            "GetAppTotals" => () =>
            {
                db.GetAppTotals(from, to);
                db.GetAppTotals(U(2000, 1, 1), to);
                db.GetAppTotals(U(new DateTime(now.Year, now.Month, 1).AddMonths(-1)), U(new DateTime(now.Year, now.Month, 1)));
            },
            "GetSystemDays" => () => db.GetSystemDays(U(2000, 1, 1), to),
            "GetSessions" => () => db.GetSessions(from, to),
            "GetLongestSessions" => () => db.GetLongestSessions(from, to, 60, 200),
            "GetSessionStats" => () => db.GetSessionStats(from, to, 60),
            "GetRecentSessions" => () => db.GetRecentSessions(1, from, to, 60, 20),
            "GetCrashes" => () => db.GetCrashes(from, to),
            "GetCrashContext" => () => db.GetCrashContext(from, to),
            "TempRange" => () => db.TempRange(from, to),
            "AverageTemps" => () => db.AverageTemps(from, to),
            "FindMinute" => () => db.FindMinute("cpu_temp_max", 80, day, day + 86400),
            "GetAppMonths" => () => db.GetAppMonths(U(2000, 1, 1), to),
            "GetAppTime" => () => { db.GetAppTime(1, from, to, monthly: false); db.GetAppTime(1, U(2000, 1, 1), to, monthly: true); },
            "PeakTempsBefore" => () => db.PeakTempsBefore(day + 43200, 5),
            "FrontAppAt" => () => db.FrontAppAt(day + 43200),
            "FirstTimes" => () => { db.FirstDataTime(); db.FirstMinuteTime(); db.FirstCrashTime(); },
            "GetDriveDays" => () => db.GetDriveDays(from),
            _ => throw new ArgumentException(method),
        };
        AssertNoFullScans(Seeds.Small, Capture(db, run), method);
    }

    public static TheoryData<ReportRange> Ranges => [.. Enum.GetValues<ReportRange>()];

    [Theory]
    [MemberData(nameof(Ranges))]
    public void Building_a_report_reads_no_whole_table(ReportRange range)
    {
        using var db = Seeds.Open("small");
        var statements = Capture(db, () => ReportBuilder.Build(db, range, Seeds.SmallNow, Settings()));
        AssertNoFullScans(Seeds.Small, statements, $"The {range} report");
    }

    [Fact]
    public void Building_the_apps_page_totals_reads_no_whole_table()
    {
        using var db = Seeds.Open("small");
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var statements = Capture(db, () => ReportBuilder.BuildAppTotals(db, new DateTime(2000, 1, 1), Seeds.SmallNow.Date.AddDays(1), apps, Settings()));
        AssertNoFullScans(Seeds.Small, statements, "The Apps page");
    }

    [Fact]
    public void Recording_and_cleaning_up_read_no_whole_table()
    {
        var path = Path.Combine(TestEnvironment.NewFolder("plan-writes"), "rigsight.db");
        File.Copy(Seeds.Small, path);
        using var db = RigsightDb.OpenWriter(path);
        long now = U(TimeUtil.LocalMinuteStart(Seeds.SmallNow));
        var statements = Capture(db, () =>
        {
            db.WriteMinute(Minute(now, app: 1));
            db.AddAppHour(Hour(U(TimeUtil.LocalHourStart(Seeds.SmallNow)), 1));
            db.InsertSession(Session(1, now - 3600, now));
            db.InsertCrashes([new CrashEvent { Ts = now, Kind = CrashKind.AppHang, AppExe = "x.exe" }]);
            db.UpsertDriveDay(new DriveDay { Day = U(Seeds.SmallNow.Date), Drive = "C:\\", UsedGb = 1, TotalGb = 2 });
            db.UpsertApp("new.exe", "New", null, Rigsight.Core.Settings.AppCategory.Other);
            db.Prune(U(Seeds.SmallNow.Date.AddDays(-7)) + 4321);
        });
        AssertNoFullScans(path, statements, "Recording");
    }

    [Fact]
    public void The_plan_check_catches_a_missing_index()
    {
        var path = Path.Combine(TestEnvironment.NewFolder("plan-noindex"), "rigsight.db");
        File.Copy(Seeds.Small, path);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP INDEX ix_crashes_ts";
            cmd.ExecuteNonQuery();
        }
        using var db = RigsightDb.OpenReader(path)!;
        var statements = Capture(db, () => db.GetCrashes(0, U(2100, 1, 1)));
        Assert.Contains(FullScans(path, statements), p => p.StartsWith("SCAN crashes"));

        // Under an alias too, as the crash-context query names it.
        statements = Capture(db, () => db.GetCrashContext(0, U(2100, 1, 1)));
        Assert.Contains(FullScans(path, statements), p => p.StartsWith("SCAN c"));
    }
}

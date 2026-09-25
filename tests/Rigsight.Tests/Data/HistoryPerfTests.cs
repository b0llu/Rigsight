using System.Diagnostics;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>Two years of a PC that's on all day with 300 apps (<see cref="SeedProfile.Heavy"/>), generated once for the perf tests.</summary>
public sealed class HeavyHistory
{
    public string Path { get; }
    public DateTime Now { get; }
    public TimeSpan GenerationTime { get; }

    public HeavyHistory()
    {
        Path = System.IO.Path.Combine(TestEnvironment.NewFolder("heavy"), "rigsight.db");
        Now = DateTime.Now;
        var sw = Stopwatch.StartNew();
        SeedData.Generate(Path, SeedProfile.Heavy, Now);
        GenerationTime = sw.Elapsed;
    }
}

/// <summary>Timed alone, after the tests that run side by side, so other work doesn't skew the numbers.</summary>
[CollectionDefinition("Perf", DisableParallelization = true)]
public sealed class PerfCollection : ICollectionFixture<HeavyHistory>;

/// <summary>
/// What the pages run, timed on the heavy history: each must stay within a budget of about 3× its median on the
/// reference PC (Ryzen 7 5700X3D, Debug build, measured numbers next to each budget). Allocation budgets catch a
/// query that starts pulling far more rows into memory even where a fast disk hides it.
/// </summary>
[Collection("Perf")]
[Trait("Category", "Perf")]
public sealed class HistoryPerfTests(HeavyHistory heavy)
{
    private const int Runs = 5;

    /// <summary>Median time and allocations of <paramref name="work"/> over a few runs, after one warm-up.</summary>
    private static (double Ms, long Bytes) Measure(Action work)
    {
        work();
        var times = new List<double>();
        var bytes = new List<long>();
        for (int i = 0; i < Runs; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            work();
            times.Add(sw.Elapsed.TotalMilliseconds);
            bytes.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return (times.Order().ElementAt(Runs / 2), bytes.Order().ElementAt(Runs / 2));
    }

    private static void Within(string what, (double Ms, long Bytes) m, double budgetMs, double budgetMb)
    {
        Record($"{what,-40} {m.Ms,9:0.0} ms {m.Bytes / 1048576.0,9:0.00} MB   (budget {budgetMs} ms, {budgetMb} MB)");
        Assert.True(m.Ms <= budgetMs, $"{what} took {m.Ms:0.0} ms (median of {Runs}); its budget is {budgetMs} ms");
        Assert.True(m.Bytes <= budgetMb * 1048576, $"{what} allocated {m.Bytes / 1048576.0:0.00} MB; its budget is {budgetMb} MB");
    }

    private static readonly Lock RecordGate = new();

    /// <summary>Kept in perf.txt in the run's folder, for comparing runs.</summary>
    private static void Record(string line)
    {
        lock (RecordGate) File.AppendAllText(System.IO.Path.Combine(TestEnvironment.DataDir, "perf.txt"), line + Environment.NewLine);
    }

    private RigsightDb Open() => RigsightDb.OpenReader(heavy.Path)!;

    private static readonly RigsightSettings Defaults = Settings();

    // Budgets: ~3× the median measured on the reference PC (in the comment), rounded up.

    [Theory]
    [InlineData(ReportRange.Day, 100, 9.5)]   // measured 28–37 ms, 3.0 MB
    [InlineData(ReportRange.Week, 300, 18.5)] // measured 79–117 ms, 6.1 MB
    [InlineData(ReportRange.Month, 630, 62)]  // measured 190–209 ms, 20.4 MB
    [InlineData(ReportRange.Year, 190, 12.5)] // measured 62–64 ms, 4.2 MB
    [InlineData(ReportRange.All, 75, 5.5)]    // measured 20–28 ms, 1.7 MB
    public void A_report_builds_within_budget(ReportRange range, double ms, double mb)
    {
        using var db = Open();
        Within($"Build {range}", Measure(() => ReportBuilder.Build(db, range, heavy.Now, Defaults)), ms, mb);
    }

    [Fact]
    public void Yesterdays_report_builds_within_budget()
    {
        using var db = Open();
        Within("Build Day (yesterday)", Measure(() => ReportBuilder.Build(db, ReportRange.Day, heavy.Now.AddDays(-1), Defaults)), 110, 11); // measured 27–39 ms, 3.5 MB
    }

    [Fact]
    public void The_apps_page_all_time_totals_build_within_budget()
    {
        using var db = Open();
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        Within("BuildAppTotals all time", Measure(() => ReportBuilder.BuildAppTotals(db, new DateTime(2000, 1, 1), heavy.Now.Date.AddDays(1), apps, Defaults)), 38, 1); // measured 10–13 ms, 0.29 MB
    }

    public static TheoryData<string, double, double> Queries => new()
    {
        // Sub-millisecond queries get a 5 ms / 0.25 MB floor: below that, timer and GC noise dominate.
        { "GetAppTotals all time", 30, 0.3 },         // measured 6.7–11.7 ms, 0.09 MB
        { "GetAppTotals 45 days", 30, 0.25 },         // measured 9.2–9.4 ms, 0.04 MB
        { "GetSystemDays 2 years", 8, 0.5 },          // measured 1.6–2.7 ms, 0.16 MB
        { "GetCrashContext 2 years", 60, 1.2 },       // measured 17.7–27.6 ms, 0.37 MB
        { "GetCrashes 2 years", 12, 2.1 },            // measured 2.9–5.4 ms, 0.68 MB
        { "GetSessions 2 years", 45, 3.4 },           // measured 10.4–15.9 ms, 1.12 MB
        { "GetSessions 30 days", 5, 0.25 },           // measured 0.4–0.7 ms, 0.05 MB
        { "GetLongestSessions 2 years", 8, 0.25 },    // measured 1.7–3.0 ms, 0.02 MB
        { "GetSessionStats 2 years", 12, 0.25 },      // measured 2.7–4.7 ms, 0.03 MB
        { "GetMinutes one day", 12, 0.75 },           // measured 2.2–3.8 ms, 0.24 MB
        { "GetMinutes last 24 h", 12, 0.8 },          // measured 2.4–4.3 ms, 0.26 MB
        { "TempRange one year", 330, 0.25 },          // measured 97–127 ms, 0.00 MB
        { "GetAppMonths 2 years", 5, 0.6 },           // measured 0.9–1.6 ms, 0.19 MB
        { "GetAppTime one app 2 years monthly", 5, 0.25 }, // measured 0.1–0.2 ms, 0.00 MB
        { "FirstDataTime", 5, 0.25 },                 // measured 0.0–0.1 ms, 0.00 MB
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void A_query_runs_within_budget(string query, double ms, double mb)
    {
        using var db = Open();
        var today = heavy.Now.Date;
        long now = U(heavy.Now), end = U(today.AddDays(1)), twoYears = U(today.AddYears(-2).AddDays(-5)), year = U(today.AddYears(-1));
        Action work = query switch
        {
            "GetAppTotals all time" => () => db.GetAppTotals(U(2000, 1, 1), end),
            "GetAppTotals 45 days" => () => db.GetAppTotals(U(today.AddDays(-45)), end),
            "GetSystemDays 2 years" => () => db.GetSystemDays(twoYears, end),
            "GetCrashContext 2 years" => () => db.GetCrashContext(twoYears, end),
            "GetCrashes 2 years" => () => db.GetCrashes(twoYears, end),
            "GetSessions 2 years" => () => db.GetSessions(twoYears, end),
            "GetSessions 30 days" => () => db.GetSessions(U(today.AddDays(-30)), end),
            "GetLongestSessions 2 years" => () => db.GetLongestSessions(twoYears, end, 60, 200),
            "GetSessionStats 2 years" => () => db.GetSessionStats(twoYears, end, 60),
            "GetMinutes one day" => () => db.GetMinutes(U(today.AddDays(-1)), U(today)),
            "GetMinutes last 24 h" => () => db.GetMinutes(now - 86400, now + 60),
            "TempRange one year" => () => db.TempRange(year, end),
            "GetAppMonths 2 years" => () => db.GetAppMonths(twoYears, end),
            "GetAppTime one app 2 years monthly" => () => db.GetAppTime(1, twoYears, end, monthly: true),
            "FirstDataTime" => () => { db.FirstDataTime(); db.FirstMinuteTime(); db.FirstCrashTime(); },
            _ => throw new ArgumentException(query),
        };
        Within(query, Measure(work), ms, mb);
    }

    [Fact]
    public void Opening_the_database_is_quick()
    {
        Within("OpenReader + dispose", Measure(() => RigsightDb.OpenReader(heavy.Path)!.Dispose()), 5, 0.25); // measured 0.2 ms, 0.00 MB
    }

    [Fact]
    public void Recording_a_minute_stays_quick_on_a_long_history()
    {
        // What the agent does every minute: the minute (and its day recomputed), a few apps' hours (and their
        // months), in one transaction. Written far in the future on a copy, so the shared history is untouched.
        var path = System.IO.Path.Combine(TestEnvironment.NewFolder("heavy-write"), "rigsight.db");
        File.Copy(heavy.Path, path);
        using var db = RigsightDb.OpenWriter(path);
        long ts = U(2099, 6, 15, 12);
        Within("Record a minute", Measure(() =>
        {
            using var tx = db.BeginTransaction();
            ts += 60;
            db.WriteMinute(Minute(ts, app: 1));
            for (int app = 1; app <= 5; app++) db.AddAppHour(Hour(ts - ts % 3600, app, fg: 12));
            tx.Commit();
        }), 5, 0.5); // measured 1.3 ms, 0.16 MB
    }

    [Fact]
    public void The_heavy_history_has_a_sensible_size_and_clearing_it_gives_the_space_back()
    {
        var path = System.IO.Path.Combine(TestEnvironment.NewFolder("heavy-clear"), "rigsight.db");
        File.Copy(heavy.Path, path);
        long size = new FileInfo(path).Length;
        Record($"{"Heavy history file",-40} {size / 1048576.0,9:0.0} MB   (generated in {heavy.GenerationTime.TotalSeconds:0.0} s)");
        // measured 79.1 MB: a new column or index that grows every row shows up here.
        Assert.InRange(size / 1048576.0, 40, 120);

        using (var db = RigsightDb.OpenWriter(path))
        {
            var sw = Stopwatch.StartNew();
            db.ClearHistory();
            Record($"{"ClearHistory (with VACUUM)",-40} {sw.Elapsed.TotalMilliseconds,9:0.0} ms");
            Assert.True(sw.Elapsed.TotalMilliseconds < 350, $"clearing took {sw.Elapsed.TotalMilliseconds:0} ms"); // measured 98–107 ms
        }
        long cleared = new FileInfo(path).Length;
        Record($"{"Cleared file",-40} {cleared / 1024.0,9:0.0} KB");
        Assert.True(cleared < 360 * 1024, $"the cleared database is still {cleared / 1024} KB"); // measured 120 KB
        using var check = TestDb.At(path);
        Assert.Equal(300, check.Count("apps"));
    }
}

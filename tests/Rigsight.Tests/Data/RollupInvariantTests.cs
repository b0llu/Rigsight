using Rigsight.Core.Data;
using Rigsight.Core.Stability;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>
/// The monthly (app_month) and daily (system_day) totals must always equal the raw rows added up, whatever the
/// agent does: writes in any order, a minute written twice, missing readings, clean-ups that cut a month or a day in
/// two, clearing everything. Long reports and the Apps page read only the totals, so a drift here shows wrong numbers.
/// </summary>
public sealed class RollupInvariantTests
{
    private static readonly DateTime WindowStart = new(2024, 11, 20), WindowEnd = new(2025, 3, 10);

    public static TheoryData<int> Seeds => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_writes_prunes_and_clears_keep_every_rollup_exact(int seed)
    {
        using var t = new TestDb("prop");
        var rnd = new Random(seed);
        var written = new List<long>();
        long windowFrom = U(WindowStart), windowTo = U(WindowEnd);
        int minutesSpan = (int)((windowTo - windowFrom) / 60);

        for (int batch = 0; batch < 25; batch++)
        {
            bool inTx = rnd.Next(3) == 0, rollback = inTx && rnd.Next(4) == 0;
            var tx = inTx ? t.Db.BeginTransaction() : null;
            var log = new List<string>();
            for (int op = 0; op < 40; op++)
            {
                int kind = rnd.Next(100);
                if (kind < 40)
                {
                    long ts = written.Count > 0 && rnd.Next(8) == 0 ? written[rnd.Next(written.Count)] // the same minute again
                        : windowFrom + rnd.Next(minutesSpan) * 60L;
                    t.Db.WriteMinute(RandomMinute(rnd, ts));
                    written.Add(ts);
                    log.Add($"minute {L(ts):MM-dd HH:mm}");
                }
                else if (kind < 75)
                {
                    var hour = WindowStart.AddHours(rnd.Next((int)(WindowEnd - WindowStart).TotalHours));
                    t.Db.AddAppHour(RandomHour(rnd, U(hour)));
                    log.Add($"hour {hour:MM-dd HH}");
                }
                else if (kind < 88)
                {
                    long start = windowFrom + rnd.Next(minutesSpan) * 60L;
                    long length = rnd.Next(10) == 0 ? rnd.Next(5 * 86400) : rnd.Next(4 * 3600);
                    t.Db.InsertSession(Session(1 + rnd.Next(6), start, start + length, active: length * rnd.NextDouble()));
                    log.Add($"session {L(start):MM-dd HH:mm}+{length}");
                }
                else if (kind < 94)
                {
                    t.Db.InsertCrashes([new CrashEvent { Ts = windowFrom + rnd.Next(minutesSpan) * 60L, Kind = CrashKind.AppHang, AppExe = "a.exe" }]);
                }
                else if (kind < 99 && !inTx)
                {
                    long cutoff = PickCutoff(rnd);
                    t.Db.Prune(cutoff);
                    log.Add($"prune {L(cutoff):yyyy-MM-dd HH:mm:ss}");
                    Assert.Equal(0, t.Count("system_minute", $"ts < {cutoff}"));
                    Assert.Equal(0, t.Count("app_hour", $"ts < {cutoff}"));
                }
                else if (!inTx && rnd.Next(3) == 0)
                {
                    t.Db.ClearHistory();
                    log.Add("clear");
                }
            }
            if (rollback) tx!.Rollback(); else tx?.Commit();
            tx?.Dispose();

            string when = $"seed {seed}, batch {batch}{(rollback ? " (rolled back)" : "")}: {string.Join(", ", log.TakeLast(6))}";
            Rollups.AssertAll(t, when);
            for (int i = 0; i < 4; i++)
            {
                long a = windowFrom - 86400 * 3 + (long)(rnd.NextDouble() * (windowTo - windowFrom + 86400 * 6));
                long b = windowFrom - 86400 * 3 + (long)(rnd.NextDouble() * (windowTo - windowFrom + 86400 * 6));
                AppTotalsTests.AssertTotalsMatchHours(t.Db, Math.Min(a, b), Math.Max(a, b), when);
            }
        }
    }

    private static long PickCutoff(Random rnd)
    {
        var day = WindowStart.AddDays(rnd.Next((int)(WindowEnd - WindowStart).TotalDays));
        return rnd.Next(7) switch
        {
            0 => U(day),                                          // a local midnight
            1 => U(new DateTime(day.Year, day.Month, 1)),         // a month start
            2 => U(new DateTime(day.Year, day.Month, 1)) - 1,     // the last second of a month
            3 => U(day) + 1,                                      // just after midnight
            4 => U(WindowStart) - 86400,                          // before everything (nothing to do)
            _ => U(day.AddSeconds(rnd.Next(86400))),              // anywhere in a day
        };
    }

    private static double? Maybe(Random rnd, double v) => rnd.Next(5) == 0 ? null : Math.Round(v, 2);

    private static SystemMinute RandomMinute(Random rnd, long ts)
    {
        int active = rnd.Next(4) switch { 0 => 0, 1 => rnd.Next(61), _ => 60 };
        return new SystemMinute
        {
            Ts = ts, CpuTemp = Maybe(rnd, 30 + rnd.NextDouble() * 60), CpuTempMax = Maybe(rnd, 30 + rnd.NextDouble() * 70),
            GpuTemp = Maybe(rnd, 30 + rnd.NextDouble() * 60), GpuTempMax = Maybe(rnd, 30 + rnd.NextDouble() * 60),
            GpuHotMax = Maybe(rnd, 40 + rnd.NextDouble() * 70), GpuMemMax = Maybe(rnd, 40 + rnd.NextDouble() * 60),
            CpuLoad = Maybe(rnd, rnd.NextDouble() * 100), GpuLoad = Maybe(rnd, rnd.NextDouble() * 100),
            CpuPower = Maybe(rnd, rnd.NextDouble() * 200), GpuPower = Maybe(rnd, rnd.NextDouble() * 450),
            CpuVoltMax = Maybe(rnd, 0.8 + rnd.NextDouble() * 0.6), GpuVoltMax = Maybe(rnd, 0.7 + rnd.NextDouble() * 0.4),
            RamUsed = Maybe(rnd, rnd.NextDouble() * 32), FgApp = rnd.Next(4) == 0 ? null : 1 + rnd.Next(6),
            ActiveSec = active, IdleSec = rnd.Next(61 - active),
        };
    }

    private static AppHour RandomHour(Random rnd, long ts)
    {
        bool empty = rnd.Next(10) == 0; // an all-zero delta, as when an app is open but nothing was sampled
        int n = empty ? 0 : rnd.Next(1, 720);
        return new AppHour
        {
            Ts = ts, AppId = 1 + rnd.Next(6),
            FgSec = empty ? 0 : rnd.Next(3600), IdleSec = empty ? 0 : rnd.Next(600), BgSec = empty ? 0 : rnd.Next(3600), MinSec = empty ? 0 : rnd.Next(3600),
            CpuSum = n * rnd.NextDouble() * 30, CpuN = n, CpuMax = empty ? null : Maybe(rnd, rnd.NextDouble() * 100),
            MemSum = n * rnd.NextDouble() * 3000, MemN = n, MemMax = empty ? null : Maybe(rnd, rnd.NextDouble() * 9000),
            CpuTempSum = n * 55.5, CpuTempN = n, CpuTempMax = empty ? null : Maybe(rnd, 40 + rnd.NextDouble() * 60),
            GpuTempSum = n * 48.25, GpuTempN = n, GpuTempMax = empty ? null : Maybe(rnd, 40 + rnd.NextDouble() * 50),
            GpuHotMax = empty ? null : Maybe(rnd, 50 + rnd.NextDouble() * 60),
            CpuPowerMax = Maybe(rnd, rnd.NextDouble() * 200), GpuPowerMax = Maybe(rnd, rnd.NextDouble() * 450),
            CpuVoltMax = Maybe(rnd, 1 + rnd.NextDouble() * 0.5), GpuVoltMax = Maybe(rnd, 0.7 + rnd.NextDouble() * 0.4),
            GpuLoadSum = n * rnd.NextDouble() * 100, GpuLoadN = n,
        };
    }

    // ── The same rules, one case at a time ──

    [Fact]
    public void Adding_to_an_hour_twice_sums_the_times_and_keeps_the_higher_peak()
    {
        using var t = new TestDb();
        long h = U(2025, 1, 15, 10);
        t.Db.AddAppHour(Hour(h, 1, fg: 100, cpuTemp: 60));
        t.Db.AddAppHour(Hour(h, 1, fg: 50, cpuTemp: 50));
        var row = Assert.Single(t.Db.GetAppHours(h, h + 3600));
        Assert.Equal(150, row.FgSec);
        Assert.Equal(63, row.CpuTempMax);
        Assert.Equal(20, row.CpuTempN);
        Assert.Equal(1100, row.CpuTempSum);
        Rollups.AssertAll(t);
    }

    [Theory]
    [InlineData(null, 70.0, 70.0)]
    [InlineData(70.0, null, 70.0)]
    [InlineData(null, null, null)]
    [InlineData(60.0, 70.0, 70.0)]
    [InlineData(70.0, 60.0, 70.0)]
    public void A_missing_peak_never_wipes_a_known_one(double? first, double? second, double? expected)
    {
        using var t = new TestDb();
        long h = U(2025, 1, 15, 10);
        t.Db.AddAppHour(new AppHour { Ts = h, AppId = 1, FgSec = 1, CpuTempMax = first, GpuPowerMax = first, MemMax = first });
        t.Db.AddAppHour(new AppHour { Ts = h, AppId = 1, FgSec = 1, CpuTempMax = second, GpuPowerMax = second, MemMax = second });
        var hour = Assert.Single(t.Db.GetAppHours(h, h + 3600));
        var total = Assert.Single(t.Db.GetAppTotals(U(2025, 1, 1), U(2025, 2, 1)));
        foreach (var row in new[] { hour, total })
        {
            Assert.Equal(expected, row.CpuTempMax);
            Assert.Equal(expected, row.GpuPowerMax);
            Assert.Equal(expected, row.MemMax);
        }
        Rollups.AssertAll(t);
    }

    [Fact]
    public void Hours_of_one_month_add_up_into_one_monthly_row_per_app()
    {
        using var t = new TestDb();
        t.Db.AddAppHour(Hour(U(2025, 1, 1, 0), 1, fg: 10));
        t.Db.AddAppHour(Hour(U(2025, 1, 15, 12), 1, fg: 20));
        t.Db.AddAppHour(Hour(U(2025, 1, 31, 23), 1, fg: 30));
        t.Db.AddAppHour(Hour(U(2025, 1, 31, 23), 2, fg: 5));
        t.Db.AddAppHour(Hour(U(2025, 2, 1, 0), 1, fg: 40));
        var months = t.Rows("SELECT month, app_id, fg_sec FROM app_month ORDER BY month, app_id")
            .Select(r => ((long)r["month"]!, (long)r["app_id"]!, (double)r["fg_sec"]!)).ToList();
        Assert.Equal([(U(2025, 1, 1), 1L, 60.0), (U(2025, 1, 1), 2L, 5.0), (U(2025, 2, 1), 1L, 40.0)], months);
    }

    [Fact]
    public void An_empty_delta_still_makes_the_hour_and_month_rows_and_they_agree()
    {
        using var t = new TestDb();
        t.Db.AddAppHour(new AppHour { Ts = U(2025, 1, 5, 9), AppId = 3 });
        Assert.Equal(1, t.Count("app_hour"));
        Assert.Equal(1, t.Count("app_month"));
        Rollups.AssertAll(t);
    }

    [Fact]
    public void Writing_a_minute_again_replaces_it_in_the_day_instead_of_adding_it_twice()
    {
        using var t = new TestDb();
        long ts = U(2025, 1, 10, 12, 30);
        t.Db.WriteMinute(Minute(ts, cpu: 50, cpuMax: 90, active: 60));
        t.Db.WriteMinute(Minute(ts, cpu: 40, cpuMax: 45, active: 30, idle: 30));
        var day = Assert.Single(t.Db.GetSystemDays(U(2025, 1, 10), U(2025, 1, 11))!);
        Assert.Equal(1, day.Minutes);
        Assert.Equal(30, day.ActiveSec);
        Assert.Equal(30, day.IdleSec);
        Assert.Equal(40, day.CpuTempSum);
        Assert.Equal(45, day.CpuTempMax); // the old, higher peak is gone with the old reading
        Rollups.AssertAll(t);
    }

    [Fact]
    public void Missing_readings_are_left_out_of_the_days_averages_and_highs()
    {
        using var t = new TestDb();
        long day = U(2025, 1, 10);
        t.Db.WriteMinute(new SystemMinute { Ts = day + 3600, ActiveSec = 60 });
        t.Db.WriteMinute(new SystemMinute { Ts = day + 7200, CpuTemp = 50, CpuTempMax = 55, ActiveSec = 60 });
        t.Db.WriteMinute(new SystemMinute { Ts = day + 7260, IdleSec = 60 });
        var d = Assert.Single(t.Db.GetSystemDays(day, day + 86400)!);
        Assert.Equal(3, d.Minutes);
        Assert.Equal(1, d.CpuTempN);
        Assert.Equal(50, d.CpuTempSum);
        Assert.Equal(55, d.CpuTempMax);
        Assert.Equal(0, d.GpuTempN);
        Assert.Equal(0, d.GpuTempSum);
        Assert.Null(d.GpuTempMax);
        Assert.Null(d.GpuPowerMax);
        Assert.Equal(120, d.ActiveSec);
        Assert.Equal(60, d.IdleSec);
    }

    [Fact]
    public void Minutes_written_out_of_order_add_up_the_same()
    {
        using var a = new TestDb();
        using var b = new TestDb();
        var minutes = Enumerable.Range(0, 200).Select(i => Minute(U(2025, 1, 31, 20) + i * 60L * 3, cpu: 40 + i % 30, cpuPower: i)).ToList();
        foreach (var m in minutes) a.Db.WriteMinute(m);
        foreach (var m in Enumerable.Reverse(minutes)) b.Db.WriteMinute(m);
        MigrationTests.AssertSameRows(a, b, "SELECT * FROM system_day ORDER BY day");
        Rollups.AssertAll(b);
    }

    [Fact]
    public void A_minute_just_before_midnight_and_one_at_midnight_land_on_their_own_days()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(U(2025, 1, 31, 23, 59)));
        t.Db.WriteMinute(Minute(U(2025, 2, 1, 0, 0)));
        var days = t.Db.GetSystemDays(U(2025, 1, 1), U(2025, 3, 1))!;
        Assert.Equal([U(2025, 1, 31), U(2025, 2, 1)], days.Select(d => d.Day));
        Assert.All(days, d => Assert.Equal(1, d.Minutes));
    }

    [Fact]
    public void Pruning_mid_day_recomputes_that_day_from_what_is_left()
    {
        using var t = new TestDb();
        for (int h = 0; h < 24; h++) t.Db.WriteMinute(Minute(U(2025, 1, 10, h, 15), cpu: h));
        t.Db.WriteMinute(Minute(U(2025, 1, 9, 12)));
        t.Db.WriteMinute(Minute(U(2025, 1, 11, 12)));
        t.Db.Prune(U(2025, 1, 10, 12));
        var days = t.Db.GetSystemDays(U(2025, 1, 1), U(2025, 2, 1))!;
        Assert.Equal([U(2025, 1, 10), U(2025, 1, 11)], days.Select(d => d.Day));
        Assert.Equal(12, days[0].Minutes);
        Assert.Equal(Enumerable.Range(12, 12).Sum(), days[0].CpuTempSum);
        Rollups.AssertAll(t);
    }

    [Fact]
    public void Pruning_mid_month_recomputes_that_month_from_what_is_left()
    {
        using var t = new TestDb();
        for (int d = 1; d <= 31; d++) t.Db.AddAppHour(Hour(U(2025, 1, d, 10), 1, fg: d));
        t.Db.AddAppHour(Hour(U(2024, 12, 20, 10), 1, fg: 1000));
        t.Db.AddAppHour(Hour(U(2025, 2, 3, 10), 1, fg: 2000));
        t.Db.Prune(U(2025, 1, 16, 10) + 1);
        var totals = Assert.Single(t.Db.GetAppTotals(U(2024, 1, 1), U(2026, 1, 1)));
        Assert.Equal(Enumerable.Range(17, 15).Sum() + 2000, totals.FgSec);
        Assert.Equal(2, t.Count("app_month"));
        Rollups.AssertAll(t);
    }

    [Fact]
    public void A_row_exactly_at_the_cutoff_is_kept()
    {
        using var t = new TestDb();
        long cutoff = U(2025, 1, 10, 12);
        t.Db.WriteMinute(Minute(cutoff - 60));
        t.Db.WriteMinute(Minute(cutoff));
        t.Db.AddAppHour(Hour(cutoff - 3600, 1));
        t.Db.AddAppHour(Hour(cutoff, 1));
        t.Db.InsertSession(Session(1, cutoff - 1, cutoff + 600));
        t.Db.InsertSession(Session(1, cutoff, cutoff + 600));
        t.Db.UpsertDriveDay(new DriveDay { Day = cutoff - 86400, Drive = "C:\\", UsedGb = 1, TotalGb = 2 });
        t.Db.UpsertDriveDay(new DriveDay { Day = cutoff, Drive = "C:\\", UsedGb = 1, TotalGb = 2 });
        t.Db.InsertCrashes([new CrashEvent { Ts = cutoff - 1, Kind = CrashKind.AppHang }, new CrashEvent { Ts = cutoff, Kind = CrashKind.AppHang }]);
        t.Db.Prune(cutoff);
        foreach (var (table, col) in new[] { ("system_minute", "ts"), ("app_hour", "ts"), ("sessions", "start"), ("drive_day", "day"), ("crashes", "ts") })
            Assert.Equal([cutoff], t.Rows($"SELECT {col} AS v FROM {table}").Select(r => (long)r["v"]!));
        Rollups.AssertAll(t);
    }

    [Fact]
    public void Pruning_drops_a_session_that_started_before_the_cutoff_even_if_it_ended_after()
    {
        using var t = new TestDb();
        long cutoff = U(2025, 1, 10);
        t.Db.InsertSession(Session(1, cutoff - 3600, cutoff + 3600));
        t.Db.Prune(cutoff);
        Assert.Equal(0, t.Count("sessions"));
    }

    [Fact]
    public void Pruning_keeps_apps_and_settings_kept_in_meta()
    {
        using var t = new TestDb();
        t.Db.UpsertApp("a.exe", "A", null, Rigsight.Core.Settings.AppCategory.Game);
        t.Db.SetMeta("extremes", "x");
        t.Db.InsertSession(Session(1, U(2025, 1, 1), U(2025, 1, 3)));
        t.Db.Prune(U(2026, 1, 1));
        Assert.Equal(1, t.Count("apps"));
        Assert.Equal("x", t.Db.GetMeta("extremes"));
        Assert.Equal((2 * 86400L).ToString(), t.Db.GetMeta("max_session_sec")); // still an upper bound: nothing is longer
    }

    [Fact]
    public void Pruning_an_empty_database_is_harmless()
    {
        using var t = new TestDb();
        t.Db.Prune(U(2025, 1, 1));
        t.Db.Prune(0);
        t.Db.Prune(long.MaxValue / 2);
        Rollups.AssertAll(t);
    }

    [Fact]
    public void Clearing_history_empties_every_history_table_but_keeps_apps_and_meta()
    {
        using var t = new TestDb();
        History.Sample().WriteFresh(t.Db);
        t.Db.UpsertDriveDay(new DriveDay { Day = U(2025, 1, 1), Drive = "C:\\", UsedGb = 1, TotalGb = 2 });
        t.Db.SetMeta("extremes", "x");
        t.Db.ClearHistory();
        foreach (var table in new[] { "system_minute", "system_day", "app_hour", "app_month", "sessions", "drive_day", "crashes" })
            Assert.Equal(0, t.Count(table));
        Assert.Equal(4, t.Count("apps"));
        Assert.Equal("x", t.Db.GetMeta("extremes"));
        Assert.Null(t.Db.FirstDataTime());
        Rollups.AssertAll(t);

        // And recording carries on normally.
        t.Db.WriteMinute(Minute(U(2025, 5, 1, 10)));
        t.Db.AddAppHour(Hour(U(2025, 5, 1, 10), 1));
        Rollups.AssertAll(t);
    }

    [Fact]
    public void The_session_bound_only_grows_and_survives_reopening()
    {
        using var t = new TestDb();
        t.Db.InsertSession(Session(1, 1000, 4600));
        Assert.Equal("3600", t.Db.GetMeta("max_session_sec"));
        t.Db.InsertSession(Session(1, 10000, 10060));
        Assert.Equal("3600", t.Db.GetMeta("max_session_sec"));
        t.Db.InsertSession(Session(1, 20000, 30000));
        Assert.Equal("10000", t.Db.GetMeta("max_session_sec"));
        t.OpenWriter();
        t.Db.InsertSession(Session(1, 40000, 45000));
        Assert.Equal("10000", t.Db.GetMeta("max_session_sec"));
        Rollups.AssertMaxSession(t);
    }

    [Fact]
    public void A_rolled_back_session_does_not_leave_the_stored_bound_short_of_a_later_one()
    {
        // The agent writes each minute's rows and the sessions it closes in one transaction; if anything in it
        // fails, all of it is rolled back, including the new bound. A later, shorter session must still raise it.
        using var t = new TestDb();
        using (var tx = t.Db.BeginTransaction())
        {
            t.Db.InsertSession(Session(1, 0, 10_000));
            tx.Rollback();
        }
        t.Db.InsertSession(Session(1, 20_000, 25_000));
        Rollups.AssertMaxSession(t);
        // After a restart the stored bound is all there is: the session must still be found from a range inside it.
        t.OpenWriter();
        Assert.Contains(t.Db.GetSessions(24_000, 24_001), s => s.Start == 20_000);
    }

    [Fact]
    public void A_session_bound_lost_from_meta_is_worked_out_again_on_opening()
    {
        using var t = new TestDb();
        t.Db.InsertSession(Session(1, 1000, 8200));
        t.Db.Dispose();
        t.Exec("DELETE FROM meta WHERE key = 'max_session_sec'");
        t.OpenWriter();
        Assert.Equal("7200", t.Db.GetMeta("max_session_sec"));
    }
}

using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>Databases from earlier versions: the agent brings them up to date, and the app can read them before it does.</summary>
public sealed class MigrationTests
{
    public static TheoryData<string> Versions => Legacy.Versions;

    private static (TestDb Migrated, TestDb Fresh, History History) MigrateAndWriteFresh(string version)
    {
        var history = History.Sample();
        var migrated = TestDb.At(Legacy.Create(version, history));
        migrated.OpenWriter();
        var fresh = new TestDb("fresh");
        history.WriteFresh(fresh.Db);
        return (migrated, fresh, history);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void An_old_database_gets_every_table_index_and_column(string version)
    {
        using var old = TestDb.At(Legacy.Create(version, History.Sample()));
        old.OpenWriter();
        using var fresh = new TestDb();
        Assert.Equal(fresh.Shape(), old.Shape());
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void An_empty_old_database_migrates(string version)
    {
        using var old = TestDb.At(Legacy.Create(version, null));
        old.OpenWriter();
        using var fresh = new TestDb();
        Assert.Equal(fresh.Shape(), old.Shape());
        Assert.Equal("0", old.Db.GetMeta("max_session_sec"));
        Assert.Equal(0, old.Count("app_month"));
        Assert.Equal(0, old.Count("system_day"));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_backfilled_rollups_hold_the_invariants(string version)
    {
        var (migrated, fresh, _) = MigrateAndWriteFresh(version);
        using (migrated) using (fresh)
        {
            Assert.True(migrated.Count("app_month") > 0);
            Assert.True(migrated.Count("system_day") > 0);
            Rollups.AssertAll(migrated, $"migrated from {version}");
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_backfilled_monthly_totals_equal_what_the_agent_writes_now(string version)
    {
        var (migrated, fresh, _) = MigrateAndWriteFresh(version);
        using (migrated) using (fresh)
            AssertSameRows(fresh, migrated, "SELECT * FROM app_month ORDER BY month, app_id");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_backfilled_daily_totals_equal_what_the_agent_writes_now(string version)
    {
        var (migrated, fresh, _) = MigrateAndWriteFresh(version);
        using (migrated) using (fresh)
            AssertSameRows(fresh, migrated, "SELECT * FROM system_day ORDER BY day");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_session_bound_is_the_longest_stored_session(string version)
    {
        var (migrated, fresh, history) = MigrateAndWriteFresh(version);
        using (migrated) using (fresh)
        {
            long longest = history.Sessions.Max(s => s.End - s.Start);
            Assert.Equal(longest.ToString(), migrated.Db.GetMeta("max_session_sec"));
            Assert.Equal(longest.ToString(), fresh.Db.GetMeta("max_session_sec"));
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Every_raw_row_survives_the_migration(string version)
    {
        var (migrated, fresh, h) = MigrateAndWriteFresh(version);
        using (migrated) using (fresh)
        {
            Assert.Equal(h.Minutes.Count, migrated.Count("system_minute"));
            Assert.Equal(h.Hours.Count, migrated.Count("app_hour"));
            Assert.Equal(h.Sessions.Count, migrated.Count("sessions"));
            Assert.Equal(h.Crashes.Count, migrated.Count("crashes"));
            Assert.Equal(h.Apps.Count, migrated.Count("apps"));
            AssertSameRows(fresh, migrated, "SELECT ts, cpu_temp, cpu_temp_max, gpu_temp, gpu_temp_max, gpu_hot_max, cpu_load, gpu_load, cpu_power, gpu_power, cpu_volt_max, gpu_volt_max, ram_used, fg_app, active_sec, idle_sec FROM system_minute ORDER BY ts");
            AssertSameRows(fresh, migrated, "SELECT * FROM app_hour ORDER BY ts, app_id");
            AssertSameRows(fresh, migrated, "SELECT app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game FROM sessions ORDER BY start");
            Assert.All(migrated.Db.GetMinutes(U(2024, 1, 1), U(2026, 1, 1)), m => Assert.Null(m.GpuMemMax));
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Writes_after_the_migration_keep_the_rollups_exact(string version)
    {
        var (migrated, fresh, h) = MigrateAndWriteFresh(version);
        using (migrated) using (fresh)
        {
            foreach (var t in new[] { migrated, fresh })
            {
                // Into a day and a month that were backfilled, again over a minute that exists, and a new month.
                t.Db.WriteMinute(Minute(h.Minutes[5].Ts, cpu: 99, cpuMax: 101, mem: 7000));
                t.Db.WriteMinute(Minute(h.Minutes[5].Ts + 60 * 60 * 3, cpu: null, gpu: null));
                t.Db.WriteMinute(Minute(U(2025, 3, 1, 0, 0), cpuPower: 150));
                t.Db.AddAppHour(Hour(h.Hours[3].Ts, h.Hours[3].AppId, fg: 100, cpuTemp: 99));
                t.Db.AddAppHour(Hour(U(2025, 3, 1, 0), 2, fg: 50));
                t.Db.InsertSession(Session(1, U(2025, 2, 10), U(2025, 2, 14)));
                Rollups.AssertAll(t, $"after writing to {(t == migrated ? version : "fresh")}");
            }
            AssertSameRows(fresh, migrated, "SELECT * FROM app_month ORDER BY month, app_id");
            AssertSameRows(fresh, migrated, "SELECT * FROM system_day ORDER BY day");
            Assert.Equal((4 * 86400L).ToString(), migrated.Db.GetMeta("max_session_sec"));
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Migrating_is_done_once_reopening_does_not_add_the_history_up_again(string version)
    {
        using var t = TestDb.At(Legacy.Create(version, History.Sample()));
        t.OpenWriter();
        var month = t.Rows("SELECT * FROM app_month ORDER BY month, app_id");
        var day = t.Rows("SELECT * FROM system_day ORDER BY day");
        t.OpenWriter();
        t.OpenWriter();
        Assert.Equal(month, t.Rows("SELECT * FROM app_month ORDER BY month, app_id"));
        Assert.Equal(day, t.Rows("SELECT * FROM system_day ORDER BY day"));
    }

    [Fact]
    public void A_0_4_13_database_keeps_its_monthly_totals_as_they_were()
    {
        using var t = TestDb.At(Legacy.Create(Legacy.V0413, History.Sample()));
        // A marker only this database has: if migrating rebuilt app_month, it would be gone.
        t.Exec("UPDATE app_month SET min_sec = 12345 WHERE rowid = (SELECT min(rowid) FROM app_month)");
        t.OpenWriter();
        Assert.Equal(1, t.Count("app_month", "min_sec = 12345"));
    }

    [Fact]
    public void A_0_4_13_database_keeps_its_session_bound()
    {
        using var t = TestDb.At(Legacy.Create(Legacy.V0413, History.Sample()));
        t.Exec("UPDATE meta SET value = '999999' WHERE key = 'max_session_sec'");
        t.OpenWriter();
        Assert.Equal("999999", t.Db.GetMeta("max_session_sec"));
    }

    // ── The app reading an old database before the agent has updated it ──

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_app_reads_an_old_database_without_changing_it(string version)
    {
        var h = History.Sample();
        using var t = TestDb.At(Legacy.Create(version, h));
        var before = t.Shape();
        using (var db = t.Reader())
        {
            Assert.Equal(h.Minutes.Count, db.GetMinutes(U(2024, 1, 1), U(2026, 1, 1)).Count);
            db.TempRange(U(2024, 1, 1), U(2026, 1, 1));
            db.GetSessions(U(2025, 1, 1), U(2025, 2, 1));
            ReportBuilder.Build(db, ReportRange.Year, new DateTime(2025, 1, 15), Settings());
        }
        Assert.Equal(before, t.Shape());
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Minutes_and_temperature_ranges_read_from_an_old_database(string version)
    {
        var h = History.Sample();
        using var t = TestDb.At(Legacy.Create(version, h));
        using var db = t.Reader();
        var minutes = db.GetMinutes(U(2024, 12, 1), U(2025, 3, 1));
        Assert.Equal(h.Minutes.Select(m => m.Ts), minutes.Select(m => m.Ts));
        Assert.Equal(h.Minutes.Select(m => m.CpuTemp), minutes.Select(m => m.CpuTemp));
        Assert.All(minutes, m => Assert.Null(m.GpuMemMax));

        var (cpuMin, cpuMax, gpuMin, gpuMax, hotMax, memMax) = db.TempRange(U(2024, 12, 1), U(2025, 3, 1));
        Assert.Equal(h.Minutes.Min(m => m.CpuTemp), cpuMin);
        Assert.Equal(h.Minutes.Max(m => m.CpuTempMax), cpuMax);
        Assert.Equal(h.Minutes.Min(m => m.GpuTemp), gpuMin);
        Assert.Equal(h.Minutes.Max(m => m.GpuTempMax), gpuMax);
        Assert.Equal(h.Minutes.Max(m => m.GpuHotMax), hotMax);
        Assert.Null(memMax);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Daily_totals_are_missing_from_an_old_database_and_say_so(string version)
    {
        using var t = TestDb.At(Legacy.Create(version, History.Sample()));
        using var db = t.Reader();
        Assert.Null(db.GetSystemDays(U(2024, 12, 1), U(2025, 3, 1)));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Sessions_overlapping_a_range_are_found_in_an_old_database(string version)
    {
        using var t = TestDb.At(Legacy.Create(version, History.Sample()));
        using var db = t.Reader();
        // The two-day session started on 30 Dec: it still overlaps 1 January.
        var sessions = db.GetSessions(U(2025, 1, 1), U(2025, 1, 2));
        Assert.Contains(sessions, s => s.Start == U(2024, 12, 30, 20));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Per_app_totals_read_from_an_old_database(string version)
    {
        var h = History.Sample();
        using var t = TestDb.At(Legacy.Create(version, h));
        using var db = t.Reader();
        foreach (var (from, to) in new[] { (U(2024, 12, 1), U(2025, 3, 1)), (U(2024, 12, 25, 13), U(2025, 2, 2, 7)), (U(2025, 1, 1), U(2025, 2, 1)) })
        {
            var totals = db.GetAppTotals(from, to).ToDictionary(x => x.AppId);
            var expected = h.Hours.Where(x => x.Ts >= from && x.Ts < to).GroupBy(x => x.AppId).ToDictionary(g => g.Key, g => g.Sum(x => x.FgSec));
            Assert.Equal(expected.Keys.Order(), totals.Keys.Order());
            foreach (var (app, fg) in expected) Assert.Equal(fg, totals[app].FgSec, 6);
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void One_apps_monthly_chart_reads_from_an_old_database(string version)
    {
        var h = History.Sample();
        using var t = TestDb.At(Legacy.Create(version, h));
        using var db = t.Reader();
        var months = db.GetAppTime(1, U(2024, 12, 1), U(2025, 3, 1), monthly: true).ToDictionary(x => x.Ts, x => x.FgSec);
        var expected = h.Hours.Where(x => x.AppId == 1 && x.FgSec > 0).GroupBy(x => MonthStart(x.Ts)).ToDictionary(g => g.Key, g => g.Sum(x => x.FgSec));
        Assert.Equal(expected.Keys.Order(), months.Keys.Order());
        foreach (var (m, fg) in expected) Assert.Equal(fg, months[m], 6);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Reports_from_an_old_database_match_the_migrated_one(string version)
    {
        var h = History.Sample();
        using var old = TestDb.At(Legacy.Create(version, h));
        using var migrated = TestDb.At(Legacy.Create(version, h));
        migrated.OpenWriter();
        using var oldDb = old.Reader();
        var settings = Settings();
        foreach (var range in new[] { ReportRange.Day, ReportRange.Week, ReportRange.Month, ReportRange.Year })
        {
            var anchor = L(h.Minutes[h.Minutes.Count / 2].Ts);
            var a = ReportBuilder.Build(oldDb, range, anchor, settings);
            var b = ReportBuilder.Build(migrated.Db, range, anchor, settings);
            Assert.True(a.HasData, $"{range}");
            Assert.Equal(b.OnSec, a.OnSec);
            Assert.Equal(b.ActiveSec, a.ActiveSec, 6);
            Assert.Equal(b.AwaySec, a.AwaySec, 6);
            Assert.Equal(b.CpuTempPeak, a.CpuTempPeak);
            Assert.Equal(b.GpuTempPeak, a.GpuTempPeak);
            Assert.Equal(b.Apps.Select(x => (x.Id, Math.Round(x.ActiveSec, 3), x.SessionCount)), a.Apps.Select(x => (x.Id, Math.Round(x.ActiveSec, 3), x.SessionCount)));
            Assert.Equal(b.Crashes.Select(c => c.Ts), a.Crashes.Select(c => c.Ts));
        }
    }

    /// <summary>The same rows in the same order, numbers equal to rounding.</summary>
    internal static void AssertSameRows(TestDb expected, TestDb actual, string sql)
    {
        var e = expected.Rows(sql);
        var a = actual.Rows(sql);
        Assert.Equal(e.Count, a.Count);
        for (int i = 0; i < e.Count; i++)
            foreach (var (col, v) in e[i])
            {
                var w = a[i][col];
                if (v is double or long && w is double or long) Rollups.Near(Rollups.Num(v)!.Value, Rollups.Num(w)!.Value, $"row {i} {col}");
                else Assert.True(Equals(v, w), $"row {i} {col}: expected {v ?? "NULL"}, got {w ?? "NULL"}");
            }
    }
}

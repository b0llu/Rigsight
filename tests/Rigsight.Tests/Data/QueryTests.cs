using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>Every read: ranges include <c>from</c> and exclude <c>to</c>, orderings, missing values, empty databases.</summary>
public sealed class QueryTests
{
    private static readonly long T0 = U(2025, 1, 10, 12);

    // ── Minutes ──

    [Fact]
    public void Minutes_include_from_exclude_to_and_come_in_time_order()
    {
        using var t = new TestDb();
        foreach (int i in new[] { 5, 0, 3, 1, 4, 2 }) t.Db.WriteMinute(Minute(T0 + i * 60));
        Assert.Equal([T0 + 60, T0 + 120, T0 + 180], t.Db.GetMinutes(T0 + 60, T0 + 240).Select(m => m.Ts));
        Assert.Equal([T0 + 60, T0 + 120, T0 + 180, T0 + 240], t.Db.GetMinutes(T0 + 1, T0 + 241).Select(m => m.Ts));
        Assert.Empty(t.Db.GetMinutes(T0, T0));
        Assert.Empty(t.Db.GetMinutes(T0 + 300, T0));
    }

    [Fact]
    public void A_minute_reads_back_exactly_as_written_missing_readings_included()
    {
        using var t = new TestDb();
        var full = new SystemMinute
        {
            Ts = T0, CpuTemp = 51.5, CpuTempMax = 60.25, GpuTemp = 44.5, GpuTempMax = 47, GpuHotMax = 58, GpuMemMax = 70, CpuLoad = 12.5,
            GpuLoad = 99, CpuPower = 65.5, GpuPower = 320.25, CpuVoltMax = 1.35, GpuVoltMax = 1.05, RamUsed = 17.5, FgApp = 42, ActiveSec = 45, IdleSec = 15,
        };
        var empty = new SystemMinute { Ts = T0 + 60 };
        t.Db.WriteMinute(full);
        t.Db.WriteMinute(empty);
        using var reader = t.Reader();
        var read = reader.GetMinutes(T0, T0 + 120);
        Assert.Equivalent(full, read[0], strict: true);
        Assert.Equivalent(empty, read[1], strict: true);
    }

    [Fact]
    public void Temperature_range_takes_the_lowest_average_and_the_highest_peaks()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0, cpu: 40, cpuMax: 90, gpu: 35, gpuMax: 70, hot: 80, mem: 60));
        t.Db.WriteMinute(Minute(T0 + 60, cpu: 30, cpuMax: 50, gpu: 45, gpuMax: 88, hot: 99, mem: 85));
        t.Db.WriteMinute(new SystemMinute { Ts = T0 + 120 });
        t.Db.WriteMinute(Minute(T0 + 180, cpu: 10, cpuMax: 200, gpu: 5, gpuMax: 200, hot: 200, mem: 200)); // outside the range
        Assert.Equal((30.0, 90.0, 35.0, 88.0, 99.0, 85.0), t.Db.TempRange(T0, T0 + 180));
        Assert.Equal((null, null, null, null, null, null), t.Db.TempRange(T0 + 120, T0 + 180));
        Assert.Equal((null, null, null, null, null, null), t.Db.TempRange(0, 1));
    }

    [Fact]
    public void Average_temperatures_skip_missing_readings()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0, cpu: 40, gpu: null));
        t.Db.WriteMinute(Minute(T0 + 60, cpu: 50, gpu: 60));
        t.Db.WriteMinute(Minute(T0 + 120, cpu: null, gpu: 70));
        Assert.Equal((45.0, 65.0), t.Db.AverageTemps(T0, T0 + 180));
        Assert.Equal((null, null), t.Db.AverageTemps(0, 1));
    }

    [Fact]
    public void Peak_temperatures_before_a_moment_cover_the_minutes_up_to_it()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0 - 301, cpuMax: 99, gpuMax: 99));
        t.Db.WriteMinute(Minute(T0 - 300, cpuMax: 70, gpuMax: 60));
        t.Db.WriteMinute(Minute(T0, cpuMax: 75, gpuMax: 50));
        t.Db.WriteMinute(Minute(T0 + 60, cpuMax: 98, gpuMax: 98));
        Assert.Equal((75.0, 60.0), t.Db.PeakTempsBefore(T0, 5));
        Assert.Equal((null, null), t.Db.PeakTempsBefore(0, 5));
    }

    [Fact]
    public void Front_app_is_the_latest_known_one_within_three_minutes()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0 - 180, app: 1));
        t.Db.WriteMinute(Minute(T0 - 120, app: 2));
        t.Db.WriteMinute(Minute(T0 - 60, app: null));
        t.Db.WriteMinute(Minute(T0 + 60, app: 3));
        Assert.Equal(2, t.Db.FrontAppAt(T0));        // app 1's minute is exactly 3 minutes back: out
        Assert.Equal(3, t.Db.FrontAppAt(T0 + 60));
        Assert.Equal(1, t.Db.FrontAppAt(T0 - 121));
        Assert.Null(t.Db.FrontAppAt(T0 - 181));
    }

    [Fact]
    public void Find_minute_gives_the_first_minute_with_the_value_in_the_range_and_its_app()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0, cpuMax: 80, app: 1));
        t.Db.WriteMinute(Minute(T0 + 60, cpuMax: 90, app: null));
        t.Db.WriteMinute(Minute(T0 + 120, cpuMax: 90, app: 2));
        t.Db.WriteMinute(Minute(T0 + 180, cpuMax: 95, app: 3));
        Assert.Equal((T0 + 60, (long?)null), t.Db.FindMinute("cpu_temp_max", 90, T0, T0 + 3600));
        Assert.Equal((T0 + 120, (long?)2), t.Db.FindMinute("cpu_temp_max", 90, T0 + 61, T0 + 3600));
        Assert.Null(t.Db.FindMinute("cpu_temp_max", 95, T0, T0 + 180));
        Assert.Null(t.Db.FindMinute("cpu_temp_max", 91, T0, T0 + 3600));
        Assert.Equal((T0, (long?)1), t.Db.FindMinute("cpu_temp_max", 80, T0, T0 + 1));
    }

    [Theory]
    [InlineData("cpu_temp_max")]
    [InlineData("gpu_temp_max")]
    [InlineData("gpu_hot_max")]
    [InlineData("cpu_volt_max")]
    [InlineData("gpu_volt_max")]
    [InlineData("cpu_power")]
    [InlineData("gpu_power")]
    public void Find_minute_works_for_every_column_reports_look_up(string column)
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0, cpuMax: 1.25, gpuMax: 1.25, hot: 1.25, cpuVolt: 1.25, gpuVolt: 1.25, cpuPower: 1.25, gpuPower: 1.25, app: 7));
        Assert.Equal((T0, (long?)7), t.Db.FindMinute(column, 1.25, T0, T0 + 60));
    }

    // ── Days ──

    [Fact]
    public void Days_include_from_exclude_to_and_come_in_order()
    {
        using var t = new TestDb();
        foreach (var d in new[] { 12, 10, 11, 13 }) t.Db.WriteMinute(Minute(U(2025, 1, d, 12)));
        Assert.Equal([U(2025, 1, 11), U(2025, 1, 12)], t.Db.GetSystemDays(U(2025, 1, 11), U(2025, 1, 13))!.Select(d => d.Day));
        Assert.Equal([U(2025, 1, 12), U(2025, 1, 13)], t.Db.GetSystemDays(U(2025, 1, 11) + 1, U(2025, 2, 1))!.Select(d => d.Day));
        Assert.Empty(t.Db.GetSystemDays(U(2025, 1, 13), U(2025, 1, 10))!);
    }

    [Fact]
    public void A_day_reads_back_every_total()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0, cpu: 50, gpu: 40, cpuLoad: 20, gpuLoad: 90, cpuMax: 60, gpuMax: 45, hot: 70, cpuVolt: 1.3, gpuVolt: 1.0, cpuPower: 80, gpuPower: 300, active: 50, idle: 10));
        t.Db.WriteMinute(Minute(T0 + 60, cpu: 70, gpu: null, cpuLoad: null, gpuLoad: 10, cpuMax: 75, gpuMax: null, hot: null, cpuVolt: 1.1, gpuVolt: null, cpuPower: 100, gpuPower: null, active: 0, idle: 60));
        var d = Assert.Single(t.Db.GetSystemDays(U(2025, 1, 10), U(2025, 1, 11))!);
        Assert.Equivalent(new SystemDay
        {
            Day = U(2025, 1, 10), Minutes = 2, ActiveSec = 50, IdleSec = 70, CpuTempSum = 120, CpuTempN = 2, GpuTempSum = 40, GpuTempN = 1,
            CpuLoadSum = 20, CpuLoadN = 1, GpuLoadSum = 100, GpuLoadN = 2, CpuTempMax = 75, GpuTempMax = 45, GpuHotMax = 70,
            CpuVoltMax = 1.3, GpuVoltMax = 1.0, CpuPowerMax = 100, GpuPowerMax = 300,
        }, d, strict: true);
    }

    // ── Hours ──

    [Fact]
    public void Hours_include_from_exclude_to_and_read_back_every_column()
    {
        using var t = new TestDb();
        var full = new AppHour
        {
            Ts = T0, AppId = 3, FgSec = 1, IdleSec = 2, BgSec = 3, MinSec = 4, CpuSum = 5, CpuN = 6, CpuMax = 7, MemSum = 8, MemN = 9, MemMax = 10,
            CpuTempSum = 11, CpuTempN = 12, CpuTempMax = 13, GpuTempSum = 14, GpuTempN = 15, GpuTempMax = 16, GpuHotMax = 17,
            CpuPowerMax = 18, GpuPowerMax = 19, CpuVoltMax = 20, GpuVoltMax = 21, GpuLoadSum = 22, GpuLoadN = 23,
        };
        t.Db.AddAppHour(full);
        t.Db.AddAppHour(new AppHour { Ts = T0 + 3600, AppId = 3, FgSec = 1 });
        t.Db.AddAppHour(new AppHour { Ts = T0 - 3600, AppId = 3, FgSec = 1 });
        Assert.Equivalent(full, Assert.Single(t.Db.GetAppHours(T0, T0 + 3600)), strict: true);
        Assert.Equal(2, t.Db.GetAppHours(T0 - 3600, T0 + 1).Count);
        Assert.Empty(t.Db.GetAppHours(T0 + 1, T0 + 3600));
    }

    // ── Sessions ──

    private static TestDb WithSessions()
    {
        var t = new TestDb();
        // A long session sets the look-back; the others sit around a range [T0, T0 + 1h).
        t.Db.InsertSession(Session(1, T0 - 86400, T0 + 60, active: 50000));        // started a day before, ends inside
        t.Db.InsertSession(Session(2, T0 - 7200, T0, active: 7200));               // ends exactly at from: not in the range
        t.Db.InsertSession(Session(2, T0 - 7200, T0 - 60, active: 7000));          // over before
        t.Db.InsertSession(Session(3, T0, T0 + 600, active: 600));                 // starts exactly at from
        t.Db.InsertSession(Session(3, T0 + 1200, T0 + 9000, active: 5000, game: true)); // starts inside, ends after
        t.Db.InsertSession(Session(4, T0 + 3600, T0 + 4000, active: 400));         // starts exactly at to: not in it
        t.Db.InsertSession(Session(4, T0 + 1800, T0 + 1830, active: 30));          // short
        return t;
    }

    [Fact]
    public void Sessions_overlapping_the_range_include_one_started_long_before_and_none_that_ended_at_or_before_from()
    {
        using var t = WithSessions();
        var s = t.Db.GetSessions(T0, T0 + 3600);
        Assert.Equal([(1L, T0 - 86400), (3L, T0), (3L, T0 + 1200), (4L, T0 + 1800)], s.Select(x => (x.AppId, x.Start)));
    }

    [Fact]
    public void A_session_exactly_as_long_as_the_longest_still_reaches_a_range_it_ends_in()
    {
        using var t = new TestDb();
        t.Db.InsertSession(Session(1, 1_000_000, 1_000_000 + 500_000));
        Assert.Single(t.Db.GetSessions(1_499_999, 1_500_000));
        Assert.Empty(t.Db.GetSessions(1_500_000, 1_600_000));
        using var reader = t.Reader();
        Assert.Single(reader.GetSessions(1_499_999, 1_500_000));
    }

    [Fact]
    public void A_session_read_back_has_every_field()
    {
        using var t = new TestDb();
        t.Db.InsertSession(new SessionRow { AppId = 5, Start = T0, End = T0 + 100, ActiveSec = 90.5, CpuTempMax = 70.5, GpuTempMax = null, IsGame = true });
        var s = Assert.Single(t.Db.GetSessions(T0, T0 + 1));
        Assert.Equivalent(new SessionRow { Id = s.Id, AppId = 5, Start = T0, End = T0 + 100, ActiveSec = 90.5, CpuTempMax = 70.5, GpuTempMax = null, IsGame = true }, s, strict: true);
        Assert.True(s.Id > 0);
    }

    [Fact]
    public void Longest_sessions_are_longest_first_above_the_minimum_and_limited()
    {
        using var t = WithSessions();
        Assert.Equal([50000.0, 5000, 600], t.Db.GetLongestSessions(T0, T0 + 3600, 60, 10).Select(x => x.ActiveSec));
        Assert.Equal([50000.0, 5000], t.Db.GetLongestSessions(T0, T0 + 3600, 60, 2).Select(x => x.ActiveSec));
        Assert.Equal([50000.0, 5000, 600, 30], t.Db.GetLongestSessions(T0, T0 + 3600, 0, 10).Select(x => x.ActiveSec));
        Assert.Equal([50000.0], t.Db.GetLongestSessions(T0, T0 + 3600, 5001, 10).Select(x => x.ActiveSec));
        Assert.Empty(t.Db.GetLongestSessions(T0, T0 + 3600, 60, 0));
        Assert.Empty(t.Db.GetLongestSessions(T0 + 3600, T0, 0, 10));
    }

    [Fact]
    public void Session_stats_count_and_take_the_longest_per_app()
    {
        using var t = WithSessions();
        var stats = t.Db.GetSessionStats(T0, T0 + 3600, 60);
        Assert.Equal(new Dictionary<long, (int, double)> { [1] = (1, 50000), [3] = (2, 5000) }, stats);
        Assert.Equal((2, 400.0), t.Db.GetSessionStats(T0, T0 + 7200, 0)[4]);
        Assert.Empty(t.Db.GetSessionStats(T0 + 100_000, T0 + 200_000, 0));
    }

    [Fact]
    public void Recent_sessions_are_one_apps_newest_first_above_the_minimum_and_limited()
    {
        using var t = WithSessions();
        Assert.Equal([T0 + 1200, T0], t.Db.GetRecentSessions(3, T0, T0 + 3600, 60, 10).Select(x => x.Start));
        Assert.Equal([T0 + 1200], t.Db.GetRecentSessions(3, T0, T0 + 3600, 60, 1).Select(x => x.Start));
        Assert.Equal([T0 + 1200], t.Db.GetRecentSessions(3, T0, T0 + 3600, 601, 10).Select(x => x.Start));
        Assert.Equal([T0 - 86400], t.Db.GetRecentSessions(1, T0, T0 + 3600, 60, 10).Select(x => x.Start));
        Assert.Empty(t.Db.GetRecentSessions(2, T0, T0 + 3600, 0, 10));
        Assert.Empty(t.Db.GetRecentSessions(99, 0, long.MaxValue / 2, 0, 10));
    }

    [Fact]
    public void Session_queries_on_an_empty_database_are_empty()
    {
        using var t = new TestDb();
        Assert.Empty(t.Db.GetSessions(0, T0));
        Assert.Empty(t.Db.GetLongestSessions(0, T0, 0, 10));
        Assert.Empty(t.Db.GetSessionStats(0, T0, 0));
        Assert.Empty(t.Db.GetRecentSessions(1, 0, T0, 0, 10));
    }

    // ── Crashes ──

    [Fact]
    public void Crashes_are_newest_first_in_the_range_and_read_back_every_field()
    {
        using var t = new TestDb();
        var c = new CrashEvent { Ts = T0, Kind = CrashKind.SystemCrash, AppExe = "", AppPath = null, Module = "ntoskrnl", Code = "0x124", Detail = "WHEA", DuringSleep = true };
        t.Db.InsertCrashes([
            new CrashEvent { Ts = T0 - 1, Kind = CrashKind.AppHang, AppExe = "a.exe" },
            c,
            new CrashEvent { Ts = T0 + 100, Kind = CrashKind.AppCrash, AppExe = "b.exe", AppPath = @"C:\b.exe" },
            new CrashEvent { Ts = T0 + 200, Kind = CrashKind.GpuDriverReset },
        ]);
        var list = t.Db.GetCrashes(T0, T0 + 200);
        Assert.Equal([T0 + 100, T0], list.Select(x => x.Ts));
        Assert.Equivalent(new { c.Ts, c.Kind, c.AppExe, c.AppPath, c.Module, c.Code, c.Detail, c.DuringSleep }, list[1]);
        Assert.Equal(@"C:\b.exe", list[0].AppPath);
    }

    [Fact]
    public void A_crash_kind_this_version_does_not_know_reads_as_an_app_crash()
    {
        using var t = new TestDb();
        t.Exec("INSERT INTO crashes(ts, kind) VALUES(5, 'FutureKind')");
        Assert.Equal(CrashKind.AppCrash, Assert.Single(t.Db.GetCrashes(0, 10)).Kind);
    }

    [Fact]
    public void Inserting_crashes_returns_only_the_new_ones()
    {
        using var t = new TestDb();
        var a = new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "a.exe" };
        var b = new CrashEvent { Ts = T0, Kind = CrashKind.AppHang, AppExe = "a.exe" };      // other kind: new
        var c = new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "b.exe" };     // other app: new
        var d = new CrashEvent { Ts = T0 + 1, Kind = CrashKind.AppCrash, AppExe = "a.exe" }; // other time: new
        Assert.Equal([a], t.Db.InsertCrashes([a]));
        Assert.Equal([b, c, d], t.Db.InsertCrashes([a, b, c, d, a]));
        Assert.Empty(t.Db.InsertCrashes([a, b, c, d]));
        Assert.Empty(t.Db.InsertCrashes([]));
        Assert.Equal(4, t.Count("crashes"));
    }

    [Fact]
    public void A_crash_without_an_app_is_stored_with_an_empty_exe_and_deduplicated_the_same()
    {
        using var t = new TestDb();
        Assert.Single(t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.UnexpectedShutdown, AppExe = null! }]));
        Assert.Empty(t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.UnexpectedShutdown }]));
        Assert.Equal("", Assert.Single(t.Db.GetCrashes(0, T0 + 1)).AppExe);
    }

    [Fact]
    public void Crash_context_has_the_temperatures_of_the_five_minutes_before()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0 - 360, cpuMax: 99, gpuMax: 99));  // too early
        t.Db.WriteMinute(Minute(T0 - 300, cpuMax: 80, gpuMax: 60));
        t.Db.WriteMinute(Minute(T0 - 60, cpuMax: 70, gpuMax: 75));
        t.Db.WriteMinute(Minute(T0 + 60, cpuMax: 98, gpuMax: 98));   // after
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.SystemCrash }]);
        var ctx = Assert.Single(t.Db.GetCrashContext(T0, T0 + 1)).Value;
        Assert.Equal(80, ctx.CpuBefore);
        Assert.Equal(75, ctx.GpuBefore);
    }

    [Fact]
    public void Crash_context_has_the_latest_app_in_front_within_three_minutes()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0 - 170, app: 1));
        t.Db.WriteMinute(Minute(T0 - 110, app: 2));
        t.Db.WriteMinute(Minute(T0 - 50, app: null)); // away: skipped
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.GpuDriverReset }, new CrashEvent { Ts = T0 + 75, Kind = CrashKind.GpuDriverReset }]);
        var ctx = t.Db.GetCrashContext(T0, T0 + 3600);
        var byTime = t.Db.GetCrashes(T0, T0 + 3600).ToDictionary(c => c.Ts, c => ctx[c.Id]);
        Assert.Equal(2, byTime[T0].FrontApp);
        Assert.Null(byTime[T0 + 75].FrontApp); // app 2's minute is 185 s back by then, and the latest minute had nothing in front
    }

    [Fact]
    public void Crash_context_has_nothing_when_nothing_was_recorded()
    {
        using var t = new TestDb();
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "x.exe" }]);
        Assert.Equal(new CrashContext(null, null, null, null), Assert.Single(t.Db.GetCrashContext(0, T0 + 1)).Value);
        Assert.Empty(t.Db.GetCrashContext(T0 + 1, T0 + 100));
        Assert.Empty(t.Db.GetCrashContext(T0 + 100, T0));
    }

    [Fact]
    public void Crash_context_has_how_long_the_crashed_apps_session_ran()
    {
        using var t = new TestDb();
        long game = t.Db.UpsertApp("Game.exe", "Game", null, AppCategory.Game);
        long other = t.Db.UpsertApp("other.exe", "Other", null, AppCategory.Other);
        t.Db.InsertSession(Session(game, T0 - 3 * 86400, T0 - 2 * 86400, active: 1));  // long over
        t.Db.InsertSession(Session(game, T0 - 7200, T0 - 30, active: 5000));           // the one the crash ended
        t.Db.InsertSession(Session(other, T0 - 600, T0, active: 600));                 // another app
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "GAME.EXE" }]);
        Assert.Equal(5000, Assert.Single(t.Db.GetCrashContext(T0, T0 + 1)).Value.SessionSec);
    }

    [Fact]
    public void Crash_context_finds_a_session_that_started_days_before()
    {
        using var t = new TestDb();
        long game = t.Db.UpsertApp("game.exe", "Game", null, AppCategory.Game);
        t.Db.InsertSession(Session(game, T0 - 4 * 86400, T0 + 30, active: 99_000));
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.AppHang, AppExe = "game.exe" }]);
        Assert.Equal(99_000, Assert.Single(t.Db.GetCrashContext(T0, T0 + 1)).Value.SessionSec);
    }

    [Theory]
    [InlineData(-4000, -601, false)] // ended over ten minutes before the crash
    [InlineData(-4000, -600, true)]  // ended exactly ten minutes before
    [InlineData(60, 600, true)]      // started just after (the log's time is rounded)
    [InlineData(61, 600, false)]     // started over a minute after: a new session
    public void Crash_context_only_takes_a_session_ending_around_the_crash(int start, int end, bool found)
    {
        using var t = new TestDb();
        long game = t.Db.UpsertApp("game.exe", "Game", null, AppCategory.Game);
        t.Db.InsertSession(Session(game, T0 + start, T0 + end, active: 1234));
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "game.exe" }]);
        Assert.Equal(found ? 1234 : null, Assert.Single(t.Db.GetCrashContext(T0, T0 + 1)).Value.SessionSec);
    }

    [Fact]
    public void Crash_context_takes_the_session_that_ended_last_when_two_are_close()
    {
        using var t = new TestDb();
        long game = t.Db.UpsertApp("game.exe", "Game", null, AppCategory.Game);
        t.Db.InsertSession(Session(game, T0 - 3000, T0 - 500, active: 111));
        t.Db.InsertSession(Session(game, T0 - 400, T0 - 10, active: 222));
        t.Db.InsertCrashes([new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash, AppExe = "game.exe" }]);
        Assert.Equal(222, Assert.Single(t.Db.GetCrashContext(T0, T0 + 1)).Value.SessionSec);
    }

    [Fact]
    public void Crash_context_is_keyed_by_crash_id_for_every_crash_in_the_range()
    {
        using var t = new TestDb();
        t.Db.InsertCrashes(Enumerable.Range(0, 20).Select(i => new CrashEvent { Ts = T0 + i * 100, Kind = CrashKind.AppHang, AppExe = "a.exe" }));
        var ids = t.Db.GetCrashes(T0 + 500, T0 + 1500).Select(c => c.Id).Order();
        Assert.Equal(ids, t.Db.GetCrashContext(T0 + 500, T0 + 1500).Keys.Order());
    }

    // ── Apps and drives ──

    [Fact]
    public void Upserting_an_app_keeps_its_id_first_seen_and_path_when_the_new_path_is_unknown()
    {
        using var t = new TestDb();
        long id = t.Db.UpsertApp("game.exe", "Game", @"C:\Games\game.exe", AppCategory.Game);
        long firstSeen = t.Db.LoadApps().Single().FirstSeen;
        Assert.Equal(id, t.Db.UpsertApp("GAME.exe", "Game Renamed", null, AppCategory.Other));
        var app = Assert.Single(t.Db.LoadApps());
        Assert.Equal(("game.exe", "Game Renamed", @"C:\Games\game.exe", AppCategory.Other, firstSeen), (app.Exe, app.Name, app.Path, app.Category, app.FirstSeen));
        t.Db.UpsertApp("game.exe", "Game", @"D:\game.exe", AppCategory.Game);
        Assert.Equal(@"D:\game.exe", t.Db.LoadApps().Single().Path);
    }

    [Fact]
    public void First_seen_is_when_the_app_was_first_recorded()
    {
        using var t = new TestDb();
        long before = TimeUtil.NowUnix();
        t.Db.UpsertApp("a.exe", "A", null, AppCategory.Other);
        Assert.InRange(t.Db.LoadApps().Single().FirstSeen, before, TimeUtil.NowUnix());
    }

    [Fact]
    public void Apps_get_increasing_ids_and_an_unknown_category_reads_as_other()
    {
        using var t = new TestDb();
        long a = t.Db.UpsertApp("a.exe", "A", null, AppCategory.Game);
        long b = t.Db.UpsertApp("b.exe", "B", null, AppCategory.Game);
        Assert.True(b > a);
        t.Exec("UPDATE apps SET category = 'Spaceship' WHERE exe = 'b.exe'");
        Assert.Equal(AppCategory.Other, t.Db.LoadApps().Single(x => x.Id == b).Category);
    }

    [Fact]
    public void Drive_days_replace_the_same_day_and_read_from_a_day_on_in_order()
    {
        using var t = new TestDb();
        t.Db.UpsertDriveDay(new DriveDay { Day = U(2025, 1, 2), Drive = "C:\\", UsedGb = 10, TotalGb = 100 });
        t.Db.UpsertDriveDay(new DriveDay { Day = U(2025, 1, 1), Drive = "C:\\", UsedGb = 5, TotalGb = 100 });
        t.Db.UpsertDriveDay(new DriveDay { Day = U(2025, 1, 2), Drive = "C:\\", UsedGb = 11, TotalGb = 100 });
        t.Db.UpsertDriveDay(new DriveDay { Day = U(2025, 1, 2), Drive = "D:\\", UsedGb = 1, TotalGb = 2 });
        var days = t.Db.GetDriveDays(U(2025, 1, 2));
        Assert.Equal([(U(2025, 1, 2), 11.0), (U(2025, 1, 2), 1.0)], days.Select(d => (d.Day, d.UsedGb)).OrderByDescending(x => x.Item2));
        Assert.Equal(3, t.Db.GetDriveDays(0).Count);
        Assert.Equal(U(2025, 1, 1), t.Db.GetDriveDays(0)[0].Day);
    }

    // ── When history starts ──

    [Fact]
    public void First_times_are_null_on_an_empty_database()
    {
        using var t = new TestDb();
        using var reader = t.Reader();
        Assert.Null(reader.FirstDataTime());
        Assert.Null(reader.FirstMinuteTime());
        Assert.Null(reader.FirstCrashTime());
    }

    [Fact]
    public void First_data_time_is_the_oldest_minute_or_hour_whichever_is_older()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0));
        Assert.Equal(T0, t.Db.FirstDataTime());
        t.Db.AddAppHour(Hour(T0 - 7200, 1));
        Assert.Equal(T0 - 7200, t.Db.FirstDataTime());
        Assert.Equal(T0, t.Db.FirstMinuteTime());
        t.Db.WriteMinute(Minute(T0 - 9000));
        Assert.Equal(T0 - 9000, t.Db.FirstDataTime());
    }

    [Fact]
    public void First_data_time_counts_hours_alone()
    {
        using var t = new TestDb();
        t.Db.AddAppHour(Hour(T0, 1));
        Assert.Equal(T0, t.Db.FirstDataTime());
        Assert.Null(t.Db.FirstMinuteTime());
    }

    [Fact]
    public void First_crash_time_is_the_oldest_crash_and_ignores_other_history()
    {
        using var t = new TestDb();
        t.Db.WriteMinute(Minute(T0 - 99999));
        t.Db.InsertCrashes([new CrashEvent { Ts = T0 + 5, Kind = CrashKind.AppHang }, new CrashEvent { Ts = T0, Kind = CrashKind.AppCrash }]);
        Assert.Equal(T0, t.Db.FirstCrashTime());
    }
}

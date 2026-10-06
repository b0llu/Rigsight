using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Reports;

/// <summary>
/// What a report reads out of the minutes beyond totals: who made the heat, work done in the background, steady load
/// and rest, clocks running down, fans that stopped; and out of the days before it: records, streaks, the same weekday,
/// months ago.
/// </summary>
public sealed class HeatShapeTests
{
    private static readonly DateTime Day = new(2025, 4, 2);
    private static readonly long T0 = U(Day.AddHours(10));

    private static Report Build(TestDb t, DateTime? day = null) => ReportBuilder.Build(t.Db, ReportRange.Day, day ?? Day, Settings());

    private static (long Game, long Chrome, long Blender) Apps(TestDb t) =>
        (t.Db.UpsertApp("game.exe", "Game", null, AppCategory.Game), t.Db.UpsertApp("chrome.exe", "Chrome", null, AppCategory.Browser),
            t.Db.UpsertApp("blender.exe", "Blender", null, AppCategory.Productivity));

    [Fact]
    public void Hot_minutes_are_counted_by_the_app_working_the_part()
    {
        using var t = new TestDb();
        var (game, chrome, _) = Apps(t);
        for (int i = 0; i < 46; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: chrome, cpuMax: 80, gpuMax: 60, cpuApp: game, gpuApp: game));
        for (int i = 46; i < 48; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: chrome, cpuMax: 78, gpuMax: 77, cpuApp: chrome));
        for (int i = 48; i < 60; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: chrome, cpuMax: 60, gpuMax: 55));
        var r = Build(t);
        Assert.Equal((48, 2), (r.CpuHotMinutes, r.GpuHotMinutes));
        Assert.Equal([("Game", 46), ("Chrome", 2)], r.CpuHotByApp.Select(h => (h.App, h.Minutes)));
        Assert.Empty(r.GpuHotByApp); // the two hot GPU minutes were nobody's
    }

    [Fact]
    public void The_longest_run_of_heavy_work_behind_another_app_is_kept()
    {
        using var t = new TestDb();
        var (_, chrome, blender) = Apps(t);
        for (int i = 0; i < 12; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: chrome, cpuLoad: 90, cpuApp: blender));
        for (int i = 12; i < 20; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: chrome, cpuLoad: 90, cpuApp: chrome)); // its own work
        for (int i = 20; i < 24; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: chrome, gpuLoad: 95, gpuApp: blender)); // four: too short
        for (int i = 24; i < 30; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: chrome, cpuLoad: 90, cpuApp: blender, active: 0, idle: 60)); // nobody there
        var bg = Build(t).BackgroundWork;
        Assert.NotNull(bg);
        Assert.Equal(("Blender", "Chrome", AppCategory.Browser, L(T0), 12, false), (bg.App, bg.FrontApp, bg.FrontCategory, bg.Start, bg.Minutes, bg.Gpu));
    }

    [Fact]
    public void Clocks_well_under_the_same_sessions_cool_ones_when_hot_are_throttling()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        for (int i = 0; i < 15; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpuLoad: 95, gpuMax: 70, gpuPower: 300, gpuClock: 1800 + i, gpuApp: game)); // cool, at full clocks
        for (int i = 15; i < 19; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpuLoad: 95, gpuMax: 85 + i - 15, gpuPower: 290, gpuClock: 1500, gpuApp: game));
        for (int i = 19; i < 22; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpuLoad: 95, gpuMax: 86, gpuPower: 300, gpuClock: 1780, gpuApp: game)); // hot but not slowed
        var r = Build(t);
        Assert.NotNull(r.GpuThrottle);
        Assert.Equal((4, 85.0), (r.GpuThrottle.Minutes, r.GpuThrottle.FromTemp));
        Assert.InRange(r.GpuThrottle.DropPercent, 16, 18);
    }

    [Fact]
    public void Lower_clocks_in_a_heavier_game_or_scene_are_not_throttling()
    {
        using var t = new TestDb();
        var (game, _, blender) = Apps(t);
        // A light game cool at high clocks, then a heavy one hot at lower clocks: another load, not the same one slowed.
        for (int i = 0; i < 30; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpuLoad: 95, gpuMax: 70, gpuPower: 180, gpuClock: 2800, gpuApp: game));
        for (int i = 30; i < 60; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: blender, gpuLoad: 99, gpuMax: 86, gpuPower: 330, gpuClock: 2400, gpuApp: blender));
        Assert.Null(Build(t).GpuThrottle);
        // In one game: a heavier scene draws more power and clocks lower by the power limit, hot or not.
        using var t2 = new TestDb();
        var (game2, _, _) = Apps(t2);
        for (int i = 0; i < 20; i++) t2.Db.WriteMinute(Minute(T0 + i * 60, app: game2, gpuLoad: 95, gpuMax: 74, gpuPower: 250, gpuClock: 2700, gpuApp: game2));
        for (int i = 20; i < 40; i++) t2.Db.WriteMinute(Minute(T0 + i * 60, app: game2, gpuLoad: 99, gpuMax: 84, gpuPower: 340, gpuClock: 2300, gpuApp: game2));
        Assert.Null(Build(t2).GpuThrottle);
    }

    [Fact]
    public void Steady_load_skips_each_runs_warm_up_and_rest_skips_the_cool_down_after()
    {
        using var t = new TestDb();
        var (game, chrome, _) = Apps(t);
        int m = 0;
        void Write(int count, Func<int, SystemMinute> make) { for (int i = 0; i < count; i++, m++) t.Db.WriteMinute(make(i)); }
        Write(30, _ => Minute(T0 + m * 60, app: chrome, cpu: 40, gpu: 35, cpuLoad: 5, gpuLoad: 3)); // at rest
        Write(40, i => Minute(T0 + m * 60, app: game, cpu: 60, gpu: i < 10 ? 50 + i * 2 : 72, cpuLoad: 30, gpuLoad: 97, gpuPower: 300, gpuApp: game, hot: i < 10 ? null : 72 + 14));
        Write(15, i => Minute(T0 + m * 60, app: chrome, cpu: 45, gpu: 60 - i, cpuLoad: 5, gpuLoad: 3)); // cooling down: not rest
        Write(30, _ => Minute(T0 + m * 60, app: chrome, cpu: 41, gpu: 37, cpuLoad: 5, gpuLoad: 3)); // rest again
        Write(20, _ => Minute(T0 + m * 60, app: chrome, cpu: 38, gpu: 34, cpuLoad: 2, gpuLoad: 1, active: 0, idle: 60)); // away: not rest either
        var r = Build(t);
        var steady = Assert.Single(r.Steady);
        Assert.Equal(("Game", 30, 1, 72.0, 60.0, 300.0), (steady.App, steady.Minutes, steady.Days, steady.Gpu, steady.Cpu, steady.GpuPower));
        Assert.NotNull(r.RestTemps);
        Assert.Equal((60, 40.5, 36.0), (r.RestTemps.Minutes, r.RestTemps.Cpu, r.RestTemps.Gpu));
        Assert.Equal((13.0, 30), (r.HotSpotGap!.Gpu, r.HotSpotGap.Minutes)); // steady minutes only: 86 over the core's high of 73
    }

    [Fact]
    public void A_fans_minutes_that_should_turn_and_its_longest_stop_are_counted()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        long gpuFan = t.Db.FanId("/gpu-nvidia/0/fan/1", "GPU Fan 1", "RTX"), caseFan = t.Db.FanId("/lpc/it8686e/0/fan/1", "Fan #2", "ITE IT8686E"),
            empty = t.Db.FanId("/lpc/it8686e/0/fan/3", "Fan #4", "ITE IT8686E");
        for (int i = 0; i < 60; i++)
        {
            // Twenty cool minutes (the GPU fan stopped on purpose), then hot ones; the GPU fan stops at 30–37, the case fan at 40–54.
            double gpu = i < 20 ? 45 : 78;
            int gpuRpm = i < 20 || i is >= 30 and < 38 ? 0 : 1600, caseRpm = i is >= 40 and < 55 ? 0 : 1000;
            t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpu: gpu, gpuLoad: i < 20 ? 3 : 95));
            t.Db.WriteFanMinutes(T0 + i * 60, [(gpuFan, gpuRpm, gpuRpm), (caseFan, caseRpm, caseRpm), (empty, 0, 0)]);
        }
        var fans = Build(t).Fans;
        var g = fans.Single(f => f.Gpu);
        Assert.Equal((40, 8, 8, L(T0 + 30 * 60), 78.0), (g.SpinMinutes, g.StoppedMinutes, g.LongestStop, g.StopStart, g.StopTemp)); // cool minutes don't count
        var c = fans.Single(f => f.Name == "Fan #2");
        Assert.Equal((60, 15, 15, L(T0 + 40 * 60)), (c.SpinMinutes, c.StoppedMinutes, c.LongestStop, c.StopStart));
        var e = fans.Single(f => f.Name == "Fan #4");
        Assert.Equal((60, 60, 60), (e.SpinMinutes, e.StoppedMinutes, e.LongestStop)); // always 0: the week before will say it's nothing
    }

    /// <summary>Days of history before <see cref="Day"/>: a minute a day with the given values, on days the picker says.</summary>
    private static void DaysBefore(TestDb t, int count, Func<int, bool> on, Func<int, SystemMinute> minute)
    {
        for (int back = 1; back <= count; back++)
            if (on(back)) t.Db.WriteMinute(minute(back));
    }

    [Fact]
    public void A_peak_beating_every_day_in_a_window_is_a_record_for_that_window()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        DaysBefore(t, 45, _ => true, back => Minute(U(Day.AddDays(-back).AddHours(12)), app: game, cpuMax: 60, gpuMax: 55));
        t.Db.WriteMinute(Minute(T0, app: game, cpuMax: 78, gpuMax: 69)); // the GPU's 69° isn't worth a record
        var cpu = Assert.Single(Build(t).Records);
        Assert.Equal((RecordKind.HottestCpu, "78°", 30), (cpu.Kind, cpu.Value, cpu.Days)); // 45 days of history: a month, not "three months"
        DaysBefore(t, 100, back => back > 45 && back % 2 == 0, back => Minute(U(Day.AddDays(-back).AddHours(12)), app: game, cpuMax: 60, gpuMax: 55));
        cpu = Assert.Single(Build(t).Records);
        Assert.Equal(90, cpu.Days); // 100 days back, 72 of them used: three months, not a year

        t.Db.WriteMinute(Minute(U(Day.AddDays(-50).AddHours(12)), app: game, cpuMax: 79)); // a hotter day within three months: only a month's record
        Assert.Equal(30, Assert.Single(Build(t).Records).Days);
        t.Db.WriteMinute(Minute(U(Day.AddDays(-10).AddHours(12)), app: game, cpuMax: 79));
        Assert.Empty(Build(t).Records);
    }

    [Fact]
    public void Screen_time_over_the_usual_several_days_running_is_a_streak()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        // A minute a day for weeks, then five busy days (an hour each) ending today.
        DaysBefore(t, 40, back => back >= 5, back => Minute(U(Day.AddDays(-back).AddHours(12)), app: game));
        for (int back = 4; back >= 1; back--)
            for (int i = 0; i < 60; i++) t.Db.WriteMinute(Minute(U(Day.AddDays(-back).AddHours(12)) + i * 60, app: game));
        for (int i = 0; i < 60; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game));
        Assert.Equal(5, Build(t).StreakDays);
        Assert.Equal(0, Build(t, Day.AddDays(-5)).StreakDays);
    }

    [Fact]
    public void A_date_with_two_day_rows_after_a_time_zone_change_still_gives_a_report()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        DaysBefore(t, 40, back => back >= 5, back => Minute(U(Day.AddDays(-back).AddHours(12)), app: game));
        for (int back = 4; back >= 1; back--)
            for (int i = 0; i < 60; i++) t.Db.WriteMinute(Minute(U(Day.AddDays(-back).AddHours(12)) + i * 60, app: game));
        for (int i = 0; i < 60; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game));
        // The PC's zone moved an hour during a day two days back: that date has a row from each zone's midnight.
        t.Exec("""
            CREATE TABLE moved AS SELECT * FROM system_day WHERE day = $day;
            UPDATE moved SET day = day + 3600;
            INSERT INTO system_day SELECT * FROM moved;
            DROP TABLE moved;
            """, ("$day", U(Day.AddDays(-2))));
        Assert.Equal(2L, t.Scalar("SELECT count(*) FROM system_day WHERE day >= $a AND day < $b", ("$a", U(Day.AddDays(-2))), ("$b", U(Day.AddDays(-1)))));

        var r = Build(t); // threw on the two rows for one date, and every day report for the next 40 days was blank
        Assert.Equal(5, r.StreakDays);
    }

    [Fact]
    public void The_same_weekday_over_recent_weeks_is_your_usual_for_that_weekday()
    {
        using var t = new TestDb();
        var (game, chrome, _) = Apps(t);
        foreach (int back in new[] { 7, 14, 21 })
        {
            long day = U(Day.AddDays(-back));
            for (int i = 0; i < 60; i++) t.Db.WriteMinute(Minute(day + 36000 + i * 60, app: i < 30 ? game : chrome));
            t.Db.AddAppHour(Hour(day + 36000, game, fg: 1800));
            t.Db.AddAppHour(Hour(day + 36000, chrome, fg: 1800));
        }
        t.Db.WriteMinute(Minute(U(Day.AddDays(-1)) + 36000, app: chrome)); // another weekday: not counted
        var usual = ReportBuilder.WeekdayUsualOf(t.Db, Day, t.Db.LoadApps().ToDictionary(a => a.Id), Settings());
        Assert.NotNull(usual);
        Assert.Equal((Day.DayOfWeek, 3, 3600.0, 1800.0), (usual.Day, usual.Days, usual.ActiveSec, usual.GamingSec));
        Assert.Null(ReportBuilder.WeekdayUsualOf(t.Db, Day.AddDays(1), t.Db.LoadApps().ToDictionary(a => a.Id), Settings()));
    }

    [Fact]
    public void Months_ago_is_each_games_steady_load_and_the_rest_from_four_months_back_to_one()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        for (int back = 40; back < 112; back += 6) // twelve days: half an hour at rest, then half an hour of play
            for (int i = 0; i < 60; i++)
            {
                long ts = U(Day.AddDays(-back).AddHours(12)) + i * 60;
                t.Db.WriteMinute(i < 30 ? Minute(ts, app: game, cpu: 40, gpu: 35, cpuLoad: 5, gpuLoad: 3)
                    : Minute(ts, app: game, cpu: 75, gpu: 70, cpuLoad: 40, gpuLoad: 90, gpuPower: 280, gpuApp: game));
            }
        for (int i = 0; i < 60; i++) t.Db.WriteMinute(Minute(U(Day.AddDays(-20).AddHours(12)) + i * 60, app: game, cpu: 60, gpu: 60, gpuLoad: 90, gpuApp: game)); // too recent
        var then = ReportBuilder.ThenHeatOf(t.Db, Day, id => id == game ? "Game" : "?");
        Assert.NotNull(then);
        var steady = Assert.Single(then.Steady);
        Assert.Equal(("Game", 12 * 20, 12, 70.0, 75.0, 280.0), (steady.App, steady.Minutes, steady.Days, steady.Gpu, steady.Cpu, steady.GpuPower)); // ten warm-up minutes a day left out
        Assert.Equal((40.0, 35.0), (then.Rest!.Cpu, then.Rest.Gpu));
        Assert.Equal((Day.AddDays(-120), Day.AddDays(-30)), (then.From, then.To));
        Assert.Null(ReportBuilder.ThenHeatOf(t.Db, Day.AddDays(-200), _ => "?")); // nothing ran then
    }

    [Fact]
    public void The_daily_heat_totals_read_each_day_as_a_report_reads_its_minutes()
    {
        // "Months ago" comes from heat_day, "now" from the report's own minutes: the two must measure alike.
        string path = Path.Combine(Support.TestEnvironment.NewFolder("heat-day"), "rigsight.db");
        Support.PcSim.Live(path, Day.AddDays(-6), Day, new Support.PcSim.Options { Seed = 11, GameChance = 1, Game = 0 });
        using var db = RigsightDb.OpenReader(path)!;
        for (var d = Day.AddDays(-6); d < Day; d = d.AddDays(1))
        {
            long f = U(d), to = U(d.AddDays(1));
            var (steady, rest, _) = ReportBuilder.SteadyOf(db.GetMinutes(f, to), id => id.ToString()!);
            var rows = db.GetHeatDays(f, to);
            foreach (var s in steady)
            {
                var row = Assert.Single(rows, r => r.App == s.AppId);
                Assert.Equal(s.Minutes, row.N);
                Assert.Equal(s.Gpu, row.GpuSum / row.GpuN, 6);
                Assert.Equal(s.GpuPower!.Value, row.PowerSum / row.PowerN, 6);
            }
            Assert.Equal(steady.Count, rows.Count(r => r.App != 0));
            var restRow = rows.SingleOrDefault(r => r.App == 0);
            Assert.Equal(rest?.Minutes ?? 0, restRow?.N ?? 0);
            if (rest is not null) Assert.Equal(rest.Gpu!.Value, restRow!.GpuSum / restRow.GpuN, 6);
        }
    }
}


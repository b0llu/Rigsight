using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Reports;

/// <summary>
/// What a report reads out of the minutes beyond totals: who made the heat, work done in the background, clocks
/// running down, cooling down, the fans; and out of the days before it: records, streaks, the same weekday, months ago.
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
    public void Clocks_well_under_their_cool_level_when_hot_are_throttling()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        for (int i = 0; i < 15; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpuLoad: 95, gpuMax: 70, gpuClock: 1800 + i)); // cool, at full clocks
        for (int i = 15; i < 19; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpuLoad: 95, gpuMax: 85 + i - 15, gpuClock: 1500));
        for (int i = 19; i < 22; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpuLoad: 95, gpuMax: 86, gpuClock: 1780)); // hot but not slowed
        var r = Build(t);
        Assert.NotNull(r.GpuThrottle);
        Assert.Equal((4, 85.0), (r.GpuThrottle.Minutes, r.GpuThrottle.FromTemp));
        Assert.InRange(r.GpuThrottle.DropPercent, 16, 18);
        Assert.Null(r.CpuThrottle);
    }

    [Fact]
    public void Cooling_down_is_timed_from_the_end_of_heavy_load_to_the_cool_line()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        int i = 0;
        for (; i < 12; i++) t.Db.WriteMinute(Minute(T0 + i * 60, app: game, gpuLoad: 95, gpu: 70));
        foreach (double temp in new[] { 65, 60, 55, 52, 49, 47 }) t.Db.WriteMinute(Minute(T0 + i++ * 60, app: game, gpuLoad: 5, gpu: temp));
        for (int k = 0; k < 5; k++) t.Db.WriteMinute(Minute(T0 + i++ * 60, app: game, gpuLoad: 95, gpu: 70)); // too short a run to count
        foreach (double temp in new[] { 60, 48 }) t.Db.WriteMinute(Minute(T0 + i++ * 60, app: game, gpuLoad: 5, gpu: temp));
        var r = Build(t);
        Assert.Equal((5.0, 1), (r.CooldownMinutes, r.CooldownCount));
    }

    [Fact]
    public void Each_fans_speed_is_read_at_idle_and_in_the_warm_band()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        long gpuFan = t.Db.FanId("/gpu-nvidia/0/fan/0", "GPU Fan", "RTX"), caseFan = t.Db.FanId("/lpc/it8686e/fan/1", "Fan #2", "B650");
        for (int i = 0; i < 40; i++)
        {
            bool idle = i < 20;
            t.Db.WriteMinute(Minute(T0 + i * 60, app: game, cpu: idle ? 40 : 65, gpu: idle ? 35 : 68, cpuLoad: idle ? 5 : 60, gpuLoad: idle ? 3 : 90));
            t.Db.WriteFanMinutes(T0 + i * 60, [(gpuFan, idle ? 800 : 1600, idle ? 850 : 1700), (caseFan, idle ? 700 : 900, idle ? 700 : 950)]);
        }
        var fans = Build(t).Fans;
        Assert.Equal(2, fans.Count);
        var gpu = fans.Single(f => f.Name == "GPU Fan");
        Assert.Equal((true, 800.0, 20, 1600.0, 20), (gpu.Gpu, gpu.IdleRpm, gpu.IdleMinutes, gpu.WarmRpm, gpu.WarmMinutes));
        var c = fans.Single(f => f.Name == "Fan #2");
        Assert.Equal((false, 700.0, 20, 900.0, 20), (c.Gpu, c.IdleRpm, c.IdleMinutes, c.WarmRpm, c.WarmMinutes)); // warm by the CPU's temperature
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
        var records = Build(t).Records;
        var cpu = Assert.Single(records);
        Assert.Equal((RecordKind.HottestCpu, "78°", 90), (cpu.Kind, cpu.Value, cpu.Days)); // 45 days: enough for three months, not a year

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
    public void Months_ago_is_the_stretch_from_four_months_back_to_one_with_enough_idle_days()
    {
        using var t = new TestDb();
        var (game, _, _) = Apps(t);
        for (int back = 40; back < 112; back += 6) // twelve days, half an hour idle each, and a bit under load
            for (int i = 0; i < 40; i++)
            {
                long ts = U(Day.AddDays(-back).AddHours(12)) + i * 60;
                t.Db.WriteMinute(i < 30 ? Minute(ts, app: game, cpu: 40, gpu: 35, cpuLoad: 5, gpuLoad: 3) : Minute(ts, app: game, cpu: 75, gpu: 70, cpuLoad: 60, gpuLoad: 90));
            }
        for (int i = 0; i < 60; i++) t.Db.WriteMinute(Minute(U(Day.AddDays(-20).AddHours(12)) + i * 60, app: game, cpu: 60, gpu: 60, cpuLoad: 5, gpuLoad: 3)); // too recent
        var then = ReportBuilder.ThenTempsOf(t.Db, Day);
        Assert.NotNull(then);
        Assert.Equal((40.0, 35.0, 360, 12), (then.Idle.Cpu, then.Idle.Gpu, then.Idle.Minutes, then.Days));
        Assert.Equal((75.0, 70.0), (then.Load.Cpu, then.Load.Gpu));
        Assert.Equal((Day.AddDays(-120), Day.AddDays(-30)), (then.From, then.To));
        Assert.Null(ReportBuilder.ThenTempsOf(t.Db, Day.AddDays(-100))); // too few days then
    }
}

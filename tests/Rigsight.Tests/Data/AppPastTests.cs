using Rigsight.Core.Settings;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Data;

/// <summary>
/// What is on record about one app for the box under its row on the Processes page, and the Timeline's line for
/// something ended from that page, written and read back.
/// </summary>
public sealed class AppPastTests
{
    // Mid-afternoon on a day of its own, so "today" has hours behind it whatever the clock says.
    private static readonly DateTime Now = new(2026, 6, 15, 15, 20, 0);
    private static readonly DateTime Today = Now.Date;

    private static long Hour(DateTime day, int hour) => TimeUtil.ToUnix(day.AddHours(hour));

    private static void Hours(RigsightDb db, long app, DateTime day, int from, int to, double memMB, double fg = 0, double bg = 3600)
    {
        for (int h = from; h <= to; h++)
            db.AddAppHour(new AppHour { Ts = Hour(day, h), AppId = app, FgSec = fg, BgSec = bg, MemSum = memMB * 60, MemN = 60 });
    }

    [Fact]
    public void An_app_with_a_past_has_every_fact()
    {
        using var t = new TestDb();
        long discord = t.Db.UpsertApp("Discord.exe", "Discord", null, AppCategory.Communication);
        long chrome = t.Db.UpsertApp("chrome.exe", "Google Chrome", null, AppCategory.Browser);
        for (int d = 1; d <= 5; d++) Hours(t.Db, discord, Today.AddDays(-d), 9, 18, 500);
        Hours(t.Db, chrome, Today.AddDays(-1), 9, 18, 3000);
        // Today: open since 9, growing; an hour in front, the rest behind other windows; one hour without a reading.
        for (int h = 9; h <= 15; h++)
            t.Db.AddAppHour(new AppHour { Ts = Hour(Today, h), AppId = discord, FgSec = h == 9 ? 3000 : 0, IdleSec = h == 9 ? 600 : 0, BgSec = h == 9 ? 0 : 1800, MinSec = h == 9 ? 0 : 1800,
                MemSum = h == 12 ? 0 : (500 + (h - 9) * 100) * 60, MemN = h == 12 ? 0 : 60 });
        Hours(t.Db, chrome, Today, 9, 15, 2800);
        t.Db.AddNetAppUse(new NetAppUse { Ts = Hour(Today, 10), App = discord, Down = 60_000_000, Up = 4_000_000 });
        t.Db.AddNetAppUse(new NetAppUse { Ts = Hour(Today, 14), App = discord, Down = 30_000_000, Up = 2_000_000 });
        t.Db.AddNetAppUse(new NetAppUse { Ts = Hour(Today.AddDays(-1), 14), App = discord, Down = 900_000_000 });
        t.Db.AddNetAppUse(new NetAppUse { Ts = Hour(Today, 14), App = chrome, Down = 700_000_000 });
        t.Db.InsertCrashes(
        [
            new CrashEvent { Ts = Hour(Today.AddDays(-3), 20), Kind = CrashKind.AppCrash, AppExe = "discord.exe" },
            new CrashEvent { Ts = Hour(Today.AddDays(-9), 20), Kind = CrashKind.AppHang, AppExe = "Discord.exe" },
            new CrashEvent { Ts = Hour(Today.AddDays(-40), 20), Kind = CrashKind.AppCrash, AppExe = "Discord.exe" }, // too long ago
            new CrashEvent { Ts = Hour(Today.AddDays(-2), 20), Kind = CrashKind.AppCrash, AppExe = "chrome.exe" },
        ]);

        var past = AppPast.Read(t.Db, "DISCORD.EXE", Now);
        Assert.Equal(16, past.Hours.Length); // midnight to the hour now
        Assert.All(past.Hours.Take(9), h => Assert.Null(h));
        Assert.Equal(new double?[] { 500, 600, 700, null, 900, 1000, 1100 }, past.Hours.Skip(9));
        Assert.True(past.HasChart);
        Assert.Equal(500, past.UsualMB); // the days before, not today's growth
        Assert.Equal(3600, past.FrontSec);
        Assert.Equal(6 * 3600, past.BackSec);
        Assert.Equal(96_000_000, past.NetBytes);
        Assert.Equal(2, past.Crashes);

        // One app's hours only, asked of the database by its ID.
        Assert.All(t.Db.GetAppHoursOf(discord, Hour(Today, 0), Hour(Today, 24)), h => Assert.Equal(discord, h.AppId));
        Assert.Equal(7, t.Db.GetAppHoursOf(discord, Hour(Today, 0), Hour(Today, 24)).Count);
        Assert.Equal(2, t.Db.GetAppHoursOf(chrome, Hour(Today, 14), Hour(Today, 16)).Count);
    }

    [Fact]
    public void A_usual_needs_three_days_of_memory_on_record()
    {
        using var t = new TestDb();
        long app = t.Db.UpsertApp("new.exe", "New", null, AppCategory.Other);
        Hours(t.Db, app, Today.AddDays(-1), 9, 20, 400);
        Hours(t.Db, app, Today.AddDays(-2), 9, 20, 400);
        Hours(t.Db, app, Today, 14, 15, 420);
        Assert.Null(AppPast.Read(t.Db, "new.exe", Now).UsualMB);

        Hours(t.Db, app, Today.AddDays(-3), 9, 9, 700);
        Assert.Equal((24 * 400 + 700) / 25.0, AppPast.Read(t.Db, "new.exe", Now).UsualMB!.Value, 6);

        // Hours open with no memory kept (a small app) don't count as days of it.
        long small = t.Db.UpsertApp("small.exe", "Small", null, AppCategory.Other);
        for (int d = 1; d <= 6; d++) t.Db.AddAppHour(new AppHour { Ts = Hour(Today.AddDays(-d), 10), AppId = small, BgSec = 3600 });
        t.Db.AddAppHour(new AppHour { Ts = Hour(Today, 10), AppId = small, BgSec = 3600 });
        var past = AppPast.Read(t.Db, "small.exe", Now);
        Assert.Null(past.UsualMB);
        Assert.False(past.HasChart);
        Assert.Equal(0, past.FrontSec);
        Assert.Equal(3600, past.BackSec);
        Assert.Null(past.NetBytes);
    }

    [Fact]
    public void An_app_never_recorded_has_nothing_but_its_crashes()
    {
        using var t = new TestDb();
        t.Db.InsertCrashes([new CrashEvent { Ts = Hour(Today.AddDays(-1), 20), Kind = CrashKind.AppCrash, AppExe = "ghost.exe" }]);
        var past = AppPast.Read(t.Db, "ghost.exe", Now);
        Assert.Equal(1, past.Crashes);
        Assert.Empty(past.Hours);
        Assert.False(past.HasChart);
        Assert.Null(past.UsualMB);
        Assert.Null(past.FrontSec);
        Assert.Null(past.BackSec);
        Assert.Null(past.NetBytes);
        Assert.Equal(0, AppPast.Read(t.Db, "nobody.exe", Now).Crashes);
    }

    [Fact]
    public void Its_cpu_time_is_read_by_the_hour_today_and_by_the_day_before()
    {
        using var t = new TestDb();
        long discord = t.Db.UpsertApp("Discord.exe", "Discord", null, AppCategory.Communication);
        int threads = Environment.ProcessorCount;
        // Recorded from 9 AM today (every process's total has a row), and on the 4 days before but not the 3 before those.
        for (int h = 9; h <= 15; h++) t.Db.AddAppCpuUse(new AppCpuUse(Hour(Today, h), AppCpuUse.AllApps, 100_000));
        foreach (int d in new[] { 1, 2, 3, 4, 8 })
            t.Db.AddAppCpuUse(new AppCpuUse(Hour(Today.AddDays(-d), 12), AppCpuUse.AllApps, 100_000));
        // Today: CPU time in three hours (the other recorded hours none); the hour now is 20 minutes old.
        t.Db.AddAppCpuUse(new AppCpuUse(Hour(Today, 10), discord, 36_000L * threads, 0, 0, 0, 3600, 3600L * 500));  // 1% of the CPU
        t.Db.AddAppCpuUse(new AppCpuUse(Hour(Today, 11), discord, 122_000, 0, 0, 0, 3600, 3600L * 500));
        t.Db.AddAppCpuUse(new AppCpuUse(Hour(Today, 15), discord, 12_000L * threads, 0, 0, 0, 1200, 1200L * 600)); // 1% of 20 minutes
        // The days before: three days with CPU time (a usual), one it didn't run.
        foreach (int d in new[] { 1, 2, 3 })
            t.Db.AddAppCpuUse(new AppCpuUse(Hour(Today.AddDays(-d), 12), discord, 72_000L * threads, 0, 0, 0, 7200, 7200L * 400)); // 2% of an hour

        var past = AppPast.Read(t.Db, "discord.exe", Now);
        Assert.Equal(16, past.CpuHourMs.Length);
        Assert.All(past.CpuHourMs.Take(9), h => Assert.Null(h)); // before recording started today
        Assert.Equal(new long?[] { 0, 36_000L * threads, 122_000, 0, 0, 0, 12_000L * threads }, past.CpuHourMs.Skip(9));
        Assert.Equal(1, past.CpuPct(10)!.Value, 6);
        Assert.Equal(1, past.CpuPct(15)!.Value, 6); // over the 20 minutes gone
        Assert.Equal(0, past.CpuPct(9));
        Assert.Null(past.CpuPct(3));
        Assert.Equal(2, past.CpuUsualPct!.Value, 6);

        Assert.Equal(AppPast.UsualDays, past.Days.Count);
        Assert.Equal(Today, past.Days[^1].Day);
        var today = past.Days[^1];
        Assert.Equal((true, 48_000L * threads + 122_000, 8400.0), (today.Recorded, today.CpuMs, today.RunSec));
        Assert.Equal((3600 * 500.0 * 2 + 1200 * 600) / 8400, today.MemMb!.Value, 6);
        Assert.True(past.Days[^2].Ran);
        Assert.Equal((true, false), (past.Days[^5].Recorded, past.Days[^5].Ran)); // recorded, but it didn't run
        Assert.False(past.Days[^6].Recorded);                                      // nothing recorded at all that day
        Assert.Equal(72_000.0 * threads, past.CpuUsualDayMs!.Value, 6);

        // Nothing recorded at all yet: nothing about CPU time.
        using var none = new TestDb();
        none.Db.UpsertApp("Discord.exe", "Discord", null, AppCategory.Communication);
        var bare = AppPast.Read(none.Db, "discord.exe", Now);
        Assert.Empty(bare.CpuHourMs);
        Assert.Empty(bare.Days);
        Assert.Null(bare.CpuUsualPct);
    }

    [Fact]
    public void Something_ended_from_Processes_is_kept_as_a_change_and_read_back()
    {
        using var t = new TestDb();
        var at = new DateTime(2026, 10, 10, 15, 12, 0);
        var ended = TaskEnded.Parse(TaskEnded.Format("Discord", "Discord.exe", 1126), at)!;
        Assert.Single(t.Db.InsertChanges([ended]));
        Assert.Empty(t.Db.InsertChanges([ended])); // the same second, the same app: once
        // Ended again later the same day: another line.
        Assert.Single(t.Db.InsertChanges([TaskEnded.Parse(TaskEnded.Format("Discord", "Discord.exe", 300), at.AddHours(2))!]));

        using var reader = t.Reader();
        var read = reader.GetChanges(0, long.MaxValue / 2);
        Assert.Equal(2, read.Count);
        Assert.Equal((at, ChangeKind.TaskEnded, "Discord ended from Processes", "It held 1.1 GB."), (read[0].Time, read[0].Kind, read[0].Title, read[0].Detail));
        Assert.Equal("It held 300 MB.", read[1].Detail);
        Assert.False(read[0].IsApproximate);
        Assert.Null(read[0].OnFrom);
    }
}

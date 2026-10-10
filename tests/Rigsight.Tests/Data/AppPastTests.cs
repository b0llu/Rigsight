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
        Assert.NotNull(past.FirstSeen);

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
        Assert.Null(past.FirstSeen);
        Assert.Equal(0, AppPast.Read(t.Db, "nobody.exe", Now).Crashes);
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

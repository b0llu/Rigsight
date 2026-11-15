using Rigsight.Core.Settings;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;

namespace Rigsight.Tests.Data;

/// <summary>What is on record about every app's memory at once, for the Memory page's list and today's fullest moment.</summary>
public sealed class MemoryPastTests
{
    // Mid-afternoon on a day of its own, so "today" has hours behind it whatever the clock says.
    private static readonly DateTime Now = new(2026, 6, 15, 15, 20, 0);
    private static readonly DateTime Today = Now.Date;

    private static long Hour(DateTime day, int hour) => TimeUtil.ToUnix(day.AddHours(hour));

    private static void Hours(RigsightDb db, long app, DateTime day, int from, int to, double memMB, int readings = 60)
    {
        for (int h = from; h <= to; h++)
            db.AddAppHour(new AppHour { Ts = Hour(day, h), AppId = app, BgSec = 3600, MemSum = memMB * readings, MemN = readings });
    }

    [Fact]
    public void Every_app_gets_the_usual_its_own_box_on_processes_would_say()
    {
        using var t = new TestDb();
        long discord = t.Db.UpsertApp("Discord.exe", "Discord", null, AppCategory.Communication);
        long chrome = t.Db.UpsertApp("chrome.exe", "Google Chrome", null, AppCategory.Browser);
        long fresh = t.Db.UpsertApp("new.exe", "New", null, AppCategory.Other);
        long old = t.Db.UpsertApp("old.exe", "Old", null, AppCategory.Other);
        long silent = t.Db.UpsertApp("silent.exe", "Silent", null, AppCategory.Other);
        for (int d = 1; d <= 5; d++) Hours(t.Db, discord, Today.AddDays(-d), 9, 18, 500);
        // Weighted by its readings: a short day of many readings counts for more than a long one of few.
        Hours(t.Db, chrome, Today.AddDays(-1), 9, 9, 4000, readings: 90);
        Hours(t.Db, chrome, Today.AddDays(-2), 9, 11, 3000, readings: 10);
        Hours(t.Db, chrome, Today.AddDays(-3), 9, 9, 3000, readings: 30);
        // Two days are too few; so are days from before the thirty; and hours without a reading aren't days at all.
        Hours(t.Db, fresh, Today.AddDays(-1), 9, 20, 400);
        Hours(t.Db, fresh, Today.AddDays(-2), 9, 20, 400);
        for (int d = 31; d <= 40; d++) Hours(t.Db, old, Today.AddDays(-d), 9, 18, 700);
        for (int d = 1; d <= 5; d++) Hours(t.Db, silent, Today.AddDays(-d), 9, 18, 0, readings: 0);
        // Today is not part of anyone's usual.
        Hours(t.Db, discord, Today, 9, 15, 1100);
        Hours(t.Db, fresh, Today, 9, 15, 2000);

        var past = MemoryPast.Read(t.Db, Now);
        Assert.Equal(["chrome.exe", "Discord.exe"], past.Usual.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(500, past.Usual["discord.EXE"]); // by the program's name, however it is spelled
        Assert.Equal((4000 * 90 + 3000 * 30 + 3000 * 30) / 150.0, past.Usual["chrome.exe"], 6);
        foreach (var exe in past.Usual.Keys) Assert.Equal(AppPast.Read(t.Db, exe, Now).UsualMB!.Value, past.Usual[exe], 6);
        Assert.Null(AppPast.Read(t.Db, "new.exe", Now).UsualMB);
    }

    [Fact]
    public void Todays_hours_run_from_midnight_to_the_hour_now()
    {
        using var t = new TestDb();
        long discord = t.Db.UpsertApp("Discord.exe", "Discord", null, AppCategory.Communication);
        long chrome = t.Db.UpsertApp("chrome.exe", "Google Chrome", null, AppCategory.Browser);
        for (int h = 9; h <= 15; h++)
            t.Db.AddAppHour(new AppHour { Ts = Hour(Today, h), AppId = discord, BgSec = 3600, MemSum = h == 12 ? 0 : (500 + (h - 9) * 100) * 60, MemN = h == 12 ? 0 : 60 });
        Hours(t.Db, chrome, Today.AddDays(-1), 9, 18, 3000);

        var past = MemoryPast.Read(t.Db, Now);
        var hours = past.Hours["discord.exe"];
        Assert.Equal(16, hours.Length);
        Assert.All(hours.Take(9), h => Assert.Null(h));
        Assert.Equal(new double?[] { 500, 600, 700, null, 900, 1000, 1100 }, hours.Skip(9));
        Assert.Equal(AppPast.Read(t.Db, "Discord.exe", Now).Hours, hours);
        Assert.False(past.Hours.ContainsKey("chrome.exe")); // nothing of it today
        Assert.Null(past.Fullest);
        Assert.Empty(past.FullestApps);
    }

    [Fact]
    public void The_fullest_minute_of_today_comes_with_the_two_apps_holding_the_most_in_its_hour()
    {
        using var t = new TestDb();
        long game = t.Db.UpsertApp("dota2.exe", "Dota 2", null, AppCategory.Game);
        long chrome = t.Db.UpsertApp("chrome.exe", "Google Chrome", null, AppCategory.Browser);
        long discord = t.Db.UpsertApp("Discord.exe", "Discord", null, AppCategory.Communication);
        Hours(t.Db, game, Today, 13, 14, 5200);
        Hours(t.Db, chrome, Today, 9, 15, 3100);
        Hours(t.Db, discord, Today, 9, 15, 900);
        Hours(t.Db, discord, Today, 10, 10, 9000); // the most of anything, but in another hour
        t.Db.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(Today.AddDays(-1).AddHours(20)), RamUsed = 15.5 }); // yesterday
        t.Db.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(Today.AddHours(10)), RamUsed = 9.1 });
        t.Db.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(Today.AddHours(14).AddMinutes(10)), RamUsed = 13.8 });
        t.Db.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(Today.AddHours(14).AddMinutes(30)), RamUsed = 13.8 }); // as full, later
        t.Db.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(Today.AddHours(15)), RamUsed = null });

        var past = MemoryPast.Read(t.Db, Now);
        Assert.Equal((Today.AddHours(14).AddMinutes(10), 13.8), past.Fullest);
        // No apps kept for that minute (as before 0.24.0): their averages over its hour, said as such.
        Assert.Equal(["Dota 2", "Google Chrome"], past.FullestApps);
        Assert.True(past.FullestByHour);
        Assert.Equal([("Dota 2", 5200.0), ("Google Chrome", 3100.0), ("Discord", 900.0)], past.FullestHeld);
        Assert.Null(past.FullestCommitPct);
        Assert.Equal((TimeUtil.ToUnix(Today.AddHours(14).AddMinutes(10)), 13.8), t.Db.GetFullestMinute(Hour(Today, 0), Hour(Today, 24)));
        Assert.Null(t.Db.GetFullestMinute(Hour(Today.AddDays(-5), 0), Hour(Today.AddDays(-4), 0)));
    }

    [Fact]
    public void The_fullest_minute_says_what_each_app_held_in_it_and_how_much_windows_had_promised()
    {
        using var t = new TestDb();
        long game = t.Db.UpsertApp("dota2.exe", "Dota 2", null, AppCategory.Game);
        long chrome = t.Db.UpsertApp("chrome.exe", "Google Chrome", null, AppCategory.Browser);
        long discord = t.Db.UpsertApp("Discord.exe", "Discord", null, AppCategory.Communication);
        long steam = t.Db.UpsertApp("steam.exe", "Steam", null, AppCategory.Other);
        long obs = t.Db.UpsertApp("obs64.exe", "OBS Studio", null, AppCategory.Other);
        Hours(t.Db, game, Today, 14, 14, 4000);
        Hours(t.Db, chrome, Today, 14, 14, 5000); // more over the hour, less in the minute
        long at = TimeUtil.ToUnix(Today.AddHours(14).AddMinutes(10));
        t.Db.WriteMinute(new SystemMinute { Ts = at, RamUsed = 14.8, CommitPct = 94 });
        t.Db.WriteAppMinutes(at, [new(at, game, 30, 90, 8200, 0), new(at, chrome, 4, 0, 3100, 0), new(at, discord, 1, 0, 600, 0),
            new(at, steam, 0, 0, 450, 0), new(at, obs, 6, 10, 380, 0)]);
        t.Db.WriteAppMinutes(at + 60, [new(at + 60, chrome, 4, 0, 9000, 0)]); // the next minute isn't this one

        var past = MemoryPast.Read(t.Db, Now);
        Assert.False(past.FullestByHour);
        Assert.Equal([("Dota 2", 8200.0), ("Google Chrome", 3100.0), ("Discord", 600.0), ("Steam", 450.0)], past.FullestHeld); // the biggest four
        Assert.Equal(["Dota 2", "Google Chrome"], past.FullestApps);
        Assert.Equal(94, past.FullestCommitPct);

        // A minute that kept only one app: the hour's averages, which name more.
        t.Db.WriteAppMinutes(at, [new(at, game, 30, 90, 8200, 0)]);
        past = MemoryPast.Read(t.Db, Now);
        Assert.True(past.FullestByHour);
        Assert.Equal(["Google Chrome", "Dota 2"], past.FullestApps);
    }

    [Fact]
    public void Each_app_has_its_peak_of_today_and_the_days_on_record_say_when_usuals_can_start()
    {
        using var t = new TestDb();
        long discord = t.Db.UpsertApp("Discord.exe", "Discord", null, AppCategory.Communication);
        long chrome = t.Db.UpsertApp("chrome.exe", "Google Chrome", null, AppCategory.Browser);
        t.Db.AddAppHour(new AppHour { Ts = Hour(Today, 9), AppId = discord, BgSec = 3600, MemSum = 400 * 60, MemN = 60, MemMax = 480 });
        t.Db.AddAppHour(new AppHour { Ts = Hour(Today, 10), AppId = discord, BgSec = 3600, MemSum = 500 * 60, MemN = 60, MemMax = 561 });
        t.Db.AddAppHour(new AppHour { Ts = Hour(Today.AddDays(-1), 10), AppId = discord, BgSec = 3600, MemSum = 900 * 60, MemN = 60, MemMax = 990 }); // yesterday

        // Today only: too soon, with none of the days before on record.
        var past = MemoryPast.Read(t.Db, Now);
        Assert.Equal(561, past.Peak["discord.exe"]);
        Assert.False(past.Peak.ContainsKey("chrome.exe"));
        Assert.Equal(1, past.RecordedDays);
        Assert.True(past.TooSoon);

        Hours(t.Db, chrome, Today.AddDays(-2), 9, 9, 3000);
        Hours(t.Db, chrome, Today.AddDays(-3), 9, 9, 3000);
        Assert.Equal(2, MemoryPast.Read(t.Db, Now).RecordedDays); // the most any one app has, not the days between them
        Hours(t.Db, discord, Today.AddDays(-3), 9, 9, 300);
        Hours(t.Db, discord, Today.AddDays(-4), 9, 9, 300);
        past = MemoryPast.Read(t.Db, Now);
        Assert.Equal(3, past.RecordedDays);
        Assert.False(past.TooSoon);
        Assert.True(past.Usual.ContainsKey("Discord.exe"));
        Assert.True(new MemoryPast().TooSoon);
    }

    // ── Since when an app has been above its usual ──

    private static double?[] Day(params (int Hour, double MB)[] recorded)
    {
        var hours = new double?[Now.Hour + 1];
        foreach (var (hour, mb) in recorded) hours[hour] = mb;
        return hours;
    }

    [Fact]
    public void Above_its_usual_since_the_first_hour_of_todays_latest_run_above_it()
    {
        // Usual 300 MB: marked from 480 MB (1.6 times), and stays marked down to 390 MB (1.3 times).
        var flat = Day((9, 301), (10, 309), (11, 322), (12, 334), (13, 512), (14, 548));
        Assert.Equal((Today.AddHours(13), false), MemoryPast.AboveSince(flat, 300, null, Now));
        // An earlier run, then back down: the latest run counts.
        Assert.Equal((Today.AddHours(13), false), MemoryPast.AboveSince(Day((9, 600), (10, 300), (13, 512), (14, 548)), 300, null, Now));
        // Came in over 1.6 times and stayed over 1.3 times: from when it came in, not from when it dipped.
        Assert.Equal((Today.AddHours(11), false), MemoryPast.AboveSince(Day((10, 400), (11, 500), (12, 420), (13, 410), (14, 520)), 300, null, Now));
        // Over 1.3 times but never 1.6 in those hours: it came in within the hour now.
        Assert.Equal((Today.AddHours(15), false), MemoryPast.AboveSince(Day((13, 420), (14, 410)), 300, null, Now));
        // An hour not recorded ends the run; the hour now (maybe not written yet) isn't looked at.
        Assert.Equal((Today.AddHours(14), false), MemoryPast.AboveSince(Day((12, 600), (14, 600), (15, 100)), 300, null, Now));
        Assert.Equal((Today.AddHours(15), false), MemoryPast.AboveSince([], 300, null, Now));
    }

    [Fact]
    public void An_app_above_its_usual_since_it_started_today_says_when_it_started()
    {
        var started = Today.AddHours(9).AddMinutes(12);
        var above = Day((9, 600), (10, 620), (11, 640), (12, 650), (13, 660), (14, 670));
        Assert.Equal((started, true), MemoryPast.AboveSince(above, 300, started, Now));
        // Started yesterday: from the first hour of the run.
        Assert.Equal((Today.AddHours(9), false), MemoryPast.AboveSince(above, 300, started.AddDays(-1), Now));
        // Started today but below its usual at first: from when it came above.
        Assert.Equal((Today.AddHours(11), false), MemoryPast.AboveSince(Day((9, 300), (10, 300), (11, 640), (12, 650), (13, 660), (14, 670)), 300, started, Now));
        // Started within the hour now, with nothing on record yet: since it started.
        var late = Today.AddHours(15).AddMinutes(3);
        Assert.Equal((late, true), MemoryPast.AboveSince(Day(), 300, late, Now));
    }
}

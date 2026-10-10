using Rigsight.Core.Settings;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;

namespace Rigsight.Tests.Data;

/// <summary>What is on record about every app's memory at once, for the Memory page's list and its cards.</summary>
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
        Assert.Equal(["Dota 2", "Google Chrome"], past.FullestApps);
        Assert.Equal((TimeUtil.ToUnix(Today.AddHours(14).AddMinutes(10)), 13.8), t.Db.GetFullestMinute(Hour(Today, 0), Hour(Today, 24)));
        Assert.Null(t.Db.GetFullestMinute(Hour(Today.AddDays(-5), 0), Hour(Today.AddDays(-4), 0)));
    }
}

using Rigsight.Agent.Ui;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The daily recap: when the PC is turned on (or wakes), the last day it was used, if that day's recap wasn't shown
/// yet and the day is over (5 AM the next morning). Not at midnight in a late session, and not "yesterday" when
/// yesterday the PC stayed off.
/// </summary>
public sealed class DailyRecapTests
{
    private static readonly DateTime Sat = new(2026, 9, 26);
    private static DateTime D(int day) => new(2026, 9, day);

    [Fact]
    public void Turned_on_the_next_day_it_recaps_yesterday()
    {
        Assert.Equal(D(25), DailyRecap.DayToRecap(Sat, lastUsedDay: D(25), recapped: D(24)));
        Assert.Equal("Yesterday on your PC", DailyRecap.Title(D(25), Sat));
    }

    [Fact]
    public void After_days_off_it_recaps_the_last_day_the_pc_was_used()
    {
        // Used on Wednesday, off Thursday and Friday, turned on Saturday.
        Assert.Equal(D(23), DailyRecap.DayToRecap(Sat, lastUsedDay: D(23), recapped: D(22)));
        Assert.Equal("Wednesday on your PC", DailyRecap.Title(D(23), Sat));
        Assert.Equal("12 September on your PC", DailyRecap.Title(D(12), Sat));
    }

    [Fact]
    public void A_day_is_recapped_once()
    {
        Assert.Null(DailyRecap.DayToRecap(Sat, lastUsedDay: D(25), recapped: D(25)));
        // Turned on again on Sunday without using the PC on Saturday: Friday isn't shown again.
        Assert.Null(DailyRecap.DayToRecap(D(27), lastUsedDay: D(25), recapped: D(25)));
    }

    [Fact]
    public void Nothing_to_recap_without_an_earlier_day_of_use()
    {
        Assert.Null(DailyRecap.DayToRecap(Sat, lastUsedDay: null, recapped: null));
        Assert.Null(DailyRecap.DayToRecap(Sat, lastUsedDay: Sat, recapped: null)); // only today so far
        Assert.Equal(D(25), DailyRecap.DayToRecap(Sat, lastUsedDay: D(25), recapped: null)); // never recapped before
    }

    [Fact]
    public void Settings_from_before_0_5_16_count_the_day_before_the_recap_was_shown()
    {
        // 0.5.15 recorded the day a recap was shown (it was always of the day before).
        var old = new RigsightSettings { LastRecapDay = "2026-09-26" };
        Assert.Equal(D(25), DailyRecap.Recapped(old));
        Assert.Equal(D(26), DailyRecap.DayToRecap(D(27), lastUsedDay: D(26), DailyRecap.Recapped(old)));
        var now = new RigsightSettings { LastRecapDay = "2026-09-26", RecappedDay = "2026-09-23" };
        Assert.Equal(D(23), DailyRecap.Recapped(now));
        Assert.Null(DailyRecap.Recapped(new RigsightSettings()));
    }

    [Fact]
    [Trait("Category", "Machine")] // run within ten minutes of signing in (right after a restart), it rightly fails
    public void Signing_in_is_told_from_the_agent_restarting()
    {
        // This test runs long after Windows (and Explorer) started: not a fresh sign-in.
        Assert.False(DailyRecap.JustSignedIn());
    }

    [Fact]
    public void Opening_a_folder_isnt_a_sign_in()
    {
        // Explorer starts another copy of itself for a moment to open a folder (as the Storage page's "Open" does):
        // it was taken for the desktop starting, i.e. a fresh sign-in. The desktop is the oldest Explorer.
        var now = Sat.AddHours(12);
        Assert.False(DailyRecap.JustSignedIn([now.AddHours(-4), now.AddMinutes(-1)], now));
        Assert.True(DailyRecap.JustSignedIn([now.AddMinutes(-2)], now));             // the desktop itself, just started
        Assert.True(DailyRecap.JustSignedIn([now.AddMinutes(-1), now.AddMinutes(-3)], now));
        Assert.False(DailyRecap.JustSignedIn([], now));                              // no desktop at all
    }

    [Theory]
    [InlineData(0, 10, 25)]  // 12:10 AM: yesterday isn't over, days before it are
    [InlineData(4, 59, 25)]
    [InlineData(5, 0, 26)]   // from 5 AM yesterday is over
    [InlineData(23, 30, 26)]
    public void A_day_is_over_at_5_AM_the_next_morning(int hour, int minute, int overBefore) =>
        Assert.Equal(D(overBefore), DailyRecap.OverBefore(Sat.AddHours(hour).AddMinutes(minute)));

    [Fact]
    public void Waking_the_pc_after_midnight_doesnt_recap_the_evening_still_going_on()
    {
        // Lid closed at 11:50 PM on Friday, opened at 12:10 AM: Friday isn't over. At 9 AM it is.
        Assert.Null(DailyRecap.DayToRecap(DailyRecap.OverBefore(Sat.AddMinutes(10)), lastUsedDay: D(25), recapped: D(24)));
        Assert.Equal(D(25), DailyRecap.DayToRecap(DailyRecap.OverBefore(Sat.AddHours(9)), lastUsedDay: D(25), recapped: D(24)));
        // A day before that, not recapped yet, can come at 12:10 AM: it's long over.
        Assert.Equal(D(23), DailyRecap.DayToRecap(DailyRecap.OverBefore(Sat.AddMinutes(10)), lastUsedDay: D(23), recapped: D(22)));
    }

    [Fact]
    public void Temperatures_by_the_hour_give_each_hours_average_highest_and_lowest()
    {
        var path = Path.Combine(TestEnvironment.NewFolder("temp-hours"), "rigsight.db");
        using var db = RigsightDb.OpenWriter(path);
        var day = DateTime.Today.AddDays(-3);
        long Unix(DateTime local) => TimeUtil.ToUnix(local);
        // 2 PM: 20 minutes at 50 and 20 at 70 (with a lowest and a highest each); nothing at 3 PM; one minute at 4 PM
        // from before the lowest was kept.
        for (int m = 0; m < 40; m++)
        {
            double t = m < 20 ? 50 : 70;
            db.WriteMinute(new SystemMinute { Ts = Unix(day.AddHours(14).AddMinutes(m)), CpuTemp = t, CpuTempMax = t + 8, CpuTempMin = t - 5, GpuTemp = 45, GpuTempMax = 46, GpuTempMin = 44, GpuHotMax = 60 + m, ActiveSec = 60 });
        }
        db.WriteMinute(new SystemMinute { Ts = Unix(day.AddHours(16).AddMinutes(10)), CpuTemp = 55, CpuTempMax = 61, GpuTemp = 47, GpuTempMax = 48, ActiveSec = 60 });
        var hours = db.GetTempHours(Unix(day), Unix(day.AddDays(1)));
        Assert.Equal([Unix(day.AddHours(14)), Unix(day.AddHours(16))], hours.Select(h => h.Ts));
        Assert.Equal((60.0, 78.0, 45.0), (hours[0].CpuTemp, hours[0].CpuTempMax, hours[0].CpuTempMin));
        Assert.Equal((45.0, 46.0, 44.0), (hours[0].GpuTemp, hours[0].GpuTempMax, hours[0].GpuTempMin));
        Assert.Equal(99, hours[0].GpuHotMax);
        Assert.Null(hours[0].GpuHotAvg); // none of its minutes kept the hot spot's average
        // An hour whose minutes all kept it has the average of those.
        for (int m = 0; m < 4; m++)
            db.WriteMinute(new SystemMinute { Ts = Unix(day.AddHours(18).AddMinutes(m)), GpuTemp = 50, GpuHotMax = 70 + m, GpuHotAvg = 60 + m, GpuMemMax = 80, GpuMemAvg = m < 3 ? 75 : null, ActiveSec = 60 });
        var evening = db.GetTempHours(Unix(day.AddHours(18)), Unix(day.AddHours(19))).Single();
        Assert.Equal((61.5, 73.0), (evening.GpuHotAvg, evening.GpuHotMax));
        Assert.Null(evening.GpuMemAvg); // one minute read the memory without keeping its average
        hours = db.GetTempHours(Unix(day), Unix(day.AddHours(17)));
        // An hour whose minutes didn't all keep their lowest has none (an average isn't a reading).
        Assert.Equal((55.0, 61.0, (double?)null), (hours[1].CpuTemp, hours[1].CpuTempMax, hours[1].CpuTempMin));
        db.WriteMinute(new SystemMinute { Ts = Unix(day.AddHours(16).AddMinutes(11)), CpuTemp = 57, CpuTempMax = 63, CpuTempMin = 51, ActiveSec = 60 });
        Assert.Null(db.GetTempHours(Unix(day), Unix(day.AddDays(1)))[1].CpuTempMin);
        Assert.Null(hours[1].GpuHotMax);
        Assert.Empty(db.GetTempHours(Unix(day.AddDays(1)), Unix(day.AddDays(2))));
        // Day by day (for a year): one row for the day, at its midnight.
        var days = db.GetTempHours(Unix(day.AddDays(-2)), Unix(day.AddDays(2)), byDay: true);
        Assert.Equal([Unix(day)], days.Select(d => d.Ts));
        Assert.Equal(78, days[0].CpuTempMax);
        Assert.InRange(days[0].CpuTemp!.Value, 59, 61);
        Assert.Null(days[0].CpuTempMin); // one of its minutes kept no lowest
    }

    [Fact]
    public void The_last_day_used_skips_days_with_only_a_few_minutes()
    {
        var path = Path.Combine(TestEnvironment.NewFolder("last-used"), "rigsight.db");
        using var db = RigsightDb.OpenWriter(path);
        long Unix(DateTime local) => TimeUtil.ToUnix(local);
        Assert.Null(db.LastUsedDayBefore(Unix(DateTime.Today)));
        void Use(DateTime day, int minutes)
        {
            for (int m = 0; m < minutes; m++) db.WriteMinute(new SystemMinute { Ts = Unix(day.AddHours(20).AddMinutes(m)), ActiveSec = 60 });
        }
        var today = DateTime.Today;
        Use(today.AddDays(-4), 60);
        Use(today.AddDays(-2), 3);   // switched on for three minutes: not a day of use
        Use(today, 30);              // today doesn't count
        Assert.Equal(Unix(today.AddDays(-4)), db.LastUsedDayBefore(Unix(today)));
        Assert.Equal(Unix(today.AddDays(-2)), db.LastUsedDayBefore(Unix(today), minActiveSec: 60));
    }
}

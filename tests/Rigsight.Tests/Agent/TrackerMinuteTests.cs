using Rigsight.Agent.Sensors;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>The per-minute system rows (system_minute) and their daily totals (system_day).</summary>
public class TrackerMinuteTests
{
    private static readonly KeyValues Typical = new()
    {
        CpuTemp = 55, CpuLoad = 20, CpuPower = 40, CpuVoltage = 1.1, GpuTemp = 48, GpuLoad = 10, GpuPower = 60,
        GpuHotSpot = 60, GpuMemJunction = 62, GpuVoltage = 0.9, RamUsed = 12,
    };

    [Fact]
    public void Nothing_is_written_before_the_first_minute_ends()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 50);
        Assert.Empty(rig.Minutes());
        Assert.Empty(rig.Hours());
    }

    [Fact]
    public void Each_finished_minute_is_one_row_at_its_start()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 185);
        var minutes = rig.Minutes();
        Assert.Equal([rig.Clock.Unix / 60 * 60 - 180, rig.Clock.Unix / 60 * 60 - 120, rig.Clock.Unix / 60 * 60 - 60], minutes.Select(m => m.Ts));
        Assert.All(minutes, m => Assert.Equal(0, m.Ts % 60));
        rig.AssertInvariants();
    }

    [Fact]
    public void A_minute_in_use_is_active_with_the_app_in_front()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 125);
        var minutes = rig.Minutes();
        Assert.Equal(2, minutes.Count);
        Assert.Equal(59, minutes[0].ActiveSec); // the first sample lands at :01
        Assert.Equal(60, minutes[1].ActiveSec);
        Assert.All(minutes, m => Assert.Equal(0, m.IdleSec));
        Assert.All(minutes, m => Assert.Equal(rig.AppId("code.exe"), m.FgApp));
    }

    [Fact]
    public void Time_away_is_idle_and_has_no_app_in_front()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 59);
        rig.Away("code.exe", 61);
        var m = rig.Minutes().Last();
        Assert.Equal(0, m.ActiveSec);
        Assert.Equal(60, m.IdleSec);
        Assert.Null(m.FgApp);
        rig.AssertInvariants();
    }

    [Theory]
    [InlineData(5, 4 * 60_000, true)]    // input 4 min ago, limit 5: still here
    [InlineData(5, 5 * 60_000, false)]   // exactly at the limit: away
    [InlineData(1, 59_000, true)]
    [InlineData(1, 60_000, false)]
    [InlineData(120, 119 * 60_000, true)]
    public void Idle_limit_follows_the_setting(int idleMinutes, uint idleMs, bool active)
    {
        var settings = new RigsightSettings();
        settings.Tracking.IdleMinutes = idleMinutes;
        using var rig = new TrackerRig(settings: settings);
        rig.Use("code.exe", 120, idleMs: idleMs);
        var m = rig.Minutes().Last();
        Assert.Equal(active ? 60 : 0, m.ActiveSec);
        Assert.Equal(active ? 0 : 60, m.IdleSec);
        Assert.Equal(active, rig.Tracker.Activity.Present);
    }

    [Fact]
    public void A_fullscreen_app_counts_as_in_use_without_input()
    {
        using var rig = new TrackerRig();
        rig.Use("movie.exe", 120, fullscreen: true, idleMs: 60 * 60_000);
        var m = rig.Minutes().Last();
        Assert.Equal(60, m.ActiveSec);
        Assert.Equal(rig.AppId("movie.exe"), m.FgApp);
        Assert.True(rig.Tracker.Activity.Fullscreen);
        Assert.True(rig.Tracker.Activity.Present);
    }

    [Fact]
    public void Fullscreen_counts_as_away_when_the_setting_is_off()
    {
        var settings = new RigsightSettings();
        settings.Tracking.FullscreenCountsAsActive = false;
        using var rig = new TrackerRig(settings: settings);
        rig.Use("movie.exe", 120, fullscreen: true, idleMs: 60 * 60_000);
        var m = rig.Minutes().Last();
        Assert.Equal(0, m.ActiveSec);
        Assert.Equal(60, m.IdleSec);
    }

    [Fact]
    public void A_locked_PC_is_away_with_no_app()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 59);
        rig.Use("code.exe", 61, locked: true);
        var m = rig.Minutes().Last();
        Assert.Equal(0, m.ActiveSec);
        Assert.Equal(60, m.IdleSec);
        Assert.Null(m.FgApp);
        Assert.Null(rig.Tracker.Activity.Exe);
        Assert.False(rig.Tracker.Activity.Present);
    }

    [Fact]
    public void Locked_even_with_fullscreen_is_away()
    {
        using var rig = new TrackerRig();
        rig.Use("game.exe", 120, fullscreen: true, locked: true);
        Assert.Equal(0, rig.Minutes().Last().ActiveSec);
    }

    [Fact]
    public void The_desktop_is_active_time_without_an_app()
    {
        using var rig = new TrackerRig();
        rig.Use(null, 120);
        var m = rig.Minutes().Last();
        Assert.Equal(60, m.ActiveSec);
        Assert.Null(m.FgApp);
        Assert.Empty(rig.Hours());
        Assert.Null(rig.Tracker.Activity.Name);
        Assert.True(rig.Tracker.Activity.Present);
    }

    [Fact]
    public void The_app_in_front_longest_owns_the_minute()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 59);
        rig.Use("chrome.exe", 20);
        rig.Use("code.exe", 40);
        rig.Use("code.exe", 1);
        Assert.Equal(rig.AppId("code.exe"), rig.Minutes()[1].FgApp);

        rig.Use("chrome.exe", 35);
        rig.Use("code.exe", 25);
        Assert.Equal(rig.AppId("chrome.exe"), rig.Minutes()[2].FgApp);
    }

    [Fact]
    public void Away_seconds_dont_decide_the_app_in_front()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 59);
        rig.Away("chrome.exe", 45);
        rig.Use("code.exe", 16);
        Assert.Equal(rig.AppId("code.exe"), rig.Minutes()[1].FgApp);
    }

    [Fact]
    public void Sensors_are_averaged_and_peaks_kept()
    {
        using var rig = new TrackerRig();
        rig.SensorEvery = 1;
        rig.Use("code.exe", 59);
        for (int i = 0; i < 60; i++)
        {
            rig.Keys = i % 2 == 0 ? Typical with { CpuTemp = 50, GpuTemp = 40, CpuLoad = 10, GpuLoad = 90, CpuPower = 30, GpuPower = 200, RamUsed = 10 }
                : Typical with { CpuTemp = 70, GpuTemp = 60, CpuLoad = 30, GpuLoad = 70, CpuPower = 50, GpuPower = 100, RamUsed = 14, GpuHotSpot = 85, GpuMemJunction = 90, CpuVoltage = 1.35, GpuVoltage = 1.05 };
            rig.Use("code.exe", 1);
        }
        rig.Use("code.exe", 1);
        var m = rig.Minutes()[1];
        Assert.Equal(60, m.CpuTemp!.Value, 6);
        Assert.Equal(70, m.CpuTempMax);
        Assert.Equal(50, m.GpuTemp!.Value, 6);
        Assert.Equal(60, m.GpuTempMax);
        Assert.Equal(20, m.CpuLoad!.Value, 6);
        Assert.Equal(80, m.GpuLoad!.Value, 6);
        Assert.Equal(40, m.CpuPower!.Value, 6);
        Assert.Equal(150, m.GpuPower!.Value, 6);
        Assert.Equal(12, m.RamUsed!.Value, 6);
        Assert.Equal(85, m.GpuHotMax);
        Assert.Equal(90, m.GpuMemMax);
        Assert.Equal(1.35, m.CpuVoltMax);
        Assert.Equal(1.05, m.GpuVoltMax);
        rig.AssertInvariants();
    }

    [Fact]
    public void Missing_sensors_are_stored_as_nothing_not_zero()
    {
        using var rig = new TrackerRig();
        rig.Keys = new KeyValues { CpuLoad = 12 };
        rig.Use("code.exe", 125);
        var m = rig.Minutes().Last();
        Assert.Null(m.CpuTemp);
        Assert.Null(m.CpuTempMax);
        Assert.Null(m.GpuTemp);
        Assert.Null(m.GpuTempMax);
        Assert.Null(m.GpuHotMax);
        Assert.Null(m.GpuMemMax);
        Assert.Null(m.GpuLoad);
        Assert.Null(m.CpuPower);
        Assert.Null(m.GpuPower);
        Assert.Null(m.RamUsed);
        Assert.Null(m.CpuVoltMax);
        Assert.Equal(12, m.CpuLoad);
        rig.AssertInvariants();
    }

    [Fact]
    public void A_sensor_missing_part_of_the_minute_averages_only_its_readings()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 59);
        rig.Keys = new KeyValues { CpuTemp = 80 };
        rig.Use("code.exe", 30);
        rig.Keys = new KeyValues();
        rig.Use("code.exe", 31);
        var m = rig.Minutes()[1];
        Assert.Equal(80, m.CpuTemp);
        Assert.Equal(80, m.CpuTempMax);
    }

    [Fact]
    public void Readings_while_away_still_count_for_the_system()
    {
        using var rig = new TrackerRig();
        rig.Keys = Typical with { CpuTemp = 90 };
        rig.Away("code.exe", 125);
        Assert.Equal(90, rig.Minutes().Last().CpuTempMax);
        Assert.Equal(0, rig.HoursOf("code.exe").CpuTempN); // but not for the app
    }

    [Fact]
    public void Active_and_idle_seconds_never_exceed_a_minute()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 125, dt: 2.5); // a slow sampler catching up
        var m = rig.Minutes().Last();
        Assert.Equal(60, m.ActiveSec);
        rig.Away("code.exe", 125, dt: 3);
        Assert.Equal(60, rig.Minutes().Last().IdleSec);
    }

    [Fact]
    public void A_minute_with_under_a_second_of_time_is_not_written()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 59, dt: 0); // just after waking from sleep
        rig.Use("code.exe", 1);
        Assert.Empty(rig.Minutes());
    }

    [Fact]
    public void Flush_writes_the_minute_in_progress()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 20);
        rig.Tracker.Flush(closeAllSessions: false);
        var m = Assert.Single(rig.Minutes());
        Assert.Equal(20, m.ActiveSec);
        Assert.Equal(20, rig.HoursOf("code.exe").FgSec);
        rig.AssertInvariants();
    }

    [Fact]
    public void Flushing_twice_doesnt_count_twice()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 20);
        rig.Tracker.Flush(closeAllSessions: false);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.Equal(20, rig.HoursOf("code.exe").FgSec);
        Assert.Equal(20, Assert.Single(rig.Minutes()).ActiveSec);
    }

    [Fact]
    public void A_minute_flushed_early_and_continued_keeps_all_its_time()
    {
        // Saved mid-minute (Windows signing out, then the sign-out is cancelled): the minute, written again when it
        // ends, still holds its first part.
        using var rig = new TrackerRig();
        rig.Use("code.exe", 20);
        rig.Tracker.Flush(closeAllSessions: false);
        rig.Use("code.exe", 40);
        var m = Assert.Single(rig.Minutes());
        Assert.Equal(59, m.ActiveSec);
        Assert.Equal(59, rig.HoursOf("code.exe").FgSec); // app time adds up (deltas)
        rig.AssertInvariants();
    }

    [Fact]
    public void Several_hours_of_mixed_use_keep_the_daily_row_exact()
    {
        using var rig = new TrackerRig(FakeClock.At(8));
        var rnd = new Random(3);
        string?[] apps = ["code.exe", "chrome.exe", "discord.exe", null, "game.exe"];
        for (int block = 0; block < 60; block++)
        {
            rig.Keys = Typical with { CpuTemp = 40 + rnd.Next(50), GpuTemp = 35 + rnd.Next(50), GpuLoad = rnd.Next(100) };
            var app = apps[rnd.Next(apps.Length)];
            if (rnd.Next(4) == 0) rig.Away(app, 60 + rnd.Next(400));
            else rig.Use(app, 60 + rnd.Next(400), fullscreen: app == "game.exe");
        }
        rig.Tracker.Flush(closeAllSessions: true);
        rig.AssertInvariants();

        var minutes = rig.Minutes();
        var day = Assert.Single(rig.Db.GetSystemDays(0, long.MaxValue)!);
        Assert.Equal(minutes.Count, day.Minutes);
        Assert.Equal(minutes.Sum(m => m.ActiveSec), day.ActiveSec);
        Assert.Equal(minutes.Sum(m => m.IdleSec), day.IdleSec);
        Assert.Equal(minutes.Max(m => m.CpuTempMax), day.CpuTempMax);
        Assert.All(minutes, m => Assert.InRange(m.ActiveSec + m.IdleSec, 1, 60));
    }
}

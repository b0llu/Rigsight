using Rigsight.Agent;
using Rigsight.Agent.Sensors;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>Pausing, sleep, midnight, clean-up of old history, and today's running totals.</summary>
public class TrackerDayTests
{
    // ── Pause ──

    [Fact]
    public void Paused_for_a_while_records_nothing_then_resumes_by_itself()
    {
        var settings = new RigsightSettings();
        using var rig = new TrackerRig(settings: settings);
        settings.Tracking.PausedUntil = rig.Clock.Unix + 600;
        rig.Keys = new KeyValues { CpuTemp = 90 };
        rig.Use("code.exe", 599);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Empty(rig.Minutes());
        Assert.Empty(rig.Hours());
        Assert.Empty(rig.Sessions());
        Assert.False(rig.HasApp("code.exe"));
        Assert.True(rig.Tracker.Activity.Paused);
        Assert.Null(rig.Tracker.Activity.Name);
        var today = rig.Tracker.Today();
        Assert.Equal(0, today.OnSec);
        Assert.Null(today.CpuPeak);

        rig.Use("code.exe", 120);
        Assert.False(rig.Tracker.Activity.Paused);
        Assert.Equal("code.exe", rig.Tracker.Activity.Exe);
        Assert.Equal(60, rig.Minutes().Single().ActiveSec);
        rig.AssertInvariants();
    }

    [Fact]
    public void Paused_until_resumed_stays_paused()
    {
        var settings = new RigsightSettings();
        settings.Tracking.PausedUntil = -1;
        using var rig = new TrackerRig(settings: settings);
        rig.Use("code.exe", 3 * 3600);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Empty(rig.Minutes());
        Assert.Empty(rig.Hours());
        Assert.True(rig.Tracker.Activity.Paused);

        settings.Tracking.PausedUntil = 0;
        rig.Use("code.exe", 119);
        Assert.Single(rig.Minutes());
    }

    [Fact]
    public void Tracking_turned_off_records_nothing()
    {
        var settings = new RigsightSettings();
        settings.Tracking.Enabled = false;
        using var rig = new TrackerRig(settings: settings);
        rig.Running.Add("big.exe");
        rig.Windows["big.exe"] = new(true, false);
        rig.Use("code.exe", 600);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Empty(rig.Minutes());
        Assert.Empty(rig.Hours());
        Assert.Empty(rig.Db.LoadApps());
        Assert.True(rig.Tracker.Activity.Paused);
    }

    [Fact]
    public void Pausing_mid_minute_saves_the_part_before()
    {
        var settings = new RigsightSettings();
        using var rig = new TrackerRig(settings: settings);
        rig.Use("code.exe", 89);
        settings.Tracking.PausedUntil = -1;
        rig.Use("code.exe", 60);
        Assert.Equal([59, 30], rig.Minutes().Select(m => m.ActiveSec));
    }

    [Fact]
    public void A_long_pause_ends_the_session_in_progress()
    {
        var settings = new RigsightSettings();
        using var rig = new TrackerRig(settings: settings);
        rig.Use("code.exe", 300);
        long paused = rig.Clock.Unix;
        settings.Tracking.PausedUntil = -1;
        rig.Use("code.exe", 15 * 60);
        var s = Assert.Single(rig.Ended).Row;
        Assert.Equal(paused, s.End);
        Assert.Equal(300, s.ActiveSec);
    }

    [Fact]
    public void An_app_closed_while_paused_still_ends_its_session()
    {
        var settings = new RigsightSettings();
        using var rig = new TrackerRig(settings: settings);
        rig.Use("code.exe", 120);
        settings.Tracking.PausedUntil = -1;
        rig.Use(null, 5);
        rig.Quit("code.exe");
        Assert.Equal(120, Assert.Single(rig.Ended).Row.ActiveSec);
    }

    [Fact]
    public void Readings_while_paused_dont_count()
    {
        var settings = new RigsightSettings();
        using var rig = new TrackerRig(settings: settings);
        rig.Keys = new KeyValues { CpuTemp = 50 };
        rig.Use("code.exe", 60);
        settings.Tracking.PausedUntil = -1;
        rig.Keys = new KeyValues { CpuTemp = 99 };
        rig.Use("code.exe", 60);
        settings.Tracking.PausedUntil = 0;
        rig.Keys = new KeyValues { CpuTemp = 55 };
        rig.Use("code.exe", 120);
        Assert.DoesNotContain(rig.Minutes(), m => m.CpuTempMax > 55);
        Assert.Equal(55, rig.Tracker.Today().CpuPeak);
    }

    // ── Sleep ──

    [Theory]
    [InlineData(0, 1000, 1.0)]
    [InlineData(0, 950, 0.95)]
    [InlineData(0, 10_000, 10.0)]
    [InlineData(0, 10_001, 0.0)]
    [InlineData(5000, 5000, 0.0)]
    [InlineData(0, 3_600_000, 0.0)]
    public void A_long_gap_between_samples_is_sleep_and_counts_nothing(long last, long now, double expected) =>
        Assert.Equal(expected, AgentContext.ActivityStep(last, now), 9);

    [Fact]
    public void Sleep_leaves_no_minutes_and_no_time()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 120);
        rig.Clock.Advance(8 * 3600);
        rig.Use("code.exe", 1, dt: 0);
        rig.Use("code.exe", 119);
        rig.Tracker.Flush(closeAllSessions: true);
        var minutes = rig.Minutes();
        Assert.Equal(6, minutes.Count);
        Assert.Equal(239, rig.HoursOf("code.exe").FgSec);
        Assert.Equal(8 * 3600, minutes[3].Ts - minutes[2].Ts);
        rig.AssertInvariants();
    }

    // ── Midnight ──

    [Fact]
    public void Today_starts_again_at_midnight()
    {
        using var rig = new TrackerRig(FakeClock.At(23, 58));
        rig.Keys = new KeyValues { CpuTemp = 85, GpuTemp = 80 };
        rig.Use("code.exe", 119);
        Assert.Equal(119, rig.Tracker.Today().ActiveSec);
        Assert.Equal(85, rig.Tracker.Today().CpuPeak);

        rig.Keys = new KeyValues { CpuTemp = 50, GpuTemp = 45 };
        rig.Use("chrome.exe", 121);
        var today = rig.Tracker.Today();
        Assert.Equal(121, today.ActiveSec);
        Assert.Equal(121, today.OnSec);
        Assert.Equal(50, today.CpuPeak);
        Assert.Equal(45, today.GpuPeak);
        Assert.Equal("Chrome", today.TopApp);
        Assert.Equal("Chrome", today.CpuPeakApp);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(2, rig.Db.GetSystemDays(0, long.MaxValue)!.Count);
        rig.AssertInvariants();
    }

    [Fact]
    public void The_last_minute_before_midnight_belongs_to_yesterday()
    {
        using var rig = new TrackerRig(FakeClock.At(23, 59));
        rig.Use("code.exe", 61);
        var m = Assert.Single(rig.Minutes());
        Assert.Equal(FakeClock.At(23, 59).ToUnixTimeSeconds(), m.Ts);
        var day = Assert.Single(rig.Db.GetSystemDays(0, long.MaxValue)!);
        Assert.Equal(TimeUtil.ToUnix(new DateTime(2026, 6, 10)), day.Day);
    }

    // ── Clean-up ──

    private static void FillDays(TrackerRig rig, int days)
    {
        var first = rig.Clock.Now;
        for (int d = 0; d < days; d++)
        {
            rig.Clock.Now = first.AddDays(d);
            rig.Use("code.exe", 1, dt: 0);
            rig.Use("code.exe", 300);
            rig.Quit("code.exe");
            rig.Tracker.Flush(closeAllSessions: true);
        }
    }

    [Fact]
    public void History_older_than_the_setting_is_deleted_at_the_first_save_of_a_day()
    {
        var settings = new RigsightSettings();
        settings.Tracking.KeepHistoryDays = 3;
        using var rig = new TrackerRig(FakeClock.At(12, 0, 0, day: 1), settings);
        rig.Db.UpsertDriveDay(new DriveDay { Day = TimeUtil.ToUnix(new DateTime(2026, 6, 1)), Drive = @"C:\", UsedGb = 1, TotalGb = 2 });
        rig.Db.UpsertDriveDay(new DriveDay { Day = TimeUtil.ToUnix(new DateTime(2026, 6, 9)), Drive = @"C:\", UsedGb = 1, TotalGb = 2 });
        FillDays(rig, 10); // 1–10 June

        long cutoff = TimeUtil.ToUnix(new DateTime(2026, 6, 7));
        Assert.All(rig.Minutes(), m => Assert.True(m.Ts >= cutoff));
        Assert.All(rig.Hours(), h => Assert.True(h.Ts >= cutoff));
        Assert.All(rig.Sessions(), s => Assert.True(s.Start >= cutoff));
        Assert.Equal(4, rig.Sessions().Count); // 7, 8, 9, 10 June
        Assert.Equal(4, rig.Db.GetSystemDays(0, long.MaxValue)!.Count);
        Assert.Single(rig.Db.GetDriveDays(0));
        rig.AssertInvariants();
    }

    [Fact]
    public void Keeping_history_forever_deletes_nothing()
    {
        var settings = new RigsightSettings();
        settings.Tracking.KeepHistoryDays = 0;
        using var rig = new TrackerRig(FakeClock.At(12, 0, 0, day: 1, month: 1, year: 2020), settings);
        FillDays(rig, 3);
        rig.Clock.Now = FakeClock.At(12);
        rig.Use("code.exe", 120);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(4, rig.Sessions().Count);
        Assert.Equal(4, rig.Db.GetSystemDays(0, long.MaxValue)!.Count);
        rig.AssertInvariants();
    }

    [Fact]
    public void Clean_up_across_a_month_keeps_the_monthly_totals_exact()
    {
        var settings = new RigsightSettings();
        settings.Tracking.KeepHistoryDays = 0;
        using var rig = new TrackerRig(FakeClock.At(12, 0, 0, day: 20, month: 5), settings);
        FillDays(rig, 30); // 20 May – 18 June
        settings.Tracking.KeepHistoryDays = 20;
        rig.Clock.Now = FakeClock.At(12, 0, 0, day: 19);
        rig.Use("code.exe", 120); // the first save of 19 June cleans up before 30 May
        rig.Tracker.Flush(closeAllSessions: true);

        long may = TimeUtil.ToUnix(new DateTime(2026, 5, 1));
        var mayTotal = rig.Db.GetAppMonths(0, long.MaxValue).Single(m => m.Month == may).FgSec;
        Assert.Equal(300 * 2, mayTotal); // 30 and 31 May are left
        rig.AssertInvariants();
    }

    [Fact]
    public void Clean_up_runs_once_a_day()
    {
        var settings = new RigsightSettings();
        settings.Tracking.KeepHistoryDays = 0;
        using var rig = new TrackerRig(FakeClock.At(1, 0, 0, day: 1), settings);
        FillDays(rig, 3);
        settings.Tracking.KeepHistoryDays = 1; // changed later the same day: waits for tomorrow
        rig.Use("code.exe", 120);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(3, rig.Db.GetSystemDays(0, long.MaxValue)!.Count);
        rig.Clock.Now = FakeClock.At(1, 0, 0, day: 4);
        rig.Use("code.exe", 61);
        Assert.Equal(2, rig.Db.GetSystemDays(0, long.MaxValue)!.Count);
    }

    // ── Clear history ──

    [Fact]
    public void Clear_history_empties_everything_and_starts_afresh()
    {
        using var rig = new TrackerRig();
        rig.Keys = new KeyValues { CpuTemp = 70 };
        rig.Use("code.exe", 600);
        rig.Tracker.Flush(closeAllSessions: true);
        rig.Use("chrome.exe", 30);
        rig.Tracker.ClearHistory();

        Assert.Empty(rig.Minutes());
        Assert.Empty(rig.Hours());
        Assert.Empty(rig.Sessions());
        Assert.Empty(rig.Db.GetSystemDays(0, long.MaxValue)!);
        Assert.Empty(rig.Db.GetAppMonths(0, long.MaxValue));
        var today = rig.Tracker.Today();
        Assert.Equal(0, today.OnSec);
        Assert.Equal(0, today.ActiveSec);
        Assert.Null(today.TopApp);
        Assert.Null(today.CpuPeak);

        rig.Use("chrome.exe", 150);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(150, rig.HoursOf("chrome.exe").FgSec);
        Assert.Equal(150, rig.Tracker.Today().ActiveSec);
        rig.AssertInvariants();
    }

    [Fact]
    public void Clear_history_keeps_the_apps()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 60);
        rig.Tracker.ClearHistory();
        Assert.True(rig.HasApp("code.exe"));
    }
}

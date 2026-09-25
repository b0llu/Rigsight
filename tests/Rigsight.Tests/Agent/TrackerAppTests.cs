using Rigsight.Agent.Sensors;
using Rigsight.Agent.Tracking;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>Per-app hourly totals (app_hour) and their monthly rollup (app_month).</summary>
public class TrackerAppTests
{
    [Fact]
    public void Time_in_front_is_counted_per_app()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 300);
        rig.Use("chrome.exe", 120);
        rig.Use("code.exe", 60);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.Equal(360, rig.HoursOf("code.exe").FgSec);
        Assert.Equal(120, rig.HoursOf("chrome.exe").FgSec);
        rig.AssertInvariants();
    }

    [Fact]
    public void Time_away_with_an_app_in_front_is_its_idle_time()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 100);
        rig.Away("code.exe", 200);
        rig.Tracker.Flush(closeAllSessions: false);
        var h = rig.HoursOf("code.exe");
        Assert.Equal(100, h.FgSec);
        Assert.Equal(200, h.IdleSec);
    }

    [Fact]
    public void Open_windows_of_other_apps_count_as_background_or_minimized()
    {
        using var rig = new TrackerRig();
        rig.Running.UnionWith(["chrome.exe", "spotify.exe"]);
        rig.Windows["chrome.exe"] = new WindowState(AnyVisible: true, AnyMinimized: true);
        rig.Windows["spotify.exe"] = new WindowState(AnyVisible: false, AnyMinimized: true);
        rig.Windows["code.exe"] = new WindowState(AnyVisible: true, AnyMinimized: false);
        rig.Use("code.exe", 300); // 60 process samples of 5 s
        rig.Tracker.Flush(closeAllSessions: false);

        Assert.Equal(300, rig.HoursOf("chrome.exe").BgSec);
        Assert.Equal(0, rig.HoursOf("chrome.exe").MinSec);
        Assert.Equal(300, rig.HoursOf("spotify.exe").MinSec);
        Assert.Equal(0, rig.HoursOf("spotify.exe").BgSec);
        // The app in front isn't also "in the background".
        Assert.Equal(0, rig.HoursOf("code.exe").BgSec);
        Assert.Equal(300, rig.HoursOf("code.exe").FgSec);
        rig.AssertInvariants();
    }

    [Fact]
    public void The_app_in_front_is_matched_whatever_the_case_of_its_name()
    {
        using var rig = new TrackerRig();
        rig.Windows["CODE.EXE"] = new WindowState(true, false);
        rig.Use("code.exe", 60);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.Equal(0, rig.HoursOf("code.exe").BgSec);
        Assert.Single(rig.Db.LoadApps());
    }

    [Fact]
    public void Resource_use_is_recorded_for_apps_that_matter()
    {
        using var rig = new TrackerRig();
        rig.Running.UnionWith(["big.exe", "busy.exe", "windowed.exe", "tiny.exe", "System", "Rigsight.Agent.exe"]);
        rig.Usage["big.exe"] = (0.1, 800);
        rig.Usage["busy.exe"] = (12, 40);
        rig.Usage["windowed.exe"] = (0, 20);
        rig.Usage["tiny.exe"] = (0.5, 30);
        rig.Usage["System"] = (3, 500);
        rig.Usage["Rigsight.Agent.exe"] = (1, 200);
        rig.Usage["code.exe"] = (4, 600);
        rig.Windows["windowed.exe"] = new WindowState(true, false);
        rig.Use("code.exe", 100); // 20 process samples
        rig.Tracker.Flush(closeAllSessions: false);

        var big = rig.HoursOf("big.exe");
        Assert.Equal(20, big.MemN);
        Assert.Equal(800, big.MemMax);
        Assert.Equal(800 * 20, big.MemSum, 6);
        Assert.Equal(20, big.CpuN);
        var busy = rig.HoursOf("busy.exe");
        Assert.Equal(12, busy.CpuMax);
        Assert.Equal(12 * 20, busy.CpuSum, 6);
        Assert.Equal(20, rig.HoursOf("windowed.exe").CpuN);
        Assert.Equal(20, rig.HoursOf("code.exe").MemN);
        Assert.False(rig.HasApp("tiny.exe"));             // small and quiet: not worth a row
        Assert.False(rig.HasApp("System"));               // kernel processes have no exe
        Assert.False(rig.HasApp("Rigsight.Agent.exe"));   // never itself
        rig.AssertInvariants();
    }

    [Theory]
    [InlineData(149.9, 1.99, false)]
    [InlineData(150, 0, true)]
    [InlineData(0, 2, true)]
    public void Memory_and_cpu_thresholds(double mem, double cpu, bool recorded)
    {
        using var rig = new TrackerRig();
        rig.Running.Add("bg.exe");
        rig.Usage["bg.exe"] = (cpu, mem);
        rig.Use(null, 10);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.Equal(recorded, rig.HasApp("bg.exe"));
    }

    [Fact]
    public void Readings_go_to_the_app_in_front_while_in_use()
    {
        using var rig = new TrackerRig();
        rig.SensorEvery = 1;
        rig.Keys = new KeyValues { CpuTemp = 60, GpuTemp = 50, GpuLoad = 30, GpuHotSpot = 70, CpuPower = 45, GpuPower = 150, CpuVoltage = 1.2, GpuVoltage = 0.95 };
        rig.Use("code.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 80, GpuTemp = 70, GpuLoad = 90, GpuHotSpot = 88, CpuPower = 90, GpuPower = 300, CpuVoltage = 1.4, GpuVoltage = 1.05 };
        rig.Use("code.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 95, GpuTemp = 90 };
        rig.Away("code.exe", 30);        // away: not the app's
        rig.Use("chrome.exe", 30);        // another app's
        rig.Tracker.Flush(closeAllSessions: false);

        var h = rig.HoursOf("code.exe");
        Assert.Equal(60, h.CpuTempN);
        Assert.Equal(30 * 60 + 30 * 80, h.CpuTempSum, 6);
        Assert.Equal(80, h.CpuTempMax);
        Assert.Equal(60, h.GpuTempN);
        Assert.Equal(70, h.GpuTempMax);
        Assert.Equal(60, h.GpuLoadN);
        Assert.Equal(30 * 30 + 30 * 90, h.GpuLoadSum, 6);
        Assert.Equal(88, h.GpuHotMax);
        Assert.Equal(90, h.CpuPowerMax);
        Assert.Equal(300, h.GpuPowerMax);
        Assert.Equal(1.4, h.CpuVoltMax);
        Assert.Equal(1.05, h.GpuVoltMax);
        Assert.Equal(95, rig.HoursOf("chrome.exe").CpuTempMax);
        rig.AssertInvariants();
    }

    [Fact]
    public void Time_is_split_at_the_hour()
    {
        using var rig = new TrackerRig(FakeClock.At(10, 55));
        rig.Use("code.exe", 600); // 10:55:01 → 11:05:00
        rig.Tracker.Flush(closeAllSessions: false);
        var hours = rig.Hours().OrderBy(h => h.Ts).ToList();
        Assert.Equal(2, hours.Count);
        Assert.Equal(FakeClock.At(10).ToUnixTimeSeconds(), hours[0].Ts);
        Assert.Equal(FakeClock.At(11).ToUnixTimeSeconds(), hours[1].Ts);
        Assert.Equal(299, hours[0].FgSec);
        Assert.Equal(301, hours[1].FgSec);
        rig.AssertInvariants();
    }

    [Fact]
    public void A_whole_day_of_use_makes_one_row_per_hour()
    {
        using var rig = new TrackerRig(FakeClock.At(0, 0, 0, day: 11));
        rig.Keys = new KeyValues { CpuTemp = 50, GpuTemp = 45, GpuLoad = 20 };
        rig.Use("code.exe", 24 * 3600 - 1);
        rig.Tracker.Flush(closeAllSessions: true);
        var hours = rig.Hours().OrderBy(h => h.Ts).ToList();
        Assert.Equal(24, hours.Count);
        Assert.All(hours.Skip(1), h => Assert.Equal(3600, h.FgSec));
        Assert.Equal(3599, hours[0].FgSec);
        Assert.Equal(24 * 60, rig.Minutes().Count);
        var day = Assert.Single(rig.Db.GetSystemDays(0, long.MaxValue)!);
        Assert.Equal(24 * 60, day.Minutes);
        rig.AssertInvariants();
    }

    [Fact]
    public void Months_are_rolled_up_separately()
    {
        using var rig = new TrackerRig(FakeClock.At(23, 50, 0, day: 30, month: 6));
        rig.Use("code.exe", 1200); // into 1 July
        rig.Tracker.Flush(closeAllSessions: true);
        var months = rig.Db.GetAppMonths(0, long.MaxValue);
        Assert.Equal(2, months.Count);
        Assert.Equal(600 - 1, months.Single(m => m.Month == TimeUtil.ToUnix(new DateTime(2026, 6, 1))).FgSec);
        Assert.Equal(601, months.Single(m => m.Month == TimeUtil.ToUnix(new DateTime(2026, 7, 1))).FgSec);
        rig.AssertInvariants();
    }

    [Fact]
    public void A_week_of_varied_use_keeps_every_rollup_exact()
    {
        using var rig = new TrackerRig(FakeClock.At(7, 0, 0, day: 26, month: 6));
        var rnd = new Random(11);
        string?[] apps = ["code.exe", "chrome.exe", "discord.exe", "spotify.exe", null, "eldenring.exe"];
        rig.Running.Add("spotify.exe");
        rig.Windows["spotify.exe"] = new WindowState(false, true);
        rig.Windows["discord.exe"] = new WindowState(true, false);
        for (int day = 0; day < 7; day++)
        {
            for (int block = 0; block < 25; block++)
            {
                rig.Keys = new KeyValues { CpuTemp = 35 + rnd.Next(55), GpuTemp = 30 + rnd.Next(55), GpuLoad = rnd.Next(100), CpuLoad = rnd.Next(100), RamUsed = 8 + rnd.Next(8) };
                var app = apps[rnd.Next(apps.Length)];
                if (rnd.Next(5) == 0) rig.Away(app, 30 + rnd.Next(900));
                else rig.Use(app, 30 + rnd.Next(900), fullscreen: app == "eldenring.exe");
            }
            rig.Tracker.Flush(closeAllSessions: false);
            rig.Clock.Now = FakeClock.At(7, 0, 0, day: 26, month: 6).AddDays(day + 1);
            rig.Use(null, 1, dt: 0);
        }
        rig.Tracker.Flush(closeAllSessions: true);
        rig.AssertInvariants();
        Assert.True(rig.Db.GetSystemDays(0, long.MaxValue)!.Count >= 7);
        Assert.True(rig.Db.GetAppMonths(0, long.MaxValue).Select(m => m.Month).Distinct().Count() == 2); // June and July
    }

    [Fact]
    public void Excluded_apps_are_never_recorded()
    {
        var settings = new RigsightSettings();
        settings.Tracking.ExcludedApps.Add("Secret.EXE");
        using var rig = new TrackerRig(settings: settings);
        rig.Running.Add("secret.exe");
        rig.Usage["secret.exe"] = (50, 4000);
        rig.Windows["secret.exe"] = new WindowState(true, false);
        rig.Use("secret.exe", 125, fullscreen: true);
        rig.Use("code.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);

        Assert.False(rig.HasApp("secret.exe"));
        Assert.DoesNotContain(rig.Minutes(), m => m.FgApp is not null && m.FgApp != rig.AppId("code.exe"));
        Assert.All(rig.Sessions(), s => Assert.Equal(rig.AppId("code.exe"), s.AppId));
        // The time itself still counts: the PC was in use.
        Assert.Equal(60, rig.Minutes()[1].ActiveSec);
        Assert.NotEqual("secret.exe", rig.Tracker.Activity.Exe);
    }

    [Fact]
    public void Excluded_app_in_front_shows_as_nothing_in_front()
    {
        var settings = new RigsightSettings();
        settings.Tracking.ExcludedApps.Add("secret.exe");
        using var rig = new TrackerRig(settings: settings);
        rig.Use("secret.exe", 10);
        Assert.Null(rig.Tracker.Activity.Exe);
        Assert.Null(rig.Tracker.Activity.Name);
        Assert.True(rig.Tracker.Activity.Present);
        Assert.Null(rig.Tracker.Today().TopApp);
    }

    [Fact]
    public void Excluding_an_app_later_stops_recording_it_from_then_on()
    {
        var settings = new RigsightSettings();
        using var rig = new TrackerRig(settings: settings);
        rig.Use("code.exe", 60);
        var excluded = settings.Clone();
        excluded.Tracking.ExcludedApps.Add("code.exe");
        rig.Tracker.SetSettings(excluded);
        rig.Use("code.exe", 120);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.Equal(60, rig.HoursOf("code.exe").FgSec);
    }

    [Fact]
    public void An_app_seen_first_is_added_once()
    {
        using var rig = new TrackerRig();
        rig.Use("newapp.exe", 30);
        rig.Use("NEWAPP.EXE", 30);
        rig.Tracker.Flush(closeAllSessions: false);
        var app = Assert.Single(rig.Db.LoadApps());
        Assert.Equal("newapp.exe", app.Exe);
        Assert.Equal("Newapp", app.Name);
        Assert.Equal(AppCategory.Other, app.Category);
        Assert.Equal(60, rig.HoursOf("newapp.exe").FgSec);
    }

    [Fact]
    public void Known_apps_get_their_category()
    {
        using var rig = new TrackerRig();
        rig.Use("chrome.exe", 10);
        rig.Use("code.exe", 10);
        rig.Use("svchost.exe", 10);
        var apps = rig.Db.LoadApps().ToDictionary(a => a.Exe);
        Assert.Equal(AppCategory.Browser, apps["chrome.exe"].Category);
        Assert.Equal(AppCategory.Development, apps["code.exe"].Category);
        Assert.Equal("Windows service", apps["svchost.exe"].Name);
    }
}

using Rigsight.Agent.Sensors;
using Rigsight.Agent.Tracking;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>Today's running totals (Home page, Today widget) and how they survive an agent restart.</summary>
public class TrackerTodayTests
{
    private static readonly (string, string, AppCategory) EldenRing = ("eldenring.exe", "Elden Ring", AppCategory.Game);

    [Fact]
    public void A_new_day_starts_at_zero()
    {
        using var rig = new TrackerRig();
        var t = rig.Tracker.Today();
        Assert.Equal(0, t.OnSec);
        Assert.Equal(0, t.ActiveSec);
        Assert.Equal(0, t.IdleSec);
        Assert.Null(t.TopApp);
        Assert.Equal(0, t.TopAppSec);
        Assert.Null(t.CpuPeak);
        Assert.Null(t.GpuPeak);
        Assert.Null(t.CpuPeakApp);
    }

    [Fact]
    public void On_active_and_away_add_up()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 400);
        rig.Away("code.exe", 200);
        rig.Use(null, 100);
        rig.Use("chrome.exe", 50, locked: true);
        var t = rig.Tracker.Today();
        Assert.Equal(750, t.OnSec);
        Assert.Equal(500, t.ActiveSec);
        Assert.Equal(250, t.IdleSec);
    }

    [Fact]
    public void The_most_used_app_is_the_top_app()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Use("code.exe", 300);
        rig.Use("eldenring.exe", 200);
        rig.Use("code.exe", 100);
        rig.Use("eldenring.exe", 250);
        rig.Away("code.exe", 1000); // away time isn't use
        var t = rig.Tracker.Today();
        Assert.Equal("Elden Ring", t.TopApp);
        Assert.Equal(450, t.TopAppSec);
    }

    [Fact]
    public void Peaks_name_the_app_doing_the_work_not_the_one_in_front()
    {
        // The game left running in the background made the heat; the browser in front did nothing much.
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Usage["eldenring.exe"] = (30, 4000);
        rig.GpuUsage["eldenring.exe"] = 92;
        rig.Usage["chrome.exe"] = (2, 800);
        rig.GpuUsage["chrome.exe"] = 3;
        rig.Running.Add("eldenring.exe");
        rig.Use("chrome.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 84, GpuTemp = 79, GpuLoad = 97 };
        rig.Use("chrome.exe", 120);
        var t = rig.Tracker.Today();
        Assert.Equal((84, "Elden Ring"), (t.CpuPeak, t.CpuPeakApp));
        Assert.Equal((79, "Elden Ring"), (t.GpuPeak, t.GpuPeakApp));
        Assert.Equal("While playing Elden Ring", t.CpuPeakLine);

        // The minutes keep it too (for the reports and insights), apart from the app in front.
        var minute = rig.Minutes().First(m => m.CpuTempMax == 84);
        Assert.Equal((rig.AppId("eldenring.exe"), rig.AppId("eldenring.exe"), rig.AppId("chrome.exe")), (minute.CpuApp, minute.GpuApp, minute.FgApp));
    }

    [Fact]
    public void Each_peak_names_the_app_working_that_part()
    {
        // A render hammering the CPU while a video plays on the GPU.
        using var rig = new TrackerRig();
        rig.Usage["blender.exe"] = (70, 3000);
        rig.Usage["vlc.exe"] = (2, 300);
        rig.GpuUsage["vlc.exe"] = 35;
        rig.GpuUsage["blender.exe"] = 4;
        rig.Running.Add("blender.exe");
        rig.Use("vlc.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 90, GpuTemp = 55, GpuLoad = 40 };
        rig.Use("vlc.exe", 60);
        Assert.Equal(("Blender", "Vlc"), (rig.Tracker.Today().CpuPeakApp, rig.Tracker.Today().GpuPeakApp));
    }

    [Fact]
    public void No_app_is_named_when_none_clearly_did_the_work()
    {
        using var rig = new TrackerRig();
        rig.Usage["code.exe"] = (12, 500);
        rig.Usage["chrome.exe"] = (10, 800);  // about as busy: either could have made the heat
        rig.Running.Add("chrome.exe");
        rig.Use("code.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 80 };
        rig.Use("code.exe", 60);
        var t = rig.Tracker.Today();
        Assert.Equal(80, t.CpuPeak);
        Assert.Null(t.CpuPeakApp);
        Assert.Equal($"At {TimeUtil.FromUnix(t.CpuPeakTime!.Value):h:mm tt}", t.CpuPeakLine);
        Assert.Null(t.GpuPeakLine); // no GPU reading at all
    }

    [Fact]
    public void A_light_load_names_no_app()
    {
        using var rig = new TrackerRig();
        rig.Usage["code.exe"] = (Tracker.MinCpuShare - 1, 500);
        rig.Use("code.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 45 };
        rig.Use("code.exe", 60);
        Assert.Null(rig.Tracker.Today().CpuPeakApp);
    }

    [Fact]
    public void Gpu_work_that_cant_be_put_down_to_an_app_names_none()
    {
        // A game with anti-cheat can't be read: the GPU is flat out, and all that can be seen is Windows drawing the desktop.
        using var rig = new TrackerRig();
        rig.GpuUsage["dwm.exe"] = 12;
        rig.Use("dwm.exe", 30);
        rig.Keys = new KeyValues { GpuTemp = 80, GpuLoad = 98 };
        rig.Use("dwm.exe", 60);
        Assert.Equal(80, rig.Tracker.Today().GpuPeak);
        Assert.Null(rig.Tracker.Today().GpuPeakApp);
    }

    [Fact]
    public void Apps_left_out_of_tracking_are_never_named()
    {
        var settings = new RigsightSettings();
        settings.Tracking.ExcludedApps.Add("secret.exe");
        using var rig = new TrackerRig(settings: settings);
        rig.Usage["secret.exe"] = (60, 500);
        rig.Usage["code.exe"] = (6, 500);
        rig.Running.Add("secret.exe");
        rig.Use("code.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 88 };
        rig.Use("code.exe", 60);
        Assert.Null(rig.Tracker.Today().CpuPeakApp); // not the next busiest either: that one didn't make the heat
        Assert.False(rig.HasApp("secret.exe"));
    }

    [Fact]
    public void A_one_second_handoff_doesnt_move_the_peak()
    {
        // Seen for real: an app restarting, Windows' display manager held the foreground for a second just as the CPU
        // peaked, and the peak was put down to it. What was doing the work is what counts.
        using var rig = new TrackerRig { SensorEvery = 1 }; // read every second, as the agent does
        rig.Usage["rigsight.exe"] = (20, 300);
        rig.GpuUsage["rigsight.exe"] = 30;
        rig.Usage["dwm.exe"] = (1, 100);
        rig.Use("rigsight.exe", 20);
        rig.Keys = new KeyValues { CpuTemp = 60, GpuTemp = 50 };
        rig.Use("rigsight.exe", 20);
        rig.Keys = new KeyValues { CpuTemp = 78, GpuTemp = 70 };
        rig.Use("dwm.exe", 1);
        rig.Keys = new KeyValues { CpuTemp = 61, GpuTemp = 50 };
        rig.Use("rigsight.exe", 20);
        Assert.Equal(78, rig.Tracker.Today().CpuPeak);
        Assert.Equal("Rigsight", rig.Tracker.Today().CpuPeakApp);
        Assert.Equal("Rigsight", rig.Tracker.Today().GpuPeakApp);

        rig.Use("rigsight.exe", 120); // the minute is written
        Assert.Equal(rig.AppId("rigsight.exe"), rig.Minutes().Single(m => m.CpuTempMax == 78).CpuApp); // what the insight names

        rig.Restart(); // rebuilt from the database: the same app
        Assert.Equal(78, rig.Tracker.Today().CpuPeak);
        Assert.Equal("Rigsight", rig.Tracker.Today().CpuPeakApp);
        Assert.Equal("Rigsight", rig.Tracker.Today().GpuPeakApp);
        Assert.Equal(rig.Tracker.Today().CpuPeakTime, rig.Minutes().Single(m => m.CpuTempMax == 78).Ts); // to the minute
    }

    [Fact]
    public void An_equal_reading_later_doesnt_take_the_peak()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 80 };
        rig.Use("code.exe", 60);
        rig.Running.Remove("code.exe");
        rig.Use("chrome.exe", 120);
        Assert.Equal("Code", rig.Tracker.Today().CpuPeakApp);
    }

    [Fact]
    public void A_category_picked_after_the_peak_changes_its_words_at_once_and_after_a_restart()
    {
        using var rig = new TrackerRig();
        rig.Usage["someapp.exe"] = (60, 900);
        rig.Use("someapp.exe", 30);
        rig.Keys = new KeyValues { CpuTemp = 82 };
        rig.Use("someapp.exe", 120);
        Assert.Equal("While Someapp was running", rig.Tracker.Today().CpuPeakLine);

        var settings = new RigsightSettings();
        settings.AppCategories["someapp.exe"] = AppCategory.Media;
        rig.Tracker.SetSettings(settings);
        Assert.Equal("While Someapp was playing", rig.Tracker.Today().CpuPeakLine);

        rig.Tracker.Flush(closeAllSessions: true);
        rig.Restart();
        rig.Tracker.SetSettings(settings);
        Assert.Equal((AppCategory.Media, "While Someapp was playing"), (rig.Tracker.Today().CpuPeakCategory, rig.Tracker.Today().CpuPeakLine));
    }

    [Fact]
    public void Names_and_categories_chosen_by_the_user_are_shown()
    {
        var settings = new RigsightSettings();
        settings.AppNames["code.exe"] = "My Editor";
        settings.AppCategories["code.exe"] = AppCategory.Game;
        using var rig = new TrackerRig(settings: settings);
        rig.Use("CODE.EXE", 30);
        rig.Keys = new KeyValues { CpuTemp = 75 };
        rig.Use("CODE.EXE", 270);
        var t = rig.Tracker.Today();
        Assert.Equal("My Editor", t.TopApp);
        Assert.Equal("My Editor", t.CpuPeakApp);
        Assert.Equal("While playing My Editor", t.CpuPeakLine);
        Assert.Equal("My Editor", rig.Tracker.Activity.Name);
        Assert.Equal(AppCategory.Game, rig.Tracker.Activity.Category);
        // The saved record keeps its own name: renaming is the user's view of it.
        Assert.Equal("CODE", rig.Db.LoadApps().Single().Name);
    }

    [Fact]
    public void Renaming_an_app_applies_at_once()
    {
        var settings = new RigsightSettings();
        using var rig = new TrackerRig(settings: settings);
        rig.Use("code.exe", 60);
        var renamed = settings.Clone();
        renamed.AppNames["code.exe"] = "Work";
        rig.Tracker.SetSettings(renamed);
        Assert.Equal("Work", rig.Tracker.Today().TopApp);
        rig.Use("code.exe", 1);
        Assert.Equal("Work", rig.Tracker.Activity.Name);
    }

    [Fact]
    public void A_restart_continues_todays_totals()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Keys = new KeyValues { CpuTemp = 65, GpuTemp = 60 };
        rig.Use("code.exe", 1800);
        rig.Keys = new KeyValues { CpuTemp = 84, GpuTemp = 77 };
        rig.Use("eldenring.exe", 3600, fullscreen: true);
        rig.Keys = new KeyValues { CpuTemp = 50, GpuTemp = 45 };
        rig.Away("code.exe", 900);
        var before = rig.Tracker.Today();

        rig.Restart();
        var after = rig.Tracker.Today();
        // Minutes are stored whole: at most a minute apart.
        Assert.InRange(after.OnSec, before.OnSec - 60, before.OnSec + 60);
        Assert.InRange(after.ActiveSec, before.ActiveSec - 60, before.ActiveSec + 1);
        Assert.InRange(after.IdleSec, before.IdleSec - 60, before.IdleSec + 1);
        Assert.Equal(before.TopApp, after.TopApp);
        Assert.Equal(before.TopAppSec, after.TopAppSec);
        Assert.Equal(before.CpuPeak, after.CpuPeak);
        Assert.Equal(before.CpuPeakApp, after.CpuPeakApp);
        Assert.Equal(before.GpuPeak, after.GpuPeak);
        Assert.Equal(before.GpuPeakApp, after.GpuPeakApp);

        rig.Use("eldenring.exe", 600, fullscreen: true);
        Assert.Equal(after.ActiveSec + 600, rig.Tracker.Today().ActiveSec);
        Assert.Equal(before.TopAppSec + 600, rig.Tracker.Today().TopAppSec);
    }

    [Fact]
    public void Todays_peak_is_the_same_after_a_restart()
    {
        // The hottest moment came while nobody was at the PC (a render left running): it's today's peak, and it
        // mustn't change just because the agent restarted.
        using var rig = new TrackerRig();
        rig.Keys = new KeyValues { CpuTemp = 60 };
        rig.Use("code.exe", 300);
        rig.Keys = new KeyValues { CpuTemp = 95 };
        rig.Away("code.exe", 300);
        rig.Use(null, 300);
        var before = rig.Tracker.Today();
        Assert.Equal(95, before.CpuPeak);
        rig.Restart();
        var after = rig.Tracker.Today();
        Assert.Equal(before.CpuPeak, after.CpuPeak);
        Assert.Equal(before.CpuPeakApp, after.CpuPeakApp);
    }

    /// <summary>
    /// Home's peak for today is the one the day's report shows (and the Temperatures page): the hottest reading of
    /// the day with the app doing that part's work, however the PC was being used when it came. Home once counted only
    /// the time someone was at the PC, so a peak while away showed on one page and not the other.
    /// </summary>
    [Theory]
    [InlineData("in use")]
    [InlineData("away")]
    [InlineData("locked")]
    [InlineData("nothing in front")]
    [InlineData("in use, then hotter while away")]
    [InlineData("away, then hotter in use")]
    public void Home_shows_the_days_peak_as_the_report_does(string when)
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Usage["eldenring.exe"] = (40, 4000);
        rig.GpuUsage["eldenring.exe"] = 90;
        rig.Running.Add("eldenring.exe");
        rig.Keys = new KeyValues { CpuTemp = 55, GpuTemp = 50, GpuLoad = 20 };
        rig.Use("code.exe", 300);
        void Hot(double cpu, double gpu, Action run) { rig.Keys = new KeyValues { CpuTemp = cpu, GpuTemp = gpu, GpuLoad = 95 }; run(); }
        switch (when)
        {
            case "in use": Hot(84, 77, () => rig.Use("code.exe", 120)); break;
            case "away": Hot(88, 79, () => rig.Away("code.exe", 120)); break;
            case "locked": Hot(90, 80, () => rig.Use("code.exe", 120, locked: true)); break;
            case "nothing in front": Hot(91, 81, () => rig.Use(null, 120)); break;
            case "in use, then hotter while away":
                Hot(80, 70, () => rig.Use("code.exe", 120));
                Hot(87, 78, () => rig.Away("code.exe", 120));
                break;
            case "away, then hotter in use":
                Hot(80, 70, () => rig.Away("code.exe", 120));
                Hot(86, 76, () => rig.Use("code.exe", 120));
                break;
        }
        rig.Keys = new KeyValues { CpuTemp = 50, GpuTemp = 45, GpuLoad = 5 };
        rig.Use("code.exe", 180);
        rig.Tracker.Flush(closeAllSessions: true);

        var day = rig.Clock.Now.LocalDateTime.Date;
        var report = Rigsight.Core.Reports.ReportBuilder.BuildRaw(rig.Db, Rigsight.Core.Reports.ReportRange.Day, day, day.AddDays(1),
            rig.Db.LoadApps().ToDictionary(a => a.Id), rig.Settings);
        void Same(Rigsight.Core.Protocol.TodayInfo t)
        {
            Assert.Equal((report.CpuTempPeak!.Value, report.CpuTempPeak.App), (t.CpuPeak!.Value, t.CpuPeakApp));
            Assert.Equal((report.GpuTempPeak!.Value, report.GpuTempPeak.App), (t.GpuPeak!.Value, t.GpuPeakApp));
            Assert.Equal("Elden Ring", t.CpuPeakApp);
            Assert.Equal("While playing Elden Ring", t.CpuPeakLine);
        }
        Same(rig.Tracker.Today());
        rig.Restart();
        Same(rig.Tracker.Today());
    }

    [Fact]
    public void A_restart_the_next_day_starts_at_zero()
    {
        using var rig = new TrackerRig(FakeClock.At(20));
        rig.Use("code.exe", 600);
        rig.Tracker.Flush(closeAllSessions: true);
        rig.Clock.Now = FakeClock.At(8, 0, 0, day: 11);
        rig.Restart();
        var t = rig.Tracker.Today();
        Assert.Equal(0, t.OnSec);
        Assert.Null(t.TopApp);
    }

    [Fact]
    public void A_restart_keeps_sessions_already_ended()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 300);
        rig.Restart();
        rig.Use("code.exe", 300);
        rig.Restart();
        Assert.Equal(2, rig.Sessions().Count);
        Assert.All(rig.Sessions(), s => Assert.Equal(300, s.ActiveSec));
        rig.AssertInvariants();
    }
}

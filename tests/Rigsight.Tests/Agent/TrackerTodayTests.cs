using Rigsight.Agent.Sensors;
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
    public void Peaks_name_the_app_in_front_and_its_category()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Keys = new KeyValues { CpuTemp = 60, GpuTemp = 50 };
        rig.Use("code.exe", 120);
        rig.Keys = new KeyValues { CpuTemp = 88, GpuTemp = 64 };
        rig.Use("eldenring.exe", 120);
        rig.Keys = new KeyValues { CpuTemp = 70, GpuTemp = 79 };
        rig.Use("chrome.exe", 120);
        rig.Keys = new KeyValues { CpuTemp = 40, GpuTemp = 40 };
        rig.Use("code.exe", 120);
        var t = rig.Tracker.Today();
        Assert.Equal(88, t.CpuPeak);
        Assert.Equal("Elden Ring", t.CpuPeakApp);
        Assert.Equal(AppCategory.Game, t.CpuPeakCategory);
        Assert.Equal(79, t.GpuPeak);
        Assert.Equal("Chrome", t.GpuPeakApp);
        Assert.Equal(AppCategory.Browser, t.GpuPeakCategory);
        Assert.Equal("while playing Elden Ring", t.CpuPeakWhile);
    }

    [Fact]
    public void An_equal_reading_later_doesnt_take_the_peak()
    {
        using var rig = new TrackerRig();
        rig.Keys = new KeyValues { CpuTemp = 80 };
        rig.Use("code.exe", 60);
        rig.Use("chrome.exe", 60);
        Assert.Equal("Code", rig.Tracker.Today().CpuPeakApp);
    }

    [Fact]
    public void Names_and_categories_chosen_by_the_user_are_shown()
    {
        var settings = new RigsightSettings();
        settings.AppNames["code.exe"] = "My Editor";
        settings.AppCategories["code.exe"] = AppCategory.Game;
        using var rig = new TrackerRig(settings: settings);
        rig.Keys = new KeyValues { CpuTemp = 75 };
        rig.Use("CODE.EXE", 300);
        var t = rig.Tracker.Today();
        Assert.Equal("My Editor", t.TopApp);
        Assert.Equal("My Editor", t.CpuPeakApp);
        Assert.Equal(AppCategory.Game, t.CpuPeakCategory);
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
        Assert.Equal(before.CpuPeakCategory, after.CpuPeakCategory);
        Assert.Equal(before.GpuPeak, after.GpuPeak);
        Assert.Equal(before.GpuPeakApp, after.GpuPeakApp);

        rig.Use("eldenring.exe", 600, fullscreen: true);
        Assert.Equal(after.ActiveSec + 600, rig.Tracker.Today().ActiveSec);
        Assert.Equal(before.TopAppSec + 600, rig.Tracker.Today().TopAppSec);
    }

    [Fact]
    public void Todays_peak_is_the_same_after_a_restart()
    {
        // The hottest moment came while nobody was at the PC (a render left running): the peak shown today
        // mustn't change just because the agent restarted.
        using var rig = new TrackerRig();
        rig.Keys = new KeyValues { CpuTemp = 60 };
        rig.Use("code.exe", 300);
        rig.Keys = new KeyValues { CpuTemp = 95 };
        rig.Away("code.exe", 300);
        rig.Use(null, 300);
        var before = rig.Tracker.Today();
        rig.Restart();
        var after = rig.Tracker.Today();
        Assert.Equal(before.CpuPeak, after.CpuPeak);
        Assert.Equal(before.CpuPeakApp, after.CpuPeakApp);
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

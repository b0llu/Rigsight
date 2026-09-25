using Rigsight.Agent.Sensors;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>Sessions: a stretch of using one app, ended by quitting it or by a 10-minute break.</summary>
public class TrackerSessionTests
{
    private static readonly (string, string, AppCategory) EldenRing = ("eldenring.exe", "Elden Ring", AppCategory.Game);

    [Fact]
    public void Quitting_an_app_ends_its_session_at_once()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        long start = rig.Clock.Unix;
        rig.Use("eldenring.exe", 1800, fullscreen: true);
        long last = rig.Clock.Unix;
        rig.Quit("eldenring.exe");

        var (row, app) = Assert.Single(rig.Ended);
        Assert.Equal("eldenring.exe", app.Exe);
        Assert.Equal(start, row.Start);
        Assert.Equal(last, row.End);
        Assert.Equal(1800, row.ActiveSec);
        Assert.True(row.IsGame);
        var saved = Assert.Single(rig.Sessions());
        Assert.Equal(row.Start, saved.Start);
        Assert.Equal(row.End, saved.End);
        Assert.True(saved.IsGame);
    }

    [Fact]
    public void A_session_ends_once_whatever_happens_after()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Use("eldenring.exe", 600);
        rig.Quit("eldenring.exe");
        rig.Quit("eldenring.exe");
        rig.Tracker.Flush(closeAllSessions: true);
        rig.Tracker.Flush(closeAllSessions: true);
        rig.Use(null, 1200);
        Assert.Single(rig.Ended);
        Assert.Single(rig.Sessions());
    }

    [Fact]
    public void Alt_tabbing_briefly_keeps_one_session_per_app()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Use("eldenring.exe", 900);
        rig.Use("discord.exe", 300);
        rig.Use("eldenring.exe", 900);
        rig.Tracker.Flush(closeAllSessions: true);

        Assert.Equal(2, rig.Ended.Count);
        var game = rig.Ended.Single(e => e.App.Exe == "eldenring.exe").Row;
        Assert.Equal(1800, game.ActiveSec);
        Assert.Equal(2100, game.End - game.Start);
        Assert.Equal(300, rig.Ended.Single(e => e.App.Exe == "discord.exe").Row.ActiveSec);
    }

    [Fact]
    public void A_break_over_ten_minutes_starts_a_new_session()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Use("eldenring.exe", 600);
        long firstEnd = rig.Clock.Unix;
        rig.Use("chrome.exe", 11 * 60 + 60);
        // Ended while chrome was in front, at the minute after the break passed 10 minutes.
        var first = Assert.Single(rig.Ended);
        Assert.Equal(firstEnd, first.Row.End);
        rig.Use("eldenring.exe", 300);
        rig.Tracker.Flush(closeAllSessions: true);
        var games = rig.Ended.Where(e => e.App.Exe == "eldenring.exe").Select(e => e.Row).ToList();
        Assert.Equal(2, games.Count);
        Assert.Equal([600.0, 300.0], games.Select(g => g.ActiveSec));
    }

    [Fact]
    public void A_break_of_exactly_ten_minutes_continues_the_session()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 600);
        rig.Use("chrome.exe", 600);
        rig.Use("code.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(660, rig.Ended.Single(e => e.App.Exe == "code.exe").Row.ActiveSec);
    }

    [Fact]
    public void Time_away_doesnt_count_and_a_long_absence_ends_the_session()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 300);
        long lastActive = rig.Clock.Unix;
        rig.Away("code.exe", 5 * 60);
        Assert.Empty(rig.Ended);
        rig.Away("code.exe", 7 * 60);
        var s = Assert.Single(rig.Ended).Row;
        Assert.Equal(300, s.ActiveSec);
        Assert.Equal(lastActive, s.End);

        rig.Use("code.exe", 120);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(2, rig.Ended.Count);
        Assert.Equal(120, rig.Ended[1].Row.ActiveSec);
    }

    [Fact]
    public void A_fullscreen_game_without_input_stays_one_session()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Use("eldenring.exe", 3600, fullscreen: true, idleMs: 30 * 60_000); // a cutscene, controller, a movie
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(3600, Assert.Single(rig.Ended).Row.ActiveSec);
    }

    [Fact]
    public void Sessions_keep_the_highest_temperatures_while_in_use()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Keys = new KeyValues { CpuTemp = 60, GpuTemp = 70 };
        rig.Use("eldenring.exe", 600);
        rig.Keys = new KeyValues { CpuTemp = 82, GpuTemp = 76 };
        rig.Use("eldenring.exe", 60);
        rig.Keys = new KeyValues { CpuTemp = 99, GpuTemp = 99 };
        rig.Use("chrome.exe", 60);   // another app's heat
        rig.Keys = new KeyValues { CpuTemp = 50 };
        rig.Use("eldenring.exe", 60);
        Assert.Equal(82, rig.Tracker.Activity.SessionCpuMax);
        Assert.Equal(76, rig.Tracker.Activity.SessionGpuMax);
        rig.Quit("eldenring.exe");
        var row = rig.Ended.Single(e => e.App.Exe == "eldenring.exe").Row;
        Assert.Equal(82, row.CpuTempMax);
        Assert.Equal(76, row.GpuTempMax);
    }

    [Fact]
    public void Activity_describes_the_session_in_progress()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        long start = rig.Clock.Unix;
        rig.Use("eldenring.exe", 125, fullscreen: true);
        var a = rig.Tracker.Activity;
        Assert.Equal("eldenring.exe", a.Exe);
        Assert.Equal("Elden Ring", a.Name);
        Assert.Equal(AppCategory.Game, a.Category);
        Assert.True(a.Present);
        Assert.True(a.Fullscreen);
        Assert.False(a.Paused);
        Assert.Equal(start, a.SessionStart);
        Assert.Equal(125, a.SessionActiveSec);
    }

    [Fact]
    public void Only_games_are_flagged_as_game_sessions()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Use("chrome.exe", 60);
        rig.Use("eldenring.exe", 60);
        rig.Use("code.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(["eldenring.exe"], rig.Ended.Where(e => e.Row.IsGame).Select(e => e.App.Exe));
        Assert.Single(rig.Sessions(), s => s.IsGame);
    }

    [Fact]
    public void An_unknown_app_fullscreen_on_a_busy_GPU_for_two_minutes_is_learnt_as_a_game()
    {
        using var rig = new TrackerRig();
        rig.SensorEvery = 1;
        rig.Keys = new KeyValues { GpuLoad = 95 };
        rig.Use("indiegame.exe", 119, fullscreen: true);
        Assert.Equal(AppCategory.Other, rig.Db.LoadApps().Single().Category);
        rig.Use("indiegame.exe", 5, fullscreen: true);
        Assert.Equal(AppCategory.Game, rig.Db.LoadApps().Single().Category);
        Assert.Equal(AppCategory.Game, rig.Tracker.Activity.Category);
        rig.Quit("indiegame.exe");
        Assert.True(Assert.Single(rig.Ended).Row.IsGame);
    }

    [Theory]
    [InlineData("indie.exe", false, 95)]   // windowed
    [InlineData("indie.exe", true, 39)]    // light GPU use (a video)
    [InlineData("chrome.exe", true, 95)]   // a known app (a browser game or WebGL demo)
    public void Not_learnt_as_a_game(string exe, bool fullscreen, double gpuLoad)
    {
        using var rig = new TrackerRig();
        rig.SensorEvery = 1;
        rig.Keys = new KeyValues { GpuLoad = gpuLoad };
        rig.Use(exe, 600, fullscreen: fullscreen);
        Assert.NotEqual(AppCategory.Game, rig.Db.LoadApps().Single().Category);
    }

    [Fact]
    public void A_category_set_by_the_user_decides_game_sessions()
    {
        var settings = new RigsightSettings();
        settings.AppCategories["emulator.exe"] = AppCategory.Game;
        settings.AppCategories["eldenring.exe"] = AppCategory.Other;
        using var rig = new TrackerRig(null, settings, null, EldenRing);
        rig.Use("emulator.exe", 60);
        rig.Use("eldenring.exe", 60);
        Assert.Equal(AppCategory.Other, rig.Tracker.Activity.Category);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.True(rig.Ended.Single(e => e.App.Exe == "emulator.exe").Row.IsGame);
        Assert.False(rig.Ended.Single(e => e.App.Exe == "eldenring.exe").Row.IsGame);
    }

    [Fact]
    public void A_session_with_no_time_in_use_is_not_saved()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 1, dt: 0); // woke from sleep with it in front, then closed it
        rig.Quit("code.exe");
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Empty(rig.Ended);
        Assert.Empty(rig.Sessions());
    }

    [Fact]
    public void Flush_closing_all_sessions_ends_each_once()
    {
        using var rig = new TrackerRig(null, null, null, EldenRing);
        rig.Use("chrome.exe", 60);
        rig.Use("code.exe", 60);
        rig.Use("eldenring.exe", 60);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.Empty(rig.Ended);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(3, rig.Ended.Count);
        Assert.Equal(3, rig.Ended.Select(e => e.App.Exe).Distinct().Count());
        Assert.Equal(3, rig.Sessions().Count);
    }

    [Fact]
    public void After_closing_all_sessions_use_starts_new_ones()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);
        rig.Use("code.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(2, rig.Sessions().Count);
        Assert.All(rig.Sessions(), s => Assert.Equal(60, s.ActiveSec));
    }

    [Fact]
    public void A_session_through_midnight_is_one_session()
    {
        using var rig = new TrackerRig(FakeClock.At(23, 30));
        rig.Use("code.exe", 3600);
        rig.Quit("code.exe");
        var s = Assert.Single(rig.Sessions());
        Assert.Equal(3600, s.ActiveSec);
        Assert.Equal(FakeClock.At(23, 30).ToUnixTimeSeconds(), s.Start);
    }

    [Fact]
    public void Sleep_ends_the_session_at_the_moment_the_PC_went_to_sleep()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 300);
        long slept = rig.Clock.Unix;
        rig.Clock.Advance(3 * 3600);
        rig.Use("code.exe", 1, dt: 0); // the first sample after waking counts nothing
        rig.Use("code.exe", 59);
        var first = Assert.Single(rig.Ended).Row;
        Assert.Equal(slept, first.End);
        Assert.Equal(300, first.ActiveSec);

        rig.Tracker.Flush(closeAllSessions: true);
        var second = rig.Ended[1].Row;
        Assert.Equal(59, second.ActiveSec);
        Assert.Equal(slept + 3 * 3600 + 1, second.Start);
        Assert.Equal(359, rig.Tracker.Today().ActiveSec);
        Assert.Equal(359, rig.Tracker.Today().OnSec);
        Assert.DoesNotContain(rig.Minutes(), m => m.Ts > slept && m.Ts < slept + 3 * 3600 - 60);
        rig.AssertInvariants();
    }

    [Fact]
    public void Clear_history_forgets_sessions_in_progress_without_ending_them()
    {
        using var rig = new TrackerRig();
        rig.Use("code.exe", 300);
        rig.Tracker.ClearHistory();
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Empty(rig.Ended);
        Assert.Empty(rig.Sessions());
    }
}

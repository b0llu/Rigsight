using Rigsight.Agent.Tracking;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// A game played in a VR headset: controllers, not the keyboard or mouse, and the desktop window can be anything
/// (2026-10-29: a Titanfall 2 VR session, windowed and played only with the controllers, wasn't recorded).
/// </summary>
public class TrackerVrTests
{
    private const string Game = "NorthstarLauncher.exe";

    private static TrackerRig VrRig()
    {
        var rig = new TrackerRig(apps: [(Game, "Northstar Launcher", AppCategory.Game)]);
        rig.Running.UnionWith(["vrserver.exe", "vrmonitor.exe", Game]);
        rig.GpuUsage[Game] = 70;
        return rig;
    }

    [Fact]
    public void A_windowed_game_in_the_headset_counts_without_keyboard_or_mouse()
    {
        using var rig = VrRig();
        rig.Use(Game, 10);
        rig.Away(Game, 30 * 60);
        rig.Tracker.Flush(closeAllSessions: true);

        Assert.Equal(10 + 30 * 60, rig.HoursOf(Game).FgSec);
        Assert.Equal(10 + 30 * 60, Assert.Single(rig.Sessions()).ActiveSec, 0);
    }

    [Fact]
    public void With_SteamVRs_window_or_the_desktop_in_front_the_time_is_the_games()
    {
        using var rig = VrRig();
        rig.Use(Game, 10);
        rig.Away("vrmonitor.exe", 600);
        rig.Away(null, 600);
        rig.Tracker.Flush(closeAllSessions: true);

        Assert.Equal(1210, rig.HoursOf(Game).FgSec);
        Assert.Equal(0, rig.HoursOf("vrmonitor.exe").FgSec);
        var session = Assert.Single(rig.Sessions());
        Assert.True(session.IsGame);
    }

    [Fact]
    public void Another_app_in_front_is_someone_using_it()
    {
        using var rig = VrRig();
        rig.Use(Game, 10);
        rig.Use("chrome.exe", 300);
        rig.Tracker.Flush(closeAllSessions: false);

        Assert.Equal(300, rig.HoursOf("chrome.exe").FgSec);
        Assert.Equal(10, rig.HoursOf(Game).FgSec);
    }

    [Fact]
    public void A_game_measured_by_RivaTuner_drawing_no_frames_isnt_played()
    {
        using var rig = VrRig();
        rig.Tracker.FramesMeasured = _ => true;
        rig.Tracker.ReadFrames = _ => null;
        rig.Use(Game, 10);
        rig.Away(Game, 30 * 60);
        rig.Tracker.Flush(closeAllSessions: false);

        // Away counts as without VR: the keys and mouse decide.
        Assert.Equal(10, rig.HoursOf(Game).FgSec);
    }

    [Fact]
    public void Without_VR_running_the_idle_rule_is_unchanged()
    {
        using var rig = VrRig();
        rig.Running.Remove("vrserver.exe");
        rig.Use(Game, 10);
        rig.Away(Game, 30 * 60);
        rig.Tracker.Flush(closeAllSessions: false);

        Assert.Equal(10, rig.HoursOf(Game).FgSec);
    }

    [Fact]
    public void A_game_barely_using_the_GPU_isnt_taken_for_the_one_in_the_headset()
    {
        using var rig = VrRig();
        rig.GpuUsage[Game] = 2;
        rig.Use(Game, 10);
        rig.Away("vrmonitor.exe", 600);
        rig.Tracker.Flush(closeAllSessions: false);

        Assert.Equal(10, rig.HoursOf(Game).FgSec);
    }

    [Fact]
    public void Drive_names_padded_with_NULs_are_logged_clean()
    {
        Assert.Equal("PCSPECIALIST PCS3480 2TB (5 temperature)", Log.Printable("PCSPECIALIST PCS3480 2TB\0\0\0\0 (5 temperature)"));
        Assert.Equal("line one\r\n\tline two", Log.Printable("line one\r\n\tline two"));
    }
}

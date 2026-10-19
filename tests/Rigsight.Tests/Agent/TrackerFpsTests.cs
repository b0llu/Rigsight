using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>A game's frame rate kept with each minute while RivaTuner measures it (the Games page's "How it runs").</summary>
public class TrackerFpsTests
{
    private static readonly (string, string, AppCategory) Dota = ("dota2.exe", "Dota 2", AppCategory.Game);
    private static readonly (string, string, AppCategory) Chrome = ("chrome.exe", "Google Chrome", AppCategory.Browser);

    [Fact]
    public void A_games_minute_keeps_its_average_frame_rate_and_1_percent_low()
    {
        using var rig = new TrackerRig(null, null, null, Dota);
        int second = 0;
        rig.Tracker.ReadFrames = _ => (second++ % 2 == 0 ? 230 : 240, 170);
        rig.Use("dota2.exe", 120, fullscreen: true);
        rig.Tracker.Flush(closeAllSessions: true);

        var minute = rig.Minutes().First(m => m.Fps is not null);
        Assert.Equal(235, minute.Fps!.Value, 0);
        Assert.Equal(170, minute.FpsLow!.Value, 0);
    }

    [Fact]
    public void No_frame_rate_is_kept_for_an_app_that_isnt_a_game_or_while_nothing_measures_it()
    {
        using var rig = new TrackerRig(null, null, null, Dota, Chrome);
        rig.Tracker.ReadFrames = _ => (60, 50);
        rig.Use("chrome.exe", 120);
        rig.Tracker.ReadFrames = _ => null;
        rig.Use("dota2.exe", 120, fullscreen: true);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.All(rig.Minutes(), m => Assert.Null(m.Fps));
    }

    [Fact]
    public void A_game_glimpsed_for_a_few_seconds_gives_its_minute_no_frame_rate()
    {
        using var rig = new TrackerRig(null, null, null, Dota, Chrome);
        rig.Tracker.ReadFrames = _ => (144, 120);
        rig.Use("chrome.exe", 55);
        rig.Use("dota2.exe", 5, fullscreen: true);
        rig.Use("chrome.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.All(rig.Minutes(), m => Assert.Null(m.Fps));
    }
}

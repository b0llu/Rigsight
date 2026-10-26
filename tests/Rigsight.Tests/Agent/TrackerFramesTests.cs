using Rigsight.Core.Games;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Every frame of the game in front (2026-10-25): read a second at a time from RivaTuner, summed up each minute for the
/// true lows, its stutters kept, and every frame kept for 14 days.
/// </summary>
public class TrackerFramesTests
{
    private static readonly (string, string, AppCategory) Dota = ("dota2.exe", "Dota 2", AppCategory.Game);
    private static readonly (string, string, AppCategory) Chrome = ("chrome.exe", "Google Chrome", AppCategory.Browser);

    /// <summary>A second of 144 fps; every 10th second one 40 ms frame in the middle. The first read only starts the count.</summary>
    private static Func<int, List<uint>, (bool, bool)> Game(Func<int> second) => (_, into) =>
    {
        int s = second();
        into.Clear();
        for (int i = 0; i < 144; i++) into.Add(s % 10 == 9 && i == 72 ? 40_000u : 6_944u);
        return (true, s == 0);
    };

    [Fact]
    public void A_games_minutes_keep_the_true_lows_its_stutters_and_every_frame()
    {
        using var rig = new TrackerRig(null, null, null, Dota);
        int second = 0;
        rig.Tracker.ReadFrames = _ => (144, 140);
        rig.Tracker.ReadNewFrames = Game(() => second++);
        rig.Use("dota2.exe", 125, fullscreen: true);
        rig.Tracker.Flush(closeAllSessions: true);

        long app = rig.Db.LoadApps().Single(a => a.Exe == "dota2.exe").Id;
        var (spread, minutes, stutters) = rig.Db.GetFrameSpread(app, 0, long.MaxValue / 2);
        Assert.True(minutes >= 2);
        Assert.InRange(stutters, 11, 13);
        Assert.InRange(spread.Fps!.Value, 142, 144.1);
        Assert.All(rig.Db.GetStutters(app, 0, long.MaxValue / 2), x => Assert.Equal(40, x.Ms, 1));
        // The minute's own 1% low is from the frames: a full minute has 6 stutters in 8,640 frames, under 1%.
        var minute = rig.Minutes().First(m => m.Fps is not null);
        Assert.InRange(minute.FpsLow!.Value, 135, 150);

        // Every frame, read back for the play.
        Assert.Equal(124 * 144, FrameStore.Read(app, 0, long.MaxValue / 2, rig.FramesFolder).Count);
    }

    [Fact]
    public void A_play_s_stats_say_the_lows_the_worst_stutter_and_stutters_an_hour()
    {
        using var rig = new TrackerRig(null, null, null, Dota);
        int second = 0;
        rig.Tracker.ReadFrames = _ => (144, 140);
        rig.Tracker.ReadNewFrames = Game(() => second++);
        rig.Use("dota2.exe", 600, fullscreen: true);
        rig.Tracker.Flush(closeAllSessions: true);

        long app = rig.Db.LoadApps().Single(a => a.Exe == "dota2.exe").Id;
        var session = rig.Db.GetAppSessions(app).Single();
        var stats = rig.Db.GetPlayStats(app, session.Start, session.End + 60);
        Assert.InRange(stats.StuttersAnHour!.Value, 300, 380);
        Assert.Equal(40, stats.Worst!.Ms, 1);
        Assert.NotNull(stats.FpsLow01);
        Assert.InRange(stats.FrameMinutes, 9, 11);
    }

    [Fact]
    public void Nothing_is_kept_for_an_app_that_isnt_a_game_and_frames_after_a_gap_aren_t_joined_on()
    {
        using var rig = new TrackerRig(null, null, null, Dota, Chrome);
        int second = 0;
        rig.Tracker.ReadFrames = _ => (144, 140);
        rig.Tracker.ReadNewFrames = Game(() => second++);
        rig.Use("chrome.exe", 70);
        long chrome = rig.Db.LoadApps().Single(a => a.Exe == "chrome.exe").Id;
        Assert.Equal(0, rig.Db.GetFrameSpread(chrome, 0, long.MaxValue / 2).Minutes);

        // Every read says frames went by: none are counted.
        rig.Tracker.ReadNewFrames = (_, into) => { into.Clear(); into.Add(7000); return (true, true); };
        rig.Use("dota2.exe", 70, fullscreen: true);
        rig.Tracker.Flush(closeAllSessions: true);
        long dota = rig.Db.LoadApps().Single(a => a.Exe == "dota2.exe").Id;
        Assert.Equal(0, rig.Db.GetFrameSpread(dota, 0, long.MaxValue / 2).Minutes);
    }
}

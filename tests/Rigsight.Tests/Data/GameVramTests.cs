using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.ViewModels;

namespace Rigsight.Tests.Data;

/// <summary>
/// A game's video memory on the Games page: its own where that was recorded (since 0.31.2), and where only the card's
/// whole memory in use was (every app together, as before), said as that, never passed off as the game's.
/// </summary>
public sealed class GameVramTests
{
    private static readonly DateTime Start = new(2026, 10, 20, 12, 0, 0);

    private static void Minutes(RigsightDb db, long app, int count, double cardMb, double? ownMb)
    {
        for (int i = 0; i < count; i++)
            db.WriteMinute(new SystemMinute
            {
                Ts = TimeUtil.ToUnix(Start.AddMinutes(i)), FgApp = app, ActiveSec = 60,
                VramUsed = cardMb + i, VramTotal = 12288, FgVram = ownMb is double own ? own + i : null,
            });
    }

    [Fact]
    public void A_play_from_before_says_its_figure_is_the_whole_card()
    {
        using var t = new TestDb();
        long game = t.Db.UpsertApp("Risk of Rain 2.exe", "Risk of Rain 2", null, AppCategory.Game);
        // The local AI held 7.5 GB of the card the whole time.
        Minutes(t.Db, game, 10, cardMb: 11_500, ownMb: null);
        var stats = t.Db.GetPlayStats(game, TimeUtil.ToUnix(Start), TimeUtil.ToUnix(Start.AddHours(1)));
        Assert.Null(stats.VramOwnMax);
        Assert.Equal(11_509, stats.VramMax);
        Assert.Equal("up to 11.2 of 12 GB · whole card, all apps", GamesViewModel.GraphicsMemory(stats));
    }

    [Fact]
    public void A_play_from_now_on_shows_the_games_own_memory()
    {
        using var t = new TestDb();
        long game = t.Db.UpsertApp("Risk of Rain 2.exe", "Risk of Rain 2", null, AppCategory.Game);
        Minutes(t.Db, game, 10, cardMb: 11_500, ownMb: 3_900);
        var stats = t.Db.GetPlayStats(game, TimeUtil.ToUnix(Start), TimeUtil.ToUnix(Start.AddHours(1)));
        Assert.Equal(3_909, stats.VramOwnMax);
        Assert.Equal("up to 3.8 of 12 GB", GamesViewModel.GraphicsMemory(stats));
    }

    [Fact]
    public void A_play_that_starts_partway_through_a_minute_keeps_that_minute()
    {
        // Real PC, 26 Oct 2026: a play from 9:18:06 PM lost the 9:18 minute (and its 9 stutters).
        using var t = new TestDb();
        long game = t.Db.UpsertApp("Risk of Rain 2.exe", "Risk of Rain 2", null, AppCategory.Game);
        Minutes(t.Db, game, 10, cardMb: 11_500, ownMb: 3_900);
        var stats = t.Db.GetPlayStats(game, TimeUtil.ToUnix(Start.AddSeconds(6)), TimeUtil.ToUnix(Start.AddHours(1)));
        Assert.Equal(10, stats.Minutes);
    }

    [Fact]
    public void Nothing_recorded_says_so()
    {
        Assert.Equal("Not recorded", GamesViewModel.GraphicsMemory(new PlayStats()));
    }
}

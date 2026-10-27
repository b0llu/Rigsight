using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

/// <summary>
/// When a play's stutters bunched up and what else was going on then (approved mock-up, 2026-10-25): named only when it
/// stood out against the rest of the same play.
/// </summary>
public class StutterCausesTests
{
    private const long Start = 1_792_900_000 / 60 * 60;

    private static (RigsightDb Db, long Game, long Steam) NewDb()
    {
        var db = RigsightDb.OpenWriter(Path.Combine(TestEnvironment.NewFolder("stutter-causes"), "rigsight.db"));
        long game = db.UpsertApp("cyberpunk2077.exe", "Cyberpunk 2077", null, AppCategory.Game);
        long steam = db.UpsertApp("steam.exe", "Steam", null, AppCategory.Other);
        return (db, game, steam);
    }

    /// <summary>A stutter at each of these minutes into the play (seconds into the minute: 20).</summary>
    private static List<Stutter> At(params int[] minutes) => [.. minutes.Select(m => new Stutter((Start + m * 60 + 20) * 1000, 40, 7))];

    [Fact]
    public void Stutters_bunched_while_another_app_used_the_disk_heavily_say_so_and_the_rest_are_counted()
    {
        var (db, game, steam) = NewDb();
        using (db)
        {
            // Steam reads 380 MB/s from minute 24 to 40 of a 150-minute play, a trickle otherwise.
            for (int m = 0; m < 150; m++)
                db.WriteAppMinutes(Start + m * 60, [new AppMinute(Start + m * 60, steam, 1, 0, 300, m is >= 24 and <= 40 ? 380e6 : 0.5e6), new AppMinute(Start + m * 60, game, 30, 95, 9000, 1e6)]);
            var stutters = At(25, 26, 27, 28, 29, 30, 31, 33, 34, 35, 36, 37, 38, 39, 5, 60, 90, 120, 145);
            string? why = StutterCauses.Explain(db, game, stutters, Start, Start + 150 * 60, new RigsightSettings());
            Assert.NotNull(why);
            Assert.StartsWith("14 of the 19 stutters came between ", why);
            Assert.Contains("while Steam was using the disk heavily (380 MB/s)", why);
            Assert.Contains("The rest were spread out, about one ", why);
        }
    }

    [Fact]
    public void Spread_out_stutters_and_bunched_ones_with_nothing_standing_out_are_said_as_such()
    {
        var (db, game, _) = NewDb();
        using (db)
        {
            Assert.Equal("The 4 stutters were spread out, about one every 15 minutes.", StutterCauses.Explain(db, game, At(5, 20, 35, 50), Start, Start + 60 * 60, new RigsightSettings()));
            string? why = StutterCauses.Explain(db, game, At(10, 11, 12, 13, 14, 15), Start, Start + 60 * 60, new RigsightSettings());
            Assert.Contains("6 of the 6 stutters came between ", why);
            Assert.Contains("nothing else stood out then", why);
            Assert.Null(StutterCauses.Explain(db, game, [], Start, Start + 600, new RigsightSettings()));
        }
    }

    [Fact]
    public void The_graphics_card_s_memory_nearly_full_is_named()
    {
        var (db, game, _) = NewDb();
        using (db)
        {
            for (int m = 0; m < 60; m++)
                db.WriteMinute(new SystemMinute { Ts = Start + m * 60, ActiveSec = 60, VramUsed = m is >= 30 and <= 40 ? 11900 : 8000, VramTotal = 12288 });
            string? why = StutterCauses.Explain(db, game, At(31, 32, 33, 34, 35, 36), Start, Start + 60 * 60, new RigsightSettings());
            Assert.Contains("while the graphics card's memory was nearly full (11.6 of 12 GB)", why);
        }
    }
    // "The 765 stutters were spread out, about one every 1 minutes" for a two-and-a-half-hour play (2026-10-27).
    [Theory]
    [InlineData(15.0, "about one every 15 minutes")]
    [InlineData(1.0, "about one a minute")]
    [InlineData(150.0 / 765, "about one every 12 seconds")]
    [InlineData(0.001, "about one a second")]
    public void How_often_reads_in_minutes_or_seconds(double minutesEach, string says) =>
        Assert.Equal(says, StutterCauses.Every(minutesEach));
}

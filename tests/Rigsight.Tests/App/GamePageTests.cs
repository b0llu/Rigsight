using System.Globalization;
using Rigsight.Controls;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// One game's page as the approved mock-up "One game's page" (2026-11-14) draws it: Problems only for what happened,
/// and How it runs with a slot a play when the range holds 12 plays or fewer.
/// </summary>
public sealed class GamePageTests
{
    private static T InEnglish<T>(Func<T> work)
    {
        var before = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); return work(); }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static GameStory Story(int crashes = 0, int resets = 0, int hangs = 0, int offs = 0, params DateTime[] cuts) => new()
    {
        Entry = new GameEntry { Name = "Cyberpunk 2077" },
        Crashes = crashes, DriverResets = resets, Hangs = hangs, ShutOffs = offs,
        CrashTimes = [.. Enumerable.Range(0, crashes).Select(i => new DateTime(2026, 10, 24, 21, 0, 0).AddDays(i * 16))],
        DriverResetTimes = [.. Enumerable.Range(0, resets).Select(i => new DateTime(2026, 10, 29, 22, 0, 0).AddDays(i))],
        HangTimes = [.. Enumerable.Range(0, hangs).Select(i => new DateTime(2026, 10, 1, 22, 0, 0).AddDays(i))],
        PowerCuts = [.. cuts.Select(c => new GamePlay(TimeUtil.ToUnix(c), TimeUtil.ToUnix(c) + 2160, 2160, 1, new PlayStats(), PlayEnd.PowerCut))],
    };

    // ── Problems ──

    [Fact]
    public void No_problems_leaves_no_card() => Assert.Empty(GamesViewModel.ProblemTiles(Story()));

    [Fact]
    public void Only_what_happened_gets_a_tile_and_a_power_cut_is_grey() => InEnglish(() =>
    {
        var tiles = GamesViewModel.ProblemTiles(Story(crashes: 2, resets: 1, cuts: [new DateTime(2026, 11, 3, 22, 5, 0)]));
        Assert.Equal(["2 game crashes Windows recorded", "1 graphics driver reset", "1 power cut"], tiles.Select(t => t.Lead + t.Count + t.Trail));
        Assert.Equal(["2", "1", "1"], tiles.Select(t => t.Count));
        Assert.Equal(["Sat 24 Oct and Mon 9 Nov", "Thu 29 Oct", "Tue 3 Nov"], tiles.Select(t => t.Days));
        Assert.Equal([false, false, true], tiles.Select(t => t.Grey));
        return 0;
    });

    [Fact]
    public void Many_days_say_the_last_one() => InEnglish(() =>
    {
        var tile = Assert.Single(GamesViewModel.ProblemTiles(Story(hangs: 5)));
        Assert.Equal("5 freezes", tile.Lead + tile.Count + tile.Trail);
        Assert.Equal("The last one on Mon 5 Oct", tile.Days);
        return 0;
    });

    [Fact]
    public void A_single_shut_off_reads_as_one()
    {
        var tile = Assert.Single(GamesViewModel.ProblemTiles(Story(offs: 1)));
        Assert.Equal("1 sudden shut-off that wasn't a power cut", tile.Lead + tile.Count + tile.Trail);
    }

    // ── How it runs in slots ──

    private static RunsPoint Play(DateTime at, double minutes = 90) => new(at, at.AddMinutes(minutes), minutes * 60, 150, 100, null, 1, false, false, false, null, null);

    [Fact]
    public void Slots_are_named_by_their_dates_as_room_allows() => InEnglish(() =>
    {
        var plays = new[] { Play(new DateTime(2026, 10, 15, 20, 5, 0)), Play(new DateTime(2026, 10, 16, 18, 5, 0)), Play(new DateTime(2026, 10, 16, 22, 5, 0)), Play(new DateTime(2026, 11, 8, 20, 5, 0)) };
        Assert.Equal(["Thu 15 Oct", "Fri 16 Oct", "10:05 PM", "Sun 8 Nov"], RunsChart.SlotDays(plays, 107));
        Assert.Equal(["15 Oct", "16 Oct", "·", "8 Nov"], RunsChart.SlotDays(plays, 50));
        // Narrow: the day alone, the month kept on the first and where it turns.
        Assert.Equal(["15 Oct", "16", "·", "8 Nov"], RunsChart.SlotDays(plays, 30));
        return 0;
    });

    [Fact]
    public void A_week_or_more_between_plays_is_said_in_days()
    {
        var plays = new[] { Play(new DateTime(2026, 10, 15, 20, 0, 0)), Play(new DateTime(2026, 10, 18, 21, 0, 0)), Play(new DateTime(2026, 11, 8, 20, 0, 0), 60) };
        Assert.Equal([(2, 21)], RunsChart.SlotGaps(plays));
    }

    [Fact]
    public void An_update_stands_between_the_plays_it_came_between()
    {
        var plays = new[] { Play(new DateTime(2026, 10, 18, 20, 0, 0)), Play(new DateTime(2026, 10, 22, 21, 0, 0)), Play(new DateTime(2026, 10, 26, 16, 0, 0)) };
        double[] xs = [100, 300, 500];
        Assert.Equal(200, RunsChart.SlotMarkX(plays, xs, new DateTime(2026, 10, 22, 18, 12, 0), 600));
        // Before the first play, or after the last: at the chart's edges.
        Assert.Equal(47, RunsChart.SlotMarkX(plays, xs, new DateTime(2026, 10, 16), 600));
        Assert.Equal(583, RunsChart.SlotMarkX(plays, xs, new DateTime(2026, 10, 30), 600));
    }
}

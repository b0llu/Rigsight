using System.Globalization;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.ViewModels;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>
/// A game's RAM on the Games page (approved mock-up "One game's page", 2026-11-14): its own per play from the hours the
/// play touched, the whole PC's in use while it was in front, and the Memory card and Every play's column that say them.
/// </summary>
public sealed class GameMemoryTests
{
    private static readonly DateTime Start = new(2026, 10, 28, 21, 12, 0);

    private static void Hour(RigsightDb db, long app, DateTime hour, double avgMb, double maxMb, int readings = 60) =>
        db.AddAppHour(new AppHour { Ts = TimeUtil.ToUnix(hour), AppId = app, FgSec = 3000, MemSum = avgMb * readings, MemN = readings, MemMax = maxMb });

    /// <summary>Figures as an English Windows writes them, whatever this PC's region.</summary>
    private static void InEnglish(Action work)
    {
        var before = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); work(); }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void A_plays_RAM_is_the_games_own_over_the_hours_it_touched()
    {
        using var t = new TestDb("game-ram");
        long game = t.Db.UpsertApp("control.exe", "CONTROL Resonant", null, AppCategory.Game);
        // 9:12 PM to 11:47 PM: the 9, 10 and 11 PM hours, not the morning after.
        Hour(t.Db, game, new DateTime(2026, 10, 28, 21, 0, 0), 9_000, 10_000);
        Hour(t.Db, game, new DateTime(2026, 10, 28, 22, 0, 0), 9_600, 11_674);
        Hour(t.Db, game, new DateTime(2026, 10, 28, 23, 0, 0), 10_200, 11_000);
        Hour(t.Db, game, new DateTime(2026, 10, 29, 9, 0, 0), 20_000, 30_000);
        var (avg, peak) = t.Db.GetAppMemory(game, TimeUtil.ToUnix(Start), TimeUtil.ToUnix(new DateTime(2026, 10, 28, 23, 47, 0)));
        Assert.Equal(9_600, avg!.Value, 1);
        Assert.Equal(11_674, peak);
    }

    [Fact]
    public void Nothing_kept_for_those_hours_is_no_figure()
    {
        using var t = new TestDb("game-ram-none");
        long game = t.Db.UpsertApp("control.exe", "CONTROL Resonant", null, AppCategory.Game);
        Assert.Equal((null, null), t.Db.GetAppMemory(game, TimeUtil.ToUnix(Start), TimeUtil.ToUnix(Start.AddHours(2))));
    }

    [Fact]
    public void A_plays_figures_carry_its_RAM_and_the_PCs_RAM_in_use_while_the_game_was_in_front()
    {
        using var t = new TestDb("game-ram-stats");
        long game = t.Db.UpsertApp("control.exe", "CONTROL Resonant", null, AppCategory.Game);
        long other = t.Db.UpsertApp("chrome.exe", "Chrome", null, AppCategory.Browser);
        long from = TimeUtil.ToUnix(new DateTime(2026, 10, 28, 21, 0, 0));
        for (int m = 0; m < 30; m++) t.Db.WriteMinute(Minute(from + m * 60, app: game, ram: 18 + m * 0.1));
        // After the game, a browser with more RAM in use: not the game's play.
        t.Db.WriteMinute(Minute(from + 31 * 60, app: other, ram: 30));
        Hour(t.Db, game, new DateTime(2026, 10, 28, 21, 0, 0), 9_300, 11_400);

        var stats = t.Db.GetPlayStats(game, from, from + 40 * 60);
        Assert.Equal(9_300, stats.MemAvg!.Value, 1);
        Assert.Equal(11_400, stats.MemMax);
        Assert.Equal(20.9, stats.RamUsedMax!.Value, 2);
    }

    [Fact]
    public void The_PCs_installed_RAM_is_read_from_the_inventory()
    {
        using var t = new TestDb("game-ram-total");
        Assert.Null(t.Db.GetRamTotalGb());
        t.Db.ApplyInventory(new() { [Inventory.Ram] = [new(Inventory.Ram, Inventory.Ram, "Memory", "32 GB")] }, "test", Start);
        Assert.Equal(32, t.Db.GetRamTotalGb());
    }

    [Fact]
    public void The_Memory_card_says_the_games_RAM_the_PCs_and_video_memory_in_four_rows() => InEnglish(() =>
    {
        var all = new PlayStats { MemAvg = 9_318, MemMax = 11_674, RamUsedMax = 21.7, VramOwnMax = 10_854, VramTotal = 12_288 };
        var rows = GamesViewModel.MemoryRows(all, 32);
        Assert.Equal(["RAM while playing", "RAM peak", "RAM in use, all apps", "Video memory"], rows.Select(r => r.Label));
        Assert.Equal(["9.1 GB average", "11.4 GB", "up to 21.7 of 32 GB", "up to 10.6 of 12 GB"], rows.Select(r => r.Value));
        // Plain, even near the PC's whole RAM: nothing here has a usual to set it against.
        Assert.All(rows, r => Assert.Null(r.Brush));
        Assert.Equal(3, rows.Count(r => r.Tip is not null));
        Assert.Equal("up to 15.3 of 16 GB", GamesViewModel.MemoryRows(new PlayStats { RamUsedMax = 15.3 }, 16)[2].Value);
    });

    [Fact]
    public void The_Memory_card_says_what_wasnt_recorded() => InEnglish(() =>
    {
        var rows = GamesViewModel.MemoryRows(new PlayStats { RamUsedMax = 14.9 }, null);
        Assert.Equal(["Not recorded", "Not recorded", "up to 14.9 GB", "Not recorded"], rows.Select(r => r.Value));
    });

    [Fact]
    public void Every_play_says_the_RAM_average_then_its_peak() => InEnglish(() =>
    {
        Assert.Equal(("9.6 GB", "peak 11.4"), GamesViewModel.PlayRam(new PlayStats { MemAvg = 9_830, MemMax = 11_674 }));
        Assert.Equal(("650 MB", "peak 1.2 GB"), GamesViewModel.PlayRam(new PlayStats { MemAvg = 650, MemMax = 1_229 }));
        Assert.Equal(((string?)null, (string?)null), GamesViewModel.PlayRam(new PlayStats()));
    });

    [Fact]
    public void All_plays_together_weigh_each_plays_RAM_by_its_minutes_and_keep_the_highest()
    {
        using var t = new TestDb("game-ram-story");
        long game = t.Db.UpsertApp("control.exe", "CONTROL Resonant", null, AppCategory.Game);
        var a = new DateTime(2026, 10, 18, 20, 0, 0);
        var b = new DateTime(2026, 10, 22, 21, 0, 0);
        foreach (var (start, minutes, mem, max, ram) in new[] { (a, 30, 8_000.0, 9_000.0, 20.0), (b, 60, 11_000.0, 12_000.0, 22.0) })
        {
            long s = TimeUtil.ToUnix(start);
            t.Db.InsertSession(Session(game, s, s + minutes * 60, game: true));
            for (int m = 0; m < minutes; m++) t.Db.WriteMinute(Minute(s + m * 60, app: game, ram: ram));
            Hour(t.Db, game, start, mem, max);
        }
        var settings = Settings();
        var story = GameHistory.Story(t.Db, GameHistory.Library(t.Db, [], settings).Single(), settings, b.AddHours(3));
        Assert.Equal(10_000, story.All.MemAvg!.Value, 1); // 30 minutes at 8 GB, 60 at 11 GB
        Assert.Equal(12_000, story.All.MemMax);
        Assert.Equal(22, story.All.RamUsedMax);
    }
}

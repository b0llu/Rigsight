using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Core;

/// <summary>A game's plays, usual and story, from the sessions, minutes, crashes and changes already recorded.</summary>
[Collection("Disks")]
public class GameHistoryTests
{
    private static readonly long Day = U(2026, 10, 10, 20);

    private static GamePlay Play(double minutes, double? gpu, double? watts, double? fps = null, long start = 0) =>
        new(Day + start, Day + start + (long)(minutes * 60), minutes * 60, 1,
            new PlayStats { Minutes = (int)minutes, Gpu = gpu, GpuPower = watts, Cpu = 57, Fps = fps, FpsMinutes = fps is null ? 0 : (int)minutes }, PlayEnd.Closed);

    // ── Plays ──

    [Fact]
    public void Sessions_with_a_break_under_a_quarter_hour_are_one_play()
    {
        // Dota 2 on the user's PC: a match, back to the menu for five minutes, another match.
        var plays = GameHistory.Join([Session(1, Day, Day + 1800), Session(1, Day + 2100, Day + 4800), Session(1, Day + 7200, Day + 9000)], []);
        Assert.Equal(2, plays.Count);
        Assert.Equal((Day, Day + 4800, 2), (plays[0].Start, plays[0].End, plays[0].Parts));
        Assert.Equal(1800 + 2700, plays[0].Active);
    }

    [Fact]
    public void A_shut_off_between_two_sessions_always_ends_the_play()
    {
        // REMATCH on 17 Oct: in at 11:16 PM, the PC went off at 11:21, back in at 11:34.
        long t = U(2026, 10, 17, 23, 16);
        var plays = GameHistory.Join([Session(1, t, t + 60), Session(1, t + 18 * 60, t + 78 * 60)], [t + 5 * 60]);
        Assert.Equal(2, plays.Count);
    }

    // ── The usual: like for like ──

    [Fact]
    public void A_games_usual_is_its_own_plays_at_its_usual_power()
    {
        var plays = new[] { Play(45, 65, 255), Play(50, 64, 260), Play(90, 66, 250), Play(40, 80, 400), Play(10, 90, 255) };
        var usual = GameHistory.UsualOf(plays);
        Assert.NotNull(usual);
        Assert.Equal(65, usual.Gpu);
        Assert.Equal(3, usual.Plays); // the 400 W play is another kind of play, the 10-minute one too short
    }

    [Fact]
    public void Fewer_than_three_steady_plays_have_no_usual() =>
        Assert.Null(GameHistory.UsualOf([Play(45, 65, 255), Play(50, 64, 260), Play(5, 66, 250)]));

    [Fact]
    public void A_light_game_never_at_full_load_still_gets_a_usual()
    {
        // Dota 2 runs the graphics card at about 60%: R4's steady heavy-load usual had nothing for it.
        Assert.NotNull(GameHistory.UsualOf([Play(45, 64, 250), Play(60, 65, 255), Play(50, 66, 262)]));
    }

    // ── Frames since the last update ──

    [Fact]
    public void Frames_since_an_update_within_five_percent_are_no_change()
    {
        long update = Day + 10 * 3600;
        var plays = new[] { Play(60, 65, 250, 236), Play(60, 65, 250, 240, 3600 * 2), Play(60, 65, 250, 238, 3600 * 20), Play(60, 65, 250, 232, 3600 * 30) };
        Assert.Equal("No change", GameHistory.SinceUpdate(plays, L(update)));
    }

    [Fact]
    public void Frames_since_an_update_say_how_much_lower()
    {
        long update = Day + 10 * 3600;
        var plays = new[] { Play(60, 65, 250, 160), Play(60, 65, 250, 164, 3600 * 2), Play(60, 65, 250, 140, 3600 * 20), Play(60, 65, 250, 141, 3600 * 30) };
        Assert.Equal("13% lower", GameHistory.SinceUpdate(plays, L(update)));
    }

    [Fact]
    public void One_play_on_a_side_of_an_update_is_too_few_to_compare() =>
        Assert.Null(GameHistory.SinceUpdate([Play(60, 65, 250, 160), Play(60, 65, 250, 140, 3600 * 20)], L(Day + 10 * 3600)));

    // ── The story, from what's recorded ──

    [Fact]
    public void A_games_story_has_its_plays_figures_endings_and_the_changes_between_them()
    {
        using var t = new TestDb("game-story");
        long game = t.Db.UpsertApp("dota2.exe", "Dota 2", @"E:\SteamLibrary\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe", AppCategory.Game);
        long a = U(2026, 10, 10, 20), b = U(2026, 10, 12, 21);
        foreach (var (start, minutes, fps) in new[] { (a, 40, 236.0), (b, 32, 200.0) })
        {
            t.Db.InsertSession(Session(game, start, start + minutes * 60, game: true));
            for (int m = 0; m < minutes; m++)
            {
                var row = Minute(start + m * 60, cpu: 57, gpu: 65, gpuLoad: m % 4 == 0 ? 98 : 60, app: game, gpuPower: 255, cpuPower: 60);
                row.Fps = fps; row.FpsLow = fps * 0.7;
                t.Db.WriteMinute(row);
            }
        }
        // A graphics driver between the two plays, and an app's update that isn't the game's.
        t.Db.InsertChanges([
            new SystemChange(L(a + 3 * 3600), ChangeKind.Driver, "NVIDIA graphics driver 617.42") { Subject = "gpudriver:nvidia" },
            new SystemChange(L(a + 4 * 3600), ChangeKind.AppUpdated, "Discord updated to 1.0.9262") { Subject = "app:discord" },
        ]);

        var entry = GameHistory.Library(t.Db, [], Settings()).Single(e => e.Name == "Dota 2");
        Assert.Equal(72 * 60, entry.ActiveSec);
        var story = GameHistory.Story(t.Db, entry, Settings(), L(b + 7200));

        Assert.Equal(2, story.Plays.Count);
        Assert.Equal(40, story.Plays[0].Stats.Minutes);
        Assert.Equal(236, story.Plays[0].Stats.Fps!.Value, 1);
        Assert.Equal(25, story.GpuFullShare!.Value, 0); // a quarter of its minutes at full load
        Assert.All(story.Plays, p => Assert.Equal(PlayEnd.Closed, p.Ending));
        var change = Assert.Single(story.Changes);
        Assert.Equal((0, ChangeKind.Driver), (change.After, change.Change.Kind));
        Assert.Equal(0, story.Crashes + story.Hangs + story.DriverResets + story.ShutOffs);
        // Two plays with frames: the chart draws plays, so no minutes ride along.
        Assert.Empty(story.OnlyPlayMinutes);
    }

    [Fact]
    public void A_games_only_play_with_frames_brings_its_minutes_for_the_chart()
    {
        using var t = new TestDb("game-one-play");
        long game = t.Db.UpsertApp("riskofrain2.exe", "Risk of Rain 2", null, AppCategory.Game);
        long a = U(2026, 10, 18, 21);
        t.Db.InsertSession(Session(game, a, a + 27 * 60, game: true));
        for (int m = 0; m < 27; m++)
        {
            var row = Minute(a + m * 60, cpu: 57, gpu: 63, gpuLoad: 90, app: game, gpuPower: 230, cpuPower: 60);
            // A few minutes without frames (a menu RivaTuner didn't measure) are left out, not drawn as zero.
            if (m % 9 != 4) { row.Fps = 140 + m; row.FpsLow = 100 + m; }
            t.Db.WriteMinute(row);
        }
        var entry = GameHistory.Library(t.Db, [], Settings()).Single();
        var story = GameHistory.Story(t.Db, entry, Settings(), L(a + 7200));

        Assert.Equal(24, story.OnlyPlayMinutes.Count);
        Assert.All(story.OnlyPlayMinutes, m => Assert.NotNull(m.Fps));
        Assert.Equal(a, story.OnlyPlayMinutes[0].Ts);
        Assert.Equal(166, story.OnlyPlayMinutes[^1].Fps!.Value, 0);
    }

    [Fact]
    public void A_games_own_crash_ends_its_play_and_another_apps_crash_doesnt()
    {
        using var t = new TestDb("game-crash");
        long game = t.Db.UpsertApp("rematch.exe", "REMATCH", null, AppCategory.Game);
        long a = U(2026, 10, 14, 23);
        t.Db.InsertSession(Session(game, a, a + 1800, game: true));
        t.Db.InsertCrashes([
            new CrashEvent { Ts = a + 600, Kind = CrashKind.AppCrash, AppExe = "RocketLeague.exe" },
            new CrashEvent { Ts = a + 1800, Kind = CrashKind.AppCrash, AppExe = "rematch.exe" },
        ]);
        var entry = GameHistory.Library(t.Db, [], Settings()).Single();
        var story = GameHistory.Story(t.Db, entry, Settings(), L(a + 7200));
        Assert.Equal(PlayEnd.Crashed, Assert.Single(story.Plays).Ending);
        Assert.Equal(1, story.Crashes);
    }

    [Fact]
    public void A_games_freeze_before_it_was_ever_a_play_still_counts()
    {
        // Real PC, 19 Oct 2026: REMATCH froze 19 seconds after it started, with no play recorded then, and the game said
        // "hangs 0" while Crashes listed the freeze.
        using var t = new TestDb("game-hang-outside");
        long game = t.Db.UpsertApp("rematch.exe", "REMATCH", null, AppCategory.Game);
        long a = U(2026, 10, 14, 23);
        t.Db.InsertSession(Session(game, a, a + 1800, game: true));
        t.Db.InsertCrashes([new CrashEvent { Ts = a + 5 * 86400, Kind = CrashKind.AppHang, AppExe = "rematch.exe" }]);
        var entry = GameHistory.Library(t.Db, [], Settings()).Single();
        var story = GameHistory.Story(t.Db, entry, Settings(), L(a + 6 * 86400));
        Assert.Equal(PlayEnd.Closed, Assert.Single(story.Plays).Ending);
        Assert.Equal((0, 1), (story.Crashes, story.Hangs));
    }

    [Fact]
    public void An_installed_game_never_played_is_in_the_library_without_a_story()
    {
        using var t = new TestDb("game-unplayed");
        var siege = new InstalledGame { Key = "steam:359550", Launcher = Launcher.Steam, Name = "Tom Clancy's Rainbow Six Siege", InstallDir = @"E:\x\Siege" };
        var entry = Assert.Single(GameHistory.Library(t.Db, [siege], Settings()));
        Assert.False(entry.HasHistory);
        Assert.Empty(GameHistory.Story(t.Db, entry, Settings(), DateTime.Now).Plays);
    }

    [Fact]
    public void A_games_programs_in_one_launcher_folder_are_one_game()
    {
        using var t = new TestDb("game-two-exes");
        const string dir = @"E:\SteamLibrary\steamapps\common\Apex Legends";
        long main = t.Db.UpsertApp("r5apex_dx12.exe", "Apex Legends", dir + @"\r5apex_dx12.exe", AppCategory.Game);
        long other = t.Db.UpsertApp("r5apex.exe", "Apex Legends", dir + @"\r5apex.exe", AppCategory.Game);
        long a = U(2026, 10, 9, 18);
        t.Db.InsertSession(Session(main, a, a + 3600, game: true));
        t.Db.InsertSession(Session(other, a + 7200, a + 7500, game: true));
        var apex = new InstalledGame { Key = "steam:1172470", Launcher = Launcher.Steam, Name = "Apex Legends", InstallDir = dir };
        var entry = Assert.Single(GameHistory.Library(t.Db, [apex], Settings()));
        Assert.Equal([main, other], entry.AppIds);
        Assert.Equal(3900, entry.ActiveSec);
    }

    [Fact]
    public void A_launcher_is_worked_out_from_where_a_removed_game_was_installed()
    {
        Assert.Equal(Launcher.Steam, GameHistory.LauncherOfPath(@"E:\SteamLibrary\steamapps\common\Just Cause 3\JustCause3.exe"));
        Assert.Null(GameHistory.LauncherOfPath(@"C:\Users\x\Downloads\FUMES\FUMES.exe"));
    }

    // ── Game updates in the inventory ──

    [Fact]
    public void A_games_new_version_is_a_game_update_and_a_new_game_isnt_a_change()
    {
        var before = new[] { new InventoryItem(Inventory.Game, "steam:570", "Dota 2", "build 25971480") };
        var after = new[] { new InventoryItem(Inventory.Game, "steam:570", "Dota 2", "build 25999000"), new InventoryItem(Inventory.Game, "steam:730", "Counter-Strike 2", "build 1") };
        var change = Assert.Single(Inventory.Diff(Inventory.Game, before, after, new DateTime(2026, 10, 16, 8, 0, 0)));
        Assert.Equal((ChangeKind.GameUpdated, "Dota 2 updated", "game:steam:570"), (change.Kind, change.Title, change.Subject));
        Assert.Equal(("build 25971480", "build 25999000"), (change.Was, change.Now));
        // A game uninstalled is the apps' change ("removed"), not the game record's.
        Assert.Empty(Inventory.Diff(Inventory.Game, after, before.Take(0), new DateTime(2026, 10, 16, 8, 0, 0)));
    }

    // ── A game no launcher lists ──

    [Fact]
    public void A_game_no_launcher_lists_is_installed_while_its_program_is_still_there()
    {
        using var t = new TestDb("game-on-disk");
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "on-disk-" + Guid.NewGuid().ToString("N")[..8], "Games", "Hollow Peak");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "HollowPeak.exe");
        File.WriteAllText(exe, "");
        try
        {
            long game = t.Db.UpsertApp("hollowpeak.exe", "Hollow Peak", exe, AppCategory.Game);
            long gone = t.Db.UpsertApp("oldgame.exe", "Old Game", Path.Combine(dir, "..", "Old Game", "OldGame.exe"), AppCategory.Game);
            t.Db.InsertSession(Session(game, Day, Day + 1800, game: true));
            t.Db.InsertSession(Session(gone, Day, Day + 1800, game: true));

            var library = GameHistory.Library(t.Db, [], Settings());
            var there = library.Single(e => e.Name == "Hollow Peak");
            Assert.True(there.OnDisk);
            Assert.True(there.IsInstalled);
            Assert.Null(there.Installed); // no launcher, so nothing a launcher would say (size, art)
            var deleted = library.Single(e => e.Name == "Old Game");
            Assert.False(deleted.IsInstalled);
        }
        finally { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, true); }
    }
}

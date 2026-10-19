using Rigsight.Core.Apps;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

/// <summary>The games the launchers list as installed: Steam, Epic, and Windows' list of programs for the rest.</summary>
public class GameLibraryTests
{
    /// <summary>A Steam install with one library on another drive, each holding games, and Steam's picture cache.</summary>
    private static (string Root, string Library) Steam(params (string Id, string Name, string Dir, string Build, bool InSecond)[] games)
    {
        var root = TestEnvironment.NewFolder("steam-root");
        var library = TestEnvironment.NewFolder("steam-library");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{root.Replace(@"\", @"\\")}}" }
                "1" { "path" "{{library.Replace(@"\", @"\\")}}" }
            }
            """);
        foreach (var g in games)
        {
            var steamapps = Path.Combine(g.InSecond ? library : root, "steamapps");
            Directory.CreateDirectory(Path.Combine(steamapps, "common", g.Dir));
            File.WriteAllText(Path.Combine(steamapps, "common", g.Dir, "game.exe"), "x");
            File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{g.Id}.acf"), $$"""
                "AppState"
                {
                    "appid"		"{{g.Id}}"
                    "name"		"{{g.Name}}"
                    "installdir"		"{{g.Dir}}"
                    "SizeOnDisk"		"77300000000"
                    "buildid"		"{{g.Build}}"
                    "LastUpdated"		"1792209540"
                    "LastPlayed"		"1792260480"
                    "UserConfig" { "name" "Not this name" }
                }
                """);
        }
        return (root, library);
    }

    private static GameSources Sources(string? steam = null, string? epic = null, params UninstallEntry[] entries) => new()
    {
        SteamRoots = steam is null ? [] : [steam],
        EpicManifests = epic,
        UninstallEntries = () => entries,
    };

    [Fact]
    public void Steam_games_come_from_every_library_with_their_build_and_launch_and_uninstall_links()
    {
        var (root, library) = Steam(("570", "Dota 2", "dota 2 beta", "25971480", true), ("2138720", "REMATCH", "Rematch", "25882442", false));
        var games = GameLibrary.Scan(Sources(root));

        var dota = Assert.Single(games, g => g.Name == "Dota 2");
        Assert.Equal(Launcher.Steam, dota.Launcher);
        Assert.Equal("steam:570", dota.Key);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "dota 2 beta"), dota.InstallDir);
        Assert.Equal("build 25971480", dota.Version);
        Assert.Equal(77_300_000_000, dota.SizeBytes);
        Assert.Equal("steam://rungameid/570", dota.LaunchTarget);
        Assert.Equal("steam://uninstall/570", dota.UninstallTarget);
        Assert.NotNull(dota.Updated);
        Assert.NotNull(dota.LastPlayed);
        Assert.Contains(games, g => g.Name == "REMATCH");
    }

    [Fact]
    public void Steams_own_tools_are_not_games()
    {
        var (root, _) = Steam(("228980", "Steamworks Common Redistributables", "Steamworks Shared", "1", false), ("431960", "Wallpaper Engine", "wallpaper_engine", "2", true));
        Assert.Empty(GameLibrary.Scan(Sources(root)));
    }

    [Fact]
    public void Steams_pictures_are_found_in_its_cache_old_and_new_layout_and_only_when_asked()
    {
        var (root, _) = Steam(("570", "Dota 2", "dota 2 beta", "1", true), ("730", "Counter-Strike 2", "csgo", "2", true));
        var cache = Path.Combine(root, "appcache", "librarycache");
        // Newer Steam: a folder per game, pictures in hashed sub-folders.
        Directory.CreateDirectory(Path.Combine(cache, "570", "a1b2"));
        File.WriteAllText(Path.Combine(cache, "570", "a1b2", "library_capsule.jpg"), "x");
        Directory.CreateDirectory(Path.Combine(cache, "570", "c3d4"));
        File.WriteAllText(Path.Combine(cache, "570", "c3d4", "library_hero.jpg"), "x");
        File.WriteAllText(Path.Combine(cache, "570", "logo.png"), "x");
        // Older Steam: flat files named after the game.
        File.WriteAllText(Path.Combine(cache, "730_library_600x900.jpg"), "x");

        var games = GameLibrary.Scan(new GameSources { SteamRoots = [root], WithArt = true });
        var dota = games.Single(g => g.Name == "Dota 2").Art;
        Assert.EndsWith("library_capsule.jpg", dota.Cover);
        Assert.EndsWith("library_hero.jpg", dota.Banner);
        Assert.EndsWith("logo.png", dota.Logo);
        Assert.EndsWith("730_library_600x900.jpg", games.Single(g => g.Name == "Counter-Strike 2").Art.Cover);
        // The agent's minute check doesn't look for pictures.
        Assert.All(GameLibrary.Scan(Sources(root)), g => Assert.Equal(GameArt.None, g.Art));
    }

    private static string Epic(params string[] items)
    {
        var dir = TestEnvironment.NewFolder("epic-manifests");
        for (int i = 0; i < items.Length; i++) File.WriteAllText(Path.Combine(dir, $"{i}.item"), items[i]);
        return dir;
    }

    /// <summary>A real folder holding the program the manifest names (Binaries/Win64/Launcher.exe).</summary>
    private static string EpicFolder(string name)
    {
        var dir = TestEnvironment.NewFolder(name);
        Directory.CreateDirectory(Path.Combine(dir, "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(dir, "Binaries", "Win64", "Launcher.exe"), "x");
        return dir;
    }

    private static string EpicItem(string name, string location, long size, string app, string main = "", bool incomplete = false) => $$"""
        {
            "DisplayName": "{{name}}",
            "InstallLocation": "{{location.Replace(@"\", @"\\")}}",
            "LaunchExecutable": "Binaries/Win64/Launcher.exe",
            "InstallSize": {{size}},
            "AppVersionString": "++Prime+Update60-CL-529371",
            "AppName": "{{app}}",
            "MainGameAppName": "{{main}}",
            "CatalogNamespace": "ns1",
            "CatalogItemId": "item1",
            "bIsIncompleteInstall": {{(incomplete ? "true" : "false")}}
        }
        """;

    [Fact]
    public void Epic_games_come_from_its_manifests_wherever_installed_without_their_trademark_signs()
    {
        var folder = EpicFolder("rocketleague");
        var games = GameLibrary.Scan(Sources(epic: Epic(EpicItem("Rocket League®", folder, 43_679_198_548, "Sugar"))));
        var rl = Assert.Single(games);
        Assert.Equal("Rocket League", rl.Name);
        Assert.Equal(Launcher.Epic, rl.Launcher);
        Assert.Equal(folder, rl.InstallDir);
        Assert.Equal("++Prime+Update60-CL-529371", rl.Version);
        Assert.Equal("com.epicgames.launcher://apps/ns1%3Aitem1%3ASugar?action=launch&silent=true", rl.LaunchTarget);
        // Epic writes nothing to Windows' list of programs: a game is removed in its library.
        Assert.Equal("com.epicgames.launcher://store/library", rl.UninstallTarget);
    }

    [Fact]
    public void An_epic_download_in_progress_and_a_dlc_are_not_games_of_their_own()
    {
        var games = GameLibrary.Scan(Sources(epic: Epic(
            EpicItem("Half Done", @"E:\half", 10, "Half", incomplete: true),
            EpicItem("Season Pass", @"E:\rocketleague\dlc", 10, "Pass", main: "Sugar"))));
        Assert.Empty(games);
    }

    [Fact]
    public void An_epic_manifest_left_behind_by_a_deleted_or_half_uninstalled_game_is_not_installed()
    {
        // A user's Fortnite: Epic's manifest still said 75 GB, the folder it named was empty.
        var empty = TestEnvironment.NewFolder("fortnite");
        var games = GameLibrary.Scan(Sources(epic: Epic(
            EpicItem("Fortnite", empty, 75_000_000_000, "Fortnite"),
            EpicItem("Gone", Path.Combine(empty, "gone"), 10, "Gone"),
            EpicItem("Rocket League", EpicFolder("rocketleague"), 10, "Sugar"))));
        Assert.Equal("Rocket League", Assert.Single(games).Name);
    }

    [Fact]
    public void A_steam_manifest_whose_game_folder_is_empty_or_gone_is_not_installed()
    {
        var (root, library) = Steam(("570", "Dota 2", "dota 2 beta", "1", true), ("2138720", "REMATCH", "Rematch", "1", false), ("730", "Counter-Strike 2", "csgo", "1", true));
        File.Delete(Path.Combine(library, "steamapps", "common", "dota 2 beta", "game.exe"));
        Directory.Delete(Path.Combine(library, "steamapps", "common", "csgo"), recursive: true);
        Assert.Equal("REMATCH", Assert.Single(GameLibrary.Scan(Sources(root))).Name);
    }

    private static readonly UninstallEntry Valorant = new("Riot Game valorant.live", "VALORANT", "Riot Games, Inc", "E:/Riot Games/VALORANT/live",
        "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=valorant --uninstall-patchline=live", null, 33_533_142);

    [Fact]
    public void Riot_games_come_from_windows_list_with_a_launch_made_from_their_uninstall_command()
    {
        var game = Assert.Single(GameLibrary.Scan(Sources(entries: Valorant)));
        Assert.Equal(Launcher.Riot, game.Launcher);
        Assert.Equal(@"E:\Riot Games\VALORANT\live", game.InstallDir);
        Assert.Equal(33_533_142L * 1024, game.SizeBytes);
        Assert.Equal(@"C:\Riot Games\Riot Client\RiotClientServices.exe", game.LaunchTarget);
        Assert.Equal("--launch-product=valorant --launch-patchline=live", game.LaunchArgs);
        Assert.Equal("--uninstall-product=valorant --uninstall-patchline=live", game.UninstallArgs);
    }

    [Fact]
    public void One_game_added_through_epic_but_run_by_the_riot_client_shows_once_as_riots()
    {
        // VALORANT on the user's PC: Epic's entry is a 71 MB starter that opens the Riot Client; the 32 GB game is Riot's.
        var games = GameLibrary.Scan(Sources(epic: Epic(EpicItem("VALORANT", EpicFolder("VALORANT"), 73_632_944, "valorant")), entries: Valorant));
        var game = Assert.Single(games);
        Assert.Equal(Launcher.Riot, game.Launcher);
        Assert.Equal(Launcher.Epic, game.AddedThrough);
        Assert.Equal(@"E:\Riot Games\VALORANT\live", game.InstallDir);
    }

    [Theory]
    [InlineData("Uplay Install 66088", "Steep", "Ubisoft", @"C:\Ubisoft\Steep", "", Launcher.Ubisoft)]
    [InlineData("1207658924_is1", "The Witcher 3", "GOG.com", @"C:\GOG Games\The Witcher 3", "\"C:\\GOG Games\\The Witcher 3\\unins000.exe\"", Launcher.Gog)]
    [InlineData("Diablo IV", "Diablo IV", "Blizzard Entertainment", @"C:\Program Files (x86)\Diablo IV",
        "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=fenris --displayname=\"Diablo IV\"", Launcher.BattleNet)]
    public void Ubisoft_gog_and_battlenet_games_come_from_windows_list(string key, string name, string publisher, string dir, string un, Launcher launcher)
    {
        var game = Assert.Single(GameLibrary.Scan(Sources(entries: new UninstallEntry(key, name, publisher, dir, un, "1.0", 1000))));
        Assert.Equal(launcher, game.Launcher);
        Assert.Equal(name, game.Name);
        if (launcher == Launcher.Ubisoft)
        {
            Assert.Equal("uplay://launch/66088/0", game.LaunchTarget);
            Assert.Equal("uplay://uninstall/66088", game.UninstallTarget);
        }
    }

    [Fact]
    public void The_launchers_themselves_and_ordinary_programs_are_not_games()
    {
        var games = GameLibrary.Scan(Sources(entries:
        [
            new("Riot Game Riot_Client.", "Riot Client", "Riot Games, Inc", @"C:\Riot Games\Riot Client", "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=Riot_Client --uninstall-patchline=", null, null),
            new("Battle.net", "Battle.net", "Blizzard Entertainment", @"C:\Program Files (x86)\Battle.net", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=battle.net", null, null),
            new("7-Zip", "7-Zip 26.01", "Igor Pavlov", @"C:\Program Files\7-Zip", "\"C:\\Program Files\\7-Zip\\Uninstall.exe\"", "26.01", 6000),
        ]));
        Assert.Empty(games);
    }

    [Fact]
    public void A_steam_game_listed_by_windows_too_is_steams_and_shows_once()
    {
        var (root, library) = Steam(("570", "Dota 2", "dota 2 beta", "1", true));
        var dir = Path.Combine(library, "steamapps", "common", "dota 2 beta");
        var games = GameLibrary.Scan(Sources(root, entries: new UninstallEntry("Steam App 570", "Dota 2", "Valve", dir, "steam://uninstall/570", null, null)));
        Assert.Equal(Launcher.Steam, Assert.Single(games).Launcher);
    }

    [Theory]
    [InlineData("RocketLeague_EAC.exe")]
    [InlineData("TslGame_BE.exe")]
    [InlineData("EasyAntiCheat_EOS.exe")]
    public void A_games_anti_cheat_starter_in_its_folder_is_not_the_game(string exe)
    {
        Assert.Equal(AppCategory.Launcher, AppCatalog.Classify(exe.ToLowerInvariant(), @"E:\Epic Games\rocketleague\Binaries\Win64\" + exe));
        Assert.Equal(AppCategory.Launcher, AppCatalog.Reclassified(exe.ToLowerInvariant(), @"E:\Epic Games\rocketleague\Binaries\Win64\" + exe, AppCategory.Game));
    }

    [Fact]
    public void A_game_whose_manifest_sits_in_two_libraries_shows_once()
    {
        // One left behind by moving the game: two entries under one key stopped every change being recorded.
        var (root, _) = Steam(("570", "Dota 2", "dota 2 beta", "1", false), ("570", "Dota 2", "dota 2 beta", "1", true));
        Assert.Equal("steam:570", Assert.Single(GameLibrary.Scan(Sources(root))).Key);
    }

    [Fact]
    public void A_program_listed_with_an_empty_folder_costs_only_itself()
    {
        var broken = new UninstallEntry("Riot Game lol.live", "League of Legends", "Riot Games, Inc", "\"\"",
            "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=league_of_legends --uninstall-patchline=live", null, null);
        Assert.Equal("VALORANT", Assert.Single(GameLibrary.Scan(Sources(entries: [broken, Valorant]))).Name);
    }

    [Fact]
    public void A_launcher_that_cant_be_read_leaves_out_its_games_and_keeps_the_rest()
    {
        var games = GameLibrary.Scan(new GameSources { SteamRoots = [@"Z:\nowhere"], EpicManifests = @"Z:\nowhere", UninstallEntries = () => throw new IOException("no"), });
        Assert.Empty(games);
    }

    [Theory]
    [InlineData("\"C:\\a b\\c.exe\" --x y", "C:\\a b\\c.exe", "--x y")]
    [InlineData("C:\\a\\c.exe --x", "C:\\a\\c.exe", "--x")]
    [InlineData("\"C:\\a\\c.exe\"", "C:\\a\\c.exe", null)]
    [InlineData("steam://uninstall/570", "steam://uninstall/570", null)]
    public void A_command_splits_into_its_program_and_arguments(string command, string file, string? args) =>
        Assert.Equal((file, args), GameLibrary.SplitCommand(command));

    [Theory]
    [InlineData(@"E:\rocketleague\Binaries\Win64\RocketLeague.exe", @"E:\rocketleague", true)]
    [InlineData(@"E:/Riot Games/VALORANT/live/VALORANT.exe", @"E:\Riot Games\VALORANT\live", true)]
    [InlineData(@"E:\rocketleague2\x.exe", @"E:\rocketleague", false)]
    public void A_program_is_inside_a_games_folder_whatever_the_slashes(string path, string dir, bool inside) =>
        Assert.Equal(inside, GameLibrary.IsUnder(path, dir));

    // ── What the catalogue makes of it ──

    [Fact]
    public void A_game_a_launcher_lists_is_a_game_wherever_its_folder_is()
    {
        var before = AppCatalog.InstalledGames;
        try
        {
            AppCatalog.InstalledGames = [new InstalledGame { Key = "epic:Sugar", Launcher = Launcher.Epic, Name = "Rocket League", InstallDir = @"E:\rocketleague" }];
            Assert.Equal(AppCategory.Game, AppCatalog.Classify("RocketLeague.exe", @"E:\rocketleague\Binaries\Win64\RocketLeague.exe"));
            // Filed as Other by an older version: put right, and its sessions with it.
            Assert.Equal(AppCategory.Game, AppCatalog.Reclassified("RocketLeague.exe", @"E:\rocketleague\Binaries\Win64\RocketLeague.exe", AppCategory.Other));
        }
        finally { AppCatalog.InstalledGames = before; }
    }

    [Theory]
    [InlineData("EpicWebHelper.exe", @"C:\Program Files (x86)\Epic Games\Launcher\Engine\Binaries\Win64\EpicWebHelper.exe")]
    [InlineData("EOSBootStrapper.exe", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Extras\EOSBootStrapper\EOSBootStrapper.exe")]
    [InlineData("EpicOnlineServicesUserHelper.exe", @"C:\Program Files (x86)\Epic Games\Epic Online Services\EpicOnlineServicesUserHelper.exe")]
    [InlineData("RiotClientCrashHandler.exe", @"C:\Riot Games\Riot Client\RiotClientCrashHandler.exe")]
    public void A_launchers_own_programs_are_launchers_not_games(string exe, string path)
    {
        Assert.Equal(AppCategory.Launcher, AppCatalog.Classify(exe, path));
        Assert.Equal(AppCategory.Launcher, AppCatalog.Reclassified(exe, path, AppCategory.Game));
    }

    [Theory]
    [InlineData("start_protected_game.exe", AppCategory.Launcher)]
    [InlineData("EAAntiCheat.GameServiceLauncher.exe", AppCategory.Launcher)]
    [InlineData("wallpaper64.exe", AppCategory.Other)]
    [InlineData("stremio-shell-ng.exe", AppCategory.Media)]
    public void Programs_installed_beside_games_that_arent_games_are_filed_as_what_they_are(string exe, AppCategory category)
    {
        var path = @"E:\SteamLibrary\steamapps\common\Some Game\" + exe;
        Assert.Equal(category, AppCatalog.Classify(exe, path));
        Assert.Equal(category, AppCatalog.Reclassified(exe, path, AppCategory.Game));
    }

    [Fact]
    public void A_game_learned_from_play_keeps_being_a_game()
    {
        // Full screen with the graphics card busy made it a game; nothing says otherwise for certain.
        Assert.Null(AppCatalog.Reclassified("fumes.exe", @"C:\Users\x\Downloads\FUMES\FUMES.exe", AppCategory.Game));
    }
}

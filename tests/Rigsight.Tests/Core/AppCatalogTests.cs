using Rigsight.Core.Apps;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

public sealed class AppCatalogTests
{
    [Theory]
    [InlineData("chrome.exe", AppCategory.Browser)]
    [InlineData("msedge.exe", AppCategory.Browser)]
    [InlineData("firefox.exe", AppCategory.Browser)]
    [InlineData("opera_gx.exe", AppCategory.Browser)]
    [InlineData("zen.exe", AppCategory.Browser)]
    [InlineData("code.exe", AppCategory.Development)]
    [InlineData("devenv.exe", AppCategory.Development)]
    [InlineData("windowsterminal.exe", AppCategory.Development)]
    [InlineData("docker desktop.exe", AppCategory.Development)]
    [InlineData("notepad++.exe", AppCategory.Development)]
    [InlineData("claude.exe", AppCategory.Development)]
    [InlineData("discord.exe", AppCategory.Communication)]
    [InlineData("ms-teams.exe", AppCategory.Communication)]
    [InlineData("olk.exe", AppCategory.Communication)]
    [InlineData("spotify.exe", AppCategory.Media)]
    [InlineData("obs64.exe", AppCategory.Media)]
    [InlineData("premiere pro.exe", AppCategory.Media)]
    [InlineData("blender.exe", AppCategory.Media)]
    [InlineData("steam.exe", AppCategory.Launcher)]
    [InlineData("steamwebhelper.exe", AppCategory.Launcher)]
    [InlineData("battle.net.exe", AppCategory.Launcher)]
    [InlineData("playnite.desktopapp.exe", AppCategory.Launcher)]
    [InlineData("winword.exe", AppCategory.Productivity)]
    [InlineData("figma.exe", AppCategory.Productivity)]
    [InlineData("notepad.exe", AppCategory.Productivity)]
    [InlineData("explorer.exe", AppCategory.System)]
    [InlineData("taskmgr.exe", AppCategory.System)]
    [InlineData("rigsight.exe", AppCategory.System)]
    [InlineData("rigsight.agent.exe", AppCategory.System)]
    public void Known_apps_have_their_category(string exe, AppCategory expected)
    {
        Assert.Equal(expected, AppCatalog.Classify(exe, null));
        Assert.Equal(expected, AppCatalog.Classify(exe.ToUpperInvariant(), null));
        Assert.Equal(expected, AppCatalog.Classify(char.ToUpperInvariant(exe[0]) + exe[1..], @"D:\Anywhere\" + exe));
    }

    [Fact]
    public void Rigsight_itself_is_part_of_the_system()
    {
        Assert.Equal(AppCategory.System, AppCatalog.Classify("Rigsight.exe", @"C:\Program Files\Rigsight\Rigsight.exe"));
        Assert.Equal(AppCategory.System, AppCatalog.Classify("Rigsight.Agent.exe", @"C:\Program Files\Rigsight\Rigsight.Agent.exe"));
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteClient-Win64-Shipping.exe")]
    [InlineData(@"C:\GOG Galaxy\Games\Witcher 3\witcher3.exe")]
    [InlineData(@"C:\Riot Games\VALORANT\live\VALORANT.exe")]
    [InlineData(@"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe")]
    [InlineData(@"C:\Program Files\EA Games\Battlefield 2042\BF2042.exe")]
    [InlineData(@"E:\Games\Hades\Hades.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Far Cry 6\bin\FarCry6.exe")]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Games\Overwatch\Overwatch.exe")]
    [InlineData(@"C:\Program Files\Rockstar Games\Grand Theft Auto V\GTA5.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.MinecraftUWP_1.21\Minecraft.Windows.exe")]
    [InlineData(@"C:\Program Files\HoYoPlay\games\ZenlessZoneZero Game\ZenlessZoneZero.exe")]
    [InlineData(@"D:\Genshin Impact\Genshin Impact Game\GenshinImpact.exe")]
    [InlineData(@"D:\STEAMLIBRARY\STEAMAPPS\COMMON\X\X.EXE")]
    public void Apps_in_game_libraries_are_games(string path) =>
        Assert.Equal(AppCategory.Game, AppCatalog.Classify(Path.GetFileName(path), path));

    [Theory]
    [InlineData("UnityCrashHandler64.exe")]
    [InlineData("CrashReportClient.exe")]
    [InlineData("EasyAntiCheat.exe")]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]
    public void Helpers_in_game_folders_are_not_games(string exe) =>
        Assert.Equal(AppCategory.Other, AppCatalog.Classify(exe, @"D:\SteamLibrary\steamapps\common\Some Game\" + exe));

    [Fact]
    public void A_known_app_in_a_game_folder_keeps_its_category() =>
        Assert.Equal(AppCategory.Launcher, AppCatalog.Classify("steam.exe", @"C:\Games\Steam\steam.exe"));

    [Fact]
    public void Apps_in_the_Windows_folder_are_system()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(AppCategory.System, AppCatalog.Classify("dllhost.exe", Path.Combine(windows, "System32", "dllhost.exe")));
        Assert.Equal(AppCategory.System, AppCatalog.Classify("x.exe", Path.Combine(windows.ToUpperInvariant(), "x.exe")));
    }

    [Theory]
    [InlineData("mystery.exe", null)]
    [InlineData("mystery.exe", @"C:\Program Files\Mystery\mystery.exe")]
    [InlineData("mystery.exe", @"C:\Users\me\AppData\Local\mystery.exe")]
    [InlineData("", null)]
    [InlineData("gamebar.exe", @"C:\Program Files\gamesmith\gamebar.exe")]
    public void Anything_else_is_other(string exe, string? path) => Assert.Equal(AppCategory.Other, AppCatalog.Classify(exe, path));

    [Fact]
    public void The_users_choice_wins_over_everything()
    {
        var overrides = new Dictionary<string, AppCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome.exe"] = AppCategory.Game,
            ["eldenring.exe"] = AppCategory.Media,
            ["mystery.exe"] = AppCategory.Productivity,
        };
        Assert.Equal(AppCategory.Game, AppCatalog.Classify("Chrome.exe", null, overrides));
        Assert.Equal(AppCategory.Media, AppCatalog.Classify("eldenring.exe", @"D:\steamapps\common\ER\eldenring.exe", overrides));
        Assert.Equal(AppCategory.Productivity, AppCatalog.Classify("MYSTERY.EXE", null, overrides));
        Assert.Equal(AppCategory.Browser, AppCatalog.Classify("firefox.exe", null, overrides));
        Assert.Equal(AppCategory.Browser, AppCatalog.Classify("firefox.exe", null, new Dictionary<string, AppCategory>()));
    }

    [Fact]
    public void Settings_overrides_work_in_any_case_after_loading()
    {
        var s = SettingsStore.Deserialize("""{ "SettingsVersion": 6, "AppCategories": { "Chrome.exe": "Game" } }""");
        Assert.Equal(AppCategory.Game, AppCatalog.Classify("chrome.exe", null, s.AppCategories));
    }

    [Theory]
    [InlineData("eldenring.exe", "Eldenring")]
    [InlineData("my_cool-game.exe", "My Cool Game")]
    [InlineData("HADES.exe", "HADES")]
    [InlineData("noextension", "Noextension")]
    [InlineData("two.dots.exe", "Two.Dots")]
    [InlineData("a", "A")]
    [InlineData(".exe", ".exe")]
    [InlineData("", "")]
    [InlineData("__.exe", "  ")]
    public void Fallback_names(string exe, string expected) => Assert.Equal(expected, AppCatalog.FallbackName(exe));

    [Fact]
    public void Fallback_names_are_the_same_in_every_culture()
    {
        using var _ = new CultureScope("tr-TR");
        Assert.Equal("Idle", AppCatalog.FallbackName("idle.exe")); // not "İdle"
    }

    [Theory]
    [InlineData("screenclippinghost.exe", "Snipping Tool")]
    [InlineData("SnippingTool.exe", "Snipping Tool")]
    [InlineData("searchhost.exe", "Windows Search")]
    [InlineData("StartMenuExperienceHost.exe", "Start menu")]
    [InlineData("shellexperiencehost.exe", "Windows taskbar")]
    [InlineData("textinputhost.exe", "Emoji & clipboard panel")]
    [InlineData("LockApp.exe", "Lock screen")]
    [InlineData("consent.exe", "Admin prompt")]
    [InlineData("dwm.exe", "Windows display (DWM)")]
    [InlineData("svchost.exe", "Windows service")]
    [InlineData("SystemSettings.exe", "Settings")]
    [InlineData("wallpaper64.exe", "Wallpaper Engine")]
    [InlineData("wallpaperservice32.exe", "Wallpaper Engine service")]
    public void Windows_helpers_have_readable_names(string exe, string expected)
    {
        Assert.Equal(expected, AppCatalog.KnownName(exe));
        Assert.Equal(expected, AppCatalog.ResolveName(exe, @"C:\does\not\matter.exe"));
    }

    [Fact]
    public void Other_apps_have_no_built_in_name() => Assert.Null(AppCatalog.KnownName("chrome.exe"));

    [Theory]
    [InlineData("Application")]
    [InlineData("electron")]
    [InlineData("Chromium")]
    [InlineData("Node.js")]
    [InlineData("Java(TM) Platform SE binary")]
    [InlineData("OpenJDK Platform binary")]
    [InlineData("Python")]
    [InlineData("Qt")]
    [InlineData("CEF")]
    [InlineData("Unity")]
    [InlineData("LAUNCHER")]
    [InlineData("Microsoft® Windows® Operating System")]
    [InlineData("Some operating system thing")]
    public void Generic_descriptions_say_nothing(string name) => Assert.True(AppCatalog.IsGenericName(name));

    [Theory]
    [InlineData("Google Chrome")]
    [InlineData("Discord")]
    [InlineData("Unity Hub")]
    [InlineData("Epic Games Launcher")]
    [InlineData("")]
    public void Real_names_are_not_generic(string name) => Assert.False(AppCatalog.IsGenericName(name));

    [Fact]
    public void Without_a_readable_exe_the_name_comes_from_the_file_name()
    {
        Assert.Equal("Eldenring", AppCatalog.ResolveName("eldenring.exe", null));
        Assert.Equal("Eldenring", AppCatalog.ResolveName("eldenring.exe", @"C:\no\such\eldenring.exe"));
        var text = Path.Combine(TestEnvironment.NewFolder("apps"), "not-an-exe.exe");
        File.WriteAllText(text, "just text");
        Assert.Equal("Not An Exe", AppCatalog.ResolveName("not-an-exe.exe", text));
        Assert.Equal("Bad Path", AppCatalog.ResolveName("bad_path.exe", "\0:bad"));
    }

    [Fact]
    public void A_real_exe_is_named_by_its_description()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        if (!File.Exists(notepad)) Assert.Skip("No notepad.exe");
        var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(notepad).FileDescription?.Trim();
        if (string.IsNullOrEmpty(description) || AppCatalog.IsGenericName(description)) Assert.Skip("notepad.exe has no usable description here");
        Assert.Equal(description, AppCatalog.ResolveName("notepad.exe", notepad));
    }

    public static TheoryData<AppCategory> Categories => [.. Enum.GetValues<AppCategory>()];

    [Theory]
    [MemberData(nameof(Categories))]
    public void Every_category_has_a_label_and_a_colour(AppCategory category)
    {
        Assert.False(string.IsNullOrWhiteSpace(AppCatalog.Label(category)));
        Assert.Matches("^#[0-9A-F]{6}$", AppCatalog.Color(category));
    }

    [Fact]
    public void Categories_look_different_from_each_other()
    {
        var all = Enum.GetValues<AppCategory>();
        Assert.Equal(all.Length, all.Select(AppCatalog.Label).Distinct().Count());
        Assert.Equal(all.Length, all.Select(AppCatalog.Color).Distinct().Count());
    }

    [Theory]
    [InlineData(AppCategory.Game, "Games")]
    [InlineData(AppCategory.Browser, "Browsing")]
    [InlineData(AppCategory.Communication, "Chat & calls")]
    [InlineData(AppCategory.Media, "Media & creative")]
    [InlineData(AppCategory.Other, "Other")]
    [InlineData((AppCategory)99, "Other")]
    public void Labels(AppCategory category, string expected) => Assert.Equal(expected, AppCatalog.Label(category));

    [Theory]
    [InlineData(AppCategory.Game, "while playing Hades")]
    [InlineData(AppCategory.Browser, "while browsing in Hades")]
    [InlineData(AppCategory.Media, "while Hades was playing")]
    [InlineData(AppCategory.Communication, "while on Hades")]
    [InlineData(AppCategory.Development, "while working in Hades")]
    [InlineData(AppCategory.Productivity, "while working in Hades")]
    [InlineData(AppCategory.Launcher, "while in Hades")]
    [InlineData(AppCategory.System, "while in Hades")]
    [InlineData(AppCategory.Other, "while using Hades")]
    [InlineData((AppCategory)99, "while using Hades")]
    public void Activity_words_fit_the_kind_of_app(AppCategory category, string expected) =>
        Assert.Equal(expected, ActivityWords.While("Hades", category));

    [Fact]
    public void Activity_words_keep_the_app_name_as_is() =>
        Assert.Equal("while playing ELDEN RING™: Nightreign", ActivityWords.While("ELDEN RING™: Nightreign", AppCategory.Game));
}

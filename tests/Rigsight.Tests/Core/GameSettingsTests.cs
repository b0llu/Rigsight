using Rigsight.Core.Games;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

/// <summary>
/// A game's own saved settings, found from the game itself and read whatever their format (the person's ask,
/// 2026-10-25: "pull each game's settings so the AI can say how to run it better"). The files are laid out as the real
/// games on the person's PC keep them; only graphics settings are kept, and nothing that names the person.
/// </summary>
public class GameSettingsTests
{
    private sealed record Pc(string Root, GameSettings.Places Places, string Games);

    private static Pc NewPc(Func<string, string, (string, DateTime, IReadOnlyList<GameSetting>)?>? unity = null)
    {
        var root = TestEnvironment.NewFolder("game-settings");
        string Dir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }
        return new(root, new GameSettings.Places
        {
            LocalAppData = Dir("Local"), RoamingAppData = Dir("Roaming"), LocalLow = Dir("LocalLow"), Documents = Dir("Documents"),
            SavedGames = Dir("Saved Games"), Steam = Dir("Steam"), UnityPrefs = unity ?? ((_, _) => null),
        }, Dir("Games"));
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Value(GameSettingsRead read, string label) => Assert.Single(read.Facts, f => f.Label == label).Value;

    [Fact]
    public void An_Unreal_game_is_read_from_its_project_folder_and_the_newest_of_its_copies_wins()
    {
        var pc = NewPc();
        // REMATCH: the program is Runtime\Binaries\Win64\RuntimeClient-Win64-Shipping.exe, its settings under %LOCALAPPDATA%\Runtime.
        string exe = Write(Path.Combine(pc.Games, "Rematch", "Runtime", "Binaries", "Win64", "RuntimeClient-Win64-Shipping.exe"), "x");
        string old = Write(Path.Combine(pc.Places.LocalAppData, "Runtime", "Saved", "Config", "Windows", "GameUserSettings.ini"), "ResolutionSizeX=1920\nResolutionSizeY=1080\nbUseVSync=True\n");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));
        Write(Path.Combine(pc.Places.LocalAppData, "Runtime", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"), """
            [/Script/Engine.GameUserSettings]
            bUseVSync=False
            ResolutionSizeX=2560
            ResolutionSizeY=1440
            LastConfirmedFullscreenMode=1
            PreferredFullscreenMode=1
            FrameRateLimit=0.000000
            AudioQualityLevel=0
            DefaultMonitorDeviceID="MONITOR\\GSM5B7F\\{4d36e96e-e325-11ce-bfc1-08002be10318}\\0002"
            [ScalabilityGroups]
            sg.ShadowQuality=3
            sg.TextureQuality=3
            sg.EffectsQuality=2
            sg.FoliageQuality=3
            """);

        var read = GameSettings.Read("REMATCH", exe, Path.Combine(pc.Games, "Rematch"), "2138720", pc.Places);

        Assert.EndsWith(@"WindowsClient\GameUserSettings.ini", Assert.Single(read.Files).Path);
        Assert.Equal("2560 × 1440", Value(read, GameSettings.Resolution));
        Assert.Equal(GameSettings.Borderless, Value(read, GameSettings.DisplayMode));
        Assert.Equal(GameSettings.Off, Value(read, GameSettings.VSync));
        Assert.Equal(GameSettings.NoLimit, Value(read, GameSettings.FrameLimit));
        Assert.Equal("Mostly Epic", Value(read, GameSettings.Quality));
        // Sound and the monitor's id are never kept.
        Assert.DoesNotContain(read.Files[0].Settings, s => s.Key.Contains("Audio") || s.Key.Contains("Monitor"));
    }

    [Fact]
    public void A_frame_limit_switched_off_is_no_limit_a_menu_word_for_VSync_is_read_and_path_tracing_is_named()
    {
        var pc = NewPc();
        // Cyberpunk 2077 keeps the slider's last number beside its switch, and VSync as the menu's own text (2026-10-25:
        // the person's real file said "MaximumFPS_OnOff": false, "MaximumFPS_Value": 30, and the AI was told 30 fps).
        Write(Path.Combine(pc.Places.LocalAppData, "CD Projekt Red", "Cyberpunk 2077", "UserSettings.json"), """
            {"version": 3, "data": [
              {"group_name": "/video/display", "options": [
                {"name": "VSync", "type": "Name", "value": "UI-Settings-Video-QualitySetting-Off"},
                {"name": "MaximumFPS_OnOff", "type": "Bool", "value": false},
                {"name": "MaximumFPS_Value", "type": "Int", "value": 30},
                {"name": "Resolution", "type": "Name", "value": "2560x1440"}]},
              {"group_name": "/graphics/presets", "options": [
                {"name": "ResolutionScaling", "type": "Name", "value": "DLSS"},
                {"name": "DLSS", "type": "Name", "value": "Quality"},
                {"name": "TextureQuality", "type": "Name", "value": "High"}]},
              {"group_name": "/graphics/raytracing", "options": [
                {"name": "RayTracing", "type": "Bool", "value": true},
                {"name": "RayTracedPathTracing", "type": "Bool", "value": true},
                {"name": "RayTracedPathTracingForPhotoMode", "type": "Bool", "value": false}]}]}
            """);

        var read = GameSettings.Read("Cyberpunk 2077", null, null, null, pc.Places);

        Assert.Equal(GameSettings.NoLimit, Value(read, GameSettings.FrameLimit));
        Assert.Equal(GameSettings.Off, Value(read, GameSettings.VSync));
        Assert.Equal("2560 × 1440", Value(read, GameSettings.Resolution));
        // Path tracing on is said by name, not only "Ray tracing On".
        Assert.Equal(GameSettings.PathTracing, Value(read, GameSettings.RayTracing));
    }

    [Fact]
    public void A_Source_game_is_read_from_Steam_s_copy_and_not_the_defaults_in_its_install_folder()
    {
        var pc = NewPc();
        string install = Path.Combine(pc.Games, "dota 2 beta");
        // The copy the game shipped with, never touched since.
        Write(Path.Combine(install, "game", "dota", "cfg", "video.txt"), "\"video.cfg\"\n{\n\t\"setting.defaultres\"\t\t\"1280\"\n\t\"setting.defaultresheight\"\t\t\"720\"\n\t\"setting.fullscreen\"\t\t\"1\"\n\t\"setting.mat_vsync\"\t\t\"1\"\n\t\"setting.r_ssao\"\t\t\"true\"\n}\n");
        Write(Path.Combine(pc.Places.Steam!, "userdata", "334004950", "570", "local", "cfg", "video.txt"), """
            "video.cfg"
            {
            	"VendorID"		"4318"
            	"DeviceID"		"8712"
            	"setting.defaultres"		"2560"
            	"setting.defaultresheight"		"1440"
            	"setting.fullscreen"		"0"
            	"setting.nowindowborder"		"1"
            	"setting.mat_vsync"		"0"
            	"setting.r_ssao"		"true"
            	"setting.r_dota_fsr_upsample"		"0"
            }
            """);

        var read = GameSettings.Read("Dota 2", null, install, "570", pc.Places);

        Assert.Contains(@"userdata\334004950\570", Assert.Single(read.Files).Path);
        Assert.Equal("2560 × 1440", Value(read, GameSettings.Resolution));
        Assert.Equal(GameSettings.Borderless, Value(read, GameSettings.DisplayMode));
        Assert.Equal(GameSettings.Off, Value(read, GameSettings.VSync));
        Assert.Equal(GameSettings.Off, Value(read, GameSettings.Upscaling));
        Assert.DoesNotContain(read.Files[0].Settings, s => s.Key is "VendorID" or "DeviceID");
    }

    [Fact]
    public void A_settings_file_in_a_folder_named_like_the_game_is_found_even_with_its_maker_s_short_name()
    {
        var pc = NewPc();
        // Apex Legends keeps Saved Games\Respawn\Apex\local\videoconfig.txt, ended by a zero byte.
        string file = Path.Combine(pc.Places.SavedGames, "Respawn", "Apex", "local", "videoconfig.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [.. System.Text.Encoding.UTF8.GetBytes("\"VideoConfig\"\n{\n\t\"setting.defaultres\"\t\t\"2560\"\n\t\"setting.defaultresheight\"\t\t\"1440\"\n\t\"setting.fullscreen\"\t\t\"1\"\n\t\"setting.mat_vsync_mode\"\t\t\"0\"\n\t\"setting.shadow_enable\"\t\t\"0\"\n\t\"setting.sound_volume\"\t\t\"0.8\"\n}\n"), 0]);
        // Rainbow Six keeps "My Games\Rainbow Six - Siege\<account id>\GameSettings.ini".
        Write(Path.Combine(pc.Places.Documents, "My Games", "Rainbow Six - Siege", "980fd82c-129c-4ea1-9f5a-47bf6db56e8c", "GameSettings.ini"),
            "[DISPLAY]\nResolutionWidth=1920\nResolutionHeight=1080\nVSync=0\nFPSLimit=0\nWindowMode=2\n[GRAPHICS]\nShadowQuality=2\nTextureQuality=3\n");

        var apex = GameSettings.Read("Apex Legends", null, null, null, pc.Places);
        Assert.Equal("2560 × 1440", Value(apex, GameSettings.Resolution));
        Assert.Equal(GameSettings.Fullscreen, Value(apex, GameSettings.DisplayMode));
        Assert.DoesNotContain(apex.Files[0].Settings, s => s.Key.Contains("sound"));

        var siege = GameSettings.Read("Tom Clancy's Rainbow Six Siege", null, null, null, pc.Places);
        Assert.Equal("1920 × 1080", Value(siege, GameSettings.Resolution));
        Assert.Equal(GameSettings.NoLimit, Value(siege, GameSettings.FrameLimit));
        // The account id in the folder's name is never said: the path is kept, shown without the person's folder.
        Assert.Single(siege.Files);
    }

    [Fact]
    public void A_Unity_game_is_read_from_its_saved_preferences_by_maker_and_name()
    {
        string? asked = null;
        var pc = NewPc(unity: (company, product) =>
        {
            asked = $"{company}/{product}";
            return ("HKEY_CURRENT_USER\\Software\\Hopoo Games, LLC\\Risk of Rain 2", DateTime.MinValue, (IReadOnlyList<GameSetting>)
            [
                new("Screenmanager Resolution Width_h182942802", "2560"), new("Screenmanager Resolution Height_h2627697771", "1440"),
                new("Screenmanager Fullscreen mode_h3630240806", "1"), new("UnityGraphicsQuality_h1669003810", "5"), new("Volume_h1", "0.5"),
            ]);
        });
        string exe = Write(Path.Combine(pc.Games, "Risk of Rain 2", "Risk of Rain 2.exe"), "x");
        Write(Path.Combine(pc.Games, "Risk of Rain 2", "Risk of Rain 2_Data", "app.info"), "Hopoo Games, LLC\nRisk of Rain 2\n");

        var read = GameSettings.Read("Risk of Rain 2", exe, Path.Combine(pc.Games, "Risk of Rain 2"), null, pc.Places);

        Assert.Equal("Hopoo Games, LLC/Risk of Rain 2", asked);
        Assert.Equal("2560 × 1440", Value(read, GameSettings.Resolution));
        Assert.Equal(GameSettings.Borderless, Value(read, GameSettings.DisplayMode));
        Assert.Null(read.Saved is { } t && t > DateTime.MinValue.AddYears(1) ? t : null);
    }

    [Fact]
    public void A_Unity_game_that_keeps_only_the_screen_in_the_registry_is_also_read_from_its_own_config_file()
    {
        // Risk of Rain 2 as it is on a real PC: 2 settings in the registry, every graphics one in _Data\Config\config.cfg.
        var pc = NewPc(unity: (_, _) => ("HKEY_CURRENT_USER\\Software\\Hopoo Games, LLC\\Risk of Rain 2", DateTime.MinValue, (IReadOnlyList<GameSetting>)
            [new("Screenmanager Resolution Width_h182942802", "2560"), new("Screenmanager Resolution Height_h2627697771", "1440"), new("Screenmanager Fullscreen mode_h3630240806", "1")]));
        string exe = Write(Path.Combine(pc.Games, "Risk of Rain 2", "Risk of Rain 2.exe"), "x");
        Write(Path.Combine(pc.Games, "Risk of Rain 2", "Risk of Rain 2_Data", "app.info"), "Hopoo Games, LLC\nRisk of Rain 2\n");
        string cfg = Write(Path.Combine(pc.Games, "Risk of Rain 2", "Risk of Rain 2_Data", "Config", "config.cfg"),
            "﻿volume_master 100;\nuser_profile_main cef0f896-1bd2-4a0d-b9ea-e021b5c44a70;\nvsync_count 0;\nwindow_mode Fullscreen;\nresolution 2560x1440x144;\nfps_max 144;\n" +
            "r_shadows All;\nshadow_resolution High;\nanisotropic_filtering Disable;\npp_aa Off;\npp_motionblur 0;\npp_ao 1;\necho \"Loaded archived convars.\";\n");
        Write(Path.Combine(pc.Games, "Risk of Rain 2", "Risk of Rain 2_Data", "Config", "autoexec.cfg"), "echo hi;\n");

        var read = GameSettings.Read("Risk of Rain 2", exe, Path.Combine(pc.Games, "Risk of Rain 2"), null, pc.Places);

        Assert.Contains(read.Files, f => f.Path == cfg);
        Assert.Equal("2560 × 1440", Value(read, GameSettings.Resolution));
        Assert.Equal(GameSettings.Off, Value(read, GameSettings.VSync));
        Assert.Equal("144 fps", Value(read, GameSettings.FrameLimit));
        // As its menu says it, not as Unity's code does (1, a borderless window).
        Assert.Equal(GameSettings.Fullscreen, Value(read, GameSettings.DisplayMode));
        var all = read.Files.SelectMany(f => f.Settings).ToList();
        Assert.Contains(all, s => s.Key == "r_shadows" && s.Value == "All");
        Assert.Contains(all, s => s.Key == "pp_aa" && s.Value == "Off");
        Assert.DoesNotContain(all, s => s.Key.StartsWith("user", StringComparison.Ordinal) || s.Key.StartsWith("volume", StringComparison.Ordinal));
    }

    [Fact]
    public void XML_and_JSON_settings_are_read_by_their_names_and_values()
    {
        var pc = NewPc();
        // No Man's Sky: <Property name=… value=…/> nested, in the install folder.
        string nms = Path.Combine(pc.Games, "No Man's Sky");
        Write(Path.Combine(nms, "Binaries", "SETTINGS", "TKGRAPHICSSETTINGS.MXML"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Data template="TkGraphicsSettings">
              <Property name="FullScreen" value="true" />
              <Property name="Borderless" value="false" />
              <Property name="MonitorNames"><Property name="MonitorNames" value="0|NVIDIA GeForce RTX 3080 Ti|Generic PnP Monitor" _index="0" /></Property>
              <Property name="ResolutionWidth" value="2560" />
              <Property name="ResolutionHeight" value="1440" />
              <Property name="VsyncEx" value="Off" />
              <Property name="DLSSQuality" value="Balanced" />
              <Property name="ShadowQuality" value="TkGraphicsDetailTypes"><Property name="GraphicDetail" value="Ultra" /></Property>
            </Data>
            """);
        var read = GameSettings.Read("No Man's Sky", null, nms, null, pc.Places);
        Assert.Equal("2560 × 1440", Value(read, GameSettings.Resolution));
        Assert.Equal(GameSettings.Fullscreen, Value(read, GameSettings.DisplayMode));
        Assert.Equal(GameSettings.Off, Value(read, GameSettings.VSync));
        Assert.Equal("DLSS Balanced", Value(read, GameSettings.Upscaling));
        Assert.Contains(read.Files[0].Settings, s => s.Key == "ShadowQuality.GraphicDetail" && s.Value == "Ultra");
        Assert.DoesNotContain(read.Files[0].Settings, s => s.Key.Contains("Monitor"));

        // A Godot game's JSON settings in Roaming, under a folder with the Steam account's number.
        Write(Path.Combine(pc.Places.RoamingAppData, "SlayTheSpire2", "steam", "76561198294270678", "settings.save"),
            """{ "fullscreen": true, "vsync": false, "fps_limit": 144, "resolution": "2560x1440", "user_name": "sam", "screenshake": 0, "window_mode": "fullscreen", "msaa": 2 }""");
        var spire = GameSettings.Read("Slay the Spire 2", null, null, null, pc.Places);
        Assert.Equal("144 fps", Value(spire, GameSettings.FrameLimit));
        Assert.Equal("2560 × 1440", Value(spire, GameSettings.Resolution));
        Assert.DoesNotContain(spire.Files[0].Settings, s => s.Key.Contains("user"));
    }

    [Fact]
    public void Nothing_found_is_none_and_a_network_or_launcher_file_isn_t_taken_for_settings()
    {
        var pc = NewPc();
        string steep = Path.Combine(pc.Games, "Steep");
        Write(Path.Combine(steep, "netengine.ini"), "SendBufferSize=65536\nRecvBufferSize=65536\nTickRate=60\n");
        Write(Path.Combine(steep, "launcher_config.json"), """{ "region": "eu", "path": "E:\\Games" }""");
        var read = GameSettings.Read("Steep", null, steep, null, pc.Places);
        Assert.False(read.Found);
        Assert.Empty(read.Facts);
        Assert.Contains("couldn't find the game's saved settings", GameSettings.Text(read, null));
    }

    [Fact]
    public void Values_that_could_name_the_person_or_their_PC_are_left_out()
    {
        var kept = GameSettings.Graphics(
        [
            new("ResolutionSizeX", "2560"), new("LastUserName", "sam"), new("ShaderCachePath", @"C:\Users\sam\cache"),
            new("TextureQuality", "3"), new("ServerRegion", "ap"), new("ShadowQuality", "a1b2c3d4e5f6a7b8"), new("bUseVSync", "False"),
            new("ScreenshotFolder", "D:/shots"), new("PlayerEmailQuality", "x@y.z"),
        ]);
        Assert.Equal(["ResolutionSizeX", "TextureQuality", "bUseVSync"], kept.Select(s => s.Key));
    }

    [Fact]
    public void The_AI_s_text_says_the_settings_by_the_file_s_names_without_the_person_s_folder_and_never_offers_to_change_them()
    {
        var pc = NewPc();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var read = new GameSettingsRead([new(Path.Combine(profile, @"AppData\Local\Runtime\Saved\Config\WindowsClient\GameUserSettings.ini"), new DateTime(2026, 10, 21, 18, 5, 0),
            [new("ResolutionSizeX", "2560"), new("ResolutionSizeY", "1440"), new("sg.ShadowQuality", "3")])],
            [new(GameSettings.Resolution, "2560 × 1440"), new(GameSettings.Quality, "Epic")]);
        string text = GameSettings.Text(read, null);
        Assert.Contains("Resolution 2560 × 1440; Quality Epic.", text);
        Assert.Contains(@"~\AppData\Local\Runtime\Saved\Config\WindowsClient\GameUserSettings.ini", text);
        Assert.DoesNotContain(profile, text);
        Assert.Contains("ResolutionSizeX=2560, ResolutionSizeY=1440, sg.ShadowQuality=3", text);
        Assert.Contains("last saved 21 Oct 2026 6:05 PM", text);
        Assert.Contains("it never changes a game's settings", text);
    }
}

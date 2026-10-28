using Rigsight.Core.Games;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.Core;

/// <summary>
/// Every graphics setting named from the game's own keys, and NVIDIA's pick beside it when the NVIDIA App has one (the
/// person's ask, 2026-10-27: the NVIDIA App showed Risk of Rain 2's 15 settings where RigSight named 4; no list of games is
/// kept, the game is read when its page opens). Laid out as on the person's PC.
/// </summary>
public class GameSettingNamesTests
{
    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private const string Ror2Config = """
        volume_master 100;
        aim_stick_global_scale 1;
        corpses_max 50;
        corpses_disposal OutOfSight;
        exp_and_money_effects 1;
        vsync_count 0;
        window_mode Fullscreen;
        resolution 2560x1440x144;
        fps_max 144;
        r_shadows All;
        r_softparticles 1;
        r_foliagewind 1;
        r_lod_bias 2;
        r_lod_max 0;
        master_texture_limit 0;
        anisotropic_filtering Disable;
        shadow_resolution High;
        shadow_cascades 4;
        shadow_distance 200;
        pp_aa Off;
        pp_motionblur 0;
        pp_sobel_outline 1;
        pp_bloom 1;
        pp_ao 1;
        memhudbudget 3000;
        hud_scale 100;
        """;

    /// <summary>Risk of Rain 2 as on the person's PC: a Unity program with its maker in app.info and every setting in Config\config.cfg.</summary>
    private static (GameSettings.Places Places, string Exe, string Dir) Ror2(string? nvidia = null)
    {
        var root = TestEnvironment.NewFolder("game-setting-names");
        string Dir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }
        string dir = Path.Combine(root, "Games", "Risk of Rain 2");
        string exe = Write(Path.Combine(dir, "Risk of Rain 2.exe"), "x");
        Write(Path.Combine(dir, "Risk of Rain 2_Data", "app.info"), "Hopoo Games, LLC\nRisk of Rain 2\n");
        Write(Path.Combine(dir, "Risk of Rain 2_Data", "Config", "config.cfg"), Ror2Config);
        return (new GameSettings.Places
        {
            LocalAppData = Dir("Local"), RoamingAppData = Dir("Roaming"), LocalLow = Dir("LocalLow"), Documents = Dir("Documents"),
            SavedGames = Dir("Saved Games"), NvidiaBackend = nvidia ?? "",
        }, exe, dir);
    }

    /// <summary>The NVIDIA App's files for a game, shaped as on the person's PC (NvBackend, 2026-10-27).</summary>
    private static string NvidiaBackend(string exe, string dir, string resolution = "2560x1440", string kind = "regular")
    {
        var nv = TestEnvironment.NewFolder("nvidia-backend");
        Write(Path.Combine(nv, "ApplicationStorage.json"), """
            {"Applications":[{"LocalId":1,"Application":{"DisplayName":"Other","ShortName":"other","InstallDirectory":"C:\\Elsewhere\\","DetectedFiles":[]}},
              {"LocalId":731649299,"Application":{"DisplayName":"Risk of Rain 2","ShortName":"risk_of_rain_2","InstallDirectory":DIR,"DetectedFiles":[EXE]}}]}
            """.Replace("DIR", System.Text.Json.JsonSerializer.Serialize(dir + "\\")).Replace("EXE", System.Text.Json.JsonSerializer.Serialize(exe)));
        string game = Path.Combine(nv, "Recommendations", "risk_of_rain_2");
        Write(Path.Combine(game, "profiles_metadata.json"), """{"ce":"c818","ue":"","cv":"1","uv":""}""");
        Write(Path.Combine(game, "c818", "metadata.json"), """
            {"KIND":{"version":"1","pops":{"filename":"pops.pub.tsv"},"ops":[{"resolution":"1000x700","pops":2,"rate":40,"belowMinSpec":false},{"resolution":"RES","pops":3,"rate":40,"belowMinSpec":false}]},"etag":"x"}
            """.Replace("RES", resolution).Replace("KIND", kind));
        Write(Path.Combine(game, "c818", kind, "pops.pub.tsv"), """
            {"settings":[{"Anti-Aliasing":{"type":"ENUM"}},{"Shadow Distance":{"type":"ENUM"}},{"SSAO":{"type":"ENUM"}},{"Display Mode":{"type":"ENUM"}},{"LOD Bias":{"type":"ENUM"}}],
             "pops":[{"values":{"1":"Off","2":"Low","3":"Off","4":"Full-screen","5":"Off"}},
                     {"values":{"1":"FXAA","2":"Medium","3":"On","4":"Full-screen","5":"Medium"}},
                     {"values":{"1":"SMAA","2":"Ultra","3":"On","4":"Full-screen","5":"Ultra"}}]}
            """);
        return nv;
    }

    [Fact]
    public void Risk_of_Rain_2_shows_every_graphics_setting_named_from_its_keys()
    {
        var (places, exe, dir) = Ror2();
        var read = GameSettings.Read("Risk of Rain 2", exe, dir, "632360", places);

        string Row(string label) => Assert.Single(read.Rows, r => r.Label == label).Value;
        Assert.Equal("2560 × 1440", Row("Resolution"));
        Assert.Equal("144", Row("FPS max"));
        Assert.Equal("Off", Row("Anti-aliasing"));
        Assert.Equal("All", Row("Shadows"));
        // Glued words come apart, and a number the game saves stays that number (no list of games says what it means).
        Assert.Equal("On", Row("Soft particles"));
        Assert.Equal("Off", Row("Motion blur"));
        Assert.Equal("On", Row("Ambient occlusion"));
        Assert.Equal("2", Row("LOD bias"));
        Assert.Equal("200", Row("Shadow distance"));
        // Settings that cost frames in a busy run stay, though they aren't graphics by name; a CamelCase word is spaced.
        Assert.Equal("50", Row("Corpses max"));
        Assert.Equal("Out of sight", Row("Corpses disposal"));
        Assert.Equal([GameSettingNames.Screen, GameSettingNames.Quality, GameSettingNames.Shadows, GameSettingNames.Effects, GameSettingNames.Gameplay],
            read.Rows.Select(r => r.Group).Distinct());
        // Nothing the menu's graphics don't show: a controller's aim, the HUD, money pop-ups.
        Assert.DoesNotContain(read.Files.SelectMany(f => f.Settings), s => s.Key is "aim_stick_global_scale" or "memhudbudget" or "exp_and_money_effects" or "hud_scale");
        Assert.Null(read.Nvidia);
        Assert.All(read.Rows, r => Assert.Null(r.Recommended));
    }

    [Fact]
    public void Nvidia_s_pick_sits_beside_each_setting_it_covers_and_only_like_for_like_differences_are_said()
    {
        var (_, exe, dir) = Ror2();
        var places = Ror2(NvidiaBackend(exe, dir)).Places;
        var read = GameSettings.Read("Risk of Rain 2", exe, dir, "632360", places);

        Assert.NotNull(read.Nvidia);
        Assert.Equal("2560x1440", read.Nvidia!.Resolution);
        Assert.Equal((3, 3), (read.Nvidia.Step, read.Nvidia.Steps));
        GameSettingRow Row(string label) => Assert.Single(read.Rows, r => r.Label == label);
        Assert.Equal("SMAA", Row("Anti-aliasing").Recommended);
        Assert.True(Row("Anti-aliasing").Differs);
        // NVIDIA's other name for a setting ("SSAO") finds ours.
        Assert.Equal("On", Row("Ambient occlusion").Recommended);
        Assert.False(Row("Ambient occlusion").Differs);
        // "2" and "200" against NVIDIA's "Ultra" may be the same choice: shown, never called a difference.
        Assert.Equal("Ultra", Row("LOD bias").Recommended);
        Assert.False(Row("LOD bias").Differs);
        Assert.False(Row("Shadow distance").Differs);
        // A setting NVIDIA leaves out has no pick.
        Assert.Null(Row("Bloom").Recommended);
        Assert.Equal(["Anti-aliasing"], read.Differing.Select(r => r.Label));
        Assert.Equal("1 setting differs from NVIDIA's pick for this PC at 2560 × 1440: anti-aliasing.", GamesViewModel.DiffSentence(read));
        string text = GameSettings.Text(read, exe);
        Assert.Contains("Anti-aliasing Off (NVIDIA: SMAA)", text);
        Assert.Contains("LOD bias 2 (NVIDIA: Ultra)", text);
    }

    [Fact]
    public void Without_a_pick_for_the_game_s_screen_size_there_is_none_and_broken_files_are_ignored()
    {
        var (_, exe, dir) = Ror2();
        // NVIDIA has picks only for screen sizes that are neither the game's nor this PC's.
        Assert.Null(NvidiaPicks.Read(NvidiaBackend(exe, dir, resolution: "3840x1601"), exe, dir, "2560x1440", "7680x4320"));
        Assert.Equal(3, NvidiaPicks.Read(NvidiaBackend(exe, dir), exe, dir, "1366x768", "2560x1440")!.Step);
        // RTX cards get their own kind of pick in its own folder (REMATCH's "regular_rtx").
        Assert.Equal("SMAA", NvidiaPicks.Read(NvidiaBackend(exe, dir, kind: "regular_rtx"), exe, dir, "2560x1440", null)!.Values["Anti-Aliasing"]);
        // A file NVIDIA changed the shape of gives no pick, and the settings still read.
        var broken = NvidiaBackend(exe, dir);
        File.WriteAllText(Path.Combine(broken, "Recommendations", "risk_of_rain_2", "c818", "regular", "pops.pub.tsv"), "{\"settings\": 5}");
        var read2 = GameSettings.Read("Risk of Rain 2", exe, dir, null, Ror2(broken).Places);
        Assert.Null(read2.Nvidia);
        Assert.NotEmpty(read2.Rows);
        Assert.Equal("", GamesViewModel.DiffSentence(read2));
    }

    [Theory]
    [InlineData("r_softparticles", "Soft particles")]
    [InlineData("pp_screendistortion", "Screen distortion")]
    [InlineData("vfxbudget_particle_cost_bias", "VFX budget particle cost bias")]
    [InlineData("sg.ShadowQuality", "Shadow quality")]
    [InlineData("bUseVSync", "Use VSync")]
    [InlineData("setting.mat_vsync", "VSync")]
    [InlineData("pp_aa", "Anti-aliasing")]
    [InlineData("graphics_basic.TextureQuality", "Texture quality")]
    [InlineData("SSAOQuality", "SSAO quality")]
    [InlineData("Shadow Quality_h2692908302", "Shadow quality")]
    public void A_setting_without_a_definition_is_named_from_its_key(string key, string label) =>
        Assert.Equal(label, GameSettingNames.Label(key));

    [Fact]
    public void A_game_without_a_definition_still_shows_every_setting_with_plain_values()
    {
        var root = TestEnvironment.NewFolder("game-setting-auto");
        string Dir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }
        var places = new GameSettings.Places { LocalAppData = Dir("Local"), RoamingAppData = Dir("Roaming"), LocalLow = Dir("LocalLow"), Documents = Dir("Documents"), SavedGames = Dir("Saved Games") };
        string exe = Write(Path.Combine(root, "Games", "Rematch", "Runtime", "Binaries", "Win64", "RuntimeClient-Win64-Shipping.exe"), "x");
        Write(Path.Combine(places.LocalAppData, "Runtime", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"), """
            [/Script/Engine.GameUserSettings]
            bUseVSync=False
            ResolutionSizeX=2560
            ResolutionSizeY=1440
            FrameRateLimit=0.000000
            bUseDynamicResolution=True
            LastRecommendedScreenHeight=-1.000000
            DesiredScreenWidth=1280
            LastConfirmedFullscreenMode=1
            [ScalabilityGroups]
            sg.ShadowQuality=3
            sg.TextureQuality=4
            sg.ShadowCascades=1
            """);
        var read = GameSettings.Read("REMATCH", exe, Path.Combine(root, "Games", "Rematch"), null, places);

        string Row(string label) => Assert.Single(read.Rows, r => r.Label == label).Value;
        Assert.Equal("Epic", Row("Shadow quality"));
        Assert.Equal("Cinematic", Row("Texture quality"));
        Assert.Equal("Off", Row("Use VSync"));
        Assert.Equal("On", Row("Use dynamic resolution"));
        Assert.Equal("No limit", Row("Frame rate limit"));
        // What the game keeps for itself beside its settings isn't shown.
        Assert.DoesNotContain(read.Rows, r => r.Label.StartsWith("Last ", StringComparison.Ordinal) || r.Label.StartsWith("Desired", StringComparison.Ordinal));
        // A count stays a number, never On.
        Assert.Equal("1", Row("Shadow cascades"));
        // The screen size is said once, in the main settings, not again as width and height.
        Assert.DoesNotContain(read.Rows, r => r.Label.Contains("size", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GameSettingNames.Shadows, Assert.Single(read.Rows, r => r.Label == "Shadow quality").Group);
    }

    [Theory]
    // Unreal's level 3 is "Epic" to RigSight and "Ultra" in REMATCH's menu and to NVIDIA: maybe the same choice, never called a difference.
    [InlineData("Epic", new[] { "Low", "Medium", "High", "Epic", "Cinematic" }, "Ultra", false)]
    [InlineData("Epic", new[] { "Low", "Medium", "High", "Epic", "Cinematic" }, "High", true)]
    // A word as the game saved it against NVIDIA's: "Off" where NVIDIA picks DLSS.
    [InlineData("Off", null, "NVIDIA DLSS", true)]
    // A number the game saved against NVIDIA's word.
    [InlineData("200", null, "Ultra", false)]
    [InlineData("On", new[] { "On", "Off" }, "Enabled", false)]
    [InlineData("Fullscreen", null, "Full-screen", false)]
    public void A_difference_is_only_said_like_for_like(string value, string[]? words, string pick, bool differs) =>
        Assert.Equal(differs, new GameSettingRow(GameSettingNames.Quality, "Shadow quality", value, pick, words).Differs);
}

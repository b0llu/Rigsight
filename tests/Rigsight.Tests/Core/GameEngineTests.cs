using Rigsight.Core.Apps;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>
/// A game is known by the files its engine leaves beside it, wherever it sits (a folder in Downloads, a standalone
/// install): Unity, Unreal, Godot, GameMaker. Each case builds the real folder layout on disk.
/// </summary>
public sealed class GameEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigsight-tests", "engines-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Make(string relativeExe, params string[] alongside)
    {
        var exe = Path.Combine(_root, relativeExe);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        foreach (var rel in alongside)
        {
            var p = Path.Combine(_root, rel);
            if (rel.EndsWith('/')) Directory.CreateDirectory(p);
            else { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, ""); }
        }
        return exe;
    }

    [Fact]
    public void A_unity_game_in_downloads_is_a_game()
    {
        // As Super Battle Golf sits (2026-10-22): UnityPlayer.dll and "<name>_Data" beside the program.
        var exe = Make(@"Downloads\Compressed\Super Battle Golf\Super Battle Golf.exe",
            @"Downloads\Compressed\Super Battle Golf\UnityPlayer.dll", @"Downloads\Compressed\Super Battle Golf\Super Battle Golf_Data/");
        Assert.Equal("Unity", AppCatalog.EngineOf("Super Battle Golf.exe", exe));
        Assert.Equal(AppCategory.Game, AppCatalog.Classify("Super Battle Golf.exe", exe));
        // Its crash handler, in the same folder, isn't.
        var handler = Make(@"Downloads\Compressed\Super Battle Golf\UnityCrashHandler64.exe");
        Assert.NotEqual(AppCategory.Game, AppCatalog.Classify("UnityCrashHandler64.exe", handler));
    }

    [Fact]
    public void An_unreal_game_is_a_game_by_its_packed_content()
    {
        var exe = Make(@"MyGames\Stellar\Stellar\Binaries\Win64\Stellar-Win64-Shipping.exe", @"MyGames\Stellar\Stellar\Content\Paks/");
        Assert.Equal("Unreal", AppCatalog.EngineOf("Stellar-Win64-Shipping.exe", exe));
        // The same folder names without the packed content: a tool, not a game.
        var tool = Make(@"Tools\Thing\Binaries\Win64\Thing.exe");
        Assert.Null(AppCatalog.EngineOf("Thing.exe", tool));
    }

    [Fact]
    public void Godot_and_gamemaker_games_are_games()
    {
        var godot = Make(@"Indie\Tiny Farm\TinyFarm.exe", @"Indie\Tiny Farm\TinyFarm.pck");
        Assert.Equal("Godot", AppCatalog.EngineOf("TinyFarm.exe", godot));
        var gm = Make(@"Indie\Pixel Run\PixelRun.exe", @"Indie\Pixel Run\data.win");
        Assert.Equal("GameMaker", AppCatalog.EngineOf("PixelRun.exe", gm));
    }

    [Fact]
    public void An_ordinary_app_and_the_engines_editors_are_not_games()
    {
        var app = Make(@"Apps\Notes\Notes.exe", @"Apps\Notes\Notes.dll");
        Assert.Null(AppCatalog.EngineOf("Notes.exe", app));
        Assert.Equal(AppCategory.Other, AppCatalog.Classify("Notes.exe", app));
        var editor = Make(@"UE\Engine\Binaries\Win64\UnrealEditor.exe", @"UE\Engine\Content\Paks/");
        Assert.Null(AppCatalog.EngineOf("UnrealEditor.exe", editor));
        Assert.Null(AppCatalog.EngineOf("missing.exe", Path.Combine(_root, "nowhere", "missing.exe")));
    }

    [Fact]
    public void A_game_saved_as_other_is_filed_again_as_a_game()
    {
        var exe = Make(@"Downloads\Golf\Golf.exe", @"Downloads\Golf\UnityPlayer.dll");
        Assert.Equal(AppCategory.Game, AppCatalog.Reclassified("Golf.exe", exe, AppCategory.Other));
    }
}

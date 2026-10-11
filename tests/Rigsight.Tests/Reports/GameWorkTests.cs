using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Reports;

public class GameWorkTests
{
    // 1 a game, 2 its launcher, 3 a browser, 4 another game.
    private static AppCategory Category(long? id) => id switch
    {
        1 or 4 => AppCategory.Game,
        2 => AppCategory.Launcher,
        3 => AppCategory.Browser,
        _ => AppCategory.Other,
    };

    [Theory]
    [InlineData(2L, 1L, 1L)] // the launcher on top while a game is in front: the game
    [InlineData(2L, 3L, 2L)] // a launcher downloading behind a browser really is the launcher
    [InlineData(2L, null, 2L)]
    [InlineData(4L, 1L, 4L)] // another game working behind this one keeps its name
    [InlineData(1L, 1L, 1L)]
    [InlineData(null, 1L, null)] // nobody clearly did the work: nobody is named
    public void A_launcher_named_while_a_game_is_in_front_gives_way_to_the_game(long? found, long? front, long? expected)
    {
        var minute = new SystemMinute { Ts = 60, GpuApp = found, FgApp = front, CpuApp = 2 };
        GameWork.Settle([minute], Category);
        Assert.Equal(expected, minute.GpuApp);
        // The CPU's work can be read for every app: left as recorded.
        Assert.Equal(2, minute.CpuApp);
    }

    [Fact]
    public void The_agent_names_the_game_the_same_way()
    {
        string game = "game", launcher = "launcher", browser = "browser";
        AppCategory? Of(string app) => app == game ? AppCategory.Game : app == launcher ? AppCategory.Launcher : AppCategory.Browser;
        Assert.Same(game, GameWork.GpuApp(launcher, game, Of));
        Assert.Same(launcher, GameWork.GpuApp(launcher, browser, Of));
        Assert.Same(launcher, GameWork.GpuApp(launcher, null, Of));
        Assert.Same(game, GameWork.GpuApp(game, game, Of));
        Assert.Null(GameWork.GpuApp(null, game, Of));
    }
}

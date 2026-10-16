using Rigsight.Agent.Widgets;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Widgets stay over a windowed game, and over a borderless one on a screen with overlay planes; they step aside, on
/// the game's screen only, for a borderless game on a screen without them and for exclusive fullscreen. A fullscreen
/// video isn't a game. Each change is logged once, with why.
/// </summary>
public class WidgetsOverGamesTests
{
    private static readonly IntPtr ScreenA = 101, ScreenB = 202;

    [Fact]
    public void A_windowed_game_keeps_its_widgets()
    {
        var rule = new WidgetsOverGames();
        Assert.Equal(IntPtr.Zero, rule.Update(gameInFront: true, fillsScreen: false, exclusive: false, overlayPlanes: false, ScreenA, "Dota 2", out var log));
        Assert.Equal("Staying over Dota 2: game is windowed", log);
    }

    [Fact]
    public void A_borderless_game_keeps_them_on_a_screen_with_overlay_planes()
    {
        // Measured with PresentMon (Dota 2, 2026-10-16): widgets over it, the game still goes straight to the screen.
        var rule = new WidgetsOverGames();
        Assert.Equal(IntPtr.Zero, rule.Update(true, fillsScreen: true, exclusive: false, overlayPlanes: true, ScreenA, "Dota 2", out var log));
        Assert.Equal("Staying over Dota 2: game is borderless, the screen has overlay planes", log);
    }

    [Fact]
    public void A_borderless_game_moves_them_aside_on_a_screen_without_overlay_planes()
    {
        var rule = new WidgetsOverGames();
        Assert.Equal(ScreenA, rule.Update(true, fillsScreen: true, exclusive: false, overlayPlanes: false, ScreenA, "Dota 2", out var log));
        Assert.Equal("Stepping aside for Dota 2: game is borderless, the screen has no overlay planes", log);
    }

    [Fact]
    public void An_exclusive_fullscreen_game_moves_them_aside()
    {
        var rule = new WidgetsOverGames();
        // Overlay planes or not: exclusive fullscreen hides every window.
        Assert.Equal(ScreenA, rule.Update(true, fillsScreen: true, exclusive: true, overlayPlanes: true, ScreenA, "Dota 2", out var log));
        Assert.Equal("Stepping aside for Dota 2: game fills the screen (exclusive fullscreen)", log);
    }

    [Fact]
    public void A_fullscreen_video_is_not_a_game()
    {
        var rule = new WidgetsOverGames();
        Assert.Equal(IntPtr.Zero, rule.Update(gameInFront: false, fillsScreen: true, exclusive: false, overlayPlanes: false, ScreenA, "VLC", out var log));
        Assert.Null(log);
    }

    [Fact]
    public void Only_the_games_screen_is_affected()
    {
        // The manager hides only widgets on the returned screen; one on another screen compares unequal and stays.
        var rule = new WidgetsOverGames();
        var hidden = rule.Update(true, true, false, overlayPlanes: false, ScreenB, "Dota 2", out _);
        Assert.Equal(ScreenB, hidden);
        Assert.NotEqual(ScreenA, hidden);
    }

    [Fact]
    public void Each_change_is_logged_once()
    {
        var rule = new WidgetsOverGames();
        var logs = new List<string>();
        void Tick(bool game, bool fills)
        {
            rule.Update(game, fills, false, overlayPlanes: false, ScreenA, "Dota 2", out var log);
            if (log is not null) logs.Add(log);
        }

        // Borderless for a while, Alt+Enter to windowed, back again, then the game closes.
        Tick(true, true); Tick(true, true); Tick(true, true);
        Tick(true, false); Tick(true, false);
        Tick(true, true); Tick(true, true);
        Tick(false, false); Tick(false, false);

        Assert.Equal([
            "Stepping aside for Dota 2: game is borderless, the screen has no overlay planes",
            "Showing again: game is windowed",
            "Stepping aside for Dota 2: game is borderless, the screen has no overlay planes",
            "Showing again: no game in front",
        ], logs);
    }

    [Fact]
    public void Leaving_a_windowed_game_says_nothing()
    {
        // Nothing moved on screen either way.
        var rule = new WidgetsOverGames();
        rule.Update(true, false, false, overlayPlanes: false, ScreenA, "Dota 2", out _);
        Assert.Equal(IntPtr.Zero, rule.Update(false, false, false, overlayPlanes: false, IntPtr.Zero, null, out var log));
        Assert.Null(log);
    }

    [Fact]
    public void On_a_screen_with_overlay_planes_switching_modes_moves_nothing_but_says_why()
    {
        var rule = new WidgetsOverGames();
        var logs = new List<string>();
        void Tick(bool fills, bool exclusive)
        {
            Assert.Equal(exclusive ? ScreenA : IntPtr.Zero, rule.Update(true, fills, exclusive, overlayPlanes: true, ScreenA, "Dota 2", out var log));
            if (log is not null) logs.Add(log);
        }

        Tick(false, false); Tick(true, false); Tick(true, false); Tick(true, true); Tick(true, false);
        Assert.Equal([
            "Staying over Dota 2: game is windowed",
            "Staying over Dota 2: game is borderless, the screen has overlay planes",
            "Stepping aside for Dota 2: game fills the screen (exclusive fullscreen)",
            "Showing again: game is borderless, the screen has overlay planes",
        ], logs);
    }

    [Fact]
    [Trait("Category", "Machine")] // asks this PC's own graphics card about its main screen
    public void Windows_answers_for_the_main_screen_and_it_is_asked_once()
    {
        var main = Rigsight.Agent.Native.Win32.MonitorFromWindow(IntPtr.Zero, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        bool first = OverlayPlanes.On(main);
        Assert.Equal(first, OverlayPlanes.On(main));
        Assert.False(OverlayPlanes.On(IntPtr.Zero)); // no screen: step aside, as before
        // The PC the measurement was made on (RTX 3080 Ti, one 2560x1440 screen) has them.
        if (Environment.MachineName.Equals("DESKTOP-CO43VTH", StringComparison.OrdinalIgnoreCase)) Assert.True(first);
    }
}

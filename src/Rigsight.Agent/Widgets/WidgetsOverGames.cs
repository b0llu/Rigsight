namespace Rigsight.Agent.Widgets;

/// <summary>
/// Whether widgets step aside for the game in front, and why. A windowed game (one that doesn't fill its screen) is
/// already composed by Windows with the rest of the desktop, so a widget over it costs it nothing: widgets stay. A
/// game that fills its screen goes straight to the screen ("independent flip"), which is what keeps G-Sync/FreeSync
/// working. Borderless, it stays there with widgets over it when the screen has overlay planes (the graphics card
/// draws each widget on a layer of its own; see <see cref="OverlayPlanes"/>), so widgets stay; without them a window
/// on top would knock it off, so they step aside there. Exclusive fullscreen hides every window: they step aside and
/// the overlay takes over. Only the game's screen is affected. Kept apart from the windows so the rule can be tested
/// on its own. UI thread only.
/// </summary>
internal sealed class WidgetsOverGames
{
    public enum Why { NoGame, Windowed, FillsScreen, Exclusive }

    private Why _why = Why.NoGame;
    private IntPtr _monitor;

    /// <summary>What kind of game is in front. A fullscreen video or any other app that fills the screen isn't a game.</summary>
    public static Why Classify(bool gameInFront, bool fillsScreen, bool exclusive) =>
        !gameInFront ? Why.NoGame
        : exclusive ? Why.Exclusive
        : fillsScreen ? Why.FillsScreen
        : Why.Windowed;

    /// <param name="overlayPlanes">The game's screen has overlay planes (asked only for a borderless game).</param>
    public static bool StepsAside(Why why, bool overlayPlanes) => why == Why.Exclusive || (why == Why.FillsScreen && !overlayPlanes);

    /// <summary>
    /// The screen widgets step aside on (zero for none), from the game in front and its screen. <paramref name="log"/>
    /// is set only when that changes (a game switching between windowed and borderless with Alt+Enter is one change
    /// each way), so the log says it once and not every tick.
    /// </summary>
    public IntPtr Update(bool gameInFront, bool fillsScreen, bool exclusive, bool overlayPlanes, IntPtr gameMonitor, string? game, out string? log)
    {
        var why = Classify(gameInFront, fillsScreen, exclusive);
        bool aside = StepsAside(why, overlayPlanes);
        var monitor = aside ? gameMonitor : IntPtr.Zero;
        log = null;
        string name = game ?? "a game";
        if (monitor != _monitor)
            log = monitor != IntPtr.Zero ? $"Stepping aside for {name}: {Reason(why, overlayPlanes)}"
                : $"Showing again: {(why == Why.NoGame ? "no game in front" : Reason(why, overlayPlanes))}";
        else if (why != _why && why != Why.NoGame && !aside)
            // Nothing moves, but the log should still say why widgets stayed over a game.
            log = $"Staying over {name}: {Reason(why, overlayPlanes)}";
        _why = why;
        _monitor = monitor;
        return monitor;
    }

    private static string Reason(Why why, bool overlayPlanes) => why switch
    {
        Why.Exclusive => "game fills the screen (exclusive fullscreen)",
        Why.FillsScreen => overlayPlanes ? "game is borderless, the screen has overlay planes" : "game is borderless, the screen has no overlay planes",
        Why.Windowed => "game is windowed",
        _ => "no game in front",
    };
}

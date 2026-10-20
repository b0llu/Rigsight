namespace Rigsight.Core.Settings;

/// <summary>
/// The app window's zoom (Ctrl + wheel, Ctrl + plus or minus, Ctrl + 0 for 100%): 80% to 150% in whole steps of 10,
/// never anything in between such as 131% (the user's word, 2026-10-20). Never so far in that the pages get less room
/// than the smallest window they are laid out for: on a small window zooming in stops a whole step before pages would
/// start to clip, on a big one it goes to 150%.
/// </summary>
public static class AppZoom
{
    public const double Least = 0.8, Most = 1.5;

    /// <summary>The zoom chosen, <paramref name="steps"/> tenths in (more than 0) or out.</summary>
    public static double Stepped(double zoom, int steps) => Clamp((Tenths(Clamp(zoom)) + steps) / 10.0);

    /// <summary>A zoom kept to one of the steps (a setting from before, or one edited by hand, included).</summary>
    public static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(Tenths(zoom), 8, 15) / 10.0 : 1;

    /// <summary>
    /// The zoom used: the one chosen, unless the window hasn't the room for it, then the largest whole step it has room
    /// for (never a 131%). <paramref name="room"/> is how many times the smallest window's room it has (the narrower of
    /// width and height).
    /// </summary>
    public static double Used(double chosen, double room)
    {
        chosen = Clamp(chosen);
        // Zooming out always fits; in, never below 100% for want of room.
        if (chosen <= 1 || !double.IsFinite(room)) return chosen;
        return Math.Max(1, Math.Min(chosen, Math.Floor(room * 10 + 1e-9) / 10));
    }

    private static int Tenths(double zoom) => double.IsFinite(zoom) ? (int)Math.Round(zoom * 10) : 10;
}

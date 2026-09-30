namespace Rigsight.Core.Settings;

/// <summary>
/// Where the overlay goes on a screen of any size: hung from one of nine anchors (the corners, the middle of each edge,
/// the centre), moved from there by a share of the screen, so it lands in the same spot at any resolution. At an anchor
/// with no offset it sits exactly as the old corners did, <c>gap</c> in from the edges. It may hang off any edge (the
/// user's choice); only a sliver has to stay on screen, so it can always be found and moved back.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>The part that stays on screen (pixels) however far it's pushed off an edge, while it snaps (placed freely, none need).</summary>
    public const int MinVisible = 24;

    public static int Column(int anchor) => Math.Clamp(anchor, 0, 8) % 3;
    public static int Row(int anchor) => Math.Clamp(anchor, 0, 8) / 3;

    /// <summary>The overlay's anchor: its own, or (settings from before 0.8) its corner's.</summary>
    public static int AnchorOf(OverlaySettings o) => Math.Clamp(o.Anchor ?? FromCorner(o.Corner), 0, 8);

    public static int FromCorner(OverlayCorner corner) => corner switch
    {
        OverlayCorner.TopRight => 2,
        OverlayCorner.BottomLeft => 6,
        OverlayCorner.BottomRight => 8,
        _ => 0,
    };

    /// <summary>The top-left of an overlay of <paramref name="width"/> × <paramref name="height"/> on a screen of <paramref name="screenWidth"/> × <paramref name="screenHeight"/>.</summary>
    /// <param name="keepOnScreen">
    /// Keep <see cref="MinVisible"/> of it on the screen (while it snaps). Placed freely it goes wherever it was put,
    /// off the screen too (the Overlay page has a reset for that).
    /// </param>
    public static (double X, double Y) Place(int anchor, double offsetX, double offsetY,
        double screenWidth, double screenHeight, double width, double height, double gap, bool keepOnScreen = true)
    {
        double x = Start(Column(anchor), screenWidth, width, gap) + offsetX * screenWidth;
        double y = Start(Row(anchor), screenHeight, height, gap) + offsetY * screenHeight;
        return keepOnScreen ? KeepOnScreen(x, y, screenWidth, screenHeight, width, height) : (x, y);
    }

    /// <summary>At least <see cref="MinVisible"/> of it on the screen.</summary>
    public static (double X, double Y) KeepOnScreen(double x, double y, double screenWidth, double screenHeight, double width, double height) =>
        (Math.Clamp(x, MinVisible - width, screenWidth - MinVisible), Math.Clamp(y, MinVisible - height, screenHeight - MinVisible));

    /// <summary>
    /// The anchor and offsets for an overlay whose top-left is at (<paramref name="x"/>, <paramref name="y"/>): the
    /// anchor is the third of the screen its middle is in, so it grows away from the nearest edge.
    /// </summary>
    public static (int Anchor, double OffsetX, double OffsetY) FromPosition(double x, double y,
        double screenWidth, double screenHeight, double width, double height, double gap)
    {
        int column = Third(x + width / 2, screenWidth), row = Third(y + height / 2, screenHeight);
        double offsetX = (x - Start(column, screenWidth, width, gap)) / screenWidth;
        double offsetY = (y - Start(row, screenHeight, height, gap)) / screenHeight;
        // Six places: a hundredth of a pixel even on an 8K screen, so a free placement lands on the exact pixel.
        return (row * 3 + column, Math.Round(offsetX, 6), Math.Round(offsetY, 6));
    }

    /// <summary>Where the overlay starts along one axis with no offset: in from the near edge, centred, or in from the far edge.</summary>
    private static double Start(int third, double screen, double size, double gap) => third switch
    {
        0 => gap,
        1 => (screen - size) / 2,
        _ => screen - gap - size,
    };

    private static int Third(double middle, double screen) => middle < screen / 3 ? 0 : middle > screen * 2 / 3 ? 2 : 1;
}

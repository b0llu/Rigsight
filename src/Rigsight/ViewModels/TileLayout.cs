using Rigsight.Core.Settings;

namespace Rigsight.ViewModels;

/// <summary>
/// Grid layout rules for custom pages: tiles never overlap and float up to fill gaps. One tile can be
/// "pinned" (the one being dragged or resized); everything else flows around it.
/// </summary>
internal static class TileLayout
{
    private const int Columns = TileConfig.Columns;

    private static bool Overlaps(TileViewModel t, int x, int y, int w, int h) =>
        t.X < x + w && x < t.X + t.W && t.Y < y + h && y < t.Y + t.H;

    private static bool Free(IEnumerable<TileViewModel> placed, int x, int y, int w, int h) =>
        !placed.Any(p => Overlaps(p, x, y, w, h));

    /// <summary>
    /// Lays out <paramref name="tiles"/>. The pinned tile keeps its column; the others keep their order
    /// (by <paramref name="home"/> position) and each takes the highest free row in its own column.
    /// </summary>
    public static void Flow(IEnumerable<TileViewModel> tiles, TileViewModel? pinned, IReadOnlyDictionary<TileViewModel, (int X, int Y)>? home = null)
    {
        (int X, int Y) Home(TileViewModel t) => home is not null && home.TryGetValue(t, out var p) ? p : (t.X, t.Y);

        var placed = new List<TileViewModel>();
        if (pinned is not null)
        {
            pinned.X = Math.Clamp(pinned.X, 0, Columns - pinned.W);
            pinned.Y = Math.Max(0, pinned.Y);
            placed.Add(pinned);
        }

        foreach (var t in tiles.Where(t => t != pinned && !t.IsPlaceholder).OrderBy(t => Home(t).Y).ThenBy(t => Home(t).X).ToList())
        {
            int x = Math.Clamp(Home(t).X, 0, Columns - t.W);
            int y = 0;
            while (!Free(placed, x, y, t.W, t.H)) y++;
            t.X = x;
            t.Y = y;
            placed.Add(t);
        }

        // Let the pinned tile float up too if there's room above it.
        if (pinned is not null)
        {
            var others = placed.Where(p => p != pinned).ToList();
            for (int y = 0; y < pinned.Y; y++)
            {
                if (Free(others, pinned.X, y, pinned.W, pinned.H))
                {
                    pinned.Y = y;
                    break;
                }
            }
        }
    }

    /// <summary>The first free spot (top to bottom, left to right) for a new tile of this size.</summary>
    public static (int X, int Y) FindSpot(IEnumerable<TileViewModel> tiles, int w, int h)
    {
        var list = tiles.Where(t => !t.IsPlaceholder).ToList();
        for (int y = 0; ; y++)
            for (int x = 0; x <= Columns - w; x++)
                if (Free(list, x, y, w, h)) return (x, y);
    }
}

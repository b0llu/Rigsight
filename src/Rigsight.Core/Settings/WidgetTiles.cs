namespace Rigsight.Core.Settings;

/// <summary>A kind of tile a widget's box can hold: its name and line in the list, its group, and the size it starts at.</summary>
/// <param name="Id">Stored in the settings (<see cref="WidgetTile.Kind"/>): never changes, never shown.</param>
/// <param name="Name">Its name in English, shown translated (<see cref="WidgetTiles.NameOf"/>).</param>
/// <param name="Line">What it shows, in a few words, shown translated.</param>
public sealed record WidgetTileKind(string Id, string Name, string Line, string Group, int W, int H, int MinW, int MinH, TileAlign Align = TileAlign.Left);

/// <summary>
/// The tiles a widget's box is made of (the person puts as many as they like in one widget and arranges them on its grid,
/// approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U, 2026-11-10), and the rules for arranging them. Shared by the agent (which
/// draws them) and the app (whose editor moves them), so a widget comes out the same on the page and on the desktop.
/// </summary>
/// <remarks>
/// The grid has no end: a widget can hold any number of tiles and be bigger than the screen (the person's call: "user
/// can make any type of odd looking widget they want, even if the widgets wont even fit in screen"). Tiles never overlap,
/// and nothing packs or tidies them by itself.
/// </remarks>
public static class WidgetTiles
{
    /// <summary>A grid cell, in pixels at size M (scale 1): the dashboards' cells, at widget size.</summary>
    public const int CellWidth = 40, CellHeight = 32;

    /// <summary>The room between the panel's edge and the tiles, in pixels at size M.</summary>
    public const int Edge = 4;

    public const string Clock = "clock", Date = "date", Calendar = "calendar", Reading = "reading", Line = "line",
        Graph = "graph", Fps = "fps", Today = "today", NowPlaying = "nowplaying", Notes = "notes", Todo = "todo", Timer = "timer",
        Media = "media", Weather = "weather", Shortcuts = "shortcuts", Photos = "photos";

    public static string GroupEveryday => Lang.Key("Everyday");
    public static string GroupLive => Lang.Key("Live");
    public static string GroupYourDay => Lang.Key("Your day");

    /// <summary>Every kind, in the order the editor lists them (by group).</summary>
    public static readonly IReadOnlyList<WidgetTileKind> Kinds =
    [
        new(Clock, Lang.Key("Clock"), Lang.Key("The time as digits, hands, flip cards or words."), Lang.Key("Everyday"), 6, 2, 2, 1, TileAlign.Center),
        new(Date, Lang.Key("Date"), Lang.Key("The day and the date."), Lang.Key("Everyday"), 3, 2, 3, 1, TileAlign.Center),
        new(Calendar, Lang.Key("Calendar"), Lang.Key("This month, with today marked."), Lang.Key("Everyday"), 6, 6, 5, 5),
        new(Notes, Lang.Key("Notes"), Lang.Key("A note you type straight onto the desktop."), Lang.Key("Everyday"), 6, 4, 3, 2),
        new(Todo, Lang.Key("To-do"), Lang.Key("A short list you tick off."), Lang.Key("Everyday"), 6, 4, 4, 2),
        new(Timer, Lang.Key("Timer"), Lang.Key("A timer, a stopwatch, or a countdown to a day."), Lang.Key("Everyday"), 6, 2, 4, 2),
        new(Media, Lang.Key("Media"), Lang.Key("What's playing on this PC, with play, pause and skip."), Lang.Key("Everyday"), 8, 3, 4, 2),
        new(Weather, Lang.Key("Weather"), Lang.Key("The weather for a place you pick. Uses the internet."), Lang.Key("Everyday"), 8, 3, 4, 2),
        new(Shortcuts, Lang.Key("Shortcuts"), Lang.Key("Apps, folders and links you open with one click."), Lang.Key("Everyday"), 6, 2, 2, 1),
        new(Photos, Lang.Key("Photos"), Lang.Key("Pictures from a folder, one after another."), Lang.Key("Everyday"), 6, 4, 3, 2),
        new(Reading, Lang.Key("Any reading"), Lang.Key("One reading or sensor, as a number, a ring, a level or a graph."), Lang.Key("Live"), 3, 2, 2, 1),
        new(Line, Lang.Key("Line of readings"), Lang.Key("Readings in one slim line, made for the top of the screen."), Lang.Key("Live"), 7, 1, 3, 1),
        new(Graph, Lang.Key("Temperature graph"), Lang.Key("CPU and GPU temperatures over the last five minutes."), Lang.Key("Live"), 8, 4, 6, 3),
        new(Fps, Lang.Key("FPS"), Lang.Key("Your game's frame rate, frame time and 1% low. Needs RivaTuner."), Lang.Key("Live"), 5, 3, 3, 2),
        new(Today, Lang.Key("Today"), Lang.Key("Screen time so far today, your most-used app and today's peaks."), Lang.Key("Your day"), 7, 5, 6, 4),
        new(NowPlaying, Lang.Key("Now playing"), Lang.Key("The app or game you're using, for how long, and its peak temperatures."), Lang.Key("Your day"), 8, 3, 6, 2),
    ];

    public static WidgetTileKind? Kind(string? id) => Kinds.FirstOrDefault(k => k.Id == id);

    public static string NameOf(WidgetTileKind kind) => Lang.T(kind.Name);
    public static string LineOf(WidgetTileKind kind) => Lang.T(kind.Line);

    /// <summary>A timer tile's kinds, as stored (<see cref="WidgetTile.TimerKind"/>) and shown (translated).</summary>
    public static readonly IReadOnlyList<string> TimerKinds = [Lang.Key("Timer"), Lang.Key("Stopwatch"), Lang.Key("Countdown")];

    /// <summary>A note's or a to-do list's text sizes, as stored (<see cref="WidgetTile.TextSize"/>), in pixels at size M.</summary>
    public static readonly IReadOnlyList<(string Name, float Px)> TextSizes = [(Lang.Key("Small"), 11.5f), (Lang.Key("Medium"), 13f), (Lang.Key("Large"), 15f)];

    public static float TextPx(WidgetTile t) => TextSizes.FirstOrDefault(s => s.Name == t.TextSize) is { Name: not null } s ? s.Px : 13f;

    /// <summary>The longest note kept: far more than a tile shows, a guard against a runaway paste.</summary>
    public const int MaxNoteLength = 20_000;

    /// <summary>A note's look: a sticky note (yellow, written by hand) instead of the widget's own.</summary>
    public const string Sticky = "Sticky note";

    /// <summary>How many apps, folders and links one shortcuts tile holds at most.</summary>
    public const int MaxLinks = 12;

    /// <summary>How often a photos tile can move on to the next picture, in seconds, with its name.</summary>
    public static readonly IReadOnlyList<(int Seconds, string Name)> PhotoSteps =
        [(10, Lang.Key("Every 10 seconds")), (60, Lang.Key("Every minute")), (300, Lang.Key("Every 5 minutes")), (1800, Lang.Key("Every 30 minutes")), (3600, Lang.Key("Every hour"))];

    /// <summary>A clock's styles, as stored (<see cref="WidgetTile.Style"/>) and shown (translated).</summary>
    public static readonly IReadOnlyList<string> ClockStyles =
        [Lang.Key("Digital"), Lang.Key("Analog"), Lang.Key("Flip"), Lang.Key("In words"), Lang.Key("Day and time")];

    /// <summary>A reading's looks, as stored (<see cref="WidgetTile.Look"/>) and shown (translated).</summary>
    public const string Number = "Number only", Big = "Big number", Ring = "Ring", Level = "Level", Blocks = "Blocks",
        LineGraph = "Line graph", FilledGraph = "Filled graph", BarGraph = "Bar graph", LoadAndPower = "With load and power";

    /// <summary>The looks a reading can have: rings, levels and blocks need a temperature or a percentage, graphs five minutes of it.</summary>
    public static IReadOnlyList<string> Looks(string? item, SensorKind? sensorKind = null)
    {
        var metric = item is null ? null : WidgetCatalog.Metric(item);
        bool full = metric is { } m ? WidgetCatalog.Allows(WidgetLayout.Gauges, m) : sensorKind is { } k && WidgetCatalog.GaugeKind(k);
        bool history = metric is { } h && WidgetCatalog.GraphMetrics.Contains(h);
        var looks = new List<string> { Lang.Key(Number), Lang.Key(Big) };
        if (full) looks.AddRange([Lang.Key(Ring), Lang.Key(Level), Lang.Key(Blocks)]);
        if (history) looks.AddRange([Lang.Key(LineGraph), Lang.Key(FilledGraph), Lang.Key(BarGraph)]);
        if (metric is OverlayMetric.CpuTemp or OverlayMetric.GpuTemp) looks.Add(Lang.Key(LoadAndPower));
        return looks;
    }

    /// <summary>A new tile of a kind, at its starting size and with its starting settings.</summary>
    public static WidgetTile New(WidgetTileKind kind) => new()
    {
        Kind = kind.Id, W = kind.W, H = kind.H,
        Item = kind.Id == Reading ? nameof(OverlayMetric.CpuTemp) : null,
        Look = kind.Id == Reading ? Ring : null,
        Items = kind.Id == Line ? WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.Ram)
              : kind.Id == Graph ? WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp) : null,
    };

    /// <summary>
    /// The readings whose last five minutes a widget draws: a box's reading tiles shown as a graph (or with load and
    /// power) and its temperature graphs; an older widget's tiles and graph layouts. The agent reads only these from its
    /// history (the user, 2026-11-11: a box's sparklines and graph showed in the app and were empty on the desktop).
    /// </summary>
    public static IEnumerable<OverlayMetric> HistoryMetrics(WidgetConfig w)
    {
        if (w.Tiles is { } tiles)
        {
            foreach (var t in tiles)
            {
                if (t.Kind == Reading && t.Look is LineGraph or FilledGraph or BarGraph or LoadAndPower
                    && WidgetCatalog.Metric(t.Item ?? nameof(OverlayMetric.CpuTemp)) is { } m)
                    yield return m;
                else if (t.Kind == Graph)
                    foreach (var item in t.Items ?? WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp))
                        if (WidgetCatalog.Metric(item.Id) is { } g) yield return g;
            }
            yield break;
        }
        if (WidgetCatalog.LayoutOf(w) is WidgetLayout.Tiles or WidgetLayout.Graph)
            foreach (var item in WidgetCatalog.ItemsOf(w))
                if (WidgetCatalog.Metric(item.Id) is { } m) yield return m;
    }

    /// <summary>A widget's size in cells: up to the right and bottom edges of its furthest tiles.</summary>
    public static (int W, int H) BoxSize(IReadOnlyCollection<WidgetTile> tiles) =>
        tiles.Count == 0 ? (0, 0) : (tiles.Max(t => t.X + t.W), tiles.Max(t => t.Y + t.H));

    /// <summary>A widget's size in pixels at size M.</summary>
    public static (int W, int H) BoxPixels(IReadOnlyCollection<WidgetTile> tiles)
    {
        var (w, h) = BoxSize(tiles);
        return (w * CellWidth + Edge * 2, h * CellHeight + Edge * 2);
    }

    private static bool Hit(int ax, int ay, int aw, int ah, WidgetTile b) => ax < b.X + b.W && b.X < ax + aw && ay < b.Y + b.H && b.Y < ay + ah;

    /// <summary>The free spot nearest a tile's own, for a tile pushed off its place (never overlapping another).</summary>
    public static (int X, int Y) FreeSpot(IReadOnlyList<WidgetTile> tiles, WidgetTile t)
    {
        var others = tiles.Where(u => !ReferenceEquals(u, t)).ToList();
        if (others.Count == 0) return (t.X, t.Y);
        int x0 = others.Min(u => u.X) - t.W, y0 = others.Min(u => u.Y) - t.H, x1 = others.Max(u => u.X + u.W), y1 = others.Max(u => u.Y + u.H);
        (int X, int Y, double D)? best = null;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (others.Any(u => Hit(x, y, t.W, t.H, u))) continue;
                double d = Math.Abs(x - t.X) + Math.Abs(y - t.Y) * 1.2;
                if (best is null || d < best.Value.D) best = (x, y, d);
            }
        return best is { } b ? (b.X, b.Y) : (x1, 0);
    }

    /// <summary>
    /// After <paramref name="moved"/> was dropped or resized (from <paramref name="from"/>, its spot before): a tile it now
    /// covers takes its old spot where it fits there, else the nearest free one, so tiles never overlap. Then the whole
    /// box moves so its top left is the grid's first cell. Nothing else moves: gaps and odd shapes stay as the person made them.
    /// </summary>
    public static void Settle(List<WidgetTile> tiles, WidgetTile? moved = null, (int X, int Y)? from = null)
    {
        if (moved is not null)
            foreach (var u in tiles.Where(u => !ReferenceEquals(u, moved) && Hit(moved.X, moved.Y, moved.W, moved.H, u)).ToList())
            {
                if (from is { } f && !tiles.Any(v => !ReferenceEquals(v, u) && Hit(f.X, f.Y, u.W, u.H, v))) (u.X, u.Y) = (f.X, f.Y);
                else (u.X, u.Y) = FreeSpot(tiles, u);
            }
        if (tiles.Count == 0) return;
        int mx = tiles.Min(t => t.X), my = tiles.Min(t => t.Y);
        foreach (var t in tiles) (t.X, t.Y) = (t.X - mx, t.Y - my);
    }

    /// <summary>Puts a new tile beside the box (to the right while it's wider than tall, else under it), clear of the rest.</summary>
    public static void Place(List<WidgetTile> tiles, WidgetTile t)
    {
        var (w, h) = BoxSize(tiles);
        bool right = tiles.Count == 0 || w * CellWidth <= h * CellHeight * 1.7;
        (t.X, t.Y) = right ? (w, 0) : (0, h);
        tiles.Add(t);
        if (tiles.Any(u => !ReferenceEquals(u, t) && Hit(t.X, t.Y, t.W, t.H, u))) (t.X, t.Y) = FreeSpot(tiles, t);
        Settle(tiles);
    }

    /// <summary>
    /// A widget drawn as before the box came, as tiles that look the same, for its first change in the editor (its own
    /// readings, short names, look and spot are all kept).
    /// </summary>
    public static List<WidgetTile> FromLayout(WidgetConfig w)
    {
        var items = WidgetCatalog.ShownItems(w);
        var tiles = new List<WidgetTile>();
        switch (WidgetCatalog.LayoutOf(w))
        {
            case WidgetLayout.Bar:
                tiles.Add(new() { Kind = Line, W = Math.Max(3, 2 + items.Count * 2), H = 1, Items = [.. items] });
                break;
            case WidgetLayout.Graph:
                tiles.Add(new() { Kind = Graph, W = 8, H = 4, Items = [.. items] });
                break;
            case WidgetLayout.NowPlaying:
                tiles.Add(new() { Kind = NowPlaying, W = 8, H = 3 });
                break;
            case WidgetLayout.Today:
                tiles.Add(new() { Kind = Today, W = 7, H = 5 });
                break;
            case WidgetLayout.Gauges:
                for (int i = 0; i < items.Count; i++)
                    tiles.Add(new() { Kind = Reading, X = i * 3, W = 3, H = 4, Item = items[i].Id, Label = items[i].Label, Look = Ring });
                break;
            default:
                if (w.Style == WidgetStyle.Fps)
                {
                    tiles.Add(new() { Kind = Fps, W = 5, H = 3 });
                    break;
                }
                bool compact = w.Style == WidgetStyle.Compact && w.Items is null;
                for (int i = 0; i < items.Count; i++)
                    tiles.Add(new()
                    {
                        Kind = Reading, X = i % 2 * 3, Y = i / 2 * (compact ? 4 : 2), W = 3, H = compact ? 4 : 2,
                        Item = items[i].Id, Label = items[i].Label,
                        Look = compact && WidgetCatalog.Metric(items[i].Id) is OverlayMetric.CpuTemp or OverlayMetric.GpuTemp ? LoadAndPower : Number,
                    });
                break;
        }
        if (tiles.Count == 0) tiles.Add(New(Kind(Clock)!));
        return tiles;
    }

    /// <summary>Keeps a box tidy after a hand edit: known kinds, sizes no smaller than their kind's, no overlaps.</summary>
    public static void Clean(WidgetConfig w)
    {
        if (w.Tiles is null) return;
        var tiles = w.Tiles.Where(t => t is not null && Kind(t.Kind) is not null).ToList();
        foreach (var t in tiles)
        {
            var k = Kind(t.Kind)!;
            t.W = Math.Clamp(t.W, k.MinW, 400);
            t.H = Math.Clamp(t.H, k.MinH, 400);
            if (t.Style is not null && !ClockStyles.Contains(t.Style)) t.Style = null;
            if (t.TextSize is not null && TextSizes.All(z => z.Name != t.TextSize)) t.TextSize = null;
            if (t.TimerKind is not null && !TimerKinds.Contains(t.TimerKind)) t.TimerKind = null;
            if (t.Text is { Length: > MaxNoteLength }) t.Text = t.Text[..MaxNoteLength];
            t.Todos = t.Todos?.Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Text)).ToList();
            t.Minutes = Math.Clamp(t.Minutes, 1, 24 * 60);
            if (t.RunMs < 0) t.RunMs = 0;
            if (t.Lat is not (>= -90 and <= 90) || t.Lon is not (>= -180 and <= 180)) (t.Lat, t.Lon) = (null, null);
            t.Links = t.Links?.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Path)).Take(MaxLinks).ToList();
            if (t.Folder is { } f && string.IsNullOrWhiteSpace(f)) t.Folder = null;
            if (PhotoSteps.All(s => s.Seconds != t.Every)) t.Every = 60;
            if (t.Kind == Notes && t.Look is not (null or Sticky)) t.Look = null;
            t.Items = t.Items?.Where(i => i is not null && (WidgetCatalog.Metric(i.Id) is not null || WidgetCatalog.Sensor(i.Id) is not null)).DistinctBy(i => i.Id).ToList();
        }
        var placed = new List<WidgetTile>();
        foreach (var t in tiles)
        {
            if (placed.Any(u => Hit(t.X, t.Y, t.W, t.H, u))) (t.X, t.Y) = FreeSpot([.. placed, t], t);
            placed.Add(t);
        }
        Settle(placed);
        w.Tiles = placed;
    }
}

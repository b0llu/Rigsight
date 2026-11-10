namespace Rigsight.Core.Settings;

/// <summary>The built-in widgets (one of each), and <see cref="Custom"/> for the user's own.</summary>
public enum WidgetStyle
{
    /// <summary>CPU and GPU temperature with load, power and a sparkline.</summary>
    Compact,
    /// <summary>One slim horizontal bar: CPU · GPU · RAM.</summary>
    Pill,
    /// <summary>Two ring gauges.</summary>
    Gauges,
    /// <summary>The app you're using right now, how long, and its peak temperatures.</summary>
    NowPlaying,
    /// <summary>Today so far: screen time, top app, peaks.</summary>
    Today,
    /// <summary>Last five minutes of CPU and GPU temperature as a chart.</summary>
    Graph,
    /// <summary>The frame rate of the game in front, from RivaTuner.</summary>
    Fps,
    /// <summary>One the user made: its own name, layout and readings (see <see cref="WidgetConfig.Layout"/>).</summary>
    Custom,
}

/// <summary>How a widget lays out its readings (see <see cref="WidgetCatalog"/>).</summary>
public enum WidgetLayout
{
    /// <summary>One line of readings.</summary>
    Bar,
    /// <summary>A small grid of big readings, with a mini chart where there's history.</summary>
    Tiles,
    /// <summary>Ring gauges (temperatures and percentages).</summary>
    Gauges,
    /// <summary>Lines over the last five minutes.</summary>
    Graph,
    /// <summary>The app in front (Now playing); no readings to pick.</summary>
    NowPlaying,
    /// <summary>Today so far; no readings to pick.</summary>
    Today,
}

/// <summary>
/// A reading on a widget: one of the overlay's (<see cref="OverlayMetric"/>, by name) or any sensor
/// ("sensor:" and its identifier), with an optional short name shown instead of its own.
/// </summary>
public sealed class WidgetItem
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
}

/// <summary>
/// System follows Windows' app mode; Grey is the dark one on dark grey, as the app's Grey theme; App ("Like RigSight")
/// takes the app's own theme and accent, and is what a new widget starts on. Black is no longer offered (it became
/// Dark); kept so older settings files still load.
/// </summary>
public enum WidgetTheme { Dark, Light, System, Black, Grey, App }

/// <summary>Where a widget sits among windows. Over windows is how widgets always were; a new one starts on the desktop.</summary>
public enum WidgetSits { OverWindows, OnDesktop }

/// <summary>The corners of a widget's panel (and of each tile's box).</summary>
public enum WidgetCorners { Round, Slight, Square }

/// <summary>The room between a widget's tiles.</summary>
public enum WidgetGap { None, Small, Normal, Large }

/// <summary>How a tile lines up what it shows (Center is by the ink, both ways).</summary>
public enum TileAlign { Left, Center, Right }

/// <summary>
/// One tile in a widget's box (see <see cref="WidgetConfig.Tiles"/> and <see cref="WidgetTiles"/>): what it is, where it
/// sits on the box's grid and how big it is, in cells (<see cref="WidgetTiles.CellWidth"/> by
/// <see cref="WidgetTiles.CellHeight"/> pixels at size M), and its own settings. Only the settings its kind uses are read.
/// </summary>
public sealed class WidgetTile
{
    /// <summary>The kind (<see cref="WidgetTiles"/>' identifiers, like "clock" or "reading").</summary>
    public string Kind { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;

    /// <summary>Its alignment; null: its kind's own (a clock is centred, a reading starts on the left).</summary>
    public TileAlign? Align { get; set; }

    /// <summary>A reading tile's reading (an overlay reading's name or "sensor:…", as <see cref="WidgetItem.Id"/>).</summary>
    public string? Item { get; set; }

    /// <summary>A reading tile's short name, instead of the reading's own.</summary>
    public string? Label { get; set; }

    /// <summary>A reading tile's look (<see cref="WidgetTiles.Looks"/>); null: a number.</summary>
    public string? Look { get; set; }

    /// <summary>The readings of a line of readings or a graph, in order.</summary>
    public List<WidgetItem>? Items { get; set; }

    /// <summary>A clock's style (<see cref="WidgetTiles.ClockStyles"/>); null: digital.</summary>
    public string? Style { get; set; }

    /// <summary>A clock's 24-hour time; null: as Windows has it.</summary>
    public bool? Hours24 { get; set; }

    public bool Seconds { get; set; }

    /// <summary>Whether a clock shows the date beside or under the time.</summary>
    public bool ShowDate { get; set; } = true;

    public WidgetTile Copy() => new()
    {
        Kind = Kind, X = X, Y = Y, W = W, H = H, Align = Align, Item = Item, Label = Label, Look = Look,
        Items = Items?.Select(i => new WidgetItem { Id = i.Id, Label = i.Label }).ToList(), Style = Style, Hours24 = Hours24,
        Seconds = Seconds, ShowDate = ShowDate,
    };
}

/// <summary>
/// No longer a choice: widgets step aside on the screen of a game that fills it (borderless or exclusive fullscreen),
/// and the overlay takes over (a window over such a game can cost it latency and turn G-Sync/FreeSync off); over a
/// windowed game they stay. Kept so older settings files still load.
/// </summary>
public enum WidgetVisibility
{
    Always,
    HideInFullscreen,
    OnlyInFullscreen,
}

public sealed class WidgetConfig
{
    public WidgetStyle Style { get; set; }

    /// <summary>Tells widgets apart: the style's name for a built-in one, "custom-…" for the user's own.</summary>
    public string Id { get; set; } = "";

    /// <summary>The user's own widget's name (built-in ones have theirs, see <see cref="WidgetCatalog.Title"/>).</summary>
    public string? Name { get; set; }

    /// <summary>The user's own widget's layout (a built-in one's comes with its style, see <see cref="WidgetCatalog.LayoutOf"/>).</summary>
    public WidgetLayout? Layout { get; set; }

    /// <summary>What it shows, in order; null: the style's own (see <see cref="WidgetCatalog.ItemsOf"/>), so "reset" is null.</summary>
    public List<WidgetItem>? Items { get; set; }

    public bool Enabled { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    /// <summary>How solid the panel behind the readings is (0: none, just the readings).</summary>
    public double BackgroundOpacity { get; set; } = 0.94;

    /// <summary>How solid the readings themselves are.</summary>
    public double ContentOpacity { get; set; } = 1.0;

    /// <summary>Before 0.4.13: one opacity for the whole thing. Read once to set both values below, never written.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? Opacity { get; set; }

    public double Scale { get; set; } = 1.0;

    /// <summary>Locked widgets can't be dragged and let clicks pass through to what's underneath.</summary>
    public bool Locked { get; set; }

    public WidgetTheme Theme { get; set; } = WidgetTheme.Dark;

    /// <summary>Labels grey and readings white instead of coloured, as the overlay can.</summary>
    public bool Grayscale { get; set; }

    /// <summary>
    /// The widget as a box of tiles the person arranges (see <see cref="WidgetTile"/>); null for a widget drawn as before
    /// the box came (a built-in one, or one of the person's own with a layout), which turns into tiles the first time it's
    /// changed in the editor (<see cref="WidgetTiles.FromLayout"/>).
    /// </summary>
    public List<WidgetTile>? Tiles { get; set; }

    /// <summary>The colour that marks things on the widget (today on a calendar, a clock's second hand), as #RRGGBB; null: the theme's.</summary>
    public string? Accent { get; set; }

    /// <summary>The font of everything on the widget; null: Segoe UI, as Windows' own.</summary>
    public string? Font { get; set; }

    public WidgetCorners Corners { get; set; }

    /// <summary>Each tile on a panel of its own instead of one panel behind them all.</summary>
    public bool TileBoxes { get; set; }

    public WidgetGap Gap { get; set; }

    /// <summary>A thin line between tiles that touch (not with a box for each tile).</summary>
    public bool TileLines { get; set; }

    /// <summary>Over windows (how every widget was before) or on the desktop, under them; a new widget starts on the desktop.</summary>
    public WidgetSits Sits { get; set; }

    /// <summary>Whether this is a box of tiles (see <see cref="Tiles"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBox => Tiles is not null;

    /// <summary>No longer used (see <see cref="WidgetVisibility"/>).</summary>
    public WidgetVisibility Visibility { get; set; } = WidgetVisibility.Always;

    /// <summary>One of each built-in style, in grayscale; only the slim bar is on to start with.</summary>
    public static List<WidgetConfig> Defaults() =>
        [.. WidgetCatalog.BuiltIn.Select(s => new WidgetConfig { Style = s, Id = s.ToString(), Enabled = s == WidgetStyle.Pill, Grayscale = true })];
}

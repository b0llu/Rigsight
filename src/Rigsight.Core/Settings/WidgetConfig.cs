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
/// System follows Windows' app mode; Grey is the dark one on dark grey, as the app's Grey theme. Black is no longer
/// offered (it became Dark); kept so older settings files still load.
/// </summary>
public enum WidgetTheme { Dark, Light, System, Black, Grey }

/// <summary>
/// No longer a choice: widgets always step aside on the screen of a game in front, and the overlay takes over (a
/// window over a game costs it latency and turns G-Sync/FreeSync off). Kept so older settings files still load.
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

    /// <summary>No longer used (see <see cref="WidgetVisibility"/>).</summary>
    public WidgetVisibility Visibility { get; set; } = WidgetVisibility.Always;

    /// <summary>One of each built-in style; only the slim bar is on to start with.</summary>
    public static List<WidgetConfig> Defaults() =>
        [.. WidgetCatalog.BuiltIn.Select(s => new WidgetConfig { Style = s, Id = s.ToString(), Enabled = s == WidgetStyle.Pill })];
}

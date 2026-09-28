namespace Rigsight.Core.Settings;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public enum OverlayLayout
{
    /// <summary>One row per part: CPU, GPU, memory, then the app in front.</summary>
    Rows,
    /// <summary>Everything on a single line.</summary>
    Line,
}

/// <summary>A reading the overlay can show. The order here is the order on screen.</summary>
public enum OverlayMetric
{
    // From RivaTuner, which measures every game it draws in.
    Fps, FrameTime, OnePercentLow,
    CpuTemp, CpuLoad, CpuClock, CpuPower,
    GpuTemp, GpuHotSpot, GpuLoad, GpuClock, GpuPower, GpuMemory,
    Ram,
    Session, Clock,
}

/// <summary>A sensor on the overlay: its identifier and, optionally, a short name to show instead of its own.</summary>
public sealed class OverlaySensor
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
}

/// <summary>The in-game overlay: a click-through readout shown and hidden with a keyboard shortcut.</summary>
public sealed class OverlaySettings
{
    /// <summary>Whether the keyboard shortcut is active.</summary>
    public bool Enabled { get; set; } = true;

    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Before 0.8: one of four corners. Read once for <see cref="Anchor"/>, which replaced it.</summary>
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;

    /// <summary>
    /// Where the overlay hangs from, in reading order: 0 top left, 1 top centre, 2 top right, 3 middle left, 4 centre,
    /// 5 middle right, 6 bottom left, 7 bottom centre, 8 bottom right. It grows away from it (a right one grows to
    /// the left). Null in older settings (taken from <see cref="Corner"/>). See <see cref="OverlayPlacement"/>.
    /// </summary>
    public int? Anchor { get; set; }

    /// <summary>How far the overlay is moved from its anchor, as a share of the screen's width (+ right) and height (+ down).</summary>
    public double OffsetX { get; set; }

    /// <inheritdoc cref="OffsetX"/>
    public double OffsetY { get; set; }
    public OverlayLayout Layout { get; set; } = OverlayLayout.Rows;
    /// <summary>How solid the panel behind the readings is (0: none, just the readings).</summary>
    public double BackgroundOpacity { get; set; } = 0.9;

    /// <summary>How solid the readings themselves are.</summary>
    public double ContentOpacity { get; set; } = 1.0;

    /// <summary>Before 0.4.13: one opacity for the whole thing. Read once to set both values below, never written.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? Opacity { get; set; }

    public double Scale { get; set; } = 1.0;

    /// <summary>Everything in white and grey: no coloured labels or temperatures, so it never draws the eye.</summary>
    public bool Grayscale { get; set; }

    public List<OverlayMetric> Metrics { get; set; } = DefaultMetrics();

    /// <summary>Any of the PC's sensors, shown as extra rows under the readings above (at most <see cref="MaxSensors"/>).</summary>
    public List<OverlaySensor> Sensors { get; set; } = [];

    public const int MaxSensors = 10;
    public const int MaxLabelLength = 18;

    public const string DefaultHotkey = "Alt+Shift+O";

    public static List<OverlayMetric> DefaultMetrics() =>
    [
        OverlayMetric.Fps, OverlayMetric.OnePercentLow,
        OverlayMetric.CpuTemp, OverlayMetric.CpuLoad,
        OverlayMetric.GpuTemp, OverlayMetric.GpuLoad, OverlayMetric.GpuMemory,
        OverlayMetric.Ram, OverlayMetric.Session,
    ];
}

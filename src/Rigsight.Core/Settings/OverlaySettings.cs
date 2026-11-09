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
    // Widgets only (the overlay lists its own): today so far, and what's in front. Added at the end: the order is saved.
    ScreenTime, PcOn, TopApp, CpuPeakToday, GpuPeakToday, Date, InFront,
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
    /// <summary>
    /// The overlay on or off (its page's switch by the title, the tray menu; it was the shortcut's switch): off, it's
    /// hidden and its shortcut does nothing.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Shown only while a game is in front (the user, 2026-11-09): over the desktop and other apps it stays hidden, and
    /// it's back as soon as a game is in front again. Off as before.
    /// </summary>
    public bool OnlyOverGames { get; set; }

    /// <summary>
    /// The room between its readings (the user, 2026-11-09: "reduce the space between 2 readings"): Normal as before,
    /// None very close, Small closer, Large further apart; the same four as the taskbar's Space between.
    /// </summary>
    public TrayGap Gap { get; set; }

    /// <summary>
    /// The screen a game was last played on (Windows' name for it, "\\.\DISPLAY2"), noted by the agent while a game is in
    /// front, for the Overlay page's picture with two screens or more (2026-11-09). Null until a game has been seen.
    /// </summary>
    public string? GameScreen { get; set; }

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

    /// <summary>
    /// Dragging it on the Overlay page pulls it onto the edges and centre when close. Off: it goes exactly where it's
    /// dropped, and arrow keys move it a pixel at a time.
    /// </summary>
    public bool SnapPosition { get; set; } = true;
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

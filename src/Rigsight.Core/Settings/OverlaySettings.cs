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
    CpuTemp, CpuLoad, CpuClock, CpuPower,
    GpuTemp, GpuHotSpot, GpuLoad, GpuClock, GpuPower, GpuMemory,
    Ram,
    Session, Clock,
}

/// <summary>The in-game overlay: a click-through readout shown and hidden with a keyboard shortcut.</summary>
public sealed class OverlaySettings
{
    /// <summary>Whether the keyboard shortcut is active.</summary>
    public bool Enabled { get; set; } = true;

    public string Hotkey { get; set; } = DefaultHotkey;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;
    public OverlayLayout Layout { get; set; } = OverlayLayout.Rows;
    public double Opacity { get; set; } = 0.9;
    public double Scale { get; set; } = 1.0;

    public List<OverlayMetric> Metrics { get; set; } = DefaultMetrics();

    public const string DefaultHotkey = "Alt+Shift+O";

    public static List<OverlayMetric> DefaultMetrics() =>
    [
        OverlayMetric.CpuTemp, OverlayMetric.CpuLoad,
        OverlayMetric.GpuTemp, OverlayMetric.GpuLoad, OverlayMetric.GpuMemory,
        OverlayMetric.Ram, OverlayMetric.Session,
    ];
}

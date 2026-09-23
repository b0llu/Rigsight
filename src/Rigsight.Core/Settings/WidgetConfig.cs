namespace Rigsight.Core.Settings;

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
}

public enum WidgetTheme { Dark, Black, Light }

public enum WidgetVisibility
{
    Always,
    /// <summary>Hide while a fullscreen app (game, video) is in front.</summary>
    HideInFullscreen,
    /// <summary>Only show while a fullscreen app is in front — an in-game overlay.</summary>
    OnlyInFullscreen,
}

public sealed class WidgetConfig
{
    public WidgetStyle Style { get; set; }
    public bool Enabled { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public double Opacity { get; set; } = 0.94;
    public double Scale { get; set; } = 1.0;

    /// <summary>Locked widgets can't be dragged and let clicks pass through to what's underneath.</summary>
    public bool Locked { get; set; }

    public WidgetTheme Theme { get; set; } = WidgetTheme.Dark;
    public WidgetVisibility Visibility { get; set; } = WidgetVisibility.Always;

    /// <summary>One of each style; only the slim bar is on to start with.</summary>
    public static List<WidgetConfig> Defaults() =>
        [.. Enum.GetValues<WidgetStyle>().Select(s => new WidgetConfig { Style = s, Enabled = s == WidgetStyle.Pill })];
}

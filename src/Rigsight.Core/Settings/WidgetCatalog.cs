namespace Rigsight.Core.Settings;

/// <summary>
/// What widgets are made of: the built-in ones and their readings, the layouts, and which readings each layout can
/// show. A widget is a layout, a list of readings (<see cref="WidgetItem"/>) and its look; the built-in ones are the
/// same with their own readings, which the user can change (and reset).
/// </summary>
public static class WidgetCatalog
{
    /// <summary>The built-in widgets, in their order in settings.</summary>
    public static readonly IReadOnlyList<WidgetStyle> BuiltIn = [.. Enum.GetValues<WidgetStyle>().Where(s => s != WidgetStyle.Custom)];

    /// <summary>The built-in widgets as the app and the tray list them: the FPS one first, the one people look for.</summary>
    public static readonly IReadOnlyList<WidgetStyle> Listed = [.. BuiltIn.OrderBy(s => s != WidgetStyle.Fps)];

    public const string SensorPrefix = "sensor:";
    public const int MaxCustom = 24;
    public const int MaxNameLength = 40;

    public static string Title(WidgetStyle style) => style switch
    {
        WidgetStyle.Compact => "Compact",
        WidgetStyle.Pill => "Slim bar",
        WidgetStyle.Gauges => "Gauges",
        WidgetStyle.NowPlaying => "Now playing",
        WidgetStyle.Today => "Today",
        WidgetStyle.Fps => "FPS",
        WidgetStyle.Graph => "Temperature graph",
        _ => "My widget",
    };

    public static string Title(WidgetConfig w) => w.Style == WidgetStyle.Custom ? w.Name ?? Title(w.Style) : Title(w.Style);

    public static string Description(WidgetStyle style) => style switch
    {
        WidgetStyle.Compact => "CPU and GPU temperature with load, power and a mini chart.",
        WidgetStyle.Pill => "One slim line: CPU, GPU and RAM. Great along the top of the screen.",
        WidgetStyle.Gauges => "Ring gauges for CPU and GPU temperature.",
        WidgetStyle.NowPlaying => "The app or game you're using, for how long, and its peak temperatures.",
        WidgetStyle.Today => "Screen time so far today, your most-used app and today's peaks.",
        WidgetStyle.Fps => "Your game's frame rate, frame time and 1% low, on another screen while you play. Needs RivaTuner.",
        WidgetStyle.Graph => "The last five minutes of CPU and GPU temperature.",
        _ => "",
    };

    public static WidgetLayout LayoutOf(WidgetConfig w) => w.Style switch
    {
        WidgetStyle.Custom => w.Layout ?? WidgetLayout.Bar,
        WidgetStyle.Pill => WidgetLayout.Bar,
        WidgetStyle.Compact or WidgetStyle.Fps => WidgetLayout.Tiles,
        WidgetStyle.Gauges => WidgetLayout.Gauges,
        WidgetStyle.Graph => WidgetLayout.Graph,
        WidgetStyle.NowPlaying => WidgetLayout.NowPlaying,
        _ => WidgetLayout.Today,
    };

    public static string LayoutName(WidgetLayout layout) => layout switch
    {
        WidgetLayout.Bar => "Bar",
        WidgetLayout.Tiles => "Tiles",
        WidgetLayout.Gauges => "Gauges",
        WidgetLayout.Graph => "Graph",
        WidgetLayout.NowPlaying => "Now playing",
        _ => "Today",
    };

    /// <summary>The layouts a widget of the user's own can have (Now playing and Today show no readings to pick).</summary>
    public static readonly IReadOnlyList<WidgetLayout> CustomLayouts = [WidgetLayout.Bar, WidgetLayout.Tiles, WidgetLayout.Gauges, WidgetLayout.Graph];

    /// <summary>A built-in widget's own readings.</summary>
    public static List<WidgetItem> DefaultItems(WidgetStyle style) => style switch
    {
        WidgetStyle.Pill => Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.Ram),
        WidgetStyle.Compact or WidgetStyle.Gauges or WidgetStyle.Graph => Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp),
        WidgetStyle.Fps => Items(OverlayMetric.Fps),
        WidgetStyle.Custom => Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.Ram),
        _ => [],
    };

    /// <summary>What the widget shows: its own list, or its style's.</summary>
    public static List<WidgetItem> ItemsOf(WidgetConfig w) => w.Items ?? DefaultItems(w.Style);

    public static List<WidgetItem> Items(params OverlayMetric[] metrics) => [.. metrics.Select(m => new WidgetItem { Id = m.ToString() })];

    public static int MaxItems(WidgetLayout layout) => layout switch
    {
        WidgetLayout.Bar => 8,
        WidgetLayout.Tiles => 6,
        WidgetLayout.Gauges or WidgetLayout.Graph => 4,
        _ => 0,
    };

    /// <summary>The overlay reading an item is, if it's one (not a sensor, not unknown).</summary>
    public static OverlayMetric? Metric(string id) =>
        Enum.TryParse<OverlayMetric>(id, out var m) && Enum.IsDefined(m) && m != OverlayMetric.Session && !int.TryParse(id, out _) ? m : null;

    /// <summary>The sensor an item is ("sensor:…"), if it's one.</summary>
    public static string? Sensor(string id) => id.StartsWith(SensorPrefix, StringComparison.Ordinal) && id.Length > SensorPrefix.Length ? id[SensorPrefix.Length..] : null;

    /// <summary>The readings gauges can show: a temperature or a percentage, so there's a full circle.</summary>
    private static readonly HashSet<OverlayMetric> GaugeMetrics =
        [OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.GpuHotSpot, OverlayMetric.CpuLoad, OverlayMetric.GpuLoad, OverlayMetric.Ram];

    /// <summary>The readings a graph can show: those the agent keeps five minutes of.</summary>
    public static readonly IReadOnlyList<OverlayMetric> GraphMetrics =
        [OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.GpuHotSpot, OverlayMetric.CpuLoad, OverlayMetric.GpuLoad, OverlayMetric.Ram];

    /// <summary>Sensor kinds a gauge can show (a temperature, or something out of 100).</summary>
    public static bool GaugeKind(SensorKind kind) => kind is SensorKind.Temperature or SensorKind.Load or SensorKind.Level or SensorKind.Control or SensorKind.Humidity;

    /// <summary>Whether <paramref name="layout"/> can show the overlay reading <paramref name="metric"/>.</summary>
    public static bool Allows(WidgetLayout layout, OverlayMetric metric) => layout switch
    {
        WidgetLayout.Bar or WidgetLayout.Tiles => metric != OverlayMetric.Session,
        WidgetLayout.Gauges => GaugeMetrics.Contains(metric),
        WidgetLayout.Graph => GraphMetrics.Contains(metric),
        _ => false,
    };

    /// <summary>Whether <paramref name="layout"/> can show a sensor (of <paramref name="kind"/>, when known).</summary>
    public static bool AllowsSensor(WidgetLayout layout, SensorKind? kind = null) => layout switch
    {
        WidgetLayout.Bar or WidgetLayout.Tiles => true,
        WidgetLayout.Gauges => kind is null || GaugeKind(kind.Value),
        _ => false, // the agent keeps history of its key readings only
    };

    /// <summary>The overlay readings offered for a layout, in the overlay's order.</summary>
    public static IEnumerable<OverlayMetric> Offered(WidgetLayout layout) => Enum.GetValues<OverlayMetric>().Where(m => Allows(layout, m));

    /// <summary>A reading's name in the editor.</summary>
    public static string MetricName(OverlayMetric m) => m switch
    {
        OverlayMetric.Fps => "Frame rate (FPS)",
        OverlayMetric.FrameTime => "Frame time",
        OverlayMetric.OnePercentLow => "1% low",
        OverlayMetric.CpuTemp => "CPU temperature",
        OverlayMetric.CpuLoad => "CPU load",
        OverlayMetric.CpuClock => "CPU clock speed",
        OverlayMetric.CpuPower => "CPU power",
        OverlayMetric.GpuTemp => "GPU temperature",
        OverlayMetric.GpuHotSpot => "GPU hot spot",
        OverlayMetric.GpuLoad => "GPU load",
        OverlayMetric.GpuClock => "GPU clock speed",
        OverlayMetric.GpuPower => "GPU power",
        OverlayMetric.GpuMemory => "Video memory",
        OverlayMetric.Ram => "RAM in use",
        OverlayMetric.Clock => "Time of day",
        _ => m.ToString(),
    };

    /// <summary>A reading's label on the widget itself (short, like the slim bar's "CPU").</summary>
    public static string ShortLabel(OverlayMetric m) => m switch
    {
        OverlayMetric.Fps => "FPS",
        OverlayMetric.FrameTime => "Frame",
        OverlayMetric.OnePercentLow => "1% low",
        OverlayMetric.CpuTemp => "CPU",
        OverlayMetric.CpuLoad => "CPU load",
        OverlayMetric.CpuClock => "CPU clock",
        OverlayMetric.CpuPower => "CPU power",
        OverlayMetric.GpuTemp => "GPU",
        OverlayMetric.GpuHotSpot => "Hot spot",
        OverlayMetric.GpuLoad => "GPU load",
        OverlayMetric.GpuClock => "GPU clock",
        OverlayMetric.GpuPower => "GPU power",
        OverlayMetric.GpuMemory => "VRAM",
        OverlayMetric.Ram => "RAM",
        OverlayMetric.Clock => "Time",
        _ => m.ToString(),
    };

    /// <summary>
    /// Keeps a widget's readings to what its layout can show: known readings, each once, at most
    /// <see cref="MaxItems"/>, short names trimmed. A built-in widget's own list goes back to null (its default).
    /// </summary>
    public static void Clean(WidgetConfig w)
    {
        var layout = LayoutOf(w);
        if (w.Items is null)
        {
            if (w.Style == WidgetStyle.Custom) w.Items = DefaultItems(WidgetStyle.Custom);
            return;
        }
        var items = w.Items
            .Where(i => i is not null && (Metric(i.Id) is { } m ? Allows(layout, m) : Sensor(i.Id) is not null && AllowsSensor(layout)))
            .DistinctBy(i => i.Id)
            .Take(MaxItems(layout))
            .Select(i => new WidgetItem { Id = i.Id, Label = Label(i.Label) })
            .ToList();
        bool isDefault = items.Select(i => (i.Id, i.Label)).SequenceEqual(DefaultItems(w.Style).Select(i => (i.Id, (string?)null)));
        if (w.Style == WidgetStyle.Custom)
            w.Items = items.Count > 0 || MaxItems(layout) == 0 ? items : DefaultItems(WidgetStyle.Custom).Where(i => Metric(i.Id) is { } m && Allows(layout, m)).ToList();
        else
            w.Items = items.Count == 0 || isDefault ? null : items;

        static string? Label(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            label = label.Trim();
            return label.Length > OverlaySettings.MaxLabelLength ? label[..OverlaySettings.MaxLabelLength] : label;
        }
    }

    /// <summary>A new identifier for a widget of the user's own.</summary>
    public static string NewId() => "custom-" + Guid.NewGuid().ToString("N")[..8];
}

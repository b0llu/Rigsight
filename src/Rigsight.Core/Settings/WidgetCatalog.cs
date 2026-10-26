namespace Rigsight.Core.Settings;

/// <summary>The groups a widget's readings are picked from (the picker's chips).</summary>
public enum WidgetCategory { Game, Cpu, Gpu, Ram, Storage, Network, Fans, Board, Today }

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
        WidgetStyle.Compact => Lang.T("Compact"),
        WidgetStyle.Pill => Lang.T("Slim bar"),
        WidgetStyle.Gauges => Lang.T("Gauges"),
        WidgetStyle.NowPlaying => Lang.T("Now playing"),
        WidgetStyle.Today => Lang.T("Today"),
        WidgetStyle.Fps => Lang.T("FPS"),
        WidgetStyle.Graph => Lang.T("Temperature graph"),
        _ => Lang.T("My widget"),
    };

    public static string Title(WidgetConfig w) => w.Style == WidgetStyle.Custom ? w.Name ?? Title(w.Style) : Title(w.Style);

    public static string Description(WidgetStyle style) => style switch
    {
        WidgetStyle.Compact => Lang.T("CPU and GPU temperature with load, power and a mini chart."),
        WidgetStyle.Pill => Lang.T("One slim line with CPU, GPU and RAM. Great along the top of the screen."),
        WidgetStyle.Gauges => Lang.T("Ring gauges for CPU and GPU temperature."),
        WidgetStyle.NowPlaying => Lang.T("The app or game you're using, for how long, and its peak temperatures."),
        WidgetStyle.Today => Lang.T("Screen time so far today, your most-used app and today's peaks."),
        WidgetStyle.Fps => Lang.T("Your game's frame rate, frame time and 1% low, on another screen while you play. Needs RivaTuner."),
        WidgetStyle.Graph => Lang.T("The last five minutes of CPU and GPU temperature."),
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
        WidgetLayout.Bar => Lang.T("Bar"),
        WidgetLayout.Tiles => Lang.T("Tiles"),
        WidgetLayout.Gauges => Lang.T("Gauges"),
        WidgetLayout.Graph => Lang.T("Graph"),
        WidgetLayout.NowPlaying => Lang.T("Now playing"),
        _ => Lang.T("Today"),
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

    /// <summary>
    /// The most readings a widget keeps: what the roomiest layout shows. A widget keeps its whole list whatever its
    /// layout, so moving a bar of 8 to tiles (6) and back loses nothing; each layout shows what it can of it.
    /// </summary>
    public const int MaxKept = 8;

    /// <summary>Whether <paramref name="layout"/> can show this reading at all (a graph can't show a sensor, gauges a clock speed).</summary>
    public static bool Shows(WidgetLayout layout, WidgetItem item) =>
        Metric(item.Id) is { } m ? Allows(layout, m) : Sensor(item.Id) is not null && AllowsSensor(layout);

    /// <summary>What the widget shows in its layout: the readings it can show, in order, as many as it holds.</summary>
    public static List<WidgetItem> ShownItems(WidgetConfig w)
    {
        var layout = LayoutOf(w);
        return [.. ItemsOf(w).Where(i => Shows(layout, i)).Take(MaxItems(layout))];
    }

    public static int MaxItems(WidgetLayout layout) => layout switch
    {
        WidgetLayout.Bar => 8,
        WidgetLayout.Tiles => 6,
        WidgetLayout.Gauges or WidgetLayout.Graph => 4,
        _ => 0,
    };

    /// <summary>The overlay reading an item is, if it's one (not a sensor, not unknown).</summary>
    public static OverlayMetric? Metric(string id) =>
        Enum.TryParse<OverlayMetric>(id, out var m) && Enum.IsDefined(m) && !int.TryParse(id, out _) ? m : null;

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
        WidgetLayout.Bar or WidgetLayout.Tiles => true,
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
        OverlayMetric.Fps => Lang.T("Frame rate (FPS)"),
        OverlayMetric.FrameTime => Lang.T("Frame time"),
        OverlayMetric.OnePercentLow => Lang.T("1% low"),
        OverlayMetric.CpuTemp => Lang.T("CPU temperature"),
        OverlayMetric.CpuLoad => Lang.T("CPU load"),
        OverlayMetric.CpuClock => Lang.T("CPU clock speed"),
        OverlayMetric.CpuPower => Lang.T("CPU power"),
        OverlayMetric.GpuTemp => Lang.T("GPU temperature"),
        OverlayMetric.GpuHotSpot => Lang.T("GPU hot spot"),
        OverlayMetric.GpuLoad => Lang.T("GPU load"),
        OverlayMetric.GpuClock => Lang.T("GPU clock speed"),
        OverlayMetric.GpuPower => Lang.T("GPU power"),
        OverlayMetric.GpuMemory => Lang.T("Video memory"),
        OverlayMetric.Ram => Lang.T("RAM in use"),
        OverlayMetric.Clock => Lang.T("Time of day"),
        OverlayMetric.Session => Lang.T("This session"),
        OverlayMetric.ScreenTime => Lang.T("Screen time today"),
        OverlayMetric.PcOn => Lang.T("PC on today"),
        OverlayMetric.TopApp => Lang.T("Most used app today"),
        OverlayMetric.CpuPeakToday => Lang.T("CPU peak today"),
        OverlayMetric.GpuPeakToday => Lang.T("GPU peak today"),
        OverlayMetric.Date => Lang.T("Date"),
        OverlayMetric.InFront => Lang.T("App in front"),
        _ => m.ToString(),
    };

    /// <summary>What a reading is, in a few words, where its name doesn't say it all (the picker's second line).</summary>
    public static string MetricDetail(OverlayMetric m) => m switch
    {
        OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow => Lang.T("From RivaTuner, in the game you're playing"),
        OverlayMetric.Session => Lang.T("How long the app in front has been in use"),
        OverlayMetric.ScreenTime => Lang.T("Time you were using the PC today"),
        OverlayMetric.PcOn => Lang.T("Time the PC was on today"),
        OverlayMetric.InFront => Lang.T("The game or app you're using"),
        _ => "",
    };

    /// <summary>The group a reading is picked from.</summary>
    public static WidgetCategory CategoryOf(OverlayMetric m) => m switch
    {
        OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow or OverlayMetric.Session or OverlayMetric.InFront => WidgetCategory.Game,
        OverlayMetric.CpuTemp or OverlayMetric.CpuLoad or OverlayMetric.CpuClock or OverlayMetric.CpuPower => WidgetCategory.Cpu,
        OverlayMetric.GpuTemp or OverlayMetric.GpuHotSpot or OverlayMetric.GpuLoad or OverlayMetric.GpuClock or OverlayMetric.GpuPower or OverlayMetric.GpuMemory => WidgetCategory.Gpu,
        OverlayMetric.Ram => WidgetCategory.Ram,
        _ => WidgetCategory.Today,
    };

    /// <summary>The group a sensor is picked from, by its part (LibreHardwareMonitor's hardware type) and kind.</summary>
    public static WidgetCategory CategoryOf(string hardwareType, SensorKind kind) => hardwareType switch
    {
        "Cpu" => WidgetCategory.Cpu,
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => WidgetCategory.Gpu,
        "Memory" => WidgetCategory.Ram,
        "Storage" => WidgetCategory.Storage,
        "Network" => WidgetCategory.Network,
        _ when kind is SensorKind.Fan or SensorKind.Control => WidgetCategory.Fans,
        "Cooler" => WidgetCategory.Fans,
        _ => WidgetCategory.Board,
    };

    public static string CategoryName(WidgetCategory c) => c switch
    {
        WidgetCategory.Cpu => Lang.T("CPU"),
        WidgetCategory.Gpu => Lang.T("GPU"),
        WidgetCategory.Ram => Lang.T("RAM"),
        WidgetCategory.Board => Lang.T("Motherboard"),
        WidgetCategory.Game => Lang.T("Game"),
        WidgetCategory.Storage => Lang.T("Storage"),
        WidgetCategory.Network => Lang.T("Network"),
        WidgetCategory.Fans => Lang.T("Fans"),
        WidgetCategory.Today => Lang.T("Today"),
        _ => c.ToString(),
    };

    /// <summary>
    /// Why <paramref name="layout"/> can't show a reading, for the picker and the editor's list; null when it can. A
    /// gauge needs a temperature or a percentage; a graph needs the five minutes the agent keeps of its main readings.
    /// </summary>
    public static string? WhyNot(WidgetLayout layout, OverlayMetric? metric, SensorKind? sensorKind = null) =>
        (metric is { } m ? Allows(layout, m) : AllowsSensor(layout, sensorKind)) ? null
        : layout switch
        {
            WidgetLayout.Gauges => Lang.T("Gauges show a temperature or a percentage"),
            WidgetLayout.Graph => Lang.T("A graph keeps five minutes of the main readings only"),
            _ => Lang.T($"{LayoutName(layout)} can't show this"),
        };

    /// <summary>A reading's label on the widget itself (short, like the slim bar's "CPU").</summary>
    public static string ShortLabel(OverlayMetric m) => m switch
    {
        OverlayMetric.Fps => Lang.T("FPS"),
        OverlayMetric.FrameTime => Lang.T("Frame"),
        OverlayMetric.OnePercentLow => Lang.T("1% low"),
        OverlayMetric.CpuTemp => Lang.T("CPU"),
        OverlayMetric.CpuLoad => Lang.T("CPU load"),
        OverlayMetric.CpuClock => Lang.T("CPU clock"),
        OverlayMetric.CpuPower => Lang.T("CPU power"),
        OverlayMetric.GpuTemp => Lang.T("GPU"),
        OverlayMetric.GpuHotSpot => Lang.T("Hot spot"),
        OverlayMetric.GpuLoad => Lang.T("GPU load"),
        OverlayMetric.GpuClock => Lang.T("GPU clock"),
        OverlayMetric.GpuPower => Lang.T("GPU power"),
        OverlayMetric.GpuMemory => Lang.T("VRAM"),
        OverlayMetric.Ram => Lang.T("RAM"),
        OverlayMetric.Clock => Lang.T("Time"),
        OverlayMetric.Session => Lang.T("Session"),
        OverlayMetric.ScreenTime => Lang.T("Screen"),
        OverlayMetric.PcOn => Lang.T("PC on"),
        OverlayMetric.TopApp => Lang.T("Top app"),
        OverlayMetric.CpuPeakToday => Lang.T("CPU peak"),
        OverlayMetric.GpuPeakToday => Lang.T("GPU peak"),
        OverlayMetric.Date => Lang.T("Date"),
        OverlayMetric.InFront => Lang.T("App"),
        _ => m.ToString(),
    };

    /// <summary>
    /// Keeps a widget's readings tidy: known readings, each once, at most <see cref="MaxKept"/>, short names trimmed. What
    /// its layout can't show is kept, not dropped (see <see cref="ShownItems"/>). A built-in widget's own list goes back
    /// to null (its default).
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
            .Where(i => i is not null && (Metric(i.Id) is not null || Sensor(i.Id) is not null))
            .DistinctBy(i => i.Id)
            .Take(MaxKept)
            .Select(i => new WidgetItem { Id = i.Id, Label = Label(i.Label) })
            .ToList();
        bool isDefault = items.Select(i => (i.Id, i.Label)).SequenceEqual(DefaultItems(w.Style).Select(i => (i.Id, (string?)null)));
        if (w.Style == WidgetStyle.Custom)
            w.Items = items.Count > 0 || MaxItems(layout) == 0 ? items : DefaultItems(WidgetStyle.Custom);
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

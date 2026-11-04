namespace Rigsight.Core;

/// <summary>What a list of apps in a reading's card is measured by.</summary>
public enum CardApps { None, Cpu, Gpu, Memory, Disk, Network }

/// <summary>
/// A taskbar reading's card (issue #5, approved mock-up v5): what a click on a reading in the taskbar opens above it. A
/// card has one section per part that was clicked (a Two rows box can hold two parts; an icon per part holds two
/// readings of one): the clicked readings big with their last minutes, then the part's other readings, then what uses
/// the part. The rules here; the agent fills them in (its ReadingCard draws them).
/// </summary>
public static class ReadingCards
{
    /// <summary>
    /// The part's own readings a card lists from the key sensors (<see cref="KeySensors"/>), with their words: the CPU's,
    /// the graphics card's and RAM's. Other parts list the other sensors of the same device.
    /// </summary>
    public static IReadOnlyList<(string Key, string Label)> KeyRows(TrayPart part) => part switch
    {
        TrayPart.Cpu => [(KeySensors.CpuTemp, Lang.T("Temperature")), (KeySensors.CpuLoad, Lang.T("Load")), (KeySensors.CpuClock, Lang.T("Clock")),
            (KeySensors.CpuPower, Lang.T("Power")), (KeySensors.CpuVoltage, Lang.T("Voltage"))],
        TrayPart.Gpu => [(KeySensors.GpuTemp, Lang.T("Temperature")), (KeySensors.GpuHotSpot, Lang.T("Hot spot")), (KeySensors.GpuLoad, Lang.T("Load")),
            (KeySensors.GpuVramUsed, Lang.T("Memory used")), (KeySensors.GpuClock, Lang.T("Clock")), (KeySensors.GpuPower, Lang.T("Power")), (KeySensors.GpuFan, Lang.T("Fan"))],
        TrayPart.Ram => [(KeySensors.RamLoad, Lang.T("In use")), (KeySensors.RamUsed, Lang.T("Used")), (KeySensors.RamAvailable, Lang.T("Available"))],
        _ => [],
    };

    /// <summary>The order a device's other sensors are picked in (one of each kind), for parts without key sensors.</summary>
    public static readonly SensorKind[] DeviceKinds =
        [SensorKind.Temperature, SensorKind.Load, SensorKind.Throughput, SensorKind.Data, SensorKind.SmallData, SensorKind.Fan, SensorKind.Clock, SensorKind.Power, SensorKind.Voltage];

    /// <summary>The most of the part's other readings a section lists.</summary>
    public const int MaxRows = 5;

    /// <summary>The apps a section lists, the most.</summary>
    public const int MaxApps = 3;

    /// <summary>
    /// The page a section's button opens (the app's page keys): temperatures to Temperatures, fans to Fans, the CPU's load
    /// to Processes, RAM to Memory, drives to Storage, the network to Network, anything else to All sensors.
    /// </summary>
    public static string Page(TrayPart part, SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "temperatures",
        SensorKind.Fan or SensorKind.Control => "fans",
        _ => part switch
        {
            TrayPart.Cpu when kind == SensorKind.Load => "processes",
            TrayPart.Ram => "memory",
            TrayPart.Drive => "storage",
            TrayPart.Network => "network",
            _ => "sensors",
        },
    };

    /// <summary>The button's words for a page ("Open Temperatures").</summary>
    public static string Button(string page) => page switch
    {
        "temperatures" => Lang.T("Open Temperatures"),
        "fans" => Lang.T("Open Fans"),
        "processes" => Lang.T("Open Processes"),
        "memory" => Lang.T("Open Memory"),
        "storage" => Lang.T("Open Storage"),
        "network" => Lang.T("Open Network"),
        _ => Lang.T("Open All sensors"),
    };

    /// <summary>
    /// Whether "highest today" means something for a reading: not for a drive's used space or an amount of data (they
    /// only grow), nor a voltage.
    /// </summary>
    public static bool ShowsHighest(TrayPart part, SensorKind kind) => kind switch
    {
        SensorKind.Temperature or SensorKind.Power or SensorKind.Throughput or SensorKind.Fan or SensorKind.Clock => true,
        SensorKind.Load => part != TrayPart.Drive,
        _ => false,
    };

    /// <summary>What a part's apps are listed by, and the list's heading.</summary>
    public static (CardApps By, string Title) Apps(TrayPart part) => part switch
    {
        TrayPart.Cpu => (CardApps.Cpu, Lang.T("Using the CPU most now")),
        TrayPart.Gpu => (CardApps.Gpu, Lang.T("Using the graphics card most now")),
        TrayPart.Ram => (CardApps.Memory, Lang.T("Using memory most now")),
        TrayPart.Drive => (CardApps.Disk, Lang.T("Reading and writing most now")),
        TrayPart.Network => (CardApps.Network, Lang.T("Using the network most now")),
        _ => (CardApps.None, ""),
    };

    /// <summary>The time a section's graph shows: the last ten minutes (or since the reading was first shown, when less).</summary>
    public const int GraphMinutes = 10;

    /// <summary>
    /// The words under a section's graph: "Last 10 minutes", or since when (the reading came to the taskbar, or RigSight
    /// started, less than ten minutes ago).
    /// </summary>
    public static string Span(DateTime oldest, DateTime now) =>
        now - oldest >= TimeSpan.FromMinutes(GraphMinutes - 0.5) ? Lang.T("Last 10 minutes") : Lang.T($"Since {TimeText.Time(oldest)}");

    /// <summary>"low 41° · average 45° · high 52°", each as the taskbar writes it.</summary>
    public static string LowAvgHigh(SensorKind kind, IReadOnlyList<float> values)
    {
        var real = values.Where(float.IsFinite).ToList();
        if (real.Count == 0) return "";
        return Lang.T($"low {Units.StripText(kind, real.Min())} · average {Units.StripText(kind, real.Average())} · high {Units.StripText(kind, real.Max())}");
    }
}

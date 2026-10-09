using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rigsight.Models;

/// <summary>A hardware component (CPU, GPU, drive, sensor chip…) and its sensors.</summary>
public sealed partial class HardwareNode(string name, string type) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>What its place and its folded state are saved under: the name, or "name #2" for the second of two
    /// parts called the same (two drives of one model), so each keeps its own.</summary>
    public string Key { get; init; } = name;

    /// <summary>LibreHardwareMonitor hardware type name, e.g. "Cpu", "GpuNvidia", "Storage".</summary>
    public string Type { get; } = type;

    public ObservableCollection<SensorItem> Sensors { get; } = [];

    [ObservableProperty] private bool _hasVisibleSensors = true;
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Being dragged to a new place on All sensors (outlined meanwhile).</summary>
    [ObservableProperty] private bool _isBeingMoved;

    /// <summary>"24 sensors · 3 hidden", shown next to the name (useful when collapsed).</summary>
    [ObservableProperty] private string _summary = "";

    public bool IsGpu => Type is "GpuNvidia" or "GpuAmd" or "GpuIntel";

    /// <summary>The few readings that sum the part up, shown on its card's header, so a folded card still says
    /// something: a CPU's temperature, load and power; a drive's temperature and how full it is; a board's warmest
    /// reading and how many of its fans are spinning.</summary>
    public IReadOnlyList<HardwareKey> Keys { get; private set; } = [];

    /// <summary>Picks <see cref="Keys"/>, once the sensors are in.</summary>
    public void BuildKeys()
    {
        SensorItem? One(Core.SensorKind kind, params string[] names) =>
            names.Select(n => Sensors.FirstOrDefault(s => s.Kind == kind && s.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(s => s is not null);
        var keys = new List<HardwareKey>();
        void Add(SensorItem? sensor, string label = "") { if (sensor is not null) keys.Add(new HardwareKey(() => sensor, label)); }
        switch (Badge)
        {
            case "CPU":
                Add(One(Core.SensorKind.Temperature, "Tctl", "Package", "Core"));
                Add(One(Core.SensorKind.Load, "Total"), "load");
                Add(One(Core.SensorKind.Power, "Package"));
                break;
            case "GPU":
                Add(Sensors.FirstOrDefault(s => s.Kind == Core.SensorKind.Temperature && s.Name == "GPU Core") ?? One(Core.SensorKind.Temperature, ""));
                Add(Sensors.FirstOrDefault(s => s.Kind == Core.SensorKind.Load && s.Name == "GPU Core") ?? One(Core.SensorKind.Load, ""), "load");
                Add(One(Core.SensorKind.Power, "Package", ""));
                break;
            case "DRIVE":
                Add(Sensors.FirstOrDefault(s => s.Kind == Core.SensorKind.Temperature && s.HasTempScale));
                Add(One(Core.SensorKind.Load, "Used Space"), "full");
                break;
            case "RAM":
                Add(One(Core.SensorKind.Load, ""), "in use");
                Add(One(Core.SensorKind.Temperature, ""));
                break;
            default:
                // A sensor chip's readings have no names worth showing ("Temperature #3"): its warmest, and its fans.
                keys.Add(new HardwareKey(() => Sensors.Where(s => s.Kind == Core.SensorKind.Temperature && s.Value > 0).MaxBy(s => s.Value), "warmest"));
                keys.Add(new HardwareKey(() => Sensors.Count(s => s.Kind == Core.SensorKind.Fan && s.Value > 0) is int n and > 0 ? n == 1 ? "1 fan spinning" : $"{n} fans spinning" : null));
                break;
        }
        Keys = keys;
        RefreshKeys();
    }

    /// <summary>After new readings: the ones that are picked by what the readings are (the warmest, the fans spinning).</summary>
    public void RefreshKeys()
    {
        foreach (var key in Keys) key.Refresh();
    }

    /// <summary>Marks the bottom edge of this component's card in the All sensors list.</summary>
    public SensorGroupEnd End => _end ??= new(this);
    private SensorGroupEnd? _end;

    public string Badge => Type switch
    {
        "Cpu" => "CPU",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "GPU",
        "Motherboard" or "SuperIO" or "EmbeddedController" => "BOARD",
        "Memory" => "RAM",
        "Storage" => "DRIVE",
        "Cooler" => "COOLER",
        "Psu" => "PSU",
        "Battery" => "BATTERY",
        "Network" => "NET",
        _ => "DEVICE",
    };
}

/// <summary>One reading on a part's header: a sensor's value with a word after it ("5% load"), or plain words ("4 fans spinning").</summary>
public sealed partial class HardwareKey : ObservableObject
{
    private readonly Func<SensorItem?>? _pick;
    private readonly Func<string?>? _words;

    public HardwareKey(Func<SensorItem?> sensor, string label)
    {
        _pick = sensor;
        Label = label;
    }

    public HardwareKey(Func<string?> text) => _words = text;

    /// <summary>The sensor read (it can change: a board's warmest), or none for plain words.</summary>
    [ObservableProperty] private SensorItem? _sensor;
    [ObservableProperty] private string? _text;
    public string Label { get; } = "";

    /// <summary>Whether there is anything to show: a reading that has a value, or words.</summary>
    [ObservableProperty] private bool _isShown;

    public void Refresh()
    {
        if (_pick is not null)
        {
            Sensor = _pick();
            IsShown = Sensor?.Value is not null;
        }
        else
        {
            Text = _words!();
            IsShown = Text is not null;
        }
    }
}

/// <summary>
/// The All sensors page is one flat, virtualized list (so only rows on screen are built): each card is
/// a <see cref="HardwareNode"/> header, its <see cref="SensorItem"/> rows, then one of these.
/// </summary>
public sealed class SensorGroupEnd(HardwareNode owner)
{
    public HardwareNode Owner { get; } = owner;
}

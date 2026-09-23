using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rigsight.Models;

/// <summary>A hardware component (CPU, GPU, drive, sensor chip…) and its sensors.</summary>
public sealed partial class HardwareNode(string name, string type) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>LibreHardwareMonitor hardware type name, e.g. "Cpu", "GpuNvidia", "Storage".</summary>
    public string Type { get; } = type;

    public ObservableCollection<SensorItem> Sensors { get; } = [];

    [ObservableProperty] private bool _hasVisibleSensors = true;
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>"24 sensors · 3 hidden", shown next to the name (useful when collapsed).</summary>
    [ObservableProperty] private string _summary = "";

    public bool IsGpu => Type is "GpuNvidia" or "GpuAmd" or "GpuIntel";

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

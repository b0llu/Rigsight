using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;

namespace Rigsight.Models;

/// <summary>One graphics processor's readings, for the Temperatures page's GPU card (which can switch between GPUs).</summary>
public sealed partial class GpuView(string name, bool integrated, Func<string, SensorItem?> sensor) : ObservableObject
{
    public string Name { get; } = name;
    public bool Integrated { get; } = integrated;

    public SensorItem? Temp { get; } = sensor(KeySensors.GpuTemp);
    public SensorItem? HotSpot { get; } = sensor(KeySensors.GpuHotSpot);
    public SensorItem? MemJunction { get; } = sensor(KeySensors.GpuMemJunction);
    public SensorItem? Load { get; } = sensor(KeySensors.GpuLoad);
    public SensorItem? Power { get; } = sensor(KeySensors.GpuPower);
    public SensorItem? Clock { get; } = sensor(KeySensors.GpuClock);
    public SensorItem? Fan { get; } = sensor(KeySensors.GpuFan);
    public SensorItem? VramLoad { get; } = sensor(KeySensors.GpuVramLoad);
    public SensorItem? VramUsed { get; } = sensor(KeySensors.GpuVramUsed);
    public SensorItem? VramTotal { get; } = sensor(KeySensors.GpuVramTotal);

    /// <summary>"3.1 / 12.0 GB", or the load when the sizes aren't reported.</summary>
    [ObservableProperty] private string _vramText = "";

    /// <summary>Video memory can be shown: a load, or used and total.</summary>
    public bool HasVram => VramLoad is not null || (VramUsed is not null && VramTotal is not null);

    /// <summary>How full video memory is (0–100), from the load or from used and total.</summary>
    public double VramPercent => VramLoad?.Value is double load ? load
        : VramUsed?.Value is double used && VramTotal?.Value is double total && total > 0 ? used / total * 100 : 0;

    public void Refresh()
    {
        if (VramUsed?.Value is double used && VramTotal?.Value is double total && total > 0)
            VramText = $"{used / 1024:0.0} / {total / 1024:0.0} GB";
        else if (VramLoad?.Value is double load)
            VramText = $"{load:0}%";
        OnPropertyChanged(nameof(VramPercent));
    }
}

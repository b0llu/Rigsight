namespace Rigsight.Models;

/// <summary>Summary of one physical drive (from its SMART sensors).</summary>
public sealed class DriveSummary(string name, SensorItem? temperature, SensorItem? life, SensorItem? usedSpace, SensorItem? powerOnHours)
{
    public string Name { get; } = name;
    public SensorItem? Temperature { get; } = temperature;
    public SensorItem? Life { get; } = life;
    public SensorItem? UsedSpace { get; } = usedSpace;
    public SensorItem? PowerOnHours { get; } = powerOnHours;
}

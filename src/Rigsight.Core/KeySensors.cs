namespace Rigsight.Core;

/// <summary>
/// Names of the handful of sensors Rigsight tracks over time, and the rules for finding them
/// among the ~150 sensors a PC exposes (names differ between AMD, Intel and NVIDIA).
/// </summary>
public static class KeySensors
{
    public const string CpuTemp = "cpuTemp";
    public const string CpuDieTemp = "cpuDieTemp";
    public const string CpuLoad = "cpuLoad";
    public const string CpuPower = "cpuPower";
    public const string CpuClock = "cpuClock";
    public const string CpuVoltage = "cpuVoltage";
    public const string GpuTemp = "gpuTemp";
    public const string GpuHotSpot = "gpuHotSpot";
    public const string GpuMemJunction = "gpuMemJunction";
    public const string GpuLoad = "gpuLoad";
    public const string GpuPower = "gpuPower";
    public const string GpuClock = "gpuClock";
    public const string GpuFan = "gpuFan";
    public const string GpuVoltage = "gpuVoltage";
    public const string GpuVramLoad = "gpuVramLoad";
    public const string GpuVramUsed = "gpuVramUsed";
    public const string GpuVramTotal = "gpuVramTotal";
    public const string RamLoad = "ramLoad";
    public const string RamUsed = "ramUsed";
    public const string RamAvailable = "ramAvailable";

    /// <summary>Keys whose recent history is sent to the app when it connects.</summary>
    public static readonly string[] HistoryKeys = [CpuTemp, GpuTemp, GpuHotSpot, GpuMemJunction, CpuLoad, GpuLoad, RamLoad];

    public readonly record struct Candidate(int Index, string HardwareType, string HardwareName, string Name, SensorKind Kind);

    /// <summary>Maps key names to indexes into the flat sensor list.</summary>
    public static Dictionary<string, int> Pick(IReadOnlyList<Candidate> all)
    {
        var keys = new Dictionary<string, int>();
        bool IsCpu(Candidate c) => c.HardwareType == "Cpu";
        bool IsGpu(Candidate c) => c.HardwareType is "GpuNvidia" or "GpuAmd";
        bool IsIgpu(Candidate c) => c.HardwareType == "GpuIntel";
        // "Virtual Memory" (RAM plus the page file) uses the same sensor names as the real RAM: skip it.
        bool IsRam(Candidate c) => c.HardwareType == "Memory" && !c.HardwareName.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

        var gpuFilter = all.Any(IsGpu) ? (Func<Candidate, bool>)IsGpu : IsIgpu;

        void Add(string key, Func<Candidate, bool> hw, SensorKind kind, params string[] names)
        {
            foreach (var n in names)
            {
                var match = all.FirstOrDefault(c => hw(c) && c.Kind == kind && c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (match.Name is not null)
                {
                    keys[key] = match.Index;
                    return;
                }
            }
        }

        Add(CpuTemp, IsCpu, SensorKind.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core (Tdie)", "Core Average", "Core Max");
        if (!keys.ContainsKey(CpuTemp))
        {
            var any = all.FirstOrDefault(c => IsCpu(c) && c.Kind == SensorKind.Temperature);
            if (any.Name is not null) keys[CpuTemp] = any.Index;
        }
        Add(CpuDieTemp, IsCpu, SensorKind.Temperature, "CCD1 (Tdie)", "CCDs Max (Tdie)", "Core Max");
        if (keys.TryGetValue(CpuDieTemp, out var die) && keys.TryGetValue(CpuTemp, out var main) && die == main)
            keys.Remove(CpuDieTemp);
        Add(CpuLoad, IsCpu, SensorKind.Load, "CPU Total");
        Add(CpuPower, IsCpu, SensorKind.Power, "Package", "CPU Package", "Core (SMU)");
        Add(CpuClock, IsCpu, SensorKind.Clock, "Cores (Average)", "Core #1", "CPU Core #1");
        Add(CpuVoltage, IsCpu, SensorKind.Voltage, "Core (SVI2 TFN)", "Core (SVI3 TFN)", "CPU Core", "Core #1 VID", "Core VID");

        Add(GpuTemp, gpuFilter, SensorKind.Temperature, "GPU Core");
        if (!keys.ContainsKey(GpuTemp))
        {
            var any = all.FirstOrDefault(c => gpuFilter(c) && c.Kind == SensorKind.Temperature);
            if (any.Name is not null) keys[GpuTemp] = any.Index;
        }
        Add(GpuHotSpot, gpuFilter, SensorKind.Temperature, "GPU Hot Spot");
        Add(GpuMemJunction, gpuFilter, SensorKind.Temperature, "GPU Memory Junction", "GPU Memory");
        Add(GpuLoad, gpuFilter, SensorKind.Load, "GPU Core", "D3D 3D");
        Add(GpuPower, gpuFilter, SensorKind.Power, "GPU Package", "GPU Power", "GPU Core");
        Add(GpuClock, gpuFilter, SensorKind.Clock, "GPU Core");
        Add(GpuVoltage, gpuFilter, SensorKind.Voltage, "GPU Core Voltage", "GPU Core");
        Add(GpuVramLoad, gpuFilter, SensorKind.Load, "GPU Memory");
        Add(GpuVramUsed, gpuFilter, SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        Add(GpuVramTotal, gpuFilter, SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        var fan = all.FirstOrDefault(c => gpuFilter(c) && c.Kind == SensorKind.Fan);
        if (fan.Name is not null) keys[GpuFan] = fan.Index;

        Add(RamLoad, IsRam, SensorKind.Load, "Memory");
        Add(RamUsed, IsRam, SensorKind.Data, "Memory Used");
        Add(RamAvailable, IsRam, SensorKind.Data, "Memory Available");
        return keys;
    }
}

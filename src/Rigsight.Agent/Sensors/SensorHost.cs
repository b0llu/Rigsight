using LibreHardwareMonitor.Hardware;
using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// Wraps LibreHardwareMonitor. To stay light, hardware is updated in tiers: only CPU, GPU and RAM
/// are read while the app is closed; everything else is read only while someone is watching.
/// </summary>
internal sealed class SensorHost
{
    private enum Tier
    {
        /// <summary>Always (tracking needs these).</summary>
        Fast,
        /// <summary>Every tick, but only while the app is open.</summary>
        Live,
        /// <summary>Every ~10 s while the app is open (SMART reads are slow).</summary>
        Slow,
        /// <summary>Only once at startup (e.g. RAM stick SPD data).</summary>
        Static,
    }

    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsMemoryEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = true,
        IsPsuEnabled = true,
        IsBatteryEnabled = true,
    };

    private readonly List<(IHardware Hw, Tier Tier)> _hardware = [];
    private readonly List<ISensor> _sensors = [];
    // 0, not long.MinValue: "now - long.MinValue" overflows to a negative number, and drives would never refresh.
    private long _lastSlowMs;

    // Cheap NVIDIA readings used instead of the full GPU update while the app is closed.
    private NvidiaFastPath? _fastGpu;
    private IHardware? _fastGpuHardware;
    private readonly Dictionary<string, double?> _fastValues = [];
    private bool _usingFastValues;

    public List<HardwareMeta> Schema { get; } = [];
    public Dictionary<string, int> Keys { get; private set; } = [];
    public int SensorCount => _sensors.Count;

    /// <summary>Each sensor's identifier, in the same order as <see cref="ReadAll"/>.</summary>
    public string[] Ids { get; private set; } = [];

    /// <summary>Whether each sensor (same order) is a temperature, where 0 means "no reading".</summary>
    public bool[] IsTemperature { get; private set; } = [];

    public void Open()
    {
        _computer.Open();
        foreach (var hw in _computer.Hardware)
            Collect(hw);

        // Read everything before listing sensors: some chips (e.g. motherboard fan headers) only
        // expose a sensor once it has produced a first reading.
        foreach (var (hw, _) in _hardware) SafeUpdate(hw);
        foreach (var (hw, _) in _hardware) SafeUpdate(hw);

        foreach (var hw in _computer.Hardware)
            Describe(hw, parent: null);

        var nvidia = _computer.Hardware.Where(h => h.HardwareType == HardwareType.GpuNvidia).ToList();
        if (nvidia.Count == 1 && NvidiaFastPath.TryCreate(nvidia[0]) is { } fast)
        {
            _fastGpu = fast;
            _fastGpuHardware = nvidia[0];
        }

        var candidates = new List<KeySensors.Candidate>();
        int index = 0;
        foreach (var hwMeta in Schema)
            foreach (var s in hwMeta.Sensors)
                candidates.Add(new KeySensors.Candidate(index++, hwMeta.Type, hwMeta.Name, s.Name, s.Kind));
        Keys = KeySensors.Pick(candidates);
        Ids = [.. _sensors.Select(s => s.Identifier.ToString())];
        IsTemperature = [.. _sensors.Select(s => s.SensorType == SensorType.Temperature)];
    }

    private void Collect(IHardware hw)
    {
        var tier = hw.HardwareType switch
        {
            HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => Tier.Fast,
            HardwareType.Memory when hw.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) => Tier.Fast,
            HardwareType.Memory when hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) => Tier.Live,
            HardwareType.Memory => Tier.Static,
            HardwareType.Storage => Tier.Slow,
            _ => Tier.Live,
        };
        _hardware.Add((hw, tier));
        foreach (var sub in hw.SubHardware)
            Collect(sub);
    }

    private void Describe(IHardware hw, IHardware? parent)
    {
        var sensors = hw.Sensors.Where(s => s.SensorType != SensorType.Timing).OrderBy(s => SortKey(s.SensorType)).ThenBy(s => s.Index).ToList();
        if (sensors.Count > 0)
        {
            var meta = new HardwareMeta
            {
                Name = parent is null ? hw.Name : $"{hw.Name}  ·  {parent.Name}",
                Type = hw.HardwareType.ToString(),
            };
            foreach (var s in sensors)
            {
                meta.Sensors.Add(new SensorMeta
                {
                    Id = s.Identifier.ToString(),
                    Name = s.Name,
                    Kind = Enum.TryParse<SensorKind>(s.SensorType.ToString(), out var k) ? k : SensorKind.Factor,
                });
                _sensors.Add(s);
            }
            Schema.Add(meta);
        }

        foreach (var sub in hw.SubHardware)
            Describe(sub, hw);
    }

    private static int SortKey(SensorType t) => t switch
    {
        SensorType.Temperature => 0, SensorType.Load => 1, SensorType.Clock => 2, SensorType.Power => 3,
        SensorType.Voltage => 4, SensorType.Current => 5, SensorType.Fan => 6, SensorType.Control => 7,
        SensorType.Flow => 8, SensorType.Level => 9, SensorType.Data => 10, SensorType.SmallData => 11,
        SensorType.Throughput => 12, _ => 20,
    };

    /// <summary>Reads hardware. <paramref name="everything"/> is true while the app is open.</summary>
    public void Update(bool everything, long nowMs)
    {
        // Slow hardware (drives): every 10 s while the app is open, otherwise every 5 minutes, so today's
        // drive temperature range is known even when nobody is watching.
        bool slowDue = nowMs - _lastSlowMs >= (everything ? 10_000 : 300_000);
        if (slowDue) _lastSlowMs = nowMs;

        _usingFastValues = !everything && _fastGpu is not null;
        if (_usingFastValues)
        {
            _fastValues.Clear();
            _fastGpu!.Read(_fastValues);
        }

        foreach (var (hw, tier) in _hardware)
        {
            if (_usingFastValues && hw == _fastGpuHardware) continue;
            bool update = tier switch
            {
                Tier.Fast => true,
                Tier.Live => everything,
                Tier.Slow => slowDue,
                _ => false,
            };
            if (update) SafeUpdate(hw);
        }
    }

    public float?[] ReadAll()
    {
        var values = new float?[_sensors.Count];
        for (int i = 0; i < values.Length; i++)
        {
            var v = _sensors[i].Value;
            values[i] = v is float f && float.IsFinite(f) ? MathF.Round(f, 3) : null;
        }
        return values;
    }

    public double? Read(string key)
    {
        if (_usingFastValues && key.StartsWith("gpu", StringComparison.Ordinal))
            return _fastValues.GetValueOrDefault(key);
        if (!Keys.TryGetValue(key, out var i)) return null;
        var v = _sensors[i].Value;
        if (v is not float f || !float.IsFinite(f)) return null;
        // Without driver access some temperature sensors report 0 instead of nothing.
        if (_sensors[i].SensorType == SensorType.Temperature && f <= 0) return null;
        return f;
    }

    public KeyValues ReadKeys() => new()
    {
        CpuTemp = Read(KeySensors.CpuTemp),
        CpuLoad = Read(KeySensors.CpuLoad),
        CpuPower = Read(KeySensors.CpuPower),
        CpuVoltage = Read(KeySensors.CpuVoltage),
        CpuClock = Read(KeySensors.CpuClock),
        GpuTemp = Read(KeySensors.GpuTemp),
        GpuHotSpot = Read(KeySensors.GpuHotSpot),
        GpuMemJunction = Read(KeySensors.GpuMemJunction),
        GpuLoad = Read(KeySensors.GpuLoad),
        GpuPower = Read(KeySensors.GpuPower),
        GpuVoltage = Read(KeySensors.GpuVoltage),
        GpuClock = Read(KeySensors.GpuClock),
        GpuVramUsed = Read(KeySensors.GpuVramUsed),
        GpuVramTotal = Read(KeySensors.GpuVramTotal),
        RamLoad = Read(KeySensors.RamLoad),
        RamUsed = Read(KeySensors.RamUsed),
        RamAvailable = Read(KeySensors.RamAvailable),
    };

    /// <summary>Each drive's SMART health (status for every drive; sector counts for SATA drives only,
    /// since NVMe reports its health under different attribute numbers).</summary>
    public List<DriveHealthInfo> DriveHealth()
    {
        var list = new List<DriveHealthInfo>();
        foreach (var (hw, _) in _hardware)
        {
            if (hw is not LibreHardwareMonitor.Hardware.Storage.StorageDevice device) continue;
            try
            {
                if (device.Storage?.Smart is not { } smart) continue;
                bool sata = !device.Storage.IsNVMe;
                long? Raw(byte id) => sata && smart.SmartAttributes?.FirstOrDefault(a => a.Info.ID == id) is { } attr
                    ? (long)attr.Attribute.RawValueULong : null;
                list.Add(new DriveHealthInfo
                {
                    Name = hw.Name,
                    Status = smart.DiskStatus.ToString(),
                    ReallocatedSectors = Raw(0x05),
                    PendingSectors = Raw(0xC5),
                    UncorrectableSectors = Raw(0xC6),
                });
            }
            catch (Exception ex)
            {
                Log.Error("drives", ex);
            }
        }
        return list;
    }

    private static void SafeUpdate(IHardware hw)
    {
        try { hw.Update(); }
        catch { /* One misbehaving device shouldn't stop the others. */ }
    }

    public void Close()
    {
        try { _computer.Close(); } catch { }
    }
}

/// <summary>The readings Rigsight records over time.</summary>
internal readonly record struct KeyValues
{
    public double? CpuTemp { get; init; }
    public double? CpuLoad { get; init; }
    public double? CpuPower { get; init; }
    public double? CpuVoltage { get; init; }
    public double? CpuClock { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotSpot { get; init; }
    public double? GpuMemJunction { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuPower { get; init; }
    public double? GpuVoltage { get; init; }
    public double? GpuClock { get; init; }
    public double? GpuVramUsed { get; init; }
    public double? GpuVramTotal { get; init; }
    public double? RamLoad { get; init; }
    public double? RamUsed { get; init; }
    public double? RamAvailable { get; init; }
}

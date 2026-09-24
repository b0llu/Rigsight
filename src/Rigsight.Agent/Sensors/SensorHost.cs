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
    private readonly List<IHardware> _sensorHardware = [];   // each sensor's hardware, same order
    private readonly HashSet<IHardware> _updatedNow = [];
    private bool[] _fresh = [];
    // 0, not long.MinValue: "now - long.MinValue" overflows to a negative number, and drives would never refresh.
    private long _lastSlowMs;

    // Cheap NVIDIA readings used instead of the full GPU update while the app is closed.
    private NvidiaFastPath? _fastGpu;
    private IHardware? _fastGpuHardware;
    private readonly Dictionary<string, double?> _fastValues = [];
    private bool _usingFastValues;

    private Dictionary<string, int> _indexById = [];

    // Hardware behind the sensors the overlay shows: read every tick while it's up, whatever its tier.
    private readonly HashSet<IHardware> _watched = [];
    private string _watchedKey = "";
    private bool _watchesFastGpu;
    private long _lastWatchedSlowMs;

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
        _indexById = [];
        for (int i = 0; i < Ids.Length; i++) _indexById.TryAdd(Ids[i], i);
        IsTemperature = [.. _sensors.Select(s => s.SensorType == SensorType.Temperature)];
        _fresh = new bool[_sensors.Count];
        DriveHealth = ReadDriveHealth();
    }

    /// <summary>Whether sensor <paramref name="index"/> was read by the last <see cref="Update"/> (not an old value).</summary>
    public bool IsFresh(int index) => index < _fresh.Length && _fresh[index];

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
                _sensorHardware.Add(hw);
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
        DrivesUpdated = slowDue;

        // The quick NVIDIA readings only cover the key GPU sensors; a GPU sensor on the overlay needs the full read.
        _usingFastValues = !everything && _fastGpu is not null && !_watchesFastGpu;
        // Drives are slow to read: a watched drive sensor every 10 s is plenty.
        bool watchedSlowDue = nowMs - _lastWatchedSlowMs >= 10_000;
        if (watchedSlowDue) _lastWatchedSlowMs = nowMs;
        if (_usingFastValues)
        {
            _fastValues.Clear();
            _fastGpu!.Read(_fastValues);
        }

        _updatedNow.Clear();
        foreach (var (hw, tier) in _hardware)
        {
            if (_usingFastValues && hw == _fastGpuHardware) continue;
            bool update = tier switch
            {
                Tier.Fast => true,
                Tier.Live => everything,
                Tier.Slow => slowDue,
                _ => false,
            } || (_watched.Contains(hw) && (tier != Tier.Slow || watchedSlowDue));
            if (update)
            {
                SafeUpdate(hw);
                _updatedNow.Add(hw);
            }
        }
        for (int i = 0; i < _fresh.Length; i++) _fresh[i] = _updatedNow.Contains(_sensorHardware[i]);
        // SMART health is read here, on the sampler thread, right after the drives were updated: the
        // library isn't thread-safe, so the app's connection thread only ever reads this cached copy.
        if (slowDue) DriveHealth = ReadDriveHealth();
    }

    /// <summary>
    /// Keeps the hardware behind these sensors (the overlay's) up to date on every <see cref="Update"/>, even with
    /// the app closed. An empty list goes back to the usual light reading.
    /// </summary>
    public void Watch(IReadOnlyList<string> ids)
    {
        string key = string.Join('|', ids);
        if (key == _watchedKey) return;
        _watchedKey = key;
        _watched.Clear();
        foreach (var id in ids)
            if (_indexById.TryGetValue(id, out int i)) _watched.Add(_sensorHardware[i]);
        _watchesFastGpu = _fastGpuHardware is not null && _watched.Contains(_fastGpuHardware);
    }

    /// <summary>One sensor's current reading, kind and name, by identifier (null if this PC doesn't have it).</summary>
    public (double? Value, SensorKind Kind, string Name)? ReadSensor(string id)
    {
        if (!_indexById.TryGetValue(id, out int i)) return null;
        var s = _sensors[i];
        var kind = Enum.TryParse<SensorKind>(s.SensorType.ToString(), out var k) ? k : SensorKind.Factor;
        double? value = s.Value is float f && float.IsFinite(f) ? f : null;
        // Without driver access some temperature sensors report 0 instead of nothing.
        if (kind == SensorKind.Temperature && value <= 0) value = null;
        return (value, kind, s.Name);
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

    /// <summary>Whether the last <see cref="Update"/> read the drives (they're read far less often than the rest).</summary>
    public bool DrivesUpdated { get; private set; }

    /// <summary>Current drive temperatures by sensor id (not their fixed warning/critical limits).</summary>
    public IEnumerable<(string Id, double Value)> DriveTemperatures()
    {
        for (int i = 0; i < _sensors.Count; i++)
        {
            var s = _sensors[i];
            if (s.SensorType != SensorType.Temperature || s.Hardware.HardwareType != HardwareType.Storage) continue;
            if (s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Value is float v && float.IsFinite(v) && v > 0) yield return (Ids[i], v);
        }
    }

    /// <summary>Each drive's SMART health, as of the last drive update (safe to read from any thread).</summary>
    public List<DriveHealthInfo> DriveHealth { get; private set; } = [];

    /// <summary>Each drive's SMART health (status for every drive; sector counts for SATA drives only,
    /// since NVMe reports its health under different attribute numbers).</summary>
    private List<DriveHealthInfo> ReadDriveHealth()
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
                long? reallocated = Raw(0x05), pending = Raw(0xC5), uncorrectable = Raw(0xC6);
                string status = smart.DiskStatus.ToString();
                // The library leaves SATA drives "Unknown"; judge them the way CrystalDiskInfo does: failing
                // if any attribute has fallen to its threshold, a caution for bad sectors, otherwise good.
                if (status == "Unknown" && sata && smart.SmartAttributes is { Count: > 0 } attrs)
                {
                    bool failing = attrs.Any(a => a.Attribute.Threshold > 0 && a.Attribute.CurrentValue > 0 && a.Attribute.CurrentValue <= a.Attribute.Threshold);
                    bool badSectors = reallocated > 0 || pending > 0 || uncorrectable > 0;
                    status = failing ? "Bad" : badSectors ? "Caution" : "Good";
                }
                list.Add(new DriveHealthInfo
                {
                    Name = hw.Name,
                    Status = status,
                    ReallocatedSectors = reallocated,
                    PendingSectors = pending,
                    UncorrectableSectors = uncorrectable,
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

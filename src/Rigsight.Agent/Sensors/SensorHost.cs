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

    private readonly Computer _computer;

    /// <param name="skip">Hardware not to touch at all: not even detected (see <see cref="HardwareApps"/> and <see cref="ScanGuard"/>).</param>
    public SensorHost(SensorParts skip = SensorParts.None)
    {
        Skipped = skip;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = !skip.HasFlag(SensorParts.Motherboard),
            // Opened later, in the background (see OpenMemory): finding the RAM sticks takes seconds.
            IsMemoryEnabled = false,
            IsStorageEnabled = true,
            IsControllerEnabled = !skip.HasFlag(SensorParts.FanHubs),
            IsPsuEnabled = !skip.HasFlag(SensorParts.PowerSupply),
            IsBatteryEnabled = true,
        };
    }

    /// <summary>The hardware this host leaves alone.</summary>
    public SensorParts Skipped { get; }

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

    public List<HardwareMeta> Schema { get; private set; } = [];
    public Dictionary<string, int> Keys { get; private set; } = [];

    /// <summary>The graphics adapters as Windows offers them to games, high-performance first (see <see cref="GpuPreference"/>).</summary>
    public List<KeySensors.PreferredGpu> PreferredGpus { get; private set; } = [];
    public int SensorCount => _sensors.Count;

    /// <summary>Each sensor's identifier, in the same order as <see cref="ReadAll"/>.</summary>
    public string[] Ids { get; private set; } = [];

    /// <summary>Whether each sensor (same order) is a temperature, where 0 means "no reading".</summary>
    public bool[] IsTemperature { get; private set; } = [];

    // The fans (their place in the list), read for the history each time the sensors are.
    private int[] _fans = [];

    // Fans are recorded every minute, so their chips are read every 10 s even with the app closed (else only while
    // it's open: a motherboard's). An NVIDIA card's through NVML (_fastFans: each NVML fan's sensor), or failing that
    // its full update once a minute. Only readings taken this time are recorded (_fanValues, _fresh): an old value
    // left over from the last time the app was open would say a fan held one speed for hours.
    private const long FanIntervalMs = 10_000, FullGpuFanIntervalMs = 60_000;
    private long _lastFansMs, _lastGpuFansMs;
    private readonly HashSet<IHardware> _fanHardware = [];
    private int[] _fastFans = [];
    private readonly Dictionary<int, double?> _fanValues = [];

    /// <summary>The speed of every fan read by the last <see cref="Update"/> (see <see cref="FanIntervalMs"/>), with its sensor, name and hardware.</summary>
    public List<Tracking.FanReading> ReadFans()
    {
        var list = new List<Tracking.FanReading>(_fans.Length);
        foreach (int i in _fans)
        {
            var s = _sensors[i];
            double? rpm;
            if (_fanValues.TryGetValue(i, out var fast)) rpm = fast;
            else if (_fresh[i]) rpm = s.Value is float f && float.IsFinite(f) ? f : null;
            else continue; // not read this time: its last value may be hours old
            list.Add(new Tracking.FanReading(Ids[i], s.Name, s.Hardware.Name, rpm));
        }
        return list;
    }

    // RAM usage until the memory group is open (see EarlyMemory), and whether that has happened.
    private readonly List<IHardware> _earlyMemory = [];
    private Task? _memoryTask;
    private volatile bool _memoryOpened;
    private bool _memoryMerged;

    /// <summary>
    /// Opens everything but the RAM sticks (about a second): CPU, GPU, RAM usage, motherboard, drives. The sticks
    /// follow from <see cref="OpenMemory"/>, and join the list at the next <see cref="TakeMemory"/>.
    /// </summary>
    public void Open()
    {
        _computer.Open();
        _earlyMemory.AddRange(EarlyMemory.Create());
        Build(readFirst: [.. Tops()]);

        var nvidia = _computer.Hardware.Where(h => h.HardwareType == HardwareType.GpuNvidia).ToList();
        // Only when that card is the main one: the quick readings answer for every "gpu" reading, and on a PC whose
        // main card is another maker's (an older NVIDIA card beside it) they'd be recorded as the main card's.
        var gpuKeys = new[] { KeySensors.GpuTemp, KeySensors.GpuLoad }.Where(Keys.ContainsKey).Select(k => Keys[k]).ToList();
        if (nvidia.Count == 1 && gpuKeys.Count > 0 && gpuKeys.All(i => _sensorHardware[i] == nvidia[0]) && NvidiaFastPath.TryCreate(nvidia[0]) is { } fast)
        {
            _fastGpu = fast;
            _fastGpuHardware = nvidia[0];
            MapFastFans();
        }
        DriveHealth = ReadDriveHealth();
    }

    /// <summary>
    /// Opens the memory group: the RAM sticks (their temperatures and details) and the library's own RAM usage. Slow
    /// (it probes the memory bus), so the agent runs it on a thread of its own while the sampler keeps reading; the
    /// library does the same when its first try finds no sticks. Takes effect at the sampler's next <see cref="TakeMemory"/>.
    /// </summary>
    public void OpenMemory()
    {
        try
        {
            _computer.IsMemoryEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Error("sensors", ex);
        }
        _memoryOpened = true;
    }

    /// <summary>Runs <paramref name="open"/> (which calls <see cref="OpenMemory"/>) in the background; <see cref="Close"/> waits for it.</summary>
    public void OpenMemoryInBackground(Action open) => _memoryTask = Task.Run(open);

    /// <summary>
    /// Sampler thread: once the memory group is open, puts it in the list in place of the early RAM usage. True when
    /// the list changed (the app needs to hear about it).
    /// </summary>
    public bool TakeMemory()
    {
        if (!_memoryOpened || _memoryMerged) return false;
        _memoryMerged = true;
        var added = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Memory).ToList();
        _earlyMemory.Clear(); // they only ask Windows: nothing to close
        Build(readFirst: added);
        _watchedKey = ""; // the overlay's sensors are found again in the new list
        return true;
    }

    /// <summary>
    /// The top-level hardware in the order the library lists it when everything opens at once (board, CPU, memory,
    /// GPUs, the rest), so the memory group opening last doesn't move it to the end of All sensors.
    /// </summary>
    private IEnumerable<IHardware> Tops() =>
        _computer.Hardware.Concat(_earlyMemory).Select((hw, i) => (hw, i)).OrderBy(x => x.hw.HardwareType switch
        {
            HardwareType.Motherboard => 0,
            HardwareType.Cpu => 1,
            HardwareType.Memory => 2,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => 3,
            _ => 4,
        }).ThenBy(x => x.i).Select(x => x.hw);

    /// <summary>
    /// (Re)lists every sensor. <paramref name="readFirst"/> is read twice beforehand: some chips (e.g. motherboard fan
    /// headers) only expose a sensor once it has produced a reading. Everything the app's connection thread reads
    /// (Schema, Keys) is replaced whole, never changed in place.
    /// </summary>
    private void Build(List<IHardware> readFirst)
    {
        var tops = Tops().ToList();
        _hardware.Clear();
        foreach (var hw in tops) Collect(hw);
        var fresh = new HashSet<IHardware>();
        foreach (var hw in readFirst) AddWithSubs(hw, fresh);
        foreach (var (hw, _) in _hardware.Where(h => fresh.Contains(h.Hw))) SafeUpdate(hw);
        foreach (var (hw, _) in _hardware.Where(h => fresh.Contains(h.Hw))) SafeUpdate(hw);

        _sensors.Clear();
        _sensorHardware.Clear();
        var schema = new List<HardwareMeta>();
        foreach (var hw in tops)
            Describe(hw, parent: null, schema);

        var candidates = new List<KeySensors.Candidate>();
        int index = 0;
        for (int hw = 0; hw < schema.Count; hw++)
            foreach (var s in schema[hw].Sensors)
                candidates.Add(new KeySensors.Candidate(index++, schema[hw].Type, schema[hw].Name, s.Name, s.Kind, hw));
        if (PreferredGpus.Count == 0)
        {
            PreferredGpus = GpuPreference.Read();
            if (PreferredGpus.Count > 0) Log.Write("sensors", $"GPUs as Windows offers them to games: {string.Join(", ", PreferredGpus.Select(g => g.Name))}");
        }
        var ids = UniqueIds([.. _sensors.Select(s => s.Identifier.ToString())], [.. _sensors.Select(s => s.Name)]);
        int flat = 0;
        foreach (var hw in schema)
            foreach (var meta in hw.Sensors)
                meta.Id = ids[flat++];
        var indexById = new Dictionary<string, int>();
        for (int i = 0; i < ids.Length; i++) indexById.TryAdd(ids[i], i);

        Keys = KeySensors.Pick(candidates, PreferredGpus);
        Ids = ids;
        _indexById = indexById;
        IsTemperature = [.. _sensors.Select(s => s.SensorType == SensorType.Temperature)];
        _fans = [.. Enumerable.Range(0, _sensors.Count).Where(i => _sensors[i].SensorType == SensorType.Fan)];
        _fanHardware.Clear();
        foreach (int i in _fans) _fanHardware.Add(_sensorHardware[i]);
        MapFastFans();
        _fresh = new bool[_sensors.Count];
        Schema = schema;

        static void AddWithSubs(IHardware hw, HashSet<IHardware> set)
        {
            set.Add(hw);
            foreach (var sub in hw.SubHardware) AddWithSubs(sub, set);
        }
    }

    /// <summary>The NVIDIA card's fan sensors in NVML's order, when NVML reads as many fans as the card lists.</summary>
    private void MapFastFans()
    {
        var gpuFans = _fans.Where(i => _sensorHardware[i] == _fastGpuHardware)
            .OrderBy(i => _sensors[i].Identifier.ToString(), StringComparer.Ordinal).ToArray();
        _fastFans = _fastGpu is { FanCount: > 0 } fast && gpuFans.Length == fast.FanCount ? gpuFans : [];
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

    private void Describe(IHardware hw, IHardware? parent, List<HardwareMeta> schema)
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
            schema.Add(meta);
        }

        foreach (var sub in hw.SubHardware)
            Describe(sub, hw, schema);
    }

    /// <summary>
    /// The library sometimes gives two sensors the same identifier (on NVIDIA cards "GPU Bus" and
    /// "GPU Memory" are both load/3), and names, hiding, tiles and today's range are all kept by id.
    /// Every sensor sharing an id gets its name added, so none of them inherits a name or range that
    /// may have belonged to the other one; ids that don't clash stay as they are.
    /// </summary>
    internal static string[] UniqueIds(IReadOnlyList<string> ids, IReadOnlyList<string> names)
    {
        var clashing = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var taken = new HashSet<string>(ids.Where(id => !clashing.Contains(id)));
        var result = new string[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            if (!clashing.Contains(ids[i])) { result[i] = ids[i]; continue; }
            var slug = new string([.. names[i].ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
            if (slug.Length == 0) slug = "sensor";
            string unique = $"{ids[i]}/{slug}";
            for (int n = 2; !taken.Add(unique); n++) unique = $"{ids[i]}/{slug}-{n}";
            result[i] = unique;
        }
        return result;
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
        bool failed = false;
        if (_usingFastValues)
        {
            _fastValues.Clear();
            _fastGpu!.Read(_fastValues);
            // No temperature from a card that gave one before: its driver has gone from under the handle.
            if (_fastValues.GetValueOrDefault(KeySensors.GpuTemp) is not null) _fastWorked = true;
            else if (_fastWorked) failed = true;
        }
        bool fansDue = nowMs - _lastFansMs >= FanIntervalMs;
        if (fansDue) _lastFansMs = nowMs;
        _fanValues.Clear();

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
            } || (_watched.Contains(hw) && (tier != Tier.Slow || watchedSlowDue)) || (fansDue && _fanHardware.Contains(hw));
            // A read that failed left the old values in place: they aren't this moment's (not "fresh" below).
            if (update)
            {
                if (SafeUpdate(hw)) _updatedNow.Add(hw);
                else failed = true;
            }
        }
        if (_usingFastValues && fansDue && _fanHardware.Contains(_fastGpuHardware!))
        {
            if (_fastFans.Length > 0)
                for (int k = 0; k < _fastFans.Length; k++) _fanValues[_fastFans[k]] = _fastGpu!.FanRpm(k);
            else if (nowMs - _lastGpuFansMs >= FullGpuFanIntervalMs)
            {
                _lastGpuFansMs = nowMs;
                if (SafeUpdate(_fastGpuHardware!)) _updatedNow.Add(_fastGpuHardware!);
                else failed = true;
            }
        }
        Unwell = failed;
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
    public (double? Value, SensorKind Kind, string Name, string HardwareType, string HardwareName)? ReadSensor(string id)
    {
        if (!_indexById.TryGetValue(id, out int i)) return null;
        var s = _sensors[i];
        var kind = Enum.TryParse<SensorKind>(s.SensorType.ToString(), out var k) ? k : SensorKind.Factor;
        double? value = s.Value is float f && float.IsFinite(f) ? f : null;
        // Without driver access some temperature sensors report 0 instead of nothing.
        if (kind == SensorKind.Temperature && value <= 0) value = null;
        return (value, kind, s.Name, s.Hardware.HardwareType.ToString(), s.Hardware.Name);
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

    /// <summary>Some hardware didn't answer on the last <see cref="Update"/> (see <see cref="SensorHealth"/>).</summary>
    public bool Unwell { get; private set; }

    private bool _fastWorked;
    private readonly HashSet<IHardware> _failedBefore = [];

    /// <summary>Reads one device; false when it threw. One misbehaving device shouldn't stop the others.</summary>
    private bool SafeUpdate(IHardware hw)
    {
        try
        {
            hw.Update();
            return true;
        }
        catch (Exception ex)
        {
            // Said once per device: it would otherwise be a line a second.
            if (_failedBefore.Add(hw)) Log.Write("sensors", $"{hw.Name} couldn't be read: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        // The memory group may still be opening: let it finish, or closing would pull the library out from under it.
        try { _memoryTask?.Wait(TimeSpan.FromSeconds(30)); } catch { }
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

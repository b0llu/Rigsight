using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// The temperatures <see cref="WindowsTemps"/> reads, put where LibreHardwareMonitor gives none, as sensors like the
/// library's own, so every page, the taskbar, widgets, the overlay, alerts and the history take them as they are:
/// <list type="bullet">
/// <item>An NVMe drive the library lists without a temperature gets one; a drive it doesn't list at all (a standard
/// account's agent opens none) becomes a drive of its own, "/nvme/2" for \\.\PhysicalDrive2 as the library numbers it, so the
/// pages tie it to its letters as they do the library's.</item>
/// <item>A graphics card with no temperature gets its driver's, named "GPU Core" as the library names an NVIDIA card's,
/// so it is the card's temperature everywhere.</item>
/// </list>
/// A PC where the library reads every temperature gets nothing new. Sampler thread only.
/// </summary>
internal sealed class WindowsTempSensors : IDisposable
{
    private readonly List<(WindowsSensor Sensor, SafeFileHandle Drive)> _drives = [];
    private readonly List<(WindowsSensor Sensor, uint Adapter)> _gpus = [];
    private readonly List<IHardware> _own = [];
    private bool _added;

    /// <summary>Drives that are only RigSight's (none listed by the library): read in their tier like the library's drives.</summary>
    public IReadOnlyList<IHardware> OwnDrives => _own;

    /// <summary>
    /// Once, when the library's hardware is first listed: what it lacks, filled in. Says in the log what was added and
    /// why, and what was looked for and not found.
    /// </summary>
    public void Add(IReadOnlyList<IHardware> hardware)
    {
        if (_added) return;
        _added = true;
        try { AddDrives(hardware); }
        catch (Exception ex) { Log.Error("sensors", ex); }
        try { AddGpus(hardware); }
        catch (Exception ex) { Log.Error("sensors", ex); }
    }

    private void AddDrives(IReadOnlyList<IHardware> hardware)
    {
        // The library's drives by number (the number in their identifier, "/nvme/2" is drive 2).
        var listed = new Dictionary<int, IHardware>();
        foreach (var hw in hardware.Where(h => h.HardwareType == HardwareType.Storage))
            if (DriveSpace.DiskOfSensor(hw.Identifier + "/") is int n) listed.TryAdd(n, hw);
        foreach (var drive in WindowsTemps.NvmeDrives())
        {
            // The limits it reports for itself: fixed figures, read once, named as the library names them (the pages
            // take a drive's limit from its "Warning" reading, and leave both out of its temperatures).
            var (warning, critical) = WindowsTemps.DriveLimits(drive.Handle);
            string limits = $"warning {(warning is double w ? $"{w:0} °C" : "none")}, critical {(critical is double c ? $"{c:0} °C" : "none")}";
            if (listed.TryGetValue(drive.Number, out var hw))
            {
                if (hw.Sensors.Any(s => s.SensorType == SensorType.Temperature)) { drive.Handle.Dispose(); continue; }
                var sensor = new WindowsSensor("Temperature", new Identifier(hw.Identifier, "temperature", "windows"), hw);
                if (!Attach(hw, sensor)) { drive.Handle.Dispose(); continue; }
                _drives.Add((sensor, drive.Handle));
                foreach (var limit in Limits(hw, warning, critical, "windows-")) Attach(hw, limit);
                Log.Write("sensors", $"Drive {drive.Number} ({hw.Name}) gave no temperature to the sensor library: reading it from Windows instead ({limits})");
            }
            else
            {
                var own = new WindowsDrive(drive.Model, drive.Number);
                own.Add(Limits(own, warning, critical, ""));
                _own.Add(own);
                _drives.Add((own.Temperature, drive.Handle));
                Log.Write("sensors", $"Drive {drive.Number} ({drive.Model}, NVMe) isn't read by the sensor library (it can't open drives without admin rights): its temperature is read from Windows ({limits})");
            }
        }
        Read(drives: true, gpus: false);
    }

    /// <summary>A drive's reported limits as sensors that keep their figure ("Warning Temperature", "Critical Temperature").</summary>
    private static List<WindowsSensor> Limits(IHardware hw, double? warning, double? critical, string prefix)
    {
        var list = new List<WindowsSensor>();
        foreach (var (name, key, value) in new[] { ("Warning Temperature", "warning", warning), ("Critical Temperature", "critical", critical) })
        {
            if (value is null) continue;
            var sensor = new WindowsSensor(name, new Identifier(hw.Identifier, "temperature", prefix + key), hw);
            sensor.Set(value);
            list.Add(sensor);
        }
        return list;
    }

    private void AddGpus(IReadOnlyList<IHardware> hardware)
    {
        var cards = hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
            && !h.Sensors.Any(s => s.SensorType == SensorType.Temperature)).ToList();
        if (cards.Count == 0) return;
        var adapters = WindowsTemps.Adapters();
        var kept = new HashSet<uint>();
        foreach (var card in cards)
        {
            var adapter = WindowsTemps.Match(card.Name, adapters.Where(a => !kept.Contains(a.Handle)));
            double? first = adapter is null ? null : WindowsTemps.GpuTemperature(adapter.Handle);
            if (adapter is null || first is null)
            {
                Log.Write("gpu", $"Expected a temperature for {card.Name} from its driver, as the sensor library gives none; found "
                    + (adapter is null ? $"no adapter by that name ({string.Join(", ", adapters.Select(a => $"'{a.Name}'"))})" : "none from its driver either"));
                continue;
            }
            var sensor = new WindowsSensor("GPU Core", new Identifier(card.Identifier, "temperature", "windows"), card);
            if (!Attach(card, sensor)) continue;
            kept.Add(adapter.Handle);
            _gpus.Add((sensor, adapter.Handle));
            sensor.Set(first);
            Log.Write("gpu", $"{card.Name} gave no temperature to the sensor library: reading its driver's instead ({first:0.0} °C now)");
        }
        foreach (var a in adapters.Where(a => !kept.Contains(a.Handle))) WindowsTemps.Close(a.Handle);
    }

    /// <summary>
    /// Puts <paramref name="sensor"/> among the library's own sensors of <paramref name="hw"/> (the library's way of adding
    /// one, internal, hence reflection). False, logged, if a library update renamed it: then nothing is added.
    /// </summary>
    internal static bool Attach(IHardware hw, ISensor sensor)
    {
        try
        {
            for (var type = hw.GetType(); type is not null; type = type.BaseType)
                if (type.GetMethod("ActivateSensor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, [typeof(ISensor)]) is { } activate)
                {
                    activate.Invoke(hw, [sensor]);
                    return hw.Sensors.Contains(sensor);
                }
            Log.Write("sensors", $"Expected the sensor library's ActivateSensor on {hw.GetType().Name}, found none: {hw.Name} keeps no temperature");
        }
        catch (Exception ex)
        {
            Log.Error("sensors", ex);
        }
        return false;
    }

    /// <summary>Reads the drives' temperatures (when <paramref name="drives"/>) and the cards' (when <paramref name="gpus"/>).</summary>
    public void Read(bool drives, bool gpus)
    {
        if (drives)
            foreach (var (sensor, handle) in _drives) sensor.Set(WindowsTemps.DriveTemperature(handle));
        if (gpus)
            foreach (var (sensor, adapter) in _gpus) sensor.Set(WindowsTemps.GpuTemperature(adapter));
    }

    /// <summary>Whether any reading of the library's hardware comes from here (the sampler then reads them with it).</summary>
    public bool Any => _drives.Count > 0 || _gpus.Count > 0;

    public void Dispose()
    {
        foreach (var (_, handle) in _drives) handle.Dispose();
        foreach (var (_, adapter) in _gpus) WindowsTemps.Close(adapter);
        _drives.Clear();
        _gpus.Clear();
        _own.Clear();
    }

    /// <summary>A temperature read from Windows, shaped as the library's sensors are (no history, no limits kept).</summary>
    internal sealed class WindowsSensor(string name, Identifier identifier, IHardware hardware) : ISensor
    {
        private float? _value;
        public void Set(double? value) => _value = value is double v ? (float)v : null;

        public IControl? Control => null;
        public IHardware Hardware { get; } = hardware;
        public Identifier Identifier { get; } = identifier;
        public int Index => 0;
        public bool IsDefaultHidden => false;
        public float? Max => _value;
        public float? Min => _value;
        public string Name { get; set; } = name;
        public IReadOnlyList<IParameter> Parameters => [];
        public SensorType SensorType => SensorType.Temperature;
        public float? Value => _value;
        public IEnumerable<SensorValue> Values => [];
        public TimeSpan ValuesTimeWindow { get; set; } = TimeSpan.Zero;
        public void ResetMin() { }
        public void ResetMax() { }
        public void ClearValues() { }
        public void Accept(IVisitor visitor) => visitor.VisitSensor(this);
        public void Traverse(IVisitor visitor) { }
    }

    /// <summary>An NVMe drive the library doesn't list: its model, numbered as the library numbers drives, with its temperature.</summary>
    internal sealed class WindowsDrive : IHardware
    {
        public WindowsDrive(string model, int number)
        {
            Name = model;
            Identifier = new Identifier("nvme", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Temperature = new WindowsSensor("Temperature", new Identifier(Identifier, "temperature", "0"), this);
        }

        private ISensor[] _sensors = [];

        public WindowsSensor Temperature { get; }

        /// <summary>The drive's reported limits, beside its temperature (once, as it's made).</summary>
        public void Add(IEnumerable<WindowsSensor> limits) => _sensors = [Temperature, .. limits];

        public HardwareType HardwareType => HardwareType.Storage;
        public Identifier Identifier { get; }
        public string Name { get; set; }
        public IHardware? Parent => null;
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public ISensor[] Sensors => _sensors.Length > 0 ? _sensors : [Temperature];
        public IHardware[] SubHardware => [];
        public string GetReport() => "";
        /// <summary>Read with the rest of what Windows gives (see <see cref="WindowsTempSensors.Read"/>).</summary>
        public void Update() { }
        public void Accept(IVisitor visitor) => visitor.VisitHardware(this);
        public void Traverse(IVisitor visitor) { foreach (var s in Sensors) s.Accept(visitor); }
#pragma warning disable CS0067 // never raised: the drive's sensors are there from the start
        public event SensorEventHandler? SensorAdded;
        public event SensorEventHandler? SensorRemoved;
#pragma warning restore CS0067
    }
}

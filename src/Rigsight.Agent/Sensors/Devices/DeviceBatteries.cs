using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors.Devices;

/// <summary>
/// Wireless devices' batteries, read once a minute on a thread of their own (a device that's asleep can keep a
/// question waiting over a second, which the sampler mustn't): Logitech devices through their receiver or cable,
/// Bluetooth devices and Xbox controllers from Windows. Each device is a hardware group of its own with one Battery
/// reading, so All sensors, the taskbar, widgets, the overlay and dashboards take it like any other reading.
/// </summary>
internal sealed class DeviceBatteries : IDisposable
{
    private const int EveryMs = 60_000;

    private readonly LogitechDevices _logitech = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly AutoResetEvent _now = new(false);
    private volatile IReadOnlyList<DeviceBattery> _current = [];
    private int _readings;

    // Sampler thread only: the hardware made for each device, by its key.
    private readonly Dictionary<string, DeviceHardware> _hardware = [];
    private string _builtKeys = "";

    public DeviceBatteries()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Device batteries", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Every device's last reading (any thread).</summary>
    public IReadOnlyList<DeviceBattery> Current => _current;

    /// <summary>Goes up by one with each round of readings (any thread), so a check can tell a new round from the last.</summary>
    public int Readings => Volatile.Read(ref _readings);

    /// <summary>Reads again at once (a device was plugged in, the PC woke up).</summary>
    public void ReadNow() => _now.Set();

    private void Loop()
    {
        bool first = true;
        while (!_stop.IsSet)
        {
            var list = new List<DeviceBattery>();
            Take(list, "Logitech", () => _logitech.Read(Environment.TickCount64));
            Take(list, "Bluetooth", WindowsDevices.Bluetooth);
            Take(list, "Xbox", WindowsDevices.Xbox);
            if (RigsightPaths.IsTestInstance) Take(list, "test", TestDevices);
            if (first || Changed(_current, list))
                Log.Write("devices", list.Count == 0 ? "No wireless device gave a battery level (looked at Logitech receivers, Bluetooth, Xbox controllers)"
                    : "Batteries: " + string.Join(", ", list.Select(d => $"{d.Name} {(d.Percent is double p ? $"{p:0}%" : d.Level?.ToString())}"
                        + $"{(d.Charge == ChargeState.Full ? " full on power" : d.Charging == true ? " charging" : "")}{(d.Connected ? "" : " (not connected, last reading)")} via {d.Via}")));
            first = false;
            _current = list;
            Interlocked.Increment(ref _readings);
            WaitHandle.WaitAny([_stop.WaitHandle, _now], EveryMs);
        }
    }

    private static void Take(List<DeviceBattery> into, string what, Func<List<DeviceBattery>> read)
    {
        try { into.AddRange(read()); }
        catch (Exception ex) { Log.Write("devices", $"Reading {what} batteries failed: {ex.Message}"); }
    }

    /// <summary>Whether what would be logged changed: devices, levels (by whole percent), charging or connection.</summary>
    private static bool Changed(IReadOnlyList<DeviceBattery> before, List<DeviceBattery> now) =>
        before.Count != now.Count || before.Zip(now).Any(p => p.First.Key != p.Second.Key || p.First.Percent != p.Second.Percent
            || p.First.Level != p.Second.Level || p.First.Charging != p.Second.Charging || p.First.Charge != p.Second.Charge || p.First.Connected != p.Second.Connected);

    /// <summary>
    /// A test copy's made-up devices, from test-devices.json in its data folder, to check how they look (never read by
    /// the installed app): [{"Key":"t1","Name":"WH-1000XM4","Kind":"Headset","Percent":14,"Connected":true,"Via":"Bluetooth"}].
    /// </summary>
    private static List<DeviceBattery> TestDevices()
    {
        string path = Path.Combine(RigsightPaths.DataDir, "test-devices.json");
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<TestDevice>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })?
            .Select(t => new DeviceBattery("test/" + t.Key, t.Name, t.Kind, t.Percent, t.Level, t.Charging, t.Connected, t.Via) { Charge = t.Full ? ChargeState.Full : t.Charging switch { true => ChargeState.Charging, false => ChargeState.NotCharging, null => ChargeState.Unknown } }).ToList() ?? [];
    }

    private sealed record TestDevice(string Key, string Name, DeviceKind Kind, double? Percent, BatteryLevel? Level, bool? Charging, bool Connected = true, string Via = "Bluetooth", bool Full = false);

    // ── As hardware (sampler thread) ────────────────────────────────────────

    /// <summary>
    /// Sampler thread: puts the newest readings into the devices' sensors. True when the devices themselves changed
    /// (one came, one went), so the list of hardware has to be made again.
    /// </summary>
    public bool Apply()
    {
        var now = _current;
        string keys = string.Join('|', now.Select(d => d.Key));
        foreach (var d in now)
        {
            if (!_hardware.TryGetValue(d.Key, out var hw)) _hardware[d.Key] = hw = new DeviceHardware(d);
            hw.Set(d);
        }
        if (keys == _builtKeys) return false;
        _builtKeys = keys;
        foreach (var gone in _hardware.Keys.Where(k => !now.Any(d => d.Key == k)).ToList()) _hardware.Remove(gone);
        return true;
    }

    /// <summary>The devices as hardware, in the order they were found (sampler thread).</summary>
    public IEnumerable<IHardware> Hardware => _current.Select(d => _hardware.GetValueOrDefault(d.Key)).OfType<IHardware>();

    /// <summary>
    /// Sampler thread: what each device says about charging, by its battery reading's id ("charging", "full"); null when
    /// none says (1.22.0). Sent with each tick, so All sensors shows the bolt or "Full" beside the number.
    /// </summary>
    public Dictionary<string, string>? Charges()
    {
        Dictionary<string, string>? map = null;
        foreach (var hw in _hardware.Values)
            if (DeviceCharge.Wire(hw.Charge) is { } word) (map ??= [])[hw.Battery.Identifier.ToString()] = word;
        return map;
    }

    /// <summary>The reading behind a sensor of theirs, by the sensor's identifier ("/device/…/battery/0").</summary>
    public DeviceBattery? Of(string sensorId) =>
        _current.FirstOrDefault(d => sensorId.StartsWith(DeviceHardware.IdOf(d.Key) + "/", StringComparison.Ordinal));

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(3000);
        _logitech.Dispose();
    }

    /// <summary>A device as the sensor library's hardware: its name, and one Battery sensor.</summary>
    internal sealed class DeviceHardware : IHardware
    {
        public DeviceHardware(DeviceBattery d)
        {
            Name = d.Name;
            Kind = d.Kind;
            Identifier = new Identifier("device", Slug(d.Key));
            Battery = new BatterySensor(new Identifier(Identifier, "battery", "0"), this);
        }

        public DeviceKind Kind { get; private set; }
        public string Via { get; private set; } = "";
        /// <summary>What it says about charging (1.22.0): sent to the app with each tick, drawn on the taskbar.</summary>
        public ChargeState Charge { get; private set; }
        public BatterySensor Battery { get; }

        public void Set(DeviceBattery d)
        {
            Name = d.Name;
            Kind = d.Kind;
            Via = d.Via;
            Charge = d.Charge;
            Battery.Value = d.Percent is double p ? (float)p : d.Level is { } l ? (float)Units.BatteryLevelValue((int)l) : null;
        }

        internal static string IdOf(string key) => "/device/" + Slug(key);

        private static string Slug(string key) => new([.. key.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

        public HardwareType HardwareType => HardwareType.Battery;
        public Identifier Identifier { get; }
        public string Name { get; set; }
        public IHardware? Parent => null;
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public ISensor[] Sensors => [Battery];
        public IHardware[] SubHardware => [];
        public string GetReport() => "";
        /// <summary>Read on the devices' own thread (see <see cref="DeviceBatteries"/>).</summary>
        public void Update() { }
        public void Accept(IVisitor visitor) => visitor.VisitHardware(this);
        public void Traverse(IVisitor visitor) => Battery.Accept(visitor);
#pragma warning disable CS0067 // never raised: the one sensor is there from the start
        public event SensorEventHandler? SensorAdded;
        public event SensorEventHandler? SensorRemoved;
#pragma warning restore CS0067
    }

    /// <summary>A device's battery, shaped as the library's sensors are: 0 to 100, or a level below 0 (see <see cref="Units.BatteryLevelValue"/>).</summary>
    internal sealed class BatterySensor(Identifier identifier, IHardware hardware) : ISensor
    {
        public float? Value { get; set; }
        public IControl? Control => null;
        public IHardware Hardware { get; } = hardware;
        public Identifier Identifier { get; } = identifier;
        public int Index => 0;
        public bool IsDefaultHidden => false;
        public float? Max => Value;
        public float? Min => Value;
        public string Name { get; set; } = "Battery";
        public IReadOnlyList<IParameter> Parameters => [];
        public SensorType SensorType => SensorType.Level;
        public IEnumerable<SensorValue> Values => [];
        public TimeSpan ValuesTimeWindow { get; set; } = TimeSpan.Zero;
        public void ResetMin() { }
        public void ResetMax() { }
        public void ClearValues() { }
        public void Accept(IVisitor visitor) => visitor.VisitSensor(this);
        public void Traverse(IVisitor visitor) { }
    }
}

using System.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Building blocks for the app's view model tests. Everything here runs on the shared UI thread.</summary>
internal static class Kit
{
    /// <summary>A settings model with no agent to talk to (changes stay local), on the UI thread.</summary>
    public static SettingsModel OfflineSettings(RigsightSettings? start = null) => Ui.Run(() =>
    {
        var settings = new SettingsModel(new AgentClient(Ui.Dispatcher));
        settings.ApplyFromAgent(start ?? SeedData.QuietSettings());
        return settings;
    });

    public static LiveData Live(SettingsModel settings) => Ui.Run(() => new LiveData(settings));

    /// <summary>Settings model and live data for a synthetic PC, already greeted.</summary>
    public static (SettingsModel Settings, LiveData Live) Greeted(RigsightSettings? start = null, AgentMessage? hello = null)
    {
        var settings = OfflineSettings(start);
        var live = Live(settings);
        Ui.Run(() => live.LoadHello(hello ?? Pc.Hello()));
        return (settings, live);
    }

    /// <summary>Names of the properties <paramref name="source"/> reports changed while <paramref name="action"/> runs.</summary>
    public static List<string> Changes(INotifyPropertyChanged source, Action action)
    {
        var names = new List<string>();
        void Handler(object? _, PropertyChangedEventArgs e) => names.Add(e.PropertyName ?? "");
        source.PropertyChanged += Handler;
        try { action(); }
        finally { source.PropertyChanged -= Handler; }
        return names;
    }

    /// <summary>Runs async UI work to its end (Ui.Run would pick its Func&lt;T&gt; overload for a lambda returning a task).</summary>
    public static void Wait(Func<Task> work) => Ui.Run(work, 60_000);

    /// <summary>Waits out the settings debounce (350 ms) so a flush has happened.</summary>
    public static void AfterDebounce() => Ui.Pump(450);

    public static ProcInfo Proc(string exe, double memMB, int count = 1, bool window = false, double cpu = 0, string? name = null) =>
        new() { Exe = exe, Name = name ?? Path.GetFileNameWithoutExtension(exe), MemMB = memMB, Count = count, HasWindow = window, Cpu = cpu };

    public static List<ProcDetail> Detail(int count, double memMB = 100) =>
        [.. Enumerable.Range(0, count).Select(i => new ProcDetail { Pid = 100 + i, Label = $"Process {i}", MemMB = memMB, Cpu = i * 0.5 })];
}

/// <summary>
/// A small made-up PC: a CPU, a GPU, RAM (and Windows' virtual memory), one SSD and a board sensor chip, with the key
/// sensors mapped the way the agent maps them.
/// </summary>
internal static class Pc
{
    public static readonly (string Hardware, string Type, (string Id, string Name, SensorKind Kind)[] Sensors)[] Layout =
    [
        ("Test CPU", "Cpu",
        [
            ("/cpu/temperature/0", "Core (Tctl/Tdie)", SensorKind.Temperature),
            ("/cpu/temperature/1", "CCD1 (Tdie)", SensorKind.Temperature),
            ("/cpu/load/0", "CPU Total", SensorKind.Load),
            ("/cpu/load/1", "CPU Core #1", SensorKind.Load),
            ("/cpu/load/2", "CPU Core #2 Thread #1", SensorKind.Load),
            ("/cpu/load/3", "CPU Core Max", SensorKind.Load),
            ("/cpu/power/0", "Package", SensorKind.Power),
            ("/cpu/clock/0", "Cores (Average)", SensorKind.Clock),
            ("/cpu/voltage/0", "Core (SVI2 TFN)", SensorKind.Voltage),
        ]),
        ("Test GPU", "GpuNvidia",
        [
            ("/gpu/temperature/0", "GPU Core", SensorKind.Temperature),
            ("/gpu/temperature/1", "GPU Hot Spot", SensorKind.Temperature),
            ("/gpu/temperature/2", "GPU Memory Junction", SensorKind.Temperature),
            ("/gpu/load/0", "GPU Core", SensorKind.Load),
            ("/gpu/load/1", "GPU Memory", SensorKind.Load),
            ("/gpu/smalldata/0", "GPU Memory Used", SensorKind.SmallData),
            ("/gpu/smalldata/1", "GPU Memory Total", SensorKind.SmallData),
            ("/gpu/power/0", "GPU Package", SensorKind.Power),
            ("/gpu/clock/0", "GPU Core", SensorKind.Clock),
            ("/gpu/control/0", "GPU Fan", SensorKind.Control),
        ]),
        ("Total Memory", "Memory",
        [
            ("/ram/load/0", "Memory", SensorKind.Load),
            ("/ram/data/0", "Memory Used", SensorKind.Data),
            ("/ram/data/1", "Memory Available", SensorKind.Data),
        ]),
        ("Virtual Memory", "Memory",
        [
            ("/vram/load/0", "Memory", SensorKind.Load),
        ]),
        ("Test SSD", "Storage",
        [
            ("/nvme/0/temperature/0", "Composite Temperature", SensorKind.Temperature),
            ("/nvme/0/temperature/10", "Warning Temperature", SensorKind.Temperature),
            ("/nvme/0/level/20", "Life", SensorKind.Level),
            ("/nvme/0/load/30", "Used Space", SensorKind.Load),
            ("/nvme/0/factor/0", "Power On Hours", SensorKind.Factor),
        ]),
        ("Test Board", "SuperIO",
        [
            ("/lpc/fan/0", "Fan #1", SensorKind.Fan),
            ("/lpc/fan/1", "Fan #2", SensorKind.Fan),
            ("/lpc/fan/2", "Fan #3", SensorKind.Fan),
            ("/lpc/temperature/0", "System", SensorKind.Temperature),
            ("/lpc/temperature/1", "Bogus", SensorKind.Temperature),
            ("/lpc/voltage/0", "Vcore", SensorKind.Voltage),
        ]),
    ];

    public static readonly string[] Ids = [.. Layout.SelectMany(h => h.Sensors).Select(s => s.Id)];
    public static int Count => Ids.Length;
    public static int IndexOf(string id) => Array.IndexOf(Ids, id);

    private static readonly (string Key, string Id)[] KeyMap =
    [
        (KeySensors.CpuTemp, "/cpu/temperature/0"), (KeySensors.CpuDieTemp, "/cpu/temperature/1"), (KeySensors.CpuLoad, "/cpu/load/0"),
        (KeySensors.CpuPower, "/cpu/power/0"), (KeySensors.CpuClock, "/cpu/clock/0"), (KeySensors.CpuVoltage, "/cpu/voltage/0"),
        (KeySensors.GpuTemp, "/gpu/temperature/0"), (KeySensors.GpuHotSpot, "/gpu/temperature/1"), (KeySensors.GpuMemJunction, "/gpu/temperature/2"),
        (KeySensors.GpuLoad, "/gpu/load/0"), (KeySensors.GpuVramLoad, "/gpu/load/1"), (KeySensors.GpuVramUsed, "/gpu/smalldata/0"),
        (KeySensors.GpuVramTotal, "/gpu/smalldata/1"), (KeySensors.GpuPower, "/gpu/power/0"), (KeySensors.GpuClock, "/gpu/clock/0"),
        (KeySensors.GpuFan, "/gpu/control/0"), (KeySensors.RamLoad, "/ram/load/0"), (KeySensors.RamUsed, "/ram/data/0"),
        (KeySensors.RamAvailable, "/ram/data/1"),
    ];

    public static Dictionary<string, int> Keys => KeyMap.ToDictionary(k => k.Key, k => IndexOf(k.Id));

    /// <summary>The hello for this PC; <paramref name="skip"/> leaves hardware out (a PC without it).</summary>
    public static AgentMessage Hello(params string[] skip)
    {
        var hardware = Layout.Where(h => !skip.Contains(h.Hardware)).Select(h => new HardwareMeta
        {
            Name = h.Hardware, Type = h.Type,
            Sensors = [.. h.Sensors.Select(s => new SensorMeta { Id = s.Id, Name = s.Name, Kind = s.Kind })],
        }).ToList();
        var ids = hardware.SelectMany(h => h.Sensors).Select(s => s.Id).ToList();
        return new AgentMessage
        {
            T = "hello",
            IsAdmin = true,
            Hardware = hardware,
            Keys = KeyMap.Where(k => ids.Contains(k.Id)).ToDictionary(k => k.Key, k => ids.IndexOf(k.Id)),
        };
    }

    /// <summary>A tick with every sensor at <paramref name="baseValue"/> (plus its index / 10), and some set explicitly.</summary>
    public static AgentMessage Tick(long time, float baseValue = 40, params (string Id, float? Value)[] set)
    {
        var values = new float?[Count];
        for (int i = 0; i < Count; i++) values[i] = baseValue + i / 10f;
        foreach (var (id, v) in set) values[IndexOf(id)] = v;
        return new AgentMessage { T = "tick", Time = time, Values = values };
    }
}

/// <summary>
/// A settings model and client connected to a <see cref="FakeAgent"/> (started with the run's settings file), wired
/// as the shell wires them, to see exactly what a page sends to the agent.
/// </summary>
internal sealed class AgentLink : IDisposable
{
    public FakeAgent Agent { get; }
    public AgentClient Client { get; }
    public SettingsModel Settings { get; }

    public AgentLink()
    {
        Agent = new FakeAgent(SharedData.ResetSettings());
        (Client, Settings) = Ui.Run(() =>
        {
            var client = new AgentClient(Ui.Dispatcher);
            var settings = new SettingsModel(client);
            client.MessageReceived += m =>
            {
                if (m.Settings is not null) settings.ApplyFromAgent(m.Settings);
                if (m.T == "hello") settings.OnConnected();
            };
            client.Start();
            return (client, settings);
        });
        Assert.True(Ui.WaitFor(() => Client.IsConnected && Agent.ClientCount > 0), "didn't connect to the fake agent");
        Ui.Pump(100);
    }

    /// <summary>The last time the page sent <paramref name="cmd"/> (waiting for it to arrive).</summary>
    public UiMessage Sent(string cmd, int count = 1)
    {
        Assert.True(Ui.WaitFor(() => Agent.Commands(cmd).Count >= count, 5000), $"\"{cmd}\" wasn't sent");
        return Agent.Commands(cmd)[^1];
    }

    public void Dispose()
    {
        Ui.Run(Client.Dispose);
        Agent.Dispose();
        Ui.Run(() => Units.Fahrenheit = false);
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Rigsight.Agent.Ipc;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Support;

/// <summary>
/// Stands in for Rigsight.Agent: the agent's own <see cref="PipeServer"/> on this test run's pipe (never the real one),
/// greeting each app with a hello built from a captured real PC, and recording what the app sends.
/// </summary>
public sealed class FakeAgent : IDisposable
{
    private readonly PipeServer _pipe;
    public ConcurrentQueue<UiMessage> Received { get; } = new();
    public Func<AgentMessage> Hello { get; set; }

    public FakeAgent(RigsightSettings? settings = null, bool integratedGpu = false)
    {
        Settings = settings ?? SeedData.QuietSettings();
        IntegratedGpu = integratedGpu; // before listening: the first app to connect gets the right hello
        Hello = () => IntegratedGpu ? Fixtures.HelloWithIntegratedGpu(Settings) : Fixtures.Hello(Settings);
        _pipe = new PipeServer(() => Hello(), msg =>
        {
            Received.Enqueue(msg);
            // Like the agent: settings sent by the app are saved and echoed back to every app.
            if (msg.T == "settings" && msg.Settings is not null)
            {
                Settings = SettingsStore.Deserialize(SettingsStore.Serialize(msg.Settings));
                Broadcast(new AgentMessage { T = "settings", Settings = Settings });
            }
        });
        _pipe.Start();
    }

    public RigsightSettings Settings { get; private set; }

    /// <summary>The captured PC with a processor's integrated graphics too (see <see cref="Fixtures.HelloWithIntegratedGpu"/>).</summary>
    public bool IntegratedGpu { get; set; }

    public int ClientCount => _pipe.ClientCount;

    public void Broadcast(AgentMessage message) => _pipe.Broadcast(message);

    /// <summary>A live tick from the captured PC, its values nudged so every tick differs.</summary>
    public void Tick(int n = 0) => Broadcast(IntegratedGpu ? Fixtures.TickWithIntegratedGpu(n) : Fixtures.Tick(n));

    public void Procs(int apps = 60, string? detailFor = null) => Broadcast(new AgentMessage { T = "procs", Procs = Fixtures.Procs(apps, detailFor) });

    public List<UiMessage> Commands(string cmd) => [.. Received.Where(m => m.Cmd == cmd)];

    public void Dispose() => _pipe.Dispose();
}

/// <summary>Messages from a real PC (Ryzen 7 5700X3D, RTX 3080 Ti, three drives), captured once from a running agent.</summary>
public static class Fixtures
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Read(string name) => File.ReadAllText(Path.Combine(Folder, name));

    public static AgentMessage Hello(RigsightSettings? settings = null)
    {
        var hello = ProtocolJson.Deserialize<AgentMessage>(Read("hello-ryzen-rtx.json"))!;
        hello.Settings = settings;
        hello.IsAdmin = true;
        // The captured history ends when it was captured: move it to end now, so charts show it.
        long shift = TimeUtil.NowUnixMs() - (hello.History?.SelectMany(s => s.Times).DefaultIfEmpty(0).Max() ?? 0);
        foreach (var s in hello.History ?? []) s.Times = [.. s.Times.Select(t => t + shift)];
        return hello;
    }

    public static int SensorCount => Hello().Hardware!.Sum(h => h.Sensors.Count);

    /// <summary>A processor's graphics, as LibreHardwareMonitor lists a Ryzen's: added before the card.</summary>
    public const string IntegratedGpuName = "AMD Radeon(TM) Graphics";

    private static readonly (SensorKind Kind, string Name, string Id, float Value)[] IntegratedGpuSensors =
    [
        (SensorKind.Temperature, "GPU Core", "/gpu-amd/0/temperature/0", 44f),
        (SensorKind.Temperature, "GPU Memory", "/gpu-amd/0/temperature/1", 54f),
        (SensorKind.Load, "GPU Core", "/gpu-amd/0/load/0", 7f),
        (SensorKind.Power, "GPU Package", "/gpu-amd/0/power/0", 12.5f),
        (SensorKind.Clock, "GPU Core", "/gpu-amd/0/clock/0", 600f),
        (SensorKind.SmallData, "D3D Dedicated Memory Used", "/gpu-amd/0/smalldata/0", 512f),
        (SensorKind.SmallData, "D3D Dedicated Memory Total", "/gpu-amd/0/smalldata/1", 2048f),
    ];

    /// <summary>Where the integrated GPU goes in the hardware list: just before the card.</summary>
    private static int IntegratedGpuAt(AgentMessage hello) => hello.Hardware!.FindIndex(h => h.Type.StartsWith("Gpu", StringComparison.Ordinal));

    /// <summary>
    /// The captured PC with integrated graphics listed before its RTX card (as on a Ryzen with a card, where only the
    /// integrated graphics used to show), with the key sensors worked out as the agent does.
    /// </summary>
    public static AgentMessage HelloWithIntegratedGpu(RigsightSettings? settings = null)
    {
        var hello = Hello(settings);
        var igpu = new HardwareMeta { Name = IntegratedGpuName, Type = "GpuAmd" };
        foreach (var (kind, name, id, _) in IntegratedGpuSensors) igpu.Sensors.Add(new SensorMeta { Id = id, Name = name, Kind = kind });
        hello.Hardware!.Insert(IntegratedGpuAt(hello), igpu);
        var candidates = new List<KeySensors.Candidate>();
        int index = 0;
        for (int hw = 0; hw < hello.Hardware.Count; hw++)
            foreach (var meta in hello.Hardware[hw].Sensors)
                candidates.Add(new KeySensors.Candidate(index++, hello.Hardware[hw].Type, hello.Hardware[hw].Name, meta.Name, meta.Kind, hw));
        hello.Keys = KeySensors.Pick(candidates);
        return hello;
    }

    /// <summary>A tick for <see cref="HelloWithIntegratedGpu"/>: the integrated GPU's readings in their place.</summary>
    public static AgentMessage TickWithIntegratedGpu(int n = 0)
    {
        var tick = Tick(n);
        var plain = Hello();
        int offset = plain.Hardware!.Take(IntegratedGpuAt(plain)).Sum(h => h.Sensors.Count);
        var values = tick.Values!.ToList();
        values.InsertRange(offset, IntegratedGpuSensors.Select(x => (float?)(x.Value + n % 3)));
        tick.Values = [.. values];
        return tick;
    }

    public static AgentMessage Tick(int n = 0)
    {
        var tick = ProtocolJson.Deserialize<AgentMessage>(Read("tick-ryzen-rtx.json"))!;
        tick.Time = TimeUtil.NowUnixMs();
        tick.ExtremesDay = DateTime.Today.ToString("yyyy-MM-dd");
        if (n > 0)
        {
            tick.ExtremesFull = false;
            tick.Extremes = [];
            for (int i = 0; i < tick.Values!.Length; i++)
                if (tick.Values[i] is float v) tick.Values[i] = v + (float)Math.Sin(n + i) * Math.Max(0.5f, Math.Abs(v) * 0.03f);
        }
        tick.Activity = new ActivityInfo
        {
            Exe = "cyberpunk2077.exe", Name = "Cyberpunk 2077", Category = AppCategory.Game, Present = true,
            SessionStart = TimeUtil.NowUnix() - 3600, SessionActiveSec = 3500, SessionCpuMax = 71, SessionGpuMax = 68,
        };
        tick.Today = new TodayInfo
        {
            OnSec = 5 * 3600, ActiveSec = 4 * 3600, IdleSec = 3600, TopApp = "Cyberpunk 2077", TopAppSec = 2 * 3600,
            CpuPeak = 78, CpuPeakApp = "Cyberpunk 2077", CpuPeakCategory = AppCategory.Game,
            GpuPeak = 71, GpuPeakApp = "Cyberpunk 2077", GpuPeakCategory = AppCategory.Game,
        };
        return tick;
    }

    /// <summary>A running PC's apps: a browser with many processes, a game, services, and a long tail.</summary>
    public static List<ProcInfo> Procs(int apps = 60, string? detailFor = null)
    {
        var list = new List<ProcInfo>
        {
            new() { Exe = "chrome.exe", Name = "Google Chrome", Count = 38, Cpu = 3.2, MemMB = 4210, HasWindow = true },
            new() { Exe = "cyberpunk2077.exe", Name = "Cyberpunk 2077", Count = 1, Cpu = 22.5, MemMB = 9120, HasWindow = true },
            new() { Exe = "svchost.exe", Name = "Service Host", Count = 92, Cpu = 0.4, MemMB = 1640 },
            new() { Exe = "discord.exe", Name = "Discord", Count = 7, Cpu = 0.8, MemMB = 820, HasWindow = true },
            new() { Exe = "explorer.exe", Name = "File Explorer", Count = 1, Cpu = 0.1, MemMB = 210, HasWindow = true },
        };
        for (int i = list.Count; i < apps; i++)
            list.Add(new ProcInfo { Exe = $"app{i:000}.exe", Name = $"Synthetic App {i}", Count = 1 + i % 4, Cpu = i % 7 * 0.1, MemMB = 400.0 / i * 10 });
        foreach (var p in list.Where(p => p.Exe == detailFor))
            p.Processes = [.. Enumerable.Range(0, p.Count).Select(i => new ProcDetail
            {
                Pid = 1000 + i, Label = i == 0 ? "Main process" : i % 5 == 0 ? "Graphics" : "Tab", Cpu = i % 3 * 0.2, MemMB = p.MemMB / p.Count,
            })];
        return list;
    }

    public static JsonElement Json(string name) => JsonDocument.Parse(Read(name)).RootElement;
}

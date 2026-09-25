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

    public FakeAgent(RigsightSettings? settings = null)
    {
        Settings = settings ?? SeedData.QuietSettings();
        Hello = () => Fixtures.Hello(Settings);
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

    public int ClientCount => _pipe.ClientCount;

    public void Broadcast(AgentMessage message) => _pipe.Broadcast(message);

    /// <summary>A live tick from the captured PC, its values nudged so every tick differs.</summary>
    public void Tick(int n = 0) => Broadcast(Fixtures.Tick(n));

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

using System.Text.Json;
using System.Text.Json.Serialization;
using Rigsight.Core.Apps;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Protocol;

// The agent and the app talk over a named pipe using one JSON object per line.

public sealed class SensorMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SensorKind Kind { get; set; }
}

public sealed class HardwareMeta
{
    public string Name { get; set; } = "";
    /// <summary>LibreHardwareMonitor HardwareType name, e.g. "Cpu", "GpuNvidia", "SuperIO".</summary>
    public string Type { get; set; } = "";
    public List<SensorMeta> Sensors { get; set; } = [];
}

/// <summary>Recent history of one key sensor, sent when the app connects so charts start filled.</summary>
public sealed class SeriesHistory
{
    public string Key { get; set; } = "";
    public long[] Times { get; set; } = [];
    public float?[] Values { get; set; } = [];
}

/// <summary>What the user is doing right now.</summary>
public sealed class ActivityInfo
{
    public string? Exe { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public AppCategory Category { get; set; }
    /// <summary>User is at the PC (recent input, or a fullscreen app is running).</summary>
    public bool Present { get; set; }
    public bool Fullscreen { get; set; }
    public bool Paused { get; set; }
    /// <summary>Unix seconds when the current session of the foreground app started (0 = none).</summary>
    public long SessionStart { get; set; }
    public double SessionActiveSec { get; set; }
    public double? SessionCpuMax { get; set; }
    public double? SessionGpuMax { get; set; }
}

/// <summary>Running totals for today, kept live by the agent.</summary>
public sealed class TodayInfo
{
    public double OnSec { get; set; }
    public double ActiveSec { get; set; }
    public double IdleSec { get; set; }
    public string? TopApp { get; set; }
    public double TopAppSec { get; set; }
    public double? CpuPeak { get; set; }
    /// <summary>When the peak was (unix seconds).</summary>
    public long? CpuPeakTime { get; set; }
    /// <summary>The app working the CPU hardest just before the peak; null when no app clearly was.</summary>
    public string? CpuPeakApp { get; set; }
    public double? GpuPeak { get; set; }
    public long? GpuPeakTime { get; set; }
    public string? GpuPeakApp { get; set; }
    /// <summary>What kind of app each peak's app is (for the words under it); null with no app, or from an older agent.</summary>
    public AppCategory? CpuPeakCategory { get; set; }
    public AppCategory? GpuPeakCategory { get; set; }

    /// <summary>"While playing Rematch", or the time when no app clearly was (null before the first reading).</summary>
    [JsonIgnore] public string? CpuPeakLine => PeakWords.Line(CpuPeak, CpuPeakApp, CpuPeakTime, CpuPeakCategory);
    [JsonIgnore] public string? GpuPeakLine => PeakWords.Line(GpuPeak, GpuPeakApp, GpuPeakTime, GpuPeakCategory);
}

/// <summary>A drive's SMART health, as judged the way CrystalDiskInfo does (hard drives have no wear "Life").</summary>
public sealed class DriveHealthInfo
{
    public string Name { get; set; } = "";
    /// <summary>Good · Caution · Bad · Unknown</summary>
    public string Status { get; set; } = "Unknown";
    /// <summary>Sectors moved to spares, waiting to be, and unreadable (SATA drives; null when not reported).</summary>
    public long? ReallocatedSectors { get; set; }
    public long? PendingSectors { get; set; }
    public long? UncorrectableSectors { get; set; }
}

/// <summary>Sensors the agent isn't reading right now, and why (hello). Nothing skipped: no parts, no safe mode.</summary>
public sealed class SensorStatus
{
    /// <summary>What's paused for another program: e.g. { "Motherboard", ["Gigabyte Control Center"] }.</summary>
    public List<SkippedSensors> Paused { get; set; } = [];

    /// <summary>The last hardware scan didn't finish (the PC hung or had to be restarted), so the risky parts were skipped.</summary>
    public bool SafeMode { get; set; }

    /// <summary>Windows' kernel memory kept growing while the hardware was read, so reading those parts was stopped.</summary>
    public bool StoppedForMemory { get; set; }
}

/// <summary>One kind of hardware that isn't being read, and the programs it was left to.</summary>
public sealed class SkippedSensors
{
    /// <summary>Motherboard · Fan hubs · Power supply</summary>
    public string Part { get; set; } = "";
    public List<string> Because { get; set; } = [];
}

/// <summary>One app's live resource use (all of its processes combined).</summary>
public sealed class ProcInfo
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public int Count { get; set; }
    public double Cpu { get; set; }
    public double MemMB { get; set; }
    public bool HasWindow { get; set; }
    /// <summary>Each of its processes, biggest first; only for apps the app asked about ("procs-detail").</summary>
    public List<ProcDetail>? Processes { get; set; }
}

/// <summary>One process of an app.</summary>
public sealed class ProcDetail
{
    public int Pid { get; set; }
    /// <summary>What it is: its window's title, or its role ("Tab", "Graphics", a service's name…).</summary>
    public string Label { get; set; } = "";
    public double Cpu { get; set; }
    public double MemMB { get; set; }
}

/// <summary>The internet right now (the last second): the whole connection's speed and each app's, in bytes a second.</summary>
public sealed class NetLive
{
    /// <summary>When (unix seconds).</summary>
    public long Time { get; set; }
    public double Down { get; set; }
    public double Up { get; set; }
    /// <summary>The apps moving anything, fastest first.</summary>
    public List<NetAppLive> Apps { get; set; } = [];
}

public sealed class NetAppLive
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public double Down { get; set; }
    public double Up { get; set; }
}

/// <summary>Agent → app.</summary>
public sealed class AgentMessage
{
    /// <summary>hello · tick · procs · settings · navigate · overlay · update</summary>
    public string T { get; set; } = "";

    // hello
    public string? Version { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>Whether the agent is registered to start with Windows (hello and "status").</summary>
    public bool? StartupEnabled { get; set; }
    public List<HardwareMeta>? Hardware { get; set; }
    public Dictionary<string, int>? Keys { get; set; }
    /// <summary>The graphics adapters as Windows offers them to games (the main GPU first); null from agents before 0.5.17.</summary>
    public List<KeySensors.PreferredGpu>? PreferredGpus { get; set; }
    public List<SeriesHistory>? History { get; set; }
    /// <summary>The internet's speed over the last minute (hello), oldest first; null without app network use (no admin rights).</summary>
    public List<NetLive>? NetHistory { get; set; }
    public List<DriveHealthInfo>? Drives { get; set; }
    /// <summary>Sensors left out on purpose (another program controls that hardware, or the last scan didn't finish).</summary>
    public SensorStatus? SensorStatus { get; set; }

    // hello / settings
    public RigsightSettings? Settings { get; set; }

    // tick
    public long Time { get; set; }
    public float?[]? Values { get; set; }
    public ActivityInfo? Activity { get; set; }
    public TodayInfo? Today { get; set; }
    /// <summary>Today's [lowest, highest] per sensor id: every sensor when <see cref="ExtremesFull"/>, else only changes.</summary>
    public Dictionary<string, double[]>? Extremes { get; set; }
    public string? ExtremesDay { get; set; }
    public bool ExtremesFull { get; set; }
    /// <summary>The internet this second (null: not read, no admin rights).</summary>
    public NetLive? Net { get; set; }

    // procs
    public List<ProcInfo>? Procs { get; set; }

    // overlay (also on hello): whether it's on screen, and whether another program already owns its shortcut
    public bool? OverlayVisible { get; set; }
    public bool? OverlayHotkeyTaken { get; set; }
    /// <summary>RivaTuner: running · stopped · missing · installing · install-failed</summary>
    public string? RtssState { get; set; }

    // navigate (also allowed on hello)
    public string? Page { get; set; }
    public string? Arg { get; set; }

    /// <summary>update: "started" once the installer is running, "failed" if the agent couldn't start it.</summary>
    public string? UpdateStatus { get; set; }
}

/// <summary>App → agent.</summary>
public sealed class UiMessage
{
    /// <summary>settings · cmd</summary>
    public string T { get; set; } = "";
    public RigsightSettings? Settings { get; set; }
    /// <summary>clear-history · startup-on · startup-off · pause · resume · quit · overlay-toggle · overlay-status · install-rtss · start-rtss · install-update …</summary>
    public string? Cmd { get; set; }
    public string? Arg { get; set; }
}

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

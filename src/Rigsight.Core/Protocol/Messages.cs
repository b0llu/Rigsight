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
    /// <summary>
    /// The CPU's busiest minute today, in percent, with when and the app doing the work: what Home shows where the
    /// CPU's temperature can't be read (a standard account). Null before the first full minute, or from an older agent.
    /// </summary>
    public double? CpuBusy { get; set; }
    public long? CpuBusyTime { get; set; }
    public string? CpuBusyApp { get; set; }
    public AppCategory? CpuBusyCategory { get; set; }
    /// <summary>What kind of app each peak's app is (for the words under it); null with no app, or from an older agent.</summary>
    public AppCategory? CpuPeakCategory { get; set; }
    public AppCategory? GpuPeakCategory { get; set; }

    /// <summary>"While playing Rematch", or the time when no app clearly was (null before the first reading).</summary>
    [JsonIgnore] public string? CpuPeakLine => PeakWords.Line(CpuPeak, CpuPeakApp, CpuPeakTime, CpuPeakCategory);
    [JsonIgnore] public string? GpuPeakLine => PeakWords.Line(GpuPeak, GpuPeakApp, GpuPeakTime, GpuPeakCategory);
    [JsonIgnore] public string? CpuBusyLine => PeakWords.Line(CpuBusy, CpuBusyApp, CpuBusyTime, CpuBusyCategory);
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

    // The rest of what the drive reports (wear and drive details, 2026-11-14), read by the same SMART read.

    /// <summary>The drive's lasting name (see <see cref="Rigsight.Core.Storage.DriveKey"/>): what its daily record and its reads and writes are kept under.</summary>
    public string? Key { get; set; }
    /// <summary>Windows' number for the drive (\\.\PhysicalDriveN), which <see cref="VolumeSpace.Disk"/> gives each letter.</summary>
    public int? DiskNumber { get; set; }
    public bool IsNvme { get; set; }
    public bool IsSsd { get; set; }
    /// <summary>"NVMe", "SATA", "USB"… as Windows reports the connection.</summary>
    public string? Bus { get; set; }
    public ulong SizeBytes { get; set; }
    /// <summary>Life left, 100 new to 0 (SSDs; null for a hard drive).</summary>
    public double? Life { get; set; }
    /// <summary>Everything the drive says it has read and written in its life, in GB.</summary>
    public double? ReadGb { get; set; }
    public double? WrittenGb { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerOnCount { get; set; }
    public double? Temperature { get; set; }
    /// <summary>The temperature the drive itself warns at (an NVMe drive's WCTEMP), when it says.</summary>
    public double? TemperatureWarning { get; set; }
    public double? TemperatureCritical { get; set; }
    /// <summary>Every attribute it reports, as CrystalDiskInfo lists them (NVMe drives' health log is given the same shape).</summary>
    public List<DriveAttribute>? Attributes { get; set; }
}

/// <summary>One SMART attribute: its number and the maker's name for it, the drive's own score now, its worst and the score it calls failing at, and the raw count.</summary>
public sealed class DriveAttribute
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Current { get; set; }
    public int Worst { get; set; }
    public int Threshold { get; set; }
    public ulong Raw { get; set; }
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

    /// <summary>
    /// What is wrong with the PawnIO driver, which CPU readings come through: null (nothing, or the agent has no admin
    /// rights and says that instead) · "missing" (not on this PC) · "stopped" (installed, but Windows doesn't have it
    /// running and the CPU's temperature isn't read).
    /// </summary>
    public string? Driver { get; set; }

    /// <summary>The agent installing the driver after "Install": null · "installing" · "failed" · "no-winget".</summary>
    public string? DriverInstall { get; set; }
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
    /// <summary>What its processes read and write, in bytes a second (0 from an agent that doesn't measure it). Most apps
    /// are at rest at any moment: nothing is sent for those.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Disk { get; set; }
    /// <summary>When the earliest of its processes started (Unix seconds; 0: not known).</summary>
    public long Started { get; set; }
    /// <summary>Windows marks one of its processes as one it can't lose: ending it stops Windows. Not said by an agent
    /// without admin rights, nor by an older one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Critical { get; set; }
    /// <summary>Each of its processes, biggest first; only for apps the app asked about ("procs-detail").</summary>
    public List<ProcDetail>? Processes { get; set; }
}

/// <summary>One process of an app.</summary>
public sealed class ProcDetail
{
    public int Pid { get; set; }
    /// <summary>What it is: its window's title, or its role ("Tab", "Graphics", a service's name…).</summary>
    public string Label { get; set; } = "";
    /// <summary>
    /// Its part in an app made of many processes ("Tab", "Extension", "Graphics", "Main"…), whatever its label says (a
    /// window's title takes the label's place); null for an app that isn't built that way.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }
    public double Cpu { get; set; }
    public double MemMB { get; set; }
    /// <summary>What it reads and writes, in bytes a second.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Disk { get; set; }
    /// <summary>Windows marks it as a process it can't lose (see <see cref="ProcInfo.Critical"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Critical { get; set; }
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
    /// <summary>Windows has stopped delivering network events though the network is in use, and the agent couldn't get
    /// them back: nothing is being recorded for now (it keeps trying).</summary>
    public bool Stalled { get; set; }
    /// <summary>While <see cref="Stalled"/>: <see cref="Down"/> and <see cref="Up"/> are the whole PC's, from the network
    /// cards' own counters, with no apps (issue #13: the speeds went blank with the apps).</summary>
    public bool FromCards { get; set; }

    /// <summary>Whether <see cref="Down"/> and <see cref="Up"/> are a real speed now (the apps' sum, or the cards').</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSpeed => !Stalled || FromCards;
}

/// <summary>
/// The line right now (the last minute of the line check): the middle ping to this PC's router (the home side) and to
/// the internet provider's first router (the provider's side), in milliseconds, how much the provider's jumped, and the
/// share of pings that went unanswered. Null times: that stop isn't known or didn't answer.
/// </summary>
public sealed class LineLive
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LanMs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IspMs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Jitter { get; set; }
    public double LostPercent { get; set; }
    /// <summary>The provider's router was found: without it only the home side is checked.</summary>
    public bool ProviderFound { get; set; }
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
    /// <summary>hello · tick · procs · settings · navigate · overlay · update · scanned (the answer to "scan-now") · history-cleared · drives (each time the drives are read, in <see cref="Drives"/>)</summary>
    public string T { get; set; } = "";

    // hello
    public string? Version { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>The agent runs under a standard account: admin rights would take someone else's password, so nothing asks for them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StandardAccount { get; set; }
    /// <summary>The PC starts Rigsight for everyone who signs in / keeps it up to date by itself (hello and "status"); see MachineSetup.</summary>
    public bool? Everyone { get; set; }
    public bool? KeepUpdated { get; set; }
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
    /// <summary>The line check's last minute (null: no router found yet, or an agent from before 1.3.0).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LineLive? Line { get; set; }
    /// <summary>The taskbar readings waiting for room in "+N" (by sensor id); null when every one fits.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TrayWaiting { get; set; }

    // procs
    public List<ProcInfo>? Procs { get; set; }
    /// <summary>What every process together reads and writes, in bytes a second (null from an agent that doesn't measure it).</summary>
    public double? Disk { get; set; }

    // overlay (also on hello): whether it's on screen, and whether another program already owns its shortcut
    public bool? OverlayVisible { get; set; }
    public bool? OverlayHotkeyTaken { get; set; }
    /// <summary>The Ask shortcut (Settings › Ask RigSight) is already used by another program.</summary>
    public bool? AskHotkeyTaken { get; set; }
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
    /// <summary>clear-history · startup-on · startup-off · pause · resume · quit · overlay-toggle · overlay-status · install-rtss · start-rtss · install-update · scan-now · task-ended …</summary>
    public string? Cmd { get; set; }
    public string? Arg { get; set; }
}

/// <summary>
/// The messages' JSON code, made when the app is built. Read by reflection, the agent's first message to a window
/// just opened (its hello, with every setting in it) had .NET make a setter for each field as the window opened: about
/// 850 of the 1,750 methods compiled while opening (2026-11-12).
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentMessage))]
[JsonSerializable(typeof(UiMessage))]
[JsonSerializable(typeof(ProcInfo))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
        // The generated code for the messages; anything else asked of it still by reflection.
        TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(ProtocolJsonContext.Default, new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()),
    };

    /// <summary>The same options by reflection only, as before the generated code (for the tests: both must write the same).</summary>
    internal static readonly JsonSerializerOptions ByReflection = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

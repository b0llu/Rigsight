using System.Text.Json;
using System.Text.Json.Serialization;
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
    public string? CpuPeakApp { get; set; }
    public double? GpuPeak { get; set; }
    public string? GpuPeakApp { get; set; }
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
}

/// <summary>Agent → app.</summary>
public sealed class AgentMessage
{
    /// <summary>hello · tick · procs · settings · navigate</summary>
    public string T { get; set; } = "";

    // hello
    public string? Version { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>Whether the agent is registered to start with Windows (hello and "status").</summary>
    public bool? StartupEnabled { get; set; }
    public List<HardwareMeta>? Hardware { get; set; }
    public Dictionary<string, int>? Keys { get; set; }
    public List<SeriesHistory>? History { get; set; }

    // hello / settings
    public RigsightSettings? Settings { get; set; }

    // tick
    public long Time { get; set; }
    public float?[]? Values { get; set; }
    public ActivityInfo? Activity { get; set; }
    public TodayInfo? Today { get; set; }

    // procs
    public List<ProcInfo>? Procs { get; set; }

    // navigate (also allowed on hello)
    public string? Page { get; set; }
    public string? Arg { get; set; }
}

/// <summary>App → agent.</summary>
public sealed class UiMessage
{
    /// <summary>settings · cmd</summary>
    public string T { get; set; } = "";
    public RigsightSettings? Settings { get; set; }
    /// <summary>clear-history · startup-on · startup-off · pause · resume · quit</summary>
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

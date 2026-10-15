using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rigsight.Core.Data;

namespace Rigsight.Core.Stability;

/// <summary>
/// One reading of everything the agent knows about the PC at a moment, kept for a close-up of a crash (every 2 seconds
/// or so, as the sensors are read). Null where it wasn't read: a standard account has no CPU temperature, a PC with no
/// battery no charge. Fans and apps are the last ones read (every 10 and 5 seconds), carried to each reading.
/// </summary>
public sealed class CloseUpPoint
{
    /// <summary>When, in Unix milliseconds.</summary>
    [JsonPropertyName("t")] public long T { get; set; }
    [JsonPropertyName("ct")] public double? CpuTemp { get; set; }
    [JsonPropertyName("gt")] public double? GpuTemp { get; set; }
    [JsonPropertyName("gh")] public double? GpuHot { get; set; }
    [JsonPropertyName("cl")] public double? CpuLoad { get; set; }
    [JsonPropertyName("gl")] public double? GpuLoad { get; set; }
    [JsonPropertyName("cp")] public double? CpuPower { get; set; }
    [JsonPropertyName("gp")] public double? GpuPower { get; set; }
    /// <summary>Memory in use, GB.</summary>
    [JsonPropertyName("r")] public double? Ram { get; set; }
    /// <summary>Memory promised to apps, % of all Windows can promise.</summary>
    [JsonPropertyName("cm")] public double? Commit { get; set; }
    /// <summary>The graphics card's memory in use and all it has, MB.</summary>
    [JsonPropertyName("v")] public double? Vram { get; set; }
    [JsonPropertyName("vt")] public double? VramTotal { get; set; }
    /// <summary>What the drives read and wrote, and what the network moved down and up, bytes a second.</summary>
    [JsonPropertyName("d")] public double? Disk { get; set; }
    [JsonPropertyName("dn")] public double? Down { get; set; }
    [JsonPropertyName("up")] public double? Up { get; set; }
    [JsonPropertyName("b")] public int? Battery { get; set; }
    [JsonPropertyName("ob")] public bool? OnBattery { get; set; }
    /// <summary>The app in front (its id in the apps table), and whether anyone was at the PC.</summary>
    [JsonPropertyName("f")] public long? Front { get; set; }
    [JsonPropertyName("h")] public bool Here { get; set; }
    [JsonPropertyName("fans")] public List<CloseUpFan>? Fans { get; set; }
    /// <summary>The apps doing the most (a few for the CPU, the GPU and memory), the hardest on the CPU first.</summary>
    [JsonPropertyName("a")] public List<CloseUpApp>? Apps { get; set; }

    [JsonIgnore] public DateTime Time => DateTimeOffset.FromUnixTimeMilliseconds(T).LocalDateTime;
}

public sealed record CloseUpFan([property: JsonPropertyName("n")] string Name, [property: JsonPropertyName("r")] int Rpm);

/// <summary>An app's share of the CPU and the GPU (%) and its memory (MB).</summary>
public sealed record CloseUpApp([property: JsonPropertyName("a")] long App, [property: JsonPropertyName("c")] double Cpu,
    [property: JsonPropertyName("g")] double Gpu, [property: JsonPropertyName("m")] double MemMb);

/// <summary>Why a close-up was kept: an app or game crashed.</summary>
public static class CloseUpKind
{
    public const string App = "app";
}

/// <summary>
/// The seconds around one crash: for an app crash the 5 minutes before and the 2 after (the before is kept at once, the
/// after added when it is in: <see cref="Done"/>). Found by the crash's own time and program.
/// </summary>
public sealed record CloseUp(long Ts, string Exe, string Kind, bool Done, IReadOnlyList<CloseUpPoint> Points)
{
    /// <summary>How much before an app crash is kept, and after it.</summary>
    public const int BeforeSec = 5 * 60, AfterSec = 2 * 60;

    /// <summary>How long the agent keeps readings in memory: the before and the after, and a little to spare for a crash noticed late.</summary>
    public const int RingSec = 7 * 60;

    public DateTime Time => TimeUtil.FromUnix(Ts);

    /// <summary>The points, gzipped JSON: about 10 KB for 7 minutes.</summary>
    public static byte[] Pack(IReadOnlyList<CloseUpPoint> points)
    {
        using var buffer = new MemoryStream();
        using (var zip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(zip, new CloseUpData { P = [.. points] }, CloseUpJson.Default.CloseUpData);
        return buffer.ToArray();
    }

    /// <summary>The points back; empty when they can't be read (cut short, or written by a newer version in a shape this one can't read).</summary>
    public static List<CloseUpPoint> Unpack(byte[]? data)
    {
        if (data is null || data.Length == 0) return [];
        try
        {
            using var zip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            return JsonSerializer.Deserialize(zip, CloseUpJson.Default.CloseUpData)?.P ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return [];
        }
    }
}

internal sealed class CloseUpData
{
    [JsonPropertyName("p")] public List<CloseUpPoint> P { get; set; } = [];
}

// Written by code made at build time, as CrashFacts is: the usual way's first use costs the agent its start-up time.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloseUpData))]
internal sealed partial class CloseUpJson : JsonSerializerContext;

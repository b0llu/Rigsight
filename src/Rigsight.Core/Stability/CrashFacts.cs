using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rigsight.Core.Stability;

/// <summary>
/// What else Windows wrote down about a crash, beside the few things every crash has (<see cref="CrashEvent"/>): kept
/// as it was read, because Windows' logs only reach back so far. Everything here can be missing: an older Windows, a
/// log that no longer holds it, a crash stored before this was kept. Stored as JSON in crashes.facts.
/// </summary>
public sealed class CrashFacts
{
    /// <summary>The crashed program's version, and the version of the file it crashed in.</summary>
    public string? AppVersion { get; set; }
    public string? ModuleVersion { get; set; }
    /// <summary>Where in that file (the same from one crash to the next when it is the same fault).</summary>
    public string? Offset { get; set; }
    /// <summary>
    /// How long the program had been running when it failed, in seconds: Windows writes the moment it was started
    /// into the crash's entry. (A session in Rigsight's own history is a stretch of use, which begins anew each time
    /// the app comes to the front, so it can't say this.)
    /// </summary>
    public double? RanSec { get; set; }
    /// <summary>Windows Error Reporting's own name for the kind of failure ("APPCRASH", "BEX64", "CLR20r3", "AppHangXProcB1").</summary>
    public string? ReportType { get; set; }
    /// <summary>A frozen app's report can name the other program it was waiting on, with its version.</summary>
    public string? WaitedOn { get; set; }
    public string? WaitedOnVersion { get; set; }
    /// <summary>A .NET app's unhandled error ("System.NullReferenceException") and where it was thrown.</summary>
    public string? Error { get; set; }
    public string? ErrorAt { get; set; }
    /// <summary>The driver file Windows names for a blue screen, and what that file says it is.</summary>
    public string? Driver { get; set; }
    public string? DriverName { get; set; }
    /// <summary>The four numbers that go with a blue screen's code.</summary>
    public string[]? BugParams { get; set; }
    /// <summary>A PC going down: the power button was held (Windows notes it at the next start). Null when not read.</summary>
    public bool? PowerButton { get; set; }
    /// <summary>…and how many hardware errors the firmware handed Windows at that start.</summary>
    public int? HardwareErrors { get; set; }
    /// <summary>…and when that start was (Unix seconds).</summary>
    public long? NextStart { get; set; }
    /// <summary>
    /// A graphics driver reset read from Windows' hang report: the address its records share. Windows writes one hang
    /// up two or three times (a quick record, a fuller one seconds later, sometimes again later), all with this.
    /// </summary>
    public string? HangKey { get; set; }

    [JsonIgnore]
    public bool IsEmpty => RanSec is null && AppVersion is null && ModuleVersion is null && Offset is null && ReportType is null && WaitedOn is null && Error is null
                           && Driver is null && BugParams is null && PowerButton is null && HardwareErrors is null && NextStart is null && HangKey is null;

    /// <summary>
    /// These facts with whatever <paramref name="later"/> has that these don't (Windows writes a blue screen's driver
    /// and a crash's report some seconds or minutes after the crash's own entry, so a first reading can come before
    /// them). Nothing already known is replaced: the first reading is the nearest to the event. Null when nothing is added.
    /// </summary>
    public CrashFacts? With(CrashFacts later)
    {
        var merged = (CrashFacts)MemberwiseClone();
        bool added = false;
        foreach (var p in typeof(CrashFacts).GetProperties().Where(p => p.CanWrite && p.CanRead))
            if (p.GetValue(merged) is null && p.GetValue(later) is { } value) { p.SetValue(merged, value); added = true; }
        return added ? merged : null;
    }

    // Written by code made at build time (see CrashFactsJson): the first use of the usual way costs the agent about
    // 50 ms of CPU at every start, working out the type's shape.
    public string ToJson() => JsonSerializer.Serialize(this, CrashFactsJson.Default.CrashFacts);

    public static CrashFacts? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize(json, CrashFactsJson.Default.CrashFacts); }
        catch (JsonException) { return null; } // written by a newer version in a shape this one can't read
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CrashFacts))]
internal sealed partial class CrashFactsJson : JsonSerializerContext;

/// <summary>What kind of trouble Windows logged apart from the crashes themselves (see <see cref="CrashSign"/>).</summary>
public static class SignKind
{
    /// <summary>The hardware reported an error (WHEA).</summary>
    public const string Hardware = "hardware";
    /// <summary>A drive failed to read or write, or its file system was found damaged.</summary>
    public const string Drive = "drive";
    /// <summary>The graphics driver reported a fault of the card.</summary>
    public const string Graphics = "gpu";
    /// <summary>Windows filed a report that the graphics card stopped answering. Its time is when the report was filed,
    /// which for a PC that then went down is the next start.</summary>
    public const string GraphicsHang = "gpu-hang";
    /// <summary>
    /// The moment the graphics card hung, from the report's own time (Windows keeps it where only an admin can read it):
    /// for a PC that went down, this is before it went, not when it was written up at the next start.
    /// </summary>
    public const string GraphicsHangAt = "gpu-hang-at";
    /// <summary>Windows was running out of memory.</summary>
    public const string Memory = "memory";
}

/// <summary>
/// Something Windows logged that isn't a crash but can explain one that came soon after: a drive error, a hardware
/// error, the graphics driver reporting a fault. Kept (crash_signs) only to be set beside crashes.
/// </summary>
public sealed record CrashSign(long Ts, string Kind, string Detail)
{
    public DateTime Time => Data.TimeUtil.FromUnix(Ts);
}

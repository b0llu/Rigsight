using Rigsight.Core.Settings;

namespace Rigsight.Core.Data;

// All timestamps are Unix seconds (UTC). Hour and day buckets start at *local* hour/midnight
// boundaries, so reports line up with the user's clock even in half-hour time zones.

public sealed class AppRow
{
    public long Id { get; set; }
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public AppCategory Category { get; set; }
    public long FirstSeen { get; set; }
}

/// <summary>
/// One local day of <see cref="SystemMinute"/> rows, added up (system_day): minutes on, active and away time, sums and
/// counts for the averages, and the day's highs. Long periods (a year) read these instead of every minute.
/// </summary>
public sealed class SystemDay
{
    public long Day { get; set; }
    public int Minutes { get; set; }
    public double ActiveSec { get; set; }
    public double IdleSec { get; set; }
    public double CpuTempSum { get; set; }
    public int CpuTempN { get; set; }
    public double GpuTempSum { get; set; }
    public int GpuTempN { get; set; }
    public double CpuLoadSum { get; set; }
    public int CpuLoadN { get; set; }
    public double GpuLoadSum { get; set; }
    public int GpuLoadN { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTempMax { get; set; }
    public double? GpuHotMax { get; set; }
    public double? CpuVoltMax { get; set; }
    public double? GpuVoltMax { get; set; }
    public double? CpuPowerMax { get; set; }
    public double? GpuPowerMax { get; set; }

    // Temperatures by load band (see LoadBands), so a day's idle and heavy-load averages can be compared with other
    // days' without reading their minutes; and the clocks. All zero in days from before 0.9.1, until recomputed.
    public double IdleCpuSum { get; set; }
    public int IdleCpuN { get; set; }
    public double IdleGpuSum { get; set; }
    public int IdleGpuN { get; set; }
    public double LoadCpuSum { get; set; }
    public int LoadCpuN { get; set; }
    public double LoadGpuSum { get; set; }
    public int LoadGpuN { get; set; }
    public double CpuClockSum { get; set; }
    public int CpuClockN { get; set; }
    public double GpuClockSum { get; set; }
    public int GpuClockN { get; set; }

    public double OnSec() => Minutes * 60.0;

    public double? IdleCpu => IdleCpuN > 0 ? IdleCpuSum / IdleCpuN : null;
    public double? IdleGpu => IdleGpuN > 0 ? IdleGpuSum / IdleGpuN : null;
    public double? LoadCpu => LoadCpuN > 0 ? LoadCpuSum / LoadCpuN : null;
    public double? LoadGpu => LoadGpuN > 0 ? LoadGpuSum / LoadGpuN : null;
}

/// <summary>A fan the agent has seen: its sensor, and its name and hardware as the sensors list gives them.</summary>
public sealed record FanRow(long Id, string Sensor, string Name, string Hardware);

/// <summary>One fan's speed over one minute the PC was on (rpm, averaged and the highest).</summary>
public sealed record FanMinute(long Ts, long Fan, int RpmAvg, int RpmMax);

/// <summary>One fan's minutes of a local day added up (rpm sum over the minutes and their count, the highest, and the same at idle).</summary>
public sealed record FanDay(long Day, long Fan, double RpmSum, int RpmN, int RpmMax, double IdleSum, int IdleN)
{
    public double? Rpm => RpmN > 0 ? RpmSum / RpmN : null;
    public double? IdleRpm => IdleN > 0 ? IdleSum / IdleN : null;
}

/// <summary>One row per minute the PC was on: system-wide sensor summary and what was in front.</summary>
public sealed class SystemMinute
{
    public long Ts { get; set; }
    public double? CpuTemp { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTemp { get; set; }
    public double? GpuTempMax { get; set; }
    public double? GpuHotMax { get; set; }
    public double? GpuMemMax { get; set; }
    public double? CpuLoad { get; set; }
    public double? GpuLoad { get; set; }
    public double? CpuPower { get; set; }
    public double? GpuPower { get; set; }
    public double? CpuVoltMax { get; set; }
    public double? GpuVoltMax { get; set; }
    public double? RamUsed { get; set; }
    /// <summary>App that was in front for most of the minute.</summary>
    public long? FgApp { get; set; }
    /// <summary>
    /// The app working the CPU (GPU) hardest in the minute before the minute's hottest CPU (GPU) reading: what its highs
    /// are put down to. Null when no app clearly was, and in minutes from before 0.8.1.
    /// </summary>
    public long? CpuApp { get; set; }
    public long? GpuApp { get; set; }
    /// <summary>Average clocks over the minute, in MHz (null before 0.9.1, or without the sensor).</summary>
    public double? CpuClock { get; set; }
    public double? GpuClock { get; set; }
    public int ActiveSec { get; set; }
    public int IdleSec { get; set; }
}

/// <summary>Per-app totals for one local hour. Written as deltas and summed by the database.</summary>
public sealed class AppHour
{
    public long Ts { get; set; }
    public long AppId { get; set; }

    /// <summary>In front and in use.</summary>
    public double FgSec { get; set; }
    /// <summary>In front but the user was away.</summary>
    public double IdleSec { get; set; }
    /// <summary>Open with a visible window, but another app was in front.</summary>
    public double BgSec { get; set; }
    /// <summary>Open with all windows minimized.</summary>
    public double MinSec { get; set; }

    public double CpuSum { get; set; }
    public int CpuN { get; set; }
    public double? CpuMax { get; set; }
    public double MemSum { get; set; }
    public int MemN { get; set; }
    public double? MemMax { get; set; }

    // Hardware readings attributed to this app while it was in use.
    public double CpuTempSum { get; set; }
    public int CpuTempN { get; set; }
    public double? CpuTempMax { get; set; }
    public double GpuTempSum { get; set; }
    public int GpuTempN { get; set; }
    public double? GpuTempMax { get; set; }
    public double? GpuHotMax { get; set; }
    public double? CpuPowerMax { get; set; }
    public double? GpuPowerMax { get; set; }
    public double? CpuVoltMax { get; set; }
    public double? GpuVoltMax { get; set; }
    public double GpuLoadSum { get; set; }
    public int GpuLoadN { get; set; }

    public bool IsEmpty => FgSec == 0 && IdleSec == 0 && BgSec == 0 && MinSec == 0 && CpuN == 0 && MemN == 0 && CpuTempN == 0;
}

/// <summary>A stretch of time actively using one app (like "played for 2h 14m").</summary>
public sealed class SessionRow
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public double ActiveSec { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTempMax { get; set; }
    public bool IsGame { get; set; }
}

public sealed class DriveDay
{
    public long Day { get; set; }
    public string Drive { get; set; } = "";
    public double UsedGb { get; set; }
    public double TotalGb { get; set; }
}

public static class TimeUtil
{
    public static long ToUnix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();
    public static DateTime FromUnix(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static DateTime LocalMinuteStart(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
    public static DateTime LocalHourStart(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);

    /// <summary>
    /// Start of the current local hour as Unix seconds, counted back from now. Unlike converting a local
    /// time, this can't be off by an hour in the repeated hour when clocks go back, and it keeps local hour
    /// boundaries for half-hour time zones (e.g. India, UTC+5:30).
    /// </summary>
    public static long LocalHourStartUnix()
    {
        var now = DateTime.Now;
        return NowUnix() - (now.Minute * 60 + now.Second);
    }
}

/// <summary>What was going on just before a crash (see RigsightDb.GetCrashContext).</summary>
/// <summary>
/// A day's heat (heat_day): for an app, its minutes of steady heavy GPU load with the GPU and CPU temperatures and GPU
/// power summed; for app 0, the minutes at rest with their temperatures.
/// </summary>
public sealed record HeatDay(long Day, long App, int N, double GpuSum, int GpuN, double CpuSum, int CpuN, double PowerSum, int PowerN);

public sealed record CrashContext(double? CpuBefore, double? GpuBefore, long? FrontApp, double? SessionSec);

using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

public enum ReportRange { Day, Week, Month }

public sealed record Peak(double Value, DateTime Time, string? App);

public enum InsightTone { Neutral, Good, Warn, Hot }

/// <summary>One plain-language observation, e.g. "Elden Ring ran your GPU the hottest (avg 74°C)".</summary>
public sealed record Insight(string Icon, string Text, InsightTone Tone = InsightTone.Neutral);

public sealed class AppStat
{
    public long Id { get; init; }
    public string Exe { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public AppCategory Category { get; init; }

    public double ActiveSec { get; set; }
    public double AwaySec { get; set; }
    public double BackgroundSec { get; set; }
    public double MinimizedSec { get; set; }
    public double OpenSec => ActiveSec + AwaySec + BackgroundSec + MinimizedSec;

    public double? CpuTempAvg { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTempAvg { get; set; }
    public double? GpuTempMax { get; set; }
    public double? GpuHotMax { get; set; }
    public double? CpuVoltMax { get; set; }
    public double? GpuVoltMax { get; set; }
    public double? CpuPowerMax { get; set; }
    public double? GpuPowerMax { get; set; }
    public double? GpuLoadAvg { get; set; }
    public double? CpuAvg { get; set; }
    public double? CpuMax { get; set; }
    public double? MemAvg { get; set; }
    public double? MemMax { get; set; }

    public int SessionCount { get; set; }
    public double LongestSessionSec { get; set; }
}

public sealed class SessionInfo
{
    public long AppId { get; init; }
    public string Name { get; init; } = "";
    public string Exe { get; init; } = "";
    public string? Path { get; init; }
    public AppCategory Category { get; init; }
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public double ActiveSec { get; init; }
    public double? CpuTempMax { get; init; }
    public double? GpuTempMax { get; init; }
    public bool IsGame { get; init; }
}

public sealed class TimelineSegment
{
    public DateTime Start { get; init; }
    public DateTime End { get; set; }
    public long? AppId { get; init; }
    public string? App { get; init; }
    public AppCategory Category { get; init; }
    public bool Away { get; init; }
}

public sealed record TempPoint(DateTime Time, double? Cpu, double? Gpu);

public sealed class DayBucket
{
    public DateTime Day { get; init; }
    public double OnSec { get; set; }
    public double ActiveSec { get; set; }
    public double? CpuTempAvg { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTempAvg { get; set; }
    public double? GpuTempMax { get; set; }
    public Dictionary<AppCategory, double> ActiveByCategory { get; } = [];
    public string? TopApp { get; set; }
}

public sealed class Report
{
    public ReportRange Range { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public bool HasData { get; set; }

    public double OnSec { get; set; }
    public double ActiveSec { get; set; }
    public double AwaySec { get; set; }

    public double? CpuTempAvg { get; set; }
    public double? GpuTempAvg { get; set; }
    public double? CpuLoadAvg { get; set; }
    public double? GpuLoadAvg { get; set; }

    public Peak? CpuTempPeak { get; set; }
    public Peak? GpuTempPeak { get; set; }
    public Peak? GpuHotPeak { get; set; }
    public Peak? CpuVoltPeak { get; set; }
    public Peak? GpuVoltPeak { get; set; }
    public Peak? CpuPowerPeak { get; set; }
    public Peak? GpuPowerPeak { get; set; }

    public List<AppStat> Apps { get; set; } = [];
    public List<SessionInfo> Sessions { get; set; } = [];
    public List<TimelineSegment> Timeline { get; set; } = [];
    public List<TempPoint> Temps { get; set; } = [];
    public List<DayBucket> Days { get; set; } = [];
    public Dictionary<AppCategory, double> ActiveByCategory { get; } = [];
    public List<CrashEvent> Crashes { get; set; } = [];
    public List<Insight> Insights { get; set; } = [];

    public string Title => Range switch
    {
        ReportRange.Day when From.Date == DateTime.Today => "Today",
        ReportRange.Day when From.Date == DateTime.Today.AddDays(-1) => "Yesterday",
        ReportRange.Day => From.ToString("dddd, d MMMM"),
        ReportRange.Week => $"Week of {From:d MMM}",
        _ => From.ToString("MMMM yyyy"),
    };
}

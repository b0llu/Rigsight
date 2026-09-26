using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>A calendar period: one day, a Monday-to-Sunday week, a month, a year, or everything recorded.</summary>
/// <summary>The kinds of period a report covers. Custom: any whole hours, from one date and time to another.</summary>
public enum ReportRange { Day, Week, Month, Year, All, Custom }

public sealed record Peak(double Value, DateTime Time, string? App);

public enum InsightTone { Neutral, Good, Warn, Hot }

/// <summary>One plain-language observation, e.g. "Elden Ring ran your GPU the hottest (avg 74°C)".</summary>
/// <param name="Key">What it's about ("screen", "top-app", "peak"…), so a page can leave out what it already shows.</param>
/// <param name="Priority">Higher comes first; warnings rank above trivia.</param>
public sealed record Insight(string Icon, string Text, InsightTone Tone = InsightTone.Neutral, string Key = "", int Priority = 50);

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

/// <summary>Average temperatures over the minutes that matched a load condition (Minutes of them).</summary>
public sealed record LoadTemps(double? Cpu, double? Gpu, int Minutes);

/// <summary>A stretch of use without a break of 5 minutes or more, and the app mostly in front.</summary>
public sealed record Stretch(DateTime Start, DateTime End, string? App)
{
    public double Seconds => (End - Start).TotalSeconds;
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

    /// <summary>When this report was read: minutes after it (and the minute in progress) aren't in it yet.</summary>
    public DateTime BuiltAt { get; init; } = DateTime.Now;

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

    public double GamingSec { get; set; }
    public DateTime? FirstActive { get; set; }
    public DateTime? LastActive { get; set; }
    /// <summary>First use from 5 AM on (earlier use is the night before running late).</summary>
    public DateTime? DayStart { get; set; }
    /// <summary>End of the last use before 5 AM, if the night before ran late.</summary>
    public DateTime? LateUntil { get; set; }
    public Stretch? LongestStretch { get; set; }
    public LoadTemps? IdleTemps { get; set; }
    public LoadTemps? GpuLoadTemps { get; set; }
    public LoadTemps? CpuLoadTemps { get; set; }
    /// <summary>Gpu = average hot-spot-minus-core gap under heavy GPU load.</summary>
    public LoadTemps? HotSpotGap { get; set; }
    public int CpuOverLimitMin { get; set; }
    public int GpuOverLimitMin { get; set; }

    /// <summary>Days in <see cref="Days"/> with anything recorded (for daily averages).</summary>
    public int DaysWithData => Days.Count(d => d.OnSec > 0);

    public List<AppStat> Apps { get; set; } = [];
    public List<SessionInfo> Sessions { get; set; } = [];
    public List<TimelineSegment> Timeline { get; set; } = [];
    public List<TempPoint> Temps { get; set; } = [];
    public List<DayBucket> Days { get; set; } = [];
    public Dictionary<AppCategory, double> ActiveByCategory { get; } = [];
    public List<CrashEvent> Crashes { get; set; } = [];
    public List<Insight> Insights { get; set; } = [];

    /// <summary>
    /// "Thu 25 Sep, 8 AM – Fri 26 Sep, 1 AM"; the day once when it's the same day ("Thu 25 Sep, 8 AM – 11 PM"); the year on
    /// both ends when either isn't this year ("Thu 25 Sep 2025, 12 AM – Thu 17 Sep 2026, 11 AM").
    /// </summary>
    public static string CustomTitle(DateTime from, DateTime to)
    {
        static string Hour(DateTime t) => t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt");
        // Ending at midnight belongs to the day it ends (Thu 8 AM – 12 AM), not the next.
        var lastDay = to.TimeOfDay == TimeSpan.Zero && to > from ? to.AddDays(-1).Date : to.Date;
        bool years = from.Year != DateTime.Today.Year || lastDay.Year != DateTime.Today.Year;
        string Day(DateTime t) => t.ToString(years ? "ddd d MMM yyyy" : "ddd d MMM");
        return from.Date == lastDay
            ? $"{Day(from)}, {Hour(from)} – {Hour(to)}"
            : $"{Day(from)}, {Hour(from)} – {Day(to)}, {Hour(to)}";
    }

    public string Title => Range switch
    {
        ReportRange.Custom => CustomTitle(From, To),
        ReportRange.Day when From.Date == DateTime.Today => "Today",
        ReportRange.Day when From.Date == DateTime.Today.AddDays(-1) => "Yesterday",
        ReportRange.Day => From.ToString("dddd, d MMMM"),
        ReportRange.Week => $"Week of {From:d MMM}",
        ReportRange.Year when From.Year == DateTime.Today.Year => "This year",
        ReportRange.Year => From.Year.ToString(),
        ReportRange.All => "All time",
        _ => From.ToString("MMMM yyyy"),
    };
}

using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>A calendar period: one day, a Monday-to-Sunday week, a month, a year, or everything recorded.</summary>
/// <summary>The kinds of period a report covers. Custom: any whole hours, from one date and time to another.</summary>
public enum ReportRange { Day, Week, Month, Year, All, Custom }

public sealed record Peak(double Value, DateTime Time, string? App);

public enum InsightTone { Neutral, Good, Warn, Hot }

/// <summary>One plain-language observation, e.g. "Your GPU spent 48 minutes over 75°, nearly all of it on Rematch."</summary>
/// <param name="Key">What it's about ("screen", "top-app", "peak"…), so a page can leave out what it already shows.</param>
/// <param name="Priority">Higher comes first; warnings rank above trivia.</param>
/// <param name="Detail">The numbers behind it, for whoever wants to check ("Rematch 46 min · Chrome 2 min").</param>
public sealed record Insight(string Icon, string Text, InsightTone Tone = InsightTone.Neutral, string Key = "", int Priority = 50, string Detail = "")
{
    /// <summary>The detail for a tooltip: none when there's nothing to add (a bound tooltip of "" would show an empty box).</summary>
    public string? DetailOrNull => Detail.Length == 0 ? null : Detail;
}

/// <summary>Minutes a part spent over its warm line (<see cref="Report.HotLine"/>) put down to one app.</summary>
public sealed record HotShare(string App, int Minutes);

/// <summary>
/// Work an app did in the background: the longest run of minutes it kept the CPU (or GPU) under heavy load while
/// another app was in front.
/// </summary>
public sealed record BackgroundWork(string App, string FrontApp, AppCategory FrontCategory, DateTime Start, int Minutes, bool Gpu);

public enum RecordKind { LongestGameSession, HottestGpu, HottestCpu, MostScreenTime }

/// <summary>A record this period set: the value (formatted), the app if any, and how many days back it holds (30, 90 or 365).</summary>
public sealed record RecordNote(RecordKind Kind, string Value, string? App, int Days);

/// <summary>
/// One app working the GPU hard, steadily: its minutes at heavy GPU load past the first ten of each run (still warming
/// up then), on how many days, and the average temperatures and GPU power over them. The same game at steady load is
/// the one like-for-like way to compare temperatures across time: a lighter game, a warm-up or a menu isn't.
/// </summary>
public sealed record SteadyLoad(long AppId, string App, int Minutes, int Days, double Gpu, double? Cpu, double? GpuPower)
{
    /// <summary>Two stretches of the same app's steady load as one (a day and the week before it).</summary>
    public static SteadyLoad Merge(SteadyLoad a, SteadyLoad b)
    {
        int n = a.Minutes + b.Minutes;
        static double? W(double? x, int nx, double? y, int ny) => x is null ? y : y is null ? x : (x * nx + y * ny) / (nx + ny);
        return new SteadyLoad(a.AppId, a.App, n, a.Days + b.Days, (a.Gpu * a.Minutes + b.Gpu * b.Minutes) / n,
            W(a.Cpu, a.Minutes, b.Cpu, b.Minutes), W(a.GpuPower, a.Minutes, b.GpuPower, b.Minutes));
    }
}

/// <summary>
/// How a PC ran some months ago: each app's steady load and the temperatures at rest then (the room's measure), from
/// <see cref="From"/> to <see cref="To"/>.
/// </summary>
public sealed record ThenHeat(List<SteadyLoad> Steady, LoadTemps? Rest, DateTime From, DateTime To);

/// <summary>
/// A fan over a period, for telling a stopped fan from one that's meant to be still: the minutes it should have been
/// turning (for a GPU fan, the GPU at 70° or more: below that many stop on purpose; for any other, whenever the PC was
/// on), how many of them it read 0 rpm, and the longest run of those, when it started and how hot the part got in it.
/// </summary>
public sealed record FanStat(string Name, string Hardware, bool Gpu, int SpinMinutes, int StoppedMinutes, int LongestStop, DateTime? StopStart, double? StopTemp);

/// <summary>Minutes a chip ran its clocks down while hot under load, by how much, and from what temperature.</summary>
public sealed record Throttling(int Minutes, double DropPercent, double FromTemp);

/// <summary>Your usual on this weekday: the same weekday over the weeks before.</summary>
public sealed record WeekdayUsual(DayOfWeek Day, int Days, double ActiveSec, double GamingSec);

/// <summary>What the insights compare a period with beyond the week before it.</summary>
public sealed record InsightContext(WeekdayUsual? Weekday = null, ThenHeat? Then = null);

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

    /// <summary>
    /// Time left on with nobody there, in unbroken stretches of <see cref="LongAwayMinutes"/> or more with nothing working
    /// hard (a render or a download left running is work, not the PC sitting there).
    /// </summary>
    public double LongAwaySec { get; set; }
    public const int LongAwayMinutes = 30;

    public double? CpuTempAvg { get; set; }
    public double? GpuTempAvg { get; set; }
    public double? CpuLoadAvg { get; set; }
    public double? GpuLoadAvg { get; set; }

    public Peak? CpuTempPeak { get; set; }
    public Peak? GpuTempPeak { get; set; }

    /// <summary>The highest minute averages: how hot a chip held, not a moment's spike (as <see cref="CpuTempPeak"/> can be).</summary>
    public double? CpuTempHeld { get; set; }
    public double? GpuTempHeld { get; set; }
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

    /// <summary>
    /// Temperatures at rest: someone at the PC, nothing working the CPU or GPU, and a quarter of an hour since either
    /// last worked hard (a GPU cooling down from a game isn't at rest). They follow the room.
    /// </summary>
    public LoadTemps? RestTemps { get; set; }

    /// <summary>Each app's steady heavy GPU load (see <see cref="SteadyLoad"/>), most minutes first.</summary>
    public List<SteadyLoad> Steady { get; set; } = [];

    /// <summary>Gpu = average hot-spot-minus-core gap under steady heavy GPU load (Minutes of it).</summary>
    public LoadTemps? HotSpotGap { get; set; }

    /// <summary>Minutes a chip averaged at or over its alert limit (a moment's spike doesn't count, as it doesn't for the alert).</summary>
    public int CpuOverLimitMin { get; set; }
    public int GpuOverLimitMin { get; set; }

    /// <summary>Over this, a minute counts as hot (its highest reading): what the hot minutes by app are counted from.</summary>
    public const double HotLine = 75;

    /// <summary>Minutes over <see cref="HotLine"/>, and how they split between the apps working the part (unknown ones left out).</summary>
    public int CpuHotMinutes { get; set; }
    public int GpuHotMinutes { get; set; }
    public List<HotShare> CpuHotByApp { get; set; } = [];
    public List<HotShare> GpuHotByApp { get; set; } = [];

    /// <summary>The longest run of heavy work an app did while another was in front, if any lasted.</summary>
    public BackgroundWork? BackgroundWork { get; set; }

    /// <summary>What was going on just before each crash (by crash ID), when there were any.</summary>
    public Dictionary<long, CrashContext> CrashContexts { get; set; } = [];

    /// <summary>Records this period set against the days before it, and how many days in a row screen time beat its usual.</summary>
    public List<RecordNote> Records { get; set; } = [];
    public int StreakDays { get; set; }

    public List<FanStat> Fans { get; set; } = [];
    public Throttling? GpuThrottle { get; set; }

    /// <summary>
    /// The game in front for the last minutes of a period still going on: its session isn't over, so it isn't among
    /// <see cref="Sessions"/> yet.
    /// </summary>
    public string? GameOngoing { get; set; }

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

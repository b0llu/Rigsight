using System.Globalization;
using System.Text;
using Rigsight.Core.Data;

namespace Rigsight.Core.Ai;

/// <summary>
/// How long the CPU stayed hot, said as facts the app works out, not left to the model to read off minute rows. get_moment
/// gave one CPU figure a minute (64 to 85 °C at 5 to 7% busy, only 4:05 and 4:14 PM at 80 or more), and the answer
/// either called the cooler faulty or waved the heat off as "a single brief spike" (audit Q035, 2026-11-17). The line
/// counts the hot minutes, their longest unbroken stretch, how busy the CPU was in them, their averages over the whole
/// minute, the power and clock then, and says that we don't record the CPU slowing itself to cool.
/// </summary>
public sealed partial class AiTools
{
    /// <summary>A minute's highest CPU reading at or over this counts as hot for the heat line.</summary>
    internal const double HotCpu = 80;

    /// <summary>Under this much CPU load a hot minute was a quiet one: not a game, a compile or a render.</summary>
    internal const double QuietCpuLoad = 25;

    /// <summary>get_temperatures reads a period's hot minutes for it up to this long; past it the day rows say enough.</summary>
    private const long HeatLineMaxSeconds = 31 * 86400L;

    /// <summary>Stretches said one by one in get_moment; the rest are counted.</summary>
    private const int MostHeatStretches = 6;

    /// <summary>
    /// get_moment's heat line over the minutes it lists ("" when none reached <see cref="HotCpu"/>). The rows above give
    /// each minute's highest CPU reading, so the line says that and adds the averages, which tell a moment's spike from
    /// a minute that stayed hot.
    /// </summary>
    internal static string CpuHeatInMinutes(IReadOnlyList<SystemMinute> minutes)
    {
        var read = minutes.Where(m => m.CpuTempMax is not null).ToList();
        var hot = read.Where(m => m.CpuTempMax >= HotCpu).ToList();
        if (hot.Count == 0) return "";
        var others = read.Where(m => m.CpuTempMax < HotCpu).ToList();
        var runs = Stretches(hot);
        var sb = new StringBuilder("CPU heat in these minutes (the CPU figure in each row is the minute's highest reading): ");
        sb.Append($"{HotCpu:0} °C or more in {hot.Count} of {read.Count} minutes, up to {hot.Max(m => m.CpuTempMax)!.Value:0} °C (");
        sb.Append(string.Join(", ", runs.Take(MostHeatStretches).Select(r => Stretch(r, false))));
        if (runs.Count > MostHeatStretches) sb.Append($" and {runs.Count - MostHeatStretches} more");
        sb.Append($"; longest unbroken stretch {MinutesSaid(runs.Max(r => r.Count))})");
        sb.Append(Quiet(hot));
        sb.Append(others.Count > 0 ? $"; the other minutes were {RangeSaid(others.Select(m => m.CpuTempMax!.Value), "°C")}." : "; that is every minute shown.");
        sb.Append(HeatDetails(hot, others, minutes.Any(m => m.CpuPower is not null)));
        return sb.ToString();
    }

    /// <summary>
    /// get_temperatures' heat line over a period ("" when no minute reached <see cref="HotCpu"/>, or the period is longer
    /// than a month): how many minutes, in how many stretches, the longest with when it was.
    /// </summary>
    private static string CpuHeatInPeriod(RigsightDb db, long from, long to)
    {
        if (to - from > HeatLineMaxSeconds) return "";
        var hot = db.GetHotCpuMinutes(from, to, HotCpu);
        if (hot.Count == 0) return "";
        var runs = Stretches(hot);
        var longest = runs.MaxBy(r => r.Count);
        var sb = new StringBuilder($"CPU at {HotCpu:0} °C or more (a minute's highest reading): {MinutesSaid(hot.Count)} in this period, up to {hot.Max(m => m.CpuTempMax)!.Value:0} °C");
        sb.Append(Quiet(hot));
        sb.Append(runs.Count == 1 ? $"; all in one unbroken stretch ({Stretch(longest, true)})." :
            $"; in {runs.Count} separate stretches, the longest {MinutesSaid(longest.Count)} ({Stretch(longest, true)}).");
        sb.Append(HeatDetails(hot, [], hot.Any(m => m.CpuPower is not null)));
        return sb.ToString();
    }

    /// <summary>How many hot minutes were quiet ones, with the CPU's load in them (", all while under 25% busy (5 to 7%)").</summary>
    private static string Quiet(List<SystemMinute> hot)
    {
        var loads = hot.Where(m => m.CpuLoad is not null).Select(m => m.CpuLoad!.Value).ToList();
        if (loads.Count == 0) return "";
        int quiet = loads.Count(l => l < QuietCpuLoad);
        string busy = $"({RangeSaid(loads, "%")})";
        if (hot.Count == 1) return quiet == 1 ? $", while under {QuietCpuLoad:0}% busy {busy}" : $", while {QuietCpuLoad:0}% busy or more {busy}";
        string share = quiet == hot.Count ? "all" : quiet == 0 ? "none of them" : $"{quiet} of them";
        return $", {share} while under {QuietCpuLoad:0}% busy {busy}";
    }

    /// <summary>The hot minutes' averages over the whole minute, power and clock, and what we can't know (slowing to cool).</summary>
    private static string HeatDetails(List<SystemMinute> hot, List<SystemMinute> others, bool powerRead)
    {
        var sb = new StringBuilder();
        var avgs = hot.Where(m => m.CpuTemp is not null).ToList();
        if (avgs.Count > 0)
        {
            sb.Append(hot.Count == 1 ? $" Its average over the whole minute was {RangeSaid(avgs.Select(m => m.CpuTemp!.Value), "°C")}" :
                $" Averaged over each whole minute, those minutes were {RangeSaid(avgs.Select(m => m.CpuTemp!.Value), "°C")}");
            // An average well under the highest: the reading was up there for only part of the minute.
            sb.Append(avgs.Any(m => m.CpuTempMax - m.CpuTemp >= 5) ? " (an average well under the minute's highest means it was that hot for only part of the minute)." : ".");
        }
        var watts = hot.Where(m => m.CpuPower is not null).Select(m => m.CpuPower!.Value).ToList();
        if (watts.Count > 0)
        {
            var otherWatts = others.Where(m => m.CpuPower is not null).Select(m => m.CpuPower!.Value).ToList();
            sb.Append($" CPU power in those minutes: {RangeSaid(watts, "W")}" + (otherWatts.Count > 0 ? $" (the other minutes {RangeSaid(otherWatts, "W")})." : "."));
        }
        else sb.Append(powerRead ? " We have no CPU power reading in those minutes." : " We have no CPU power reading in these minutes (this PC's CPU may not give one).");
        var clocks = hot.Where(m => m.CpuClock is > 0).Select(m => m.CpuClock!.Value / 1000).ToList();
        if (clocks.Count > 0)
        {
            var otherClocks = others.Where(m => m.CpuClock is > 0).Select(m => m.CpuClock!.Value / 1000).ToList();
            sb.Append($" CPU clock in those minutes (the minute's average): {RangeSaid(clocks, "GHz", "0.0")}" + (otherClocks.Count > 0 ? $" (the other minutes {RangeSaid(otherClocks, "GHz", "0.0")})." : "."));
        }
        sb.Append(" We don't record whether the CPU slowed itself to stay cool, so don't say it did or didn't.");
        return sb.ToString();
    }

    /// <summary>Runs of back-to-back minutes (each a minute after the one before), oldest first.</summary>
    private static List<(long First, long Last, int Count)> Stretches(List<SystemMinute> hot)
    {
        var runs = new List<(long First, long Last, int Count)>();
        foreach (var m in hot.OrderBy(m => m.Ts))
            if (runs.Count > 0 && m.Ts == runs[^1].Last + 60) runs[^1] = (runs[^1].First, m.Ts, runs[^1].Count + 1);
            else runs.Add((m.Ts, m.Ts, 1));
        return runs;
    }

    /// <summary>"4:05 PM", or "4:05 to 4:10 PM" (the minute each starts); with the day: "Tue 18 Nov 4:05 to 4:10 PM".</summary>
    private static string Stretch((long First, long Last, int Count) r, bool day)
    {
        DateTime a = TimeUtil.FromUnix(r.First), b = TimeUtil.FromUnix(r.Last);
        string start = a.ToString(day ? "ddd d MMM h:mm tt" : "h:mm tt", CultureInfo.InvariantCulture);
        if (r.Count == 1) return start;
        string end = b.ToString(b.Date != a.Date ? "ddd d MMM h:mm tt" : "h:mm tt", CultureInfo.InvariantCulture);
        if (a.ToString("tt", CultureInfo.InvariantCulture) == b.ToString("tt", CultureInfo.InvariantCulture) && b.Date == a.Date)
            start = a.ToString(day ? "ddd d MMM h:mm" : "h:mm", CultureInfo.InvariantCulture);
        return $"{start} to {end}";
    }

    private static string MinutesSaid(int n) => n == 1 ? "1 minute" : $"{n} minutes";

    /// <summary>"64 to 76 °C", or "70 °C" when they round to one figure.</summary>
    private static string RangeSaid(IEnumerable<double> values, string unit, string format = "0")
    {
        var list = values.ToList();
        string lo = list.Min().ToString(format, CultureInfo.InvariantCulture), hi = list.Max().ToString(format, CultureInfo.InvariantCulture);
        string sep = unit == "%" ? "" : " ";
        return lo == hi ? $"{lo}{sep}{unit}" : $"{lo} to {hi}{sep}{unit}";
    }
}

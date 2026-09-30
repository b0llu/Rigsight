using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>What a fan's speed goes with, as far as its minutes show.</summary>
public enum FanFollows
{
    /// <summary>Not enough spinning minutes yet to say.</summary>
    Unknown,
    /// <summary>Speeds up and slows down with the GPU's temperature (always so for a graphics card's own fans).</summary>
    Gpu,
    /// <summary>Speeds up and slows down with the CPU's temperature.</summary>
    Cpu,
    /// <summary>Holds much the same speed whatever the temperatures: set to a fixed speed, or a curve that never moves.</summary>
    Steady,
    /// <summary>Changes speed, but not clearly with either chip.</summary>
    Varies,
}

/// <summary>The moment a fan spun fastest, and the app doing the work of the chip it follows then (null when unknown).</summary>
public sealed record FanPeak(int Rpm, long Ts, long? App);

/// <summary>A fan over some minutes (a day): what it follows, how long it stood still, and its fastest moment.</summary>
public sealed record FanFacts(FanFollows Follows, int SpinningMinutes, int StoppedMinutes, FanPeak? Fastest);

/// <summary>
/// Reads a fan's recorded minutes against the PC's. Only what the minutes show: a fan's speed isn't compared across
/// different loads (a fan follows a temperature curve, so faster at a hotter moment says nothing about the fan).
/// </summary>
public static class FanAnalysis
{
    /// <summary>Spinning minutes needed before saying what a fan follows.</summary>
    public const int MinMinutes = 60;

    /// <summary>A fan whose middle 90% of speeds lie within this share of its usual speed (and at least 60 rpm) holds steady.</summary>
    internal const double SteadyShare = 0.05;
    internal const int SteadyMinRpm = 60;

    /// <summary>How closely speed must track a chip's temperature (correlation), and by how much more than the other chip's.</summary>
    internal const double FollowCorrelation = 0.6, FollowMargin = 0.1;

    /// <param name="isGpuFan">A graphics card's own fan: it follows the GPU, nothing to work out.</param>
    /// <param name="fan">The fan's minutes, oldest first.</param>
    /// <param name="minutes">The PC's minutes by their start (temperatures, and the app working each chip).</param>
    public static FanFacts Of(bool isGpuFan, IReadOnlyList<FanMinute> fan, IReadOnlyDictionary<long, SystemMinute> minutes)
    {
        var spinning = fan.Where(f => f.RpmAvg > 0).ToList();
        int stopped = fan.Count(f => f.RpmMax == 0);
        var follows = isGpuFan ? FanFollows.Gpu : FollowsOf(spinning, minutes);

        FanPeak? fastest = null;
        foreach (var f in fan)
            if (f.RpmMax > 0 && (fastest is null || f.RpmMax > fastest.Rpm))
            {
                var m = minutes.GetValueOrDefault(f.Ts);
                fastest = new FanPeak(f.RpmMax, f.Ts, follows switch
                {
                    FanFollows.Gpu => m?.GpuApp,
                    FanFollows.Cpu => m?.CpuApp,
                    _ => null, // a fan that doesn't follow a chip isn't put down to what worked one
                });
            }
        return new FanFacts(follows, spinning.Count, stopped, fastest);
    }

    private static FanFollows FollowsOf(List<FanMinute> spinning, IReadOnlyDictionary<long, SystemMinute> minutes)
    {
        if (spinning.Count < MinMinutes) return FanFollows.Unknown;
        var sorted = spinning.Select(f => f.RpmAvg).Order().ToList();
        int P(double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
        if (P(0.95) - P(0.05) <= Math.Max(SteadyMinRpm, P(0.5) * SteadyShare)) return FanFollows.Steady;

        var rpm = new List<double>();
        var cpu = new List<double>();
        var gpu = new List<double>();
        foreach (var f in spinning)
            if (minutes.GetValueOrDefault(f.Ts) is { CpuTemp: double c, GpuTemp: double g })
            {
                rpm.Add(f.RpmAvg);
                cpu.Add(c);
                gpu.Add(g);
            }
        if (rpm.Count < MinMinutes) return FanFollows.Varies;
        double withCpu = Correlation(rpm, cpu), withGpu = Correlation(rpm, gpu);
        if (withGpu >= FollowCorrelation && withGpu - withCpu >= FollowMargin) return FanFollows.Gpu;
        if (withCpu >= FollowCorrelation && withCpu - withGpu >= FollowMargin) return FanFollows.Cpu;
        return FanFollows.Varies;
    }

    internal static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Count; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db;
            saa += da * da;
            sbb += db * db;
        }
        return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
    }
}

/// <summary>
/// A fan's speed at each temperature of the chip it follows, per app working that chip, under steady heavy load only
/// (past the first minutes of a run: the chip still warming then, the fan still catching up), while turning: what two
/// periods compare to say a fan turns slower at the same heat in the same game. Reports build it from their minutes, and
/// the database keeps it per day (fan_curve_day) so months back can be compared without reading months of minutes.
/// </summary>
public static class FanCurves
{
    /// <summary>Temperatures are kept in steps of this many degrees.</summary>
    public const int BinDegrees = 2;

    /// <summary>The minutes (their starts) of steady heavy load on each chip.</summary>
    public static (HashSet<long> Gpu, HashSet<long> Cpu) Steady(IEnumerable<SystemMinute> minutes)
    {
        var gpu = new HashSet<long>();
        var cpu = new HashSet<long>();
        int gpuRun = 0, cpuRun = 0;
        long previous = 0;
        foreach (var m in minutes)
        {
            bool joined = m.Ts == previous + 60;
            gpuRun = m.GpuLoad >= LoadBands.GpuHeavyLoad ? (joined ? gpuRun : 0) + 1 : 0;
            cpuRun = m.CpuLoad >= LoadBands.CpuHeavyLoad ? (joined ? cpuRun : 0) + 1 : 0;
            if (gpuRun > ReportBuilder.WarmUpMinutes) gpu.Add(m.Ts);
            if (cpuRun > ReportBuilder.WarmUpMinutes) cpu.Add(m.Ts);
            previous = m.Ts;
        }
        return (gpu, cpu);
    }

    /// <summary>The fan's minutes and speed sum at each (app, temperature step); empty for a fan that follows neither chip.</summary>
    public static Dictionary<(long App, int Temp), (int N, double Sum)> Of(FanFollows follows, IEnumerable<FanMinute> fan,
        IReadOnlyDictionary<long, SystemMinute> byTs, (HashSet<long> Gpu, HashSet<long> Cpu) steady)
    {
        var curve = new Dictionary<(long App, int Temp), (int N, double Sum)>();
        if (follows is not (FanFollows.Gpu or FanFollows.Cpu)) return curve;
        bool onGpu = follows == FanFollows.Gpu;
        foreach (var fm in fan)
        {
            if (fm.RpmAvg <= 0 || !byTs.TryGetValue(fm.Ts, out var m)) continue;
            if (!(onGpu ? steady.Gpu : steady.Cpu).Contains(fm.Ts) || (onGpu ? m.GpuTemp : m.CpuTemp) is not double temp
                || (onGpu ? m.GpuApp : m.CpuApp) is not long app) continue;
            var bin = (app, (int)Math.Floor(temp / BinDegrees) * BinDegrees);
            var (n, sum) = curve.GetValueOrDefault(bin);
            curve[bin] = (n + 1, sum + fm.RpmAvg);
        }
        return curve;
    }
}

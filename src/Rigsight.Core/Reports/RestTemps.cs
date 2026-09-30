using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>A chip's temperature at rest today, and its usual range at rest on the days before (null: not known yet).</summary>
public sealed record RestReading(double? Today, int TodayMinutes, double? UsualLow, double? UsualHigh);

/// <summary>
/// Temperatures at rest (someone at the PC, both chips nearly idle, a quarter of an hour after either last worked hard:
/// see heat_day's app 0) today against the same on earlier days. The one like-for-like view of a PC's heat day to day:
/// the load is the same, so what's left is the room and the cooling.
/// </summary>
public static class RestTemps
{
    /// <summary>Earlier days looked at, and how many with a rest reading make a usual range.</summary>
    public const int LookBackDays = 30, MinDays = 5;

    /// <summary>A day's rest counts from this many minutes (a few idle minutes aren't a settled temperature).</summary>
    public const int MinMinutes = 10;

    /// <param name="days">heat_day rows from <see cref="LookBackDays"/> days before <paramref name="today"/> to its end (any apps).</param>
    public static (RestReading Cpu, RestReading Gpu) Of(IEnumerable<HeatDay> days, DateTime today)
    {
        long todayTs = TimeUtil.ToUnix(today.Date);
        var rest = days.Where(d => d.App == 0 && d.N >= MinMinutes).ToList();
        var now = rest.FirstOrDefault(d => d.Day == todayTs);
        var before = rest.Where(d => d.Day < todayTs).ToList();

        RestReading Read(Func<HeatDay, (double Sum, int N)> pick)
        {
            double? todayValue = now is not null && pick(now) is { N: > 0 } t ? t.Sum / t.N : null;
            var earlier = before.Select(pick).Where(p => p.N > 0).Select(p => p.Sum / p.N).Order().ToList();
            if (earlier.Count < MinDays) return new RestReading(todayValue, now?.N ?? 0, null, null);
            // The middle of the earlier days (a fifth off each end): a hot afternoon or a cold morning doesn't set "usual".
            double P(double q) => earlier[Math.Min(earlier.Count - 1, (int)Math.Round(q * (earlier.Count - 1)))];
            return new RestReading(todayValue, now?.N ?? 0, P(0.2), P(0.8));
        }
        return (Read(d => (d.CpuSum, d.CpuN)), Read(d => (d.GpuSum, d.GpuN)));
    }
}

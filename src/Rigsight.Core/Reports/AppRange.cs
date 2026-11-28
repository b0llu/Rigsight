using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>How a custom stretch on the Processes page's opened app is drawn: hour by hour, day by day, or month by month.</summary>
public enum AppRangeStep { Hours, Days, Months }

/// <summary>
/// One bar of a custom stretch: its start (an hour, a day or a month's first day), whether RigSight recorded any of it,
/// the app's CPU time in it (ms), its average memory while it ran (MB; null where none is on record) and how long it ran.
/// </summary>
public sealed record AppRangePoint(DateTime Start, bool Recorded, long CpuMs, double? MemMb, double RunSec)
{
    public bool Ran => RunSec > 0 || CpuMs > 0 || MemMb is not null;
}

/// <summary>A row read from the history for <see cref="AppRange.Bucket"/>: an hour or a day of one app.</summary>
public readonly record struct AppRangeRow(DateTime At, long CpuMs, double? MemMb, double RunSec);

/// <summary>
/// One app's CPU and memory over days picked on the Processes page (1.22.0, Custom on an opened app): any stretch back as
/// far as the history goes (app_cpu_day is kept as long as everything else), drawn by its length (hours up to 2 days,
/// days up to about 3 months, months after that), with its usual taken from the same length of time just before it.
/// </summary>
public sealed class AppRange
{
    /// <summary>Up to this many days the stretch is drawn hour by hour; up to <see cref="DaysUpTo"/> day by day; months past that.</summary>
    public const int HoursUpTo = 2;
    public const int DaysUpTo = 92;

    /// <summary>The first and the last day picked (both in it).</summary>
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public AppRangeStep Step { get; init; }

    /// <summary>Its bars, oldest first: every hour, day or month of the stretch.</summary>
    public IReadOnlyList<AppRangePoint> Points { get; init; } = [];

    /// <summary>Its usual CPU time (ms) in an hour, day or month it ran, over the same length of time before; null with too little of it.</summary>
    public double? CpuUsualMs { get; init; }

    /// <summary>Its usual memory while it ran (MB), over the same length of time before; null with too little of it.</summary>
    public double? MemUsualMb { get; init; }

    /// <summary>Its crashes and hangs in the stretch.</summary>
    public int Crashes { get; init; }

    /// <summary>Its CPU time over the whole stretch (ms), and how long it ran in it (seconds; days only, the hours don't keep it).</summary>
    public long CpuMs => Points.Sum(p => p.CpuMs);
    public double RunSec => Points.Sum(p => p.RunSec);

    /// <summary>Some of the stretch was recorded.</summary>
    public bool AnyRecorded => Points.Any(p => p.Recorded);

    /// <summary>How a stretch from day <paramref name="from"/> to day <paramref name="to"/> (both in it) is drawn, by its length.</summary>
    public static AppRangeStep StepOf(DateTime from, DateTime to)
    {
        int days = Math.Abs((to.Date - from.Date).Days) + 1;
        return days <= HoursUpTo ? AppRangeStep.Hours : days <= DaysUpTo ? AppRangeStep.Days : AppRangeStep.Months;
    }

    /// <summary>The start of the bar a moment falls in.</summary>
    public static DateTime BucketOf(DateTime at, AppRangeStep step) => step switch
    {
        AppRangeStep.Hours => at.Date.AddHours(at.Hour),
        AppRangeStep.Days => at.Date,
        _ => new DateTime(at.Year, at.Month, 1),
    };

    /// <summary>Every bar's start from day <paramref name="from"/> to day <paramref name="to"/> (both in it), oldest first.</summary>
    public static List<DateTime> Starts(DateTime from, DateTime to, AppRangeStep step)
    {
        var list = new List<DateTime>();
        var end = to.Date.AddDays(1);
        for (var t = BucketOf(from.Date, step); t < end; t = step switch { AppRangeStep.Hours => t.AddHours(1), AppRangeStep.Days => t.AddDays(1), _ => t.AddMonths(1) })
            list.Add(t);
        return list;
    }

    /// <summary>
    /// The bars of a stretch from its rows (hours for <see cref="AppRangeStep.Hours"/>, days otherwise): each bar's CPU time
    /// and running time added up, its memory the average while it ran (by running time where it's kept), and recorded where
    /// any of its hours or days is in <paramref name="recorded"/> (RigSight was running then).
    /// </summary>
    public static List<AppRangePoint> Bucket(DateTime from, DateTime to, AppRangeStep step, IEnumerable<AppRangeRow> rows, IEnumerable<DateTime> recorded)
    {
        var starts = Starts(from, to, step);
        var at = new Dictionary<DateTime, int>(starts.Count);
        for (int i = 0; i < starts.Count; i++) at[starts[i]] = i;
        var cpu = new long[starts.Count];
        var run = new double[starts.Count];
        var memSum = new double[starts.Count];
        var memWeight = new double[starts.Count];
        var seen = new bool[starts.Count];
        foreach (var r in rows)
        {
            if (!at.TryGetValue(BucketOf(r.At, step), out int i)) continue;
            cpu[i] += r.CpuMs;
            run[i] += r.RunSec;
            if (r.MemMb is double mb)
            {
                double w = r.RunSec > 0 ? r.RunSec : 1;
                memSum[i] += mb * w;
                memWeight[i] += w;
            }
            seen[i] = true;
        }
        foreach (var t in recorded)
            if (at.TryGetValue(BucketOf(t, step), out int i)) seen[i] = true;
        return [.. starts.Select((s, i) => new AppRangePoint(s, seen[i], cpu[i], memWeight[i] > 0 ? memSum[i] / memWeight[i] : null, run[i]))];
    }

    /// <summary>The fewest bars it ran in, in the time before, for there to be a usual: a few hours or days, two months.</summary>
    public static int MinUsual(AppRangeStep step) => step == AppRangeStep.Months ? 2 : AppPast.MinUsualDays;

    /// <summary>The average of a figure over the bars it ran in; null with fewer than <see cref="MinUsual"/> of them.</summary>
    public static double? Usual(IReadOnlyList<AppRangePoint> before, AppRangeStep step, Func<AppRangePoint, double?> figure)
    {
        var ran = before.Where(p => p.Recorded && p.Ran).Select(figure).OfType<double>().ToList();
        return ran.Count >= MinUsual(step) ? ran.Average() : null;
    }

    /// <summary>The same length of time just before day <paramref name="from"/>: its first and last day.</summary>
    public static (DateTime From, DateTime To) Before(DateTime from, DateTime to)
    {
        int days = (to.Date - from.Date).Days + 1;
        return (from.Date.AddDays(-days), from.Date.AddDays(-1));
    }

    public static AppRange Read(RigsightDb db, string exe, DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        from = from.Date;
        to = to.Date;
        var step = StepOf(from, to);
        long T(DateTime d) => TimeUtil.ToUnix(d);
        int crashes = db.GetCrashes(T(from), T(to.AddDays(1))).Count(c => c.AppExe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        var empty = new AppRange { From = from, To = to, Step = step, Points = Bucket(from, to, step, [], []), Crashes = crashes };
        if (db.LoadApps().FirstOrDefault(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) is not { } app) return empty;

        var (bFrom, bTo) = Before(from, to);
        var points = ReadPoints(db, app.Id, from, to, step);
        var before = ReadPoints(db, app.Id, bFrom, bTo, step);
        return new AppRange
        {
            From = from,
            To = to,
            Step = step,
            Points = points,
            CpuUsualMs = Usual(before, step, p => p.CpuMs),
            MemUsualMb = Usual(before, step, p => p.MemMb),
            Crashes = crashes,
        };
    }

    /// <summary>One app's bars over a stretch, from the hours or the days on record.</summary>
    private static List<AppRangePoint> ReadPoints(RigsightDb db, long app, DateTime from, DateTime to, AppRangeStep step)
    {
        long start = TimeUtil.ToUnix(from), end = TimeUtil.ToUnix(to.AddDays(1));
        if (step == AppRangeStep.Hours)
        {
            // An hour is recorded when every process's total has a row then; its memory from the hours the app was seen in.
            var recorded = db.GetAppCpuHours(AppCpuUse.AllApps, start, end).Select(h => TimeUtil.FromUnix(h.Ts));
            var cpu = db.GetAppCpuHours(app, start, end).ToDictionary(h => h.Ts, h => h.CpuMs);
            var rows = new List<AppRangeRow>();
            foreach (var h in db.GetAppHoursOf(app, start, end))
            {
                rows.Add(new AppRangeRow(TimeUtil.FromUnix(h.Ts), cpu.GetValueOrDefault(h.Ts), h.MemN > 0 ? h.MemSum / h.MemN : null, 0));
                cpu.Remove(h.Ts);
            }
            rows.AddRange(cpu.Select(c => new AppRangeRow(TimeUtil.FromUnix(c.Key), c.Value, null, 0)));
            return Bucket(from, to, step, rows, recorded);
        }
        var days = db.GetAppCpuDays(AppCpuUse.AllApps, start, end).Select(d => TimeUtil.FromUnix(d.Ts));
        var mine = db.GetAppCpuDays(app, start, end).Select(d => new AppRangeRow(TimeUtil.FromUnix(d.Ts), d.CpuMs, d.MemMbAvg, d.RunSec));
        return Bucket(from, to, step, mine, days);
    }
}

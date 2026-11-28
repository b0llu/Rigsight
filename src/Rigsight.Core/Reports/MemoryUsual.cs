using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// An app's usual memory as a spread (1.23.0, issue #18): each day it ran over the <see cref="AppPast.UsualDays"/> days
/// before today gives its average memory over every second it ran that day (app_cpu_day's memory times seconds over its
/// running seconds), and the usual is the middle half of those days, <see cref="Low"/> to <see cref="High"/> (the 25th to
/// the 75th percentile), with <see cref="Middle"/> (the median) where one figure is needed. Needs
/// <see cref="AppPast.MinUsualDays"/> days.
/// </summary>
/// <remarks>
/// Before 1.23.0 the usual was the plain average of the samples app_hour kept, and those were kept only while the app had
/// a window, held 150 MB or used 2% of the CPU, and came every 2 seconds with a window open but every 5 otherwise: an app
/// that mostly sat quiet was judged by its busy moments, and so looked heavier than it is.
/// </remarks>
public sealed record MemoryUsual(double Low, double Middle, double High, int Days)
{
    /// <summary>The spread as the page writes it: "180 to 240 MB", "1.2 to 1.6 GB", "900 MB to 1.1 GB", or one figure where both ends round alike.</summary>
    public string Text => TextOf(Low, High);

    /// <summary>The spread from each day's average (MB); null with fewer than <see cref="AppPast.MinUsualDays"/> days.</summary>
    public static MemoryUsual? Of(IEnumerable<double> dayAverages)
    {
        var v = dayAverages.Where(d => d >= 0 && !double.IsNaN(d)).Order().ToList();
        if (v.Count < AppPast.MinUsualDays) return null;
        return new MemoryUsual(Quantile(v, 0.25), Quantile(v, 0.5), Quantile(v, 0.75), v.Count);
    }

    /// <summary>
    /// Every app's usual by its key, read in one go from the days before <paramref name="now"/>'s day, and the most days
    /// any app ran in them (the page's "Too soon to say" counts those).
    /// </summary>
    public static (Dictionary<long, MemoryUsual> ByApp, int MostDays) ForApps(RigsightDb db, DateTime now)
    {
        var today = now.Date;
        var byApp = new Dictionary<long, MemoryUsual>();
        int most = 0;
        foreach (var g in db.GetAppMemoryDays(TimeUtil.ToUnix(today.AddDays(-AppPast.UsualDays)), TimeUtil.ToUnix(today)).GroupBy(d => d.App))
        {
            var days = g.Where(d => d.MemMbAvg is not null).Select(d => d.MemMbAvg!.Value).ToList();
            most = Math.Max(most, days.Count);
            if (Of(days) is { } usual) byApp[g.Key] = usual;
        }
        return (byApp, most);
    }

    /// <summary>One app's usual (the Processes page's box); null with too few days.</summary>
    public static MemoryUsual? ForApp(RigsightDb db, long app, DateTime now)
    {
        var today = now.Date;
        return Of(db.GetAppMemoryDays(TimeUtil.ToUnix(today.AddDays(-AppPast.UsualDays)), TimeUtil.ToUnix(today), app)
            .Where(d => d.MemMbAvg is not null).Select(d => d.MemMbAvg!.Value));
    }

    /// <summary>"180 to 240 MB", "1.2 to 1.6 GB", "900 MB to 1.1 GB"; one figure ("240 MB") where both ends are written alike.</summary>
    public static string TextOf(double lowMb, double highMb)
    {
        string lo = Units.Megabytes(lowMb), hi = Units.Megabytes(highMb);
        if (lo == hi) return lo;
        bool gb = lowMb >= 1024, sameUnit = gb == highMb >= 1024;
        if (!sameUnit) return Lang.T($"{lo} to {hi}");
        return gb ? Lang.T($"{lowMb / 1024:0.0} to {highMb / 1024:0.0} GB") : Lang.T($"{lowMb:0} to {highMb:0} MB");
    }

    private static double Quantile(List<double> sorted, double p)
    {
        double k = (sorted.Count - 1) * p;
        int f = (int)Math.Floor(k);
        int c = Math.Min(f + 1, sorted.Count - 1);
        return sorted[f] + (sorted[c] - sorted[f]) * (k - f);
    }
}

using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Reports;

public static class ReportBuilder
{
    public static (DateTime From, DateTime To) Bounds(ReportRange range, DateTime anchor)
    {
        var day = anchor.Date;
        return range switch
        {
            ReportRange.Day => (day, day.AddDays(1)),
            ReportRange.Week => WeekOf(day),
            _ => (new DateTime(day.Year, day.Month, 1), new DateTime(day.Year, day.Month, 1).AddMonths(1)),
        };

        static (DateTime, DateTime) WeekOf(DateTime d)
        {
            int offset = ((int)d.DayOfWeek + 6) % 7; // Monday-based weeks
            var start = d.AddDays(-offset);
            return (start, start.AddDays(7));
        }
    }

    public static DateTime Previous(ReportRange range, DateTime anchor) => range switch
    {
        ReportRange.Day => anchor.AddDays(-1),
        ReportRange.Week => anchor.AddDays(-7),
        _ => anchor.AddMonths(-1),
    };

    public static DateTime Next(ReportRange range, DateTime anchor) => range switch
    {
        ReportRange.Day => anchor.AddDays(1),
        ReportRange.Week => anchor.AddDays(7),
        _ => anchor.AddMonths(1),
    };

    /// <summary>Builds the report for the period containing <paramref name="anchor"/>, including insights.</summary>
    public static Report Build(RigsightDb db, ReportRange range, DateTime anchor, RigsightSettings settings)
    {
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var (from, to) = Bounds(range, anchor);
        var report = BuildRaw(db, range, from, to, apps, settings);

        var (pFrom, pTo) = Bounds(range, Previous(range, anchor));
        // Compare against the same elapsed portion of the previous period so "today so far" is fair.
        if (to > DateTime.Now && from <= DateTime.Now)
            pTo = pFrom + (DateTime.Now - from);
        var previous = BuildRaw(db, range, pFrom, pTo, apps, settings);

        var baseline = db.AverageTemps(TimeUtil.ToUnix(from.AddDays(-7)), TimeUtil.ToUnix(from));
        report.Insights = InsightEngine.Generate(report, previous, baseline);
        return report;
    }

    public static Report BuildRaw(RigsightDb db, ReportRange range, DateTime from, DateTime to,
        IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings)
    {
        var report = new Report { Range = range, From = from, To = to };
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);

        var minutes = db.GetMinutes(f, t);
        var hours = db.GetAppHours(f, t);
        var sessions = db.GetSessions(f, t);
        report.Crashes = db.GetCrashes(f, t);
        report.HasData = minutes.Count > 0 || hours.Count > 0;

        string NameOf(long? id)
        {
            if (id is not long i || !apps.TryGetValue(i, out var a)) return "Unknown";
            return settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : a.Name;
        }
        AppCategory CategoryOf(long? id)
        {
            if (id is not long i || !apps.TryGetValue(i, out var a)) return AppCategory.Other;
            return settings.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category;
        }

        // System-wide totals and peaks.
        report.OnSec = minutes.Count * 60;
        report.ActiveSec = minutes.Sum(m => (double)m.ActiveSec);
        report.AwaySec = minutes.Sum(m => (double)m.IdleSec);
        report.CpuTempAvg = Avg(minutes.Select(m => m.CpuTemp));
        report.GpuTempAvg = Avg(minutes.Select(m => m.GpuTemp));
        report.CpuLoadAvg = Avg(minutes.Select(m => m.CpuLoad));
        report.GpuLoadAvg = Avg(minutes.Select(m => m.GpuLoad));

        Peak? PeakOf(Func<SystemMinute, double?> sel)
        {
            SystemMinute? best = null;
            double bestV = double.MinValue;
            foreach (var m in minutes)
                if (sel(m) is double v && v > bestV) { bestV = v; best = m; }
            return best is null ? null : new Peak(bestV, TimeUtil.FromUnix(best.Ts), best.FgApp is null ? null : NameOf(best.FgApp));
        }
        report.CpuTempPeak = PeakOf(m => m.CpuTempMax);
        report.GpuTempPeak = PeakOf(m => m.GpuTempMax);
        report.GpuHotPeak = PeakOf(m => m.GpuHotMax);
        report.CpuVoltPeak = PeakOf(m => m.CpuVoltMax);
        report.GpuVoltPeak = PeakOf(m => m.GpuVoltMax);
        report.CpuPowerPeak = PeakOf(m => m.CpuPower);
        report.GpuPowerPeak = PeakOf(m => m.GpuPower);

        // Per-app statistics.
        var stats = new Dictionary<long, AppStat>();
        var acc = new Dictionary<long, double[]>(); // cpuTempSum, n, gpuTempSum, n, cpuSum, n, memSum, n, gpuLoadSum, n
        foreach (var h in hours)
        {
            if (!stats.TryGetValue(h.AppId, out var s))
            {
                apps.TryGetValue(h.AppId, out var row);
                s = new AppStat
                {
                    Id = h.AppId,
                    Exe = row?.Exe ?? "?",
                    Name = NameOf(h.AppId),
                    Path = row?.Path,
                    Category = CategoryOf(h.AppId),
                };
                stats[h.AppId] = s;
                acc[h.AppId] = new double[10];
            }
            var a = acc[h.AppId];
            s.ActiveSec += h.FgSec;
            s.AwaySec += h.IdleSec;
            s.BackgroundSec += h.BgSec;
            s.MinimizedSec += h.MinSec;
            a[0] += h.CpuTempSum; a[1] += h.CpuTempN;
            a[2] += h.GpuTempSum; a[3] += h.GpuTempN;
            a[4] += h.CpuSum; a[5] += h.CpuN;
            a[6] += h.MemSum; a[7] += h.MemN;
            a[8] += h.GpuLoadSum; a[9] += h.GpuLoadN;
            s.CpuTempMax = Max(s.CpuTempMax, h.CpuTempMax);
            s.GpuTempMax = Max(s.GpuTempMax, h.GpuTempMax);
            s.GpuHotMax = Max(s.GpuHotMax, h.GpuHotMax);
            s.CpuVoltMax = Max(s.CpuVoltMax, h.CpuVoltMax);
            s.GpuVoltMax = Max(s.GpuVoltMax, h.GpuVoltMax);
            s.CpuPowerMax = Max(s.CpuPowerMax, h.CpuPowerMax);
            s.GpuPowerMax = Max(s.GpuPowerMax, h.GpuPowerMax);
            s.CpuMax = Max(s.CpuMax, h.CpuMax);
            s.MemMax = Max(s.MemMax, h.MemMax);
        }
        foreach (var (id, s) in stats)
        {
            var a = acc[id];
            s.CpuTempAvg = a[1] > 0 ? a[0] / a[1] : null;
            s.GpuTempAvg = a[3] > 0 ? a[2] / a[3] : null;
            s.CpuAvg = a[5] > 0 ? a[4] / a[5] : null;
            s.MemAvg = a[7] > 0 ? a[6] / a[7] : null;
            s.GpuLoadAvg = a[9] > 0 ? a[8] / a[9] : null;
        }

        // Sessions.
        foreach (var s in sessions)
        {
            apps.TryGetValue(s.AppId, out var row);
            report.Sessions.Add(new SessionInfo
            {
                AppId = s.AppId,
                Name = NameOf(s.AppId),
                Exe = row?.Exe ?? "?",
                Path = row?.Path,
                Category = CategoryOf(s.AppId),
                Start = TimeUtil.FromUnix(s.Start),
                End = TimeUtil.FromUnix(s.End),
                ActiveSec = s.ActiveSec,
                CpuTempMax = s.CpuTempMax,
                GpuTempMax = s.GpuTempMax,
                IsGame = s.IsGame || CategoryOf(s.AppId) == AppCategory.Game,
            });
            if (stats.TryGetValue(s.AppId, out var st))
            {
                st.SessionCount++;
                st.LongestSessionSec = Math.Max(st.LongestSessionSec, s.ActiveSec);
            }
        }

        report.Apps = [.. stats.Values.OrderByDescending(s => s.ActiveSec).ThenByDescending(s => s.OpenSec)];
        foreach (var s in report.Apps.Where(s => s.ActiveSec > 0))
            report.ActiveByCategory[s.Category] = report.ActiveByCategory.GetValueOrDefault(s.Category) + s.ActiveSec;

        // Minute timeline (merged runs of the same app) and temperature curve.
        TimelineSegment? current = null;
        foreach (var m in minutes)
        {
            var time = TimeUtil.FromUnix(m.Ts);
            report.Temps.Add(new TempPoint(time, m.CpuTemp, m.GpuTemp));

            bool away = m.IdleSec > m.ActiveSec;
            long? app = m.ActiveSec == 0 && m.IdleSec == 0 ? null : m.FgApp;
            bool contiguous = current is not null && (time - current.End).TotalSeconds < 90;
            if (current is not null && contiguous && current.AppId == app && current.Away == away)
            {
                current.End = time.AddMinutes(1);
                continue;
            }
            current = new TimelineSegment
            {
                Start = time,
                End = time.AddMinutes(1),
                AppId = app,
                App = app is null ? null : NameOf(app),
                Category = CategoryOf(app),
                Away = away,
            };
            report.Timeline.Add(current);
        }

        // Daily buckets (used by week and month views).
        if (range != ReportRange.Day)
        {
            for (var d = from.Date; d < to; d = d.AddDays(1))
                report.Days.Add(new DayBucket { Day = d });

            foreach (var g in minutes.GroupBy(m => TimeUtil.FromUnix(m.Ts).Date))
            {
                var bucket = report.Days.FirstOrDefault(b => b.Day == g.Key);
                if (bucket is null) continue;
                bucket.OnSec = g.Count() * 60;
                bucket.ActiveSec = g.Sum(m => (double)m.ActiveSec);
                bucket.CpuTempAvg = Avg(g.Select(m => m.CpuTemp));
                bucket.GpuTempAvg = Avg(g.Select(m => m.GpuTemp));
                bucket.CpuTempMax = g.Max(m => m.CpuTempMax);
                bucket.GpuTempMax = g.Max(m => m.GpuTempMax);
            }
            foreach (var g in hours.GroupBy(h => TimeUtil.FromUnix(h.Ts).Date))
            {
                var bucket = report.Days.FirstOrDefault(b => b.Day == g.Key);
                if (bucket is null) continue;
                foreach (var h in g.Where(h => h.FgSec > 0))
                {
                    var cat = CategoryOf(h.AppId);
                    bucket.ActiveByCategory[cat] = bucket.ActiveByCategory.GetValueOrDefault(cat) + h.FgSec;
                }
                var top = g.GroupBy(h => h.AppId).Select(x => (Id: x.Key, Sec: x.Sum(h => h.FgSec))).MaxBy(x => x.Sec);
                if (top.Sec > 0) bucket.TopApp = NameOf(top.Id);
            }
        }

        return report;
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        double sum = 0;
        int n = 0;
        foreach (var v in values)
            if (v is double d) { sum += d; n++; }
        return n > 0 ? sum / n : null;
    }

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}

using Rigsight.Core.Apps;
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

    /// <summary>Sessions shorter than this (a screenshot, a quick alt-tab) still count as usage but aren't listed as sessions.</summary>
    public const double MinSessionSec = 60;

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

        // The 7 days before this period: what "usual" means (daily averages, temperatures at the same load).
        var usual = BuildRaw(db, ReportRange.Week, from.AddDays(-7), from, apps, settings);
        report.Insights = InsightEngine.Generate(report, previous, usual, settings.Alerts);
        return report;
    }

    /// <param name="withMinutes">False skips the minute-by-minute history (system totals, temperatures, timeline):
    /// the Apps page only needs the hourly per-app totals, which are far cheaper to read over long ranges.</param>
    public static Report BuildRaw(RigsightDb db, ReportRange range, DateTime from, DateTime to,
        IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings, bool withMinutes = true)
    {
        var report = new Report { Range = range, From = from, To = to };
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);

        var minutes = withMinutes ? db.GetMinutes(f, t) : [];
        var hours = db.GetAppHours(f, t);
        var sessions = db.GetSessions(f, t);
        report.Crashes = [.. db.GetCrashes(f, t).Where(c => !settings.IsCrashMuted(c.AppExe))];
        report.HasData = minutes.Count > 0 || hours.Count > 0;

        string NameOf(long? id)
        {
            if (id is not long i || !apps.TryGetValue(i, out var a)) return "Unknown";
            return settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;
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

        // All history is now kept for the same time, but versions before 0.4.13 deleted minute detail after 90 days
        // while keeping the hourly per-app rows for two years. Those rows hold the time in front, time away, and
        // temperature sums and highs, so days without minutes use them rather than showing "0 active".
        var minuteDays = minutes.Select(m => TimeUtil.FromUnix(m.Ts).Date).ToHashSet();
        var older = hours.Where(h => !minuteDays.Contains(TimeUtil.FromUnix(h.Ts).Date)).ToList();
        if (older.Count > 0)
        {
            double oldActive = older.Sum(h => h.FgSec);
            report.ActiveSec += oldActive;
            report.AwaySec += older.Sum(h => h.IdleSec);
            // Without minutes, "on" is the time someone was at it or away from it, hour by hour.
            report.OnSec += older.GroupBy(h => h.Ts).Sum(g => Math.Min(3600, g.Sum(h => h.FgSec + h.IdleSec)));

            // Averages: the recent part weighted by its minutes, the older part by its time in front.
            double? oldCpu = WeightedAvg(older, h => h.CpuTempSum, h => h.CpuTempN);
            double? oldGpu = WeightedAvg(older, h => h.GpuTempSum, h => h.GpuTempN);
            double recentSec = minutes.Count * 60;
            report.CpuTempAvg = Blend(report.CpuTempAvg, recentSec, oldCpu, oldActive);
            report.GpuTempAvg = Blend(report.GpuTempAvg, recentSec, oldGpu, oldActive);

            Peak? HourPeak(Func<AppHour, double?> sel)
            {
                AppHour? best = null;
                double bestV = double.MinValue;
                foreach (var h in older)
                    if (sel(h) is double v && v > bestV) { bestV = v; best = h; }
                return best is null ? null : new Peak(bestV, TimeUtil.FromUnix(best.Ts), NameOf(best.AppId));
            }
            static Peak? Higher(Peak? a, Peak? b) => a is null ? b : b is null ? a : b.Value > a.Value ? b : a;
            report.CpuTempPeak = Higher(report.CpuTempPeak, HourPeak(h => h.CpuTempMax));
            report.GpuTempPeak = Higher(report.GpuTempPeak, HourPeak(h => h.GpuTempMax));
            report.GpuHotPeak = Higher(report.GpuHotPeak, HourPeak(h => h.GpuHotMax));
            report.CpuVoltPeak = Higher(report.CpuVoltPeak, HourPeak(h => h.CpuVoltMax));
            report.GpuVoltPeak = Higher(report.GpuVoltPeak, HourPeak(h => h.GpuVoltMax));
            // Power peaks stay minute-only: they're one-minute averages, and the hourly rows hold instant highs.
        }

        // Per-app statistics.
        var stats = AppStats(hours, apps, NameOf, CategoryOf);

        // Sessions.
        foreach (var s in sessions)
        {
            if (s.ActiveSec < MinSessionSec) continue;
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

        report.GamingSec = stats.Values.Where(a => a.Category == AppCategory.Game).Sum(a => a.ActiveSec);
        AddDayShape(report, minutes, settings.Alerts, NameOf);

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

                // A day older than the minute detail: its totals and temperatures from the hourly rows.
                if (!minuteDays.Contains(g.Key))
                {
                    var list = g.ToList();
                    bucket.ActiveSec = list.Sum(h => h.FgSec);
                    bucket.OnSec = list.GroupBy(h => h.Ts).Sum(x => Math.Min(3600, x.Sum(h => h.FgSec + h.IdleSec)));
                    bucket.CpuTempAvg = WeightedAvg(list, h => h.CpuTempSum, h => h.CpuTempN);
                    bucket.GpuTempAvg = WeightedAvg(list, h => h.GpuTempSum, h => h.GpuTempN);
                    bucket.CpuTempMax = list.Max(h => h.CpuTempMax);
                    bucket.GpuTempMax = list.Max(h => h.GpuTempMax);
                }
            }
        }

        return report;
    }

    /// <summary>Per-app totals from hourly rows (or rows already summed per app, as GetAppTotals returns).</summary>
    private static Dictionary<long, AppStat> AppStats(IEnumerable<AppHour> hours, IReadOnlyDictionary<long, AppRow> apps,
        Func<long?, string> NameOf, Func<long?, AppCategory> CategoryOf)
    {
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
        return stats;
    }

    /// <summary>
    /// What the Apps page needs for a range: per-app totals and session counts, summed by the database. Much
    /// cheaper than <see cref="BuildRaw"/> over long ranges ("All time"), which reads every hourly row and session.
    /// </summary>
    public static Report BuildAppTotals(RigsightDb db, DateTime from, DateTime to, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings)
    {
        var report = new Report { Range = ReportRange.Month, From = from, To = to };
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
        string NameOf(long? id)
        {
            if (id is not long i || !apps.TryGetValue(i, out var a)) return "Unknown";
            return settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;
        }
        AppCategory CategoryOf(long? id)
        {
            if (id is not long i || !apps.TryGetValue(i, out var a)) return AppCategory.Other;
            return settings.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category;
        }

        var totals = db.GetAppTotals(f, t);
        report.HasData = totals.Count > 0;
        var stats = AppStats(totals, apps, NameOf, CategoryOf);
        foreach (var (id, (count, longest)) in db.GetSessionStats(f, t, MinSessionSec))
            if (stats.TryGetValue(id, out var st)) { st.SessionCount = count; st.LongestSessionSec = longest; }
        report.ActiveSec = stats.Values.Sum(a => a.ActiveSec);
        report.GamingSec = stats.Values.Where(a => a.Category == AppCategory.Game).Sum(a => a.ActiveSec);
        report.Apps = [.. stats.Values.OrderByDescending(s => s.ActiveSec).ThenByDescending(s => s.OpenSec)];
        foreach (var s in report.Apps.Where(s => s.ActiveSec > 0))
            report.ActiveByCategory[s.Category] = report.ActiveByCategory.GetValueOrDefault(s.Category) + s.ActiveSec;
        return report;
    }

    // A minute counts as "at the desk" with at least this much active time, and a break is this many minutes away.
    private const int ActiveMinuteSec = 20;
    private const int BreakMinutes = 5;
    private const int LateNightEndsHour = 5;

    /// <summary>When the day started and ended, the longest stretch without a break, and temperatures at like-for-like load.</summary>
    private static void AddDayShape(Report report, List<SystemMinute> minutes, AlertSettings alerts, Func<long?, string> nameOf)
    {
        // Start, end, and the longest stretch without a break (gaps shorter than a break don't end a stretch).
        long runStart = 0, runEnd = 0, bestStart = 0, bestEnd = 0;
        var runApps = new Dictionary<long, int>();
        long? bestApp = null;
        foreach (var m in minutes)
        {
            if (m.ActiveSec < ActiveMinuteSec) continue;
            var time = TimeUtil.FromUnix(m.Ts);
            report.FirstActive ??= time;
            report.LastActive = time.AddMinutes(1);
            // Use in the small hours belongs to the night before, not to "when the day started".
            if (time.Hour < LateNightEndsHour) report.LateUntil = time.AddMinutes(1);
            else report.DayStart ??= time;
            if (runEnd == 0 || m.Ts - runEnd >= BreakMinutes * 60)
            {
                runStart = m.Ts;
                runApps.Clear();
            }
            runEnd = m.Ts + 60;
            if (m.FgApp is long app) runApps[app] = runApps.GetValueOrDefault(app) + 1;
            if (runEnd - runStart > bestEnd - bestStart)
            {
                bestStart = runStart;
                bestEnd = runEnd;
                bestApp = runApps.Count > 0 ? runApps.MaxBy(x => x.Value).Key : null;
            }
        }
        if (bestEnd > bestStart)
            report.LongestStretch = new Stretch(TimeUtil.FromUnix(bestStart), TimeUtil.FromUnix(bestEnd), bestApp is null ? null : nameOf(bestApp));

        // Temperatures compared at the same load, so an idle morning isn't "cooler than usual" just because nothing ran.
        report.IdleTemps = TempsWhere(minutes, m => m.CpuLoad < 15 && m.GpuLoad < 15);
        report.GpuLoadTemps = TempsWhere(minutes, m => m.GpuLoad >= 80);
        report.CpuLoadTemps = TempsWhere(minutes, m => m.CpuLoad >= 50);

        // Hot spot vs core under heavy GPU load: a widening gap is the classic sign of dried-out paste or poor contact.
        var gaps = minutes.Where(m => m.GpuLoad >= 80 && m.GpuHotMax is not null && m.GpuTempMax is not null)
            .Select(m => m.GpuHotMax!.Value - m.GpuTempMax!.Value).ToList();
        if (gaps.Count > 0) report.HotSpotGap = new LoadTemps(null, gaps.Average(), gaps.Count);

        report.CpuOverLimitMin = minutes.Count(m => m.CpuTempMax >= alerts.CpuLimit);
        report.GpuOverLimitMin = minutes.Count(m => m.GpuTempMax >= alerts.GpuLimit);
    }

    private static LoadTemps? TempsWhere(List<SystemMinute> minutes, Func<SystemMinute, bool> match)
    {
        var picked = minutes.Where(match).ToList();
        if (picked.Count == 0) return null;
        return new LoadTemps(Avg(picked.Select(m => m.CpuTemp)), Avg(picked.Select(m => m.GpuTemp)), picked.Count);
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        double sum = 0;
        int n = 0;
        foreach (var v in values)
            if (v is double d) { sum += d; n++; }
        return n > 0 ? sum / n : null;
    }

    private static double? WeightedAvg(IEnumerable<AppHour> hours, Func<AppHour, double> sum, Func<AppHour, int> n)
    {
        double s = 0, c = 0;
        foreach (var h in hours) { s += sum(h); c += n(h); }
        return c > 0 ? s / c : null;
    }

    private static double? Blend(double? a, double weightA, double? b, double weightB) =>
        a is null ? b : b is null ? a : (a * weightA + b * weightB) / (weightA + weightB);

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}

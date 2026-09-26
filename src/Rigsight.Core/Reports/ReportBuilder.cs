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
            ReportRange.Year => (new DateTime(day.Year, 1, 1), new DateTime(day.Year + 1, 1, 1)),
            // Everything: callers show it from the first recorded day.
            ReportRange.All => (new DateTime(2000, 1, 1), DateTime.Today.AddDays(1)),
            // A custom range has its own start and end (see BuildCustom); on its own, its first day.
            ReportRange.Custom => (day, day.AddDays(1)),
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
        ReportRange.Year => anchor.AddYears(-1),
        ReportRange.All => anchor,
        _ => anchor.AddMonths(-1),
    };

    public static DateTime Next(ReportRange range, DateTime anchor) => range switch
    {
        ReportRange.Day => anchor.AddDays(1),
        ReportRange.Week => anchor.AddDays(7),
        ReportRange.Year => anchor.AddYears(1),
        ReportRange.All => anchor,
        _ => anchor.AddMonths(1),
    };

    /// <summary>
    /// The period a report is compared with (none for all time). While the period is in progress, only the same
    /// elapsed portion of the previous one, so "today so far" is fair; long periods compare whole days (their totals
    /// are per day). The portion never runs past the previous period's end (31 March against February).
    /// </summary>
    internal static (DateTime From, DateTime To)? PreviousPeriod(ReportRange range, DateTime anchor, DateTime now)
    {
        if (range == ReportRange.All) return null;
        var (from, to) = Bounds(range, anchor);
        var (pFrom, pTo) = Bounds(range, Previous(range, anchor));
        if (to > now && from <= now)
        {
            var sameElapsed = pFrom + (IsLong(range) ? now.Date.AddDays(1) - from : now - from);
            if (sameElapsed < pTo) pTo = sameElapsed;
        }
        return (pFrom, pTo);
    }

    /// <summary>A year or all time: built from the daily and monthly totals (see <see cref="BuildLong"/>).</summary>
    public static bool IsLong(ReportRange range) => range is ReportRange.Year or ReportRange.All;

    /// <summary>Sessions shorter than this (a screenshot, a quick alt-tab) still count as usage but aren't listed as sessions.</summary>
    public const double MinSessionSec = 60;

    /// <summary>Custom ranges are whole hours: history per app is kept by the hour.</summary>
    public static DateTime HourStart(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);

    /// <summary>The hour a moment falls in, rounded up (1:00 for 12:27, 1:00 for 1:00).</summary>
    public static DateTime HourEnd(DateTime t) => HourStart(t) == t ? t : HourStart(t).AddHours(1);

    /// <summary>Custom ranges up to this long read like a day: minute by minute, not in daily bars.</summary>
    public static readonly TimeSpan DayLikeLimit = TimeSpan.FromHours(48);

    /// <summary>Custom ranges longer than this are built from the monthly totals, like a year.</summary>
    public static readonly TimeSpan LongLimit = TimeSpan.FromDays(92);

    /// <summary>A day, or a custom range of up to two days: shown minute by minute.</summary>
    public static bool IsDayLike(ReportRange range, DateTime from, DateTime to) =>
        range == ReportRange.Day || (range == ReportRange.Custom && to - from <= DayLikeLimit);

    /// <summary>A year, all time, or a custom range of more than three months: bars per month.</summary>
    public static bool IsLong(ReportRange range, DateTime from, DateTime to) =>
        IsLong(range) || (range == ReportRange.Custom && to - from > LongLimit);

    /// <summary>
    /// A custom range (whole hours, see <see cref="HourStart"/>), with insights: compared with the same length of time
    /// just before it (only as much of it as has passed, while it's still going on).
    /// </summary>
    public static Report BuildCustom(RigsightDb db, DateTime from, DateTime to, RigsightSettings settings)
    {
        (from, to) = (HourStart(from), HourEnd(to));
        if (to <= from) to = from.AddHours(1);
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        Report Period(DateTime f, DateTime t) => IsLong(ReportRange.Custom, f, t)
            ? BuildLong(db, ReportRange.Custom, f, t, apps, settings) : BuildRaw(db, ReportRange.Custom, f, t, apps, settings);
        var report = Period(from, to);

        var now = DateTime.Now;
        var (pFrom, pTo) = (from - (to - from), from);
        if (from <= now && now < to) pTo = pFrom + (now - from);
        var previous = Period(pFrom, pTo);

        var usual = BuildRaw(db, ReportRange.Week, from.Date.AddDays(-7), from.Date, apps, settings);
        report.Insights = InsightEngine.Generate(report, previous, usual, settings.Alerts);
        return report;
    }

    /// <summary>
    /// "Your day": all use of <paramref name="day"/> from 5 AM until 5 AM the next morning (use in the small hours belongs
    /// to the night before, as in the "ran late" insight), from its first minute of use to its last. Null without any use.
    /// </summary>
    public static (DateTime From, DateTime To)? YourDay(RigsightDb db, DateTime day)
    {
        day = day.Date;
        var minutes = db.GetMinutes(TimeUtil.ToUnix(day.AddHours(LateNightEndsHour)), TimeUtil.ToUnix(day.AddDays(1).AddHours(LateNightEndsHour)));
        var active = minutes.Where(m => m.ActiveSec >= ActiveMinuteSec).ToList();
        if (active.Count == 0) return null;
        return (TimeUtil.FromUnix(active[0].Ts), TimeUtil.FromUnix(active[^1].Ts).AddMinutes(1));
    }

    /// <summary>
    /// How a report is asked for (the recap notification's link, Home's "Full report"): "yyyy-MM-dd" for a day, or
    /// "yyyy-MM-ddTHH:mm/yyyy-MM-ddTHH:mm" for a range (a day that ran past midnight).
    /// </summary>
    public static string LinkFor(Report report) => report.Range == ReportRange.Custom
        ? $"{report.From.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture)}/{report.To.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
        : report.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads a link made by <see cref="LinkFor"/>: a day, a range, or neither ("yesterday" is a day too).</summary>
    public static (DateTime? Day, (DateTime From, DateTime To)? Range) ReadLink(string? link, DateTime today)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (link == "yesterday") return (today.Date.AddDays(-1), null);
        if (link?.Split('/') is [var a, var b]
            && DateTime.TryParseExact(a, "yyyy-MM-ddTHH:mm", culture, System.Globalization.DateTimeStyles.None, out var from)
            && DateTime.TryParseExact(b, "yyyy-MM-ddTHH:mm", culture, System.Globalization.DateTimeStyles.None, out var to) && to > from)
            return (null, (from, to));
        if (DateTime.TryParseExact(link, "yyyy-MM-dd", culture, System.Globalization.DateTimeStyles.None, out var day)) return (day, null);
        return (null, null);
    }

    /// <summary>Whether a day's use (see <see cref="YourDay"/>) ran past midnight into the next day.</summary>
    public static bool RanPastMidnight((DateTime From, DateTime To) yourDay, DateTime day) => yourDay.To > day.Date.AddDays(1);

    /// <summary>Builds the report for the period containing <paramref name="anchor"/>, including insights.</summary>
    public static Report Build(RigsightDb db, ReportRange range, DateTime anchor, RigsightSettings settings)
    {
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var (from, to) = Bounds(range, anchor);
        Report Period(DateTime f, DateTime t) => IsLong(range) ? BuildLong(db, range, f, t, apps, settings) : BuildRaw(db, range, f, t, apps, settings);
        var report = Period(from, to);

        Report? previous = PreviousPeriod(range, anchor, DateTime.Now) is { } p ? Period(p.From, p.To) : null;

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
        AddHourlyFallback(report, [.. hours.Where(h => !minuteDays.Contains(TimeUtil.FromUnix(h.Ts).Date))], minutes.Count * 60, NameOf);

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

    /// <summary>
    /// Days older than the minute detail (versions before 0.4.13 deleted minutes after 90 days but kept the hourly
    /// per-app rows for two years): their time in front and away, temperatures and highs come from those rows, so
    /// they don't show as "0 active". <paramref name="minuteSec"/> is the time the minutes cover (for blending averages).
    /// </summary>
    private static void AddHourlyFallback(Report report, List<AppHour> older, double minuteSec, Func<long?, string> NameOf)
    {
        if (older.Count == 0) return;
        double oldActive = older.Sum(h => h.FgSec);
        report.ActiveSec += oldActive;
        report.AwaySec += older.Sum(h => h.IdleSec);
        // Without minutes, "on" is the time someone was at it or away from it, hour by hour.
        report.OnSec += older.GroupBy(h => h.Ts).Sum(g => Math.Min(3600, g.Sum(h => h.FgSec + h.IdleSec)));

        // Averages: the recent part weighted by its minutes, the older part by its time in front.
        double? oldCpu = WeightedAvg(older, h => h.CpuTempSum, h => h.CpuTempN);
        double? oldGpu = WeightedAvg(older, h => h.GpuTempSum, h => h.GpuTempN);
        report.CpuTempAvg = Blend(report.CpuTempAvg, minuteSec, oldCpu, oldActive);
        report.GpuTempAvg = Blend(report.GpuTempAvg, minuteSec, oldGpu, oldActive);

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

    /// <summary>
    /// A year (or all time) from the daily totals (system_day), the monthly per-app totals (app_month) and the
    /// longest sessions: a few hundred rows instead of every minute, hour and session. The totals, averages and
    /// highs are the same as <see cref="BuildRaw"/> would give (they add up the same minutes); what needs every
    /// minute (the day's shape, longest stretch, temperatures at like-for-like load) is left out, and the bars are
    /// per month (<see cref="Report.Days"/> holds one bucket per month).
    /// </summary>
    public static Report BuildLong(RigsightDb db, ReportRange range, DateTime from, DateTime to,
        IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings)
    {
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
        var days = db.GetSystemDays(f, t);
        if (days is null) return BuildRaw(db, range, from, to, apps, settings); // an agent from before the daily totals

        var report = new Report { Range = range, From = from, To = to };
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

        // System-wide totals and averages: the days added up.
        int minutes = days.Sum(d => d.Minutes);
        report.OnSec = minutes * 60.0;
        report.ActiveSec = days.Sum(d => d.ActiveSec);
        report.AwaySec = days.Sum(d => d.IdleSec);
        static double? Ratio(double sum, int n) => n > 0 ? sum / n : null;
        report.CpuTempAvg = Ratio(days.Sum(d => d.CpuTempSum), days.Sum(d => d.CpuTempN));
        report.GpuTempAvg = Ratio(days.Sum(d => d.GpuTempSum), days.Sum(d => d.GpuTempN));
        report.CpuLoadAvg = Ratio(days.Sum(d => d.CpuLoadSum), days.Sum(d => d.CpuLoadN));
        report.GpuLoadAvg = Ratio(days.Sum(d => d.GpuLoadSum), days.Sum(d => d.GpuLoadN));

        // Highs: the day with the highest value, then the first minute of that day that reached it (and what was in front).
        Peak? PeakOf(Func<SystemDay, double?> sel, string column)
        {
            SystemDay? best = null;
            double bestV = double.MinValue;
            foreach (var d in days)
                if (sel(d) is double v && v > bestV) { bestV = v; best = d; }
            if (best is null) return null;
            long next = TimeUtil.ToUnix(TimeUtil.FromUnix(best.Day).Date.AddDays(1));
            var hit = db.FindMinute(column, bestV, best.Day, next);
            return new Peak(bestV, TimeUtil.FromUnix(hit?.Ts ?? best.Day), hit?.App is long app ? NameOf(app) : null);
        }
        report.CpuTempPeak = PeakOf(d => d.CpuTempMax, "cpu_temp_max");
        report.GpuTempPeak = PeakOf(d => d.GpuTempMax, "gpu_temp_max");
        report.GpuHotPeak = PeakOf(d => d.GpuHotMax, "gpu_hot_max");
        report.CpuVoltPeak = PeakOf(d => d.CpuVoltMax, "cpu_volt_max");
        report.GpuVoltPeak = PeakOf(d => d.GpuVoltMax, "gpu_volt_max");
        report.CpuPowerPeak = PeakOf(d => d.CpuPowerMax, "cpu_power");
        report.GpuPowerPeak = PeakOf(d => d.GpuPowerMax, "gpu_power");

        // Older history without minutes (only ever at the start, where old versions thinned it out).
        long firstMinuteDay = days.Count > 0 ? days[0].Day : t;
        var older = firstMinuteDay > f ? db.GetAppHours(f, firstMinuteDay) : [];
        AddHourlyFallback(report, older, report.OnSec, NameOf);

        // Apps: per-app totals and session counts, summed by the database (as on the Apps page).
        var totals = db.GetAppTotals(f, t);
        var stats = AppStats(totals, apps, NameOf, CategoryOf);
        foreach (var (id, (count, longest)) in db.GetSessionStats(f, t, MinSessionSec))
            if (stats.TryGetValue(id, out var st)) { st.SessionCount = count; st.LongestSessionSec = longest; }
        report.GamingSec = stats.Values.Where(a => a.Category == AppCategory.Game).Sum(a => a.ActiveSec);
        report.Apps = [.. stats.Values.OrderByDescending(s => s.ActiveSec).ThenByDescending(s => s.OpenSec)];
        foreach (var s in report.Apps.Where(s => s.ActiveSec > 0))
            report.ActiveByCategory[s.Category] = report.ActiveByCategory.GetValueOrDefault(s.Category) + s.ActiveSec;

        // Only the longest sessions: what the page lists (top 10) and what the insights look at (the longest game).
        foreach (var s in db.GetLongestSessions(f, t, MinSessionSec, 200))
        {
            apps.TryGetValue(s.AppId, out var row);
            report.Sessions.Add(new SessionInfo
            {
                AppId = s.AppId, Name = NameOf(s.AppId), Exe = row?.Exe ?? "?", Path = row?.Path, Category = CategoryOf(s.AppId),
                Start = TimeUtil.FromUnix(s.Start), End = TimeUtil.FromUnix(s.End), ActiveSec = s.ActiveSec,
                CpuTempMax = s.CpuTempMax, GpuTempMax = s.GpuTempMax, IsGame = s.IsGame || CategoryOf(s.AppId) == AppCategory.Game,
            });
        }
        report.Crashes = [.. db.GetCrashes(f, t).Where(c => !settings.IsCrashMuted(c.AppExe))];
        report.HasData = days.Count > 0 || totals.Count > 0;

        // One bar per month.
        var firstMonth = new DateTime(from.Year, from.Month, 1);
        if (range == ReportRange.All)
        {
            var first = days.Count > 0 ? TimeUtil.FromUnix(days[0].Day) : to;
            if (older.Count > 0) first = TimeUtil.FromUnix(older.Min(h => h.Ts));
            if (first < to) firstMonth = new DateTime(first.Year, first.Month, 1);
        }
        var buckets = new Dictionary<DateTime, DayBucket>();
        for (var m = firstMonth; m < to; m = m.AddMonths(1))
            report.Days.Add(buckets[m] = new DayBucket { Day = m });
        DayBucket? BucketOf(long unix)
        {
            var d = TimeUtil.FromUnix(unix);
            return buckets.GetValueOrDefault(new DateTime(d.Year, d.Month, 1));
        }
        foreach (var g in days.GroupBy(d => BucketOf(d.Day)))
        {
            if (g.Key is not { } bucket) continue;
            bucket.OnSec = g.Sum(d => d.Minutes) * 60.0;
            bucket.ActiveSec = g.Sum(d => d.ActiveSec);
            bucket.CpuTempAvg = Ratio(g.Sum(d => d.CpuTempSum), g.Sum(d => d.CpuTempN));
            bucket.GpuTempAvg = Ratio(g.Sum(d => d.GpuTempSum), g.Sum(d => d.GpuTempN));
            bucket.CpuTempMax = g.Max(d => d.CpuTempMax);
            bucket.GpuTempMax = g.Max(d => d.GpuTempMax);
        }
        foreach (var g in older.GroupBy(h => BucketOf(h.Ts)))
        {
            if (g.Key is not { } bucket) continue;
            var list = g.ToList();
            bucket.ActiveSec += list.Sum(h => h.FgSec);
            bucket.OnSec += list.GroupBy(h => h.Ts).Sum(x => Math.Min(3600, x.Sum(h => h.FgSec + h.IdleSec)));
            bucket.CpuTempAvg ??= WeightedAvg(list, h => h.CpuTempSum, h => h.CpuTempN);
            bucket.GpuTempAvg ??= WeightedAvg(list, h => h.GpuTempSum, h => h.GpuTempN);
            bucket.CpuTempMax = Max(bucket.CpuTempMax, list.Max(h => h.CpuTempMax));
            bucket.GpuTempMax = Max(bucket.GpuTempMax, list.Max(h => h.GpuTempMax));
        }
        foreach (var g in db.GetAppMonths(f, t).GroupBy(x => BucketOf(x.Month)))
        {
            if (g.Key is not { } bucket) continue;
            foreach (var (_, app, sec) in g)
            {
                var cat = CategoryOf(app);
                bucket.ActiveByCategory[cat] = bucket.ActiveByCategory.GetValueOrDefault(cat) + sec;
            }
            bucket.TopApp = NameOf(g.MaxBy(x => x.FgSec).AppId);
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
            // Use in the small hours belongs to the night before, not to "when the day started" (except in a custom
            // range, which starts when it's asked to: its small hours are the end of its own day).
            if (time.Hour < LateNightEndsHour && report.Range != ReportRange.Custom) report.LateUntil = time.AddMinutes(1);
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

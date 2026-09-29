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
    /// A custom range (whole hours, see <see cref="HourStart"/>), with insights. Up to two days it's compared with the same
    /// hours on the day before (two days before for a range longer than a day, so the two never overlap): an evening
    /// with the evening before, not with the afternoon it followed. Longer: with the same length of time just before it.
    /// Only as much of the earlier stretch as has passed of this one, while it's still going on.
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
        var back = IsDayLike(ReportRange.Custom, from, to) ? TimeSpan.FromDays(Math.Ceiling((to - from).TotalDays)) : to - from;
        var (pFrom, pTo) = (from - back, to - back);
        if (from <= now && now < to) pTo = pFrom + (now - from);
        var previous = Period(pFrom, pTo);

        var usual = BuildRaw(db, ReportRange.Week, from.Date.AddDays(-7), from.Date, apps, settings);
        if (report.Crashes.Count > 0) report.CrashContexts = db.GetCrashContext(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        report.Insights = InsightEngine.Generate(report, previous, usual, settings.Alerts);
        return report;
    }

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
        // And further back: the same weekday over recent weeks, and how the PC ran a few months ago.
        var context = new InsightContext(range == ReportRange.Day ? WeekdayUsualOf(db, from, apps, settings) : null,
            IsLong(range) ? null : ThenHeatOf(db, from, id => NameOf(apps, settings, id)));
        if (range == ReportRange.Day)
        {
            report.Records = RecordsOf(db, report, from, [.. apps.Values.Where(a => CategoryOf(apps, settings, a.Id) == AppCategory.Game).Select(a => a.Id)]);
            report.StreakDays = StreakOf(db, report, from);
        }
        if (report.Crashes.Count > 0) report.CrashContexts = db.GetCrashContext(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        report.Insights = InsightEngine.Generate(report, previous, usual, settings.Alerts, context);
        return report;
    }

    /// <summary>
    /// The same weekday over the four weeks before <paramref name="day"/>: its active time and gaming, averaged over the
    /// ones with any use. Null under three of them (a Saturday isn't a Tuesday, but three make a habit).
    /// </summary>
    internal static WeekdayUsual? WeekdayUsualOf(RigsightDb db, DateTime day, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings)
    {
        var rows = db.GetSystemDays(TimeUtil.ToUnix(day.AddDays(-28)), TimeUtil.ToUnix(day));
        if (rows is null) return null;
        var same = rows.Where(r => r.ActiveSec > 0 && TimeUtil.FromUnix(r.Day).DayOfWeek == day.DayOfWeek).ToList();
        if (same.Count < 3) return null;
        double gaming = 0;
        foreach (var r in same)
            foreach (var h in db.GetAppHours(r.Day, r.Day + 86400))
                if (apps.TryGetValue(h.AppId, out var a) && (settings.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category) == AppCategory.Game)
                    gaming += h.FgSec;
        return new WeekdayUsual(day.DayOfWeek, same.Count, same.Sum(r => r.ActiveSec) / same.Count, gaming / same.Count);
    }

    private static string NameOf(IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings, long? id)
    {
        if (id is not long i || !apps.TryGetValue(i, out var a)) return "Unknown";
        return settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;
    }

    private static AppCategory CategoryOf(IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings, long? id)
    {
        if (id is not long i || !apps.TryGetValue(i, out var a)) return AppCategory.Other;
        return settings.AppCategories.TryGetValue(a.Exe, out var c) ? c : a.Category;
    }

    /// <summary>How long ago "then" is: from 4 months back to 1 month back.</summary>
    private const int ThenFromDays = 120, ThenToDays = 30;

    /// <summary>
    /// How the PC ran a few months ago (the days from <see cref="ThenFromDays"/> to <see cref="ThenToDays"/> back): each
    /// app's steady load and the temperatures at rest, for spotting slow drift. Dust and old paste show over months,
    /// not days. From the daily heat totals (heat_day, added up as the minutes are written, as <see cref="SteadyOf"/>
    /// reads minutes): months of minutes would be too many to read at every refresh. Null when nothing ran steadily then.
    /// </summary>
    internal static ThenHeat? ThenHeatOf(RigsightDb db, DateTime from, Func<long?, string> nameOf)
    {
        var (start, end) = (from.Date.AddDays(-ThenFromDays), from.Date.AddDays(-ThenToDays));
        var days = db.GetHeatDays(TimeUtil.ToUnix(start), TimeUtil.ToUnix(end));
        static double? Ratio(double sum, int n) => n > 0 ? sum / n : null;
        var steady = days.Where(d => d.App != 0).GroupBy(d => d.App)
            .Select(g => (App: g.Key, Rows: g.ToList()))
            .Where(g => g.Rows.Sum(x => x.GpuN) > 0)
            .Select(g => new SteadyLoad(g.App, nameOf(g.App), g.Rows.Sum(x => x.N), g.Rows.Count, g.Rows.Sum(x => x.GpuSum) / g.Rows.Sum(x => x.GpuN),
                Ratio(g.Rows.Sum(x => x.CpuSum), g.Rows.Sum(x => x.CpuN)), Ratio(g.Rows.Sum(x => x.PowerSum), g.Rows.Sum(x => x.PowerN))))
            .OrderByDescending(s => s.Minutes).ToList();
        var rest = days.Where(d => d.App == 0).ToList();
        int restN = rest.Sum(x => x.N);
        var resting = restN > 0 ? new LoadTemps(Ratio(rest.Sum(x => x.CpuSum), rest.Sum(x => x.CpuN)), Ratio(rest.Sum(x => x.GpuSum), rest.Sum(x => x.GpuN)), restN) : null;
        return steady.Count == 0 ? null : new ThenHeat(steady, resting, start, end);
    }

    /// <summary>The windows a record is checked over, and the days of history each needs to mean anything.</summary>
    private static readonly (int Days, int MinDays)[] RecordWindows = [(30, 15), (90, 40), (365, 120)];

    /// <summary>
    /// What this day beat: its longest game session, its peaks and its screen time against every day in the month, three
    /// months and year before it (the widest window it beats, given enough days of history in it). Only what's worth
    /// a record: an hour of a game, a peak of 70° or more, four hours of use.
    /// </summary>
    internal static List<RecordNote> RecordsOf(RigsightDb db, Report report, DateTime day, IReadOnlyCollection<long> games)
    {
        var list = new List<RecordNote>();
        long to = TimeUtil.ToUnix(day);
        var days = db.GetSystemDays(TimeUtil.ToUnix(day.AddDays(-365)), to);
        if (days is null) return list;
        // "In a year" needs about a year of history: not a year's worth of days scattered over five months.
        long first = db.FirstMinuteTime() ?? to;

        void Check(RecordKind kind, double? value, double atLeast, Func<SystemDay, double?> pick, string text, string? app = null, Func<int, double?>? other = null)
        {
            if (value is not double v || v < atLeast) return;
            int best = 0;
            foreach (var (window, minDays) in RecordWindows)
            {
                long start = TimeUtil.ToUnix(day.AddDays(-window));
                var inWindow = days.Where(d => d.Day >= start && d.OnSec() > 0).ToList();
                if (inWindow.Count < minDays || first > TimeUtil.ToUnix(day.AddDays(-window * 0.9))) break;
                double? max = other is not null ? other(window) : inWindow.Max(pick);
                if (max is double m && m >= v) break;
                best = window;
            }
            if (best > 0) list.Add(new RecordNote(kind, text, app, best));
        }

        // A session that began the night before is that day's to count, not this one's.
        var longest = report.Sessions.Where(s => s.IsGame && s.Start >= report.From).MaxBy(s => s.ActiveSec);
        Check(RecordKind.LongestGameSession, longest?.ActiveSec, 3600, _ => null, Units.Duration(longest?.ActiveSec ?? 0), longest?.Name,
            window => db.LongestGameSessionSec(TimeUtil.ToUnix(day.AddDays(-window)), to, games));
        Check(RecordKind.HottestGpu, report.GpuTempPeak?.Value, 70, d => d.GpuTempMax, Units.TempShort(report.GpuTempPeak?.Value ?? 0));
        Check(RecordKind.HottestCpu, report.CpuTempPeak?.Value, 70, d => d.CpuTempMax, Units.TempShort(report.CpuTempPeak?.Value ?? 0));
        Check(RecordKind.MostScreenTime, report.ActiveSec, 4 * 3600, d => d.ActiveSec, Units.Duration(report.ActiveSec));
        return list;
    }

    /// <summary>The least a day must beat its usual by to count towards a streak.</summary>
    private const double StreakMarginSec = 15 * 60;

    /// <summary>
    /// How many days in a row, ending with this one, screen time beat the usual (the average of the 7 days before each,
    /// three or more of them with use). Zero when this day doesn't.
    /// </summary>
    internal static int StreakOf(RigsightDb db, Report report, DateTime day)
    {
        var rows = db.GetSystemDays(TimeUtil.ToUnix(day.AddDays(-40)), TimeUtil.ToUnix(day.AddDays(1)));
        if (rows is null) return 0;
        var active = rows.ToDictionary(r => TimeUtil.FromUnix(r.Day).Date, r => r.ActiveSec);
        active[day.Date] = report.ActiveSec;
        int streak = 0;
        for (var d = day.Date; streak < 30; d = d.AddDays(-1))
        {
            if (!active.TryGetValue(d, out double today) || today <= 0) break;
            var before = Enumerable.Range(1, 7).Select(i => active.GetValueOrDefault(d.AddDays(-i))).Where(a => a > 0).ToList();
            if (before.Count < 3 || today <= before.Average() + StreakMarginSec) break;
            streak++;
        }
        return streak;
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

        // Each high with the app doing the work then (see SystemMinute.CpuApp), not the window in front, which often
        // isn't what made the heat. Voltage highs come at light loads, when the chip boosts: no app is named for them.
        Peak? PeakOf(Func<SystemMinute, double?> sel, Func<SystemMinute, long?>? app = null)
        {
            SystemMinute? best = null;
            double bestV = double.MinValue;
            foreach (var m in minutes)
                if (sel(m) is double v && v > bestV) { bestV = v; best = m; }
            return best is null ? null : new Peak(bestV, TimeUtil.FromUnix(best.Ts), app?.Invoke(best) is long id ? NameOf(id) : null);
        }
        report.CpuTempPeak = PeakOf(m => m.CpuTempMax, m => m.CpuApp);
        report.GpuTempPeak = PeakOf(m => m.GpuTempMax, m => m.GpuApp);
        report.GpuHotPeak = PeakOf(m => m.GpuHotMax, m => m.GpuApp);
        report.CpuVoltPeak = PeakOf(m => m.CpuVoltMax);
        report.GpuVoltPeak = PeakOf(m => m.GpuVoltMax);
        report.CpuPowerPeak = PeakOf(m => m.CpuPower, m => m.CpuApp);
        report.GpuPowerPeak = PeakOf(m => m.GpuPower, m => m.GpuApp);

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
        // A break shorter than the away time isn't seen at all: with a long one, "without a break" can't be told.
        if (settings.Tracking.IdleMinutes > BreakMinutes * 2) report.LongestStretch = null;
        AddHeat(report, minutes, NameOf, CategoryOf);
        (report.Steady, report.RestTemps, report.HotSpotGap) = SteadyOf(minutes, NameOf);
        report.GpuThrottle = ThrottleOf(minutes);
        if (minutes.Count > 0 && to - from <= FanRangeLimit) AddFans(db, report, minutes, f, t);
        // A game being played right now: its session isn't written until it ends.
        if (from <= DateTime.Now && DateTime.Now < to && minutes.Count > 0 && minutes[^1] is { FgApp: long fg, ActiveSec: > 0 } last
            && TimeUtil.NowUnix() - last.Ts <= 180 && CategoryOf(fg) == AppCategory.Game)
            report.GameOngoing = NameOf(fg);

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
            // Hours only know the app in front, not the one doing the work: no app is named.
            return best is null ? null : new Peak(bestV, TimeUtil.FromUnix(best.Ts), null);
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

        // Highs: the day with the highest value, then the first minute of that day that reached it (and what was doing the work).
        Peak? PeakOf(Func<SystemDay, double?> sel, string column, string? appColumn = null)
        {
            SystemDay? best = null;
            double bestV = double.MinValue;
            foreach (var d in days)
                if (sel(d) is double v && v > bestV) { bestV = v; best = d; }
            if (best is null) return null;
            long next = TimeUtil.ToUnix(TimeUtil.FromUnix(best.Day).Date.AddDays(1));
            var hit = db.FindMinute(column, bestV, best.Day, next, appColumn);
            return new Peak(bestV, TimeUtil.FromUnix(hit?.Ts ?? best.Day), hit?.App is long app ? NameOf(app) : null);
        }
        report.CpuTempPeak = PeakOf(d => d.CpuTempMax, "cpu_temp_max", "cpu_app");
        report.GpuTempPeak = PeakOf(d => d.GpuTempMax, "gpu_temp_max", "gpu_app");
        report.GpuHotPeak = PeakOf(d => d.GpuHotMax, "gpu_hot_max", "gpu_app");
        report.CpuVoltPeak = PeakOf(d => d.CpuVoltMax, "cpu_volt_max");
        report.GpuVoltPeak = PeakOf(d => d.GpuVoltMax, "gpu_volt_max");
        report.CpuPowerPeak = PeakOf(d => d.CpuPowerMax, "cpu_power", "cpu_app");
        report.GpuPowerPeak = PeakOf(d => d.GpuPowerMax, "gpu_power", "gpu_app");

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
        // The small hours are the night before running late only when they carry on from it (use in the first half
        // hour after midnight); up at 4:30 after a quiet night is the day starting early.
        if (report.LateUntil is not null && report.FirstActive is { } firstUse && firstUse - report.From >= LateNightCarriesOn)
        {
            report.DayStart = firstUse;
            report.LateUntil = null;
        }

        int awayRun = 0;
        long awayEnd = 0;
        void CloseAway()
        {
            if (awayRun >= Report.LongAwayMinutes) report.LongAwaySec += awayRun * 60;
            awayRun = 0;
        }
        foreach (var m in minutes)
        {
            bool away = m.IdleSec > m.ActiveSec && !(m.CpuLoad >= LoadBands.CpuHeavyLoad) && !(m.GpuLoad >= LoadBands.GpuHeavyLoad);
            if (!away || m.Ts != awayEnd) CloseAway();
            if (!away) continue;
            awayRun++;
            awayEnd = m.Ts + 60;
        }
        CloseAway();

        // By the minute's average: a moment's spike isn't holding a temperature (nor is it for the alert).
        report.CpuTempHeld = minutes.Max(m => m.CpuTemp);
        report.GpuTempHeld = minutes.Max(m => m.GpuTemp);
        report.CpuOverLimitMin = minutes.Count(m => m.CpuTemp >= alerts.CpuLimit);
        report.GpuOverLimitMin = minutes.Count(m => m.GpuTemp >= alerts.GpuLimit);
    }

    private static readonly TimeSpan LateNightCarriesOn = TimeSpan.FromMinutes(30);

    /// <summary>Fan minutes are read for ranges up to this (a week and its "usual"): a month of them per fan is too many.</summary>
    private static readonly TimeSpan FanRangeLimit = TimeSpan.FromDays(8);

    private const int BackgroundMinMinutes = 5;

    /// <summary>
    /// The heat and who made it: minutes over the warm line by the app working the part; heavy work an app did in the
    /// background; clocks running down when hot; how long the GPU took to cool after heavy load.
    /// </summary>
    private static void AddHeat(Report report, List<SystemMinute> minutes, Func<long?, string> nameOf, Func<long?, AppCategory> categoryOf)
    {
        var cpuBy = new Dictionary<long, int>();
        var gpuBy = new Dictionary<long, int>();
        foreach (var m in minutes)
        {
            if (m.CpuTempMax >= Report.HotLine) { report.CpuHotMinutes++; if (m.CpuApp is long a) cpuBy[a] = cpuBy.GetValueOrDefault(a) + 1; }
            if (m.GpuTempMax >= Report.HotLine) { report.GpuHotMinutes++; if (m.GpuApp is long b) gpuBy[b] = gpuBy.GetValueOrDefault(b) + 1; }
        }
        report.CpuHotByApp = [.. cpuBy.OrderByDescending(x => x.Value).Select(x => new HotShare(nameOf(x.Key), x.Value))];
        report.GpuHotByApp = [.. gpuBy.OrderByDescending(x => x.Value).Select(x => new HotShare(nameOf(x.Key), x.Value))];

        // Background work: the longest run of heavy minutes where the app working the part wasn't the one in front
        // (and someone was at the PC to be in front of something).
        BackgroundWork? best = null;
        void Runs(bool gpu, Func<SystemMinute, bool> heavy, Func<SystemMinute, long?> busy)
        {
            long? runApp = null;
            long runStart = 0, runEnd = 0;
            var fronts = new Dictionary<long, int>();
            void Close()
            {
                int length = (int)((runEnd - runStart) / 60);
                if (runApp is long app && length >= BackgroundMinMinutes && (best is null || length > best.Minutes))
                {
                    long front = fronts.MaxBy(x => x.Value).Key;
                    best = new BackgroundWork(nameOf(app), nameOf(front), categoryOf(front), TimeUtil.FromUnix(runStart), length, gpu);
                }
                runApp = null;
                fronts.Clear();
            }
            foreach (var m in minutes)
            {
                bool inRun = heavy(m) && busy(m) is long b && m.FgApp is long f && b != f && m.ActiveSec >= ActiveMinuteSec;
                if (!inRun || busy(m) != runApp || m.Ts != runEnd)
                {
                    if (runApp is not null) Close();
                    if (!inRun) continue;
                    runApp = busy(m);
                    runStart = m.Ts;
                }
                runEnd = m.Ts + 60;
                fronts[m.FgApp!.Value] = fronts.GetValueOrDefault(m.FgApp.Value) + 1;
            }
            if (runApp is not null) Close();
        }
        Runs(false, m => m.CpuLoad >= LoadBands.CpuHeavyLoad, m => m.CpuApp);
        Runs(true, m => m.GpuLoad >= LoadBands.GpuHeavyLoad, m => m.GpuApp);
        report.BackgroundWork = best;
    }

    /// <summary>A run of heavy GPU load is still warming up for this many minutes; after them it's steady.</summary>
    internal const int WarmUpMinutes = 10;

    /// <summary>After heavy work (either chip) ends, this long until the PC counts as at rest again.</summary>
    internal const int RestAfterMinutes = 15;

    private sealed class SteadyAcc
    {
        public int N, CpuN, PowerN;
        public double Gpu, Cpu, Power;
        public readonly HashSet<DateTime> Days = [];
    }

    /// <summary>
    /// Each app's steady heavy GPU load (see <see cref="SteadyLoad"/>), the temperatures at rest (see
    /// <see cref="Report.RestTemps"/>), and the hot spot's gap over the core under steady load.
    /// </summary>
    internal static (List<SteadyLoad> Steady, LoadTemps? Resting, LoadTemps? Gap) SteadyOf(List<SystemMinute> minutes, Func<long?, string> nameOf)
    {
        var byApp = new Dictionary<long, SteadyAcc>();
        long? runApp = null;
        long runEnd = 0, busyUntil = 0;
        int runLength = 0, restN = 0, restCpuN = 0, restGpuN = 0, gapN = 0;
        double restCpu = 0, restGpu = 0, gap = 0;
        foreach (var m in minutes)
        {
            bool gpuHeavy = m.GpuLoad >= LoadBands.GpuHeavyLoad;
            if (gpuHeavy || m.CpuLoad >= LoadBands.CpuHeavyLoad) busyUntil = m.Ts + 60 + RestAfterMinutes * 60;
            if (!gpuHeavy)
            {
                runApp = null;
                if (m.Ts < busyUntil || m.ActiveSec < ActiveMinuteSec || !(m.CpuLoad < LoadBands.IdleMaxLoad && m.GpuLoad < LoadBands.IdleMaxLoad)) continue;
                restN++;
                if (m.CpuTemp is double rc) { restCpu += rc; restCpuN++; }
                if (m.GpuTemp is double rg) { restGpu += rg; restGpuN++; }
                continue;
            }
            long app = m.GpuApp ?? 0;
            if (runApp != app || m.Ts != runEnd) (runApp, runLength) = (app, 0);
            runLength++;
            runEnd = m.Ts + 60;
            if (runLength <= WarmUpMinutes || m.GpuTemp is not double gpu) continue;
            if (m.GpuHotMax is double hot && m.GpuTempMax is double top) { gap += hot - top; gapN++; }
            if (m.GpuApp is not long id) continue;
            if (!byApp.TryGetValue(id, out var a)) byApp[id] = a = new SteadyAcc();
            a.N++;
            a.Gpu += gpu;
            if (m.CpuTemp is double c) { a.Cpu += c; a.CpuN++; }
            if (m.GpuPower is double p) { a.Power += p; a.PowerN++; }
            a.Days.Add(TimeUtil.FromUnix(m.Ts).Date);
        }
        var steady = byApp.Select(x => new SteadyLoad(x.Key, nameOf(x.Key), x.Value.N, x.Value.Days.Count, x.Value.Gpu / x.Value.N,
                x.Value.CpuN > 0 ? x.Value.Cpu / x.Value.CpuN : null, x.Value.PowerN > 0 ? x.Value.Power / x.Value.PowerN : null))
            .OrderByDescending(s => s.Minutes).ToList();
        var rest = restN > 0 ? new LoadTemps(restCpuN > 0 ? restCpu / restCpuN : null, restGpuN > 0 ? restGpu / restGpuN : null, restN) : null;
        return (steady, rest, gapN > 0 ? new LoadTemps(null, gap / gapN, gapN) : null);
    }

    /// <summary>Clocks under this share of their run's cool ones count as running down.</summary>
    private const double ThrottleClockShare = 0.88;
    private const int ThrottleMinReference = 3, ThrottleMinMinutes = 3; // a GPU is warm within minutes of a game starting

    /// <summary>The GPU is at its slow-down point from here (NVIDIA's default target is 83°).</summary>
    private const double ThrottleHot = 83;

    /// <summary>
    /// The GPU slowing itself to stay in bounds: within one run of one app's heavy load, minutes at its slow-down point
    /// with clocks well under the same run's while it was cool, at no more power than then. A different game, a
    /// heavier scene drawing more power, or clocks easing a step or two as it warms (as boost does) aren't that.
    /// </summary>
    private static Throttling? ThrottleOf(List<SystemMinute> minutes)
    {
        int slowed = 0;
        double dropSum = 0, fromTemp = double.MaxValue;
        var run = new List<SystemMinute>();
        void Close()
        {
            var cool = run.Where(m => m.GpuTempMax < ThrottleHot - 5 && m.GpuPower is not null).ToList();
            if (cool.Count >= ThrottleMinReference)
            {
                double clock = Median(cool.Select(m => m.GpuClock!.Value)), power = Median(cool.Select(m => m.GpuPower!.Value));
                foreach (var m in run)
                    if (m.GpuTempMax >= ThrottleHot && m.GpuClock < clock * ThrottleClockShare && m.GpuPower <= power * 1.02)
                    {
                        slowed++;
                        dropSum += 1 - m.GpuClock!.Value / clock;
                        fromTemp = Math.Min(fromTemp, m.GpuTempMax!.Value);
                    }
            }
            run.Clear();
        }
        long? app = null;
        long end = 0;
        foreach (var m in minutes)
        {
            bool heavy = m.GpuLoad >= LoadBands.GpuHeavyLoad && m.GpuClock is > 0 && m.GpuTempMax is not null;
            if (!heavy || m.GpuApp != app || m.Ts != end)
            {
                Close();
                if (!heavy) continue;
                app = m.GpuApp;
            }
            run.Add(m);
            end = m.Ts + 60;
        }
        Close();
        return slowed >= ThrottleMinMinutes ? new Throttling(slowed, dropSum / slowed * 100, fromTemp) : null;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// From here a GPU's fans turn: many stop below about 60° on purpose (and keep turning down to about 50° once
    /// started), so only minutes at this or more can say a GPU fan has stopped when it shouldn't.
    /// </summary>
    internal const double GpuFanSpinTemp = 70;

    /// <summary>
    /// Each fan over the period: the minutes it should have been turning (a GPU fan with the GPU at
    /// <see cref="GpuFanSpinTemp"/> or more; any other fan while the PC was on), how many it read 0 rpm, and the longest
    /// run of those (see <see cref="FanStat"/>). A fan that never should have turned isn't listed.
    /// </summary>
    private static void AddFans(RigsightDb db, Report report, List<SystemMinute> minutes, long from, long to)
    {
        var fans = db.GetFans();
        if (fans.Count == 0) return;
        var byFan = db.GetFanMinutes(from, to).GroupBy(fm => fm.Fan).ToDictionary(g => g.Key, g => g.ToList());
        if (byFan.Count == 0) return;
        var byTs = minutes.ToDictionary(m => m.Ts);
        foreach (var fan in fans)
        {
            if (!byFan.TryGetValue(fan.Id, out var fanMinutes)) continue;
            bool gpu = fan.Sensor.StartsWith("/gpu", StringComparison.Ordinal);
            int spin = 0, stopped = 0, run = 0, longest = 0;
            long runStart = 0, last = 0, bestStart = 0;
            double runTemp = 0, bestTemp = 0;
            foreach (var fm in fanMinutes)
            {
                if (!byTs.TryGetValue(fm.Ts, out var m)) continue;
                double? temp = gpu ? m.GpuTemp : m.CpuTemp;
                if (gpu && !(temp >= GpuFanSpinTemp)) { run = 0; continue; } // cool enough to be still on purpose
                spin++;
                if (fm.RpmMax > 0) { run = 0; continue; }
                stopped++;
                if (run == 0 || fm.Ts != last + 60) (run, runStart, runTemp) = (0, fm.Ts, 0);
                run++;
                last = fm.Ts;
                runTemp = Math.Max(runTemp, temp ?? 0);
                if (run > longest) (longest, bestStart, bestTemp) = (run, runStart, runTemp);
            }
            if (spin > 0)
                report.Fans.Add(new FanStat(fan.Name, fan.Hardware, gpu, spin, stopped, longest,
                    longest > 0 ? TimeUtil.FromUnix(bestStart) : null, longest > 0 && bestTemp > 0 ? bestTemp : null));
        }
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

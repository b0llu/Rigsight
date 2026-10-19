using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Services;

/// <summary>Reads history from the agent's database on a background thread.</summary>
public sealed class ReportService(SettingsModel settings)
{
    private RigsightSettings Snapshot() => settings.Current.Clone();

    /// <summary>
    /// A test copy started with RIGSIGHT_SLOW_HISTORY_MS=1500 takes that much longer over every read of the history.
    /// The end-to-end run opens the app that way to see what the pages show while they wait: on a quick PC the wait
    /// is over before anything can look. Never in an installed Rigsight.
    /// </summary>
    private static readonly int SlowBy = Core.RigsightPaths.IsTestInstance
        && int.TryParse(Environment.GetEnvironmentVariable("RIGSIGHT_SLOW_HISTORY_MS"), out int ms) ? Math.Clamp(ms, 0, 10_000) : 0;

    private static Task<T?> Run<T>(Func<RigsightDb, T> query) => Task.Run(() =>
    {
        try
        {
            if (SlowBy > 0) Thread.Sleep(SlowBy);
            using var db = RigsightDb.OpenReader();
            return db is null ? default : query(db);
        }
        catch (Exception ex)
        {
            Log.Error("reports", ex);
            HistoryRepair.Report(ex);
            return default;
        }
    });

    /// <summary>
    /// Anything read from the history with the settings as they are now, off the window's thread (exports); the
    /// type's default when the history can't be opened.
    /// </summary>
    public Task<T?> QueryAsync<T>(Func<RigsightDb, RigsightSettings, T> query)
    {
        var s = Snapshot();
        return Run(db => query(db, s));
    }

    public Task<Report?> BuildAsync(ReportRange range, DateTime anchor)
    {
        var s = Snapshot();
        return Run(db => Warmed(ReportBuilder.Build(db, range, anchor, s)));
    }

    /// <summary>A custom range's report (whole hours; see <see cref="ReportBuilder.BuildCustom"/>).</summary>
    public Task<Report?> BuildCustomAsync(DateTime from, DateTime to)
    {
        var s = Snapshot();
        return Run(db => Warmed(ReportBuilder.BuildCustom(db, from, to, s)));
    }

    /// <summary>Per-app totals and session counts for a range (Apps page, Memory page, CSV export), summed by the database.</summary>
    public Task<Report?> BuildRangeAsync(DateTime from, DateTime to)
    {
        var s = Snapshot();
        return Run(db => Warmed(ReportBuilder.BuildAppTotals(db, from, to, db.LoadApps().ToDictionary(a => a.Id), s)));
    }

    /// <summary>The report with its apps' icons read already, here where it is built (see <see cref="IconCache.Warm"/>).</summary>
    private static Report Warmed(Report report)
    {
        IconCache.Warm(report.Apps.Select(a => a.Path).Concat(report.Sessions.Select(x => x.Path)));
        return report;
    }

    /// <summary>One app's most recent sessions (a minute or longer) in the range, newest first.</summary>
    public Task<List<SessionInfo>?> RecentSessionsAsync(AppStat app, DateTime from, DateTime to, int count) => Run(db =>
        db.GetRecentSessions(app.Id, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to), ReportBuilder.MinSessionSec, count)
            .Select(x => new SessionInfo
            {
                AppId = app.Id, Name = app.Name, Exe = app.Exe, Path = app.Path, Category = app.Category,
                Start = TimeUtil.FromUnix(x.Start), End = TimeUtil.FromUnix(x.End), ActiveSec = x.ActiveSec,
                CpuTempMax = x.CpuTempMax, GpuTempMax = x.GpuTempMax, IsGame = x.IsGame || app.Category == AppCategory.Game,
            }).ToList());

    /// <summary>What is on record about one running app (the Processes page's box under its row).</summary>
    public Task<AppPast?> AppPastAsync(string exe) => Run(db => AppPast.Read(db, exe, DateTime.Now));

    /// <summary>What is on record about every app's memory (the Memory page's list and its "Worth a look" cards).</summary>
    public Task<MemoryPast?> MemoryPastAsync() => Run(db => MemoryPast.Read(db, DateTime.Now));

    /// <summary>The day Rigsight started recording (null: nothing recorded yet).</summary>
    public Task<DateTime?> FirstDayAsync() => Run(db => db.FirstDataTime() is long f ? TimeUtil.FromUnix(f).Date : (DateTime?)null);

    /// <summary>The last day before <paramref name="before"/> the PC was really used (five minutes or more), if any.</summary>
    public Task<DateTime?> LastUsedDayAsync(DateTime before) =>
        Run(db => db.LastUsedDayBefore(TimeUtil.ToUnix(before.Date)) is long d ? TimeUtil.FromUnix(d).Date : (DateTime?)null);

    /// <summary>The first day with minute-by-minute history (kept for less time than daily totals).</summary>
    public Task<DateTime?> FirstMinuteDayAsync() => Run(db => db.FirstMinuteTime() is long f ? TimeUtil.FromUnix(f).Date : (DateTime?)null);

    /// <summary>
    /// One app's active time across a period, in bars that suit it: per hour for a day, per day for a week or month,
    /// per month for a year or all time (from the monthly totals, so a long period stays cheap).
    /// </summary>
    public Task<List<DayBucket>?> AppChartAsync(long appId, AppCategory category, ReportRange unit, DateTime from, DateTime to, DateTime? firstDay) => Run(db =>
    {
        bool monthly = ReportBuilder.IsLong(unit, from, to);
        bool hourly = ReportBuilder.IsDayLike(unit, from, to);
        if (unit == ReportRange.All) from = firstDay is { } f ? new DateTime(f.Year, f.Month, 1) : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var time = db.GetAppTime(appId, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to), monthly);

        DateTime Slot(long ts)
        {
            var t = TimeUtil.FromUnix(ts);
            return hourly ? t.Date.AddHours(t.Hour) : monthly ? new DateTime(t.Year, t.Month, 1) : t.Date;
        }
        var buckets = new List<DayBucket>();
        var byTime = new Dictionary<DateTime, DayBucket>();
        var first = hourly ? ReportBuilder.HourStart(from) : monthly ? new DateTime(from.Year, from.Month, 1) : from.Date;
        for (var d = first; d < to; d = hourly ? d.AddHours(1) : monthly ? d.AddMonths(1) : d.AddDays(1))
            buckets.Add(byTime[d] = new DayBucket { Day = d });
        foreach (var (ts, sec) in time)
            if (byTime.TryGetValue(Slot(ts), out var bucket))
            {
                bucket.ActiveSec += sec;
                bucket.ActiveByCategory[category] = bucket.ActiveSec;
            }
        return buckets;
    });

    /// <summary>The Network page: the period's internet use (with its insights), and the first day any was recorded.</summary>
    public async Task<(NetReport Report, DateTime? FirstDay)> NetworkAsync(ReportRange range, DateTime anchor)
    {
        var s = Snapshot();
        var result = await Run(db =>
        {
            var apps = db.LoadApps().ToDictionary(a => a.Id);
            var (from, to) = ReportBuilder.Bounds(range, anchor);
            var report = NetReportBuilder.Build(db, range, anchor, apps, s, NetReportBuilder.GamesPlayed(db, from, to, apps, s));
            IconCache.Warm(report.Apps.Select(a => a.Path).Concat(report.Downloads.Select(d => d.Path)));
            return (report, db.FirstNetDay() is long f ? TimeUtil.FromUnix(f).Date : (DateTime?)null);
        });
        // An app given a colour on the chart keeps it: remembered, so the next report hands out the same ones.
        if (result.Item1 is { ColorsSettled: true, ColorExes.Count: > 0 } built && !built.ColorExes.SequenceEqual(settings.Current.NetColorApps, StringComparer.OrdinalIgnoreCase))
            settings.Update(x => x.NetColorApps = [.. built.ColorExes]);
        return result;
    }

    public Task<List<DriveDay>?> DriveHistoryAsync(int days) =>
        Run(db => db.GetDriveDays(TimeUtil.ToUnix(DateTime.Today.AddDays(-days))));

    /// <summary>Minute-by-minute system history for [from, to) (Unix seconds).</summary>
    public Task<List<SystemMinute>?> MinutesAsync(long from, long to) => Run(db => db.GetMinutes(from, to));

    /// <summary>Hour-by-hour (or day-by-day) temperatures for [from, to) (Unix seconds): each one's average, highest and lowest.</summary>
    public Task<List<SystemMinute>?> TempHoursAsync(long from, long to, bool byDay = false) => Run(db => db.GetTempHours(from, to, byDay));

    public Task<List<AppRow>?> KnownAppsAsync() => Run(db => db.LoadApps());

    /// <summary>What the Fans page shows: the fans, their minutes and the PC's from <paramref name="from"/> (today), their
    /// days back to <paramref name="since"/> (to tell a fan that once spun from a header with nothing plugged in), and the apps.</summary>
    public Task<FanHistory?> FanHistoryAsync(DateTime since, DateTime from, DateTime to) => FanHistoryAsync(since, from, to, from, to, from.AddDays(-ViewModels.FansViewModel.UsualDays), from);

    /// <summary>
    /// The same with each part over its own range: minutes (<paramref name="from"/>–<paramref name="to"/>), days
    /// (<paramref name="since"/> or <paramref name="daysFrom"/> to <paramref name="daysTo"/>) and daily curves
    /// (<paramref name="curvesFrom"/>–<paramref name="curvesTo"/>): a year reads only its days and curves, and today's minutes.
    /// </summary>
    public Task<FanHistory?> FanHistoryAsync(DateTime since, DateTime from, DateTime to, DateTime daysFrom, DateTime daysTo, DateTime curvesFrom, DateTime curvesTo) => Run(db =>
    {
        long start = TimeUtil.ToUnix(from), end = Math.Min(TimeUtil.ToUnix(to), TimeUtil.NowUnix() + 60);
        long daysEnd = Math.Min(TimeUtil.ToUnix(daysTo), TimeUtil.NowUnix() + 60);
        return new FanHistory(db.GetFans(), db.GetFanMinutes(start, end), db.GetMinutes(start, end),
            db.GetFanDays(Math.Min(TimeUtil.ToUnix(since), TimeUtil.ToUnix(daysFrom)), daysEnd), db.LoadApps(),
            db.GetFanCurveDays(TimeUtil.ToUnix(curvesFrom), TimeUtil.ToUnix(curvesTo)));
    });

    /// <summary>The first day with fan speeds recorded (the Fans page's picker goes no further back).</summary>
    public Task<DateTime?> FirstFanDayAsync() => Run(db => db.FirstFanDay() is long f ? TimeUtil.FromUnix(f).Date : (DateTime?)null);

    /// <summary>
    /// The seven days ending today hour by hour: each hour's average CPU and GPU temperature with the highest reading,
    /// and the app that was in front most of it (ten minutes at least).
    /// </summary>
    public Task<List<Models.HeatHour>?> HeatWeekAsync(DateTime today)
    {
        var s = Snapshot();
        return Run(db =>
        {
            long from = TimeUtil.ToUnix(today.Date.AddDays(1 - Controls.HeatGrid.Days)), to = TimeUtil.ToUnix(today.Date.AddDays(1));
            var apps = db.LoadApps().ToDictionary(a => a.Id);
            string? Name(long id) => apps.TryGetValue(id, out var a) ? s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : Core.Apps.AppCatalog.KnownName(a.Exe) ?? a.Name : null;
            var front = db.GetAppHours(from, to).GroupBy(h => TimeUtil.LocalHourStart(TimeUtil.FromUnix(h.Ts)))
                .ToDictionary(g => g.Key, g => g.MaxBy(h => h.FgSec) is { FgSec: >= 600 } top ? Name(top.AppId) : null);
            return db.GetTempHours(from, to).Select(m =>
            {
                var hour = TimeUtil.LocalHourStart(TimeUtil.FromUnix(m.Ts));
                return new Models.HeatHour(hour, m.CpuTemp ?? m.CpuTempMax, m.GpuTemp ?? m.GpuTempMax, front.GetValueOrDefault(hour)) { CpuHigh = m.CpuTempMax, GpuHigh = m.GpuTempMax };
            }).ToList();
        });
    }

    /// <summary>Temperatures at rest per day, for today and the days before it (heat_day).</summary>
    public Task<List<HeatDay>?> RestDaysAsync(DateTime today) => Run(db =>
        db.GetHeatDays(TimeUtil.ToUnix(today.AddDays(-Core.Reports.RestTemps.LookBackDays)), TimeUtil.ToUnix(today.AddDays(1))));

    /// <summary>
    /// Crashes in a range, explained, with what was going on just before (temperatures, the app in front, how long it had
    /// been in use) and, for blue screens, the dump file Windows saved. Muted apps are left out unless asked for.
    /// </summary>
    /// <summary>
    /// Whether the agent has read Windows' crash records on this version (see RigsightDb.CrashScannedKey), or there is
    /// nothing for it to have read. Null when the history can't be opened.
    /// </summary>
    public Task<bool?> CrashRecordsReadAsync() =>
        Run(db => (bool?)(db.GetMeta(Core.Data.RigsightDb.CrashScannedKey) is not null || db.FirstCrashTime() is null));

    public Task<List<Models.CrashRow>?> CrashesAsync(DateTime from, DateTime to, bool includeMuted = false)
    {
        var s = Snapshot();
        return Run(db =>
        {
            var apps = db.LoadApps();
            var byExe = apps.ToDictionary(a => a.Exe, StringComparer.OrdinalIgnoreCase);
            var byId = apps.ToDictionary(a => a.Id);
            string NameOf(Core.Data.AppRow a) => s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : Core.Apps.AppCatalog.KnownName(a.Exe) ?? a.Name;

            var events = db.GetCrashes(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to))
                .Where(e => includeMuted || !s.IsCrashMuted(e.AppExe)).ToList();
            // What was going on just before each crash, for all of them in one query.
            var context = db.GetCrashContext(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
            // …and what most likely happened in each, worked out where every page gets it from.
            var inputs = Core.Stability.CrashCases.Inputs(db, events, s);
            var findings = Core.Stability.CrashCases.Findings(inputs);

            // Blue-screen dumps are logged at the next startup: match each to the closest earlier crash with the same code.
            var dumps = events.Any(e => e.Kind == Core.Stability.CrashKind.SystemCrash)
                ? Core.Stability.CrashLogReader.ReadDumps(from.AddDays(-1)) : [];

            return events.Select(e =>
            {
                context.TryGetValue(e.Id, out var ctx);
                string? name = null;
                string? frontApp = null;
                double? sessionSec = null;
                bool isGame = false;
                if (!string.IsNullOrEmpty(e.AppExe))
                {
                    byExe.TryGetValue(e.AppExe, out var row);
                    name = s.AppNames.TryGetValue(e.AppExe, out var alias) ? alias
                        : Core.Apps.AppCatalog.KnownName(e.AppExe)
                        ?? row?.Name
                        ?? (e.AppPath is not null ? Core.Apps.AppCatalog.ResolveName(e.AppExe, e.AppPath) : Core.Apps.AppCatalog.FallbackName(e.AppExe));
                    if (e.AppPath is null && row is not null) e.AppPath = row.Path;
                    if (row is not null)
                    {
                        isGame = (s.AppCategories.TryGetValue(row.Exe, out var c) ? c : row.Category) == AppCategory.Game;
                        // The session this crash ended: written when the app closed, so it ends right around the crash.
                        sessionSec = ctx?.SessionSec;
                    }
                }
                if (ctx?.FrontApp is long front && byId.TryGetValue(front, out var frontRow)) frontApp = NameOf(frontRow);

                string? dump = null;
                if (e.Kind == Core.Stability.CrashKind.SystemCrash && ParseCode(e.Code) is uint code)
                    dump = dumps.Where(d => d.Code == code && d.Logged >= e.Time.AddMinutes(-5) && d.Logged <= e.Time.AddDays(2))
                        .OrderBy(d => d.Logged).Select(d => d.Path).FirstOrDefault();

                var (cpu, gpu) = (ctx?.CpuBefore, ctx?.GpuBefore);
                return new Models.CrashRow
                {
                    Event = e,
                    Explanation = Core.Stability.CrashExplainer.Explain(e, name),
                    AppName = name,
                    CpuBefore = cpu,
                    GpuBefore = gpu,
                    NoReadingsBefore = ctx?.NoReadings == true,
                    FrontApp = frontApp,
                    SessionSec = sessionSec,
                    IsGame = isGame,
                    DumpPath = dump,
                    Finding = findings.GetValueOrDefault(e.Id),
                    Input = inputs.GetValueOrDefault(e.Id),
                };
            }).ToList();
        });

        static uint? ParseCode(string? code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            var hex = code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? code[2..] : code;
            return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
        }
    }

    /// <summary>
    /// A crash opened (see <see cref="Models.CrashCase"/>): its finding, the minutes before it, what was running, what
    /// had changed in the week before, and its earlier times. Null when it has no finding (nothing to open).
    /// </summary>
    /// <param name="of">Which of the row's times to open, for a row that stands for several (its latest unless said).</param>
    public Task<Models.CrashCase?> CaseAsync(Models.CrashGroup group, Models.CrashRow? of = null)
    {
        var s = Snapshot();
        return Run(db =>
        {
            var row = group.IsIncident ? group.Latest : of ?? group.Latest;
            if ((group.IsIncident ? group.Finding : row.Finding) is not { } finding) return null;
            var e = row.Event;
            // Several things failing together: the minutes up to the first of them, and a little after.
            var at = group.IsIncident ? group.First.Time : e.Time;
            long ts = TimeUtil.ToUnix(at);
            var apps = db.LoadApps().ToDictionary(a => a.Id);
            string NameOf(long id) => apps.TryGetValue(id, out var a) ? (s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : Core.Apps.AppCatalog.KnownName(a.Exe) ?? a.Name) : Lang.T("An app");

            var from = at.AddMinutes(-Models.CrashCase.MinutesBefore);
            // And what came after: the minutes recorded since, up to ten.
            var now = DateTime.Now;
            var to = (group.IsIncident ? group.Latest.Time : at).AddMinutes(Models.CrashCase.MinutesAfter);
            if (to > now) to = now < at.AddMinutes(1) ? at.AddMinutes(1) : now;
            // Everything recorded about the PC in those minutes: its lanes, strips and marks, the box beside them, and
            // what stood out (see CaseMinutesBuilder).
            var built = CaseMinutesBuilder.Build(db, s, from, to, at, db.GetSessions(TimeUtil.ToUnix(from), ts + 60),
                group.IsIncident ? Lang.T("crash") : CloseUpBuilder.WordOf(e.Kind), power: !group.IsIncident && Core.Reports.PcUsual.LooksLikePower(e, finding));
            var points = built.Minutes;

            // The stretches with nothing recorded, each with why where Windows' log says (the PC was off, asleep, had
            // gone down): an empty stretch with no word on it reads as Rigsight having missed something.
            var gaps = new List<Models.CaseGap>();
            if (points.Count > 0)
            {
                var power = Core.Stability.CrashLogReader.ReadPower(from.AddHours(-2));
                void Gap(DateTime a, DateTime b)
                {
                    if ((b - a).TotalSeconds <= 90) return;
                    string why = Core.Stability.PowerLog.Words(Core.Stability.PowerLog.Reason(a, b, power, TimeSpan.FromMinutes(2))) ?? Lang.T("Not recorded");
                    gaps.Add(new Models.CaseGap(a, b, why));
                }
                Gap(from, points[0].Time);
                for (int i = 1; i < points.Count; i++) Gap(points[i - 1].Time.AddMinutes(1), points[i].Time);
                // After the last minute recorded, where the PC was off or asleep (an app's crash is followed by more minutes).
                if (points[^1].Time.AddMinutes(1) < at.AddMinutes(1)) Gap(at, to);
                else Gap(points[^1].Time.AddMinutes(1), to);
            }

            // What came after it, in words, among what stood out: the app opened again, or the PC back on.
            var stood = built.StoodOut;
            // The PC going down is marked as what it was, not as "Crash".
            string word = e.Kind switch
            {
                Core.Stability.CrashKind.UnexpectedShutdown => Lang.T("Sudden shut-off"),
                Core.Stability.CrashKind.SystemCrash => Lang.T("Blue screen"),
                Core.Stability.CrashKind.GpuDriverReset => Lang.T("Graphics driver reset"),
                _ => Lang.T("Crash"),
            };
            for (int i = 0; i < built.Marks.Count; i++)
                if (built.Marks[i].IsCrash && !group.IsIncident) built.Marks[i] = built.Marks[i] with { Label = word };
            if (After(e, ts, to, row, db, NameOf) is { } after)
                stood.Insert(stood.FindIndex(x => x.IsCalm) is int calm and >= 0 ? calm : stood.Count, new Core.Stability.StoodOut(after, false));

            // What only an opened crash looks at (its close-up, the graphics card's memory against the app's usual) adds to
            // what the finding rests on; never to what it says, which is the same as on every list.
            if (!group.IsIncident && Core.Stability.CrashCases.Of(db, [e], s, upClose: true).GetValueOrDefault(e.Id) is { } full && full.Head == finding.Head)
                finding = full;
            Models.CaseView? close = null;
            if (!group.IsIncident && e.Kind is Core.Stability.CrashKind.AppCrash or Core.Stability.CrashKind.AppHang or Core.Stability.CrashKind.GpuDriverReset && db.GetCloseUp(e.Ts, e.AppExe) is { } closeUp)
            {
                try { close = CloseUpBuilder.Build(db, s, closeUp, e, now); }
                catch (Exception ex) { Core.Log.Error("crashes", ex); } // never in the way of the crash opening
            }

            // What was open in those minutes: each app's latest stretch in front.
            // A session is an app's stretch of use, from when it was first in front to when it was last: one that
            // spans the moment was open then, the others had been closed by it. What started after it is not part of it.
            var running = db.GetSessions(TimeUtil.ToUnix(from), ts + 60).Where(x => x.Start <= ts).GroupBy(x => x.AppId).Select(g => g.MaxBy(x => x.End)!)
                .OrderByDescending(x => x.End >= ts).ThenByDescending(x => x.End).Take(7)
                .Select(x => new Models.CaseLine(NameOf(x.AppId), null,
                    x.End >= ts ? Lang.T($"open at the time, since {TimeUtil.FromUnix(x.Start):h:mm tt}")
                    : Lang.T($"{TimeUtil.FromUnix(x.Start):h:mm} to {TimeUtil.FromUnix(x.End):h:mm tt}"))).ToList();

            // The week before: what Windows runs on line by line, the apps that came and went as one line.
            var changes = new List<Models.CaseLine>();
            string noChanges = "";
            if (row.Input?.Changes is null) noChanges = Lang.T("No record of changes reaches back that far.");
            else
            {
                var week = db.GetChanges(ts - 7 * 86400, ts + 120);
                foreach (var c in week.Where(c => c.IsSystemLevel).OrderByDescending(c => c.Time).Take(6))
                    changes.Add(new Models.CaseLine(c.Shown, c.Detail, $"{(c.IsApproximate ? "~" : "")}{c.Time:ddd d MMM, h:mm tt}"));
                var others = week.Where(c => c.Kind is Core.Stability.ChangeKind.AppInstalled or Core.Stability.ChangeKind.AppUpdated or Core.Stability.ChangeKind.AppRemoved).ToList();
                if (others.Count > 0)
                {
                    var names = others.Select(c => c.Title.Split(new[] { " updated", " installed", " removed" }, StringSplitOptions.None)[0]).Distinct().ToList();
                    string shown = string.Join(", ", names.Take(4));
                    int more = names.Count - 4;
                    changes.Add(new Models.CaseLine(others.Count == 1 ? others[0].Shown : Lang.T($"{others.Count} apps installed, updated or removed"),
                        others.Count == 1 ? null : names.Count > 4 ? Lang.T($"{shown} and {more} more") : shown,
                        others.Count == 1 ? $"~{others[0].Time:ddd d MMM, h:mm tt}" : Lang.T($"{others.Min(c => c.Time):d MMM} to {others.Max(c => c.Time):d MMM}")));
                }
                if (changes.Count == 0) noChanges = Lang.T("Nothing changed on the PC in the 7 days before.");
            }

            // Earlier times of the same problem.
            var all = (row.Input?.Earlier ?? []).Select(p => p.Time).Append(e.Time).ToList();
            var bars = new List<Models.CaseBar>();
            var times = new List<Models.CaseLine>();
            if (all.Count >= 6 && (all[^1] - all[0]).TotalDays >= 45)
            {
                var first = new DateTime(all[^1].Year, all[^1].Month, 1).AddMonths(-5);
                var months = Enumerable.Range(0, 6).Select(i => first.AddMonths(i)).Where(m => m >= new DateTime(all[0].Year, all[0].Month, 1)).ToList();
                var counts = months.Select(m => all.Count(t => t.Year == m.Year && t.Month == m.Month)).ToList();
                int max = Math.Max(1, counts.Max());
                bars = [.. months.Select((m, i) => new Models.CaseBar(m.ToString("MMM"), counts[i], counts[i] / (double)max))];
            }
            else
                times = [.. all.AsEnumerable().Reverse().Skip(1).Take(5).Select(t => new Models.CaseLine(t.ToString("ddd d MMM yyyy"), null, t.ToString("h:mm tt")))];
            string note = all.Count == 1 ? Lang.T("This is the only time on record.") : Lang.T($"{all.Count} times on record, the first on {all[0]:d MMM yyyy}.");
            if (all.Count >= 8 && all.GroupBy(t => t.Hour).MaxBy(g => g.Count()) is { } hour && hour.Count() * 4 >= all.Count)
                note += Lang.T($" The busiest hour is {new DateTime(2000, 1, 1, hour.Key, 0, 0):h tt}, with {hour.Count()} of them.");
            if (e.Kind is Core.Stability.CrashKind.AppCrash or Core.Stability.CrashKind.AppHang && apps.Values.FirstOrDefault(a => a.Exe.Equals(e.AppExe, StringComparison.OrdinalIgnoreCase)) is { } crashed)
            {
                var sessions = db.GetRecentSessions(crashed.Id, 0, ts + 120, 0, 5000);
                // Sessions are stretches of use, not launches: only the time adds up to something true.
                if (sessions.Count > 0) note += Lang.T($" It has been in use for {Core.Units.Duration(sessions.Sum(x => x.ActiveSec))} in all on this PC.");
            }
            // Several things failing together: what failed, in order; and where one of them was stuck waiting on another
            // program, that program's own crashes on this PC.
            if (group.IsIncident)
            {
                bars = [];
                times = [.. group.Rows.OrderBy(r => r.Time).Select(r => new Models.CaseLine(
                    r.Event.Kind == Core.Stability.CrashKind.GpuDriverReset ? Lang.T("Graphics driver") : r.AppName ?? r.Event.AppExe,
                    r.Event.Facts?.WaitedOn is { } on ? Lang.T($"waiting on {on}") : null, r.Time.ToString("h:mm:ss tt")))];
                note = "";
                if (group.Rows.Select(r => r.Event.Facts?.WaitedOn).FirstOrDefault(w => w is not null) is { } waited)
                {
                    // The program and its helpers ("wallpaper64.exe", "wallpaperservice64.exe") by the start of the name.
                    string stem = System.IO.Path.GetFileNameWithoutExtension(waited).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                    var own = db.GetCrashes(0, long.MaxValue / 2).Where(c => c.AppExe.StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList();
                    string name = row.Input?.NameOf(waited) ?? waited;
                    note = own.Count == 0 ? Lang.T($"{name} has no crashes of its own on record.")
                        : own.Count == 1 ? Lang.T($"{name} has crashed 1 time on this PC, most recently on {own[0].Time:d MMM yyyy}.")
                        : Lang.T($"{name} has crashed {own.Count} times on this PC, most recently on {own[0].Time:d MMM yyyy}.");
                }
            }
            string crashedApp = row.AppName ?? e.AppExe;
            string title = group.IsIncident ? Lang.T("What failed, in order") : e.Kind switch
            {
                Core.Stability.CrashKind.UnexpectedShutdown => bars.Count > 0 ? Lang.T("Sudden shut-offs like this, by month") : Lang.T("Sudden shut-offs like this"),
                Core.Stability.CrashKind.SystemCrash => Lang.T("This blue screen on this PC"),
                Core.Stability.CrashKind.GpuDriverReset => bars.Count > 0 ? Lang.T("Graphics driver resets, by month") : Lang.T("Graphics driver resets"),
                _ => bars.Count > 0 ? Lang.T($"{crashedApp}: this problem, by month") : Lang.T($"{crashedApp} on this PC"),
            };

            return new Models.CrashCase
            {
                Group = group, Row = row, Finding = finding,
                WhenNote = null,
                Points = [.. finding.Points.Select(Models.CaseEvidence.Of)],
                RuledOut = [.. finding.RuledOut.Select(Models.CaseEvidence.Of)],
                NotKnown = [.. finding.NotChecked.Select(Models.CaseEvidence.Of), .. finding.CantTell is { } cant ? new[] { new Models.CaseEvidence("\uE897", "MutedBrush", cant.TrimEnd('.'), "") } : []],
                Wide = new Models.CaseView
                {
                    Minutes = points, Lanes = built.Lanes, Strips = built.Strips, Marks = built.Marks, StoodOut = stood, Readout = built.Readout,
                    Gaps = gaps, ChartFrom = from, ChartTo = to,
                },
                Close = close, UpClose = close is not null,
                NoChart = db.FirstDataTime() is long began && began > ts ? Lang.T($"Nothing was being recorded then. This PC's history starts on {TimeUtil.FromUnix(began):d MMM yyyy}.") : Lang.T("Nothing was recorded in the 45 minutes before."),
                Running = running, Changes = changes, NoChanges = noChanges,
                HistoryTitle = title, Bars = bars, Times = times, HistoryNote = note,
                Tech = TechOf(group, row),
            };
        });

        // What came after: for an app, whether it was opened again in the ten minutes after; for the PC, when it was back on.
        static string? After(Core.Stability.CrashEvent e, long ts, DateTime to, Models.CrashRow row, Core.Data.RigsightDb db, Func<long, string> nameOf)
        {
            if (e.Kind is Core.Stability.CrashKind.AppCrash or Core.Stability.CrashKind.AppHang)
            {
                var app = db.LoadApps().FirstOrDefault(a => a.Exe.Equals(e.AppExe, StringComparison.OrdinalIgnoreCase));
                if (app is null) return null;
                var again = db.GetSessions(ts + 5, ts + Models.CrashCase.MinutesAfter * 60).Where(x => x.AppId == app.Id && x.Start > ts + 5).MinBy(x => x.Start);
                if (again is not null) return Lang.T($"After: {nameOf(app.Id)} was opened again {Core.Stability.CrashFindings.Words(again.Start - ts)} later.");
                return (to - TimeUtil.FromUnix(ts)).TotalMinutes >= Models.CrashCase.MinutesAfter - 0.5
                    ? Lang.T($"After: {nameOf(app.Id)} wasn't opened again in the {Models.CrashCase.MinutesAfter} minutes after.") : null;
            }
            if (e.Kind is Core.Stability.CrashKind.UnexpectedShutdown or Core.Stability.CrashKind.SystemCrash && row.Input?.Context?.BackOn is long on && on > ts && on - ts <= 3 * 3600)
                return Lang.T($"After: back on {Core.Stability.CrashFindings.Words(on - ts)} later.");
            return null;
        }

        static List<Models.CaseLine> TechOf(Models.CrashGroup group, Models.CrashRow shown)
        {
            var list = new List<Models.CaseLine>();
            void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) list.Add(new Models.CaseLine(label, null, value)); }
            foreach (var r in group.IsIncident ? group.Rows.OrderBy(r => r.Time).ToList() : [shown])
            {
                var (e, f) = (r.Event, r.Event.Facts);
                if (group.IsIncident)
                {
                    string head = e.Kind == Core.Stability.CrashKind.GpuDriverReset ? Lang.T("Graphics driver reset") : e.AppExe;
                    string waiting = f?.WaitedOn is { } o ? Lang.T($", waiting on {o} {f.WaitedOnVersion}") : "";
                    Add(r.Time.ToString("h:mm:ss tt"), $"{head}{(f?.AppVersion is { } v ? $" {v}" : "")}{waiting}{(f?.ReportType is { } t ? $" ({t})" : "")}");
                }
                if (group.IsIncident) continue;
                // "0.0.0.0" is a file that carries no version, and "unknown" Windows not knowing which file.
                static string? Version(string? v) => v is null or "0.0.0.0" ? null : v;
                static string VersionAfter(string? v) => Version(v) is { } version ? Lang.T($", version {version}") : "";
                bool unnamed = string.Equals(e.Module, "unknown", StringComparison.OrdinalIgnoreCase);
                Add(Lang.T("Had been running"), f?.RanSec is double ran ? (ran < 90 ? Lang.T($"{ran:0} seconds") : Core.Units.Duration(ran)) : null);
                Add(Lang.T("Program"), string.IsNullOrEmpty(e.AppExe) ? null : $"{e.AppExe}{VersionAfter(f?.AppVersion)}");
                // A reset read from Windows' hang report: the driver and the report's kind say it (below), not a crash's file and code.
                bool hangReport = f?.HangKey is not null;
                if (!hangReport) Add(Lang.T("Crashed in"), e.Module is null ? null : unnamed ? Lang.T("Windows couldn't name the file") : $"{e.Module}{VersionAfter(f?.ModuleVersion)}{(f?.Offset is { } offset ? Lang.T($", at {offset}") : "")}");
                if (!hangReport) Add(e.Kind == Core.Stability.CrashKind.SystemCrash ? Lang.T("Crash code") : Lang.T("Error code"), e.Code);
                Add(Lang.T("Its four numbers"), f?.BugParams is { } p ? string.Join(", ", p) : null);
                Add(Lang.T("Driver named by Windows"), f?.Driver is { } driver ? $"{driver}{(f.DriverName is { } dn ? $" ({dn})" : "")}" : null);
                Add(Lang.T("Windows' report kind"), f?.ReportType);
                Add(Lang.T("Waiting on"), f?.WaitedOn is { } other ? $"{other}{VersionAfter(f.WaitedOnVersion)}" : null);
                Add(Lang.T(".NET error"), f?.Error is { } error ? $"{error}{(f.ErrorAt is { } where ? Lang.T($", in {where}") : "")}" : null);
                Add(Lang.T("Power button held"), f?.PowerButton is bool held ? (held ? Lang.T("Yes") : Lang.T("No")) : null);
                Add(Lang.T("Hardware errors at the next start"), f?.HardwareErrors?.ToString());
                Add(Lang.T("Written up by Windows"), f?.NextStart is long start ? Lang.T($"at the next start, {TimeUtil.FromUnix(start):ddd d MMM, h:mm:ss tt}") : null);
                Add(Lang.T("Time"), r.WindowsTimeNote);
                Add(Lang.T("Dump file"), r.DumpPath);
                // Only where there would have been more: a graphics driver reset's entry is all Windows writes about it.
                if (f is null && e.Kind != Core.Stability.CrashKind.GpuDriverReset) Add(Lang.T("More"), Lang.T("Windows' log no longer holds anything else about this one."));
            }
            return list;
        }
    }

    /// <summary>The first day anything was recorded: tracking, or a crash Windows had logged before Rigsight was installed.</summary>
    public Task<DateTime?> FirstCrashDayAsync() => Run(db =>
    {
        var times = new[] { db.FirstDataTime(), db.FirstCrashTime() }.OfType<long>().ToList();
        return times.Count == 0 ? (DateTime?)null : TimeUtil.FromUnix(times.Min()).Date;
    });

    /// <summary>
    /// What changed under Windows in a range (drivers, updates, hardware, the BIOS): possible causes of crashes that
    /// followed. From the recorded changes; from Windows' own logs until the agent has made its first record.
    /// </summary>
    public static Task<List<Core.Stability.SystemChange>> ChangesAsync(DateTime from, DateTime to) => Task.Run(() =>
    {
        try
        {
            using var db = RigsightDb.OpenReader();
            if (db is { HasChanges: true, ChangesScanned: not null })
                return [.. db.GetChanges(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)).Where(c => c.IsSystemLevel)];
        }
        catch (Exception ex)
        {
            Log.Error("reports", ex);
            HistoryRepair.Report(ex);
        }
        // Windows' own log instead: it can fail too (the event log service stopped), and the page must still finish loading.
        try { return Core.Stability.ChangeLogReader.Read(from, to); }
        catch (Exception ex) { Log.Error("reports", ex); return []; }
    });

    /// <summary>
    /// Everything the Timeline page shows: every change recorded (with the days a drive's space jumped), the problems
    /// a change can explain (blue screens, sudden shut-offs and graphics driver resets: the PC's own, not one app
    /// crashing, which is the Crashes page's), and the PC as it is now.
    /// </summary>
    public Task<TimelineData?> TimelineAsync()
    {
        var s = Snapshot();
        return Run(db =>
        {
            var changes = db.GetChanges(0, long.MaxValue / 2);
            changes.AddRange(Core.Stability.StorageChanges.From(db.GetDriveDays(0)));

            var byExe = db.LoadApps().ToDictionary(a => a.Exe, StringComparer.OrdinalIgnoreCase);
            var crashes = PcProblems(db);
            // Each one as its case says it; one whose cause was most likely outside the PC (a power cut) is told apart.
            var found = Core.Stability.CrashCases.Of(db, crashes, s);
            var problems = crashes.Select(e =>
                {
                    string? name = string.IsNullOrEmpty(e.AppExe) ? null
                        : s.AppNames.TryGetValue(e.AppExe, out var alias) ? alias
                        : Core.Apps.AppCatalog.KnownName(e.AppExe) ?? (byExe.TryGetValue(e.AppExe, out var row) ? row.Name : Core.Apps.AppCatalog.FallbackName(e.AppExe));
                    var finding = found.GetValueOrDefault(e.Id);
                    return new TimelineProblem(e.Time, e.Kind, finding is null ? Core.Stability.CrashExplainer.Explain(e, name).Title : finding.Said) { Outside = finding is { Outside: true } };
                }).ToList();
            var results = Results(db, s, changes, crashes, found);
            return new TimelineData(changes, problems, db.GetInventory()) { Results = results };
        });
    }

    private static List<Core.Stability.CrashEvent> PcProblems(RigsightDb db) => ChangeEffects.PcProblems(db);

    /// <summary>What each day's big changes made better or worse, worked out once (see <see cref="ChangeEffects.Read"/>) and kept for the other pages.</summary>
    private static Dictionary<DateTime, ChangeResult> Results(RigsightDb db, RigsightSettings s, List<Core.Stability.SystemChange>? changes = null,
        List<Core.Stability.CrashEvent>? crashes = null, Dictionary<long, Core.Stability.CrashFinding>? found = null)
    {
        // The drive-space lines aren't big changes: only the recorded ones go in.
        var results = ChangeEffects.Read(db, s, changes?.Where(c => c.Kind != Core.Stability.ChangeKind.Storage).ToList(), crashes, found);
        lock (ResultsLock) _results = (DateTime.Now, results);
        return results;
    }

    private static readonly object ResultsLock = new();
    private static (DateTime At, Dictionary<DateTime, ChangeResult> Of)? _results;

    /// <summary>How long a worked-out set of results is reused: changes are looked for every ten minutes.</summary>
    private static readonly TimeSpan ResultsFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What big changes made better or worse, by day (see <see cref="ChangeEffects"/>): for the pages that show a change
    /// with its result. Reused for a few minutes, since it reads every crash's finding.
    /// </summary>
    public Task<Dictionary<DateTime, ChangeResult>> ChangeResultsAsync()
    {
        lock (ResultsLock)
            if (_results is { } r && DateTime.Now - r.At < ResultsFor && r.At.Date == DateTime.Today) return Task.FromResult(r.Of);
        var s = Snapshot();
        return Run(db => Results(db, s)).ContinueWith(t => t.Result ?? [], TaskScheduler.Default);
    }
}

/// <summary>See <see cref="ReportService.TimelineAsync"/>.</summary>
public sealed record TimelineData(List<Core.Stability.SystemChange> Changes, List<TimelineProblem> Problems, List<Core.Stability.InventoryItem> Inventory)
{
    /// <summary>What a day's big changes made better or worse (see <see cref="ChangeEffects"/>), by day; only days where something did.</summary>
    public Dictionary<DateTime, ChangeResult> Results { get; init; } = [];
}

/// <summary>A crash, freeze or sudden shut-off as one line on the timeline.</summary>
public sealed record TimelineProblem(DateTime Time, Core.Stability.CrashKind Kind, string Title)
{
    /// <summary>Its cause was most likely outside the PC (a power cut): listed, not counted as a fault.</summary>
    public bool Outside { get; init; }

    /// <summary>The PC itself went down (a blue screen, a sudden shut-off), not one app, and not for a reason outside it.</summary>
    public bool IsCritical => !Outside && Kind is Core.Stability.CrashKind.SystemCrash or Core.Stability.CrashKind.UnexpectedShutdown && !Outside;
}

/// <summary>See <see cref="ReportService.FanHistoryAsync"/>.</summary>
public sealed record FanHistory(List<FanRow> Fans, List<FanMinute> Minutes, List<SystemMinute> System, List<FanDay> Days, List<AppRow> Apps,
    List<FanCurveDay> CurveDays);

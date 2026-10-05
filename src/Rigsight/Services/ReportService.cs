using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Services;

/// <summary>Reads history from the agent's database on a background thread.</summary>
public sealed class ReportService(SettingsModel settings)
{
    private RigsightSettings Snapshot() => settings.Current.Clone();

    private static Task<T?> Run<T>(Func<RigsightDb, T> query) => Task.Run(() =>
    {
        try
        {
            using var db = RigsightDb.OpenReader();
            return db is null ? default : query(db);
        }
        catch (Exception ex)
        {
            Log.Error("reports", ex);
            return default;
        }
    });

    public Task<Report?> BuildAsync(ReportRange range, DateTime anchor)
    {
        var s = Snapshot();
        return Run(db => ReportBuilder.Build(db, range, anchor, s));
    }

    /// <summary>A custom range's report (whole hours; see <see cref="ReportBuilder.BuildCustom"/>).</summary>
    public Task<Report?> BuildCustomAsync(DateTime from, DateTime to)
    {
        var s = Snapshot();
        return Run(db => ReportBuilder.BuildCustom(db, from, to, s));
    }

    /// <summary>Per-app totals and session counts for a range (Apps page, Memory page, CSV export), summed by the database.</summary>
    public Task<Report?> BuildRangeAsync(DateTime from, DateTime to)
    {
        var s = Snapshot();
        return Run(db => ReportBuilder.BuildAppTotals(db, from, to, db.LoadApps().ToDictionary(a => a.Id), s));
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

    /// <summary>The day Rigsight started recording (null: nothing recorded yet).</summary>
    public Task<DateTime?> FirstDayAsync() => Run(db => db.FirstDataTime() is long f ? TimeUtil.FromUnix(f).Date : (DateTime?)null);

    /// <summary>The first day with minute-by-minute history (kept for less time than daily totals).</summary>
    public Task<DateTime?> FirstMinuteDayAsync() => Run(db => db.FirstMinuteTime() is long f ? TimeUtil.FromUnix(f).Date : (DateTime?)null);

    /// <summary>Number of days since recording started, today included.</summary>
    public Task<int> TrackedDaysAsync() => Run(db =>
    {
        var first = db.FirstDataTime();
        return first is long f ? (int)(DateTime.Today - TimeUtil.FromUnix(f).Date).TotalDays + 1 : 0;
    });

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
    public Task<(NetReport Report, DateTime? FirstDay)> NetworkAsync(ReportRange range, DateTime anchor)
    {
        var s = Snapshot();
        return Run(db =>
        {
            var apps = db.LoadApps().ToDictionary(a => a.Id);
            var (from, to) = ReportBuilder.Bounds(range, anchor);
            var report = NetReportBuilder.Build(db, range, anchor, apps, s, NetReportBuilder.GamesPlayed(db, from, to, apps, s));
            return (report, db.FirstNetDay() is long f ? TimeUtil.FromUnix(f).Date : (DateTime?)null);
        });
    }

    public Task<List<DriveDay>?> DriveHistoryAsync(int days) =>
        Run(db => db.GetDriveDays(TimeUtil.ToUnix(DateTime.Today.AddDays(-days))));

    /// <summary>Minute-by-minute system history for [from, to) (Unix seconds).</summary>
    public Task<List<SystemMinute>?> MinutesAsync(long from, long to) => Run(db => db.GetMinutes(from, to));

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

    /// <summary>Temperatures at rest per day, for today and the days before it (heat_day).</summary>
    public Task<List<HeatDay>?> RestDaysAsync(DateTime today) => Run(db =>
        db.GetHeatDays(TimeUtil.ToUnix(today.AddDays(-Core.Reports.RestTemps.LookBackDays)), TimeUtil.ToUnix(today.AddDays(1))));

    /// <summary>
    /// Crashes in a range, explained, with what was going on just before (temperatures, the app in front, how long it had
    /// been in use) and, for blue screens, the dump file Windows saved. Muted apps are left out unless asked for.
    /// </summary>
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
                    FrontApp = frontApp,
                    SessionSec = sessionSec,
                    IsGame = isGame,
                    DumpPath = dump,
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
        }
        return Core.Stability.ChangeLogReader.Read(from, to);
    });

    /// <summary>
    /// Everything the Timeline page shows: every change recorded (with the days a drive's space jumped), the problems
    /// a change can explain (blue screens, sudden shutdowns and graphics driver resets: the PC's own, not one app
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
            var problems = db.GetCrashes(0, long.MaxValue / 2)
                .Where(e => e.Kind is Core.Stability.CrashKind.SystemCrash or Core.Stability.CrashKind.GpuDriverReset
                    || (e.Kind == Core.Stability.CrashKind.UnexpectedShutdown && e.Moment == Core.Stability.PowerMoment.Running))
                .Select(e =>
                {
                    string? name = string.IsNullOrEmpty(e.AppExe) ? null
                        : s.AppNames.TryGetValue(e.AppExe, out var alias) ? alias
                        : Core.Apps.AppCatalog.KnownName(e.AppExe) ?? (byExe.TryGetValue(e.AppExe, out var row) ? row.Name : Core.Apps.AppCatalog.FallbackName(e.AppExe));
                    return new TimelineProblem(e.Time, e.Kind, Core.Stability.CrashExplainer.Explain(e, name).Title);
                }).ToList();
            // How the PC ran after each big change against before it (same game, same load; problems per hour of use).
            var names = byExe.Values.ToDictionary(a => a.Id, a => s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : Core.Apps.AppCatalog.KnownName(a.Exe) ?? a.Name);
            var effects = ChangeEffects.Of(changes, db.GetHeatDays(0, long.MaxValue / 2), db.GetSystemDays(0, long.MaxValue / 2) ?? [],
                [.. problems.Select(p => (p.Time, p.Kind))], id => names.GetValueOrDefault(id, "a game"), DateTime.Now);
            return new TimelineData(changes, problems, db.GetInventory()) { Effects = effects };
        });
    }
}

/// <summary>See <see cref="ReportService.TimelineAsync"/>.</summary>
public sealed record TimelineData(List<Core.Stability.SystemChange> Changes, List<TimelineProblem> Problems, List<Core.Stability.InventoryItem> Inventory)
{
    /// <summary>What was different after a day's big changes (see <see cref="ChangeEffects"/>), by day.</summary>
    public Dictionary<DateTime, ChangeEffect> Effects { get; init; } = [];
}

/// <summary>A crash, freeze or sudden shutdown as one line on the timeline.</summary>
public sealed record TimelineProblem(DateTime Time, Core.Stability.CrashKind Kind, string Title)
{
    /// <summary>The PC itself went down (a blue screen, a sudden shutdown), not one app.</summary>
    public bool IsCritical => Kind is Core.Stability.CrashKind.SystemCrash or Core.Stability.CrashKind.UnexpectedShutdown;
}

/// <summary>See <see cref="ReportService.FanHistoryAsync"/>.</summary>
public sealed record FanHistory(List<FanRow> Fans, List<FanMinute> Minutes, List<SystemMinute> System, List<FanDay> Days, List<AppRow> Apps,
    List<FanCurveDay> CurveDays);

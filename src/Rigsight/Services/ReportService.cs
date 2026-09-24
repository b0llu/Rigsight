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
        bool monthly = ReportBuilder.IsLong(unit);
        if (unit == ReportRange.All) from = firstDay is { } f ? new DateTime(f.Year, f.Month, 1) : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var time = db.GetAppTime(appId, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to), monthly);

        DateTime Slot(long ts)
        {
            var t = TimeUtil.FromUnix(ts);
            return unit == ReportRange.Day ? t.Date.AddHours(t.Hour) : monthly ? new DateTime(t.Year, t.Month, 1) : t.Date;
        }
        var buckets = new List<DayBucket>();
        var byTime = new Dictionary<DateTime, DayBucket>();
        for (var d = from; d < to; d = unit == ReportRange.Day ? d.AddHours(1) : monthly ? d.AddMonths(1) : d.AddDays(1))
            buckets.Add(byTime[d] = new DayBucket { Day = d });
        foreach (var (ts, sec) in time)
            if (byTime.TryGetValue(Slot(ts), out var bucket))
            {
                bucket.ActiveSec += sec;
                bucket.ActiveByCategory[category] = bucket.ActiveSec;
            }
        return buckets;
    });

    public Task<List<DriveDay>?> DriveHistoryAsync(int days) =>
        Run(db => db.GetDriveDays(TimeUtil.ToUnix(DateTime.Today.AddDays(-days))));

    /// <summary>Minute-by-minute system history for [from, to) (Unix seconds).</summary>
    public Task<List<SystemMinute>?> MinutesAsync(long from, long to) => Run(db => db.GetMinutes(from, to));

    public Task<List<AppRow>?> KnownAppsAsync() => Run(db => db.LoadApps());

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

    /// <summary>Driver installs and Windows updates in a range (possible causes of crashes that followed).</summary>
    public static Task<List<Core.Stability.SystemChange>> ChangesAsync(DateTime from, DateTime to) =>
        Task.Run(() => Core.Stability.ChangeLogReader.Read(from, to));
}

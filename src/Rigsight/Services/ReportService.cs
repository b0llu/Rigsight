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

    /// <summary>Raw report for an arbitrary range (no insights), used by the Apps page.</summary>
    public Task<Report?> BuildRangeAsync(DateTime from, DateTime to)
    {
        var s = Snapshot();
        return Run(db => ReportBuilder.BuildRaw(db, ReportRange.Month, from, to, db.LoadApps().ToDictionary(a => a.Id), s));
    }

    /// <summary>Number of distinct days with any recorded activity.</summary>
    public Task<int> TrackedDaysAsync() => Run(db =>
    {
        var first = db.FirstDataTime();
        return first is long f ? (int)(DateTime.Today - TimeUtil.FromUnix(f).Date).TotalDays + 1 : 0;
    });

    /// <summary>Active time per day for one app over the last <paramref name="days"/> days.</summary>
    public Task<List<DayBucket>?> AppDailyAsync(long appId, AppCategory category, int days) => Run(db =>
    {
        var from = DateTime.Today.AddDays(-(days - 1));
        var hours = db.GetAppHours(TimeUtil.ToUnix(from), TimeUtil.ToUnix(DateTime.Today.AddDays(1)))
            .Where(h => h.AppId == appId).ToList();
        var list = new List<DayBucket>();
        for (var d = from; d <= DateTime.Today; d = d.AddDays(1))
        {
            var bucket = new DayBucket { Day = d };
            var sec = hours.Where(h => TimeUtil.FromUnix(h.Ts).Date == d).Sum(h => h.FgSec);
            bucket.ActiveSec = sec;
            if (sec > 0) bucket.ActiveByCategory[category] = sec;
            list.Add(bucket);
        }
        return list;
    });

    public Task<List<DriveDay>?> DriveHistoryAsync(int days) =>
        Run(db => db.GetDriveDays(TimeUtil.ToUnix(DateTime.Today.AddDays(-days))));

    /// <summary>Minute-by-minute system history for [from, to) (Unix seconds).</summary>
    public Task<List<SystemMinute>?> MinutesAsync(long from, long to) => Run(db => db.GetMinutes(from, to));

    public Task<List<AppRow>?> KnownAppsAsync() => Run(db => db.LoadApps());

    /// <summary>Crashes in a range, explained, with the temperatures just before each one.</summary>
    public Task<List<Models.CrashRow>?> CrashesAsync(DateTime from, DateTime to)
    {
        var s = Snapshot();
        return Run(db =>
        {
            var apps = db.LoadApps().ToDictionary(a => a.Exe, StringComparer.OrdinalIgnoreCase);
            return db.GetCrashes(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)).Select(e =>
            {
                string? name = null;
                if (!string.IsNullOrEmpty(e.AppExe))
                {
                    name = s.AppNames.TryGetValue(e.AppExe, out var alias) ? alias
                        : apps.TryGetValue(e.AppExe, out var row) ? row.Name
                        : Core.Apps.AppCatalog.FallbackName(e.AppExe);
                    if (e.AppPath is null && apps.TryGetValue(e.AppExe, out var r2)) e.AppPath = r2.Path;
                }
                var (cpu, gpu) = db.PeakTempsBefore(e.Ts, 5);
                return new Models.CrashRow
                {
                    Event = e,
                    Explanation = Core.Stability.CrashExplainer.Explain(e, name),
                    AppName = name,
                    CpuBefore = cpu,
                    GpuBefore = gpu,
                };
            }).ToList();
        });
    }
}

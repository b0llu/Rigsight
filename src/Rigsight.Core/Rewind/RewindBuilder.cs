using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Rewind;

/// <summary>
/// Reads a rewind's facts from history. Every total comes from the same report the Reports page shows for that
/// stretch of days, so a rewind never says a number the app says differently elsewhere.
/// </summary>
public static class RewindBuilder
{
    /// <summary>Fewer days of use than this in a month: no rewind of its own (its days still count in the longer ones).</summary>
    public const int ThinDays = 7;

    /// <summary>Fewer than this many days of use in a period: nothing worth a rewind.</summary>
    public static int EnoughDays(RewindKind kind) => kind switch
    {
        RewindKind.Month => ThinDays,
        RewindKind.Quarter => 21,
        RewindKind.Half => 42,
        _ => 84,
    };

    /// <summary>When in the day: each sitting's time in front spread over the hours it ran through.</summary>
    private static void Spread(IEnumerable<SessionRow> sessions, double[] hours)
    {
        foreach (var s in sessions)
        {
            long at = s.Start; double left = s.ActiveSec;
            while (left > 0)
            {
                var local = TimeUtil.FromUnix(at);
                double step = Math.Min(left, 3600 - (local.Minute * 60 + local.Second));
                hours[local.Hour] += step / 3600;
                left -= step; at += (long)Math.Ceiling(step);
            }
        }
    }

    public static string IdOf(RewindKind kind, DateTime from) => $"{from:yyyy-MM}-{kind.ToString().ToLowerInvariant()}";

    /// <summary>The period's facts with today's settings (what goes in, what is left out).</summary>
    public static RewindFacts Build(RigsightDb db, RewindKind kind, DateTime from, DateTime to, RigsightSettings settings, DateTime now)
    {
        var r = settings.Rewind;
        var report = ReportBuilder.BuildCustom(db, from, to, settings);
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var f = new RewindFacts
        {
            Id = IdOf(kind, from), Kind = kind, From = from, To = to, Made = now,
            Time = r.Time, Games = r.Games, Internet = r.Internet, Heat = r.Heat, Changes = r.Changes, Problems = r.Problems,
            OnSec = report.OnSec, ActiveSec = report.ActiveSec,
            Days = (int)Math.Round((to - from).TotalDays),
        };

        // Each day from the daily totals: over three months the report adds history up by month, not by day.
        int dayCount = (int)Math.Round((to - from).TotalDays);
        var dayUse = db.GetDayUse(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)).ToDictionary(d => TimeUtil.FromUnix(d.Day).Date);
        f.DayHours = [.. Enumerable.Range(0, dayCount).Select(k => dayUse.TryGetValue(from.AddDays(k).Date, out var d) ? d.ActiveSec / 3600 : 0)];
        var used = dayUse.Values.Where(d => d.OnSec > 0).ToList();
        f.DaysOn = used.Count;
        if (used.Count > 0) { var busiest = used.MaxBy(d => d.OnSec); (f.BusiestDay, f.BusiestDaySec) = (TimeUtil.FromUnix(busiest.Day).Date, busiest.OnSec); }

        // Games: the report's own game time; each game by its time in front, the left-out ones counted in the total only.
        f.GamingSec = report.GamingSec;
        var games = report.Apps.Where(a => a.Category == AppCategory.Game && a.ActiveSec >= 60).ToList();
        f.GameCount = games.Count;
        var allSessions = db.GetSessions(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        var sessions = allSessions.Where(s => ReportBuilder.CategoryOf(apps, settings, s.AppId) == AppCategory.Game && s.ActiveSec >= 60).ToList();
        // Apps, for a PC used for more than games (or for no games at all): the most used, and when in the day.
        f.TopApps = [.. report.Apps.Where(a => a.Category is not (AppCategory.Game or AppCategory.System) && a.ActiveSec >= 300 && !r.IsLeftOut(a.Exe))
            .OrderByDescending(a => a.ActiveSec).Take(5).Select(a => new RewindItem(a.Name, a.Exe, a.Path, a.ActiveSec, a.SessionCount))];
        Spread(allSessions, f.UseHours);

        // A longer rewind, month by month: the hours in each, and the game played most in it (the app used most, on a
        // PC that played none).
        if (kind != RewindKind.Month)
        {
            var pool = sessions.Count > 0 ? sessions
                : allSessions.Where(s => ReportBuilder.CategoryOf(apps, settings, s.AppId) is not (AppCategory.Game or AppCategory.System)).ToList();
            for (var m = from; m < to; m = m.AddMonths(1))
            {
                var end = m.AddMonths(1) < to ? m.AddMonths(1) : to;
                double hours = Enumerable.Range((int)(m - from).TotalDays, (int)(end - m).TotalDays).Where(d => d < f.DayHours.Count).Sum(d => f.DayHours[d]);
                var top = pool.Where(s => TimeUtil.FromUnix(s.Start) >= m && TimeUtil.FromUnix(s.Start) < end && apps.TryGetValue(s.AppId, out var a) && !r.IsLeftOut(a.Exe))
                    .GroupBy(s => s.AppId).Select(g => (Id: g.Key, Sec: g.Sum(s => s.ActiveSec))).OrderByDescending(g => g.Sec).FirstOrDefault();
                RewindItem? item = top.Sec > 0 && apps.TryGetValue(top.Id, out var topApp)
                    ? new RewindItem(ReportBuilder.NameOf(apps, settings, top.Id), topApp.Exe, topApp.Path, top.Sec) : null;
                f.Months.Add(new RewindMonth(m, hours, item));
            }
        }
        f.Plays = sessions.Count;
        var plays = sessions.GroupBy(s => s.AppId).ToDictionary(g => g.Key, g => g.Count());
        f.TopGames = [.. games.Where(g => !r.IsLeftOut(g.Exe)).OrderByDescending(g => g.ActiveSec).Take(5)
            .Select(g => new RewindItem(g.Name, g.Exe, g.Path, g.ActiveSec, plays.GetValueOrDefault(g.Id)))];
        if (sessions.Where(s => apps.TryGetValue(s.AppId, out var a) && !r.IsLeftOut(a.Exe)).MaxBy(s => s.ActiveSec) is { } longest
            && apps.TryGetValue(longest.AppId, out var app))
        {
            f.LongestGame = new RewindItem(ReportBuilder.NameOf(apps, settings, longest.AppId), app.Exe, app.Path, longest.ActiveSec);
            f.LongestStart = TimeUtil.FromUnix(longest.Start);
            f.LongestEnd = TimeUtil.FromUnix(longest.End);
        }
        Spread(sessions, f.PlayHours);
        double night = sessions.Where(s => TimeUtil.FromUnix(s.Start).Hour is >= 21 or < 5).Sum(s => s.ActiveSec);
        double all = sessions.Sum(s => s.ActiveSec);
        f.NightShare = all > 0 ? night / all : 0;

        // Heat: the period's highest readings, with what was in front then (a left-out app isn't named).
        RewindHeat? Heat(Peak? p) => p is null ? null : new RewindHeat(p.Value, p.Time, Named(p.App), IsGame(p.App));
        string? Named(string? name) => name is null || apps.Values.Any(a => r.IsLeftOut(a.Exe) && ReportBuilder.NameOf(apps, settings, a.Id) == name) ? null : name;
        bool IsGame(string? name) => name is not null && games.Any(g => g.Name == name);
        f.CpuHot = Heat(report.CpuTempPeak);
        f.GpuHot = Heat(report.GpuTempPeak);
        f.HotSpot = Heat(report.GpuHotPeak);

        // Changes: a new graphics driver, and apps in and out.
        var changes = db.GetChanges(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        if (changes.Where(c => c.IsGraphicsDriver).MaxBy(c => c.Time) is { } driver)
            (f.NewDriver, f.NewDriverAt) = (driver.Now, driver.Time);
        f.Installed = changes.Count(c => c.Kind == ChangeKind.AppInstalled && !r.IsLeftOut(c.Subject));
        f.Updated = changes.Count(c => c.Kind == ChangeKind.AppUpdated && !r.IsLeftOut(c.Subject));
        f.Removed = changes.Count(c => c.Kind == ChangeKind.AppRemoved && !r.IsLeftOut(c.Subject));

        // Internet: the days' totals, as the Network page adds them up (a custom period's report has no internet part);
        // the apps that took in the most.
        long uFrom = TimeUtil.ToUnix(from), uTo = TimeUtil.ToUnix(to);
        var netDays = db.GetNetDays(uFrom, uTo);
        (f.Down, f.Up) = (netDays.Sum(d => d.Down), netDays.Sum(d => d.Up));
        f.TopInternet = [.. db.GetNetAppTotals(uFrom, uTo).Where(u => u.Down > 0 && apps.TryGetValue(u.App, out var a) && !r.IsLeftOut(a.Exe))
            .OrderByDescending(u => u.Down).Take(3)
            .Select(u => new RewindItem(ReportBuilder.NameOf(apps, settings, u.App), apps[u.App].Exe, apps[u.App].Path, u.Down))];

        // Problems: counted as the Crashes page counts them.
        f.AppCrashes = report.Crashes.Count(c => c.Kind is CrashKind.AppCrash or CrashKind.AppHang && !r.IsLeftOut(c.AppExe));
        bool IsShutOff(Stability.CrashEvent c) => c.Kind == CrashKind.SystemCrash || c is { Kind: CrashKind.UnexpectedShutdown } && c.Moment != PowerMoment.ShuttingDown;
        bool IsAppCrash(Stability.CrashEvent c) => c.Kind is CrashKind.AppCrash or CrashKind.AppHang && !r.IsLeftOut(c.AppExe);
        f.ShutOffs = report.Crashes.Count(IsShutOff);
        int DayOf(Stability.CrashEvent c) => (int)(c.Time.Date - from.Date).TotalDays;
        f.CrashesByDay = [.. Enumerable.Range(0, dayCount).Select(k => report.Crashes.Count(c => IsAppCrash(c) && DayOf(c) == k))];
        f.ShutOffsByDay = [.. Enumerable.Range(0, dayCount).Select(k => report.Crashes.Count(c => IsShutOff(c) && DayOf(c) == k))];
        return f;
    }
}

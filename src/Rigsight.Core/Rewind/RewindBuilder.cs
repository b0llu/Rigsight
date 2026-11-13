using Rigsight.Core.Data;
using Rigsight.Core.Games;
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

    /// <summary>The period's facts, every topic in (choosing topics and leaving games out were removed 2026-11-13).</summary>
    public static RewindFacts Build(RigsightDb db, RewindKind kind, DateTime from, DateTime to, RigsightSettings settings, DateTime now)
    {
        var report = ReportBuilder.BuildCustom(db, from, to, settings);
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var f = new RewindFacts
        {
            Id = IdOf(kind, from), Kind = kind, From = from, To = to, Made = now,
            Time = true, Games = true, Internet = true, Heat = true, Changes = true, Problems = true,
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
        f.TopApps = [.. report.Apps.Where(a => a.Category is not (AppCategory.Game or AppCategory.System) && a.ActiveSec >= 300)
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
                var top = pool.Where(s => TimeUtil.FromUnix(s.Start) >= m && TimeUtil.FromUnix(s.Start) < end && apps.TryGetValue(s.AppId, out var a))
                    .GroupBy(s => s.AppId).Select(g => (Id: g.Key, Sec: g.Sum(s => s.ActiveSec))).OrderByDescending(g => g.Sec).FirstOrDefault();
                RewindItem? item = top.Sec > 0 && apps.TryGetValue(top.Id, out var topApp)
                    ? new RewindItem(ReportBuilder.NameOf(apps, settings, top.Id), topApp.Exe, topApp.Path, top.Sec) : null;
                f.Months.Add(new RewindMonth(m, hours, item));
            }
        }
        // Plays as the Games page counts them (sessions joined, see GameHistory.Spans), each in the period it began in: a
        // session of a minute or more each was a play here, 27 of Dota 2 in October 2026 where the Games page had 22.
        long pFrom = TimeUtil.ToUnix(from), pTo = TimeUtil.ToUnix(to);
        var stops = GameHistory.Stops(db, settings);
        var plays = games.ToDictionary(g => g.Id, g => GameHistory.Spans(db, [g.Id], stops).Where(p => p.Start >= pFrom && p.Start < pTo).ToList());
        f.Plays = plays.Values.Sum(p => p.Count);
        f.TopGames = [.. games.OrderByDescending(g => g.ActiveSec).Take(5)
            .Select(g => new RewindItem(g.Name, g.Exe, g.Path, g.ActiveSec, plays[g.Id].Count))];
        var longest = plays.Where(x => apps.TryGetValue(x.Key, out var a))
            .SelectMany(x => x.Value.Select(p => (App: x.Key, Play: p))).OrderByDescending(x => x.Play.Active).FirstOrDefault();
        if (longest.Play.Active > 0 && apps.TryGetValue(longest.App, out var app))
        {
            f.LongestGame = new RewindItem(ReportBuilder.NameOf(apps, settings, longest.App), app.Exe, app.Path, longest.Play.Active);
            f.LongestStart = TimeUtil.FromUnix(longest.Play.Start);
            f.LongestEnd = TimeUtil.FromUnix(longest.Play.End);
        }
        Spread(sessions, f.PlayHours);
        double night = sessions.Where(s => TimeUtil.FromUnix(s.Start).Hour is >= 21 or < 5).Sum(s => s.ActiveSec);
        double all = sessions.Sum(s => s.ActiveSec);
        f.NightShare = all > 0 ? night / all : 0;

        // Heat: the period's highest readings, with what was in front then.
        RewindHeat? Heat(Peak? p) => p is null ? null : new RewindHeat(p.Value, p.Time, p.App, IsGame(p.App));
        bool IsGame(string? name) => name is not null && games.Any(g => g.Name == name);
        f.CpuHot = Heat(report.CpuTempPeak);
        f.GpuHot = Heat(report.GpuTempPeak);
        f.HotSpot = Heat(report.GpuHotPeak);

        // Changes: a new graphics driver, and apps in and out.
        var changes = db.GetChanges(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to));
        if (changes.Where(c => c.IsGraphicsDriver).MaxBy(c => c.Time) is { } driver)
            (f.NewDriver, f.NewDriverAt) = (driver.Now, driver.Time);
        f.Installed = changes.Count(c => c.Kind == ChangeKind.AppInstalled);
        f.Updated = changes.Count(c => c.Kind == ChangeKind.AppUpdated);
        f.Removed = changes.Count(c => c.Kind == ChangeKind.AppRemoved);

        // Internet: the days' totals, as the Network page adds them up (a custom period's report has no internet part);
        // the apps that took in the most.
        long uFrom = TimeUtil.ToUnix(from), uTo = TimeUtil.ToUnix(to);
        var netDays = db.GetNetDays(uFrom, uTo);
        (f.Down, f.Up) = (netDays.Sum(d => d.Down), netDays.Sum(d => d.Up));
        f.TopInternet = [.. db.GetNetAppTotals(uFrom, uTo).Where(u => u.Down > 0 && apps.TryGetValue(u.App, out var a))
            .OrderByDescending(u => u.Down).Take(3)
            .Select(u => new RewindItem(ReportBuilder.NameOf(apps, settings, u.App), apps[u.App].Exe, apps[u.App].Path, u.Down))];

        // Problems: counted as the Crashes page counts them.
        f.AppCrashes = report.Crashes.Count(c => c.Kind is CrashKind.AppCrash or CrashKind.AppHang);
        bool IsShutOff(Stability.CrashEvent c) => c.Kind == CrashKind.SystemCrash || c is { Kind: CrashKind.UnexpectedShutdown } && c.Moment != PowerMoment.ShuttingDown;
        bool IsAppCrash(Stability.CrashEvent c) => c.Kind is CrashKind.AppCrash or CrashKind.AppHang;
        f.ShutOffs = report.Crashes.Count(IsShutOff);
        int DayOf(Stability.CrashEvent c) => (int)(c.Time.Date - from.Date).TotalDays;
        f.CrashesByDay = [.. Enumerable.Range(0, dayCount).Select(k => report.Crashes.Count(c => IsAppCrash(c) && DayOf(c) == k))];
        f.ShutOffsByDay = [.. Enumerable.Range(0, dayCount).Select(k => report.Crashes.Count(c => IsShutOff(c) && DayOf(c) == k))];
        return f;
    }
}

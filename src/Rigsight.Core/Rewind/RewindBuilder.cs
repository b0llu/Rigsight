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

        var used = report.Days.Where(d => d.OnSec > 0).ToList();
        f.DaysOn = used.Count;
        if (used.MaxBy(d => d.OnSec) is { } busiest) (f.BusiestDay, f.BusiestDaySec) = (busiest.Day, busiest.OnSec);

        // Games: the report's own game time; each game by its time in front, the left-out ones counted in the total only.
        f.GamingSec = report.GamingSec;
        var games = report.Apps.Where(a => a.Category == AppCategory.Game && a.ActiveSec >= 60).ToList();
        f.GameCount = games.Count;
        var sessions = db.GetSessions(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to))
            .Where(s => ReportBuilder.CategoryOf(apps, settings, s.AppId) == AppCategory.Game && s.ActiveSec >= 60).ToList();
        f.Plays = sessions.Count;
        var plays = sessions.GroupBy(s => s.AppId).ToDictionary(g => g.Key, g => g.Count());
        f.TopGames = [.. games.Where(g => !r.IsLeftOut(g.Exe)).OrderByDescending(g => g.ActiveSec).Take(5)
            .Select(g => new RewindItem(g.Name, g.Exe, g.Path, g.ActiveSec, plays.GetValueOrDefault(g.Id)))];
        if (sessions.Where(s => apps.TryGetValue(s.AppId, out var a) && !r.IsLeftOut(a.Exe)).MaxBy(s => s.ActiveSec) is { } longest
            && apps.TryGetValue(longest.AppId, out var app))
        {
            f.LongestGame = new RewindItem(ReportBuilder.NameOf(apps, settings, longest.AppId), app.Exe, app.Path, longest.ActiveSec);
            f.LongestStart = TimeUtil.FromUnix(longest.Start);
        }
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

        // Internet: the report's totals; the apps that took in the most.
        if (report.Net is { HasData: true } net)
        {
            (f.Down, f.Up) = (net.Down, net.Up);
            f.TopInternet = [.. net.Apps.Where(a => !r.IsLeftOut(a.Exe) && a.Use.Down > 0).OrderByDescending(a => a.Use.Down).Take(3)
                .Select(a => new RewindItem(a.Name, a.Exe, a.Path, a.Use.Down))];
        }

        // Problems: counted as the Crashes page counts them.
        f.AppCrashes = report.Crashes.Count(c => c.Kind is CrashKind.AppCrash or CrashKind.AppHang && !r.IsLeftOut(c.AppExe));
        f.ShutOffs = report.Crashes.Count(c => c.Kind == CrashKind.SystemCrash || c is { Kind: CrashKind.UnexpectedShutdown } && c.Moment != PowerMoment.ShuttingDown);
        return f;
    }
}

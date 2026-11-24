using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Drivers;

/// <summary>
/// A stretch in which one driver kept a CPU core busy much longer than its own usual, outside games (issue #15 follow-up,
/// approved mock-up 2026-11-24): a line on the Timeline that opens what happened.
/// </summary>
/// <param name="From">The first such minute (Unix seconds).</param>
/// <param name="To">The last such minute.</param>
/// <param name="Minutes">The minutes in the stretch that were well past the usual.</param>
/// <param name="LongestAt">The minute of the longest burst.</param>
/// <param name="UsualUs">The driver's usual longest each hour, over the week before the stretch's day.</param>
/// <param name="LongBursts">Bursts of 1 ms or more in those minutes.</param>
public sealed record DelayStretch(string Driver, DriverPart? Part, long From, long To, int Minutes, int LongestUs, long LongestAt, int UsualUs, int LongBursts)
{
    public string Who => DriverWords.Who(Driver, Part);
    public string Title => Lang.T($"{Who} kept the CPU busy much longer than usual");

    /// <summary>Grey beside the title: the file, its longest and usual, and for a stretch of several minutes when.</summary>
    public string Detail
    {
        get
        {
            string line = DriverWords.FileLine(Driver, Part, LongestUs, UsualUs);
            if (Minutes < 2) return line;
            string from = TimeText.Time(TimeUtil.FromUnix(From)), to = TimeText.Time(TimeUtil.FromUnix(To));
            return $"{line} · " + Lang.T($"in {Minutes} minutes from {from} to {to}");
        }
    }
}

/// <summary>An hour of the opened mark's day: the driver's longest burst in it.</summary>
public sealed record DelayHour(DateTime Start, int LongestUs);

/// <summary>Another driver busy much longer than usual in the same minutes.</summary>
public sealed record DelayOther(string Who, string Driver, int LongestUs);

/// <summary>What an opened mark shows (see <see cref="DriverDelays.Open"/>).</summary>
public sealed record DelayCase(DelayStretch Stretch, string What, string? InFront, bool InGame, List<DelayOther> Others, List<SystemChange> Changes,
    List<DelayHour> Hours, List<(string Step, string? More)> Tips);

/// <summary>
/// The Timeline's driver marks (issue #15 follow-up, approved mock-up 2026-11-24). A minute counts when a driver's longest
/// burst in it was at least <see cref="DriverRules.LongUs"/> and <see cref="DriverRules.MuchLonger"/> times its usual (the
/// middle of its hourly longest over the <see cref="DriverRules.UsualDays"/> days before), with a game not in front (a
/// play already says it) and none of its readings dropped. A driver's minutes less than <see cref="JoinSec"/> apart are
/// one stretch, one line. Every driver counts here, Windows' shared ones too: they're said for what they are.
/// </summary>
public static class DriverDelays
{
    /// <summary>A driver's minutes this close together are one stretch (seconds).</summary>
    public const int JoinSec = 3600;

    /// <param name="games">The apps that are games (by the person's own categories): their minutes in front are left out.</param>
    public static List<DelayStretch> Marks(RigsightDb db, IReadOnlyCollection<long> games)
    {
        if (db.FirstDriverHour() is not long first) return [];
        // Nothing can be past a usual before there's a week to make one.
        long from = first + DriverRules.UsualDays * 86400L;
        var minutes = db.GetDriverMinutesOver(from, long.MaxValue / 2, DriverRules.LongUs);
        if (minutes.Count == 0) return [];
        long lo = minutes[0].Ts, hi = minutes[^1].Ts + 60;
        var missed = db.GetDriverMissed(lo, hi);
        var front = db.GetFrontApps(lo, hi);
        var gameSet = games as IReadOnlySet<long> ?? games.ToHashSet();
        var parts = db.GetDriverParts();
        var usual = new Dictionary<(string, long), int?>();
        int? UsualOf(string driver, long ts)
        {
            long day = TimeUtil.ToUnix(TimeUtil.FromUnix(ts).Date);
            return usual.TryGetValue((driver, day), out var u) ? u : usual[(driver, day)] = db.GetDriverUsualLongest(driver, day);
        }
        return Stretches(minutes, missed, front, gameSet, parts, UsualOf);
    }

    /// <summary>The rule itself, on what was read (see the class).</summary>
    public static List<DelayStretch> Stretches(IReadOnlyList<DriverTally> minutes, IReadOnlySet<long> missed, IReadOnlyDictionary<long, long> front,
        IReadOnlySet<long> games, IReadOnlyDictionary<string, DriverPart> parts, Func<string, long, int?> usualOf)
    {
        var kept = new List<(DriverTally Minute, int Usual)>();
        foreach (var m in minutes)
        {
            if (m.Driver.Length == 0 || m.LongestUs < DriverRules.LongUs || missed.Contains(m.Ts)) continue;
            if (front.TryGetValue(m.Ts, out long app) && games.Contains(app)) continue;
            if (usualOf(m.Driver, m.Ts) is not int u) continue;
            if (m.LongestUs < Math.Max(DriverRules.LongUs, DriverRules.MuchLonger * u)) continue;
            kept.Add((m, u));
        }
        var list = new List<DelayStretch>();
        foreach (var group in kept.GroupBy(k => k.Minute.Driver, StringComparer.OrdinalIgnoreCase))
        {
            var run = new List<(DriverTally Minute, int Usual)>();
            foreach (var k in group.OrderBy(k => k.Minute.Ts))
            {
                if (run.Count > 0 && k.Minute.Ts - run[^1].Minute.Ts > JoinSec) { list.Add(Of(run)); run = []; }
                run.Add(k);
            }
            if (run.Count > 0) list.Add(Of(run));
        }
        return [.. list.OrderBy(s => s.From)];

        DelayStretch Of(List<(DriverTally Minute, int Usual)> run)
        {
            var top = run.MaxBy(k => k.Minute.LongestUs);
            string driver = top.Minute.Driver;
            return new DelayStretch(driver, parts.GetValueOrDefault(driver), run[0].Minute.Ts, run[^1].Minute.Ts, run.Count, top.Minute.LongestUs, top.Minute.Ts,
                top.Usual, run.Sum(k => k.Minute.Long));
        }
    }

    /// <summary>
    /// An opened mark: what the driver is, the app in front at its longest, the other drivers busy much longer than usual in
    /// the same minutes, the big changes in the week before, the driver's longest each hour of that day, what can be tried.
    /// </summary>
    public static DelayCase Open(RigsightDb db, DelayStretch s, Func<long, string?> nameOf, IReadOnlyCollection<long> games)
    {
        var parts = db.GetDriverParts();
        long from = s.From, to = s.To + 60;
        var front = db.GetFrontApps(s.LongestAt, s.LongestAt + 60);
        long? app = front.TryGetValue(s.LongestAt, out long a) ? a : null;
        string? inFront = app is long id ? nameOf(id) : null;

        // The others: past their own usual in the same minutes, as the mark's own driver was.
        var others = new List<DelayOther>();
        foreach (var g in db.GetDriverMinutesOver(from, to, DriverRules.LongUs).Where(m => !m.Driver.Equals(s.Driver, StringComparison.OrdinalIgnoreCase) && m.Driver.Length > 0)
                     .GroupBy(m => m.Driver, StringComparer.OrdinalIgnoreCase))
        {
            int longest = g.Max(m => m.LongestUs);
            if (db.GetDriverUsualLongest(g.Key, TimeUtil.ToUnix(TimeUtil.FromUnix(from).Date)) is int u && longest < DriverRules.MuchLonger * u) continue;
            others.Add(new(DriverWords.Who(g.Key, parts.GetValueOrDefault(g.Key)), g.Key, longest));
        }

        // The big changes, and drivers, in the week before.
        var changes = db.GetChanges(from - 7 * 86400L, to).Where(c => Reports.ChangeEffects.IsMajor(c) || c.Kind == ChangeKind.Driver)
            .OrderByDescending(c => c.Time).Take(5).ToList();

        // Its longest each hour of the day (the hours as recorded, which start on the hour of the clock Windows keeps).
        var day = TimeUtil.FromUnix(s.LongestAt).Date;
        long dayFrom = TimeUtil.ToUnix(day), dayTo = TimeUtil.ToUnix(day.AddDays(1));
        var hours = db.GetDriverHours(dayFrom - 3599, dayTo, s.Driver).Select(h => new DelayHour(TimeUtil.FromUnix(h.Ts), h.LongestUs))
            .Where(h => h.Start.AddHours(1) > day && h.Start < day.AddDays(1)).ToList();

        return new DelayCase(s, DriverWords.What(s.Driver, s.Part), inFront, app is long g2 && games.Contains(g2), others.OrderByDescending(o => o.LongestUs).Take(4).ToList(),
            changes, hours, DriverWords.Tips(DriverWords.AreaOf(s.Driver, s.Part)));
    }
}

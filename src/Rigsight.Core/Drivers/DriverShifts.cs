using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Drivers;

/// <summary>
/// How one driver's delays moved across a big change, like for like (see <see cref="DriverShifts"/>).
/// </summary>
/// <param name="Game">The game compared (its name), or null for the hours outside games.</param>
/// <param name="UsualBefore">The middle of its hourly longest before (µs).</param>
/// <param name="LongBefore">Its bursts of 1 ms or more, an hour, before.</param>
public sealed record DriverShift(string Driver, DriverPart? Part, string? Game, int HoursBefore, int HoursAfter, int UsualBefore, int UsualAfter,
    double LongBefore, double LongAfter, EffectTone Tone)
{
    public string Who => DriverWords.Who(Driver, Part);
}

/// <summary>
/// Driver delays before and since a big change (issue #15 follow-up, approved mock-up 2026-11-24): on a graphics driver
/// its GPU's driver and Windows' graphics system, on a Windows update or version or a BIOS every driver. Compared like for
/// like only: the same game's hours on both sides (an hour counts for a game when it was in front at least
/// <see cref="GameMinutes"/> minutes), else the hours outside games against the same; at least <see cref="MinHours"/> on
/// each side, the change's own hour left out. Worse or better only when it moved by a lot (see <see cref="ToneOf"/>);
/// never "because": a driver's timing moves with what runs, not only with a change.
/// </summary>
public static class DriverShifts
{
    public const int MinHours = 5, GameMinutes = 40;

    /// <summary>The comparer ChangeEffects asks for each big change, reading what it needs from <paramref name="db"/>.</summary>
    public static Func<DateTime, DateTime, DateTime, IReadOnlyList<SystemChange>, DriverShift?>? From(RigsightDb db, IReadOnlySet<long> games, Func<long, string> nameOf)
    {
        if (db.FirstDriverHour() is not long first) return null;
        Dictionary<string, DriverPart>? parts = null;
        return (from, at, to, of) =>
        {
            long f = TimeUtil.ToUnix(from), a = TimeUtil.ToUnix(at), t = TimeUtil.ToUnix(to);
            // Only changes with driver history on both sides.
            if (first > f + 86400 || !of.Any(Counts)) return null;
            parts ??= db.GetDriverParts();
            var hours = db.GetDriverHours(f, t);
            if (hours.Count == 0) return null;
            return Compare(hours, db.GetFrontHours(f, t), games, parts, a, of, nameOf);
        };
    }

    /// <summary>The changes driver delays are compared across.</summary>
    public static bool Counts(SystemChange c) => c.IsGraphicsDriver || c.Kind is ChangeKind.WindowsUpdate or ChangeKind.Windows or ChangeKind.Firmware;

    /// <summary>The rule itself, on what was read (see the class): the driver that moved most, or null.</summary>
    /// <param name="front">Each hour's app in front the most, with its minutes.</param>
    /// <param name="at">The change (Unix seconds).</param>
    public static DriverShift? Compare(IReadOnlyList<DriverTally> hours, IReadOnlyDictionary<long, (long App, int Minutes)> front, IReadOnlySet<long> games,
        IReadOnlyDictionary<string, DriverPart> parts, long at, IReadOnlyList<SystemChange> of, Func<long, string> nameOf)
    {
        bool graphics = of.All(c => c.IsGraphicsDriver);
        long changeHour = at / 3600 * 3600;
        DriverShift? best = null;
        double bestScore = 0;
        foreach (var byDriver in hours.Where(h => h.Driver.Length > 0).GroupBy(h => h.Driver, StringComparer.OrdinalIgnoreCase))
        {
            string driver = byDriver.Key.ToLowerInvariant();
            var part = parts.GetValueOrDefault(driver);
            if (graphics && part?.Kind != PartKind.Gpu && driver != "dxgkrnl.sys") continue;
            // Like for like: a game's own hours, or the hours outside games (0); hours mixing the two are left out.
            foreach (var side in byDriver.GroupBy(h => Situation(h.Ts, front, games)).Where(g => g.Key >= 0))
            {
                var before = side.Where(h => h.Ts < changeHour).ToList();
                var after = side.Where(h => h.Ts > changeHour).ToList();
                if (before.Count < MinHours || after.Count < MinHours) continue;
                int ub = Middle(before), ua = Middle(after);
                double lb = before.Average(h => (double)h.Long), la = after.Average(h => (double)h.Long);
                var tone = ToneOf(ub, ua, lb, la);
                if (tone == EffectTone.Same) continue;
                double score = (tone == EffectTone.Worse ? 2 : 1) * Math.Max(Math.Max(ua, 1) / (double)Math.Max(ub, 1), Math.Max(ub, 1) / (double)Math.Max(ua, 1));
                if (score <= bestScore) continue;
                bestScore = score;
                best = new DriverShift(driver, part, side.Key == 0 ? null : nameOf(side.Key), before.Count, after.Count, ub, ua, lb, la, tone);
            }
        }
        return best;
    }

    /// <summary>
    /// Worse: the usual longest half as long again and 0.5 ms more, reaching 1 ms; or three times the long bursts an hour,
    /// 10 more. Better the same the other way round.
    /// </summary>
    public static EffectTone ToneOf(int usualBefore, int usualAfter, double longBefore, double longAfter)
    {
        static bool Up(int b, int a, double lb, double la) =>
            (a >= 1.5 * b && a - b >= 500 && a >= DriverRules.LongUs) || (la >= 3 * lb && la - lb >= 10);
        return Up(usualBefore, usualAfter, longBefore, longAfter) ? EffectTone.Worse
            : Up(usualAfter, usualBefore, longAfter, longBefore) ? EffectTone.Better : EffectTone.Same;
    }

    /// <summary>A game's id when it held the hour, 0 for an hour outside games, -1 for one in between.</summary>
    private static long Situation(long hour, IReadOnlyDictionary<long, (long App, int Minutes)> front, IReadOnlySet<long> games)
    {
        if (!front.TryGetValue(hour, out var f) || !games.Contains(f.App)) return 0;
        return f.Minutes >= GameMinutes ? f.App : -1;
    }

    private static int Middle(List<DriverTally> hours)
    {
        var sorted = hours.Select(h => h.LongestUs).Order().ToList();
        return sorted[sorted.Count / 2];
    }
}

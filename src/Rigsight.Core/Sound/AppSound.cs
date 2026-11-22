using Rigsight.Core.Data;

namespace Rigsight.Core.Sound;

/// <summary>The line under an app's three boxes on the Apps page: how often the sound cut out in it, against its usual, and a possible cause.</summary>
public sealed record AppSoundLine(string Text, string? Cause);

/// <summary>
/// Sound dropouts on the Apps page (approved mock-up board 6, 2026-11-22): a line under the app's boxes only when the sound
/// cut out in it clearly more than on its usual days. Its usual is the average cuts a day over the <see cref="UsualDays"/>
/// days before the period, counting only days it was open and only since dropouts were looked for (days before that have
/// none because nobody looked). A period of several days is held against that usual times the days it was open in it.
/// Nothing shows without such a day to compare with.
/// </summary>
public static class AppSound
{
    /// <summary>Never for fewer cuts than this in the period.</summary>
    public const int AtLeast = 3;

    /// <summary>The cuts must be more than this many times what its usual gives the period.</summary>
    public const double Over = 2.0;

    public const int UsualDays = 14;

    /// <summary>Whether <paramref name="n"/> cuts are worth a line against <paramref name="expected"/> (its usual for the period).</summary>
    public static bool Shows(int n, double expected) => n >= AtLeast && n > Over * expected;

    /// <summary>
    /// "Your sound cut out 14 times while you were using Discord today. On most days it doesn't cut out at all."
    /// <paramref name="period"/> is "today", "yesterday", "on Fri, 20 Nov" or "in this period".
    /// </summary>
    public static string Text(int n, string app, string period, double usual) =>
        Lang.T($"Your sound cut out {n} times while you were using {app} {period}.") + " "
        + (usual <= 0 ? Lang.T("On most days it doesn't cut out at all.") : Lang.T("That's more than on most days."));

    /// <summary>The line for one app over <paramref name="from"/> to <paramref name="to"/> (local, end excluded), or null.</summary>
    public static AppSoundLine? Of(RigsightDb db, long app, string name, DateTime from, DateTime to, string period)
    {
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
        var cuts = SoundDropouts.Cuts(db.GetSoundGlitches(f * 1000, t * 1000, heardOnly: true));
        int n = cuts.Count(c => c.App == app);
        if (n < AtLeast) return null;

        // Its usual: the days before it was open, since dropouts were looked for.
        var start = from.AddDays(-UsualDays);
        if (db.SoundSince is long since && TimeUtil.FromUnix(since).Date > start) start = TimeUtil.FromUnix(since).Date;
        if (start >= from) return null;
        var usedBefore = DaysOpen(db, app, start, from);
        if (usedBefore.Count == 0) return null;
        var before = SoundDropouts.Cuts(db.GetSoundGlitches(TimeUtil.ToUnix(start) * 1000, f * 1000, heardOnly: true))
            .Count(c => c.App == app && usedBefore.Contains(TimeUtil.FromUnix(c.AtMs / 1000).Date));
        double usual = (double)before / usedBefore.Count;
        int daysNow = Math.Max(1, DaysOpen(db, app, from, to).Count);
        if (!Shows(n, usual * daysNow)) return null;

        // A possible cause: the stretch with the most of this app's cuts, when its part holds up.
        var cause = SoundDropouts.Stretches(cuts).Where(s => s.Cuts.Any(c => c.App == app))
            .OrderByDescending(s => s.Cuts.Count(c => c.App == app))
            .Select(s => SoundDropouts.Find(db, s)).FirstOrDefault(c => c is not null);
        return new AppSoundLine(Text(n, name, period, usual), cause?.RightThen);
    }

    /// <summary>The local days from <paramref name="from"/> to <paramref name="to"/> the app was open.</summary>
    private static HashSet<DateTime> DaysOpen(RigsightDb db, long app, DateTime from, DateTime to) =>
        [.. db.GetAppHoursOf(app, TimeUtil.ToUnix(from), TimeUtil.ToUnix(to))
            .Where(h => h.FgSec + h.IdleSec + h.BgSec + h.MinSec > 0).Select(h => TimeUtil.FromUnix(h.Ts).Date)];
}

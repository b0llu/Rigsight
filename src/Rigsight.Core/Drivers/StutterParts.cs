using Rigsight.Core.Data;
using Rigsight.Core.Games;

namespace Rigsight.Core.Drivers;

/// <summary>
/// "Possibly your Ethernet adapter" (approved mock-up v6, 2026-11-21): the part of the PC whose driver was using the CPU
/// for much longer than usual right when a game stuttered. Only said when it holds up:
/// <list type="bullet">
/// <item>its long bursts fell inside at least <see cref="AtLeast"/> stutters and <see cref="Share"/> of them;</item>
/// <item>that is at least <see cref="Over"/> times what chance gives: a frame of L ms in a minute with n long bursts
/// meets one about n·L/60,000 of the time, so the stutters' expected count is summed from the play's own minutes, the
/// longer stutter frames included (comparing with the play's smooth frames, which are shorter, would flatter it);</item>
/// <item>in at least <see cref="Stretches"/> separate stretches of the play, so one burst of trouble can't make the case;</item>
/// <item>the part can be named (see <see cref="DriverParts"/>) and has a usual (a week of history) that the bursts were
/// well past, so "much longer than usual" is true.</item>
/// </list>
/// Minutes in which Windows dropped readings are left out of everything. At most "possibly": lining up with stutters,
/// however often, isn't proof.
/// </summary>
public static class StutterParts
{
    public const int AtLeast = 5;
    public const double Share = 0.2, Over = 3.0;
    public const int Stretches = 2;
    /// <summary>Matched stutters further apart than this start a new stretch (ms).</summary>
    public const long StretchGapMs = 5 * 60_000;

    /// <summary>What a play's chart says about the part, in its three places.</summary>
    /// <param name="Line">The stutter line in the play's chart.</param>
    /// <param name="Value">The bright line of the play's hover ("Your Ethernet adapter"), under <see cref="Label"/>.</param>
    /// <param name="Sentence">The quieter line under it.</param>
    public sealed record Cause(DriverPart Part, int UsualUs, string Line, string Label, string Value, string Sentence);

    /// <summary>The part for a play's stutters, from what the agent stored, or null.</summary>
    public static Cause? Find(RigsightDb db, IReadOnlyList<Stutter> stutters, long playFrom, long playTo)
    {
        if (stutters.Count < AtLeast || !stutters.Any(s => s.Drv is { Length: > 0 })) return null;
        var parts = db.GetDriverParts();
        var minutes = db.GetDriverMinutes(playFrom / 60 * 60, playTo + 60);
        var missed = db.GetDriverMissed(playFrom / 60 * 60, playTo + 60);
        return Decide(stutters, minutes, missed, parts, d => db.GetDriverUsualLongest(d, playFrom));
    }

    /// <summary>The rule itself, on what was read (see the class).</summary>
    public static Cause? Decide(IReadOnlyList<Stutter> stutters, IReadOnlyList<DriverTally> minutes, IReadOnlySet<long> missed,
        IReadOnlyDictionary<string, DriverPart> parts, Func<string, int?> usualOf)
    {
        var valid = stutters.Where(s => !missed.Contains(MinuteOf(s.AtMs))).ToList();
        if (valid.Count < AtLeast) return null;
        var longs = minutes.ToDictionary(m => (m.Ts, m.Driver), m => m.Long);

        Cause? best = null;
        int bestCount = 0;
        foreach (var group in valid.Where(s => s.Drv is { Length: > 0 }).GroupBy(s => s.Drv!, StringComparer.OrdinalIgnoreCase))
        {
            string driver = group.Key;
            if (!parts.TryGetValue(driver, out var part) || part.Say is not { } say) continue;
            if (usualOf(driver) is not int usual) continue;
            // The bursts must be well past the part's usual for "much longer than usual" to be true.
            int floor = (int)Math.Max(DriverRules.LongUs, DriverRules.MuchLonger * usual);
            var matched = group.Where(s => s.DrvUs >= floor).Select(s => s.AtMs).Order().ToList();
            if (matched.Count < AtLeast || matched.Count < Share * valid.Count) continue;
            double expected = valid.Sum(s => Math.Min(1.0, longs.GetValueOrDefault((MinuteOf(s.AtMs), driver)) * s.Ms / 60_000.0));
            if (matched.Count < Over * expected) continue;
            int stretches = 1;
            for (int i = 1; i < matched.Count; i++) if (matched[i] - matched[i - 1] > StretchGapMs) stretches++;
            if (stretches < Stretches) continue;
            if (matched.Count <= bestCount) continue;
            bestCount = matched.Count;
            string sentence = Lang.T("Right when the game stuttered, it was often using the CPU for much longer than usual.");
            best = new Cause(part, usual, Lang.T($"Possibly your {say}. Right when the game stuttered, it was often using the CPU for much longer than usual."),
                Lang.T("Possible cause"), Lang.T($"Your {say}"), sentence);
        }
        return best;
    }

    /// <summary>
    /// The minute hover's line under its stutter count, for a play with a <paramref name="cause"/>: only for a minute with
    /// stutters in which the part's longest burst was well past its usual. Null otherwise.
    /// </summary>
    public static string? MinuteLine(Cause cause, int stutters, DriverTally? minute)
    {
        if (stutters == 0 || minute is null || cause.Part.Say is not { } say) return null;
        if (minute.LongestUs < Math.Max(DriverRules.LongUs, DriverRules.MuchLonger * cause.UsualUs)) return null;
        return Lang.T($"During these stutters, your {say} was using the CPU for much longer than usual.");
    }

    /// <summary>The grey line under it: the device's own name and its driver's file, for anyone who wants to search them.</summary>
    public static string Detail(DriverPart part) => part.Model is { Length: > 0 } model ? $"{model} · {part.Driver}" : part.Driver;

    /// <summary>A minute whose driver readings were partly dropped, in a play's minute hover.</summary>
    public static string Missed => Lang.T("Some readings from your devices were missed in this minute, so nothing is said about them.");

    private static long MinuteOf(long atMs) => atMs / 60_000 * 60;
}

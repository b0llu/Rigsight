using Rigsight.Core.Data;
using Rigsight.Core.Drivers;

namespace Rigsight.Core.Sound;

/// <summary>A stretch of sound dropouts: from the first to the last (Unix ms), how many cuts, the app most of them played in (null when none had most of them), the output used most.</summary>
public sealed record SoundStretch(long FromMs, long ToMs, int Count, long? App, string? Device, IReadOnlyList<SoundGlitch> Cuts);

/// <summary>
/// Sound dropouts as people read them (2026-11-22, drafts for the owner's review): the heard glitches made into cuts,
/// cuts grouped into stretches, each stretch's line, and the part of the PC that was often using the CPU for much longer
/// than usual right when the sound cut out. That part is only named when it holds up, the same rule as a game's
/// stutters (<see cref="StutterParts"/>):
/// <list type="bullet">
/// <item>its long bursts fell in the <see cref="SoundRules.WindowMs"/> before at least <see cref="AtLeast"/> cuts and
/// <see cref="Share"/> of them;</item>
/// <item>that is at least <see cref="Over"/> times what chance gives: a window of W ms in a minute with n long bursts
/// meets one about n·W/60,000 of the time, summed over the cuts;</item>
/// <item>in at least <see cref="SeparateStretches"/> separate stretches 5 minutes apart, or, for a stretch under 10 minutes, the
/// matches above alone;</item>
/// <item>the part can be named and has a usual that the bursts were well past.</item>
/// </list>
/// Minutes in which Windows dropped driver readings are left out. At most "possibly". Pure, apart from <see cref="Find"/>.
/// </summary>
public static class SoundDropouts
{
    public const int AtLeast = 5;
    public const double Share = 0.2, Over = 3.0;
    public const int SeparateStretches = 2;

    /// <summary>What a stretch says about the part (see the class).</summary>
    public sealed record Cause(DriverPart Part, int UsualUs, string Line);

    /// <summary>
    /// The heard glitches as cuts, in time order: a glitch within <see cref="SoundRules.OneCutMs"/> of the one before
    /// joins its cut. A cut keeps its first glitch's time and kind, the first app and output named in it, its frames
    /// summed, and the driver with the most time in its window.
    /// </summary>
    public static List<SoundGlitch> Cuts(IEnumerable<SoundGlitch> glitches)
    {
        var cuts = new List<SoundGlitch>();
        SoundGlitch? cut = null;
        long last = 0;
        foreach (var g in glitches.Where(g => g.Heard).OrderBy(g => g.AtMs))
        {
            if (cut is not null && g.AtMs - last <= SoundRules.OneCutMs)
            {
                cut = cut with
                {
                    App = cut.App ?? g.App,
                    Device = cut.Device ?? g.Device,
                    Frames = cut.Frames is null && g.Frames is null ? null : (cut.Frames ?? 0) + (g.Frames ?? 0),
                    Drv = g.DrvUs > cut.DrvUs ? g.Drv : cut.Drv,
                    DrvUs = Math.Max(cut.DrvUs, g.DrvUs),
                };
            }
            else
            {
                if (cut is not null) cuts.Add(cut);
                cut = g;
            }
            last = g.AtMs;
        }
        if (cut is not null) cuts.Add(cut);
        return cuts;
    }

    /// <summary>The heard glitches as stretches of cuts (a gap over <see cref="SoundRules.StretchGapMs"/> starts a new one), in time order.</summary>
    public static List<SoundStretch> Stretches(IEnumerable<SoundGlitch> glitches)
    {
        var stretches = new List<SoundStretch>();
        var group = new List<SoundGlitch>();
        foreach (var cut in Cuts(glitches))
        {
            if (group.Count > 0 && cut.AtMs - group[^1].AtMs > SoundRules.StretchGapMs)
            {
                stretches.Add(Close(group));
                group = [];
            }
            group.Add(cut);
        }
        if (group.Count > 0) stretches.Add(Close(group));
        return stretches;
    }

    private static SoundStretch Close(List<SoundGlitch> cuts)
    {
        // The app only when more than half of the cuts came while it was the one playing: "while Spotify was playing"
        // must be true of the stretch, not of a few of its cuts.
        var top = cuts.Where(c => c.App is not null).GroupBy(c => c.App!.Value).MaxBy(g => g.Count());
        long? app = top is not null && top.Count() * 2 > cuts.Count ? top.Key : null;
        string? device = cuts.Where(c => c.Device is { Length: > 0 }).GroupBy(c => c.Device!).MaxBy(g => g.Count())?.Key;
        return new SoundStretch(cuts[0].AtMs, cuts[^1].AtMs, cuts.Count, app, device, cuts);
    }

    /// <summary>The stretch's line on the Timeline, with its app's name when it has one (<paramref name="app"/>).</summary>
    public static string Line(SoundStretch s, string? app)
    {
        DateTime from = Local(s.FromMs), to = Local(s.ToMs);
        int n = s.Count;
        if (app is { Length: > 0 })
            return n == 1
                ? Lang.T($"Your sound cut out once while {app} was playing, at {from:h:mm tt}.")
                : Lang.T($"Your sound cut out {n} times while {app} was playing, {from:h:mm tt} to {to:h:mm tt}.");
        return n == 1
            ? Lang.T($"Your sound cut out once, at {from:h:mm tt}.")
            : Lang.T($"Your sound cut out {n} times, {from:h:mm tt} to {to:h:mm tt}.");
    }

    /// <summary>The part for a stretch, from what the agent stored, or null.</summary>
    public static Cause? Find(RigsightDb db, SoundStretch stretch)
    {
        if (stretch.Count < AtLeast || !stretch.Cuts.Any(c => c.Drv is { Length: > 0 })) return null;
        long from = stretch.FromMs / 60_000 * 60, to = stretch.ToMs / 1000 + 60;
        return Decide(stretch.Cuts, db.GetDriverMinutes(from, to), db.GetDriverMissed(from, to), db.GetDriverParts(),
            d => db.GetDriverUsualLongest(d, stretch.FromMs / 1000));
    }

    /// <summary>The rule itself, on what was read (see the class). <paramref name="glitches"/> are one stretch's heard glitches or cuts.</summary>
    public static Cause? Decide(IReadOnlyList<SoundGlitch> glitches, IReadOnlyList<DriverTally> minutes, IReadOnlySet<long> missed,
        IReadOnlyDictionary<string, DriverPart> parts, Func<string, int?> usualOf)
    {
        var valid = Cuts(glitches).Where(c => !missed.Contains(MinuteOf(c.AtMs))).ToList();
        if (valid.Count < AtLeast) return null;
        bool shortStretch = valid[^1].AtMs - valid[0].AtMs < SoundRules.ShortStretchMs;
        var longs = minutes.ToDictionary(m => (m.Ts, m.Driver), m => m.Long);

        Cause? best = null;
        int bestCount = 0;
        foreach (var group in valid.Where(c => c.Drv is { Length: > 0 }).GroupBy(c => c.Drv!, StringComparer.OrdinalIgnoreCase))
        {
            string driver = group.Key;
            if (!parts.TryGetValue(driver, out var part) || part.Say is not { } say) continue;
            if (usualOf(driver) is not int usual) continue;
            int floor = (int)Math.Max(DriverRules.LongUs, DriverRules.MuchLonger * usual);
            var matched = group.Where(c => c.DrvUs >= floor).Select(c => c.AtMs).Order().ToList();
            if (matched.Count < AtLeast || matched.Count < Share * valid.Count) continue;
            double expected = valid.Sum(c => Math.Min(1.0, longs.GetValueOrDefault((MinuteOf(c.AtMs), driver)) * SoundRules.WindowMs / 60_000.0));
            if (matched.Count < Over * expected) continue;
            int stretches = 1;
            for (int i = 1; i < matched.Count; i++) if (matched[i] - matched[i - 1] > SoundRules.StretchGapMs) stretches++;
            if (stretches < SeparateStretches && !shortStretch) continue;
            if (matched.Count <= bestCount) continue;
            bestCount = matched.Count;
            best = new Cause(part, usual, Lang.T($"Possibly your {say}. Right when the sound cut out, it was often using the CPU for much longer than usual."));
        }
        return best;
    }

    private static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    private static long MinuteOf(long atMs) => atMs / 60_000 * 60;
}

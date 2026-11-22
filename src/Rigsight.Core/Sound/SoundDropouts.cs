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

    /// <summary>What a stretch says about the part (see the class): <see cref="Line"/> under its Timeline row.</summary>
    public sealed record Cause(DriverPart Part, int UsualUs, string Line)
    {
        /// <summary>The same as a sentence of its own, after one about when the sound cut out (the Apps page).</summary>
        public string RightThen => Lang.T($"Possibly your {Part.Say}, which was using the CPU for much longer than usual right then.");
    }

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

    /// <summary>
    /// The stretch's row on the Timeline (tested on a no-context reader, 2026-11-22): "Sound cut out 14 times in Discord",
    /// with the app's name when it has one (<paramref name="app"/>). The time goes beside it (<see cref="When"/>).
    /// </summary>
    public static string Line(SoundStretch s, string? app) => Title(s.Count, app);

    /// <summary>"Sound cut out 14 times in Discord", "Sound cut out once", for <paramref name="n"/> cuts.</summary>
    public static string Title(int n, string? app) => app is { Length: > 0 }
        ? n == 1 ? Lang.T($"Sound cut out once in {app}") : Lang.T($"Sound cut out {n} times in {app}")
        : n == 1 ? Lang.T("Sound cut out once") : Lang.T($"Sound cut out {n} times");

    /// <summary>When the stretch was: "9:02 PM to 9:40 PM", or "9:14 PM" alone when it began and ended in the same minute.</summary>
    public static string When(SoundStretch s)
    {
        DateTime from = Local(s.FromMs), to = Local(s.ToMs);
        string a = TimeText.Time(from);
        return TimeUtil.LocalMinuteStart(from) == TimeUtil.LocalMinuteStart(to) ? a : Lang.C("stretch", $"{a} to {TimeText.Time(to)}");
    }

    /// <summary>
    /// The cuts in <paramref name="cuts"/> that belong to an app: the ones it was playing in, and any while it was the app
    /// in front (<paramref name="frontMinutes"/>: the minutes, Unix seconds at their start, it was in front). A game's
    /// play says these: the sound cutting out in Discord mid-game is heard in the game.
    /// </summary>
    public static List<SoundGlitch> OfApp(IEnumerable<SoundGlitch> cuts, long app, IReadOnlySet<long> frontMinutes) =>
        [.. cuts.Where(c => c.App == app || frontMinutes.Contains(MinuteOf(c.AtMs)))];

    /// <summary>
    /// How many times the sound cut out in each of an app's plays (<paramref name="plays"/>: Unix seconds, end included),
    /// counted as <see cref="OfApp"/> says. One read for all of them.
    /// </summary>
    public static int[] InPlays(RigsightDb db, long app, IReadOnlyList<(long Start, long End)> plays)
    {
        var counts = new int[plays.Count];
        if (plays.Count == 0) return counts;
        long from = plays.Min(p => p.Start), to = plays.Max(p => p.End) + 1;
        var cuts = OfApp(Cuts(db.GetSoundGlitches(from * 1000, to * 1000, heardOnly: true)), app, db.GetFrontMinutes(app, from, to));
        for (int i = 0; i < plays.Count; i++)
        {
            long a = plays[i].Start * 1000, b = (plays[i].End + 1) * 1000;
            counts[i] = cuts.Count(c => c.AtMs >= a && c.AtMs < b);
        }
        return counts;
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
            best = new Cause(part, usual, Lang.T($"Possibly your {say}, which was using the CPU for much longer than usual"));
        }
        return best;
    }

    private static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    private static long MinuteOf(long atMs) => atMs / 60_000 * 60;
}

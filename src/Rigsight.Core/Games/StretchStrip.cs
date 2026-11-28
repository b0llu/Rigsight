using System.Globalization;
using Rigsight.Core.Data;

namespace Rigsight.Core.Games;

/// <summary>One stutter on an opened stretch's strip: where it came (seconds from the strip's start) and its frame against its own usual.</summary>
/// <param name="Index">Its place in the stretch's <see cref="StutterStretch.Each"/> (the longest first), the list's order.</param>
public sealed record StripSpike(int Index, double At, double Ms, double UsualMs);

/// <summary>A clock mark under the strip: seconds from its start and the time it says ("8:45").</summary>
public sealed record StripTick(double At, string Label);

/// <summary>
/// The strip of an opened stretch (1.23.0, approved mock-up "1b · An opened stretch, round 2"): the stretch's own minutes,
/// from the start of its first stutter's minute to the end of its last one's (as the play's chart lights it), each stutter
/// a spike as tall as its frame on a scale to <see cref="Headroom"/> times the longest, the stretch's usual frame (the middle
/// of its stutters' own usual frames) as a dashed line, and clock marks every minute (fewer on a long stretch).
/// </summary>
public sealed record StretchStrip(long FromMs, double Seconds, IReadOnlyList<StripSpike> Spikes, double Typical, double Top, IReadOnlyList<StripTick> Ticks)
{
    /// <summary>The scale's top over the longest frame, so the longest spike doesn't touch the strip's top.</summary>
    public const double Headroom = 1.08;

    /// <summary>The steps between clock marks, minutes: the smallest that gives at most <see cref="MostSteps"/> of them.</summary>
    private static readonly int[] Steps = [1, 3, 5, 10, 15, 30, 60, 120];
    private const int MostSteps = 10;

    /// <summary>The strip of a stretch's stutters (<paramref name="each"/>, the longest first) from <paramref name="fromMs"/> to <paramref name="toMs"/> (Unix ms).</summary>
    public static StretchStrip Of(IReadOnlyList<StutterOne> each, long fromMs, long toMs)
    {
        long start = Math.Min(fromMs, toMs) / 60_000 * 60_000;
        long end = Math.Max(fromMs, toMs) / 60_000 * 60_000 + 60_000;
        double seconds = (end - start) / 1000.0;
        var spikes = each.Select((o, i) => new StripSpike(i, Math.Clamp((o.AtMs - start) / 1000.0, 0, seconds), o.Ms, o.UsualMs)).ToList();
        double typical = Median(each.Select(o => o.UsualMs).Where(u => u > 0));
        double longest = each.Count == 0 ? 0 : each.Max(o => o.Ms);
        double top = Math.Max(1, Math.Max(longest, typical) * Headroom);
        return new StretchStrip(start, seconds, spikes, typical, top, Marks(start, seconds));
    }

    /// <inheritdoc cref="Of(IReadOnlyList{StutterOne}, long, long)"/>
    public static StretchStrip Of(StutterStretch stretch) => Of(stretch.Each, stretch.FromMs, stretch.ToMs);

    /// <summary>The middle of the figures (the average of the two middle ones for an even count); 0 with none.</summary>
    internal static double Median(IEnumerable<double> values)
    {
        var list = values.Order().ToList();
        if (list.Count == 0) return 0;
        int mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
    }

    /// <summary>Minutes between clock marks on a strip <paramref name="seconds"/> long: every minute up to 10, every 3 up to 30, then 5, 10, 15…</summary>
    public static int StepMinutes(double seconds)
    {
        double minutes = seconds / 60;
        return Steps.FirstOrDefault(s => minutes / s <= MostSteps, Steps[^1]);
    }

    /// <summary>The clock marks from the strip's start, every <see cref="StepMinutes"/>, the last at its end where the step meets it.</summary>
    private static List<StripTick> Marks(long startMs, double seconds)
    {
        int step = StepMinutes(seconds);
        var marks = new List<StripTick>();
        for (int m = 0; m * 60 <= seconds + 0.5; m += step)
            marks.Add(new StripTick(m * 60, TimeText.At(TimeUtil.FromUnix(startMs / 1000 + m * 60), "h:mm")));
        return marks;
    }

    /// <summary>A spike's width, px: thinner as they crowd (about 4 under 30, 2.5 under 120, 2 above).</summary>
    public static double SpikeWidth(int count) => count < 30 ? 4 : count < 120 ? 2.5 : 2;

    /// <summary>
    /// The spike the pointer is on (<paramref name="at"/>, seconds from the start): the nearest, a longer one winning a near
    /// tie (each ms of frame counts as 0.02 px nearer, as the mock-up picks), on a strip drawn <paramref name="pxPerSecond"/>
    /// wide. Its index in <see cref="Spikes"/>; -1 with none.
    /// </summary>
    public int Nearest(double at, double pxPerSecond)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < Spikes.Count; i++)
        {
            double d = Math.Abs(Spikes[i].At - at) * pxPerSecond - Spikes[i].Ms * 0.02;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Over the strip, right: "131 stutters · usual frame 12 ms".</summary>
    public string Caption
    {
        get
        {
            string count = Spikes.Count == 1 ? Lang.T("1 stutter") : Lang.T($"{Spikes.Count:N0} stutters");
            return Typical > 0 ? Lang.T($"{count} · usual frame {Typical:0} ms") : count;
        }
    }

    /// <summary>The dashed line's label: "usual 12 ms".</summary>
    public string TypicalText => Lang.T($"usual {Typical:0} ms");

    /// <summary>A stutter's hover, under its time: "30 minutes into the play" (from <paramref name="playFrom"/>, Unix seconds).</summary>
    public static string IntoPlay(long atMs, long playFrom) => Lang.T($"{NoticeWords.Long(Math.Max(0, atMs / 1000 - playFrom))} into the play");

    /// <summary>Beside a stutter's frame in its hover: "frame, 4.5 times the usual 12 ms" (its own usual); "frame" without one.</summary>
    public static string Times(double ms, double usualMs)
    {
        if (usualMs <= 0) return Lang.T("frame");
        string times = (ms / usualMs).ToString("0.#", CultureInfo.CurrentCulture);
        return Lang.T($"frame, {times} times the usual {usualMs:0} ms");
    }
}

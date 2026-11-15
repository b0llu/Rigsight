using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>How loud the fans ran in a minute, each fan against its own range (see <see cref="FanLevels"/>).</summary>
public enum FanLevel { Silent, Quiet, Busy, Loud }

/// <summary>
/// What the Fans card's middle fact counts: the GPU fans starting up (a card whose fans stop), one fan that stops
/// starting up (a laptop's), or, with no fan that stops, the fans speeding up from quiet to busy or loud.
/// </summary>
public enum FanStartKind { GpuFans, OneFan, SpeedUps }

/// <summary>A fan's minutes over <see cref="FanLevels.RangeDays"/> days (fan_minute): its slowest while spinning, its fastest, and its minutes still and spinning.</summary>
public sealed record FanSpan(long Fan, double? Slowest, double Fastest, int StillN, int SpinN);

/// <summary>A row of the Fans page as the levels see it: one fan, or a graphics card's fans together (their speeds averaged, as they turn as one).</summary>
public sealed record LevelRow(string Name, IReadOnlyList<long> Fans, bool Gpu);

/// <summary>A row's range: its slowest to its fastest while spinning, and whether it stands still at times.</summary>
public sealed record LevelRange(double Slowest, double Fastest, bool CanStop)
{
    /// <summary>A fan whose speed moves less than <see cref="FanLevels.SetSpeedShare"/> of its fastest: at a set speed, left out.</summary>
    public bool IsSet => Fastest <= 0 || (Fastest - Slowest) / Fastest < FanLevels.SetSpeedShare;

    /// <summary>Where <paramref name="rpm"/> sits in the range, 0 (slowest) to 1 (fastest); -1 standing still.</summary>
    public double Place(double rpm) => rpm < FanList.MinSpinRpm ? -1 : Fastest <= Slowest ? 0 : Math.Clamp((rpm - Slowest) / (Fastest - Slowest), 0, 1);
}

/// <summary>
/// One recorded minute: its level, its score (the busiest fan's place in its range, 0 for a Silent minute), and each row's speed and place (null with no reading, or a row left out; a place of -1 is standing still).
/// </summary>
public sealed record LevelMinute(long Ts, FanLevel Level, double Score, double?[] Speeds, double?[] Places);

/// <summary>A stretch of minutes worked out (see <see cref="FanLevels.Of"/>).</summary>
public sealed class LevelRun
{
    public required IReadOnlyList<LevelRow> Rows { get; init; }
    public required IReadOnlyList<LevelRange?> Ranges { get; init; }
    /// <summary>The recorded minutes, in order.</summary>
    public required List<LevelMinute> Minutes { get; init; }
    public required FanStartKind Kind { get; init; }
    /// <summary>The row whose starts are counted (-1 for speed-ups).</summary>
    public required int Starter { get; init; }
    /// <summary>Some fan stops, so a minute can be Silent (with none, the legend leaves Silent out).</summary>
    public bool HasSilent => Kind != FanStartKind.SpeedUps;
    /// <summary>Each start's minute (or each speed-up's).</summary>
    public required List<long> Starts { get; init; }
    /// <summary>The longest stretch still (the starter's), or quiet (speed-ups); 0 minutes for none.</summary>
    public required (long From, int Minutes) Longest { get; init; }
}

/// <summary>
/// The fans' loudness through a day (the Fans page's Today card, approved mock-up 2026-11-14): each fan against its own
/// range, its slowest to its fastest while spinning over the last 30 days, since the same rpm sounds different on
/// fans of other sizes and models. A minute takes its busiest fan (the loudest fan is the one heard): Silent when every
/// fan that stops is still and the rest are in the slowest third, Quiet with every fan in the slowest third, Busy with
/// one in the middle third, Loud with one in the fastest third. Fans at a set speed don't move it.
/// </summary>
public static class FanLevels
{
    /// <summary>The days a fan's range is taken over.</summary>
    public const int RangeDays = 30;

    /// <summary>A fan whose speed moves less than this share of its fastest is at a set speed.</summary>
    public const double SetSpeedShare = 0.10;

    /// <summary>Minutes still over the range days for a fan to count as one that stops (a stray zero reading isn't).</summary>
    public const int StillToStop = 5;

    /// <summary>Quiet minutes before a busy or loud one for it to count as the fans speeding up.</summary>
    public const int QuietBeforeSpeedUp = 5;

    /// <summary>The least minutes an hour (a day) needs to be the loudest.</summary>
    public const int HourMinutes = 20, DayMinutes = 60;

    /// <summary>Starts are said to bunch when this many hours hold this share of them, from this many starts.</summary>
    public const int BunchHours = 3, BunchFrom = 5;
    public const double BunchShare = 0.4;

    /// <summary>A row's range from its fans' spans (a card's fans averaged); null when it never spun.</summary>
    public static LevelRange? RangeOf(IEnumerable<FanSpan> spans)
    {
        var spun = spans.Where(s => s.SpinN > 0 && s.Slowest is not null).ToList();
        if (spun.Count == 0) return null;
        return new LevelRange(spun.Average(s => s.Slowest!.Value), spun.Average(s => s.Fastest), spun.Any(s => s.StillN >= StillToStop));
    }

    /// <summary>A row counts toward the level (it has a range and isn't at a set speed).</summary>
    private static bool Counts(LevelRange? r) => r is not null && !r.IsSet;

    /// <summary>What the middle fact counts, and on which row: the GPU fans when a card's stop, else the first fan that stops.</summary>
    public static (FanStartKind Kind, int Row) StarterOf(IReadOnlyList<LevelRow> rows, IReadOnlyList<LevelRange?> ranges)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Gpu && Counts(ranges[i]) && ranges[i]!.CanStop) return (FanStartKind.GpuFans, i);
        for (int i = 0; i < rows.Count; i++)
            if (!rows[i].Gpu && Counts(ranges[i]) && ranges[i]!.CanStop) return (FanStartKind.OneFan, i);
        return (FanStartKind.SpeedUps, -1);
    }

    /// <summary>
    /// How many times a fan started up: a recorded minute standing still followed by the next minute turning (minutes
    /// with a gap between them don't join). The Fans card and the GPU fans' facts both count with this.
    /// </summary>
    public static List<long> Starts(IEnumerable<(long Ts, double Rpm)> speed)
    {
        var starts = new List<long>();
        long previous = long.MinValue;
        bool wasStill = false;
        foreach (var (ts, rpm) in speed.OrderBy(x => x.Ts))
        {
            bool still = rpm < FanList.MinSpinRpm;
            if (ts == previous + 60 && wasStill && !still) starts.Add(ts);
            (wasStill, previous) = (still, ts);
        }
        return starts;
    }

    /// <summary>The minutes of <paramref name="minutes"/> worked out for <paramref name="rows"/> with their <paramref name="ranges"/>.</summary>
    public static LevelRun Of(IReadOnlyList<LevelRow> rows, IReadOnlyList<LevelRange?> ranges, IEnumerable<FanMinute> minutes)
    {
        var rowOf = new Dictionary<long, int>();
        for (int i = 0; i < rows.Count; i++)
            foreach (long f in rows[i].Fans) rowOf.TryAdd(f, i);
        var (kind, starter) = StarterOf(rows, ranges);
        var stoppers = Enumerable.Range(0, rows.Count).Where(i => Counts(ranges[i]) && ranges[i]!.CanStop).ToList();

        var list = new List<LevelMinute>();
        foreach (var g in minutes.Where(m => rowOf.ContainsKey(m.Fan)).GroupBy(m => m.Ts).OrderBy(g => g.Key))
        {
            var speeds = new double?[rows.Count];
            foreach (var r in g.GroupBy(m => rowOf[m.Fan])) speeds[r.Key] = r.Average(m => (double)m.RpmAvg);
            var places = new double?[rows.Count];
            double busiest = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (speeds[i] is not double v || !Counts(ranges[i])) continue;
                places[i] = ranges[i]!.Place(v);
                busiest = Math.Max(busiest, places[i]!.Value);
            }
            var read = stoppers.Where(i => speeds[i] is not null).ToList();
            bool allStill = read.Count > 0 && read.All(i => speeds[i] < FanList.MinSpinRpm);
            var level = allStill && busiest < 1 / 3.0 ? FanLevel.Silent : busiest < 1 / 3.0 ? FanLevel.Quiet : busiest < 2 / 3.0 ? FanLevel.Busy : FanLevel.Loud;
            list.Add(new LevelMinute(g.Key, level, level == FanLevel.Silent ? 0 : busiest, speeds, places));
        }

        // The starts (or speed-ups) and the longest stretch still (or quiet).
        List<long> starts;
        (long From, int Minutes) longest = (0, 0);
        if (starter >= 0)
        {
            var speed = list.Where(m => m.Speeds[starter] is not null).Select(m => (m.Ts, m.Speeds[starter]!.Value)).ToList();
            starts = Starts(speed);
            longest = LongestRun(speed.Select(x => (x.Ts, x.Value < FanList.MinSpinRpm)));
        }
        else
        {
            starts = [];
            long previous = long.MinValue;
            int quietRun = 0;
            foreach (var m in list)
            {
                bool quiet = m.Level <= FanLevel.Quiet, joined = m.Ts == previous + 60;
                if (!joined) quietRun = 0;
                if (joined && !quiet && quietRun >= QuietBeforeSpeedUp) starts.Add(m.Ts);
                quietRun = quiet ? quietRun + 1 : 0;
                previous = m.Ts;
            }
            longest = LongestRun(list.Select(m => (m.Ts, m.Level <= FanLevel.Quiet)));
        }
        return new LevelRun { Rows = rows, Ranges = ranges, Minutes = list, Kind = kind, Starter = starter, Starts = starts, Longest = longest };
    }

    /// <summary>The longest run of joined minutes (one a minute apart) for which <c>On</c> holds.</summary>
    internal static (long From, int Minutes) LongestRun(IEnumerable<(long Ts, bool On)> minutes)
    {
        (long From, int Minutes) best = (0, 0);
        long previous = long.MinValue, from = 0;
        int run = 0;
        foreach (var (ts, on) in minutes)
        {
            bool joined = ts == previous + 60;
            if (!on) run = 0;
            else
            {
                if (run == 0 || !joined) (run, from) = (0, ts);
                run++;
                if (run > best.Minutes) best = (from, run);
            }
            previous = ts;
        }
        return best;
    }

    /// <summary>
    /// The slot (an hour, a day: <paramref name="slot"/> from a minute's local time) whose minutes sat highest in their
    /// busiest fan's range on average, from <paramref name="least"/> minutes; null with none.
    /// </summary>
    public static (DateTime Slot, double Score, List<LevelMinute> Minutes)? Loudest(IEnumerable<LevelMinute> minutes, Func<DateTime, DateTime> slot, int least)
    {
        var best = minutes.GroupBy(m => slot(TimeUtil.FromUnix(m.Ts))).Where(g => g.Count() >= least)
            .Select(g => (Slot: g.Key, Score: g.Average(m => m.Score), Minutes: g.ToList()))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Slot).FirstOrDefault();
        return best.Minutes is null ? null : best;
    }

    /// <summary>
    /// The app in front while the fans ran hardest in <paramref name="minutes"/>: its minutes' scores added up, so a game
    /// played for 10 minutes of the hour can win over a browser in front for the rest (said as "while", never "because").
    /// With every minute at 0, the app in front longest.
    /// </summary>
    public static long? AppWhileHardest(IEnumerable<LevelMinute> minutes, Func<long, long?> appAt)
    {
        var byApp = minutes.Select(m => (App: appAt(m.Ts), m.Score)).Where(x => x.App is not null)
            .GroupBy(x => x.App!.Value).Select(g => (App: g.Key, Score: g.Sum(x => x.Score), N: g.Count())).ToList();
        if (byApp.Count == 0) return null;
        return byApp.OrderByDescending(x => x.Score).ThenByDescending(x => x.N).First().App;
    }

    /// <summary>
    /// The <see cref="BunchHours"/> hours of the day (by local clock, never past midnight) that held the most starts, when
    /// there are <see cref="BunchFrom"/> or more and those hours hold <see cref="BunchShare"/> of them; null otherwise.
    /// </summary>
    public static (int Hour, List<long> Starts)? Bunch(IReadOnlyList<long> starts)
    {
        if (starts.Count < BunchFrom) return null;
        var hours = starts.Select(t => (Ts: t, Hour: TimeUtil.FromUnix(t).Hour)).ToList();
        (int Hour, List<long> Starts)? best = null;
        for (int h = 0; h <= 24 - BunchHours; h++)
        {
            var these = hours.Where(x => x.Hour >= h && x.Hour < h + BunchHours).Select(x => x.Ts).ToList();
            if (best is null || these.Count > best.Value.Starts.Count) best = (h, these);
        }
        return best is { } b && b.Starts.Count >= BunchShare * starts.Count ? b : null;
    }

    /// <summary>Minutes at each level (Silent, Quiet, Busy, Loud).</summary>
    public static int[] LevelMinutes(IEnumerable<LevelMinute> minutes)
    {
        var n = new int[4];
        foreach (var m in minutes) n[(int)m.Level]++;
        return n;
    }

    /// <summary>
    /// A finished day as fan_level_day keeps it (so a year can show levels and starts without a year of minutes): its
    /// minutes at each level, starts, longest stretch, the scores added up, and the app in front while the fans ran hardest.
    /// </summary>
    public static FanLevelDay DayRow(long day, LevelRun run, Func<long, long?> appAt)
    {
        var n = LevelMinutes(run.Minutes);
        var app = AppWhileHardest(run.Minutes, appAt);
        double appScore = app is long a ? run.Minutes.Where(m => appAt(m.Ts) == a).Sum(m => m.Score) : 0;
        return new FanLevelDay(day, n[0], n[1], n[2], n[3], run.Starts.Count, (int)run.Kind, run.Longest.Minutes, run.Longest.From,
            run.Minutes.Sum(m => m.Score), app, appScore);
    }

    /// <summary>The rows as the fans table has them (the agent's roll-up): each graphics card's fans as one row (by card), every other fan its own.</summary>
    public static List<LevelRow> RowsOf(IEnumerable<FanRow> fans)
    {
        var all = fans.ToList();
        static bool Gpu(FanRow f) => f.Sensor.StartsWith("/gpu", StringComparison.Ordinal);
        var rows = all.Where(Gpu).GroupBy(f => f.Hardware).Select(g => new LevelRow(g.Key, [.. g.Select(f => f.Id)], true)).ToList();
        rows.AddRange(all.Where(f => !Gpu(f)).Select(f => new LevelRow(f.Name, [f.Id], false)));
        return rows;
    }
}

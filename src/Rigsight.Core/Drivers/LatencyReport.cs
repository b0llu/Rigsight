using Rigsight.Core.Data;

namespace Rigsight.Core.Drivers;

/// <summary>The Latency page's three views of a period (issue #15): everything, only the minutes a game was in front, or only the others.</summary>
public enum LatencyFilter { All, InGames, OutsideGames }

/// <summary>A driver in the period: its longest call, its calls of 1 ms or more, its time on the CPU in all, its calls, and its usual longest (null: too soon to say).</summary>
public sealed record LatencyDriver(string Driver, DriverPart? Part, int LongestUs, int Long, long TotalUs, long Calls, int? UsualUs)
{
    public string Who => DriverWords.Who(Driver, Part);
}

/// <summary>
/// One slot of the Latency chart: an hour, or a day for periods longer than <see cref="LatencyReport.HourlyDays"/> days.
/// Null figures were not recorded (the PC off, before the trace started, or no admin rights).
/// </summary>
/// <param name="Lost">Windows dropped some driver readings in it: a longer call then could be missing.</param>
/// <param name="Others">Since 1.22.0, for its hover: the other drivers busy in it with their longest, longest first (null: not read, the game views).</param>
/// <param name="Game">Since 1.22.0: the game in front for most of it (null: none).</param>
public sealed record LatencySlot(DateTime Start, DateTime End, int? LongestUs, string Driver, int Long, bool Lost, int? WakeUs, int? WakeMedianUs,
    IReadOnlyList<LatencySlotDriver>? Others = null, long? Game = null);

/// <summary>A driver busy in a slot, and its longest call then (µs).</summary>
public sealed record LatencySlotDriver(string Driver, int Us);

/// <summary>The wake-up check over the period: checks made, the middle of its minutes' middles, the longest, and checks of 1 ms or more.</summary>
public sealed record LatencyWake(long Checks, int MedianUs, int HighestUs, int OverMs);

/// <summary>
/// The Latency page's facts for a period (issue #15, approved mock-up 2026-11-24), built once here for the page, its
/// export and the AI, from the driver tables and the app wake-up check's. Every driver is listed (the list scrolls), the
/// period's longest call is said with whose and when, and minutes in which Windows dropped readings are said, so a quiet
/// hour is never taken for a clean one. In games and Outside games read the kept minutes (every call of 0.5 ms or more):
/// their longest and counts over 1 ms are whole, their total time leaves out a driver's quiet minutes.
/// </summary>
public sealed record LatencyReport(DateTime From, DateTime To, LatencyFilter Filter, long? FirstDriverHour, (long Minute, int Us, string Driver)? Longest,
    long? LongestApp, List<LatencyDriver> Drivers, List<LatencySlot> Slots, LatencyWake? Wake, List<long> LostMinutes, long? FirstSplitHour)
{
    /// <summary>Periods up to this many days are drawn an hour at a time; longer ones a day at a time.</summary>
    public const int HourlyDays = 8;

    /// <summary>Calls of 1 ms or more, every driver together.</summary>
    public int Long => Drivers.Sum(d => d.Long);

    /// <summary>Days of driver history up to the period's end (the usual needs <see cref="DriverRules.UsualDays"/>).</summary>
    /// <summary>The first hour the app wake-up check was recorded (Unix seconds): before it, its chart has nothing to show yet.</summary>
    public long? FirstWakeHour { get; init; }

    public int HistoryDays => FirstDriverHour is long first ? Math.Max(0, (int)Math.Floor((TimeUtil.ToUnix(To) - first) / 86400.0)) : 0;

    /// <summary>Too soon for a usual: under <see cref="DriverRules.UsualDays"/> days of driver history.</summary>
    public bool TooSoon => HistoryDays < DriverRules.UsualDays;

    public bool HasDrivers => Drivers.Count > 0;

    /// <summary>Reads the period's facts.</summary>
    /// <param name="games">The apps that are games (by the person's own categories).</param>
    public static LatencyReport Read(RigsightDb db, DateTime from, DateTime to, LatencyFilter filter, IReadOnlyCollection<long> games)
    {
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
        var parts = db.GetDriverParts();
        bool hourly = (to - from).TotalDays <= HourlyDays;

        // The drivers: hours for everything (every driver, every hour), kept minutes for the game views.
        List<DriverTally> totals;
        List<(long Hour, int Us, string Driver, int Long)> hours;
        List<DriverTally>? hourRows = null;
        if (filter == LatencyFilter.All)
        {
            var rows = hourRows = db.GetDriverHours(f, t);
            totals = [.. rows.GroupBy(r => r.Driver, StringComparer.OrdinalIgnoreCase).Select(g =>
                new DriverTally(0, g.Key, g.Sum(r => r.Count), g.Sum(r => r.TotalUs), g.Max(r => r.LongestUs), g.Sum(r => r.Long)))];
            hours = [.. rows.GroupBy(r => r.Ts).OrderBy(g => g.Key).Select(g =>
            {
                var top = g.MaxBy(r => r.LongestUs)!;
                return (g.Key, top.LongestUs, top.Driver, g.Sum(r => r.Long));
            })];
        }
        else
        {
            bool inGames = filter == LatencyFilter.InGames;
            totals = db.GetDriverTotalsByGames(f, t, games, inGames);
            hours = db.GetDriverHoursByGames(f, t, games, inGames);
        }

        var drivers = totals.Where(d => d.Count > 0)
            .Select(d => new LatencyDriver(d.Driver, parts.GetValueOrDefault(d.Driver), d.LongestUs, d.Long, d.TotalUs, d.Count, db.GetDriverUsualLongest(d.Driver, f)))
            .OrderByDescending(d => d.LongestUs).ToList();

        // The period's longest call, whose and when, and what was in front (in the view's minutes).
        (long Minute, int Us, string Driver)? longest = null;
        if (drivers.Count > 0)
        {
            var top = drivers[0];
            if (filter == LatencyFilter.All) longest = db.GetDriverLongestMinute(f, t, top.Driver);
            else
            {
                // The game views: the longest of that driver's kept minutes in the view.
                var front = db.GetFrontApps(f, t);
                var gameSet = games as IReadOnlySet<long> ?? games.ToHashSet();
                var best = db.GetDriverMinutes(f, t, top.Driver)
                    .Where(m => (front.TryGetValue(m.Ts, out long app) && gameSet.Contains(app)) == (filter == LatencyFilter.InGames))
                    .MaxBy(m => m.LongestUs);
                if (best is not null) longest = (best.Ts, best.LongestUs, best.Driver);
            }
            longest ??= (0, top.LongestUs, top.Driver);
        }
        long? longestApp = longest is { Minute: > 0 } l && db.GetFrontApps(l.Minute, l.Minute + 60).TryGetValue(l.Minute, out long a) ? a : null;

        // The wake-up check: its hours for everything, its minutes for the game views.
        var wakeRows = filter == LatencyFilter.All ? db.GetWake(f, t) : FilterMinutes(db, db.GetWake(f, t, minutes: true), f, t, games, filter == LatencyFilter.InGames);
        LatencyWake? wake = wakeRows.Count == 0 ? null : new(wakeRows.Sum(w => (long)w.Checks), wakeRows.Select(w => w.MedianUs).Order().ElementAt(wakeRows.Count / 2),
            wakeRows.Max(w => w.HighestUs), wakeRows.Sum(w => w.OverMs));

        var lost = db.GetDriverMissed(f, t).Order().ToList();
        var slots = WithHover(BuildSlots(from, to, hourly, hours, wakeRows, lost), hourRows, db.GetFrontApps(f, t), games);
        return new LatencyReport(from, to, filter, db.FirstDriverHour(), longest, longestApp, drivers, slots, wake, lost, db.FirstSplitHour()) { FirstWakeHour = db.FirstWakeHour() };
    }

    // The wake-up minutes with a game in front, or the others.
    private static List<WakeTally> FilterMinutes(RigsightDb db, List<WakeTally> minutes, long f, long t, IReadOnlyCollection<long> games, bool inGames)
    {
        if (minutes.Count == 0) return minutes;
        var front = db.GetFrontApps(f, t);
        var gameSet = games as IReadOnlySet<long> ?? games.ToHashSet();
        return [.. minutes.Where(m => (front.TryGetValue(m.Ts, out long app) && gameSet.Contains(app)) == inGames)];
    }

    /// <summary>
    /// What each slot's hover says beyond its own figures (1.22.0): the other drivers busy in it with their longest (from
    /// every driver's hours; none in the game views, which keep only the longest), and the game in front for most of it.
    /// </summary>
    public static List<LatencySlot> WithHover(List<LatencySlot> slots, IReadOnlyList<DriverTally>? hourRows, IReadOnlyDictionary<long, long> front,
        IReadOnlyCollection<long> games)
    {
        if (slots.Count == 0) return slots;
        var gameSet = games as IReadOnlySet<long> ?? games.ToHashSet();
        var minutes = front.Where(m => gameSet.Contains(m.Value)).OrderBy(m => m.Key).ToList();
        var rows = hourRows?.OrderBy(r => r.Ts).ToList();
        int ri = 0, mi = 0;
        var result = new List<LatencySlot>(slots.Count);
        foreach (var slot in slots)
        {
            long s = TimeUtil.ToUnix(slot.Start), e = TimeUtil.ToUnix(slot.End);
            List<LatencySlotDriver>? others = null;
            if (rows is not null)
            {
                var longest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                while (ri < rows.Count && rows[ri].Ts < e)
                {
                    var r = rows[ri++];
                    if (r.Ts < s || r.Driver.Equals(slot.Driver, StringComparison.OrdinalIgnoreCase)) continue;
                    longest[r.Driver] = Math.Max(longest.GetValueOrDefault(r.Driver), r.LongestUs);
                }
                others = [.. longest.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => new LatencySlotDriver(kv.Key, kv.Value))];
            }
            var count = new Dictionary<long, int>();
            while (mi < minutes.Count && minutes[mi].Key < e)
            {
                var m = minutes[mi++];
                if (m.Key >= s) count[m.Value] = count.GetValueOrDefault(m.Value) + 1;
            }
            long? game = count.Count == 0 ? null : count.MaxBy(kv => kv.Value).Key;
            result.Add(slot with { Others = others, Game = game });
        }
        return result;
    }

    /// <summary>Every slot of the period, an hour (or a day) at a time, the ones with nothing recorded left empty.</summary>
    public static List<LatencySlot> BuildSlots(DateTime from, DateTime to, bool hourly, IReadOnlyList<(long Hour, int Us, string Driver, int Long)> hours,
        IReadOnlyList<WakeTally> wake, IReadOnlyList<long> lost)
    {
        var slots = new List<LatencySlot>();
        // Hours as recorded start on the hour of the clock Windows keeps (at :30 where the time zone is half an hour off):
        // each slot takes the recorded hours that start inside it.
        var starts = new List<DateTime>();
        if (hourly) for (var h = from; h < to; h = h.AddHours(1)) starts.Add(h);
        else for (var d = from.Date; d < to; d = d.AddDays(1)) starts.Add(d);
        int hi = 0, wi = 0, li = 0;
        var lostSet = lost.ToHashSet();
        var hoursByTime = hours.OrderBy(h => h.Hour).ToList();
        var wakeByTime = wake.OrderBy(w => w.Ts).ToList();
        for (int i = 0; i < starts.Count; i++)
        {
            DateTime start = starts[i], end = i + 1 < starts.Count ? starts[i + 1] : to;
            long s = TimeUtil.ToUnix(start), e = TimeUtil.ToUnix(end);
            int? longest = null;
            string driver = "";
            int longCalls = 0;
            bool lostHour = false;
            while (hi < hoursByTime.Count && hoursByTime[hi].Hour < e)
            {
                var h = hoursByTime[hi++];
                if (h.Hour < s) continue;
                if (longest is null || h.Us > longest) (longest, driver) = (h.Us, h.Driver);
                longCalls += h.Long;
                // Dropped readings count against the recorded hour itself (it starts at :30 where the time zone is half an hour off).
                if (lostSet.Any(m => m >= h.Hour && m < h.Hour + 3600)) lostHour = true;
            }
            int? wakeTop = null;
            var medians = new List<int>();
            while (wi < wakeByTime.Count && wakeByTime[wi].Ts < e)
            {
                var w = wakeByTime[wi++];
                if (w.Ts < s) continue;
                wakeTop = Math.Max(wakeTop ?? 0, w.HighestUs);
                medians.Add(w.MedianUs);
            }
            bool wasLost = lostHour;
            while (li < lost.Count && lost[li] < e)
                if (lost[li++] >= s && longest is null) wasLost = true;
            medians.Sort();
            slots.Add(new LatencySlot(start, end, longest, driver, longCalls, wasLost, wakeTop, medians.Count == 0 ? null : medians[medians.Count / 2]));
        }
        return slots;
    }
}

using Rigsight.Core.Data;

namespace Rigsight.Core.Stability;

/// <summary>
/// Motherboard fans set to another speed while the PC was off: in the BIOS, the only place a fixed fan speed changes
/// across a restart. Found from the fan speeds already recorded each minute: a fan that came back from a restart at a
/// speed far from the one it held before, where at least one side was a set speed (steady minute to minute; a fan on a
/// curve moves with the temperature, and a curve fan after a cold start is not a setting). Several fans changed at one
/// restart are one change. A fan slowing while the PC is on is never this: that is the fan warning's to say.
/// </summary>
public static class FanSettings
{
    /// <summary>A gap longer than this between a fan's minutes is the PC off, asleep or restarting.</summary>
    internal const long GapSeconds = 120;

    /// <summary>Minutes read on each side of a restart: the last before it, and after the first few (fans spin up at power-on).</summary>
    internal const int EdgeMinutes = 30, SkipAfter = 3, MinEdgeMinutes = 15;

    /// <summary>A set speed: the middle 80% of its minutes within this share of their median.</summary>
    internal const double SteadySpread = 0.03;

    /// <summary>A speed this much different, by share and by rpm, is another setting.</summary>
    internal const double MinShare = 0.15;
    internal const int MinRpm = 150;

    /// <summary>
    /// The changes found among <paramref name="minutes"/> (any order), each dated when the PC was on again. Only fans that
    /// aren't a graphics card's: its fans follow the card's own curve, set from software while the PC is on.
    /// </summary>
    public static List<SystemChange> Find(IReadOnlyList<FanRow> fans, IReadOnlyList<FanMinute> minutes)
    {
        var board = fans.Where(f => !f.Sensor.StartsWith("/gpu", StringComparison.Ordinal)).ToDictionary(f => f.Id);
        // Each restart's changed fans, by the time the PC was on again.
        var byRestart = new SortedDictionary<long, List<(FanRow Fan, Level Was, Level Now)>>();
        foreach (var group in minutes.Where(m => board.ContainsKey(m.Fan)).GroupBy(m => m.Fan))
        {
            var fan = board[group.Key];
            var runs = Runs([.. group.OrderBy(m => m.Ts)]);
            // A run too short to read (a minute or two in the BIOS, a start that went straight back down) is left out:
            // the restart that matters is from the run before it to the run after.
            var levels = runs.Select(r => (Start: r[0].Ts, Head: LevelOf(r.Skip(SkipAfter).Take(EdgeMinutes)), Tail: LevelOf(r.TakeLast(EdgeMinutes))))
                .Where(l => l.Head is not null || l.Tail is not null).ToList();
            for (int i = 1; i < levels.Count; i++)
            {
                if (levels[i - 1].Tail is not { } was || levels[i].Head is not { } now) continue;
                double change = now.Rpm - was.Rpm;
                if (Math.Abs(change) < MinRpm || Math.Abs(change) < was.Rpm * MinShare) continue;
                // A set speed on at least one side, and held: before, over the run before as well; after, to the end of
                // its own run (a curve fan at a cold start reads low and steady for a while, then climbs).
                bool wasSet = was.Steady && i >= 2 && levels[i - 2].Tail is { Steady: true } older && Near(older.Rpm, was.Rpm);
                bool nowSet = now.Steady && levels[i].Tail is { Steady: true } end && Near(end.Rpm, now.Rpm);
                if (!wasSet && !nowSet) continue;
                if (!byRestart.TryGetValue(levels[i].Start, out var list)) byRestart[levels[i].Start] = list = [];
                list.Add((fan, was, now));
            }
        }

        // Fans changed at the same restart (each fan's minutes start within a couple of minutes of the others): one change.
        var groups = new List<(long Start, List<(FanRow Fan, Level Was, Level Now)> Fans)>();
        foreach (var (start, list) in byRestart)
        {
            if (groups.Count > 0 && start - groups[^1].Start <= GapSeconds) groups[^1].Fans.AddRange(list);
            else groups.Add((start, [.. list]));
        }
        return [.. groups.Select(g => Change(TimeUtil.FromUnix(g.Start), g.Fans))];
    }

    private static SystemChange Change(DateTime at, List<(FanRow Fan, Level Was, Level Now)> fans)
    {
        fans = [.. fans.GroupBy(f => f.Fan.Id).Select(g => g.First()).OrderBy(f => f.Fan.Name, StringComparer.Ordinal)];
        // The subject carries each fan's id ("fans|2|3|"), so a fan's usual can start again from its own change.
        string subject = "fans|" + string.Concat(fans.Select(f => $"{f.Fan.Id}|"));
        string what = string.Join(" · ", fans.Select(f => f switch
        {
            { Was.Steady: true, Now.Steady: true } => $"{f.Fan.Name} {f.Was.Rpm:N0} → {f.Now.Rpm:N0} rpm",
            { Was.Steady: true } => $"{f.Fan.Name} from a set {f.Was.Rpm:N0} rpm to following the temperature",
            _ => $"{f.Fan.Name} now set at {f.Now.Rpm:N0} rpm",
        }));
        return new SystemChange(at, ChangeKind.Fans, Lang.Key("Fan speeds changed while the PC was off"))
        {
            Subject = subject,
            Was = string.Join(" · ", fans.Select(f => $"{f.Fan.Name} {f.Was.Rpm:N0}")),
            Now = $"{what} · likely a BIOS setting",
        };
    }

    /// <summary>A fan's speed over some minutes: the median, and whether it was a set speed.</summary>
    private sealed record Level(double Rpm, bool Steady);

    private static Level? LevelOf(IEnumerable<FanMinute> minutes)
    {
        var rpm = minutes.Select(m => (double)m.RpmAvg).Where(r => r > 0).OrderBy(r => r).ToList();
        if (rpm.Count < MinEdgeMinutes) return null;
        double median = rpm[rpm.Count / 2], low = rpm[rpm.Count / 10], high = rpm[rpm.Count - 1 - rpm.Count / 10];
        return new Level(median, (high - low) <= median * SteadySpread);
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= Math.Max(a, b) * SteadySpread * 2;

    /// <summary>A fan's minutes cut at every gap longer than <see cref="GapSeconds"/>.</summary>
    private static List<List<FanMinute>> Runs(List<FanMinute> minutes)
    {
        var runs = new List<List<FanMinute>>();
        foreach (var m in minutes)
        {
            if (runs.Count == 0 || m.Ts - runs[^1][^1].Ts > GapSeconds) runs.Add([]);
            runs[^1].Add(m);
        }
        return runs;
    }
}

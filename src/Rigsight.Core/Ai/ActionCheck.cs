using Rigsight.Core.Apps;
using Rigsight.Core.Settings;
using Rigsight.Core.Data;

namespace Rigsight.Core.Ai;

/// <summary>What a Check again looks at: the readings that should move once something is done.</summary>
public enum CheckWhat { Fans, Temps, Gpu, Cpu, Memory, Internet }

/// <summary>One reading before and after, said as the page would: "1,640 rpm" to "1,120 rpm", and whether it went the good way.</summary>
public sealed record CheckRow(string Label, string Before, string After, string Change, bool? Better);

/// <summary>
/// A Check again's answer: the readings over the minutes before it was pressed against the same number of minutes
/// after, from RigSight's own records (no question asked of the model). <see cref="Fair"/> is false when the two
/// stretches weren't alike (a game in front in one of them), and <see cref="Note"/> then says why.
/// </summary>
public sealed record CheckResult(IReadOnlyList<CheckRow> Rows, bool Fair, string? Note)
{
    public bool HasRows => Rows.Count > 0;
}

/// <summary>
/// Compares like with like: the same length of time either side of the moment Check again was pressed, read from the
/// minutes the agent recorded. Too few minutes on either side (the agent stopped, the PC slept) says so instead of a
/// guess, and a game in front on one side only makes the comparison unfair, said as such (the insight rules: like for
/// like or nothing).
/// </summary>
public static class ActionCheck
{
    /// <summary>The words for <paramref name="what"/> in a marker ("fans", "temps"…); null for one RigSight doesn't check.</summary>
    public static CheckWhat? Parse(string? what) => what?.Trim().ToLowerInvariant() switch
    {
        "fans" or "fan" => CheckWhat.Fans,
        "temps" or "temp" or "temperatures" or "heat" => CheckWhat.Temps,
        "gpu" or "graphics" => CheckWhat.Gpu,
        "cpu" or "processor" => CheckWhat.Cpu,
        "memory" or "ram" => CheckWhat.Memory,
        "internet" or "network" or "download" => CheckWhat.Internet,
        _ => null,
    };

    public static string Words(CheckWhat what) => what switch
    {
        CheckWhat.Fans => Lang.T("fan speeds"),
        CheckWhat.Temps => Lang.T("temperatures"),
        CheckWhat.Gpu => Lang.T("the graphics card"),
        CheckWhat.Cpu => Lang.T("the CPU"),
        CheckWhat.Memory => Lang.T("memory"),
        _ => Lang.T("internet use"),
    };

    /// <summary>The minutes either side of <paramref name="at"/>, compared; null while the after side hasn't passed yet.</summary>
    public static CheckResult? Compare(RigsightDb db, DateTime at, int minutes, CheckWhat what, DateTime? now = null)
    {
        if ((now ?? DateTime.Now) < at.AddMinutes(minutes)) return null;
        long mid = TimeUtil.ToUnix(at), span = minutes * 60L;
        long f0 = mid - span, t1 = mid + span;
        var before = db.GetMinutes(f0, mid);
        var after = db.GetMinutes(mid, t1);
        int need = Math.Max(2, minutes / 2);
        if (before.Count < need || after.Count < need)
            return new CheckResult([], false, Lang.T("Not enough was recorded on both sides to compare. RigSight's agent may not have been running, or the PC slept."));

        var apps = db.LoadApps().ToDictionary(a => a.Id);
        bool Game(SystemMinute m) => m.FgApp is long id && apps.TryGetValue(id, out var app) && app.Category == AppCategory.Game;
        double gameBefore = before.Count(Game) / (double)before.Count, gameAfter = after.Count(Game) / (double)after.Count;
        string? note = Math.Abs(gameBefore - gameAfter) > 0.5
            ? Lang.T("A game was in front on one side only, so this isn't a like-for-like comparison.") : null;

        var rows = new List<CheckRow>();
        switch (what)
        {
            case CheckWhat.Fans:
                var names = db.GetFans().ToDictionary(x => x.Id, x => x.Name);
                var fb = db.GetFanMinutes(f0, mid).GroupBy(m => m.Fan).ToDictionary(g => g.Key, g => g.Average(m => (double)m.RpmAvg));
                var fa = db.GetFanMinutes(mid, t1).GroupBy(m => m.Fan).ToDictionary(g => g.Key, g => g.Average(m => (double)m.RpmAvg));
                // The fans that were spinning, fastest first: at most four rows.
                foreach (var (fan, rpm) in fb.Where(kv => kv.Value >= 100 && fa.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).Take(4))
                    rows.Add(Row(names.GetValueOrDefault(fan) ?? Lang.T("A fan"), rpm, fa[fan], v => Lang.T($"{v:N0} rpm"), smallest: Math.Max(60, rpm * 0.05), lowerIsBetter: true));
                break;
            case CheckWhat.Temps:
                Add(rows, Lang.T("CPU temperature"), before, after, m => m.CpuTemp, v => $"{v:0}°", 2);
                Add(rows, Lang.T("GPU temperature"), before, after, m => m.GpuTemp, v => $"{v:0}°", 2);
                break;
            case CheckWhat.Gpu:
                Add(rows, Lang.T("Graphics card busy"), before, after, m => m.GpuLoad, v => $"{v:0}%", 5);
                Add(rows, Lang.T("GPU temperature"), before, after, m => m.GpuTemp, v => $"{v:0}°", 2);
                break;
            case CheckWhat.Cpu:
                Add(rows, Lang.T("CPU busy"), before, after, m => m.CpuLoad, v => $"{v:0}%", 5);
                Add(rows, Lang.T("CPU temperature"), before, after, m => m.CpuTemp, v => $"{v:0}°", 2);
                break;
            case CheckWhat.Memory:
                Add(rows, Lang.T("Memory in use"), before, after, m => m.RamUsed, v => $"{v:0.0} GB", 0.3);
                Add(rows, Lang.T("Closest to running out"), before, after, m => m.CommitPct, v => $"{v:0}%", 3);
                break;
            case CheckWhat.Internet:
                var nb = db.GetNetMinutes(f0, mid);
                var na = db.GetNetMinutes(mid, t1);
                if (nb.Count >= need && na.Count >= need)
                {
                    double db0 = nb.Average(m => (double)m.Down), da = na.Average(m => (double)m.Down);
                    rows.Add(Row(Lang.T("Downloaded a minute"), db0, da, v => Units.Bytes(v), smallest: Math.Max(1 << 20, db0 * 0.2), lowerIsBetter: true));
                }
                break;
        }
        if (rows.Count == 0) return new CheckResult([], false, Lang.T("RigSight has no readings for this on both sides to compare."));
        return new CheckResult(rows, note is null, note);
    }

    private static void Add(List<CheckRow> rows, string label, List<SystemMinute> before, List<SystemMinute> after, Func<SystemMinute, double?> read, Func<double, string> say, double smallest)
    {
        var b = before.Select(read).OfType<double>().ToList();
        var a = after.Select(read).OfType<double>().ToList();
        if (b.Count == 0 || a.Count == 0) return;
        rows.Add(Row(label, b.Average(), a.Average(), say, smallest, lowerIsBetter: true));
    }

    /// <summary>A change smaller than <paramref name="smallest"/> is "about the same": readings wobble minute to minute.</summary>
    private static CheckRow Row(string label, double before, double after, Func<double, string> say, double smallest, bool lowerIsBetter)
    {
        double d = after - before;
        if (Math.Abs(d) < smallest) return new CheckRow(label, say(before), say(after), Lang.T("about the same"), null);
        bool down = d < 0;
        string change = down ? Lang.T($"{say(-d)} lower") : Lang.T($"{say(d)} higher");
        return new CheckRow(label, say(before), say(after), change, down == lowerIsBetter);
    }
}

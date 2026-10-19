namespace Rigsight.Core.Stability;

/// <summary>
/// What stood out in the seconds around a crash, from its close-up (<see cref="CloseUp"/>): what a minute's average
/// hides, such as a hot spot rising 6° in the last half minute, power jumping for the last few seconds, graphics memory
/// filling up, Windows running out of memory; and what came straight after.
/// Fixed rules, like the findings', so the same seconds always say the same.
/// </summary>
public static class CloseUpWords
{
    /// <summary>How far before the crash a rise is looked for, and the stretch before that it is measured against (seconds).</summary>
    public const int LastSec = 60, BaseSec = 60;

    /// <summary>Least rise in the last minute to be said (°C): the CPU's moves faster, so it needs more.</summary>
    public const double CpuRise = 8, GpuRise = 5;

    /// <summary>A rise is said only where it ended at least this warm (°C): 55° to 65° at the desktop means nothing.</summary>
    public const double WarmEnough = 70;

    /// <summary>Power: the last seconds against before, at least this much more and this many watts.</summary>
    public const double PowerTimes = 1.3, PowerWatts = 40;

    /// <summary>Graphics memory: at least this much more in the last minute than over the minutes before (MB).</summary>
    public const double VramRiseMb = 1024;

    public static List<StoodOut> Find(IReadOnlyList<CloseUpPoint> points, long crashTs, Func<long, string> nameOf, long? crashedApp = null)
    {
        var said = new List<StoodOut>();
        long end = crashTs * 1000 + 999;
        var before = points.Where(p => p.T <= end).OrderBy(p => p.T).ToList();
        if (before.Count < 3) return said;
        long last = before[^1].T;

        // Only one line for the GPU: its hot spot says more than its edge, when both rose.
        if (!Rise(said, before, last, p => p.GpuHot, "GPU hot spot", GpuRise))
            Rise(said, before, last, p => p.GpuTemp, "GPU", GpuRise);
        Rise(said, before, last, p => p.CpuTemp, "CPU", CpuRise);
        Power(said, before, last, p => p.GpuPower, "GPU power");
        Power(said, before, last, p => p.CpuPower, "CPU power");
        Vram(said, before, last);

        var lastMinute = Window(before, last - LastSec * 1000L, last);
        if (lastMinute.Max(p => p.Commit) is double commit && commit >= 95)
            said.Add(new StoodOut(Lang.T($"Windows was nearly out of memory in the last minute: {commit:0}% of what it can promise was promised to apps."), true));

        if (said.Count == 0)
            said.Add(new StoodOut(Lang.T("Nothing climbing in the seconds before: no jump in temperature, power or memory."), false, IsCalm: true));

        if (After(points, crashTs, nameOf, crashedApp) is { } after) said.Insert(said.FindIndex(s => s.IsCalm) is int calm and >= 0 ? calm : said.Count, after);
        return said;
    }

    private static List<CloseUpPoint> Window(List<CloseUpPoint> points, long from, long to) => [.. points.Where(p => p.T >= from && p.T <= to)];

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? double.NaN : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>"in the last 30 seconds", from when it set off to the last reading.</summary>
    private static string Since(long from, long last)
    {
        int s = (int)Math.Max(2, Math.Round((last - from) / 1000.0));
        return s >= 90 ? Lang.T($"in the last {Math.Round(s / 60.0):0} minutes") : Lang.T($"in the last {s} seconds");
    }

    /// <summary>Which reading rose, by the name <see cref="Find"/> gives it: each a whole sentence of its own.</summary>
    private static string RoseText(string what, string from, string to, string since) => what switch
    {
        "GPU hot spot" => Lang.T($"GPU hot spot rose from {from} to {to} {since}."),
        "GPU" => Lang.T($"GPU rose from {from} to {to} {since}."),
        _ => Lang.T($"CPU rose from {from} to {to} {since}."),
    };

    /// <summary>Says the reading's rise, if it rose enough; whether it did.</summary>
    private static bool Rise(List<StoodOut> said, List<CloseUpPoint> before, long last, Func<CloseUpPoint, double?> value, string what, double least)
    {
        var late = Window(before, last - LastSec * 1000L, last).Where(p => value(p) is not null).ToList();
        var early = Window(before, last - (LastSec + BaseSec) * 1000L, last - LastSec * 1000L - 1).Where(p => value(p) is not null).ToList();
        if (late.Count < 3 || early.Count < 5) return false;
        double from = Median(early.Select(p => value(p)!.Value));
        var peak = late.MaxBy(p => value(p)!.Value)!;
        double to = value(peak)!.Value;
        if (to - from < least || to < WarmEnough) return false;
        var start = late.First(p => value(p) >= from + (to - from) / 3);
        string text = RoseText(what, Units.TempShort(from), Units.TempShort(to), Since(start.T, last));
        // What a minute's average made of it, where that hid most of the rise.
        long minute = last / 60_000 * 60_000;
        var own = Window(before, minute, last).Select(p => value(p)).OfType<double>().ToList();
        if (own.Count >= 3 && own.Average() <= to - 3) text += " " + Lang.T($"The minute's average showed only {Units.TempShort(own.Average())}.");
        said.Add(new StoodOut(text, true));
        return true;
    }

    private static void Power(List<StoodOut> said, List<CloseUpPoint> before, long last, Func<CloseUpPoint, double?> value, string what)
    {
        // The last 10 seconds against the two minutes before: a jump that short is what a minute's average hides.
        var late = Window(before, last - 10_000, last).Where(p => value(p) is not null).ToList();
        var early = Window(before, last - 120_000, last - 10_001).Where(p => value(p) is not null).ToList();
        if (late.Count < 3 || early.Count < 5) return;
        double from = Median(early.Select(p => value(p)!.Value)), to = late.Average(p => value(p)!.Value);
        if (to < from * PowerTimes || to - from < PowerWatts) return;
        var start = Window(before, last - 60_000, last).First(p => value(p) >= from + (to - from) / 2);
        long seconds = Math.Max(2, (last - start.T) / 1000);
        said.Add(new StoodOut(what == "GPU power"
            ? Lang.T($"GPU power jumped to about {to:0} W for the last {seconds} seconds, from about {from:0} W.")
            : Lang.T($"CPU power jumped to about {to:0} W for the last {seconds} seconds, from about {from:0} W."), true));
    }

    private static void Vram(List<StoodOut> said, List<CloseUpPoint> before, long last)
    {
        var late = Window(before, last - LastSec * 1000L, last).Where(p => p.Vram is not null).ToList();
        var early = Window(before, last - 5 * 60_000, last - LastSec * 1000L - 1).Where(p => p.Vram is not null).ToList();
        if (late.Count < 3 || early.Count < 5) return;
        double from = Median(early.Select(p => p.Vram!.Value));
        var peak = late.MaxBy(p => p.Vram!.Value)!;
        if (peak.Vram!.Value - from < VramRiseMb) return;
        var start = late.First(p => p.Vram >= from + (peak.Vram.Value - from) / 3);
        bool full = peak.VramTotal is double total && total > 0 && peak.Vram.Value >= total * 0.95;
        string since = Since(start.T, last);
        double fromGb = from / 1024, toGb = peak.Vram.Value / 1024;
        said.Add(new StoodOut(peak.VramTotal is double all && all > 0
            ? Lang.T($"Graphics memory climbed from {fromGb:0.0} GB to {toGb:0.0} GB of {all / 1024:0} GB {since}.")
            : Lang.T($"Graphics memory climbed from {fromGb:0.0} GB to {toGb:0.0} GB {since}."), full));
    }

    /// <summary>What came after an app's crash, in its seconds: the graphics card going quiet, the app back in front.</summary>
    private static StoodOut? After(IReadOnlyList<CloseUpPoint> points, long crashTs, Func<long, string> nameOf, long? crashedApp)
    {
        long at = crashTs * 1000;
        var after = points.Where(p => p.T > at + 999).OrderBy(p => p.T).ToList();
        if (after.Count < 3) return null;
        double? gpuBefore = points.Where(p => p.T <= at + 999 && p.T >= at - 20_000).Select(p => p.GpuLoad).OfType<double>().DefaultIfEmpty(double.NaN).Average();
        double? gpuAfter = after.Where(p => p.T <= at + 20_000).Select(p => p.GpuLoad).OfType<double>().DefaultIfEmpty(double.NaN).Average();
        bool quiet = gpuBefore >= 50 && gpuAfter < 20;
        string? name = null, later = null;
        if (crashedApp is long app && after.FirstOrDefault(p => p.Front == app && p.T > at + 10_000) is { } back)
            (name, later) = (nameOf(app), CrashFindings.Words((back.T - at) / 1000.0));
        string? text = (quiet, name) switch
        {
            (true, not null) => Lang.T($"After: the GPU went from {gpuBefore:0}% to {gpuAfter:0}% at once, and {name} was back in front {later} later."),
            (true, null) => Lang.T($"After: the GPU went from {gpuBefore:0}% to {gpuAfter:0}% at once."),
            (false, not null) => Lang.T($"After: {name} was back in front {later} later."),
            _ => null,
        };
        return text is null ? null : new StoodOut(text, false);
    }
}

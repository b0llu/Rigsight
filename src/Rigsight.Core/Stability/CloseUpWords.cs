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

        Rise(said, before, last, p => p.GpuHot, "GPU hot spot", GpuRise);
        Rise(said, before, last, p => p.GpuTemp, "GPU", GpuRise);
        Rise(said, before, last, p => p.CpuTemp, "CPU", CpuRise);
        Power(said, before, last, p => p.GpuPower, "GPU power");
        Power(said, before, last, p => p.CpuPower, "CPU power");
        Vram(said, before, last);

        var lastMinute = Window(before, last - LastSec * 1000L, last);
        if (lastMinute.Max(p => p.Commit) is double commit && commit >= 95)
            said.Add(new StoodOut($"Windows was nearly out of memory in the last minute: {commit:0}% of what it can promise was promised to apps.", true));

        if (said.Count == 0)
            said.Add(new StoodOut("Nothing climbing in the seconds before: no jump in temperature, power or memory.", false, IsCalm: true));

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
        return s >= 90 ? $"in the last {Math.Round(s / 60.0):0} minutes" : $"in the last {s} seconds";
    }

    private static void Rise(List<StoodOut> said, List<CloseUpPoint> before, long last, Func<CloseUpPoint, double?> value, string what, double least)
    {
        var late = Window(before, last - LastSec * 1000L, last).Where(p => value(p) is not null).ToList();
        var early = Window(before, last - (LastSec + BaseSec) * 1000L, last - LastSec * 1000L - 1).Where(p => value(p) is not null).ToList();
        if (late.Count < 3 || early.Count < 5) return;
        double from = Median(early.Select(p => value(p)!.Value));
        var peak = late.MaxBy(p => value(p)!.Value)!;
        double to = value(peak)!.Value;
        if (to - from < least || to < WarmEnough) return;
        // Only one line for the GPU: its hot spot says more than its edge, when both rose.
        if (what == "GPU" && said.Any(s => s.Text.StartsWith("GPU hot spot", StringComparison.Ordinal))) return;
        var start = late.First(p => value(p) >= from + (to - from) / 3);
        string text = $"{what} rose from {Units.TempShort(from)} to {Units.TempShort(to)} {Since(start.T, last)}.";
        // What a minute's average made of it, where that hid most of the rise.
        long minute = last / 60_000 * 60_000;
        var own = Window(before, minute, last).Select(p => value(p)).OfType<double>().ToList();
        if (own.Count >= 3 && own.Average() <= to - 3) text += $" The minute's average showed only {Units.TempShort(own.Average())}.";
        said.Add(new StoodOut(text, true));
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
        said.Add(new StoodOut($"{what.Substring(0, 1).ToUpperInvariant()}{what[1..]} jumped to about {to:0} W for the last {Math.Max(2, (last - start.T) / 1000)} seconds, from about {from:0} W.", true));
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
        said.Add(new StoodOut($"Graphics memory climbed from {from / 1024:0.0} GB to {peak.Vram.Value / 1024:0.0} GB"
            + (peak.VramTotal is double all && all > 0 ? $" of {all / 1024:0} GB" : "") + $" {Since(start.T, last)}.", full));
    }

    /// <summary>What came after an app's crash, in its seconds: the graphics card going quiet, the app back in front.</summary>
    private static StoodOut? After(IReadOnlyList<CloseUpPoint> points, long crashTs, Func<long, string> nameOf, long? crashedApp)
    {
        long at = crashTs * 1000;
        var after = points.Where(p => p.T > at + 999).OrderBy(p => p.T).ToList();
        if (after.Count < 3) return null;
        var parts = new List<string>();
        double? gpuBefore = points.Where(p => p.T <= at + 999 && p.T >= at - 20_000).Select(p => p.GpuLoad).OfType<double>().DefaultIfEmpty(double.NaN).Average();
        double? gpuAfter = after.Where(p => p.T <= at + 20_000).Select(p => p.GpuLoad).OfType<double>().DefaultIfEmpty(double.NaN).Average();
        if (gpuBefore >= 50 && gpuAfter < 20) parts.Add($"the GPU went from {gpuBefore:0}% to {gpuAfter:0}% at once");
        if (crashedApp is long app)
        {
            var back = after.FirstOrDefault(p => p.Front == app && p.T > at + 10_000);
            if (back is not null) parts.Add($"{nameOf(app)} was back in front {CrashFindings.Words((back.T - at) / 1000.0)} later");
        }
        return parts.Count == 0 ? null : new StoodOut($"After: {string.Join(", and ", parts)}.", false);
    }
}

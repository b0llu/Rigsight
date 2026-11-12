using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// The fans as the Fans page lists them: which fans, in what order, by what name and at what speed. Worked out here once
/// for the page and the widgets' Fans tile (on the desktop and in the Widgets page's pictures), so the tile shows the
/// page's fans. It listed only fans that had spun since the agent started, so a graphics card's fans, which stand still
/// at idle (the user's RTX 3080 Ti, still about 42% of the time), never showed on it (the user's test copy, 2026-11-12).
/// </summary>
public static class FanList
{
    /// <summary>A fan turns from here: real fans don't go slower than about 200 rpm, and a header with nothing on it can
    /// read a stray few rpm.</summary>
    public const double MinSpinRpm = 100;

    /// <summary>How far back a header must have spun at least once to be listed.</summary>
    public const int SpunWithinDays = 90;

    /// <summary>A graphics card's own fan (its hardware is a GPU): it follows the GPU, and standing still while cool is by design.</summary>
    public static bool IsGpu(string hardwareType) => hardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The rows, in the page's order: each graphics card's fans as one row, always (they stand still for days on a light
    /// load); then each cooler's fans and the board's headers, one row each, once they have spun.
    /// </summary>
    public static List<IReadOnlyList<T>> Rows<T>(IEnumerable<T> fans, Func<T, string> hardwareType, Func<T, string> hardwareName, Func<T, bool> spun)
    {
        var all = fans.ToList();
        var rows = new List<IReadOnlyList<T>>();
        rows.AddRange(all.Where(x => IsGpu(hardwareType(x))).GroupBy(hardwareName).Select(g => (IReadOnlyList<T>)[.. g]));
        var others = all.Where(x => !IsGpu(hardwareType(x))).ToList();
        rows.AddRange(others.Where(x => hardwareType(x) == "Cooler" && spun(x)).Select(x => (IReadOnlyList<T>)[x]));
        rows.AddRange(others.Where(x => hardwareType(x) != "Cooler" && spun(x)).Select(x => (IReadOnlyList<T>)[x]));
        return rows;
    }

    /// <summary>A row's speed now: its fans' average (a card's turn as one), under <see cref="MinSpinRpm"/> as 0; null with none read.</summary>
    public static double? Speed(IEnumerable<double?> rpms)
    {
        double v = rpms.Where(x => x is not null).Select(x => x!.Value).DefaultIfEmpty(double.NaN).Average();
        return double.IsNaN(v) ? null : v < MinSpinRpm ? 0 : v;
    }

    /// <summary>A row's name: "GPU fans" for a card's several, else its fan's own (the name it was given, or as All sensors shows it).</summary>
    public static string Title(int fans, string name) => fans > 1 ? Lang.T("GPU fans") : name;

    /// <summary>The fans (their sensors) that spun in the last <see cref="SpunWithinDays"/> days, today's minutes so far included.</summary>
    public static HashSet<string> SpunIn(RigsightDb db, DateTime now)
    {
        var sensors = db.GetFans().ToDictionary(f => f.Id, f => f.Sensor);
        if (sensors.Count == 0) return [];
        return [.. db.GetFanDays(TimeUtil.ToUnix(now.Date.AddDays(-SpunWithinDays)), TimeUtil.ToUnix(now) + 60)
            .Where(d => d.RpmMax >= MinSpinRpm && sensors.ContainsKey(d.Fan)).Select(d => sensors[d.Fan])];
    }
}

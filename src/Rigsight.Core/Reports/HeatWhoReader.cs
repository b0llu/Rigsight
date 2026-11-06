using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// What was in front over a stretch of minutes, for a temperature hover (2026-11-06): the apps longest in front and in
/// use, longest first, up to two. From the minutes already read for the graph, so the hover has it at once (read on
/// the first hover, it came a moment after the box and the box jumped: "the hover seems a lot flashy").
/// </summary>
public static class HeatWhoReader
{
    /// <summary>The apps in front over <paramref name="minutes"/>, named by <paramref name="app"/> (null for one not known).</summary>
    public static List<FrontApp> FrontOf(IEnumerable<SystemMinute> minutes, Func<long, (string Name, string? Path, bool IsGame)?> app) =>
        [.. minutes.Where(m => m.FgApp is not null && m.ActiveSec > 0).GroupBy(m => m.FgApp!.Value)
            .Select(g => (Id: g.Key, Sec: g.Sum(m => (double)m.ActiveSec))).Where(x => x.Sec >= 30).OrderByDescending(x => x.Sec)
            .Select(x => app(x.Id) is { } a ? new FrontApp(a.Name, a.Path, x.Sec, a.IsGame) : null).OfType<FrontApp>().Take(2)];
}

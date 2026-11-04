using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// The line check over a stretch (a bar of the chart, or the whole period): the usual ping and the highest, in
/// milliseconds, to the provider's first router when it was found (else to this PC's router), the share of pings that
/// went unanswered, and how many minutes were checked. <see cref="HomeUsual"/> is the router's usual on its own: the
/// home side's part of the time.
/// </summary>
public sealed record NetLinePing(int Usual, int Highest, double LostPercent, int Minutes, bool Provider, int? HomeUsual)
{
    /// <summary>The lowest minute's ping, and the ping most minutes stayed under (nine in ten): the graph's spread, as
    /// SmokePing draws it (a tight spread is a steady line; a tall, faint one a busy line).</summary>
    public int Low { get; init; }
    public int Most { get; init; }

    /// <summary>The usual and the highest marked "high" from this on: 80 ms (where online games start to feel it), or
    /// twice the period's usual on a line that is never that fast (satellite, the far side of the world).</summary>
    public static int HighFor(int usual) => Math.Max(NetLine.HighMs, usual * 2);
}

/// <summary>What the line check's minutes say over a stretch, and the Network page's sentence about a period.</summary>
public static class NetLine
{
    /// <summary>The ping a game starts to feel: past it an hour is marked on the chart.</summary>
    public const int HighMs = 80;

    /// <summary>The share of unanswered pings worth saying (a percent).</summary>
    public const double LostWorthSaying = 1;

    /// <summary>
    /// How many bars with ping checked a period needs before its graph is drawn: six hours (a day, a week), three days
    /// (a month), two months (a year). Fewer is a scatter of dots on an empty graph (the user, 2026-11-04: "why does
    /// the ping chart look so empty"); the page says it's being checked instead.
    /// </summary>
    public static int BarsToDraw(bool hourly, ReportRange range) => hourly ? 6 : range == ReportRange.Year ? 2 : 3;

    /// <summary>The upload an app needs in an hour to be named as what the ping rose with.</summary>
    public const long BusyUploadBytes = 500L << 20;

    /// <summary>The minutes of <paramref name="rows"/> as one stretch; null when none had a ping sent.</summary>
    public static NetLinePing? Of(IEnumerable<NetPing> rows)
    {
        var sent = rows.Where(p => p.Sent > 0).ToList();
        if (sent.Count == 0) return null;
        bool provider = sent.Any(p => p.IspMs is not null);
        var usual = sent.Select(p => provider ? p.IspMs : p.LanMs).OfType<int>().ToList();
        var highest = sent.Select(p => provider ? p.IspMax : p.LanMax).OfType<int>().ToList();
        if (usual.Count == 0) return null;
        int lost = sent.Sum(p => provider ? p.IspLost : p.LanLost), tries = sent.Sum(p => p.Sent);
        var home = sent.Select(p => p.LanMs).OfType<int>().ToList();
        var sorted = usual.Order().ToList();
        return new NetLinePing(Middle(usual), highest.Count > 0 ? highest.Max() : Middle(usual), tries > 0 ? Math.Round(100.0 * lost / tries, 1) : 0,
            sent.Count, provider, home.Count > 0 ? Middle(home) : null)
        {
            Low = sorted[0], Most = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * 0.9) - 1)],
        };
    }

    internal static int Middle(List<int> values) => values.Order().ElementAt(values.Count / 2);

    /// <summary>
    /// The page's sentence for a period: the usual ping, the hours it went high (and what was uploading in them when it
    /// was the same app), what went unanswered, and the drops. Empty when nothing was checked.
    /// </summary>
    public static string Sentence(NetReport r, IReadOnlyDictionary<long, string> names)
    {
        if (r.Line is not { } line) return "";
        int high = NetLinePing.HighFor(line.Usual);
        // Checks paused in the period and still off: the usual is of the part checked, and the sentence says until when.
        var parts = new List<string>
        {
            r.PingSwitches is { Count: > 0 } sw && !sw[^1].On
                ? r.Range == ReportRange.Day
                    ? Lang.T($"Ping was usually {line.Usual} ms until checks were paused at {TimeText.At(sw[^1].At, "h:mm tt")}.")
                    : Lang.T($"Ping was usually {line.Usual} ms until checks were paused on {sw[^1].At:ddd} at {TimeText.At(sw[^1].At, "h:mm tt")}.")
                : Lang.T($"Ping was usually {line.Usual} ms."),
        };
        var highBins = r.Bins.Where(b => b.Ping is { } p && p.Highest >= high).ToList();
        string unit = r.Hourly ? "hour" : r.Range == ReportRange.Year ? "month" : "day";
        if (highBins.Count == 0) parts.Add(Lang.T($"It stayed under {high} ms."));
        else
        {
            // The app uploading the most in each high stretch: named when it was the same one in most of them.
            var busy = highBins.Select(b => b.Apps.Where(a => a.Up >= BusyUploadBytes).MaxBy(a => a.Up)?.App).ToList();
            var top = busy.OfType<long>().GroupBy(a => a).MaxBy(g => g.Count());
            string count = unit switch
            {
                "hour" => highBins.Count == 1 ? Lang.T("1 hour") : Lang.T($"{highBins.Count} hours"),
                "month" => highBins.Count == 1 ? Lang.T("1 month") : Lang.T($"{highBins.Count} months"),
                _ => highBins.Count == 1 ? Lang.T("1 day") : Lang.T($"{highBins.Count} days"),
            };
            if (top is not null && names.TryGetValue(top.Key, out var name) && top.Count() * 2 > highBins.Count)
                parts.Add(top.Count() == highBins.Count
                    ? Lang.T($"It went past {high} ms in {count}, every time while {name} was uploading.")
                    : Lang.T($"It went past {high} ms in {count}, {top.Count()} of them while {name} was uploading."));
            else parts.Add(Lang.T($"It went past {high} ms in {count}."));
        }
        if (line.LostPercent >= LostWorthSaying) parts.Add(Lang.T($"Some data didn't get through: {line.LostPercent:0.#}% packet loss."));
        return string.Join(" ", parts);
    }
}

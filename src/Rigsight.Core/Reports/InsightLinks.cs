namespace Rigsight.Core.Reports;

/// <summary>
/// Which lines open a page. One rule for every line, so Home, the recap and Reports agree: a line opens a page only when
/// that page shows something the line can't. Something that moved against the PC's usual opens its proof; a problem that
/// happened opens Crashes; a warning about heat opens Temperatures, a fan's opens Fans, the internet's opens Network. A
/// line that is a complete fact (time in games, a break, a late night, "as usual") opens nothing.
/// </summary>
public static class InsightLinks
{
    public static InsightOpens Of(Insight i) => i.Key switch
    {
        // A problem: the crash itself, unless its cause was outside the PC (a power cut is said, not looked into).
        "crash" => i.Tone == InsightTone.Neutral ? InsightOpens.None : InsightOpens.Crashes,
        "app-crashes" => InsightOpens.Crashes,
        "usual-problems" => i.Tone == InsightTone.Warn ? InsightOpens.Crashes : InsightOpens.Usual,
        // PC health: problems that happened (as usual) are still events to look at; "none" and "as usual" are complete.
        "pc-problems" or "usual-power" => InsightOpens.Crashes,
        "drive-unwell" => InsightOpens.Storage,
        // Moved against the usual, with the figures behind it.
        // A game hotter than its usual: that game's plays against its usual, on the Games page.
        "usual-heat" => i.Game is null ? InsightOpens.Usual : InsightOpens.Game,
        "drift" => InsightOpens.Usual,
        // Warnings with more to see where they're measured.
        "over-limit" or "throttle" or "hotspot" => InsightOpens.Temperatures,
        "peak" => i.Tone is InsightTone.Warn or InsightTone.Hot ? InsightOpens.Temperatures : InsightOpens.None,
        "fan-stopped" or "fan-slower" or "fan-older" => InsightOpens.Fans,
        // Far outside an app's own usual, or the connection itself dropping.
        "net-upload" or "net-drops" => InsightOpens.Network,
        _ => InsightOpens.None,
    };

    /// <summary>Each line with where it opens.</summary>
    public static List<Insight> Apply(IEnumerable<Insight> lines) => [.. lines.Select(i => i.Opens == Of(i) ? i : i with { Opens = Of(i) })];
}

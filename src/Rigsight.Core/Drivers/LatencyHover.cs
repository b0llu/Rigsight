using System.Globalization;

namespace Rigsight.Core.Drivers;

/// <summary>Which figure the Latency chart shows (its switch): the longest driver delay, the app wake-up delay, or calls over 1 ms.</summary>
public enum LatencyMetric { Driver, Wake, Over }

/// <summary>A fact in an hour's hover: its label and its value.</summary>
public sealed record LatencyHoverFact(string Label, string Value);

/// <summary>Another driver busy in the hour, with its longest call: whose it is, its file and its kind.</summary>
public sealed record LatencyHoverDriver(string Driver, string Who, DriverPart? Part, string Ms);

/// <summary>
/// What the Latency chart's hover says about one hour or day (1.22.0, the approved mock-up): the hour, the game in front,
/// the chart's own figure big with its word, then who it was (<see cref="By"/>, the driver's name and file on one row),
/// the other figures of that hour, the other drivers busy then with their longest, and whether readings were dropped.
/// A figure that wasn't recorded is left out, never said as 0.
/// </summary>
public sealed record LatencyHoverFacts(string Title, long? Game, string Big, string BigWord, LatencyHoverDriver? By,
    IReadOnlyList<LatencyHoverFact> Facts, string OthersLabel, IReadOnlyList<LatencyHoverDriver> Others, bool Lost);

/// <summary>Builds <see cref="LatencyHoverFacts"/> from a slot (pure: the tests read it).</summary>
public static class LatencyHover
{
    /// <summary>The other drivers named in a hover, at most.</summary>
    public const int OthersShown = 2;

    /// <summary>The hover of slot <paramref name="s"/> as the chart shows <paramref name="metric"/>; null when nothing was recorded in it.</summary>
    public static LatencyHoverFacts? Of(LatencySlot s, bool hourly, LatencyMetric metric, Func<string, DriverPart?> partOf)
    {
        if (s.LongestUs is null && s.WakeUs is null) return null;
        string title = hourly ? Lang.T($"{s.Start.ToString("ddd d MMM", CultureInfo.CurrentCulture)}, {Hour(s.Start)} to {Hour(s.End)}")
            : s.Start.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

        string? longest = s.LongestUs is int us ? LatencyWords.Ms(us) : null;
        string? over = s.LongestUs is null ? null : s.Long == 0 ? Lang.T("None") : s.Long == 1 ? Lang.T("Once") : Lang.T($"{s.Long:N0} times");
        string? wake = s.WakeUs is int w ? Lang.T($"{LatencyWords.Ms(w)} at most") : null;

        // The chart's own figure is the big one; the others are facts.
        string big, word;
        var facts = new List<LatencyHoverFact>();
        switch (metric)
        {
            case LatencyMetric.Wake when wake is not null:
                (big, word) = (LatencyWords.Ms(s.WakeUs!.Value), Lang.T("longest app wake-up delay"));
                if (over is not null) facts.Add(new(Lang.T("Over 1 ms"), over));
                if (longest is not null) facts.Add(new(Lang.T("Longest driver delay"), longest));
                break;
            case LatencyMetric.Over when longest is not null:
                (big, word) = (s.Long.ToString("N0", CultureInfo.CurrentCulture), s.Long == 1 ? Lang.T("time over 1 ms") : Lang.T("times over 1 ms"));
                facts.Add(new(Lang.T("Longest driver delay"), longest));
                if (wake is not null) facts.Add(new(Lang.T("App wake-up"), wake));
                break;
            default:
                if (longest is not null) (big, word) = (longest, Lang.T("longest driver delay"));
                else (big, word) = (LatencyWords.Ms(s.WakeUs!.Value), Lang.T("longest app wake-up delay"));
                if (over is not null) facts.Add(new(Lang.T("Over 1 ms"), over));
                if (wake is not null && longest is not null) facts.Add(new(Lang.T("App wake-up"), wake));
                break;
        }

        var by = s.LongestUs is int top ? Driver(s.Driver, top, partOf) : null;
        var others = (s.Others ?? []).Where(o => !o.Driver.Equals(s.Driver, StringComparison.OrdinalIgnoreCase) && o.Us > 0)
            .OrderByDescending(o => o.Us).Take(OthersShown).Select(o => Driver(o.Driver, o.Us, partOf)).ToList();
        return new LatencyHoverFacts(title, s.Game, big, word, by, facts, hourly ? Lang.T("Also busy this hour") : Lang.T("Also busy this day"),
            others, s.Lost && metric != LatencyMetric.Wake);
    }

    private static LatencyHoverDriver Driver(string driver, int us, Func<string, DriverPart?> partOf)
    {
        var part = driver.Length == 0 ? null : partOf(driver);
        return new LatencyHoverDriver(driver, LatencyWords.Who(driver, part), part, LatencyWords.Ms(us));
    }

    // "6 PM", or "6:30 PM" where the time zone puts the hours at half past.
    private static string Hour(DateTime t) => t.Minute == 0 ? TimeText.At(t, "h tt") : TimeText.Time(t);
}

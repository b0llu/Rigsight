namespace Rigsight.Core.Settings;

/// <summary>
/// The "Enjoying RigSight?" card's own record, kept on this PC only: how many times it has asked, when it last did,
/// and whether it is finished (the coffee button or "Don't show again").
/// </summary>
public sealed class SupportAskState
{
    public int Shown { get; set; }

    /// <summary>When it last asked (local time), null before the first.</summary>
    public DateTime? LastShown { get; set; }

    /// <summary>Never again: a coffee was bought from it, or "Don't show again" was pressed.</summary>
    public bool Done { get; set; }

    /// <summary>A "Support RigSight" button (the Settings menu's or About's) was pressed at least once.</summary>
    public bool Clicked { get; set; }

    /// <summary>
    /// The Settings menu's "Support RigSight" in Buy Me a Coffee's yellow, to stand out, until it has been pressed or
    /// the card was answered for good (the user's word, 2026-10-20); then in the menu's own colour.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Highlight => !Done && !Clicked;
}

/// <summary>
/// When the app may ask for a coffee (the user's rules, 2026-10-20). Only once a person knows the app: three weeks
/// after the history starts and ten days with use. Never on a bad day: a crash or sudden shut-off in the last day.
/// At most three times: "Maybe later" waits 30 days, the second 60, and the third is the last (shown counts, answered
/// or not). The coffee button and "Don't show again" end it, and so does a Support RigSight button pressed by itself.
/// The moments it is asked at (an opening that settled on Home, or right after a rewind watched to its end) are the
/// app's; this only says whether it may.
/// </summary>
public static class SupportAsk
{
    public const int WaitDays = 21, DaysOfUse = 10, MaxShown = 3;

    /// <summary>The wait after the first "Maybe later", then after the second.</summary>
    public static readonly int[] LaterDays = [30, 60];

    public const string CoffeeUrl = "https://buymeacoffee.com/bollu";

    /// <summary>
    /// Whether it may ask now, and why not when it may not (for the log). <paramref name="firstDay"/> is the history's
    /// first day, <paramref name="daysUsed"/> the days with any use, <paramref name="badDay"/> a crash or sudden
    /// shut-off in the last 24 hours.
    /// </summary>
    public static (bool May, string Why) May(SupportAskState s, DateTime now, DateTime? firstDay, int daysUsed, bool badDay)
    {
        if (s.Done) return (false, "finished (a coffee or don't show again)");
        // Found the button by themselves (and maybe gave): never asked on top of that.
        if (s.Clicked) return (false, "Support RigSight was pressed before");
        if (s.Shown >= MaxShown) return (false, $"asked {s.Shown} times, the most");
        if (firstDay is not { } first || (now.Date - first.Date).TotalDays < WaitDays)
            return (false, $"history from {firstDay:d}: under {WaitDays} days");
        if (daysUsed < DaysOfUse) return (false, $"{daysUsed} days of use, under {DaysOfUse}");
        if (badDay) return (false, "a crash or sudden shut-off in the last day");
        if (s.Shown > 0 && s.LastShown is { } last)
        {
            int wait = LaterDays[Math.Min(s.Shown, LaterDays.Length) - 1];
            if ((now - last).TotalDays < wait) return (false, $"maybe later on {last:d}: waits {wait} days");
        }
        return (true, s.Shown == 0 ? "first time" : $"{s.Shown + 1} of {MaxShown}");
    }
}

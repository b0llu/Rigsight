namespace Rigsight.Core.Reports;

/// <summary>
/// The temperature graph's longer periods. A week, a month and a year are rolling: the last 7 days, the last 30 days
/// and the last 12 months, ending now (a calendar week on a Thursday left the graph's right half empty for days to
/// come). Each is named by its last day: today's ends now, and the arrows step by the period's own length (the 7 days
/// before…). A custom range is any span of whole hours, drawn by the minute, the hour or the day by how long it is.
/// The graph, its legend and the history it reads all take their bounds from here.
/// </summary>
public static class ChartRange
{
    public const int Week = 604800, Month = 2592000, Year = 31536000, Custom = -1;

    /// <summary>The last 7 days, 30 days or 12 months (named by its last day).</summary>
    public static bool IsRolling(int window) => window is Week or Month or Year;

    /// <summary>The days a rolling period covers: from its first midnight to the one after <paramref name="lastDay"/>.</summary>
    public static (DateTime From, DateTime To) Rolling(int window, DateTime lastDay)
    {
        var to = lastDay.Date.AddDays(1);
        return (window == Year ? to.AddYears(-1) : to.AddDays(window == Week ? -7 : -30), to);
    }

    /// <summary>The last day of the period before (<paramref name="direction"/> -1) or after (1): the 7 days before, the 12 months before…</summary>
    public static DateTime Step(int window, DateTime lastDay, int direction) =>
        window == Year ? lastDay.Date.AddYears(direction) : lastDay.Date.AddDays(direction * (window == Week ? 7 : 30));

    /// <summary>How long one point of the graph stands for: an hour on the last 7 or 30 days, a day on 12 months; a
    /// custom range by the minute up to two days, by the hour up to two months, by the day beyond.</summary>
    public static int StepSeconds(int window, DateTime customFrom = default, DateTime customTo = default) => window switch
    {
        Year => 86400,
        Week or Month => 3600,
        Custom => (customTo - customFrom).TotalDays switch { <= 2 => 60, <= 62 => 3600, _ => 86400 },
        _ => 60,
    };

    /// <summary>
    /// Where the graph ends: the period's end, or, for one still going on, the end of the point that now is in (an hour
    /// on the last 7 days, today on 12 months): no empty stretch of time still to come.
    /// </summary>
    public static DateTime ShownEnd(DateTime to, int stepSeconds, DateTime now)
    {
        var start = stepSeconds >= 86400 ? now.Date : new DateTime(now.Ticks - now.Ticks % TimeSpan.FromSeconds(stepSeconds).Ticks, now.Kind);
        var end = start == now ? now : start.AddSeconds(stepSeconds);
        return to < end ? to : end;
    }

    /// <summary>The period in words: "Last 7 days", "Last 12 months"; an earlier one by its dates ("3–9 Oct", "10 Sep – 9 Oct", "17 Oct 2024 – 16 Oct 2025").</summary>
    public static string Label(int window, DateTime lastDay, DateTime today)
    {
        if (lastDay.Date >= today.Date) return $"Last {Length(window)}";
        var (from, to) = Rolling(window, lastDay);
        var last = to.AddDays(-1);
        string year = last.Year == today.Year ? "" : $" {last:yyyy}";
        if (from.Year != last.Year) return $"{from:d MMM yyyy} – {last:d MMM yyyy}";
        return from.Month == last.Month ? $"{from:%d}–{last:d MMM}{year}" : $"{from:d MMM} – {last:d MMM}{year}";
    }

    /// <summary>The period after a statement ("Not recorded …"): "in the last 7 days", "in the 7 days to 9 Oct".</summary>
    public static string Words(int window, DateTime lastDay, DateTime today) =>
        lastDay.Date >= today.Date ? $"in the last {Length(window)}"
            : $"in the {Length(window)} to {lastDay:d MMM}{(lastDay.Year == today.Year ? "" : $" {lastDay:yyyy}")}";

    /// <summary>The last minutes or hours, up to now: "Last 5 minutes", "Last hour", "Last 6 hours", "Last 24 hours".</summary>
    public static string WindowLabel(int seconds) => seconds switch
    {
        3600 => "Last hour",
        < 3600 => $"Last {seconds / 60} minutes",
        _ => $"Last {seconds / 3600} hours",
    };

    /// <summary>"7 days", "30 days", "12 months".</summary>
    public static string Length(int window) => window switch { Week => "7 days", Month => "30 days", _ => "12 months" };
}

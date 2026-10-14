namespace Rigsight.Core.Reports;

/// <summary>
/// A report's period as a sentence says it: by its own name where it has one. Today is "today" and yesterday
/// "yesterday", this week and last week likewise; only a period further back, which has no such name, is "that day"
/// or "that week" (the report's title says which). One rule, so a recap, a page and a notification can't each word
/// it their own way (the user: "today is today, yesterday is yesterday").
/// </summary>
public static class PeriodWords
{
    /// <param name="from">The period's first day.</param>
    /// <param name="today">Today (the clock's, unless a test says otherwise).</param>
    public static string Said(ReportRange range, DateTime from, DateTime? today = null)
    {
        var now = (today ?? DateTime.Today).Date;
        if (range is not (ReportRange.Day or ReportRange.Week or ReportRange.Month or ReportRange.Year)) return "in that time";
        var start = ReportBuilder.Bounds(range, from).From;
        var thisStart = ReportBuilder.Bounds(range, now).From;
        var lastStart = ReportBuilder.Bounds(range, ReportBuilder.Previous(range, now)).From;
        string unit = range switch { ReportRange.Day => "day", ReportRange.Week => "week", ReportRange.Month => "month", _ => "year" };
        return start == thisStart ? (range == ReportRange.Day ? "today" : $"this {unit}")
            : start == lastStart ? (range == ReportRange.Day ? "yesterday" : $"last {unit}")
            : $"that {unit}";
    }
}

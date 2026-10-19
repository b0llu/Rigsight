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
        if (range is not (ReportRange.Day or ReportRange.Week or ReportRange.Month or ReportRange.Year)) return Lang.T("in that time");
        var start = ReportBuilder.Bounds(range, from).From;
        var thisStart = ReportBuilder.Bounds(range, now).From;
        var lastStart = ReportBuilder.Bounds(range, ReportBuilder.Previous(range, now)).From;
        int which = start == thisStart ? 0 : start == lastStart ? 1 : 2;
        return (range, which) switch
        {
            (ReportRange.Day, 0) => Lang.T("today"),
            (ReportRange.Day, 1) => Lang.T("yesterday"),
            (ReportRange.Day, _) => Lang.T("that day"),
            (ReportRange.Week, 0) => Lang.T("this week"),
            (ReportRange.Week, 1) => Lang.T("last week"),
            (ReportRange.Week, _) => Lang.T("that week"),
            (ReportRange.Month, 0) => Lang.T("this month"),
            (ReportRange.Month, 1) => Lang.T("last month"),
            (ReportRange.Month, _) => Lang.T("that month"),
            (_, 0) => Lang.T("this year"),
            (_, 1) => Lang.T("last year"),
            _ => Lang.T("that year"),
        };
    }

    /// <summary>A day of the week by name, in the person's language ("Saturday"): never DayOfWeek's own English.</summary>
    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Lang.T("Monday"),
        DayOfWeek.Tuesday => Lang.T("Tuesday"),
        DayOfWeek.Wednesday => Lang.T("Wednesday"),
        DayOfWeek.Thursday => Lang.T("Thursday"),
        DayOfWeek.Friday => Lang.T("Friday"),
        DayOfWeek.Saturday => Lang.T("Saturday"),
        _ => Lang.T("Sunday"),
    };
}

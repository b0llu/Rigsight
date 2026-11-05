namespace Rigsight.Core.Reports;

/// <summary>
/// A report's period as a sentence says it: by its own name where it has one. Today is "today" and yesterday
/// "yesterday"; a week, month or year is the time just gone (see <see cref="ReportBuilder.Bounds"/>), so the latest
/// one is "in the last 7 days" and an earlier one is said by its dates ("in the 7 days to 29 Oct"). One rule, so a
/// recap, a page and a notification can't each word it their own way (the user: "today is today, yesterday is yesterday").
/// </summary>
public static class PeriodWords
{
    /// <param name="from">The period's first day.</param>
    /// <param name="today">Today (the clock's, unless a test says otherwise).</param>
    public static string Said(ReportRange range, DateTime from, DateTime? today = null)
    {
        var now = (today ?? DateTime.Today).Date;
        switch (range)
        {
            case ReportRange.Day:
                var day = from.Date;
                return day == now ? Lang.T("today") : day == now.AddDays(-1) ? Lang.T("yesterday") : Lang.T("that day");
            case ReportRange.Week or ReportRange.Month or ReportRange.Year:
                if (IsLatest(range, from, now))
                    return range switch { ReportRange.Week => Lang.T("in the last 7 days"), ReportRange.Month => Lang.T("in the last 30 days"), _ => Lang.T("in the last 12 months") };
                var (start, end) = ReportBuilder.Bounds(range, LastDay(range, from));
                if (range == ReportRange.Year)
                {
                    var lastMonth = end.AddMonths(-1);
                    return start.Month == 1 ? Lang.T($"in {start.Year}") : Lang.T($"from {start:MMM yyyy} to {lastMonth:MMM yyyy}");
                }
                return ChartRange.Words(range == ReportRange.Week ? ChartRange.Week : ChartRange.Month, end.AddDays(-1), now);
            default:
                return Lang.T("in that time");
        }
    }

    /// <summary>
    /// The latest week, month or year: the one ending today, or yesterday (Home's recaps count whole days, so theirs end
    /// there); for a year, the one ending this month or last month. Those are "the last 7 days" in a sentence.
    /// </summary>
    public static bool IsLatest(ReportRange range, DateTime from, DateTime today)
    {
        var now = today.Date;
        var month = new DateTime(now.Year, now.Month, 1);
        return range switch
        {
            ReportRange.Week or ReportRange.Month => from.Date == ReportBuilder.Bounds(range, now).From || from.Date == ReportBuilder.Bounds(range, now.AddDays(-1)).From,
            ReportRange.Year => from.Date == ReportBuilder.Bounds(range, now).From || from.Date == ReportBuilder.Bounds(range, month.AddDays(-1)).From,
            _ => false,
        };
    }

    /// <summary>
    /// A week, month or year as a picker or a heading names it: "Last 7 days", "Last 30 days", "Last 12 months" for the
    /// one ending today; an earlier one by its dates ("23–29 Oct", "7 Sep – 6 Oct", "Dec 2024 – Nov 2025"), a year of
    /// January to December by its number ("2025").
    /// </summary>
    public static string Label(ReportRange range, DateTime from, DateTime today)
    {
        var now = today.Date;
        var (start, end) = ReportBuilder.Bounds(range, LastDay(range, from));
        if (start == ReportBuilder.Bounds(range, now).From)
            return range switch { ReportRange.Week => Lang.T("Last 7 days"), ReportRange.Month => Lang.T("Last 30 days"), _ => Lang.T("Last 12 months") };
        if (range == ReportRange.Year)
        {
            var lastMonth = end.AddMonths(-1);
            if (start.Month == 1) return start.Year.ToString();
            return start.Year == lastMonth.Year ? $"{start:MMM} – {lastMonth:MMM yyyy}" : $"{start:MMM yyyy} – {lastMonth:MMM yyyy}";
        }
        var last = end.AddDays(-1);
        string year = last.Year == now.Year ? "" : $" {last:yyyy}";
        if (start.Year != last.Year) return $"{start:d MMM yyyy} – {last:d MMM yyyy}";
        return start.Month == last.Month ? $"{start:%d}–{last:d MMM}{year}" : $"{start:d MMM} – {last:d MMM}{year}";
    }

    /// <summary>The day a week or month starting on <paramref name="from"/> ends on (a year: a day of its last month).</summary>
    public static DateTime LastDay(ReportRange range, DateTime from) => range switch
    {
        ReportRange.Week => from.Date.AddDays(ReportBuilder.WeekDays - 1),
        ReportRange.Month => from.Date.AddDays(ReportBuilder.MonthDays - 1),
        ReportRange.Year => new DateTime(from.Year, from.Month, 1).AddMonths(11),
        _ => from.Date,
    };

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

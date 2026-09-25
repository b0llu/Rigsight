using System.Globalization;
using Rigsight.Core.Reports;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>Which dates a period covers, stepping between periods, and what the period is called.</summary>
public sealed class ReportPeriodTests
{
    private static DateTime D(string s) => DateTime.ParseExact(s, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None);

    [Theory]
    [InlineData("Day", "2025-03-12 15:30", "2025-03-12", "2025-03-13")]
    [InlineData("Day", "2024-12-31 23:59", "2024-12-31", "2025-01-01")]
    [InlineData("Day", "2024-02-29 00:00", "2024-02-29", "2024-03-01")]
    [InlineData("Week", "2025-03-10 00:00", "2025-03-10", "2025-03-17")] // a Monday starts its own week
    [InlineData("Week", "2025-03-12 15:30", "2025-03-10", "2025-03-17")]
    [InlineData("Week", "2025-03-16 23:59", "2025-03-10", "2025-03-17")] // Sunday ends it
    [InlineData("Week", "2025-01-01 12:00", "2024-12-30", "2025-01-06")] // across the new year
    [InlineData("Week", "2024-03-01 12:00", "2024-02-26", "2024-03-04")] // across 29 February
    [InlineData("Month", "2025-03-12 15:30", "2025-03-01", "2025-04-01")]
    [InlineData("Month", "2025-12-31 23:00", "2025-12-01", "2026-01-01")]
    [InlineData("Month", "2024-02-10", "2024-02-01", "2024-03-01")]
    [InlineData("Year", "2025-03-12 15:30", "2025-01-01", "2026-01-01")]
    [InlineData("Year", "2024-12-31 23:59", "2024-01-01", "2025-01-01")]
    public void A_period_covers_whole_local_days_from_midnight_to_midnight(string range, string anchor, string from, string to)
    {
        Assert.Equal((D(from), D(to)), ReportBuilder.Bounds(Enum.Parse<ReportRange>(range), D(anchor)));
    }

    [Fact]
    public void All_time_runs_from_2000_to_the_end_of_today_whatever_the_anchor()
    {
        foreach (var anchor in new[] { D("2020-05-05"), DateTime.Now, D("2030-01-01") })
            Assert.Equal((new DateTime(2000, 1, 1), DateTime.Today.AddDays(1)), ReportBuilder.Bounds(ReportRange.All, anchor));
    }

    [Theory]
    [InlineData("Day", "2025-03-01", "2025-02-28", "2025-03-02")]
    [InlineData("Week", "2025-03-12", "2025-03-05", "2025-03-19")]
    [InlineData("Month", "2025-03-31", "2025-02-28", "2025-04-30")] // the day is clamped to the shorter month
    [InlineData("Month", "2025-01-15", "2024-12-15", "2025-02-15")]
    [InlineData("Year", "2024-02-29", "2023-02-28", "2025-02-28")]
    [InlineData("All", "2025-03-12", "2025-03-12", "2025-03-12")]
    public void Stepping_back_and_forward_moves_by_one_period(string range, string anchor, string previous, string next)
    {
        var r = Enum.Parse<ReportRange>(range);
        Assert.Equal(D(previous), ReportBuilder.Previous(r, D(anchor)));
        Assert.Equal(D(next), ReportBuilder.Next(r, D(anchor)));
    }

    [Theory]
    [InlineData("Day")]
    [InlineData("Week")]
    [InlineData("Month")]
    [InlineData("Year")]
    public void Stepping_through_periods_leaves_no_gap_or_overlap(string range)
    {
        var r = Enum.Parse<ReportRange>(range);
        var anchor = D("2023-01-31");
        for (int i = 0; i < 40; i++)
        {
            var (from, to) = ReportBuilder.Bounds(r, anchor);
            var next = ReportBuilder.Next(r, anchor);
            Assert.Equal(to, ReportBuilder.Bounds(r, next).From);
            Assert.Equal((from, to), ReportBuilder.Bounds(r, ReportBuilder.Previous(r, next)));
            anchor = next;
        }
    }

    [Theory]
    [InlineData(ReportRange.Day, false)]
    [InlineData(ReportRange.Week, false)]
    [InlineData(ReportRange.Month, false)]
    [InlineData(ReportRange.Year, true)]
    [InlineData(ReportRange.All, true)]
    public void Years_and_all_time_are_built_from_the_totals(ReportRange range, bool isLong)
    {
        Assert.Equal(isLong, ReportBuilder.IsLong(range));
    }

    [Fact]
    public void Periods_are_titled_in_plain_words()
    {
        using var _ = Make.Culture();
        string Title(ReportRange range, DateTime anchor)
        {
            var (from, to) = ReportBuilder.Bounds(range, anchor);
            return new Report { Range = range, From = from, To = to }.Title;
        }
        Assert.Equal("Today", Title(ReportRange.Day, DateTime.Now));
        Assert.Equal("Yesterday", Title(ReportRange.Day, DateTime.Now.AddDays(-1)));
        Assert.Equal("Wednesday, 12 March", Title(ReportRange.Day, D("2025-03-12")));
        Assert.Equal("Week of 10 Mar", Title(ReportRange.Week, D("2025-03-12")));
        Assert.Equal("March 2025", Title(ReportRange.Month, D("2025-03-12")));
        Assert.Equal("This year", Title(ReportRange.Year, DateTime.Now));
        Assert.Equal("2024", Title(ReportRange.Year, D("2024-06-01")));
        Assert.Equal("All time", Title(ReportRange.All, DateTime.Now));
    }
}

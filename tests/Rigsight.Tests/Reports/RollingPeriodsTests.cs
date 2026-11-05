using Rigsight.Core.Reports;
using Rigsight.ViewModels;

namespace Rigsight.Tests.Reports;

/// <summary>
/// Week, Month and Year as the time just gone (since 1.4.0): the last 7 and 30 days and the last 12 months, on every
/// page with a period picker and on Home's recap. A calendar week on a Thursday was 4 days, a calendar month on the
/// 5th was 5, and people read them as broken (issue #8, 2026-11-05).
/// </summary>
public sealed class RollingPeriodsTests
{
    /// <summary>Thursday 5 November 2026.</summary>
    private static readonly DateTime Today = new(2026, 11, 5);

    private static DateTime D(int month, int day, int year = 2026) => new(year, month, day);

    [Fact]
    public void A_week_and_a_month_end_on_their_day_and_a_year_with_its_month()
    {
        Assert.Equal((D(10, 30), D(11, 6)), ReportBuilder.Bounds(ReportRange.Week, Today));
        Assert.Equal((D(10, 7), D(11, 6)), ReportBuilder.Bounds(ReportRange.Month, Today));
        // Twelve whole months, the last one this one: a bar for each.
        Assert.Equal((D(12, 1, 2025), D(12, 1)), ReportBuilder.Bounds(ReportRange.Year, Today));
        // A time of day doesn't matter: the day does.
        Assert.Equal(ReportBuilder.Bounds(ReportRange.Week, Today), ReportBuilder.Bounds(ReportRange.Week, Today.AddHours(21)));
        Assert.Equal(7, (int)(ReportBuilder.Bounds(ReportRange.Week, Today).To - ReportBuilder.Bounds(ReportRange.Week, Today).From).TotalDays);
        Assert.Equal(30, (int)(ReportBuilder.Bounds(ReportRange.Month, Today).To - ReportBuilder.Bounds(ReportRange.Month, Today).From).TotalDays);
    }

    [Fact]
    public void Stepping_back_lands_on_the_days_just_before_with_no_gap_or_overlap()
    {
        foreach (var range in new[] { ReportRange.Week, ReportRange.Month, ReportRange.Year })
        {
            var anchor = Today;
            for (int i = 0; i < 14; i++)
            {
                var before = ReportBuilder.Previous(range, anchor);
                Assert.Equal(ReportBuilder.Bounds(range, anchor).From, ReportBuilder.Bounds(range, before).To);
                Assert.Equal(anchor.Date, ReportBuilder.Next(range, before).Date);
                anchor = before;
            }
        }
    }

    [Fact]
    public void The_picker_names_the_latest_and_says_the_dates_of_the_others()
    {
        Assert.Equal("Last 7 days", PeriodWords.Label(ReportRange.Week, D(10, 30), Today));
        Assert.Equal("23–29 Oct", PeriodWords.Label(ReportRange.Week, D(10, 23), Today));
        Assert.Equal("29 Oct – 4 Nov", PeriodWords.Label(ReportRange.Week, D(10, 29), Today));
        Assert.Equal("Last 30 days", PeriodWords.Label(ReportRange.Month, D(10, 7), Today));
        Assert.Equal("7 Sep – 6 Oct", PeriodWords.Label(ReportRange.Month, D(9, 7), Today));
        Assert.Equal("Last 12 months", PeriodWords.Label(ReportRange.Year, D(12, 1, 2025), Today));
        Assert.Equal("Dec 2024 – Nov 2025", PeriodWords.Label(ReportRange.Year, D(12, 1, 2024), Today));
        // A year picked from the list is its January to December, and says so by its number.
        var (from, _) = ReportBuilder.Bounds(ReportRange.Year, D(12, 31, 2025));
        Assert.Equal(D(1, 1, 2025), from);
        Assert.Equal("2025", PeriodWords.Label(ReportRange.Year, from, Today));
        // An earlier year's dates carry it.
        Assert.Equal("23–29 Dec 2025", PeriodWords.Label(ReportRange.Week, D(12, 23, 2025), Today));
    }

    [Fact]
    public void A_sentence_says_the_last_7_days_for_ones_ending_today_or_yesterday_and_dates_for_older_ones()
    {
        Assert.Equal("in the last 7 days", PeriodWords.Said(ReportRange.Week, D(10, 30), Today));
        // Home's recap ends yesterday: still "the last 7 days".
        Assert.Equal("in the last 7 days", PeriodWords.Said(ReportRange.Week, D(10, 29), Today));
        Assert.Equal("in the 7 days to 29 Oct", PeriodWords.Said(ReportRange.Week, D(10, 23), Today));
        Assert.Equal("in the last 30 days", PeriodWords.Said(ReportRange.Month, D(10, 6), Today));
        Assert.Equal("in the 30 days to 6 Oct", PeriodWords.Said(ReportRange.Month, D(9, 7), Today));
        Assert.Equal("in the last 12 months", PeriodWords.Said(ReportRange.Year, D(12, 1, 2025), Today));
        Assert.Equal("in the last 12 months", PeriodWords.Said(ReportRange.Year, D(11, 1, 2025), Today));
        Assert.Equal("in 2025", PeriodWords.Said(ReportRange.Year, D(1, 1, 2025), Today));
        Assert.Equal("from Dec 2024 to Nov 2025", PeriodWords.Said(ReportRange.Year, D(12, 1, 2024), Today));
        Assert.Equal("today", PeriodWords.Said(ReportRange.Day, Today, Today));
        Assert.Equal("yesterday", PeriodWords.Said(ReportRange.Day, D(11, 4), Today));
    }

    [Fact]
    public void Homes_recaps_are_whole_days_ending_yesterday_with_their_dates_under_the_title()
    {
        var (range, anchor) = HomeViewModel.Bounds(RecapPeriod.LastWeek, Today);
        Assert.Equal((D(10, 29), D(11, 5)), ReportBuilder.Bounds(range, anchor));
        (range, anchor) = HomeViewModel.Bounds(RecapPeriod.LastMonth, Today);
        Assert.Equal((D(10, 6), D(11, 5)), ReportBuilder.Bounds(range, anchor));
        // The last 12 whole months: November to October.
        (range, anchor) = HomeViewModel.Bounds(RecapPeriod.LastYear, Today);
        Assert.Equal((D(11, 1, 2025), D(11, 1)), ReportBuilder.Bounds(range, anchor));

        Assert.Equal("29 Oct – 4 Nov", HomeViewModel.DaysOf(RecapPeriod.LastWeek, Today, D(1, 1, 2025)));
        Assert.Equal("6 Oct – 4 Nov", HomeViewModel.DaysOf(RecapPeriod.LastMonth, Today, D(1, 1, 2025)));
        Assert.Equal("Nov 2025 – Oct 2026", HomeViewModel.DaysOf(RecapPeriod.LastYear, Today, D(1, 1, 2024)));
        // History starting inside them is said.
        Assert.Equal("6 Oct – 4 Nov · recorded since 20 Oct", HomeViewModel.DaysOf(RecapPeriod.LastMonth, Today, D(10, 20)));

        // Each is offered once history reaches into it.
        Assert.Equal([RecapPeriod.Yesterday, RecapPeriod.LastWeek, RecapPeriod.LastMonth], HomeViewModel.PeriodsFor(D(10, 20), Today));
    }
}

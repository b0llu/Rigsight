using Rigsight.Core.Reports;

namespace Rigsight.Tests.Reports;

/// <summary>The temperature graph's rolling periods and custom ranges (see <see cref="ChartRange"/>).</summary>
public sealed class ChartRangeTests
{
    private static readonly DateTime Today = new(2026, 10, 16);

    [Fact]
    public void A_rolling_period_ends_with_its_last_day()
    {
        Assert.Equal((new DateTime(2026, 10, 10), new DateTime(2026, 10, 17)), ChartRange.Rolling(ChartRange.Week, Today));
        Assert.Equal((new DateTime(2026, 9, 17), new DateTime(2026, 10, 17)), ChartRange.Rolling(ChartRange.Month, Today));
        Assert.Equal((new DateTime(2025, 10, 17), new DateTime(2026, 10, 17)), ChartRange.Rolling(ChartRange.Year, Today));
        // A time of day doesn't matter: the day does.
        Assert.Equal(ChartRange.Rolling(ChartRange.Week, Today), ChartRange.Rolling(ChartRange.Week, Today.AddHours(15)));
    }

    [Fact]
    public void The_arrows_step_by_the_periods_own_length()
    {
        Assert.Equal(Today.AddDays(-7), ChartRange.Step(ChartRange.Week, Today, -1));
        Assert.Equal(Today.AddDays(-30), ChartRange.Step(ChartRange.Month, Today, -1));
        Assert.Equal(Today.AddYears(-1), ChartRange.Step(ChartRange.Year, Today, -1));
        Assert.Equal(Today, ChartRange.Step(ChartRange.Week, Today.AddDays(-7), 1));
        // One period ends where the one after begins.
        var day = Today;
        for (int i = 0; i < 5; i++)
        {
            var before = ChartRange.Step(ChartRange.Month, day, -1);
            Assert.Equal(ChartRange.Rolling(ChartRange.Month, day).From, ChartRange.Rolling(ChartRange.Month, before).To);
            day = before;
        }
    }

    [Theory]
    [InlineData(ChartRange.Week, "Last 7 days", "3–9 Oct")]
    [InlineData(ChartRange.Month, "Last 30 days", "18 Aug – 16 Sep")]
    [InlineData(ChartRange.Year, "Last 12 months", "17 Oct 2024 – 16 Oct 2025")]
    public void A_period_is_named_by_its_dates_once_it_is_over(int window, string now, string before)
    {
        Assert.Equal(now, ChartRange.Label(window, Today, Today));
        Assert.Equal(before, ChartRange.Label(window, ChartRange.Step(window, Today, -1), Today));
        Assert.Equal($"in the last {ChartRange.Length(window)}", ChartRange.Words(window, Today, Today));
    }

    [Fact]
    public void An_earlier_year_is_named_with_its_dates()
    {
        Assert.Equal("29 Dec 2025 – 4 Jan 2026", ChartRange.Label(ChartRange.Week, new DateTime(2026, 1, 4), Today));
        Assert.Equal("1–7 Dec 2025", ChartRange.Label(ChartRange.Week, new DateTime(2025, 12, 7), Today));
        Assert.Equal("in the 7 days to 9 Oct", ChartRange.Words(ChartRange.Week, new DateTime(2026, 10, 9), Today));
        Assert.Equal("in the 7 days to 7 Dec 2025", ChartRange.Words(ChartRange.Week, new DateTime(2025, 12, 7), Today));
    }

    [Theory]
    [InlineData(ChartRange.Week, 0, 3600)]
    [InlineData(ChartRange.Month, 0, 3600)]
    [InlineData(ChartRange.Year, 0, 86400)]
    [InlineData(ChartRange.Custom, 5, 60)]
    [InlineData(ChartRange.Custom, 48, 60)]
    [InlineData(ChartRange.Custom, 49, 3600)]
    [InlineData(ChartRange.Custom, 62 * 24, 3600)]
    [InlineData(ChartRange.Custom, 62 * 24 + 1, 86400)]
    [InlineData(0, 0, 60)]
    public void A_point_stands_for_a_minute_an_hour_or_a_day(int window, int hours, int step)
    {
        Assert.Equal(step, ChartRange.StepSeconds(window, Today, Today.AddHours(hours)));
    }

    [Fact]
    public void A_period_still_going_on_ends_at_the_point_now_is_in()
    {
        var now = Today.AddHours(14).AddMinutes(20).AddSeconds(30);
        var end = Today.AddDays(1);
        Assert.Equal(Today.AddHours(15), ChartRange.ShownEnd(end, 3600, now));
        Assert.Equal(Today.AddDays(1), ChartRange.ShownEnd(end, 86400, now));
        Assert.Equal(Today.AddHours(14).AddMinutes(21), ChartRange.ShownEnd(end, 60, now));
        // On the hour itself: up to now.
        Assert.Equal(Today.AddHours(14), ChartRange.ShownEnd(end, 3600, Today.AddHours(14)));
        // One that is over ends at its end.
        Assert.Equal(Today, ChartRange.ShownEnd(Today, 3600, now));
    }
}

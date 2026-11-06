using Rigsight.Controls;

namespace Rigsight.Tests.App;

/// <summary>
/// How Reports' figures move from one period to the next (issue #11, mock-up ReK33EVbdKbsoarY7jwYXQ; 1.6.1, after 1.6.0's
/// motion was over before the first new frame showed it): figures count when they read alike, and the totals over a
/// month's bars shorten rather than run into each other.
/// </summary>
public sealed class ReportsMotionTests
{
    [Theory]
    [InlineData("36h 42m", "78h 06m", 0.5, "57h 24m")]
    [InlineData("38m", "12h 45m", 1, "12h 45m")]
    [InlineData("53 GB", "512 GB", 0, "53 GB")]
    [InlineData("5.8 kWh", "30.6 kWh", 0.5, "18.2 kWh")]
    [InlineData("34h 45m in use · 4h 48m away", "241h 10m in use · 20h 00m away", 1, "241h 10m in use · 20h 00m away")]
    public void A_figure_counts_from_the_old_one_when_the_two_read_alike(string from, string to, double t, string expected) =>
        Assert.Equal(expected, CountUp.Between(from, to, t));

    [Theory]
    [InlineData("947 GB", "1.1 TB")]
    [InlineData("None", "2")]
    [InlineData("—", "10h 19m")]
    [InlineData("12 GB", "12 GB used")]
    public void A_figure_that_reads_differently_just_changes(string from, string to) => Assert.Null(CountUp.Between(from, to, 0.5));

    [Fact]
    public void The_motion_eases_out_as_the_mock_up_does()
    {
        Assert.Equal(0, ChartTween.Ease(0));
        Assert.Equal(1, ChartTween.Ease(1));
        Assert.Equal(1 - Math.Pow(0.5, 4), ChartTween.Ease(0.5), 9);
        Assert.Equal(250, ChartTween.Duration.TotalMilliseconds);
        // Two frames pass before it starts: the one that lays the new figures out, and the first that shows them.
        Assert.Equal(2, Frames.Settle);
    }

    [Theory]
    [InlineData(11 * 3600 + 37 * 60, "12h")]
    [InlineData(2 * 3600 + 41 * 60, "3h")]
    [InlineData(38 * 60, "38m")]
    public void A_total_with_no_room_is_said_in_whole_hours(double sec, string expected) => Assert.Equal(expected, DailyBars.WholeHours(sec));
}

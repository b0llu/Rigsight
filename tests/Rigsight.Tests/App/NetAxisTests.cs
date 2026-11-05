using Rigsight.Controls;

namespace Rigsight.Tests.App;

/// <summary>
/// The Network graph's axis: four round steps, the smallest that hold the highest bar. An hour of 42 GB went on an
/// axis of 80 GB, half the graph empty above it (the user, 2026-11-05).
/// </summary>
public sealed class NetAxisTests
{
    private const double MB = 1L << 20, GB = 1L << 30;

    [Theory]
    [InlineData(42, 60)]    // 15 GB steps, not 20
    [InlineData(40, 40)]    // exactly fits
    [InlineData(19, 20)]
    [InlineData(21, 24)]    // 6 GB steps
    [InlineData(9, 10)]     // 2.5 GB steps
    [InlineData(70, 80)]
    [InlineData(130, 160)]
    public void The_top_is_the_smallest_round_one_over_the_highest(double highestGb, double topGb) =>
        Assert.Equal(topGb * GB, NetChartBase.NiceTop(highestGb * GB));

    [Fact]
    public void The_highest_bar_fills_most_of_the_graph()
    {
        for (double v = 1 * MB; v < 900 * GB; v *= 1.07)
        {
            double top = NetChartBase.NiceTop(v);
            Assert.True(top >= v, $"{v / GB:0.###} GB over a top of {top / GB:0.###}");
            Assert.True(v / top > 0.48, $"{v / GB:0.###} GB on a top of {top / GB:0.###}");
        }
        Assert.Equal(4 * MB, NetChartBase.NiceTop(0));
    }
}

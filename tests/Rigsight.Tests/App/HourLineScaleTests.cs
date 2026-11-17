using Rigsight.Controls;

namespace Rigsight.Tests.App;

/// <summary>
/// The CPU and memory charts under an opened app (the user, 2026-11-16: CPU today "looks floating in air"): a round top
/// that never goes under 10% or 512 MB, so a rise from 0.3% to 3% isn't drawn as a cliff.
/// </summary>
public sealed class HourLineScaleTests
{
    [Theory]
    [InlineData(3.1, 10)]
    [InlineData(0, 10)]
    [InlineData(9.5, 25)]
    [InlineData(34, 50)]
    [InlineData(95, 100)]
    public void A_cpu_chart_tops_out_at_a_round_share_never_under_10_percent(double most, double top) =>
        Assert.Equal(top, HourLine.TopFor(most, HourLineScale.Percent));

    [Theory]
    [InlineData(120, 512)]
    [InlineData(1150, 2048)]
    [InlineData(6300, 8192)]
    [InlineData(200_000, 262_144)]
    public void A_memory_chart_tops_out_at_a_round_size_never_under_512_MB(double most, double top) =>
        Assert.Equal(top, HourLine.TopFor(most, HourLineScale.Megabytes));

    [Fact]
    public void The_top_is_written_in_the_charts_own_unit()
    {
        Assert.Equal("10%", HourLine.TopText(10, HourLineScale.Percent));
        Assert.Equal("30m", HourLine.TopText(1800, HourLineScale.Seconds));
    }
}

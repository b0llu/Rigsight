using System.Net;
using Rigsight.Agent.Network;

namespace Rigsight.Tests.Agent;

/// <summary>The line check's maths: a minute's pings as one row, and which addresses are the home's.</summary>
public sealed class LineCheckTests
{
    [Fact]
    public void A_minute_of_pings_is_its_middle_its_longest_what_was_lost_and_the_jitter()
    {
        var row = LineCheck.Summarize([
            (0, 1, 20, true), (5, 1, 22, true), (10, null, 30, true), (15, 2, null, true), (20, 1, 21, true),
        ]);
        Assert.Equal(5, row.Sent);
        Assert.Equal((1, 2, 1), (row.LanMs, row.LanMax, row.LanLost));
        Assert.Equal((22, 30, 1), (row.IspMs, row.IspMax, row.IspLost)); // of four, the upper middle
        Assert.Equal(6, row.Jitter); // |22-20|, |30-22|, |21-30| on average: 6.3
    }

    [Fact]
    public void Without_the_providers_router_only_the_home_side_counts()
    {
        var row = LineCheck.Summarize([(0, 1, null, false), (5, 1, null, false)]);
        Assert.Null(row.IspMs);
        Assert.Equal(0, row.IspLost); // never tried isn't lost
        Assert.Null(row.Jitter);
        Assert.Equal(1, row.LanMs);
    }

    [Theory]
    [InlineData("192.168.10.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("100.66.0.1", false)]   // a provider's shared address: the provider's
    [InlineData("59.95.224.1", false)]
    [InlineData("172.32.0.1", false)]
    public void A_home_address_is_private_or_link_local(string address, bool home) =>
        Assert.Equal(home, LineCheck.IsHome(IPAddress.Parse(address)));
}

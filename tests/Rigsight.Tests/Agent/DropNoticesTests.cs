using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Tests.Agent;

/// <summary>
/// When the internet drops: told after 15 seconds, told again when it's back with how long, and one "keeps dropping"
/// instead of a stream (1.4.0, the user's words, 2026-11-05).
/// </summary>
public sealed class DropNoticesTests
{
    private const long T0 = 1_800_000_000;

    /// <summary>A second-by-second run: the drop going on from <paramref name="from"/> to <paramref name="to"/>, then its end.</summary>
    private static List<DropNotices.Said> Drop(DropNotices n, long from, long to, NetDropKind kind = NetDropKind.Internet, string? card = "Wi-Fi", bool? router = true)
    {
        var said = new List<DropNotices.Said>();
        for (long s = from; s < to; s++)
            if (n.Tick(s, (from, kind), null, card, router) is { } x) said.Add(x);
        if (n.Tick(to, null, to - from >= ConnectionWatch.MinDropSeconds ? new NetDrop(from, to, kind) : null, card, router) is { } end) said.Add(end);
        return said;
    }

    [Fact]
    public void A_drop_is_told_after_15_seconds_and_again_when_its_back_with_how_long()
    {
        var n = new DropNotices();
        var said = Drop(n, T0, T0 + 50);
        Assert.Equal(2, said.Count);
        Assert.Equal(("Your internet is down", "Your router is reachable, but there's no internet connection.", false), (said[0].Title, said[0].Body, said[0].Good));
        Assert.Equal(("Your internet is back", "Your connection was restored after 50 seconds of downtime.", true), (said[1].Title, said[1].Body, said[1].Good));
        Assert.All(said, s => Assert.Equal(T0, s.At)); // both open the drop's day
    }

    [Fact]
    public void A_short_drop_says_nothing_and_so_no_back_either()
    {
        var n = new DropNotices();
        Assert.Empty(Drop(n, T0, T0 + 12));
        Assert.Empty(Drop(n, T0 + 100, T0 + 100 + DropNotices.AfterSeconds - 1));
    }

    [Fact]
    public void What_broke_is_said_in_the_users_words()
    {
        Assert.Equal(("Your Wi-Fi disconnected", "Your PC lost its connection to the Wi-Fi network."), DropWords.Down(NetDropKind.Link, "Wi-Fi", null));
        Assert.Equal("Your Ethernet disconnected", DropWords.Down(NetDropKind.Link, "Ethernet", null).Title);
        Assert.Equal("Your network disconnected", DropWords.Down(NetDropKind.Link, null, null).Title); // never guessed as Wi-Fi
        Assert.Equal("Your PC is connected to the network, but there's no internet connection.", DropWords.Down(NetDropKind.Internet, "Wi-Fi", null).Body); // the router not checked
        Assert.Equal("Your PC is connected to the network, but there's no internet connection.", DropWords.Down(NetDropKind.Internet, "Wi-Fi", false).Body);
        Assert.Equal(("50 seconds", "1 minute", "2 minutes 10 seconds", "1 hour 5 minutes"), (DropWords.Long(50), DropWords.Long(60), DropWords.Long(130), DropWords.Long(3900)));
    }

    [Fact]
    public void Dropping_again_and_again_is_one_notice_then_quiet_until_ten_calm_minutes()
    {
        var n = new DropNotices();
        var all = new List<DropNotices.Said>();
        all.AddRange(Drop(n, T0, T0 + 30, NetDropKind.Link));
        all.AddRange(Drop(n, T0 + 60, T0 + 90, NetDropKind.Link));
        all.AddRange(Drop(n, T0 + 120, T0 + 150, NetDropKind.Link));
        all.AddRange(Drop(n, T0 + 200, T0 + 230, NetDropKind.Link)); // a fourth, inside the quiet
        Assert.Equal(["Your Wi-Fi disconnected", "Your internet is back", "Your Wi-Fi disconnected", "Your internet is back", "Your internet keeps dropping"], all.Select(s => s.Title));
        Assert.Equal("Your Wi-Fi disconnected 3 times in the last 10 minutes.", all[^1].Body);

        // Ten minutes with no drop: the next one is told again.
        for (long s = T0 + 231; s <= T0 + 230 + DropNotices.WindowSeconds; s++) n.Tick(s, null, null, "Wi-Fi", true);
        Assert.Equal("Your Wi-Fi disconnected", Drop(n, T0 + 900, T0 + 930, NetDropKind.Link)[0].Title);
    }
}

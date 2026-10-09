using System.Net;
using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Tests.Agent;

/// <summary>Telling where each packet went (this PC, the local network, the internet, a VPN's tunnel), and noting drops.</summary>
public unsafe class ConnectionTests
{
    // An IPv4 address as the network events carry it.
    private static uint V4(string a) => BitConverter.ToUInt32(IPAddress.Parse(a).GetAddressBytes());

    private static Route Classify6(AddressBook book, string a, string b)
    {
        var x = IPAddress.Parse(a).GetAddressBytes();
        var y = IPAddress.Parse(b).GetAddressBytes();
        fixed (byte* px = x, py = y) return book.Classify6(px, py);
    }

    private static readonly AddressBook Book = AddressBook.Of(["192.168.10.6", "2401:4900:1c2a::5"], tunnel: ["172.16.0.2", "2606:4700:110::62"]);

    [Theory]
    [InlineData("192.168.10.6", "142.250.183.14", "Internet")]
    [InlineData("142.250.183.14", "192.168.10.6", "Internet")] // either order: the events don't say which end is this PC
    [InlineData("172.16.0.2", "142.250.183.14", "Tunnel")]
    [InlineData("192.168.10.6", "192.168.10.20", "Lan")]
    [InlineData("192.168.10.6", "10.0.0.5", "Lan")]
    [InlineData("192.168.10.6", "239.255.255.250", "Lan")]
    [InlineData("127.0.0.1", "127.0.0.1", "Self")]
    [InlineData("192.168.10.6", "172.16.0.2", "Self")]
    public void IPv4_packets_are_told_apart(string a, string b, string expected) =>
        Assert.Equal(Enum.Parse<Route>(expected), Book.Classify4(V4(a), V4(b)));

    [Theory]
    [InlineData("2401:4900:1c2a::5", "2a00:1450:4009::200e", "Internet")]
    [InlineData("2606:4700:110::62", "2a00:1450:4009::200e", "Tunnel")]
    [InlineData("2401:4900:1c2a::5", "fe80::1", "Lan")]
    [InlineData("::1", "::1", "Self")]
    [InlineData("::ffff:192.168.10.6", "::ffff:192.168.10.1", "Lan")]
    public void IPv6_packets_are_told_apart(string a, string b, string expected) =>
        Assert.Equal(Enum.Parse<Route>(expected), Classify6(Book, a, b));

    [Fact]
    public void The_VPN_name_drops_what_says_adapter() =>
        Assert.Equal(["Cloudflare WARP", "WireGuard", "NordLynx"],
            new[] { "Cloudflare WARP Interface Tunnel", "WireGuard Tunnel", "NordLynx" }.Select(NetAdapters.VpnName));

    // ── Drops ──

    private static List<NetDrop> Watch(ConnectionWatch w, long start, params (int Seconds, bool? Online, bool Card, long? Bytes)[] steps)
    {
        var drops = new List<NetDrop>();
        long now = start;
        foreach (var (seconds, online, card, bytes) in steps)
            for (int i = 0; i < seconds; i++)
                if (w.Observe(++now, online, card, bytes) is { } d) drops.Add(d);
        return drops;
    }

    [Fact]
    public void A_drop_past_the_router_is_noted_when_it_ends()
    {
        var drops = Watch(new ConnectionWatch(), 1000, (60, true, true, 50_000), (40, false, true, 0), (5, true, true, 50_000));
        var d = Assert.Single(drops);
        Assert.Equal(1061, d.Start);
        Assert.Equal(1101, d.End);
        Assert.Equal(NetDropKind.Internet, d.Kind);
    }

    [Fact]
    public void A_cable_or_WiFi_drop_is_the_link()
    {
        var d = Assert.Single(Watch(new ConnectionWatch(), 1000, (10, true, true, null), (30, false, false, null), (1, true, true, null)));
        Assert.Equal(NetDropKind.Link, d.Kind);
    }

    [Fact]
    public void A_few_seconds_is_a_flicker_not_a_drop() =>
        Assert.Empty(Watch(new ConnectionWatch(), 1000, (10, true, true, null), (ConnectionWatch.MinDropSeconds - 1, false, true, null), (5, true, true, null)));

    [Fact]
    public void Apps_still_downloading_mean_Windows_was_wrong_not_the_internet() =>
        Assert.Empty(Watch(new ConnectionWatch(), 1000, (10, true, true, 0), (60, false, true, 2_000_000), (5, true, true, 0)));

    [Fact]
    public void Nothing_counts_while_the_network_comes_up_after_starting_or_waking()
    {
        var w = new ConnectionWatch();
        w.Settle(1000);
        Assert.Empty(Watch(w, 1000, (60, false, false, 0), (10, true, true, 0)));
        // Once settled, a drop counts again.
        Assert.Single(Watch(w, 1100, (30, false, true, 0), (1, true, true, 0)));
    }

    [Fact]
    public void A_sleep_in_the_middle_is_no_drop()
    {
        var w = new ConnectionWatch();
        Watch(w, 1000, (10, true, true, 0), (20, false, true, 0));
        // The PC slept for an hour, and the internet's back when it wakes.
        Assert.Null(w.Observe(1030 + 3600, true, true, 0));
    }

    [Fact]
    public void A_drop_still_going_on_is_kept_as_far_as_it_has_got()
    {
        // The PC is switched off, or put to sleep, with the internet still down: the drop was only ever written when the
        // internet came back, so it was lost.
        var w = new ConnectionWatch();
        Watch(w, 1000, (10, true, true, 50_000), (ConnectionWatch.MinOngoingSeconds - 1, false, true, 0));
        Assert.Null(w.Ongoing(1010 + ConnectionWatch.MinOngoingSeconds - 1)); // not yet a minute
        Watch(w, 1010 + ConnectionWatch.MinOngoingSeconds - 1, (120, false, true, 0));
        long now = 1010 + ConnectionWatch.MinOngoingSeconds - 1 + 120;
        var going = w.Ongoing(now);
        Assert.Equal(new NetDrop(1011, now, NetDropKind.Internet), going);
        // When it ends, it's the same drop (the same start), with its real end: written over the last.
        var ended = Assert.Single(Watch(w, now, (30, false, true, 0), (1, true, true, 0)));
        Assert.Equal(1011, ended.Start);
        Assert.Equal(now + 31, ended.End);
        Assert.Null(w.Ongoing(now + 32));
    }

    [Fact]
    public void A_drop_going_on_with_apps_still_downloading_is_not_kept()
    {
        var w = new ConnectionWatch();
        Watch(w, 1000, (10, true, true, 0), (180, false, true, 2_000_000));
        Assert.Null(w.Ongoing(1190));
    }

    [Fact]
    public void Nothing_is_kept_while_online_or_once_the_PC_sleeps()
    {
        var w = new ConnectionWatch();
        Watch(w, 1000, (10, true, true, 0));
        Assert.Null(w.Ongoing(1010));
        Watch(w, 1010, (200, false, false, 0));
        Assert.Equal(NetDropKind.Link, w.Ongoing(1210)?.Kind);
        w.Suspend();
        Assert.Null(w.Ongoing(1211));
    }

    [Fact]
    public void Windows_not_knowing_changes_nothing() =>
        Assert.Empty(Watch(new ConnectionWatch(), 1000, (10, true, true, 0), (60, null, true, 0), (5, true, true, 0)));
}

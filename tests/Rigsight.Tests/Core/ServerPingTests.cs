using System.Net;
using Rigsight.Core.Data;
using Rigsight.Core.Net;
using Rigsight.Core.Protocol;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Core;

/// <summary>
/// The ping to each game's, call's and picked app's server (approved mock-up, 2026-11-16): which apps get a check, what's
/// said about each, the city in a server's name, and the usual from its own past.
/// </summary>
public sealed class ServerPingTests
{
    private static readonly IPAddress Singapore = IPAddress.Parse("104.29.141.190"), Steam = IPAddress.Parse("23.45.67.89");

    private static StreamPicker.Seen S(string exe, IPAddress at, ushort port, int sent, int received, long bytes = 3000, bool udp = true) =>
        new(exe, 100, at, port, udp, sent, received, bytes);

    private static List<StreamPicker.Chosen> Run(StreamPicker picker, int seconds, Func<long, IEnumerable<StreamPicker.Seen>> each,
        Func<string, int, bool>? isGame = null, IReadOnlyCollection<string>? picked = null, long start = 1)
    {
        List<StreamPicker.Chosen> chosen = [];
        for (long t = start; t < start + seconds; t++) chosen = picker.Second(t, each(t), isGame ?? ((_, _) => false), picked ?? [], 4);
        return chosen;
    }

    [Fact]
    public void A_call_is_found_from_a_two_way_stream_once_it_has_gone_on_10_seconds()
    {
        var picker = new StreamPicker();
        Assert.Empty(Run(picker, 10, _ => [S("Discord.exe", Singapore, 19310, 50, 50)]));
        var chosen = picker.Second(11, [S("Discord.exe", Singapore, 19310, 50, 50)], (_, _) => false, [], 4);
        var call = Assert.Single(chosen);
        Assert.Equal(("Discord.exe", "call", 19310), (call.Exe, call.Kind, call.Server.Port));
    }

    [Fact]
    public void A_quiet_call_is_found_from_its_keep_alive_every_5_seconds()
    {
        // Nobody talking: one small packet out every 5 seconds, its answer back a moment later (the next second here).
        var picker = new StreamPicker();
        var chosen = Run(picker, 40, t => t % 5 == 0 ? [S("Discord.exe", Singapore, 50010, 1, 0, bytes: 0)]
            : t % 5 == 1 && t > 1 ? [S("Discord.exe", Singapore, 50010, 0, 1, bytes: 8)] : []);
        Assert.Equal(("Discord.exe", "call", 50010), (Assert.Single(chosen).Exe, chosen[0].Kind, chosen[0].Server.Port));
    }

    [Fact]
    public void A_call_only_listened_to_is_found_and_the_apps_web_traffic_beside_it_isnt_taken_for_it()
    {
        var picker = new StreamPicker();
        var chosen = Run(picker, 30, t =>
        [
            // Someone else talking: 50 packets a second in, only the keep-alive out.
            S("Discord.exe", Singapore, 50010, t % 5 == 0 ? 1 : 0, 50),
            // Discord loading pictures over QUIC at the same time, busier than the call.
            S("Discord.exe", Steam, 443, 80, 120, bytes: 90_000),
        ]);
        Assert.Equal(("call", 50010), (Assert.Single(chosen).Kind, chosen[0].Server.Port));
    }

    [Fact]
    public void A_call_apps_short_exchange_or_ended_call_gets_no_check()
    {
        var picker = new StreamPicker();
        // 8 seconds of traffic, then nothing.
        Assert.Empty(Run(picker, 20, t => t <= 8 ? [S("Discord.exe", Singapore, 50010, 20, 20)] : []));
        // A call that ended 11 seconds ago.
        picker = new StreamPicker();
        Assert.NotEmpty(Run(picker, 25, _ => [S("Discord.exe", Singapore, 50010, 20, 20)]));
        Assert.Empty(Run(picker, 11, _ => [], start: 26));
    }

    [Fact]
    public void A_download_a_browser_and_a_short_exchange_get_no_check()
    {
        var picker = new StreamPicker();
        var chosen = Run(picker, 40, t =>
        [
            // A download over UDP (QUIC): a lot one way, a trickle back.
            S("steam.exe", Steam, 443, 20, 900, bytes: 12_000_000),
            // A browser: a few packets now and then.
            S("chrome.exe", Steam, 443, t % 7 == 0 ? 3 : 0, t % 7 == 0 ? 4 : 0),
            // Something both ways, but only for 10 seconds.
            S("tool.exe", Singapore, 3478, t <= 10 ? 30 : 0, t <= 10 ? 30 : 0),
        ]);
        Assert.Empty(chosen);
    }

    [Fact]
    public void A_game_counts_from_any_steady_stream_and_comes_before_a_call()
    {
        var picker = new StreamPicker();
        var chosen = Run(picker, 30, _ => [S("Discord.exe", Singapore, 19310, 50, 50), S("dota2.exe", Steam, 27015, 2, 2)],
            isGame: (exe, _) => exe == "dota2.exe");
        Assert.Equal(["dota2.exe", "Discord.exe"], chosen.Select(c => c.Exe));
        Assert.Equal(["game", "call"], chosen.Select(c => c.Kind));
    }

    [Fact]
    public void A_picked_app_is_checked_over_tcp_too_and_first()
    {
        var picker = new StreamPicker();
        var chosen = Run(picker, 30, _ => [S("Discord.exe", Singapore, 19310, 50, 50), S("trader.exe", Steam, 8443, 3, 3, udp: false)],
            picked: ["trader.exe"]);
        Assert.Equal(("trader.exe", "picked"), (chosen[0].Exe, chosen[0].Kind));
    }

    [Fact]
    public void An_apps_busiest_server_is_the_one_checked()
    {
        var picker = new StreamPicker();
        var chosen = Run(picker, 30, _ => [S("Discord.exe", Singapore, 19310, 50, 50), S("Discord.exe", Steam, 19315, 5, 5)]);
        Assert.Equal(Singapore, Assert.Single(chosen).Server.Address);
    }

    /// <summary>A quiet Discord call as the trace saw it (2026-11-17): 8 bytes out every 5 s and back in about 104 ms, 52 bytes
    /// in every second unasked, 60 bytes out every 5 s never answered.</summary>
    private static EchoTimer QuietCall(int seconds, Func<int, int?>? back = null)
    {
        var timer = new EchoTimer();
        for (int s = 0; s < seconds; s++)
        {
            double at = s * 1000.0;
            timer.Packet(at, sent: false, 52);
            if (s % 5 == 2) timer.Packet(at + 275, sent: true, 60);
            if (s % 5 == 3)
            {
                timer.Packet(at + 54, sent: true, 8);
                if ((back ?? (_ => 104))(s) is int ms) timer.Packet(at + 54 + ms, sent: false, 8);
            }
        }
        timer.Expire(seconds * 1000.0);
        return timer;
    }

    [Fact]
    public void A_calls_own_keep_alive_is_found_after_three_and_timed()
    {
        Assert.Null(QuietCall(10).Size());
        var timer = QuietCall(30, s => s == 18 ? 155 : 104);
        Assert.Equal(8, timer.Size());
        Assert.Equal([104, 104, 104, 155, 104, 104], timer.Between(8, 0, 30_000).Select(e => e.Ms));
        // The 60 bytes that never come back are no echo, and the 52 that come unasked aren't one either.
        Assert.All(timer.Between(60, 0, 30_000), e => Assert.Null(e.Ms));
    }

    [Fact]
    public void A_keep_alive_not_back_within_a_second_is_lost()
    {
        var timer = QuietCall(30, s => s == 13 ? null : 104);
        Assert.Equal(8, timer.Size());
        Assert.Equal([104, 104, null, 104, 104, 104], timer.Between(8, 0, 30_000).Select(e => e.Ms));
    }

    [Fact]
    public void A_talking_calls_voice_is_never_taken_for_an_echo()
    {
        // Small voice packets both ways every 20 ms, of every size: some sizes match by chance, but they also come unasked.
        var timer = new EchoTimer();
        var random = new Random(4);
        for (int i = 0; i < 1500; i++)
        {
            timer.Packet(i * 20.0, sent: true, random.Next(30, 65));
            timer.Packet(i * 20.0 + 7, sent: false, random.Next(30, 65));
        }
        timer.Expire(30_000);
        Assert.Null(timer.Size());
    }

    [Fact]
    public void Times_that_jump_around_are_no_echo()
    {
        var timer = QuietCall(40, s => 40 + s * 37 % 700);
        Assert.Null(timer.Size());
    }

    private static ServerLive Live(int? ms = 97, int lost = 0, int sent = 120, int? low = 90, int? high = 100, string kind = "call", string? place = "Singapore", bool noAnswer = false) =>
        new() { Exe = "Discord.exe", Kind = kind, Server = "104.29.141.190", Place = place, Ms = ms, Lost = lost, Sent = sent, UsualLow = low, UsualHigh = high, NoAnswer = noAnswer };

    [Fact]
    public void The_words_say_the_figure_its_usual_as_a_range_and_losses_as_a_count()
    {
        Assert.Equal("97 ms", ServerWords.Figure(Live()));
        Assert.Equal("–", ServerWords.Figure(Live(ms: null, noAnswer: true)));
        Assert.Equal("", ServerWords.Figure(Live(ms: null)));
        Assert.Equal("Call, Singapore", ServerWords.Under(Live()));
        Assert.Equal("Game", ServerWords.Under(Live(kind: "game", place: null)));
        Assert.Null(ServerWords.Under(Live(kind: "picked", place: null)));
        Assert.Equal("Discord's server in Singapore, 97 ms\nUsual 90 to 100 ms. None of the last 120 checks lost.", ServerWords.Tip("Discord", Live()));
        Assert.Equal(", usual 90 to 100 ms, 3 of the last 120 checks lost", ServerWords.SummaryRest(Live(lost: 3)));
        Assert.Equal("Discord call", ServerWords.Named("Discord", "call"));
        Assert.Equal("Dota 2", ServerWords.Named("Dota 2", "game"));
        Assert.Contains("Too soon to say", ServerWords.Tip("Discord", Live(low: null, high: null)));
        Assert.Equal("Discord's server in Singapore doesn't answer checks, so its ping can't be shown.", ServerWords.Tip("Discord", Live(noAnswer: true)));
    }

    [Fact]
    public void A_figure_needs_a_look_only_well_above_its_usual_or_with_losses()
    {
        Assert.False(ServerWords.NeedsLook(Live(ms: 140)));   // 1.4 times the top of its usual
        Assert.True(ServerWords.NeedsLook(Live(ms: 168)));
        Assert.False(ServerWords.NeedsLook(Live(ms: 12, low: 2, high: 3)));   // half again, but under 10 ms more
        Assert.True(ServerWords.NeedsLook(Live(lost: 3)));
        Assert.False(ServerWords.NeedsLook(Live(lost: 1)));
        Assert.False(ServerWords.NeedsLook(Live(ms: 400, low: null, high: null)));   // no usual yet: nothing to be above
    }

    [Fact]
    public void The_lead_server_is_one_that_needs_a_look_then_a_game_then_a_call()
    {
        var game = Live(kind: "game", ms: 60, low: 58, high: 66);
        var call = Live(kind: "call");
        Assert.Same(game, ServerWords.Lead([call, game]));
        var slowCall = Live(kind: "call", ms: 180);
        Assert.Same(slowCall, ServerWords.Lead([game, slowCall]));
        Assert.Null(ServerWords.Lead([Live(ms: null)]));
        Assert.Null(ServerWords.Lead(null));
    }

    [Theory]
    [InlineData("c-sin02-c6f0c3aa.discord.media", "Singapore")]
    [InlineData("c-bom03-9361fcdd.discord.media", "Mumbai")]
    [InlineData("valve-fra1.steamserver.net", "Frankfurt")]
    [InlineData("ec2-3-1-2-3.ap-southeast-1.compute.amazonaws.com", null)]
    [InlineData("104.29.141.190", null)]
    [InlineData("", null)]
    public void A_city_is_read_only_from_an_airport_code_or_a_city_in_the_name(string host, string? city) =>
        Assert.Equal(city, ServerPlaces.FromHostName(host));

    [Fact]
    public void Known_call_and_remote_play_apps_are_named_by_their_exe()
    {
        Assert.True(AppKinds.IsCall("Discord.exe"));
        Assert.True(AppKinds.IsCall("ms-teams.exe"));
        Assert.True(AppKinds.IsRemote("parsecd.exe"));
        Assert.False(AppKinds.IsCall("chrome.exe"));
    }

    [Fact]
    public void Server_minutes_are_kept_pruned_and_give_a_usual_from_the_same_network_after_an_hour()
    {
        using var t = new TestDb();
        long day = TimeUtil.ToUnix(new DateTime(2026, 11, 10));
        for (int i = 0; i < 59; i++) t.Db.WriteNetServer(new NetServerMinute(day + i * 60, "Discord.exe", "104.29.141." + (i % 2 == 0 ? "190" : "14"), "call", 12, 90 + i % 10, 120, 0));
        Assert.Null(t.Db.NetServerUsual("Discord.exe", "104.29.141.77", day + 86400));   // under an hour of minutes
        t.Db.WriteNetServer(new NetServerMinute(day + 59 * 60, "Discord.exe", "104.29.141.190", "call", 12, 95, 120, 0));
        // Another region of the same service, and another app: neither counts.
        t.Db.WriteNetServer(new NetServerMinute(day + 3600, "Discord.exe", "162.159.1.1", "call", 12, 20, 25, 0));
        t.Db.WriteNetServer(new NetServerMinute(day + 3600, "dota2.exe", "104.29.141.5", "game", 12, 300, 400, 0));
        var usual = t.Db.NetServerUsual("Discord.exe", "104.29.141.77", day + 86400);
        Assert.NotNull(usual);
        Assert.InRange(usual!.Value.Low, 90, 93);
        Assert.InRange(usual.Value.High, 96, 99);
        Assert.Equal(62, t.Db.GetNetServers(day, day + 86400).Count);

        t.Db.Prune(day + 30 * 60);
        Assert.Equal(32, t.Db.GetNetServers(day, day + 86400).Count);
        t.Db.ClearHistory();
        Assert.Empty(t.Db.GetNetServers(0, long.MaxValue));
    }
}

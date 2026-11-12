using Rigsight.Agent.Network;

namespace Rigsight.Tests.Agent;

/// <summary>
/// A network trace that delivers nothing while the network cards are busy (as on 5 Oct 2026, when a test copy's
/// leftover trace silenced the installed one for seven hours): started again, then leftovers cleared, then left for a while.
/// </summary>
public class TraceWatchTests
{
    private const long MB = 1_000_000;

    /// <summary>A check a minute: each step is what went through the cards and how many events the trace delivered.</summary>
    private static List<TraceRemedy> Run(TraceWatch watch, params (long CardMb, long Events)[] minutes)
    {
        var said = new List<TraceRemedy>();
        long card = 0, events = 0, now = 1_790_000_000;
        said.Add(watch.Check(now, card, events));
        foreach (var (mb, n) in minutes)
        {
            card += mb * MB;
            events += n;
            said.Add(watch.Check(now += 60, card, events));
        }
        return said;
    }

    [Fact]
    public void A_trace_that_keeps_delivering_is_left_alone()
    {
        var said = Run(new TraceWatch(), (40, 30_000), (0, 12), (300, 200_000), (1, 900));
        Assert.All(said, r => Assert.Equal(TraceRemedy.None, r));
    }

    [Fact]
    public void A_quiet_network_is_not_a_quiet_trace()
    {
        // Nothing through the cards to speak of (the PC idle, or offline): no events is as it should be.
        var said = Run(new TraceWatch(), (0, 0), (1, 0), (4, 0), (0, 0), (2, 0), (0, 0));
        Assert.All(said, r => Assert.Equal(TraceRemedy.None, r));
    }

    [Fact]
    public void A_busy_network_with_no_events_restarts_the_trace_then_clears_leftovers_then_waits()
    {
        var watch = new TraceWatch();
        var said = Run(watch, (50, 20_000), (30, 0), (30, 0), (30, 0), (30, 0), (30, 0), (30, 0), (30, 0), (30, 0));
        Assert.Equal([TraceRemedy.None, TraceRemedy.None,
            TraceRemedy.None, TraceRemedy.Restart,      // two quiet minutes: start it again
            TraceRemedy.None, TraceRemedy.ClearOthers,  // two more: stop what test copies left, start it again
            TraceRemedy.None, TraceRemedy.GiveUp,       // two more: said once
            TraceRemedy.None, TraceRemedy.None], said); // and not again for a while
    }

    [Fact]
    public void A_trace_that_could_not_be_started_is_tried_again_then_leftovers_cleared_then_said()
    {
        var watch = new TraceWatch();
        long now = 1_790_000_000;
        Assert.Equal(TraceRemedy.Restart, watch.Missing(now += 60));
        Assert.Equal(TraceRemedy.ClearOthers, watch.Missing(now += 60));
        Assert.False(watch.Stalled);
        Assert.Equal(TraceRemedy.GiveUp, watch.Missing(now += 60));
        Assert.True(watch.Stalled); // nothing is being recorded: the Network page says so

        // Left alone until the wait is over, then the same again.
        Assert.Equal(TraceRemedy.None, watch.Missing(now += 60));
        Assert.Equal(TraceRemedy.None, watch.Missing(now + TraceWatch.RetrySeconds - 120));
        Assert.Equal(TraceRemedy.Restart, watch.Missing(now += TraceWatch.RetrySeconds));

        // That start worked and events arrive: recovered, and no longer said.
        watch.Restarted();
        Assert.Equal(TraceRemedy.Recovered, watch.Check(now += 60, 10 * MB, 400));
        Assert.False(watch.Stalled);
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, 20 * MB, 900));
    }

    [Fact]
    public void After_giving_up_it_tries_again_later()
    {
        var watch = new TraceWatch();
        long now = 1_790_000_000, card = 0;
        var said = new List<TraceRemedy>();
        for (int minute = 0; minute <= 6 + TraceWatch.RetrySeconds / 60 + 2; minute++)
            said.Add(watch.Check(now += 60, card += 30 * MB, 0));
        Assert.Equal(TraceRemedy.GiveUp, said[6]);
        Assert.All(said.Skip(7).Take(TraceWatch.RetrySeconds / 60 - 1), r => Assert.Equal(TraceRemedy.None, r));
        Assert.Equal(TraceRemedy.Restart, said[6 + TraceWatch.RetrySeconds / 60 + 1]); // two quiet checks once the wait is over
    }

    [Fact]
    public void Events_after_a_remedy_are_a_recovery_said_once()
    {
        var watch = new TraceWatch();
        long now = 1_790_000_000, card = 0;
        watch.Check(now, card, 500);
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, card += 30 * MB, 500));
        Assert.Equal(TraceRemedy.Restart, watch.Check(now += 60, card += 30 * MB, 500));
        watch.Restarted(); // the new trace counts from nothing
        Assert.Equal(TraceRemedy.Recovered, watch.Check(now += 60, card += 30 * MB, 8_000));
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, card += 30 * MB, 16_000));
        // Quiet again later: from the first remedy again.
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, card += 30 * MB, 16_000));
        Assert.Equal(TraceRemedy.Restart, watch.Check(now += 60, card += 30 * MB, 16_000));
    }

    [Fact]
    public void It_is_stalled_from_giving_up_until_events_come_in_again()
    {
        var watch = new TraceWatch();
        long now = 1_790_000_000, card = 0;
        var said = new List<TraceRemedy>();
        for (int minute = 0; minute <= 6; minute++)
        {
            said.Add(watch.Check(now += 60, card += 30 * MB, 0));
            Assert.Equal(said[^1] == TraceRemedy.GiveUp, watch.Stalled); // not while the remedies are still being tried
        }
        Assert.Equal(TraceRemedy.GiveUp, said[^1]);
        // Still nothing, through the wait: stalled all along.
        for (int minute = 0; minute < 10; minute++) watch.Check(now += 60, card += 30 * MB, 0);
        Assert.True(watch.Stalled);
        // Whatever was in the way went (the other program closed): said once, and no longer stalled.
        Assert.Equal(TraceRemedy.Recovered, watch.Check(now += 60, card += 30 * MB, 5_000));
        Assert.False(watch.Stalled);
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, card += 30 * MB, 9_000));
    }

    [Fact]
    public void One_quiet_check_or_unreadable_counters_do_nothing()
    {
        var watch = new TraceWatch();
        long now = 1_790_000_000;
        watch.Check(now, 0, 0);
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, 30 * MB, 0));
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, 60 * MB, 4_000)); // it delivered: the count starts over
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, 90 * MB, 4_000));
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, null, 4_000));    // counters unreadable
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, 150 * MB, 4_000)); // nothing to compare with
        Assert.Equal(TraceRemedy.None, watch.Check(now += 60, 10 * MB, 4_000));  // a card's counters started over
        Assert.Equal(TraceRemedy.Restart, watch.Check(now += 60, 40 * MB, 4_000));
    }
    // Issue #13 (a laptop, 11 Nov 2026): the trace went silent for hours and the taskbar's Down and Up showed dashes with
    // it. While silent, the whole PC's speed comes from the network cards and still shows; the apps don't, and it travels
    // to the app over the pipe.
    [Fact]
    public void While_the_trace_is_silent_the_cards_speed_still_shows()
    {
        Assert.True(new Rigsight.Core.Protocol.NetLive { Down = 5, Up = 1 }.HasSpeed);
        Assert.False(new Rigsight.Core.Protocol.NetLive { Stalled = true }.HasSpeed);
        var cards = new Rigsight.Core.Protocol.NetLive { Stalled = true, FromCards = true, Down = 2_500_000, Up = 40_000 };
        Assert.True(cards.HasSpeed);
        var back = System.Text.Json.JsonSerializer.Deserialize<Rigsight.Core.Protocol.NetLive>(System.Text.Json.JsonSerializer.Serialize(cards))!;
        Assert.Equal((true, true, 2_500_000d), (back.Stalled, back.FromCards, back.Down));
        Assert.Empty(back.Apps);
    }

    [Fact]
    public void Every_running_trace_session_can_be_listed_for_the_log()
    {
        // Listing needs no admin rights on most PCs; it must never throw, whatever it finds.
        var sessions = NetworkTrace.Running();
        Assert.All(sessions, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

}

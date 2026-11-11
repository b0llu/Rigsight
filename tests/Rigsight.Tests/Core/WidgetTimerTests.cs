using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>A timer tile's arithmetic (widgets part two): start, pause, carry on, run out, reset; a countdown's days.</summary>
public class WidgetTimerTests
{
    [Fact]
    public void A_timer_counts_down_pauses_and_runs_out()
    {
        var t = new WidgetTile { Kind = WidgetTiles.Timer, Minutes = 25 };
        Assert.Equal("25:00", WidgetTimer.Shown(t, 0));
        WidgetTimer.StartOrPause(t, 1_000);
        Assert.True(WidgetTimer.Running(t));
        Assert.Equal("24:00", WidgetTimer.Shown(t, 61_000));
        WidgetTimer.StartOrPause(t, 61_000); // paused a minute in
        Assert.Equal("24:00", WidgetTimer.Shown(t, 900_000));
        WidgetTimer.StartOrPause(t, 900_000);
        Assert.False(WidgetTimer.IsUp(t, 900_000 + 23 * 60_000));
        Assert.True(WidgetTimer.IsUp(t, 900_000 + 24 * 60_000));
        WidgetTimer.Stop(t);
        Assert.Equal("00:00", WidgetTimer.Shown(t, 2_000_000));
        WidgetTimer.StartOrPause(t, 3_000_000); // started again: from its length
        Assert.Equal("25:00", WidgetTimer.Shown(t, 3_000_000));
        WidgetTimer.Reset(t);
        Assert.False(WidgetTimer.Running(t));
    }

    [Fact]
    public void A_stopwatch_counts_up_with_hours_when_there_are_any()
    {
        var t = new WidgetTile { Kind = WidgetTiles.Timer, TimerKind = "Stopwatch" };
        WidgetTimer.StartOrPause(t, 0);
        Assert.Equal("01:23", WidgetTimer.Shown(t, 83_400));
        Assert.Equal("1:00:05", WidgetTimer.Shown(t, 3_605_000));
        Assert.False(WidgetTimer.IsUp(t, long.MaxValue / 2));
    }

    [Fact]
    public void A_countdown_counts_whole_days()
    {
        var t = new WidgetTile { Kind = WidgetTiles.Timer, TimerKind = "Countdown", Until = new DateTime(2026, 11, 22) };
        Assert.Equal(11, WidgetTimer.DaysLeft(t, new DateTime(2026, 11, 11, 23, 59, 0)));
        Assert.Equal(-1, WidgetTimer.DaysLeft(t, new DateTime(2026, 11, 23, 0, 1, 0)));
        Assert.Null(WidgetTimer.DaysLeft(new WidgetTile { Kind = WidgetTiles.Timer, TimerKind = "Countdown" }, DateTime.Today));
    }

    [Fact]
    public void A_timer_ends_with_one_notification_in_words() =>
        Assert.Equal(("Your timer is up", "25 minutes, on Desk."), NoticeWords.TimerUp(25, "Desk"));
}

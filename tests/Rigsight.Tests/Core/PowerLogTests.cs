using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// Why a stretch of history has nothing recorded, read from what Windows logged about the PC's power. The events are
/// a real PC's from 6 to 8 October 2026 (a shutdown every night, two power cuts, the agent stopped once in between).
/// </summary>
public sealed class PowerLogTests
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);
    private static DateTime At(int day, int hour, int minute, int second = 0) => new(2026, 10, day, hour, minute, second);

    private static readonly List<PowerEvent> Log =
    [
        new(At(6, 7, 43, 24), PowerEventKind.Started),
        new(At(7, 7, 22, 50), PowerEventKind.Started), // after the night's shutdown
        new(At(7, 10, 32, 51), PowerEventKind.Started), // after the power went
        new(At(7, 10, 33, 3), PowerEventKind.ShutOff),
        new(At(7, 16, 8, 41), PowerEventKind.Started),
        new(At(7, 22, 10), PowerEventKind.Sleep),
        new(At(7, 23, 5), PowerEventKind.Wake),
        new(At(8, 7, 18, 37), PowerEventKind.Started),
        new(At(8, 9, 51, 31), PowerEventKind.Started),
        new(At(8, 9, 51, 42), PowerEventKind.Crashed),
    ];

    [Fact]
    public void A_gap_that_ends_in_a_start_is_the_pc_having_been_off()
    {
        Assert.Equal(GapReason.Off, PowerLog.Reason(At(6, 21, 26), At(7, 7, 23), Log, Margin));
        Assert.Equal(GapReason.Off, PowerLog.Reason(At(7, 14, 56), At(7, 16, 9), Log, Margin));
        Assert.Equal("PC was off", PowerLog.Words(GapReason.Off));
    }

    [Fact]
    public void A_start_that_found_the_pc_had_gone_down_says_how()
    {
        // The readings stop at 9:52, the PC starts at 10:32 and Windows notes twelve seconds later that it hadn't shut down.
        Assert.Equal(GapReason.ShutOff, PowerLog.Reason(At(7, 9, 52), At(7, 10, 33), Log, Margin));
        Assert.Equal(GapReason.Crashed, PowerLog.Reason(At(8, 9, 43), At(8, 9, 52), Log, Margin));
        Assert.Equal("PC shut off unexpectedly", PowerLog.Words(GapReason.ShutOff));
        Assert.Equal("Windows crashed", PowerLog.Words(GapReason.Crashed));
    }

    [Fact]
    public void Sleep_with_no_start_is_sleep_and_nothing_at_all_is_the_pc_on_with_nothing_recording()
    {
        Assert.Equal(GapReason.Asleep, PowerLog.Reason(At(7, 22, 11), At(7, 23, 5), Log, Margin));
        // Half an hour in the evening with no power event in or near it: the PC ran throughout.
        Assert.Equal(GapReason.NotRunning, PowerLog.Reason(At(7, 18, 0), At(7, 18, 30), Log, Margin));
        Assert.Equal("Rigsight wasn't running", PowerLog.Words(GapReason.NotRunning));
    }

    [Fact]
    public void Where_the_log_doesnt_reach_or_wasnt_read_no_reason_is_given()
    {
        // Before the log's first record: "nothing logged" there says nothing about the PC having been on.
        Assert.Equal(GapReason.Unknown, PowerLog.Reason(At(5, 10, 0), At(5, 11, 0), Log, Margin));
        Assert.Equal(GapReason.Unknown, PowerLog.Reason(At(7, 18, 0), At(7, 18, 30), null, Margin));
        Assert.Equal(GapReason.Unknown, PowerLog.Reason(At(7, 18, 0), At(7, 18, 30), [], Margin));
        Assert.Null(PowerLog.Words(GapReason.Unknown));
    }

    [Fact]
    public void This_pcs_own_log_can_be_read()
    {
        var events = CrashLogReader.ReadPower(DateTime.Now.AddDays(-30));
        Assert.NotNull(events);
        Assert.Contains(events, e => e.Kind == PowerEventKind.Started); // it has started at least once in a month
        Assert.Equal(events.OrderBy(e => e.Time), events);
    }
}

using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>
/// Why a period has nothing recorded: before the history, the PC off or asleep (from Windows' log, the empty stretch
/// taken whole), the PC on with nothing running, or not known; and the sentence a page says it with.
/// </summary>
public sealed class WhyEmptyTests : IDisposable
{
    private readonly TestDb _t = new("whyempty");
    private RigsightDb Db => _t.Db;
    public void Dispose() => _t.Dispose();

    // A Saturday to a Monday of a PC not started (the user, 2026-11-04): Friday evening recorded, then Tuesday morning.
    private static readonly DateTime Sun = new(2026, 11, 1), Now = new(2026, 11, 4, 13, 0, 0);

    private void Minute(DateTime at) => Db.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(at) });

    private (WhyEmpty.Kind, DateTime?) Of(DateTime from, DateTime to, params PowerEvent[] power) =>
        WhyEmpty.Of(Db, from, to, Now, _ => power);

    [Fact]
    public void A_pc_left_off_for_days_was_off_from_the_start_that_ended_it()
    {
        Minute(new DateTime(2026, 10, 30, 21, 0, 0));
        Minute(new DateTime(2026, 11, 3, 9, 0, 0));
        // The start that ended the stretch is on Tuesday, outside Sunday itself: the stretch is taken whole.
        var (kind, _) = Of(Sun, Sun.AddDays(1), new PowerEvent(new DateTime(2026, 10, 20), PowerEventKind.Started), new PowerEvent(new DateTime(2026, 11, 3, 8, 58, 0), PowerEventKind.Started));
        Assert.Equal(WhyEmpty.Kind.Off, kind);
        Assert.Equal("Your PC was off on Sun 1 Nov.", WhyEmpty.Sentence(kind, null, "on Sun 1 Nov"));
    }

    [Fact]
    public void Asleep_on_with_nothing_running_or_not_known()
    {
        Minute(new DateTime(2026, 10, 31, 23, 0, 0));
        Minute(new DateTime(2026, 11, 2, 1, 0, 0));
        Assert.Equal(WhyEmpty.Kind.Asleep, Of(Sun, Sun.AddDays(1), new PowerEvent(new DateTime(2026, 10, 20), PowerEventKind.Started),
            new PowerEvent(new DateTime(2026, 10, 31, 23, 5, 0), PowerEventKind.Sleep)).Item1);
        // Windows logged nothing about power in the stretch, and its log goes back before it: the PC ran throughout.
        Assert.Equal(WhyEmpty.Kind.NotRunning, Of(Sun, Sun.AddDays(1), new PowerEvent(new DateTime(2026, 10, 20), PowerEventKind.Started)).Item1);
        Assert.Equal("Your PC was on yesterday, but RigSight wasn't running.", WhyEmpty.Sentence(WhyEmpty.Kind.NotRunning, null, "yesterday"));
        // A log that doesn't reach back: either could be true, and it says so.
        Assert.Equal(WhyEmpty.Kind.Unknown, Of(Sun, Sun.AddDays(1)).Item1);
        Assert.Equal("Nothing was recorded last week. Your PC was off, or RigSight wasn't running.", WhyEmpty.Sentence(WhyEmpty.Kind.Unknown, null, "last week"));
    }

    [Fact]
    public void Before_the_history_and_with_the_pc_on_and_recording()
    {
        Assert.Equal(WhyEmpty.Kind.BeforeHistory, Of(Sun, Sun.AddDays(1)).Item1); // nothing recorded ever
        Minute(new DateTime(2026, 11, 3, 9, 0, 0));
        var (kind, began) = Of(Sun, Sun.AddDays(1));
        Assert.Equal(WhyEmpty.Kind.BeforeHistory, kind);
        Assert.Equal("Nothing was recorded on Sun 1 Nov. This PC's history starts on 3 Nov 2026.", WhyEmpty.Sentence(kind, began, "on Sun 1 Nov"));
        // The PC was on and recording: what the page lacks is its own to say.
        Assert.Equal(WhyEmpty.Kind.PcWasOn, Of(new DateTime(2026, 11, 3), new DateTime(2026, 11, 4)).Item1);
        Assert.Null(WhyEmpty.Sentence(WhyEmpty.Kind.PcWasOn, began, "yesterday"));
    }

    [Fact]
    public void A_period_older_than_windows_log_is_not_guessed()
    {
        Minute(new DateTime(2026, 6, 1));
        Minute(new DateTime(2026, 11, 3));
        Assert.Equal(WhyEmpty.Kind.Unknown, Of(new DateTime(2026, 7, 1), new DateTime(2026, 7, 2), new PowerEvent(new DateTime(2026, 5, 1), PowerEventKind.Started)).Item1);
    }
}

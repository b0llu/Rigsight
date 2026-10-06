namespace Rigsight.Agent.Network;

/// <summary>What to do about a network trace that has gone quiet (see <see cref="TraceWatch"/>).</summary>
internal enum TraceRemedy
{
    None,
    /// <summary>Start the trace again.</summary>
    Restart,
    /// <summary>That didn't help: stop traces left behind by test copies of Rigsight, then start it again.</summary>
    ClearOthers,
    /// <summary>Neither helped: said once, and tried again later.</summary>
    GiveUp,
    /// <summary>Events are coming in again after a remedy.</summary>
    Recovered,
}

/// <summary>
/// Notices a network trace that has stopped delivering while the PC is plainly using the network: megabytes through
/// the network cards (Windows' own counters, which don't depend on the trace) and not one event from the trace.
/// Windows keeps such a trace "running" and reports no error; a trace session left behind with nobody reading it does
/// this to every other one once its file is full (seen with a test copy's, on 5 Oct 2026: seven hours of app network
/// use lost, and restarting the agent didn't bring it back). Checked once a minute, on the sampler thread.
/// </summary>
internal sealed class TraceWatch
{
    /// <summary>This much through the cards between two checks, with no event at all, is a quiet trace.</summary>
    public const long BusyBytes = 5_000_000;

    /// <summary>Checks in a row that must be quiet before anything is done (one could be a counter read mid-change).</summary>
    public const int QuietChecks = 2;

    /// <summary>After both remedies failed: how long until they're tried again (seconds).</summary>
    public const int RetrySeconds = 30 * 60;

    private long? _cardBefore;
    private long _eventsBefore;
    private int _quiet, _stage;
    private long _retryAt;

    /// <summary>Both remedies failed and nothing has come in since: app network use isn't being recorded (the Network
    /// page says so).</summary>
    public bool Stalled { get; private set; }

    /// <summary>The trace was started again: its event count starts from nothing.</summary>
    public void Restarted() => _eventsBefore = 0;

    /// <summary>
    /// There's no trace to watch: Windows wouldn't start one, or it ended by itself. The same remedies in the same order
    /// (a full set of leftover test traces can be why Windows refuses another), a check apart, then later again; while it
    /// stays that way nothing is recorded, so the Network page says so.
    /// </summary>
    /// <param name="now">Unix seconds.</param>
    public TraceRemedy Missing(long now)
    {
        _cardBefore = null;
        _quiet = 0;
        if (now < _retryAt) return TraceRemedy.None;
        switch (++_stage)
        {
            case 1: return TraceRemedy.Restart;
            case 2: return TraceRemedy.ClearOthers;
            default:
                _stage = 0;
                _retryAt = now + RetrySeconds;
                Stalled = true;
                return TraceRemedy.GiveUp;
        }
    }

    /// <param name="now">Unix seconds.</param>
    /// <param name="cardBytes">Bytes through the network cards since Windows started (null when they can't be read).</param>
    /// <param name="events">Events the trace has delivered since it started.</param>
    public TraceRemedy Check(long now, long? cardBytes, long events)
    {
        long newEvents = events - _eventsBefore;
        _eventsBefore = events;
        long? moved = cardBytes - _cardBefore;
        _cardBefore = cardBytes;

        if (newEvents > 0)
        {
            _quiet = 0;
            if (_stage == 0 && !Stalled) return TraceRemedy.None;
            _stage = 0;
            Stalled = false;
            return TraceRemedy.Recovered;
        }
        // Little or nothing moved (or a card's counters started over): a trace with nothing to say isn't quiet.
        if (moved is not >= BusyBytes || now < _retryAt) return TraceRemedy.None;
        if (++_quiet < QuietChecks) return TraceRemedy.None;
        _quiet = 0;
        switch (++_stage)
        {
            case 1: return TraceRemedy.Restart;
            case 2: return TraceRemedy.ClearOthers;
            default:
                _stage = 0;
                _retryAt = now + RetrySeconds;
                Stalled = true;
                return TraceRemedy.GiveUp;
        }
    }
}

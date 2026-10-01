using Rigsight.Core.Data;

namespace Rigsight.Agent.Network;

/// <summary>
/// Notes the times the internet drops, from Windows' own view of the connection (what the taskbar's network icon shows),
/// checked against what actually moved: a "drop" while apps were still downloading is Windows' check being wrong, not the
/// internet. Short blips, the first moments after starting or waking (the network is still coming up), and anything
/// across a sleep are left out. Sampler thread only.
/// </summary>
internal sealed class ConnectionWatch
{
    /// <summary>The shortest drop that counts: Windows' own check flickers for a few seconds now and then.</summary>
    public const int MinDropSeconds = 10;

    /// <summary>The most that may come in from the internet, a second on average, during a drop that counts.</summary>
    public const long MaxBytesWhileDown = 20_000;

    /// <summary>How long after starting or waking a lost connection is still the network coming up, not a drop.</summary>
    public const int SettleSeconds = 90;

    private sealed class Open
    {
        public long Start;
        public int Seconds, CardDown;
        public long Bytes;
        public bool Measured;
    }

    private Open? _open;
    private long _last, _quietUntil;

    /// <summary>The network is coming up (the agent started, or the PC woke): nothing counts until it settles.</summary>
    public void Settle(long now)
    {
        _quietUntil = now + SettleSeconds;
        _open = null;
    }

    /// <summary>The PC is going to sleep: whatever was going on is part of that, not a drop.</summary>
    public void Suspend() => _open = null;

    /// <summary>
    /// One look, about once a second: whether Windows sees the internet (null: it can't tell), whether a network card is
    /// connected, and how much came in from the internet since the last look (null without the trace). Returns a drop
    /// that has just ended and counts.
    /// </summary>
    public NetDrop? Observe(long now, bool? online, bool cardConnected, long? internetDown)
    {
        // A gap (the PC slept, or the agent was stuck): what was open can't be told apart from the sleep.
        if (_last != 0 && now - _last > 30) _open = null;
        _last = now;
        if (now < _quietUntil)
        {
            _open = null;
            return null;
        }
        if (online is null) return null;

        if (online == false)
        {
            _open ??= new Open { Start = now };
            _open.Seconds++;
            if (!cardConnected) _open.CardDown++;
            if (internetDown is long bytes)
            {
                _open.Bytes += bytes;
                _open.Measured = true;
            }
            return null;
        }

        if (_open is not { } drop) return null;
        _open = null;
        long length = now - drop.Start;
        if (length < MinDropSeconds) return null;
        if (drop.Measured && drop.Bytes / length >= MaxBytesWhileDown) return null;
        return new NetDrop(drop.Start, now, drop.CardDown * 2 >= drop.Seconds ? NetDropKind.Link : NetDropKind.Internet);
    }
}

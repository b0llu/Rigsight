using Rigsight.Core.Stability;

namespace Rigsight.Agent.Tracking;

/// <summary>
/// The agent's last few minutes of readings, kept in memory only (<see cref="CloseUp.RingSec"/>): one about every two
/// seconds, as the sensors are read, the oldest falling off as each new one comes. Nothing is written unless a crash
/// needs it. A gap (the PC asleep, tracking paused) is left as a gap: nothing is made up across it.
/// </summary>
internal sealed class CloseUpRing(int keepSec = CloseUp.RingSec)
{
    /// <summary>Readings closer together than this are one: the sensors are read every second while the app is open.</summary>
    internal const int LeastGapMs = 1800;

    private readonly Queue<CloseUpPoint> _points = new();
    private CloseUpPoint? _last;

    public int Count => _points.Count;

    /// <summary>The oldest reading still kept (Unix ms), or null.</summary>
    public long? Oldest => _points.Count == 0 ? null : _points.Peek().T;

    /// <summary>The newest reading (Unix ms), or null.</summary>
    public long? Newest => _last?.T;

    public void Add(CloseUpPoint point)
    {
        if (_last is not null && point.T - _last.T < LeastGapMs && point.T >= _last.T) return;
        // The clock went back (a time change): what was kept no longer lines up with it.
        if (_last is not null && point.T < _last.T) _points.Clear();
        _points.Enqueue(point);
        _last = point;
        while (_points.Count > 0 && _points.Peek().T < point.T - keepSec * 1000L) _points.Dequeue();
    }

    /// <summary>The readings from <paramref name="fromMs"/> to <paramref name="toMs"/> (Unix ms), oldest first.</summary>
    public List<CloseUpPoint> Between(long fromMs, long toMs) => [.. _points.Where(p => p.T >= fromMs && p.T <= toMs)];

    public void Clear()
    {
        _points.Clear();
        _last = null;
    }
}

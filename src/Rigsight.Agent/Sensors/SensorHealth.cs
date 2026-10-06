namespace Rigsight.Agent.Sensors;

/// <summary>
/// Notices hardware that has stopped answering (a graphics driver updated without a restart, a fan hub or AIO
/// unplugged or back from sleep under a new handle) and says when to find the sensors again: the scan drops what's
/// gone and opens what came back, where carrying on would record nothing for it until the agent restarted. Only on
/// evidence (reads failing for half a minute), not on a timer, and a few times at most: a part that stays broken
/// isn't scanned for over and over.
/// </summary>
internal sealed class SensorHealth
{
    /// <summary>Reads must fail for this long, with none succeeding in between (ms).</summary>
    public const int FailingForMs = 30_000;

    /// <summary>After a scan: no other for this long (ms).</summary>
    public const int BetweenScansMs = 10 * 60_000;

    /// <summary>Scans in a row that didn't help before it stops trying.</summary>
    public const int MaxScans = 3;

    private long _failingSince = -1, _nextScan;
    private int _scans;

    /// <param name="failed">The read just made failed for some hardware (see SensorHost.Unwell).</param>
    /// <param name="nowMs">The sampler's clock.</param>
    /// <returns>True when the sensors should be found again now.</returns>
    public bool Check(bool failed, long nowMs)
    {
        if (!failed)
        {
            _failingSince = -1;
            if (nowMs >= _nextScan) _scans = 0; // well for a good while: a later problem is a new one
            return false;
        }
        if (_failingSince < 0) _failingSince = nowMs;
        if (nowMs - _failingSince < FailingForMs || nowMs < _nextScan || _scans >= MaxScans) return false;
        _scans++;
        _nextScan = nowMs + BetweenScansMs;
        _failingSince = -1;
        return true;
    }
}

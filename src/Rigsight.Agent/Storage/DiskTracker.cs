using Rigsight.Agent.Tracking;
using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Agent.Storage;

/// <summary>
/// Turns each second's reads and writes, by process and drive number, into each app's hour and day on each drive (by the
/// drive's key, which stays the same when Windows numbers the drives differently). The system process (ID 4) keeps its
/// own row: what Windows' file cache wrote and read ahead for the apps. Sampler thread only.
/// </summary>
internal sealed class DiskTracker(RigsightDb db)
{
    private IReadOnlyDictionary<int, string> _keys = new Dictionary<int, string>();
    private readonly Dictionary<(long App, string Disk), DiskCounts> _chunk = [];
    private readonly HashSet<int> _unknownDisks = [];

    /// <summary>Since the last <see cref="TakeSummary"/>: bytes put down to an app, with no process named in the event
    /// (ID 0 or -1), of processes gone or left out of tracking, and on drives with no key.</summary>
    private long _counted, _noProcess, _noApp, _noDrive;

    /// <summary>
    /// The drives as they are now, by their number in Windows; true when that changed. Bytes on a drive with no key
    /// aren't recorded (there'd be nothing to show them under).
    /// </summary>
    public bool SetDiskMap(IReadOnlyDictionary<int, string> numberToKey)
    {
        if (numberToKey.Count == _keys.Count && numberToKey.All(kv => _keys.TryGetValue(kv.Key, out var k) && k == kv.Value)) return false;
        _keys = new Dictionary<int, string>(numberToKey);
        _unknownDisks.Clear();
        return true;
    }

    /// <summary>The drive numbers with a key (for Windows' own counters, see <see cref="DiskCounters"/>).</summary>
    public IEnumerable<int> Disks => _keys.Keys;

    /// <summary>The process ID a disk event gives when Windows didn't say which process asked (0, -1).</summary>
    internal static bool NoProcess(int pid) => pid is 0 or -1;

    /// <summary>
    /// One second's reads and writes: each process's app (null: gone before it could be named, or left out of tracking;
    /// a process ID of 0 or -1 never gets here as an app) and drive number.
    /// </summary>
    public void OnSecond(IEnumerable<(int Pid, AppInfo? App, int Disk, DiskCounts Counts)> counts)
    {
        foreach (var (pid, app, disk, c) in counts)
        {
            if (c.IsEmpty) continue;
            if (NoProcess(pid)) { _noProcess += c.Total; continue; }
            if (app is null) { _noApp += c.Total; continue; }
            if (!_keys.TryGetValue(disk, out var key))
            {
                _noDrive += c.Total;
                if (_unknownDisks.Add(disk))
                    Log.Write("disk", $"Expected drive {disk} among the drives found ({(_keys.Count == 0 ? "none yet" : string.Join(", ", _keys.Keys.Order()))}), not there: its reads and writes aren't recorded");
                continue;
            }
            _counted += c.Total;
            if (!_chunk.TryGetValue((app.Id, key), out var sum)) _chunk[(app.Id, key)] = sum = new DiskCounts();
            sum.Read += c.Read;
            sum.Write += c.Write;
        }
    }

    /// <summary>Writes what came in since the last write into each app's hour (<paramref name="hour"/>) and day. Inside the tracker's transaction.</summary>
    public void Write(long hour)
    {
        foreach (var ((app, disk), c) in _chunk)
            if (!c.IsEmpty) db.AddDiskAppUse(new DiskAppUse(hour, app, disk, c.Read, c.Write));
        _chunk.Clear();
    }

    /// <summary>For the log: bytes counted and not counted (and why) since the last call.</summary>
    public (long Counted, long NoProcess, long NoApp, long NoDrive) TakeSummary()
    {
        var s = (_counted, _noProcess, _noApp, _noDrive);
        _counted = _noProcess = _noApp = _noDrive = 0;
        return s;
    }

    /// <summary>History cleared: start again from nothing.</summary>
    public void Clear() => _chunk.Clear();
}

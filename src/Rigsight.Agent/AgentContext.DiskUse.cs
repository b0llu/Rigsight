using Rigsight.Agent.Network;
using Rigsight.Agent.Storage;
using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Agent;

// What each app reads and writes on each physical drive (2026-11-14): Windows' disk events in a trace of our own (admin
// rights only), taken once a second, written with the minute into each app's hour and day on each drive. Sampler thread.
internal sealed partial class AgentContext
{
    private DiskTrace? _diskTrace;
    private readonly TraceWatch _diskWatch = new();
    private bool _diskFirstLogged, _diskSessionsLogged;
    private int _diskChecks;
    private long _diskEventsBefore;

    private static string DiskTraceName => "Rigsight Disk" + RigsightPaths.InstanceSuffix;

    /// <summary>Sampler thread, at start: which drive is which, and the trace (with admin rights).</summary>
    private void StartDiskUse()
    {
        UpdateDiskMap();
        if (_isAdmin) _diskTrace = DiskTrace.Start(DiskTraceName);
        else Log.Write("disk", "No admin rights: what each app reads and writes on the drives isn't recorded");
    }

    /// <summary>
    /// Sampler thread, at start and each minute: each drive's number in Windows and its key in the history, from the
    /// drives the sensors found (the numbers can change when a drive is added or removed). Logged when it changes.
    /// </summary>
    private void UpdateDiskMap()
    {
        if (!_sensorsReady) return;
        var disks = _sensors.ReadDiskMap();
        var map = new Dictionary<int, string>();
        foreach (var d in disks) map.TryAdd(d.Number, d.Key);
        if (!_tracker.SetDiskMap(map)) return;
        // The serial itself stays out of the log (logs are shared in issues): only whether the key is one.
        Log.Write("disk", disks.Count == 0 ? "Expected the drives from the sensors for disk use, found none: nothing is recorded until they're found"
            : $"Drives for disk use: {string.Join("; ", disks.OrderBy(d => d.Number).Select(d =>
                $"{d.Number} = {d.Model} ({(d.Letters.Count == 0 ? "no letter" : string.Join(", ", d.Letters))}, keyed by {(d.Key.Contains('|') ? "model and size" : "serial")})"))}");
    }

    /// <summary>Sampler thread, once a second: what each process read and wrote on each drive since the last time.</summary>
    private void SampleDiskUse()
    {
        if (_diskTrace is not { } trace) return;
        var counts = trace.Take();
        if (counts.Count == 0) return;
        if (!_diskFirstLogged) LogFirstDiskEvents(trace, counts);
        _tracker.OnDisk(counts, _running);
    }

    /// <summary>
    /// Once a trace: the first events, by process and drive, so the log says what the events carry (which process,
    /// which drive numbers) on this PC.
    /// </summary>
    private void LogFirstDiskEvents(DiskTrace trace, Dictionary<DiskIoKey, DiskCounts> counts)
    {
        _diskFirstLogged = true;
        var byPid = counts.GroupBy(kv => kv.Key.Pid).Select(g => (Pid: g.Key, Bytes: g.Sum(kv => kv.Value.Total))).OrderByDescending(x => x.Bytes).ToList();
        string Name(int pid) => pid == 4 ? "System" : DiskTracker.NoProcess(pid) ? "no process named" : _running.TryGetValue(pid, out var exe) ? exe : "not in the last process sample";
        Log.Write("disk", $"First disk events: {trace.Events} so far, {counts.Values.Sum(c => c.Read)} bytes read and {counts.Values.Sum(c => c.Write)} written, "
            + $"on drive(s) {string.Join(", ", counts.Keys.Select(k => k.Disk).Distinct().Order())}; by process: "
            + string.Join(", ", byPid.Take(6).Select(x => $"{x.Pid} ({Name(x.Pid)}) {x.Bytes}")) + (byPid.Count > 6 ? $", and {byPid.Count - 6} more" : ""));
    }

    /// <summary>
    /// Sampler thread, each minute: the drives again, and a trace that delivers nothing while Windows' own counters say the
    /// drives moved megabytes is started again (the network trace's remedies, see <see cref="TraceWatch"/>). Once an hour,
    /// what was counted.
    /// </summary>
    private void CheckDiskTrace()
    {
        UpdateDiskMap();
        if (!_isAdmin) return;
        long now = TimeUtil.NowUnix();
        var remedy = _diskTrace is { Ended: false } trace ? _diskWatch.Check(now, DiskCounters.TotalBytes(_tracker.DiskNumbers), trace.Events) : _diskWatch.Missing(now);
        switch (remedy)
        {
            case TraceRemedy.Restart:
                Log.Write("disk", _diskTrace is { Ended: false } ? "Disk events stopped coming in while the drives were busy: reading them again"
                    : "Disk events aren't being read: starting the trace again");
                RestartDiskTrace();
                break;
            case TraceRemedy.ClearOthers:
                var others = DiskTrace.StopOthers(DiskTraceName);
                Log.Write("disk", others.Count == 0 ? "Still no disk events: reading them again"
                    : $"Still no disk events: stopped {others.Count} trace(s) left by test copies ({string.Join(", ", others)}), reading them again");
                RestartDiskTrace();
                break;
            case TraceRemedy.GiveUp:
                Log.Write("disk", $"Disk events still aren't coming in: trying again in {TraceWatch.RetrySeconds / 60} minutes");
                if (!_diskSessionsLogged)
                {
                    _diskSessionsLogged = true;
                    var sessions = NetworkTrace.Running();
                    Log.Write("disk", $"Trace sessions running now ({sessions.Count}): {string.Join(", ", sessions)}");
                }
                break;
            case TraceRemedy.Recovered:
                Log.Write("disk", "Disk events are coming in again");
                _diskSessionsLogged = false;
                break;
        }

        if (++_diskChecks % 60 == 0)
        {
            long events = _diskTrace?.Events ?? 0;
            var (counted, noProcess, noApp, noDrive) = _tracker.TakeDiskSummary();
            Log.Write("disk", $"Disk use, last hour: {Math.Max(0, events - _diskEventsBefore)} events; {Mb(counted)} put down to apps, "
                + $"{Mb(noProcess)} with no process named, {Mb(noApp)} of processes gone or not tracked, {Mb(noDrive)} on drives not found");
            _diskEventsBefore = events;
        }
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    private void RestartDiskTrace()
    {
        _diskTrace?.Dispose();
        _diskTrace = DiskTrace.Start(DiskTraceName);
        _diskWatch.Restarted();
        _diskEventsBefore = 0;
        _diskFirstLogged = false;
    }
}

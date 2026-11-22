using Rigsight.Agent.Drivers;
using Rigsight.Agent.Network;
using Rigsight.Core;
using Rigsight.Core.Drivers;

namespace Rigsight.Agent;

// Driver activity (issue #15, approved mock-up v6, 2026-11-22): how long each device's driver kept a CPU core to itself,
// from Windows' own driver events in a private kernel trace (admin rights only). Each calendar minute: the drivers worth
// keeping are written, every driver's hour is kept up to date (its usual), new drivers are named from the devices
// Windows has, and a minute in which Windows dropped readings is marked so nothing is concluded from it. The ring of
// long bursts is read when a minute's stutters are written (Tracker.DriverBursts) and by the tray's "Something just went
// wrong". Sampler thread.
internal sealed partial class AgentContext
{
    /// <summary>
    /// On all the time while the PC is on (true), or only while a game is in front. Decided by the Phase 0 probe's
    /// numbers against the agent's CPU budget; nobody sees this switch.
    /// </summary>
    internal const bool DriversAlwaysOn = true;

    /// <summary>No driver events for this long on a running PC means the trace has stalled: drivers never all go quiet.</summary>
    private const int DriverStallSec = 60;

    private DriverTrace? _driverTrace;
    private long _driverMinute, _driverHour, _driverEventsSeen, _driverLostSeen, _driverQuietSince, _driverRetryAt;
    private int _driverRestarts;
    private readonly Dictionary<string, DriverTally> _driverHourTallies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _driversNamed = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<(string Class, string Name)>>? _devices;
    private long _devicesRead;

    private static string DriverTraceName => "Rigsight Drivers" + RigsightPaths.InstanceSuffix;

    /// <summary>Sampler thread, at start: the trace (with admin rights), and which driver each device uses.</summary>
    private void StartDrivers()
    {
        if (!_isAdmin)
        {
            Log.Write("drivers", "No admin rights: which part of the PC was using the CPU when a game stuttered isn't recorded");
            return;
        }
        StartDriverTrace();
    }

    private void StartDriverTrace()
    {
        _driverTrace = DriverTrace.Start(DriverTraceName);
        _driverEventsSeen = 0;
        _driverLostSeen = 0;
        _driverQuietSince = 0;
        _tracker.DriverBursts = _driverTrace is null ? null : since => _driverTrace?.Recent(since) ?? [];
    }

    /// <summary>
    /// Sampler thread, once a second: at each new calendar minute the minute that ended is written. A trace that isn't
    /// running is tried again every <see cref="TraceWatch.RetrySeconds"/>.
    /// </summary>
    private void SampleDrivers()
    {
        if (!_isAdmin) return;
        long now = TimeUtil.NowUnix(), minute = now / 60 * 60;
        if (_driverMinute == 0) { _driverMinute = minute; return; }
        if (minute == _driverMinute) return;
        long ended = _driverMinute;
        _driverMinute = minute;

        if (_driverTrace is not { } trace || trace.Ended)
        {
            if (now < _driverRetryAt) return;
            _driverRetryAt = now + TraceWatch.RetrySeconds;
            Log.Write("drivers", _driverTrace is null ? "The driver trace isn't running: starting it again" : "The driver trace stopped by itself: starting it again");
            _driverTrace?.Dispose();
            StartDriverTrace();
            return;
        }

        var tallies = trace.TakeMinute(ended);
        CheckDriverHealth(trace, now, ended);
        if (tallies.Count == 0) return;
        NameNewDrivers(tallies, now);
        try
        {
            _db.WriteDriverMinute(ended, tallies);
            long hour = ended / 3600 * 3600;
            if (hour != _driverHour) { _driverHourTallies.Clear(); _driverHour = hour; }
            foreach (var t in tallies)
            {
                _driverHourTallies[t.Driver] = _driverHourTallies.TryGetValue(t.Driver, out var h)
                    ? new DriverTally(hour, t.Driver, h.Count + t.Count, h.TotalUs + t.TotalUs, Math.Max(h.LongestUs, t.LongestUs), h.Long + t.Long)
                    : t with { Ts = hour };
            }
            _db.WriteDriverHour(hour, _driverHourTallies.Values);
        }
        catch (Exception ex) { _errors.Failed("drivers", ex); }
    }

    /// <summary>
    /// A minute with dropped readings is marked; a trace with no events for <see cref="DriverStallSec"/> is started again
    /// (at most 3 times in a row, then every <see cref="TraceWatch.RetrySeconds"/>). Says what it expected and found.
    /// </summary>
    private void CheckDriverHealth(DriverTrace trace, long now, long minute)
    {
        long lost = trace.Lost;
        if (lost > _driverLostSeen)
        {
            Log.Write("drivers", $"Windows dropped driver readings in the minute from {TimeUtil.FromUnix(minute):HH:mm} ({lost - _driverLostSeen} more lost, {lost} in all): nothing is concluded from that minute");
            _driverLostSeen = lost;
            try { _db.MarkDriverMissed(minute); } catch (Exception ex) { _errors.Failed("drivers", ex); }
        }

        long events = trace.Events;
        if (events > _driverEventsSeen) { _driverEventsSeen = events; _driverQuietSince = 0; _driverRestarts = 0; return; }
        if (_driverQuietSince == 0) { _driverQuietSince = now; return; }
        if (now - _driverQuietSince < DriverStallSec) return;
        if (_driverRestarts >= 3)
        {
            if (now < _driverRetryAt) return;
            _driverRetryAt = now + TraceWatch.RetrySeconds;
            var sessions = NetworkTrace.Running();
            Log.Write("drivers", $"Expected driver events, found none for {now - _driverQuietSince}s after 3 restarts; trying again in {TraceWatch.RetrySeconds / 60} minutes. Trace sessions running now ({sessions.Count}): {string.Join(", ", sessions)}");
        }
        _driverRestarts++;
        Log.Write("drivers", $"Expected driver events every second, found none for {now - _driverQuietSince}s ({events} since the start): reading them again (restart {_driverRestarts})");
        var others = DriverTrace.StopOthers(DriverTraceName);
        if (others.Count > 0) Log.Write("drivers", $"Stopped {others.Count} driver trace(s) left by test copies ({string.Join(", ", others)})");
        trace.Dispose();
        StartDriverTrace();
    }

    /// <summary>
    /// Each driver the first time it shows up: which part of the PC it is, from the devices Windows has (read again at
    /// most once an hour, when a driver comes up that wasn't among them), saved so the app can name it.
    /// </summary>
    private void NameNewDrivers(List<DriverTally> tallies, long now)
    {
        var fresh = tallies.Where(t => t.Driver.Length > 0 && !_driversNamed.Contains(t.Driver)).Select(t => t.Driver).ToList();
        if (fresh.Count == 0) return;
        try
        {
            if (_devices is null || (fresh.Any(d => !_devices.ContainsKey(d)) && now - _devicesRead > 3600))
            {
                _devices = DeviceDrivers.Read();
                _devicesRead = now;
            }
            foreach (var driver in fresh)
            {
                var part = DriverParts.Name(driver, _devices.TryGetValue(driver, out var devices) ? devices : []);
                _db.SaveDriverPart(part, now);
                _driversNamed.Add(driver);
                Log.Write("drivers", $"{driver}: {(part.Say is { } say ? $"named \"{say}\"" : "not named (Windows' own, shared or unknown)")}"
                    + (devices is { Count: > 0 } ? $", devices {string.Join("; ", devices.Select(d => $"{d.Class} \"{d.Name}\""))}" : ", no device found using it"));
            }
        }
        catch (Exception ex) { _errors.Failed("drivers", ex); }
    }
}

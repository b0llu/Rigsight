using Rigsight.Agent.Drivers;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Drivers;
using Rigsight.Core.Settings;
using Rigsight.Core.Protocol;

namespace Rigsight.Agent;

// The app wake-up check and the Latency page's live view (issue #15, approved mock-up 2026-11-24). The check runs while its
// setting is on (on by default; turned off on the Latency page, and then only it stops: driver delays are still recorded).
// Each calendar minute its figures are written, with the hour so far. While the page is open the app says so ("latency-page
// open"), the check goes to 64 a second, and each tick carries the last two minutes. Sampler thread.
internal sealed partial class AgentContext
{
    private WakeCheck? _wakeCheck;
    private long _wakeMinute, _wakeHour;
    private readonly List<WakeTally> _wakeHourMinutes = [];
    private volatile bool _latencyPageOpen;

    /// <summary>Sampler thread, once a second: the check on or off as its setting says, told what's in front, and each new minute written.</summary>
    private void SampleWake()
    {
        WatchDriversNow();
        bool on = _settings.Latency.WakeCheck;
        if (on && _wakeCheck is null) _wakeCheck = WakeCheck.Start();
        else if (!on && _wakeCheck is not null)
        {
            WriteWakeMinute(TimeUtil.NowUnix() / 60 * 60);
            _wakeCheck.Dispose();
            _wakeCheck = null;
            Log.Write("wake", "The app wake-up check was turned off on the Latency page");
        }
        if (_wakeCheck is not { } wake) return;

        if (_pipe.ClientCount == 0) _latencyPageOpen = false;
        wake.PageOpen = _latencyPageOpen;
        wake.GameInFront = _tracker.Activity.Category == AppCategory.Game;

        long minute = TimeUtil.NowUnix() / 60 * 60;
        if (_wakeMinute == 0) { _wakeMinute = minute; return; }
        if (minute == _wakeMinute) return;
        long ended = _wakeMinute;
        _wakeMinute = minute;
        WriteWakeMinute(ended);
    }

    // The minute that ended, and its hour so far.
    private void WriteWakeMinute(long ended)
    {
        if (_wakeCheck?.TakeMinute(ended) is not { } tally) return;
        long hour = ended / 3600 * 3600;
        if (hour != _wakeHour) { _wakeHourMinutes.Clear(); _wakeHour = hour; }
        _wakeHourMinutes.Add(tally);
        try { _db.WriteWake(tally, WakeCheck.Hour(hour, _wakeHourMinutes)); }
        catch (Exception ex) { _errors.Failed("wake", ex); }
    }

    /// <summary>The app opened or closed the Latency page.</summary>
    private void OnLatencyPage(bool open)
    {
        if (_latencyPageOpen == open) return;
        _latencyPageOpen = open;
        Log.Write("wake", open ? "The Latency page opened: checking 64 times a second and sending the live view" : "The Latency page closed");
    }

    /// <summary>The last two minutes for the Latency page, a second at a time; null while it isn't open.</summary>
    private LatencyLive? LatencyNow()
    {
        if (!_latencyPageOpen) return null;
        long to = TimeUtil.NowUnix() - 1, from = to - DriverLedger.LiveSeconds + 1;
        var drivers = _driverTrace is { Ended: false } trace ? trace.Ledger.Seconds(from, to) : null;
        var wake = _wakeCheck?.Seconds(from, to);
        int n = (int)(to - from + 1);
        var live = new LatencyLive
        {
            Drivers = drivers is not null,
            Admin = _isAdmin,
            WakeOn = _settings.Latency.WakeCheck,
            WakeRate = _wakeCheck?.Rate ?? 0,
            Seconds = new long[n], DriverUs = new int[n], Driver = new string[n], WakeUs = new int[n], WakeMedianUs = new int[n],
        };
        for (int i = 0; i < n; i++)
        {
            live.Seconds[i] = from + i;
            live.Driver[i] = "";
            if (drivers is not null && i < drivers.Count) (live.DriverUs[i], live.Driver[i]) = (drivers[i].Us, drivers[i].Driver);
            if (wake is not null && i < wake.Count) (live.WakeUs[i], live.WakeMedianUs[i]) = (wake[i].Highest, wake[i].Median);
        }
        // Drivers right now, every 2 seconds (the ticks between leave it null and the page keeps what it has).
        // Not before a whole second has been kept since the page opened: the page shows its spinner until then.
        if (drivers is not null && _driverTrace is { Ended: false } running && Environment.TickCount64 - _driversNowAt >= DriversNowEveryMs - 100
            && running.Ledger.Now(to - DriverLedger.WindowSeconds + 1, to) is { Seconds: > 0 } window)
        {
            _driversNowAt = Environment.TickCount64;
            var (now, seconds) = window;
            live.DriversNowSeconds = seconds;
            live.DriversNow = [.. now.OrderByDescending(d => d.BusyUs).Take(DriversNowMax).Select(d => new LatencyDriverNow
            {
                Driver = d.Driver, Calls = d.Calls, BusyUs = d.BusyUs, DpcUs = d.DpcUs, IsrUs = d.IsrUs, LongestUs = d.LongestUs,
            })];
            if (now.Count > DriversNowMax && !_driversNowCapLogged)
            {
                _driversNowCapLogged = true;
                Log.Write("drivers", $"Drivers right now: {now.Count} drivers made calls in {seconds} seconds, more than the {DriversNowMax} sent; the least busy were left out");
            }
        }
        return live;
    }

    // Drivers right now (1.23.0, issue #18 item 11): the last 10 seconds of each driver, sent every 2 seconds while the page is open.
    private const int DriversNowEveryMs = 2000;
    private const int DriversNowMax = 64;
    private long _driversNowAt;
    private bool _driversNowCapLogged;

    /// <summary>
    /// Sampler thread, once a second: the ledger keeps each driver's last seconds only while the Latency page is open in a
    /// connected app, so a closed page costs nothing. A trace started again gets its new ledger watched within a second.
    /// </summary>
    private void WatchDriversNow()
    {
        if (_driverTrace is not { Ended: false } trace) return;
        bool open = _latencyPageOpen && _pipe.ClientCount > 0;
        if (!trace.Ledger.Watch(open, TimeUtil.NowUnix())) return;
        if (open) _driversNowAt = 0;
        Log.Write("drivers", open ? "Drivers right now: keeping each driver's last 10 seconds while the Latency page is open"
            : "Drivers right now: the Latency page closed, each driver's last seconds let go");
    }
}

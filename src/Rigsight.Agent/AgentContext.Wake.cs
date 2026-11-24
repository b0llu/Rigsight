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
        return live;
    }
}

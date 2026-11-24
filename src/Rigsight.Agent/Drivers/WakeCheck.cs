using System.Diagnostics;
using Rigsight.Agent.Sensors;
using Rigsight.Agent.Sound;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Drivers;

namespace Rigsight.Agent.Drivers;

/// <summary>
/// The app wake-up check (issue #15, approved mock-up 2026-11-24): how long Windows takes to run an app once it's woken.
/// One thread sets an event, noting the precise clock just before; a waiting thread at Highest priority (never real-time)
/// notes the clock as it runs. Measured on the user's PC (16 threads): typically 0.009 ms, 0.09 ms at most over 15 s,
/// about 0.006% of the PC at 64 checks a second. A timer's lateness was tried first and dropped: it mostly measured
/// Windows' timer steps (0.5 ms), not delay.
///
/// Checks are moments apart, so a short pause between two is missed: at 64 a second a lone 1 ms pause is caught about 6%
/// of the time. The driver trace sees every call; this is extra evidence, mostly for pauses no driver shows.
///
/// Rates: 8 a second, 64 while a game is in front, sound is playing or the Latency page is open, 2 on battery, none with
/// Battery Saver on. Paced by an ordinary sleep (Windows' normal timer, so its wake-ups can line up with Windows' own): no
/// high-resolution timer, no change to Windows' timer speed. Needs no admin rights.
/// </summary>
internal sealed class WakeCheck : IDisposable
{
    internal const int Background = 8, Busy = 64, Battery = 2;

    /// <summary>A check this long (µs) had the PC sleep or the clock jump in between: left out.</summary>
    private const int TooLongUs = 1_000_000;

    /// <summary>How often the power state and the sound playing are asked again (ms).</summary>
    private const int RecheckMs = 10_000;

    private readonly Thread _signaller, _waiter;
    private readonly AutoResetEvent _go = new(false);
    private readonly double _usPerTick = 1_000_000.0 / Stopwatch.Frequency;
    private volatile bool _stopped, _recheck = true;
    private long _signalledAt;
    private volatile int _rate = Background;
    private bool _gameInFront, _pageOpen;

    // Kept under the gate: this minute's checks, and the time spent at each rate in it; the last seconds for the live page.
    private readonly Lock _gate = new();
    private readonly List<int> _minute = new(4096);
    private readonly Dictionary<int, long> _rateMs = [];
    private readonly List<int> _second = new(128);
    private long _secondAt;
    internal const int LiveSeconds = 120;
    private readonly (long Sec, int Highest, int Median)[] _seconds = new (long, int, int)[LiveSeconds];

    private WakeCheck()
    {
        _signaller = new Thread(SignalLoop) { IsBackground = true, Name = "Wake check" };
        // Windows' audio sessions are asked from this thread (see SoundSessions): it must be MTA.
        _signaller.SetApartmentState(ApartmentState.MTA);
        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "Wake check (woken)", Priority = ThreadPriority.Highest };
    }

    public static WakeCheck Start()
    {
        var check = new WakeCheck();
        check._waiter.Start();
        check._signaller.Start();
        Log.Write("wake", "App wake-up check started (8 checks a second, 64 while a game is in front, sound plays or the Latency page is open, 2 on battery, none with Battery Saver on)");
        return check;
    }

    /// <summary>Checks a second right now (0 while paused by Battery Saver).</summary>
    public int Rate => _rate;

    /// <summary>A game is in front (set by the sampler each second): 64 a second.</summary>
    public bool GameInFront
    {
        set { if (_gameInFront != value) { _gameInFront = value; _recheck = true; } }
    }

    /// <summary>The Latency page is open in the app: 64 a second, so its live chart has enough checks.</summary>
    public bool PageOpen
    {
        set { if (_pageOpen != value) { _pageOpen = value; _recheck = true; } }
    }

    private void SignalLoop()
    {
        long nextCheck = 0, last = Environment.TickCount64;
        bool sound = false;
        while (!_stopped)
        {
            long now = Environment.TickCount64;
            if (_recheck || now >= nextCheck)
            {
                if (now >= nextCheck)
                {
                    try { sound = SoundSessions.Playing().Count > 0; }
                    catch (Exception ex) { Log.Error("wake", ex); sound = false; }
                    nextCheck = now + RecheckMs;
                }
                _recheck = false;
                int rate = RateFor(SystemState.Battery().OnBattery == true, SystemState.BatterySaver(), _gameInFront || _pageOpen || sound);
                if (rate != _rate) Log.Write("wake", $"Checks a second {_rate} → {rate}" + (rate == 0 ? " (Battery Saver is on)" : rate == Battery ? " (on battery)" : rate == Busy ? $" ({(_pageOpen ? "the Latency page is open" : _gameInFront ? "a game is in front" : "sound is playing")})" : ""));
                _rate = rate;
            }
            int r = _rate;
            if (r == 0)
            {
                Thread.Sleep(1000);
                last = Environment.TickCount64;
                continue;
            }
            Thread.Sleep(Math.Max(1, 1000 / r));
            if (_stopped) break;
            long t = Environment.TickCount64;
            lock (_gate) _rateMs[r] = _rateMs.GetValueOrDefault(r) + (t - last);
            last = t;
            Interlocked.Exchange(ref _signalledAt, Stopwatch.GetTimestamp());
            _go.Set();
        }
    }

    /// <summary>The rate for the PC's state: none with Battery Saver on, 2 on battery, 64 when busy, else 8.</summary>
    internal static int RateFor(bool onBattery, bool saver, bool busy) => saver ? 0 : onBattery ? Battery : busy ? Busy : Background;

    private void WaitLoop()
    {
        while (true)
        {
            _go.WaitOne();
            long now = Stopwatch.GetTimestamp();
            if (_stopped) return;
            int us = (int)Math.Min(int.MaxValue, Math.Max(0, (now - Interlocked.Read(ref _signalledAt)) * _usPerTick));
            if (us >= TooLongUs) continue;
            Add(us, TimeUtil.NowUnix());
        }
    }

    /// <summary>One check of <paramref name="us"/>, made in the second <paramref name="sec"/> (Unix seconds).</summary>
    internal void Add(int us, long sec)
    {
        lock (_gate)
        {
            _minute.Add(us);
            if (sec != _secondAt) EndSecond(sec);
            _second.Add(us);
        }
    }

    // The second that ended goes into the ring: its longest and middle check.
    private void EndSecond(long next)
    {
        if (_second.Count > 0)
        {
            _second.Sort();
            _seconds[(int)(((_secondAt % LiveSeconds) + LiveSeconds) % LiveSeconds)] = (_secondAt, _second[^1], _second[_second.Count / 2]);
            _second.Clear();
        }
        _secondAt = next;
    }

    /// <summary>The seconds from <paramref name="fromSec"/> to <paramref name="toSec"/>: each one's longest and middle check (0 for a second without one).</summary>
    public List<(long Sec, int Highest, int Median)> Seconds(long fromSec, long toSec)
    {
        lock (_gate)
        {
            var list = new List<(long, int, int)>();
            for (long sec = Math.Max(fromSec, toSec - LiveSeconds + 1); sec <= toSec; sec++)
            {
                // The second still going: its checks so far (it reaches the ring when the next one starts).
                if (sec == _secondAt && _second.Count > 0)
                {
                    var sorted = _second.Order().ToList();
                    list.Add((sec, sorted[^1], sorted[sorted.Count / 2]));
                    continue;
                }
                var slot = _seconds[(int)(((sec % LiveSeconds) + LiveSeconds) % LiveSeconds)];
                list.Add(slot.Sec == sec ? (sec, slot.Highest, slot.Median) : (sec, 0, 0));
            }
            return list;
        }
    }

    /// <summary>The minute starting at <paramref name="minuteTs"/>: its checks' figures, or null when none was made (paused). Starts again from nothing.</summary>
    public WakeTally? TakeMinute(long minuteTs)
    {
        int[] checks;
        int rate;
        lock (_gate)
        {
            checks = [.. _minute];
            _minute.Clear();
            rate = _rateMs.Count == 0 ? _rate : _rateMs.MaxBy(k => k.Value).Key;
            _rateMs.Clear();
        }
        return Of(minuteTs, checks, rate);
    }

    /// <summary>The figures of a minute's checks (null for none).</summary>
    internal static WakeTally? Of(long ts, int[] checks, int rate)
    {
        if (checks.Length == 0) return null;
        Array.Sort(checks);
        return new WakeTally(ts, checks.Length, checks[checks.Length / 2], checks[^1], checks.Count(c => c >= DriverRules.LongUs), rate);
    }

    /// <summary>An hour's figures from its minutes: every check counted, the middle of the minutes' middles, the longest of all.</summary>
    internal static WakeTally Hour(long hourTs, IReadOnlyList<WakeTally> minutes)
    {
        var medians = minutes.Select(m => m.MedianUs).Order().ToList();
        int rate = minutes.GroupBy(m => m.Rate).MaxBy(g => g.Count())?.Key ?? Background;
        return new WakeTally(hourTs, minutes.Sum(m => m.Checks), medians.Count == 0 ? 0 : medians[medians.Count / 2], minutes.Count == 0 ? 0 : minutes.Max(m => m.HighestUs),
            minutes.Sum(m => m.OverMs), rate);
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _go.Set();
        _signaller.Join(2000);
        _waiter.Join(2000);
        Log.Write("wake", "App wake-up check stopped");
    }
}

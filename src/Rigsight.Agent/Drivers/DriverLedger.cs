using Rigsight.Core.Drivers;

namespace Rigsight.Agent.Drivers;

/// <summary>A driver's calls over the last few seconds (see <see cref="DriverLedger.Now"/>): all, its own time, its DPCs' and interrupts' share of it, its longest (µs).</summary>
internal readonly record struct DriverNow(string Driver, int Calls, long BusyUs, long DpcUs, long IsrUs, int LongestUs);

/// <summary>The four kinds of driver call Windows times (the PerfInfo event's opcode says which).</summary>
internal enum DriverCall
{
    /// <summary>A deferred procedure call: a driver finishing work its interrupt started (opcode 68).</summary>
    Dpc,
    /// <summary>A DPC a timer queued (69): the same, started by a timer rather than a device.</summary>
    TimerDpc,
    /// <summary>
    /// A threaded DPC (66): runs as an ordinary thread at passive level, so other threads and DPCs can take the core from
    /// it. Counted, but never a burst: it didn't keep the core to itself.
    /// </summary>
    ThreadedDpc,
    /// <summary>An interrupt service routine (67): the driver's first answer to its device, ahead of everything else.</summary>
    Isr,
}

/// <summary>
/// What the driver trace keeps of each call, apart from Windows so it can be tested: the long bursts of the last
/// <see cref="DriverRules.LookBackSec"/> seconds (the stutter matcher and the tray's "Something just went wrong" read
/// them), and each driver's minute (count, own time, longest, long ones). Fed on the trace's thread, read on the
/// sampler's, under one lock. Nothing per call allocates: drivers are small numbers handed out by <see cref="Driver"/>,
/// the ring holds structs, the tallies are arrays.
/// </summary>
internal sealed class DriverLedger
{
    /// <summary>The ring's first size; it doubles as bursts come, up to <see cref="DriverRules.RingMax"/>.</summary>
    internal const int RingStart = 256;

    private readonly Lock _gate = new();
    private readonly long _ticksPerSecond;
    private readonly double _msPerTick;
    private readonly ExclusiveTime _exclusive;
    private readonly List<string> _names = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    // The clock: a trace tick (the precise counter) and the wall time it was, read together; see Anchor.
    private long _anchorTicks;
    private double _anchorMs;

    // The ring, oldest at _head.
    private DriverBurst[] _ring = new DriverBurst[RingStart];
    private int _head, _count;

    // This minute's tallies, by driver number.
    private Tally[] _tallies = new Tally[16], _spare = new Tally[16];

    private readonly long[] _calls = new long[4];
    private long _threadedUs, _bursts, _long;

    private struct Tally
    {
        public int Count, LongestUs, Long;
        public long TotalUs;
        // Apart by kind (the Latency page, issue #15): DPCs (timer DPCs with them) and interrupts.
        public int DpcCount, DpcLongestUs, IsrCount, IsrLongestUs;
        public long DpcUs, IsrUs;
        // Calls by how long they took (DriverRules.BucketUs): under 0.25 ms up to 4 ms or more.
        public int B0, B1, B2, B3, B4, B5;
    }

    // The live Latency page's seconds (issue #15): each second's longest call and its driver, the last LiveSeconds kept.
    internal const int LiveSeconds = 120;
    private readonly (long Sec, int Us, int Driver)[] _seconds = new (long, int, int)[LiveSeconds];

    // Drivers right now (1.23.0, issue #18 item 11): each driver's calls in each of the last WindowSeconds seconds, kept
    // only while the Latency page is open (Watch) and let go when it closes. A row of cells by driver number for each
    // second, cleared when its second comes round again; drivers are numbered only once they make a call, so a row is as
    // long as the drivers seen (a few dozen; 24 bytes each). TakeMinute's tallies are never touched.
    internal const int WindowSeconds = 10;
    private struct Cell
    {
        public int Count, LongestUs;
        public long TotalUs, IsrUs;
    }
    private Cell[][]? _window;
    private readonly long[] _windowSec = new long[WindowSeconds];
    private Cell[] _windowSum = [];
    private long _watchFrom;

    /// <param name="ticksPerSecond">The trace clock's rate (the precise counter's: Stopwatch.Frequency).</param>
    /// <param name="cpus">Cores to make room for at the start (more are made room for as they come).</param>
    public DriverLedger(long ticksPerSecond, int cpus = 0)
    {
        _ticksPerSecond = ticksPerSecond;
        _msPerTick = 1000.0 / ticksPerSecond;
        _exclusive = new ExclusiveTime(cpus);
        Driver(""); // number 0: an address no loaded driver holds
    }

    /// <summary>
    /// Ties the trace's clock to the wall clock: <paramref name="ticks"/> (the precise counter, read just now) was
    /// <paramref name="unixMs"/>. The two drift apart by a few ms an hour, and the wall clock can be set, so the trace ties
    /// them again every few minutes; a burst's time is then off by no more than that drift, far under a frame's.
    /// </summary>
    public void Anchor(long ticks, double unixMs)
    {
        lock (_gate)
        {
            _anchorTicks = ticks;
            _anchorMs = unixMs;
        }
    }

    /// <summary>The number of a driver's file ("" for no driver), given once per file and kept for the run.</summary>
    public int Driver(string file)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(file, out int i)) return i;
            i = _names.Count;
            _names.Add(file);
            _index[file] = i;
            return i;
        }
    }

    /// <summary>The file a driver number stands for.</summary>
    public string Name(int driver)
    {
        lock (_gate) return driver >= 0 && driver < _names.Count ? _names[driver] : "";
    }

    /// <summary>
    /// One call, as Windows logged it: it ended at <paramref name="endTicks"/> after <paramref name="ticks"/> on core
    /// <paramref name="cpu"/>. Returns its own time in µs (what was inside it on that core taken out).
    /// </summary>
    public int Add(long endTicks, long ticks, int cpu, int driver, DriverCall kind)
    {
        lock (_gate)
        {
            long own = _exclusive.Take(cpu, endTicks - ticks, endTicks);
            // Whole µs, in whole numbers: 250 ticks of a 1 MHz clock must be 250, not 249.99999 cut to 249.
            int us = (int)Math.Min(int.MaxValue, own * 1_000_000 / _ticksPerSecond);
            _calls[(int)kind]++;
            if (kind == DriverCall.ThreadedDpc)
            {
                _threadedUs += us;
                return us;
            }

            if ((uint)driver >= (uint)_names.Count) driver = 0;
            if (us > _liveUs) (_liveUs, _liveDriver) = (us, driver);
            if (driver >= _tallies.Length)
            {
                int size = Math.Max(_names.Count, _tallies.Length * 2);
                Array.Resize(ref _tallies, size);
                Array.Resize(ref _spare, size);
            }
            ref var t = ref _tallies[driver];
            t.Count++;
            t.TotalUs += us;
            if (us > t.LongestUs) t.LongestUs = us;
            if (kind == DriverCall.Isr)
            {
                t.IsrCount++;
                t.IsrUs += us;
                if (us > t.IsrLongestUs) t.IsrLongestUs = us;
            }
            else
            {
                t.DpcCount++;
                t.DpcUs += us;
                if (us > t.DpcLongestUs) t.DpcLongestUs = us;
            }
            switch (DriverSplit.BucketOf(us))
            {
                case 0: t.B0++; break;
                case 1: t.B1++; break;
                case 2: t.B2++; break;
                case 3: t.B3++; break;
                case 4: t.B4++; break;
                default: t.B5++; break;
            }
            // Its second, by the wall clock the trace is tied to; a slot of the ring is taken over when its second comes round again.
            long sec = (long)Math.Floor((_anchorMs + (endTicks - _anchorTicks) * _msPerTick) / 1000.0);
            ref var slot = ref _seconds[(int)(((sec % LiveSeconds) + LiveSeconds) % LiveSeconds)];
            if (slot.Sec != sec) slot = (sec, us, driver);
            else if (us > slot.Us) (slot.Us, slot.Driver) = (us, driver);
            if (_window is not null) Window(sec, driver, us, kind == DriverCall.Isr);
            if (us >= DriverRules.LongUs)
            {
                t.Long++;
                _long++;
            }

            if (us >= DriverRules.RingUs)
            {
                _bursts++;
                double endMs = _anchorMs + (endTicks - _anchorTicks) * _msPerTick;
                Keep(new DriverBurst(endMs, us, cpu, _names[driver]));
            }
            return us;
        }
    }

    /// <summary>
    /// Adds a burst at the ring's end, after dropping those older than <see cref="DriverRules.LookBackSec"/> from its
    /// start. Cores hand their events over in batches, so the ring is only roughly in time order: a burst slightly out of
    /// order waits for the next pass, and <see cref="Recent"/> looks at every one.
    /// </summary>
    private void Keep(DriverBurst burst)
    {
        double oldest = burst.EndMs - DriverRules.LookBackSec * 1000.0;
        while (_count > 0 && _ring[_head].EndMs < oldest)
        {
            _ring[_head] = default;
            _head = (_head + 1) % _ring.Length;
            _count--;
        }
        if (_count == _ring.Length)
        {
            if (_ring.Length < DriverRules.RingMax)
            {
                var bigger = new DriverBurst[Math.Min(DriverRules.RingMax, _ring.Length * 2)];
                for (int i = 0; i < _count; i++) bigger[i] = _ring[(_head + i) % _ring.Length];
                _ring = bigger;
                _head = 0;
            }
            else
            {
                // Full at the cap: a storm of bursts; the oldest goes.
                _head = (_head + 1) % _ring.Length;
                _count--;
            }
        }
        _ring[(_head + _count) % _ring.Length] = burst;
        _count++;
    }

    // The longest call since the taskbar last asked (TakeLive), threaded DPCs left out as everywhere else.
    private int _liveUs, _liveDriver;

    /// <summary>
    /// The longest single call since the last time this was asked, and its driver's file ("" when it matched none), for
    /// the taskbar's driver delay (issue #15 follow-up); starts again from nothing.
    /// </summary>
    public (int Us, string Driver) TakeLive()
    {
        lock (_gate)
        {
            var live = (_liveUs, _liveDriver >= 0 && _liveDriver < _names.Count ? _names[_liveDriver] : "");
            (_liveUs, _liveDriver) = (0, 0);
            return live;
        }
    }

    // One call into its second's row (under the lock). A call from a second older than its slot holds (batches come
    // slightly out of order) is from before the window and is left out; a newer one takes the slot over.
    private void Window(long sec, int driver, int us, bool isr)
    {
        int i = (int)(((sec % WindowSeconds) + WindowSeconds) % WindowSeconds);
        if (sec < _windowSec[i] || sec < _watchFrom) return;
        ref var row = ref _window![i];
        if (sec != _windowSec[i])
        {
            Array.Clear(row);
            _windowSec[i] = sec;
        }
        if (driver >= row.Length) Array.Resize(ref row, Math.Max(_names.Count, driver + 1));
        ref var c = ref row[driver];
        c.Count++;
        c.TotalUs += us;
        if (us > c.LongestUs) c.LongestUs = us;
        if (isr) c.IsrUs += us;
    }

    /// <summary>
    /// Starts or stops keeping each driver's last <see cref="WindowSeconds"/> seconds (Drivers right now, only while the
    /// Latency page is open); <paramref name="nowSec"/> (Unix seconds) is when it starts. Stopping lets the rows go.
    /// Returns whether this changed anything.
    /// </summary>
    public bool Watch(bool on, long nowSec)
    {
        lock (_gate)
        {
            if (on == _window is not null) return false;
            if (on)
            {
                _window = new Cell[WindowSeconds][];
                for (int i = 0; i < WindowSeconds; i++) _window[i] = new Cell[_names.Count];
                Array.Clear(_windowSec);
                _watchFrom = nowSec;
            }
            else
            {
                _window = null;
                _windowSum = [];
            }
            return true;
        }
    }

    /// <summary>
    /// Each driver's calls from <paramref name="fromSec"/> to <paramref name="toSec"/> (Unix seconds, both included, at
    /// most <see cref="WindowSeconds"/>), drivers with none left out, and how many of those seconds were kept (fewer
    /// just after <see cref="Watch"/> started; 0 and nothing while it isn't on).
    /// </summary>
    public (List<DriverNow> Drivers, int Seconds) Now(long fromSec, long toSec)
    {
        lock (_gate)
        {
            if (_window is null) return ([], 0);
            fromSec = Math.Max(Math.Max(fromSec, toSec - WindowSeconds + 1), _watchFrom);
            int seconds = (int)Math.Max(0, toSec - fromSec + 1);
            if (_windowSum.Length < _names.Count) _windowSum = new Cell[_names.Count];
            else Array.Clear(_windowSum);
            for (long sec = fromSec; sec <= toSec; sec++)
            {
                int i = (int)(((sec % WindowSeconds) + WindowSeconds) % WindowSeconds);
                if (_windowSec[i] != sec) continue;
                var row = _window[i];
                for (int d = 0; d < row.Length && d < _windowSum.Length; d++)
                {
                    if (row[d].Count == 0) continue;
                    ref var s = ref _windowSum[d];
                    s.Count += row[d].Count;
                    s.TotalUs += row[d].TotalUs;
                    s.IsrUs += row[d].IsrUs;
                    if (row[d].LongestUs > s.LongestUs) s.LongestUs = row[d].LongestUs;
                }
            }
            var list = new List<DriverNow>();
            for (int d = 0; d < _windowSum.Length && d < _names.Count; d++)
            {
                ref var s = ref _windowSum[d];
                if (s.Count > 0) list.Add(new DriverNow(_names[d], s.Count, s.TotalUs, s.TotalUs - s.IsrUs, s.IsrUs, s.LongestUs));
            }
            return (list, seconds);
        }
    }

    /// <summary>The kept bursts that ended at or after <paramref name="sinceMs"/> (Unix ms), oldest first as they came; a copy.</summary>
    public IReadOnlyList<DriverBurst> Recent(double sinceMs)
    {
        lock (_gate)
        {
            var list = new List<DriverBurst>();
            for (int i = 0; i < _count; i++)
            {
                var b = _ring[(_head + i) % _ring.Length];
                if (b.EndMs >= sinceMs) list.Add(b);
            }
            return list;
        }
    }

    /// <summary>
    /// Each driver's work since the last call, as the minute starting at <paramref name="minuteTs"/> (Unix seconds),
    /// drivers that did nothing left out; the counts start again from nothing.
    /// </summary>
    public List<DriverTally> TakeMinute(long minuteTs)
    {
        Tally[] taken;
        string[] names;
        lock (_gate)
        {
            taken = _tallies;
            Array.Clear(_spare);
            _tallies = _spare;
            _spare = taken;
            names = [.. _names];
        }
        // Read outside the lock: _spare (the taken array) is written again only at the next TakeMinute, on this same thread.
        var list = new List<DriverTally>();
        for (int i = 0; i < taken.Length && i < names.Length; i++)
            if (taken[i].Count > 0)
            {
                ref var t = ref taken[i];
                var split = new DriverSplit(t.DpcCount, t.DpcUs, t.DpcLongestUs, t.IsrCount, t.IsrUs, t.IsrLongestUs, [t.B0, t.B1, t.B2, t.B3, t.B4, t.B5]);
                list.Add(new DriverTally(minuteTs, names[i], t.Count, t.TotalUs, t.LongestUs, t.Long, split));
            }
        return list;
    }

    /// <summary>
    /// The seconds from <paramref name="fromSec"/> to <paramref name="toSec"/> (Unix seconds, both included, at most
    /// <see cref="LiveSeconds"/>): each one's longest call and its driver's file, 0 and "" for a second with no call.
    /// </summary>
    public List<(long Sec, int Us, string Driver)> Seconds(long fromSec, long toSec)
    {
        lock (_gate)
        {
            var list = new List<(long, int, string)>();
            for (long sec = Math.Max(fromSec, toSec - LiveSeconds + 1); sec <= toSec; sec++)
            {
                var slot = _seconds[(int)(((sec % LiveSeconds) + LiveSeconds) % LiveSeconds)];
                list.Add(slot.Sec == sec ? (sec, slot.Us, slot.Driver >= 0 && slot.Driver < _names.Count ? _names[slot.Driver] : "") : (sec, 0, ""));
            }
            return list;
        }
    }

    /// <summary>Calls of each kind since the start (see <see cref="DriverCall"/>).</summary>
    public long Calls(DriverCall kind)
    {
        lock (_gate) return _calls[(int)kind];
    }

    /// <summary>Threaded DPCs' time since the start (µs): counted apart, never part of a driver's tally.</summary>
    public long ThreadedUs
    {
        get { lock (_gate) return _threadedUs; }
    }

    /// <summary>Bursts kept in the ring since the start (<see cref="DriverRules.RingUs"/> or more), and the long ones among them.</summary>
    public (long Bursts, long Long) Totals
    {
        get { lock (_gate) return (_bursts, _long); }
    }
}

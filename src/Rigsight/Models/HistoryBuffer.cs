namespace Rigsight.Models;

/// <summary>
/// Ring buffer of timestamped samples, up to a fixed number. NaN marks a missing reading.
/// Every sensor has one, so it is kept small: storage grows as samples arrive (a window open for a few minutes
/// holds a few minutes, not a full hour of empty slots), values are floats (far finer than anything shown),
/// and times are 32-bit millisecond offsets from a base (re-based long before they could overflow).
/// </summary>
public sealed class HistoryBuffer
{
    private const int InitialSize = 64;
    // Offsets stay well below int.MaxValue (~24.8 days); the newest sample is never more than this past the base.
    private const long RebaseAfterMs = 1L << 30; // ~12.4 days

    private float[] _values = [];
    private int[] _offsets = [];
    private long _base;
    private int _start;

    public HistoryBuffer(int capacity) => Capacity = capacity;

    /// <summary>The most samples kept; older ones are dropped.</summary>
    public int Capacity { get; }
    public int Count { get; private set; }

    /// <summary>Time (Unix milliseconds) of the newest sample, or 0 when empty.</summary>
    public long LastTime => Count == 0 ? 0 : TimeAt(Count - 1);

    public void Add(long timeMs, double value)
    {
        if (Count == 0) _base = timeMs;
        else if (timeMs - _base > RebaseAfterMs)
        {
            Rebase(TimeAt(0));
            // Still too far: the oldest sample is itself weeks old (a PC that slept for weeks with the window open).
            // Start afresh rather than overflow the offsets; no chart looks back that far.
            if (timeMs - _base > int.MaxValue)
            {
                Clear();
                _base = timeMs;
            }
        }

        if (Count < _values.Length)
        {
            // Not full yet (storage never wraps before it has reached Capacity, so _start is 0 here).
            _values[Count] = (float)value;
            _offsets[Count] = (int)(timeMs - _base);
            Count++;
        }
        else if (_values.Length < Capacity)
        {
            Grow();
            Add(timeMs, value);
        }
        else
        {
            _values[_start] = (float)value;
            _offsets[_start] = (int)(timeMs - _base);
            _start = (_start + 1) % Capacity;
        }
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
    }

    /// <summary>Time of the oldest sample, or 0 when empty.</summary>
    public long FirstTime => Count == 0 ? 0 : TimeAt(0);

    /// <summary>Index of the sample closest in time to <paramref name="timeMs"/>, or -1 when empty.</summary>
    public int NearestIndex(long timeMs)
    {
        if (Count == 0) return -1;
        int i = IndexAtOrAfter(timeMs);
        if (i >= Count) return Count - 1;
        if (i > 0 && timeMs - TimeAt(i - 1) < TimeAt(i) - timeMs) return i - 1;
        return i;
    }

    public double ValueAt(int i) => _values[Slot(i)];
    public long TimeAt(int i) => _base + _offsets[Slot(i)];

    /// <summary>Index of the first sample at or after <paramref name="timeMs"/> (binary search; samples are time-ordered).</summary>
    public int IndexAtOrAfter(long timeMs)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (TimeAt(mid) < timeMs) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private int Slot(int i)
    {
        int s = _start + i;
        return s >= _values.Length ? s - _values.Length : s;
    }

    private void Grow()
    {
        int size = Math.Min(Capacity, Math.Max(InitialSize, _values.Length * 2));
        Array.Resize(ref _values, size);
        Array.Resize(ref _offsets, size);
    }

    /// <summary>Moves the base to <paramref name="newBase"/> (the oldest sample), shrinking every offset.</summary>
    private void Rebase(long newBase)
    {
        int delta = (int)(newBase - _base);
        for (int i = 0; i < Count; i++) _offsets[Slot(i)] -= delta;
        _base = newBase;
    }
}

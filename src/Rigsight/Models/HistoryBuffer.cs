namespace Rigsight.Models;

/// <summary>Fixed-size ring buffer of timestamped samples. NaN marks a missing reading.</summary>
public sealed class HistoryBuffer
{
    private readonly double[] _values;
    private readonly long[] _times;
    private int _start;

    public HistoryBuffer(int capacity)
    {
        _values = new double[capacity];
        _times = new long[capacity];
    }

    public int Capacity => _values.Length;
    public int Count { get; private set; }

    /// <summary>Time (Unix milliseconds) of the newest sample, or 0 when empty.</summary>
    public long LastTime => Count == 0 ? 0 : _times[(_start + Count - 1) % Capacity];

    public void Add(long timeMs, double value)
    {
        if (Count < Capacity)
        {
            int i = (_start + Count) % Capacity;
            _values[i] = value;
            _times[i] = timeMs;
            Count++;
        }
        else
        {
            _values[_start] = value;
            _times[_start] = timeMs;
            _start = (_start + 1) % Capacity;
        }
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
    }

    /// <summary>Time of the oldest sample, or 0 when empty.</summary>
    public long FirstTime => Count == 0 ? 0 : _times[_start];

    /// <summary>Index of the sample closest in time to <paramref name="timeMs"/>, or -1 when empty.</summary>
    public int NearestIndex(long timeMs)
    {
        if (Count == 0) return -1;
        int i = IndexAtOrAfter(timeMs);
        if (i >= Count) return Count - 1;
        if (i > 0 && timeMs - TimeAt(i - 1) < TimeAt(i) - timeMs) return i - 1;
        return i;
    }

    public double ValueAt(int i) => _values[(_start + i) % Capacity];
    public long TimeAt(int i) => _times[(_start + i) % Capacity];

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
}

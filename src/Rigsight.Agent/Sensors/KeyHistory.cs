using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Agent.Sensors;

/// <summary>Last hour of a few key sensors, so the app's charts and the graph widget start filled.</summary>
internal sealed class KeyHistory
{
    private const int Capacity = 1800;

    private sealed class Ring
    {
        public readonly long[] Times = new long[Capacity];
        public readonly float?[] Values = new float?[Capacity];
        public int Start, Count;

        public void Add(long t, float? v)
        {
            int i = (Start + Count) % Capacity;
            if (Count < Capacity) Count++;
            else Start = (Start + 1) % Capacity;
            Times[i] = t;
            Values[i] = v;
        }
    }

    private readonly Dictionary<string, Ring> _rings = KeySensors.HistoryKeys.ToDictionary(k => k, _ => new Ring());
    private readonly Lock _lock = new();

    public void Add(long timeMs, SensorHost host) => Add(timeMs, host.Read);

    /// <summary>Adds one reading of every key, as <paramref name="read"/> gives it (null: no reading).</summary>
    internal void Add(long timeMs, Func<string, double?> read)
    {
        lock (_lock)
        {
            foreach (var (key, ring) in _rings)
            {
                var v = read(key);
                ring.Add(timeMs, v is double d ? (float)Math.Round(d, 1) : null);
            }
        }
    }

    public List<SeriesHistory> Snapshot()
    {
        lock (_lock)
        {
            return [.. _rings.Select(kv =>
            {
                var r = kv.Value;
                var times = new long[r.Count];
                var values = new float?[r.Count];
                for (int i = 0; i < r.Count; i++)
                {
                    times[i] = r.Times[(r.Start + i) % Capacity];
                    values[i] = r.Values[(r.Start + i) % Capacity];
                }
                return new SeriesHistory { Key = kv.Key, Times = times, Values = values };
            })];
        }
    }

    /// <summary>Values from the last <paramref name="windowMs"/> of one key (for the graph widget).</summary>
    public float[] Recent(string key, long windowMs)
    {
        lock (_lock)
        {
            if (!_rings.TryGetValue(key, out var r) || r.Count == 0) return [];
            long last = r.Times[(r.Start + r.Count - 1) % Capacity];
            var list = new List<float>();
            for (int i = 0; i < r.Count; i++)
            {
                int idx = (r.Start + i) % Capacity;
                if (r.Times[idx] >= last - windowMs)
                    list.Add(r.Values[idx] ?? float.NaN);
            }
            return [.. list];
        }
    }
}

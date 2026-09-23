using Rigsight.Core.Protocol;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// The last few hours of each drive temperature, so the app's drive graphs start filled. Drives are read
/// every 5 minutes in the background (10 s while the app is open); one point a minute at most is kept.
/// Written on the sampler thread, read when an app connects.
/// </summary>
internal sealed class DriveTempHistory
{
    private const int Capacity = 6 * 60 + 10;
    private const long MinSpacingMs = 60_000;

    private sealed class Series
    {
        public readonly Queue<(long Time, float Value)> Points = new();
        public long LastTime;
    }

    private readonly Dictionary<string, Series> _series = [];
    private readonly Lock _lock = new();

    public void Add(long timeMs, SensorHost host)
    {
        lock (_lock)
        {
            foreach (var (id, value) in host.DriveTemperatures())
            {
                if (!_series.TryGetValue(id, out var s)) _series[id] = s = new Series();
                if (timeMs - s.LastTime < MinSpacingMs) continue;
                s.Points.Enqueue((timeMs, (float)Math.Round(value, 1)));
                s.LastTime = timeMs;
                while (s.Points.Count > Capacity) s.Points.Dequeue();
            }
        }
    }

    /// <summary>Each drive temperature's history, keyed "id:&lt;sensor id&gt;".</summary>
    public List<SeriesHistory> Snapshot()
    {
        lock (_lock)
        {
            return [.. _series.Select(kv => new SeriesHistory
            {
                Key = "id:" + kv.Key,
                Times = [.. kv.Value.Points.Select(p => p.Time)],
                Values = [.. kv.Value.Points.Select(p => (float?)p.Value)],
            })];
        }
    }
}

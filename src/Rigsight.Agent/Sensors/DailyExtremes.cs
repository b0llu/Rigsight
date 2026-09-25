using System.Globalization;
using System.Text.Json;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// Each sensor's lowest and highest reading since midnight, kept by the always-running agent so the app
/// can show today's range even for time it wasn't open. Sampler thread only.
/// </summary>
internal sealed class DailyExtremes
{
    private sealed record Saved(string Day, Dictionary<string, double[]> Values);

    private readonly Func<DateTime> _now;
    private string _day;
    private DateTime _nextMidnight;
    private readonly Dictionary<string, double[]> _values = [];
    private readonly HashSet<string> _changed = [];

    public DailyExtremes() : this(() => DateTime.Now) { }

    /// <param name="now">The local time now (tests pass their own clock, to cross midnight).</param>
    internal DailyExtremes(Func<DateTime> now)
    {
        _now = now;
        _day = Today();
        _nextMidnight = now().Date.AddDays(1);
    }

    public string Day => _day;

    /// <summary>Some range changed since the last save.</summary>
    public bool Dirty { get; set; }

    private string Today() => _now().Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Call once per round of readings, before <see cref="Observe"/>: starts a new day after midnight.</summary>
    public void BeginTick() => RollDay();

    public void Observe(string sensorId, double? value)
    {
        if (value is not double v || !double.IsFinite(v)) return;
        if (!_values.TryGetValue(sensorId, out var mm))
        {
            _values[sensorId] = [v, v];
            _changed.Add(sensorId);
            Dirty = true;
        }
        else if (v < mm[0] || v > mm[1])
        {
            mm[0] = Math.Min(mm[0], v);
            mm[1] = Math.Max(mm[1], v);
            _changed.Add(sensorId);
            Dirty = true;
        }
    }

    /// <summary>Widens today's range with values recorded earlier (e.g. from the minute history).</summary>
    public void Include(string sensorId, double? min, double? max)
    {
        RollDay();
        Observe(sensorId, min);
        Observe(sensorId, max);
    }

    private void RollDay()
    {
        var now = _now();
        if (now < _nextMidnight) return;
        _nextMidnight = now.Date.AddDays(1);
        string today = Today();
        if (today == _day) return;
        _day = today;
        _values.Clear();
        _changed.Clear();
        Dirty = true;
    }

    public Dictionary<string, double[]> Snapshot()
    {
        RollDay();
        return _values.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone());
    }

    /// <summary>Ranges that changed since the last call (for the app's once-a-second update).</summary>
    public Dictionary<string, double[]>? TakeChanges()
    {
        RollDay();
        if (_changed.Count == 0) return null;
        var changes = _changed.ToDictionary(id => id, id => (double[])_values[id].Clone());
        _changed.Clear();
        return changes;
    }

    public string Serialize() => JsonSerializer.Serialize(new Saved(_day, _values));

    /// <summary>Restores today's ranges saved before the agent restarted (ignored if they're from another day).</summary>
    public void Load(string? json)
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(json);
            if (saved?.Day != Today() || saved.Values is null) return;
            foreach (var (id, mm) in saved.Values)
                if (mm.Length == 2) Include(id, mm[0], mm[1]);
        }
        catch (JsonException)
        {
            // An unreadable save only costs today's earlier range.
        }
    }
}

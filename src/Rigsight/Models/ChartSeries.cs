using System.Windows.Media;
using Rigsight.Core.Data;

namespace Rigsight.Models;

/// <summary>One line on a history chart.</summary>
public sealed class ChartSeries
{
    public ChartSeries(string label, SensorItem sensor, Color color, Func<SystemMinute, double?>? fromMinute = null)
    {
        Label = label;
        Sensor = sensor;
        Color = color;
        FromMinute = fromMinute;
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
    }

    public string Label { get; }
    public SensorItem Sensor { get; }
    public Color Color { get; }
    public SolidColorBrush Brush { get; }

    /// <summary>Picks this line's value out of a stored minute (null: the minute history doesn't have it).</summary>
    public Func<SystemMinute, double?>? FromMinute { get; }

    /// <summary>Older history, one point per minute, for chart windows longer than the live buffer.</summary>
    public HistoryBuffer Minutes { get; } = new(24 * 60 + 60);

    /// <summary>Replaces the minute history. Gaps (PC off or asleep) are kept as breaks in the line.</summary>
    public void LoadMinutes(IEnumerable<SystemMinute> minutes)
    {
        Minutes.Clear();
        if (FromMinute is null) return;
        long previous = 0;
        foreach (var m in minutes)
        {
            long t = m.Ts * 1000 + 30_000; // the middle of the minute
            if (previous != 0 && t - previous > 150_000) Minutes.Add(previous + 60_000, double.NaN);
            Minutes.Add(t, FromMinute(m) ?? double.NaN);
            previous = t;
        }
    }
}

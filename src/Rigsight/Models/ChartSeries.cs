using System.Windows.Media;
using Rigsight.Core.Data;

namespace Rigsight.Models;

/// <summary>One line on a history chart.</summary>
public sealed class ChartSeries
{
    /// <param name="colorKey">The palette color ("CpuColor"…), so the line follows the dark or light theme.</param>
    /// <param name="minuteStats">A stored minute's average, highest and lowest, for the hover box (null: none kept).</param>
    public ChartSeries(string label, SensorItem sensor, string colorKey, Func<SystemMinute, double?>? fromMinute = null,
        Func<SystemMinute, (double? Avg, double? High, double? Low)>? minuteStats = null)
    {
        MinuteStats = minuteStats;
        Label = label;
        Sensor = sensor;
        _colorKey = colorKey;
        FromMinute = fromMinute;
    }

    /// <summary>The name in the legend and hover box (a renamed sensor updates it).</summary>
    public string Label { get; set; }
    public SensorItem Sensor { get; }

    // Drawing resources, made once per theme (the chart redraws every second).
    private readonly string _colorKey;
    private int _theme = -1;
    private Color _color;
    private SolidColorBrush _brush = null!;
    private Pen _linePen = null!;
    private System.Windows.Media.Brush _fill = null!;

    private void EnsureColors()
    {
        if (_theme == Services.ThemeManager.Version) return;
        _theme = Services.ThemeManager.Version;
        _color = Rigsight.Controls.ChartPaint.Res(_colorKey, 0x80);
        _brush = new SolidColorBrush(_color);
        _brush.Freeze();
        _linePen = Frozen(new Pen(_brush, 2) { LineJoin = PenLineJoin.Round });
        _fill = Rigsight.Controls.ChartGeometry.FadeFill(_color, Services.ThemeManager.IsLight ? 0.05 : Services.ThemeManager.IsGrey ? 0.07 : 0.10);
    }

    public Color Color { get { EnsureColors(); return _color; } }
    public SolidColorBrush Brush { get { EnsureColors(); return _brush; } }
    public Pen LinePen { get { EnsureColors(); return _linePen; } }
    public System.Windows.Media.Brush Fill { get { EnsureColors(); return _fill; } }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    /// <summary>Picks this line's value out of a stored minute (null: the minute history doesn't have it).</summary>
    public Func<SystemMinute, double?>? FromMinute { get; }

    /// <summary>Older history, one point per minute, for chart windows longer than the live buffer.</summary>
    public HistoryBuffer Minutes { get; } = new(24 * 60 + 60);

    /// <summary>Picks a stored minute's average, highest and lowest (each null where the history doesn't have it).</summary>
    public Func<SystemMinute, (double? Avg, double? High, double? Low)>? MinuteStats { get; }

    // Beside Minutes, sample for sample: what the hover box says about each minute. Empty without MinuteStats.
    private readonly HistoryBuffer _avg = new(24 * 60 + 60), _high = new(24 * 60 + 60), _low = new(24 * 60 + 60);

    /// <summary>The average, highest and lowest of the minute at <paramref name="index"/> of <see cref="Minutes"/> (null: not kept for this line).</summary>
    public (double? Avg, double? High, double? Low)? StatsAt(int index)
    {
        if (index < 0 || index >= _avg.Count || _avg.Count != Minutes.Count) return null;
        static double? Kept(double v) => double.IsNaN(v) ? null : v;
        return (Kept(_avg.ValueAt(index)), Kept(_high.ValueAt(index)), Kept(_low.ValueAt(index)));
    }

    /// <summary>Replaces the minute history. Gaps (PC off or asleep) are kept as breaks in the line.</summary>
    public void LoadMinutes(IEnumerable<SystemMinute> minutes)
    {
        if (FromMinute is null) LoadPoints([]);
        else Load(minutes.Select(m => (m.Ts, FromMinute(m), MinuteStats?.Invoke(m))));
    }

    /// <summary>Replaces the minute history with other minutes (a fan's speeds), by each minute's start.</summary>
    public void LoadPoints(IEnumerable<(long Ts, double? Value)> points) =>
        Load(points.Select(p => (p.Ts, p.Value, ((double? Avg, double? High, double? Low)?)null)));

    private void Load(IEnumerable<(long Ts, double? Value, (double? Avg, double? High, double? Low)? Stats)> points)
    {
        Minutes.Clear();
        _avg.Clear();
        _high.Clear();
        _low.Clear();
        long previous = 0;
        void Add(long t, double value, (double? Avg, double? High, double? Low)? stats)
        {
            Minutes.Add(t, value);
            if (MinuteStats is null) return;
            _avg.Add(t, stats?.Avg ?? double.NaN);
            _high.Add(t, stats?.High ?? double.NaN);
            _low.Add(t, stats?.Low ?? double.NaN);
        }
        foreach (var (ts, value, stats) in points)
        {
            long t = ts * 1000 + 30_000; // the middle of the minute
            if (previous != 0 && t - previous > 150_000) Add(previous + 60_000, double.NaN, null);
            Add(t, value ?? double.NaN, stats);
            previous = t;
        }
    }
}

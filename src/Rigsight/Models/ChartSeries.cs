using System.Windows.Media;
using Rigsight.Core.Data;

namespace Rigsight.Models;

/// <summary>One line on a history chart.</summary>
public sealed class ChartSeries
{
    /// <param name="colorKey">The palette color ("CpuColor"…), so the line follows the dark or light theme.</param>
    public ChartSeries(string label, SensorItem sensor, string colorKey, Func<SystemMinute, double?>? fromMinute = null)
    {
        Label = label;
        Sensor = sensor;
        _colorKey = colorKey;
        FromMinute = fromMinute;
    }

    public string Label { get; }
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
        _fill = Rigsight.Controls.ChartGeometry.FadeFill(_color, Services.ThemeManager.IsLight ? 0.05 : 0.10);
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

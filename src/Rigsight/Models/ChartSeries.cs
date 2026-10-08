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
    public HistoryBuffer Minutes { get; } = new(24 * 60 + 60, wholeSeconds: true); // a month of hours or a year of days too

    /// <summary>Picks a stored minute's average, highest and lowest (each null where the history doesn't have it).</summary>
    public Func<SystemMinute, (double? Avg, double? High, double? Low)>? MinuteStats { get; }

    // Beside Minutes, sample for sample: what the hover box says about each minute. Empty without MinuteStats.
    private readonly HistoryBuffer _avg = new(24 * 60 + 60, true), _high = new(24 * 60 + 60, true), _low = new(24 * 60 + 60, true);

    // Beside Minutes too: the line when it's drawn from each minute's highest or lowest. A minute that didn't keep the
    // one asked for (lowest readings are kept since 0.16.1, and never for the hot spot or the memory) has no point
    // there: the line stops rather than pass an average off as a lowest.
    private readonly HistoryBuffer _plotHigh = new(24 * 60 + 60, true), _plotLow = new(24 * 60 + 60, true);

    /// <summary>
    /// The older history as the chart draws it: each minute's (hour's, day's) average, or with <paramref name="pick"/>
    /// 1 its highest, with -1 its lowest.
    /// </summary>
    public HistoryBuffer MinutesFor(int pick) =>
        MinuteStats is null || _plotHigh.Count != Minutes.Count ? Minutes : pick > 0 ? _plotHigh : pick < 0 ? _plotLow : Minutes;

    /// <summary>The average, highest and lowest of the minute at <paramref name="index"/> of <see cref="Minutes"/> (null: not kept for this line).</summary>
    public (double? Avg, double? High, double? Low)? StatsAt(int index)
    {
        if (index < 0 || index >= _avg.Count || _avg.Count != Minutes.Count) return null;
        static double? Kept(double v) => double.IsNaN(v) ? null : v;
        return (Kept(_avg.ValueAt(index)), Kept(_high.ValueAt(index)), Kept(_low.ValueAt(index)));
    }

    /// <summary>
    /// The stretches of the older history with nothing recorded at all (the PC was off or asleep, or nothing was
    /// recording), each from the end of the last minute before it to the start of the first one after, in Unix
    /// milliseconds. Not the same as a reading that's missing from a recorded minute (a sensor that wasn't read).
    /// </summary>
    public IReadOnlyList<(long From, long To)> Gaps => _gaps;
    private readonly List<(long From, long To)> _gaps = [];

    /// <summary>How long each point of <see cref="Minutes"/> stands for: a minute, or an hour on the chart's week and month.</summary>
    public int StepSeconds { get; private set; } = 60;

    /// <summary>
    /// Replaces the minute history. Gaps (PC off or asleep) are kept as breaks in the line. With
    /// <paramref name="stepSeconds"/> 3600 the rows are hours (their average, highest and lowest), for the week and month.
    /// </summary>
    public void LoadMinutes(IEnumerable<SystemMinute> minutes, int stepSeconds = 60)
    {
        if (FromMinute is null) LoadPoints([]);
        else Load(minutes.Select(m => (m.Ts, FromMinute(m), MinuteStats?.Invoke(m))), stepSeconds);
    }

    /// <summary>Replaces the minute history with other minutes (a fan's speeds), by each minute's start.</summary>
    public void LoadPoints(IEnumerable<(long Ts, double? Value)> points) =>
        Load(points.Select(p => (p.Ts, p.Value, ((double? Avg, double? High, double? Low)?)null)));

    private void Load(IEnumerable<(long Ts, double? Value, (double? Avg, double? High, double? Low)? Stats)> points, int stepSeconds = 60)
    {
        StepSeconds = stepSeconds;
        Minutes.Clear();
        _avg.Clear();
        _high.Clear();
        _low.Clear();
        _plotHigh.Clear();
        _plotLow.Clear();
        _gaps.Clear();
        long previous = 0;
        void Add(long t, double value, (double? Avg, double? High, double? Low)? stats)
        {
            Minutes.Add(t, value);
            if (MinuteStats is null) return;
            _avg.Add(t, stats?.Avg ?? double.NaN);
            _high.Add(t, stats?.High ?? double.NaN);
            _low.Add(t, stats?.Low ?? double.NaN);
            _plotHigh.Add(t, double.IsNaN(value) ? value : stats?.High ?? double.NaN);
            _plotLow.Add(t, double.IsNaN(value) ? value : stats?.Low ?? double.NaN);
        }
        foreach (var (ts, value, stats) in points)
        {
            long t = ts * 1000 + stepSeconds * 500L; // the middle of the minute (or hour)
            if (previous != 0 && t - previous > stepSeconds * 2500L)
            {
                Add(previous + stepSeconds * 1000L, double.NaN, null);
                _gaps.Add((previous + stepSeconds * 500L, t - stepSeconds * 500L));
            }
            Add(t, value ?? double.NaN, stats);
            previous = t;
        }
    }
}

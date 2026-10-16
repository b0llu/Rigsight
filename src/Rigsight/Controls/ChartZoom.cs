using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// Drag-to-zoom on a graph that shows a stretch of time (Reports' minute by minute, Temperatures' History, Fans' speed
/// through the day): press and drag across it to mark a stretch, let go to show only that stretch, double-click (or the
/// card's reset button) to see the whole of it again. Zooming never adds detail: the points that are stored (one a
/// minute, one an hour on a week or a month) are only drawn wider. This holds the drag and the zoomed stretch for a
/// chart, and the maths, which the tests check on their own.
/// </summary>
public sealed class ChartZoom
{
    /// <summary>How far the pointer must move before a press counts as a drag (a click, or the first half of a double-click, doesn't).</summary>
    public const double DragPixels = 8;

    /// <summary>The fewest points a zoomed stretch keeps: 15 minutes of a day, 15 hours of a week, 15 days of a year.</summary>
    public const int MinPoints = 15;

    /// <summary>The stretch shown, when zoomed in (null: the whole of it).</summary>
    public (DateTime From, DateTime To)? Range { get; private set; }

    /// <summary>Where a drag started and where the pointer is now (null: no drag going on).</summary>
    public (double Start, double End)? Drag { get; private set; }

    public bool IsZoomed => Range is not null;

    /// <summary>The pointer went down on the plot at <paramref name="x"/>: a drag may start.</summary>
    public void Press(double x) => Drag = (x, x);

    /// <summary>The pointer moved while pressed: the band follows it, kept inside the plot.</summary>
    public void Move(double x, double left, double right)
    {
        if (Drag is { } d) Drag = (d.Start, Math.Clamp(x, left, right));
    }

    /// <summary>A drag that has moved far enough to draw its band.</summary>
    public bool Dragging => Drag is { } d && Math.Abs(d.End - d.Start) > DragPixels;

    /// <summary>
    /// The pointer was let go: zooms to the stretch dragged across (<paramref name="timeAt"/> turns a position into a time),
    /// and says whether it did. A press that hardly moved zooms nothing.
    /// </summary>
    public bool Release(Func<double, DateTime> timeAt, DateTime fullFrom, DateTime fullTo, TimeSpan step)
    {
        if (Drag is not { } d) return false;
        Drag = null;
        if (Math.Abs(d.End - d.Start) <= DragPixels) return false;
        if (FromDrag(timeAt(d.Start), timeAt(d.End), fullFrom, fullTo, step) is not { } range) return false;
        Range = range;
        return true;
    }

    /// <summary>A drag given up (the pointer taken away mid-drag): its band goes, the zoom stays as it was. Says whether there was one.</summary>
    public bool CancelDrag()
    {
        bool was = Drag is not null;
        Drag = null;
        return was;
    }

    /// <summary>Back to the whole stretch. Says whether anything changed.</summary>
    public bool Reset()
    {
        bool was = Range is not null || Drag is not null;
        Range = null;
        Drag = null;
        return was;
    }

    /// <summary>
    /// The stretch to show, inside the whole one: the zoomed one where it still fits (a window that moves along with the
    /// clock can leave it behind: then it's let go), else the whole.
    /// </summary>
    public (DateTime From, DateTime To) Shown(DateTime fullFrom, DateTime fullTo, TimeSpan step)
    {
        if (Range is not { } r) return (fullFrom, fullTo);
        var from = r.From < fullFrom ? fullFrom : r.From;
        var to = r.To > fullTo ? fullTo : r.To;
        if (to - from < MinSpan(step) - TimeSpan.FromSeconds(1))
        {
            Range = null;
            return (fullFrom, fullTo);
        }
        return (from, to);
    }

    /// <summary>The shortest stretch a zoom shows for points <paramref name="step"/> apart.</summary>
    public static TimeSpan MinSpan(TimeSpan step) => step * MinPoints;

    /// <summary>
    /// The stretch a drag from <paramref name="a"/> to <paramref name="b"/> (either way round) zooms to: kept inside the
    /// whole stretch, at least <see cref="MinSpan"/> long (centred on the drag where it was shorter, moved back inside
    /// where that ran past an end), and on whole points (minutes, hours, days). Null when that would be the whole
    /// stretch anyway, or the whole is too short to zoom into.
    /// </summary>
    public static (DateTime From, DateTime To)? FromDrag(DateTime a, DateTime b, DateTime fullFrom, DateTime fullTo, TimeSpan step)
    {
        if (fullTo <= fullFrom || step <= TimeSpan.Zero) return null;
        var min = MinSpan(step);
        if (fullTo - fullFrom <= min) return null;
        if (b < a) (a, b) = (b, a);
        a = Clamp(a, fullFrom, fullTo);
        b = Clamp(b, fullFrom, fullTo);
        // On whole points: a stretch from 9:30:41 says "9:30" and draws from there.
        a = Floor(a, step);
        b = Ceiling(b, step);
        // A whole stretch that doesn't start or end on a whole point (a window up to now) isn't left by rounding.
        if (a < fullFrom) a = fullFrom;
        if (b > fullTo) b = fullTo;
        if (b - a < min)
        {
            var middle = a + (b - a) / 2;
            a = Floor(middle - min / 2, step);
            b = a + min;
            if (a < fullFrom) (a, b) = (fullFrom, fullFrom + min);
            if (b > fullTo) (a, b) = (fullTo - min, fullTo);
        }
        if (a <= fullFrom && b >= fullTo) return null;
        return (a, b);
    }

    private static DateTime Clamp(DateTime t, DateTime lo, DateTime hi) => t < lo ? lo : t > hi ? hi : t;

    // Days are counted from local midnight; minutes and hours on the clock's ticks.
    private static DateTime Floor(DateTime t, TimeSpan step) =>
        step >= TimeSpan.FromDays(1) ? t.Date : new DateTime(t.Ticks - t.Ticks % step.Ticks, t.Kind);

    private static DateTime Ceiling(DateTime t, TimeSpan step)
    {
        var down = Floor(t, step);
        return down == t ? t : down + (step >= TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : step);
    }

    /// <summary>
    /// The zoomed stretch in words, as the card's header says it: "9:30 AM – 11:15 AM" for minutes, "Mon 5, 3 PM – Wed 7,
    /// 9 AM" for hours, "3 Mar – 20 Apr" for days.
    /// </summary>
    public static string Text(DateTime from, DateTime to, TimeSpan step)
    {
        var culture = CultureInfo.CurrentCulture;
        string Of(DateTime t) => step >= TimeSpan.FromDays(1) ? t.ToString("d MMM", culture)
            : step >= TimeSpan.FromHours(1) ? t.ToString("ddd d, ", culture) + Clock(t)
            : Clock(t);
        // A stretch of whole days ends at a midnight: it's said by its last day.
        return $"{Of(from)} – {Of(step >= TimeSpan.FromDays(1) ? to.AddDays(-1) : to)}";
    }

    /// <summary>A time as the charts write it: "3 PM" on the hour, "9:30 AM" otherwise.</summary>
    public static string Clock(DateTime t) =>
        t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.CurrentCulture);

    private static readonly TimeSpan[] TickSteps =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6),
        TimeSpan.FromHours(12), TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7), TimeSpan.FromDays(14),
    ];

    /// <summary>
    /// The times a zoomed stretch is labelled at: on the clock's round steps (every 15 minutes, every 2 hours, every
    /// day...), the shortest step whose labels are at least <paramref name="minPixels"/> apart on a plot
    /// <paramref name="width"/> wide.
    /// </summary>
    public static (List<DateTime> Ticks, TimeSpan Step) Ticks(DateTime from, DateTime to, double width, double minPixels)
    {
        var span = to - from;
        if (span <= TimeSpan.Zero || width <= 0) return ([], TimeSpan.Zero);
        var step = TickSteps.FirstOrDefault(s => width * s.Ticks / span.Ticks >= minPixels, TickSteps[^1]);
        var ticks = new List<DateTime>();
        var t = Floor(from, step >= TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : step);
        if (step > TimeSpan.FromDays(1))
            // Every few days from the first midnight shown.
            for (t = t < from ? t.AddDays(1) : t; t <= to; t = t.AddDays(step.TotalDays)) ticks.Add(t);
        else
            for (t = t < from ? t + step : t; t <= to; t += step) ticks.Add(t);
        return (ticks, step);
    }

    /// <summary>A tick's label for a step of <paramref name="step"/>: a time under a day, a date ("Mon 5", "5 Oct") from one up.</summary>
    public static string TickLabel(DateTime t, TimeSpan step) =>
        step >= TimeSpan.FromDays(2) ? t.ToString("d MMM", CultureInfo.CurrentCulture)
        : step >= TimeSpan.FromDays(1) || (t.TimeOfDay == TimeSpan.Zero && step >= TimeSpan.FromHours(3)) ? t.ToString("ddd d", CultureInfo.CurrentCulture)
        : Clock(t);

    // ── Drawing ──

    private static Brush? _bandFill;
    private static Pen? _bandEdge;

    /// <summary>The band a drag draws across the plot: a see-through blue, edged a little stronger.</summary>
    public void DrawBand(DrawingContext dc, Rect plot)
    {
        if (!Dragging || Drag is not { } d) return;
        _bandFill ??= ChartPaint.Brush(Color.FromRgb(0x60, 0xA5, 0xFA), 0.16);
        if (_bandEdge is null)
        {
            _bandEdge = new Pen(ChartPaint.Brush(Color.FromRgb(0x60, 0xA5, 0xFA), 0.7), 1);
            _bandEdge.Freeze();
        }
        double a = Math.Clamp(Math.Min(d.Start, d.End), plot.Left, plot.Right), b = Math.Clamp(Math.Max(d.Start, d.End), plot.Left, plot.Right);
        dc.DrawRectangle(_bandFill, _bandEdge, new Rect(Math.Round(a) + 0.5, plot.Top + 0.5, Math.Max(0, Math.Round(b - a) - 1), Math.Max(0, plot.Height - 1)));
    }
}

/// <summary>A command that runs an action (the charts' reset; no CanExecute).</summary>
internal sealed class ActionCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}

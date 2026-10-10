using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>
/// An app's memory through today, hour by hour from midnight to now, with a dashed line at its usual (the Processes
/// page, under an opened app). Drawn in the text's own colour: on that page only the totals are coloured.
/// </summary>
public sealed class HourLine : FrameworkElement
{
    /// <summary>MB in each hour from midnight; null where nothing was recorded (the line breaks there).</summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double?>), typeof(HourLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UsualProperty = DependencyProperty.Register(
        nameof(Usual), typeof(double?), typeof(HourLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double?>? Values { get => (IReadOnlyList<double?>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double? Usual { get => (double?)GetValue(UsualProperty); set => SetValue(UsualProperty, value); }

    // Like every chart, it answers the pointer: the hour under it and what the app held then.
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private int _hover = -1;

    public HourLine()
    {
        // A page switched away under a resting pointer takes the chart with it and no "mouse left" is said: the tip
        // (a window of its own) must not be left open behind it.
        Unloaded += (_, _) => HideTip();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideTip(); };
    }

    private void HideTip()
    {
        _hover = -1;
        _tip.IsOpen = false;
    }

    private double XOf(int hour, int count) => count < 2 ? 0 : 1 + (ActualWidth - 2) * hour / (count - 1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Values is not { Count: >= 2 } values || ActualWidth < 4) return;
        double x = e.GetPosition(this).X;
        _hover = Math.Clamp((int)Math.Round((x - 1) / (ActualWidth - 2) * (values.Count - 1)), 0, values.Count - 1);
        _tip.Content = HoverText(_hover);
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = x + 12;
        _tip.VerticalOffset = -30;
        _tip.IsOpen = IsLoaded;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        HideTip();
        InvalidateVisual();
    }

    /// <summary>"3 PM · 1.1 GB"; the hour now is "Now"; an hour with nothing recorded says so.</summary>
    internal string? HoverText(int hour)
    {
        if (Values is not { } values || hour < 0 || hour >= values.Count) return null;
        string when = hour == values.Count - 1 ? "Now" : DateTime.Today.AddHours(hour).ToString("h tt", System.Globalization.CultureInfo.CurrentCulture);
        return $"{when} · {(values[hour] is { } mb ? Units.Megabytes(mb) : "Not recorded")}";
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent background so the whole area answers the pointer.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Values is not { Count: >= 2 } values || ActualWidth < 4 || ActualHeight < 8) return;

        double top = Math.Max(values.Max(v => v ?? 0), Usual ?? 0) * 1.15;
        if (top <= 0) return;
        double floor = ActualHeight - 1;
        double YOf(double mb) => floor - mb / top * (floor - 2);

        if (Usual is { } usual)
        {
            var dashed = new Pen(ChartPaint.Faint, 1) { DashStyle = new DashStyle([3, 4], 0) };
            dashed.Freeze();
            double y = Math.Round(YOf(usual)) + 0.5;
            dc.DrawLine(dashed, new Point(1, y), new Point(ActualWidth - 1, y));
        }

        var color = ChartPaint.Res("TextColor", 0xFF);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            int runStart = -1;
            for (int h = 0; h <= values.Count; h++)
            {
                bool has = h < values.Count && values[h] is not null;
                if (has)
                {
                    var point = new Point(XOf(h, values.Count), YOf(values[h]!.Value));
                    if (runStart < 0)
                    {
                        runStart = h;
                        l.BeginFigure(point, isFilled: false, isClosed: false);
                        a.BeginFigure(new Point(point.X, floor), isFilled: true, isClosed: true);
                        a.LineTo(point, isStroked: false, isSmoothJoin: false);
                    }
                    else
                    {
                        l.LineTo(point, isStroked: true, isSmoothJoin: true);
                        a.LineTo(point, isStroked: false, isSmoothJoin: false);
                    }
                }
                else if (runStart >= 0)
                {
                    a.LineTo(new Point(XOf(h - 1, values.Count), floor), isStroked: false, isSmoothJoin: false);
                    // An hour on its own between two gaps is a dot's worth of line.
                    if (runStart == h - 1) l.LineTo(new Point(XOf(h - 1, values.Count) + 0.01, YOf(values[h - 1]!.Value)), isStroked: true, isSmoothJoin: false);
                    runStart = -1;
                }
            }
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(ChartGeometry.FadeFill(color, 0.16), null, area);
        var pen = new Pen(ChartPaint.TextBrush, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        if (_hover >= 0 && _hover < values.Count)
        {
            double x = Math.Round(XOf(_hover, values.Count)) + 0.5;
            dc.DrawLine(new Pen(ChartPaint.Cursor, 1), new Point(x, 0), new Point(x, ActualHeight));
            if (values[_hover] is { } mb) dc.DrawEllipse(ChartPaint.TextBrush, null, new Point(XOf(_hover, values.Count), YOf(mb)), 2.5, 2.5);
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>What an <see cref="HourLine"/>'s figures are: it picks a round top for them and writes it at the top left.</summary>
public enum HourLineScale { Megabytes, Percent, Seconds }

/// <summary>
/// An app's figures through today, hour by hour from midnight to now, as a line with a dashed line at its usual (the
/// Processes page, under an opened app); or day by day as bars (<see cref="Bars"/>), where a day nothing was recorded
/// is a flat grey line. Drawn in the text's own colour unless given a <see cref="Stroke"/> (the CPU's own).
/// It stands on a ground line, with a round top written faintly at the top left and a dot at now (the user, 2026-11-16:
/// CPU today "looks floating in air"): the top is never under 10% or 512 MB, so a rise from 0.3% to 3% isn't a cliff.
/// </summary>
public sealed class HourLine : FrameworkElement
{
    /// <summary>The figure in each hour from midnight (or each day); null where nothing was recorded (the line breaks there).</summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double?>), typeof(HourLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UsualProperty = DependencyProperty.Register(
        nameof(Usual), typeof(double?), typeof(HourLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What each point's hover says; null for the memory's own ("3 PM · 1.1 GB").</summary>
    public static readonly DependencyProperty TipsProperty = DependencyProperty.Register(
        nameof(Tips), typeof(IReadOnlyList<string?>), typeof(HourLine), new FrameworkPropertyMetadata(null));

    /// <summary>Each value is a bar of its own (days), not a point on a line (hours).</summary>
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(bool), typeof(HourLine), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The colour of the line or the bars; the text's own when not set.</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(HourLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double?>? Values { get => (IReadOnlyList<double?>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double? Usual { get => (double?)GetValue(UsualProperty); set => SetValue(UsualProperty, value); }
    public IReadOnlyList<string?>? Tips { get => (IReadOnlyList<string?>?)GetValue(TipsProperty); set => SetValue(TipsProperty, value); }
    public bool Bars { get => (bool)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale), typeof(HourLineScale), typeof(HourLine), new FrameworkPropertyMetadata(HourLineScale.Megabytes, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>What the figures are, for the round top and how it's written.</summary>
    public HourLineScale Scale { get => (HourLineScale)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    // The round tops: the smallest that holds the highest with a little room. The first is the least the chart shows.
    private static readonly double[] PercentTops = [10, 25, 50, 100];
    private static readonly double[] MegabyteTops = [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072];
    private static readonly double[] SecondTops = [60, 300, 600, 1800, 3600, 7200, 14400, 28800, 57600, 86400, 172800, 345600];

    /// <summary>The chart's top for a highest of <paramref name="most"/>: a round figure, never under the first.</summary>
    internal static double TopFor(double most, HourLineScale scale)
    {
        var tops = scale switch { HourLineScale.Percent => PercentTops, HourLineScale.Seconds => SecondTops, _ => MegabyteTops };
        // A share of the CPU tops out at all of it, however close to it the highest comes.
        double want = scale == HourLineScale.Percent && most <= 100 ? Math.Min(most * 1.1, 100) : most * 1.1;
        foreach (double top in tops) if (top >= want) return top;
        return Math.Ceiling(want / tops[^1]) * tops[^1];
    }

    /// <summary>"10%", "2 GB", "30m": the top as it's written on the chart.</summary>
    internal static string TopText(double top, HourLineScale scale) => scale switch
    {
        HourLineScale.Percent => $"{top:0}%",
        HourLineScale.Seconds => Units.Duration(top),
        _ => Units.Megabytes(top),
    };

    /// <summary>Room over the line for the top's figure.</summary>
    private const double TopRoom = 12;

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

    private double XOf(int hour, int count) => Bars
        ? (hour + 0.5) * ActualWidth / Math.Max(1, count)
        : count < 2 ? 0 : 1 + (ActualWidth - 2) * hour / (count - 1);

    /// <summary>The point or bar under <paramref name="x"/>.</summary>
    private int IndexAt(double x, int count) => Bars
        ? Math.Clamp((int)(x / ActualWidth * count), 0, count - 1)
        : Math.Clamp((int)Math.Round((x - 1) / (ActualWidth - 2) * (count - 1)), 0, count - 1);

    private int Least => Bars ? 1 : 2;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Values is not { } values || values.Count < Least || ActualWidth < 4) return;
        double x = e.GetPosition(this).X;
        _hover = IndexAt(x, values.Count);
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

    /// <summary>The point's own hover when given; else "3 PM · 1.1 GB", the hour now "Now", an hour with nothing recorded says so.</summary>
    internal string? HoverText(int hour)
    {
        if (Values is not { } values || hour < 0 || hour >= values.Count) return null;
        if (Tips is { } tips && hour < tips.Count) return tips[hour];
        string when = hour == values.Count - 1 ? Lang.T("Now") : DateTime.Today.AddHours(hour).ToString(TimeText.Fit("h tt"), System.Globalization.CultureInfo.CurrentCulture);
        return $"{when} · {(values[hour] is { } mb ? Units.Megabytes(mb) : Wording.NotRecorded)}";
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent background so the whole area answers the pointer.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Values is not { } values || values.Count < Least || ActualWidth < 4 || ActualHeight < 8) return;

        double top = TopFor(Math.Max(values.Max(v => v ?? 0), Usual ?? 0), Scale);
        double floor = ActualHeight - 1;
        double YOf(double v) => floor - Math.Min(v, top) / top * (floor - TopRoom);

        // The ground under the whole day, and the top's figure.
        var ground = new Pen(ChartPaint.Faint, 1);
        ground.Freeze();
        dc.PushOpacity(0.35);
        dc.DrawLine(ground, new Point(0, floor + 0.5), new Point(ActualWidth, floor + 0.5));
        dc.Pop();
        var topText = new FormattedText(TopText(top, Scale), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), TextSize.Mini, ChartPaint.Label,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(topText, new Point(1, -1));

        if (Usual is { } usual && usual > 0)
        {
            var dashed = new Pen(ChartPaint.Faint, 1) { DashStyle = new DashStyle([3, 4], 0) };
            dashed.Freeze();
            double y = Math.Round(YOf(usual)) + 0.5;
            dc.DrawLine(dashed, new Point(1, y), new Point(ActualWidth - 1, y));
        }

        var brush = Stroke ?? ChartPaint.TextBrush;
        if (Bars)
        {
            DrawBars(dc, values, brush, floor, YOf);
            return;
        }

        var color = brush is SolidColorBrush solid ? solid.Color : ChartPaint.Res("TextColor", 0xFF);
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
        dc.DrawGeometry(ChartGeometry.FadeFill(color, 0.26), null, area);
        var pen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
        // A dot at now, ringed in the card's colour so it stands off the line.
        if (values[^1] is { } last)
        {
            var ring = new Pen(new SolidColorBrush(ChartPaint.Res("SurfaceColor", 0x0A)), 1.5);
            ring.Freeze();
            dc.DrawEllipse(brush, ring, new Point(XOf(values.Count - 1, values.Count), YOf(last)), 3, 3);
        }

        if (_hover >= 0 && _hover < values.Count)
        {
            double x = Math.Round(XOf(_hover, values.Count)) + 0.5;
            dc.DrawLine(new Pen(ChartPaint.Cursor, 1), new Point(x, 0), new Point(x, ActualHeight));
            if (values[_hover] is { } v) dc.DrawEllipse(brush, null, new Point(XOf(_hover, values.Count), YOf(v)), 2.5, 2.5);
        }
    }

    /// <summary>A bar a day; a day not recorded is a flat grey line along the foot, the bar under the pointer full and the rest a touch lighter.</summary>
    private void DrawBars(DrawingContext dc, IReadOnlyList<double?> values, Brush brush, double floor, Func<double, double> yOf)
    {
        double slot = ActualWidth / values.Count;
        double gap = Math.Min(4, slot * 0.3), width = Math.Max(1, slot - gap);
        var missing = new Pen(ChartPaint.Faint, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        missing.Freeze();
        for (int i = 0; i < values.Count; i++)
        {
            double left = i * slot + gap / 2;
            if (values[i] is not { } v)
            {
                dc.DrawLine(missing, new Point(left + 1, floor - 1), new Point(left + width - 1, floor - 1));
                continue;
            }
            if (v <= 0) continue;
            double y = Math.Min(yOf(v), floor - 2); // the least bar still shows
            if (i != _hover) dc.PushOpacity(_hover >= 0 ? 0.55 : 0.85);
            dc.DrawRoundedRectangle(brush, null, new Rect(left, y, width, floor + 1 - y), 1.5, 1.5);
            if (i != _hover) dc.Pop();
        }
    }
}

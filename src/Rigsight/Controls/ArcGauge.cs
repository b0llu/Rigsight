using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rigsight.Controls;

/// <summary>A 270° ring gauge with a glowing, animated value arc.</summary>
public sealed class ArcGauge : FrameworkElement
{
    private const double StartAngle = 135;
    private const double SweepAngle = 270;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(ArcGauge), new PropertyMetadata(null, (d, _) => ((ArcGauge)d).AnimateToValue()));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ArcGauge), new PropertyMetadata(0.0, (d, _) => ((ArcGauge)d).AnimateToValue()));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ArcGauge), new PropertyMetadata(100.0, (d, _) => ((ArcGauge)d).AnimateToValue()));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(ArcGauge), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ArcGauge), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x23, 0x2B, 0x3D)), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        "Fraction", typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    private double _target = double.NaN;

    public ArcGauge()
    {
        // A page that isn't shown keeps its gauges bound; don't let them animate (and redraw) off-screen.
        IsVisibleChanged += (_, _) => { if (!IsVisible) SetFraction(_target); };
    }

    /// <summary>
    /// Readings arrive every second, mostly unchanged: animate only a visible change the eye would notice,
    /// briefly, and only while the gauge is on screen (a running animation redraws every frame).
    /// </summary>
    private void AnimateToValue()
    {
        double target = 0;
        if (Value is double v && !double.IsNaN(v) && Maximum > Minimum)
            target = Math.Clamp((v - Minimum) / (Maximum - Minimum), 0, 1);

        bool first = double.IsNaN(_target);
        if (!first && Math.Abs(target - _target) < 0.004) return; // under ~1° of arc: nothing to see
        _target = target;

        if (!IsVisible || first)
        {
            SetFraction(target);
            return;
        }
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(FractionProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetFraction(double fraction)
    {
        if (double.IsNaN(fraction)) return;
        BeginAnimation(FractionProperty, null);
        SetValue(FractionProperty, fraction);
    }

    // Pens are rebuilt only when the colours or thickness change, not on every animation frame.
    private (Brush? Brush, Brush? Track, double Thickness) _penKey;
    private Pen? _trackPen, _tickPen, _glowPen, _valuePen;

    private void EnsurePens()
    {
        var key = (Brush, TrackBrush, Thickness);
        if (_valuePen is not null && key == _penKey) return;
        _penKey = key;
        double t = Thickness;
        _trackPen = Frozen(new Pen(TrackBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _tickPen = Frozen(new Pen(TrackBrush, 1.5));
        var glowBrush = Brush.CloneCurrentValue();
        glowBrush.Opacity = 0.18;
        _glowPen = Frozen(new Pen(glowBrush, t * 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _valuePen = Frozen(new Pen(Brush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    }

    private static Pen Frozen(Pen pen)
    {
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        double t = Thickness;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - t;
        EnsurePens();

        // Track.
        DrawArc(dc, _trackPen!, center, radius, StartAngle, SweepAngle);

        // Tick marks every 10 %, just inside the track.
        var tickPen = _tickPen!;
        double inner = radius - t * 1.1, outer = radius - t * 0.8 - 3;
        for (int i = 0; i <= 10; i++)
        {
            double a = (StartAngle + SweepAngle * i / 10) * Math.PI / 180;
            dc.DrawLine(tickPen,
                new Point(center.X + outer * Math.Cos(a), center.Y + outer * Math.Sin(a)),
                new Point(center.X + inner * Math.Cos(a), center.Y + inner * Math.Sin(a)));
        }

        double fraction = (double)GetValue(FractionProperty);
        if (fraction <= 0.002) return;

        // Soft glow underneath, then the value arc.
        DrawArc(dc, _glowPen!, center, radius, StartAngle, SweepAngle * fraction);
        DrawArc(dc, _valuePen!, center, radius, StartAngle, SweepAngle * fraction);
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double startDeg, double sweepDeg)
    {
        if (sweepDeg <= 0 || r <= 0) return;
        sweepDeg = Math.Min(sweepDeg, 359.9);

        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(p0, isFilled: false, isClosed: false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}

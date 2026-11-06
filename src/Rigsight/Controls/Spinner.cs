using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Rigsight.Controls;

/// <summary>
/// The turning ring that says the app is working on something that takes a moment (a scan, a check, a report being
/// read). Shown next to whatever was pressed; it turns only while it's on screen.
/// </summary>
/// <remarks>
/// A faint full ring with an arc going round it that grows and shrinks as it turns (2026-11-06: the user "can barely
/// see" the old 12 px three-quarter arc, and its "coverage is bad"). The track gives the eye its shape at a glance;
/// the growing arc reads as motion even from the corner of the eye (Material's ring). 16 px with a 2 px line beside a
/// button or row; 48 px with a 4 px line over a whole page that's loading (see <see cref="PageSize"/>).
/// </remarks>
public sealed class Spinner : FrameworkElement
{
    /// <summary>A whole page loading: the ring in the middle of it.</summary>
    public const double PageSize = 48, PageStroke = 4;

    /// <summary>
    /// A test copy started with RIGSIGHT_SHOW_LOADERS=1 shows every one of these all the time, to judge where they sit
    /// and how they look without having to catch each in the moment it shows.
    /// </summary>
    internal static readonly bool ShowAll = Core.RigsightPaths.IsTestInstance && Environment.GetEnvironmentVariable("RIGSIGHT_SHOW_LOADERS") == "1";

    /// <summary>One go of the arc growing round and shrinking back.</summary>
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(1.6);

    /// <summary>
    /// The goes the turning runs before it starts again: each ends a quarter turn short of where it began (the arc
    /// grows by three quarters), so four end where the first began and the start again isn't seen.
    /// </summary>
    private const int Cycles = 4;

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Spinner), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The ring's colour: the accent unless set (a warm one where it stands for something going wrong).</summary>
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Spinner), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    /// <summary>Where the turning is, in goes of the arc (0 to <see cref="Cycles"/>): drawn from in <see cref="OnRender"/>.</summary>
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(Spinner), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    static Spinner()
    {
        if (ShowAll) VisibilityProperty.OverrideMetadata(typeof(Spinner), new FrameworkPropertyMetadata(Visibility.Visible, null, (_, _) => Visibility.Visible));
    }

    public Spinner()
    {
        // Where it takes an icon's place (the two share a small grid), the icon steps aside as it would.
        if (ShowAll)
            Loaded += (_, _) =>
            {
                if (Parent is not System.Windows.Controls.Grid { Width: <= 16 } grid) return;
                foreach (UIElement other in grid.Children)
                    if (other != this) other.Visibility = Visibility.Hidden;
            };
        Width = Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(StrokeProperty, "AccentBrush");
        IsVisibleChanged += (_, _) =>
        {
            Turn(IsVisible);
            if (Late && !ShowAll) BeginAnimation(OpacityProperty, IsVisible ? ComeIn() : null);
        };
    }

    private AnimationClock? _clock;

    /// <summary>
    /// Starts or stops the turning. The clock is held so it can be taken off WPF's timeline when the ring goes: an
    /// animation that repeats for ever and is only replaced by nothing stays on it until the next garbage collection,
    /// and the window meanwhile never comes to rest (seconds of it after a ring that showed for a moment). With
    /// Windows' animations turned off it stands still, a three-quarter arc on its ring.
    /// </summary>
    private void Turn(bool on)
    {
        _clock?.Controller?.Remove();
        _clock = on && SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(0, Cycles, TimeSpan.FromTicks(Cycle.Ticks * Cycles)) { RepeatBehavior = RepeatBehavior.Forever }.CreateClock()
            : null;
        ApplyAnimationClock(PhaseProperty, _clock);
    }

    /// <summary>
    /// For a box whose content is on its way (a list being read, readings not in yet): the ring comes in after a moment,
    /// so content that arrives at once never flashes it.
    /// </summary>
    public bool Late { get; set; }

    private static DoubleAnimationUsingKeyFrames ComeIn()
    {
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
        fade.Freeze();
        return fade;
    }

    /// <summary>
    /// Where the arc runs at <paramref name="phase"/>, in turns from the top, clockwise: its head goes three quarters
    /// round in the first half of a go and its tail follows in the second, both easing in and out, while the whole
    /// turns once a go. Without animation (<paramref name="still"/>): three quarters, from the top.
    /// </summary>
    internal static (double From, double To) ArcAt(double phase, bool still = false)
    {
        if (still) return (0, 0.75);
        int go = (int)Math.Floor(phase);
        double t = phase - go;
        static double Ease(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
        double head = 0.75 * Ease(Math.Min(1, t * 2)), tail = 0.75 * Ease(Math.Max(0, t * 2 - 1));
        // Never quite nothing: a dot where the arc is about to grow from.
        if (head - tail < 0.03) head = tail + 0.03;
        double turn = phase + go * 0.75;
        return (turn + tail, turn + head);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || Stroke is not { } brush) return;
        double thick = Math.Min(StrokeThickness, size / 3), r = (size - thick) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = brush.Clone();
        track.Opacity = 0.16;
        track.Freeze();
        dc.DrawEllipse(null, new Pen(track, thick), c, r, r);

        var (from, to) = ArcAt((double)GetValue(PhaseProperty), _clock is null);
        Point At(double turns)
        {
            double a = turns * 2 * Math.PI;
            return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        }
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(At(from), isFilled: false, isClosed: false);
            g.ArcTo(At(to), new Size(r, r), 0, isLargeArc: to - from > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, new Pen(brush, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Rigsight.Controls;

/// <summary>
/// The turning arc that says the app is working on something that takes a moment (a scan, a check, a report being
/// read). Shown next to whatever was pressed; it turns only while it's on screen.
/// </summary>
public sealed class Spinner : Shape
{
    private readonly RotateTransform _turn = new();

    /// <summary>
    /// A test copy started with RIGSIGHT_SHOW_LOADERS=1 shows every one of these all the time, to judge where they sit
    /// and how they look without having to catch each in the moment it shows.
    /// </summary>
    private static readonly bool ShowAll = Core.RigsightPaths.IsTestInstance && Environment.GetEnvironmentVariable("RIGSIGHT_SHOW_LOADERS") == "1";

    static Spinner()
    {
        if (ShowAll) VisibilityProperty.OverrideMetadata(typeof(Spinner), new PropertyMetadata(Visibility.Visible, null, (_, _) => Visibility.Visible));
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
        Width = Height = 12;
        StrokeThickness = 1.5;
        StrokeStartLineCap = StrokeEndLineCap = PenLineCap.Round;
        VerticalAlignment = VerticalAlignment.Center;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _turn;
        SetResourceReference(StrokeProperty, "AccentBrush");
        IsVisibleChanged += (_, _) =>
        {
            Turn(IsVisible);
            if (Late && !ShowAll) BeginAnimation(OpacityProperty, IsVisible ? ComeIn() : null);
        };
    }

    private AnimationClock? _clock;

    /// <summary>
    /// Starts or stops the turning. The clock is held so it can be taken off WPF's timeline when the arc goes: an
    /// animation that repeats for ever and is only replaced by nothing stays on it until the next garbage collection,
    /// and the window meanwhile never comes to rest (seconds of it after an arc that showed for a moment).
    /// </summary>
    private void Turn(bool on)
    {
        _clock?.Controller?.Remove();
        _clock = on ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever }.CreateClock() : null;
        _turn.ApplyAnimationClock(RotateTransform.AngleProperty, _clock);
    }

    /// <summary>
    /// For a box whose content is on its way (a list being read, readings not in yet): the arc comes in after a moment,
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

    /// <summary>Three quarters of a circle, from the top round to the left.</summary>
    protected override Geometry DefiningGeometry
    {
        get
        {
            double size = Math.Min(double.IsNaN(Width) ? 12 : Width, double.IsNaN(Height) ? 12 : Height);
            double c = size / 2, r = Math.Max(0, c - StrokeThickness / 2);
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(new Point(c, c - r), isFilled: false, isClosed: false);
                g.ArcTo(new Point(c - r, c), new Size(r, r), 0, isLargeArc: true, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }
            arc.Freeze();
            return arc;
        }
    }
}

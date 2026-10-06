using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rigsight.Services;

namespace Rigsight.Controls;

public enum RiggyMood
{
    /// <summary>Its usual face: wide eyes and a small smile.</summary>
    Idle,
    /// <summary>Looking up while it reads the records; its yellow button glows.</summary>
    Thinking,
    /// <summary>Smiling eyes and a wide smile: it found the answer.</summary>
    Happy,
    /// <summary>One eye half shut and a crooked mouth: it didn't understand.</summary>
    Unsure,
}

/// <summary>
/// Riggy, the face of Ask: a little screen-headed robot in the colours of Rigsight's icon, with three knobs on top, big
/// eyes and a green smile on its dark screen, and a yellow button on its side. Drawn by code (its head: what fits at
/// the sizes it is shown), after the mascot art the user chose. It moves only on a moment (a blink when the mouse
/// comes over it or its mood changes, the antenna's glow while it thinks), never by itself while the window sits there.
/// </summary>
public sealed class Riggy : FrameworkElement
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(RiggyMood), typeof(Riggy), new FrameworkPropertyMetadata(RiggyMood.Idle, FrameworkPropertyMetadataOptions.AffectsRender, OnMoodChanged));

    /// <summary>How far the eyes are closed, 0 (open) to 1 (shut): animated for a blink.</summary>
    public static readonly DependencyProperty BlinkProperty = DependencyProperty.Register(
        nameof(Blink), typeof(double), typeof(Riggy), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The antenna dot's glow, 0 to 1: pulses while thinking.</summary>
    public static readonly DependencyProperty GlowProperty = DependencyProperty.Register(
        nameof(Glow), typeof(double), typeof(Riggy), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public RiggyMood Mood
    {
        get => (RiggyMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    public double Blink
    {
        get => (double)GetValue(BlinkProperty);
        set => SetValue(BlinkProperty, value);
    }

    public double Glow
    {
        get => (double)GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    private static readonly Brush Body = Frozen(new LinearGradientBrush(Color.FromRgb(0x3F, 0x8E, 0xFF), Color.FromRgb(0x3D, 0xDC, 0xA6), new Point(0, 0.2), new Point(1, 0.8)));
    private static readonly Brush Dot = Frozen(new SolidColorBrush(Color.FromRgb(0xFD, 0xBE, 0x2C)));
    private static readonly Brush Knob = Frozen(new LinearGradientBrush(Color.FromRgb(0x3F, 0x8E, 0xFF), Color.FromRgb(0x38, 0xC8, 0xE8), new Point(0, 0), new Point(1, 1)));
    private static readonly Brush Rim = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Screen = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x43)));
    private static readonly Brush Pupil = Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x2C)));
    private static readonly Brush Smile = Frozen(new SolidColorBrush(Color.FromRgb(0x3D, 0xE0, 0xA8)));
    private static readonly Brush Face = Frozen(new SolidColorBrush(Color.FromRgb(0xFB, 0xF8, 0xF0)));

    public Riggy()
    {
        // No size of its own: a size set here would outrank one given in a template (which is where the corner bubble sets its).
        MouseEnter += (_, _) => BlinkOnce();
        IsVisibleChanged += (_, _) => ApplyMood();
    }

    private static void OnMoodChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var riggy = (Riggy)d;
        riggy.ApplyMood();
        if ((RiggyMood)e.NewValue != RiggyMood.Thinking) riggy.BlinkOnce();
    }

    /// <summary>The glow runs only while it thinks and is on screen.</summary>
    private void ApplyMood()
    {
        if (Mood == RiggyMood.Thinking && IsVisible && Motion.Enabled)
            BeginAnimation(GlowProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else
            BeginAnimation(GlowProperty, null);
    }

    public void BlinkOnce()
    {
        if (!Motion.Enabled || !IsVisible) return;
        BeginAnimation(BlinkProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        // Drawn on a 100 by 100 sheet, scaled to fit.
        dc.PushTransform(new ScaleTransform(size / 100, size / 100));
        var mood = Mood;

        // Three knobs on top, then the head over their feet: a chunky screen in the icon's blue and green.
        foreach (double x in new[] { 29.0, 44.5, 60.0 }) dc.DrawRoundedRectangle(Knob, null, new Rect(x, 5, 11, 18), 4, 4);
        dc.DrawRoundedRectangle(Body, null, new Rect(7, 16, 86, 72), 22, 22);
        dc.DrawRoundedRectangle(Rim, null, new Rect(13, 22, 74, 60), 17, 17);
        dc.DrawRoundedRectangle(Screen, null, new Rect(16, 25, 68, 54), 14, 14);

        // The yellow button on its side (it glows while Riggy thinks).
        var button = new Point(89, 50);
        if (Glow > 0) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(Glow * 110), 0xFD, 0xBE, 0x2C)), null, button, 5 + Glow * 4.5, 5 + Glow * 4.5);
        dc.DrawEllipse(Dot, null, button, 5, 5);

        // Eyes: big white ovals with dark pupils and a glint. A blink squashes them; thinking sends the pupils up and aside.
        double look = mood == RiggyMood.Thinking ? 2.6 : 0.8, up = mood == RiggyMood.Thinking ? -3.4 : 0.8;
        const double eyeY = 49;
        void Eye(double x, double scale = 1)
        {
            double open = Math.Max(0.12, 1 - Blink) * scale;
            double rx = 9.6, ry = 12.4 * open;
            dc.DrawEllipse(Face, null, new Point(x, eyeY), rx, ry);
            if (open < 0.35) return;
            var pupil = new Point(x + look, eyeY + up * open);
            dc.DrawEllipse(Pupil, null, pupil, 6.1, 8.3 * open);
            dc.DrawEllipse(Face, null, new Point(pupil.X + 2.4, pupil.Y - 3.6 * open), 2.1, 2.1);
        }
        if (mood == RiggyMood.Happy && Blink < 0.2)
        {
            // Smiling eyes: two arches.
            var pen = new Pen(Face, 4.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(new Point(27.5, 52), new Point(44.5, 52), 9.5));
            dc.DrawGeometry(null, pen, Arc(new Point(55.5, 52), new Point(72.5, 52), 9.5));
        }
        else
        {
            Eye(36);
            // One eye half shut: it didn't quite get that.
            Eye(64, mood == RiggyMood.Unsure ? 0.55 : 1);
        }

        // The mouth, in the icon's green, between and just under the eyes.
        var line = new Pen(Smile, 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        switch (mood)
        {
            case RiggyMood.Happy:
                dc.DrawGeometry(null, line, Arc(new Point(42.5, 63), new Point(57.5, 63), 9, SweepDirection.Counterclockwise));
                break;
            case RiggyMood.Unsure:
                dc.DrawLine(line, new Point(45, 67.5), new Point(55, 65.5));
                break;
            case RiggyMood.Thinking:
                dc.DrawEllipse(Smile, null, new Point(50, 66.5), 2.7, 2.7);
                break;
            default:
                dc.DrawGeometry(null, line, Arc(new Point(44.5, 64), new Point(55.5, 64), 7.5, SweepDirection.Counterclockwise));
                break;
        }
        dc.Pop();
    }

    private static Pen Round(double thickness) => new(Face, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    private static Geometry Arc(Point from, Point to, double radius, SweepDirection sweep = SweepDirection.Clockwise)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(from, false, false);
            g.ArcTo(to, new Size(radius, radius), 0, false, sweep, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

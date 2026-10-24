using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A short text (one letter) drawn with its ink, not its line box, in the middle of the space it's given: the source mark's
/// letter in its circle. Each letter's ink sits differently in its box (E and the digits a pixel right, Ö and 中 over a
/// pixel high, measured 2026-10-23), so no one nudge centres them all; the ink's own bounds do.
/// </summary>
public sealed class InkCentered : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InkCentered),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(InkCentered),
        new FrameworkPropertyMetadata(9.0, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>An icon font ("Segoe Fluent Icons, Segoe MDL2 Assets") for a glyph such as the opens-outside arrow; none for a letter.</summary>
    public static readonly DependencyProperty FontProperty = DependencyProperty.Register(nameof(Font), typeof(string), typeof(InkCentered),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(InkCentered),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public string? Font { get => (string?)GetValue(FontProperty); set => SetValue(FontProperty, value); }

    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text)) return;
        var face = Font is { Length: > 0 } font ? new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal) : Bold;
        var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, FontSize, Fill, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var ink = text.BuildGeometry(new Point(0, 0));
        var bounds = ink.Bounds;
        if (bounds.IsEmpty) return;
        // The ink's middle on the element's middle (the letters are drawn as shapes, so a fallback font's, 中, centres too).
        dc.PushTransform(new TranslateTransform(ActualWidth / 2 - (bounds.Left + bounds.Width / 2), ActualHeight / 2 - (bounds.Top + bounds.Height / 2)));
        dc.DrawGeometry(Fill, null, ink);
        dc.Pop();
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// The icon in an icon-only button, drawn as its outline and placed by that outline's own middle. Drawn as text, a glyph
/// is moved to whole pixels: at a zoom other than 100% (110%: a 28 px button is 30.8 px) the ✕ landed most of a pixel
/// up and to the left (the user, 2026-11-16, "these icons are not centered"). The glyph's own TextBlock stays where it
/// was, unseen, so screen readers and lookups by its text still find it. Anything that isn't a single icon-font glyph
/// is left to the button's ContentPresenter as before.
/// </summary>
public sealed class GlyphPresenter : FrameworkElement
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(nameof(Content), typeof(object),
        typeof(GlyphPresenter), new PropertyMetadata(null, (d, _) => ((GlyphPresenter)d).Follow()));

    /// <summary>The button's content (TemplateBinding Content).</summary>
    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(ContentPresenter),
        typeof(GlyphPresenter), new PropertyMetadata(null, (d, _) => ((GlyphPresenter)d).Follow()));

    /// <summary>The button's ContentPresenter: unseen while this draws its glyph.</summary>
    public ContentPresenter? Target { get => (ContentPresenter?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    private static readonly DependencyProperty GlyphProperty = DependencyProperty.Register("Glyph", typeof(string), typeof(GlyphPresenter),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty SizeProperty = DependencyProperty.Register("Size", typeof(double), typeof(GlyphPresenter),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty FamilyProperty = DependencyProperty.Register("Family", typeof(FontFamily), typeof(GlyphPresenter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty InkProperty = DependencyProperty.Register("Ink", typeof(Brush), typeof(GlyphPresenter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty InkOpacityProperty = DependencyProperty.Register("InkOpacity", typeof(double), typeof(GlyphPresenter),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public GlyphPresenter()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Whether this is drawing the glyph (the ContentPresenter's text is unseen then).</summary>
    internal bool Drawing { get; private set; }

    /// <summary>A single character from the icon font's private range (Segoe Fluent Icons' glyphs).</summary>
    internal static bool IsGlyph(string? text) => text is { Length: 1 } && text[0] >= '' && text[0] <= '';

    private void Follow()
    {
        foreach (var dp in new[] { GlyphProperty, SizeProperty, FamilyProperty, InkProperty, InkOpacityProperty }) BindingOperations.ClearBinding(this, dp);
        Drawing = Target is not null && (Content is TextBlock tb ? IsGlyph(tb.Text) : Content is string s && IsGlyph(s));
        if (Target is { } target) target.Opacity = Drawing ? 0 : 1;
        if (!Drawing) { InvalidateVisual(); return; }
        if (Content is TextBlock text)
        {
            // What the TextBlock shows, as the button's style and triggers give it (its colour turns with a hover).
            SetBinding(GlyphProperty, new Binding(nameof(TextBlock.Text)) { Source = text });
            SetBinding(SizeProperty, new Binding(nameof(TextBlock.FontSize)) { Source = text });
            SetBinding(FamilyProperty, new Binding(nameof(TextBlock.FontFamily)) { Source = text });
            SetBinding(InkProperty, new Binding(nameof(TextBlock.Foreground)) { Source = text });
            SetBinding(InkOpacityProperty, new Binding(nameof(Opacity)) { Source = text });
        }
        else
        {
            // A glyph given as the button's text: the presenter's own font, size and colour.
            SetValue(GlyphProperty, (string)Content!);
            SetBinding(SizeProperty, new Binding { Path = new PropertyPath(TextElement.FontSizeProperty), Source = Target });
            SetBinding(FamilyProperty, new Binding { Path = new PropertyPath(TextElement.FontFamilyProperty), Source = Target });
            SetBinding(InkProperty, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = Target });
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // A glyph a binding changes later into text (or text into a glyph) goes back to the presenter.
        if (e.Property == GlyphProperty && Target is { } target && Content is TextBlock)
        {
            bool glyph = IsGlyph(e.NewValue as string);
            if (glyph != Drawing) { Drawing = glyph; target.Opacity = glyph ? 0 : 1; }
        }
    }

    protected override Size MeasureOverride(Size available) => new();

    protected override void OnRender(DrawingContext dc)
    {
        if (!Drawing || GetValue(GlyphProperty) is not string glyph || !IsGlyph(glyph) || GetValue(InkProperty) is not Brush ink) return;
        var family = (FontFamily?)GetValue(FamilyProperty) ?? new FontFamily("Segoe Fluent Icons");
        double size = (double)GetValue(SizeProperty);
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var shape = text.BuildGeometry(new Point(0, 0));
        if (shape.IsEmpty()) return;
        var b = shape.Bounds;
        dc.PushOpacity((double)GetValue(InkOpacityProperty));
        dc.PushTransform(new TranslateTransform(RenderSize.Width / 2 - (b.X + b.Width / 2), RenderSize.Height / 2 - (b.Y + b.Height / 2)));
        dc.DrawGeometry(ink, null, shape);
        dc.Pop();
        dc.Pop();
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Controls;

/// <summary>
/// One taskbar icon as RigSight draws it (the agent's RenderTrayIcon): one number filling it, or two stacked (a second
/// <see cref="Text2"/>), on a tile in its colour (none: the numbers alone); every number as tall as two digits, a wider
/// one drawn narrower first.
/// </summary>
public sealed class TaskbarIconView : FrameworkElement
{
    public static readonly DependencyProperty Text1Property = Prop(nameof(Text1), "");
    public static readonly DependencyProperty Brush1Property = Prop<Brush>(nameof(Brush1), Brushes.White);
    public static readonly DependencyProperty Text2Property = Prop(nameof(Text2), "");
    public static readonly DependencyProperty Brush2Property = Prop<Brush>(nameof(Brush2), Brushes.White);
    public static readonly DependencyProperty TileProperty = Prop<Brush?>(nameof(Tile), null);

    public string Text1 { get => (string)GetValue(Text1Property); set => SetValue(Text1Property, value); }
    public Brush Brush1 { get => (Brush)GetValue(Brush1Property); set => SetValue(Brush1Property, value); }
    public string Text2 { get => (string)GetValue(Text2Property); set => SetValue(Text2Property, value); }
    public Brush Brush2 { get => (Brush)GetValue(Brush2Property); set => SetValue(Brush2Property, value); }
    public Brush? Tile { get => (Brush?)GetValue(TileProperty); set => SetValue(TileProperty, value); }

    private static DependencyProperty Prop<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(TaskbarIconView), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private IReadOnlyList<(string Text, Brush Brush)> Lines =>
        string.IsNullOrEmpty(Text2) ? [(Text1, Brush1)] : [(Text1, Brush1), (Text2, Brush2)];

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static Geometry Outline(string text) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 100, Brushes.Black, 1).BuildGeometry(new Point());

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        var lines = Lines;
        if (size <= 0 || string.IsNullOrEmpty(Text1)) return;
        double inset = 0;
        if (Tile is { } tile)
        {
            dc.DrawRoundedRectangle(tile, null, new Rect(0, 0, size, size), size * 0.2, size * 0.2);
            inset = TrayParts.IconInset(size);
        }
        int count = lines.Count;
        double band = size / count, width = size - 2 * inset;
        var digits = Outline("88").Bounds;
        double room = band - (count > 1 ? 1 : 0), largest = Math.Min(width / digits.Width, room / digits.Height);
        for (int i = 0; i < count; i++)
        {
            var shape = Outline(lines[i].Text);
            var b = shape.Bounds;
            if (b.IsEmpty || b.Width <= 0) continue;
            double narrow = Math.Clamp(width / (b.Width * largest), 0.7, 1), scale = Math.Min(largest, width / (b.Width * narrow));
            var t = new TransformGroup();
            t.Children.Add(new TranslateTransform(-b.X, -digits.Y));
            t.Children.Add(new ScaleTransform(scale * narrow, scale));
            t.Children.Add(new TranslateTransform(inset + (width - b.Width * scale * narrow) / 2, i * band + (band - digits.Height * scale) / 2));
            shape.Transform = t;
            dc.DrawGeometry(lines[i].Brush, null, shape);
        }
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Controls;

/// <summary>
/// One taskbar reading as the strip draws it (the agent's WidgetRenderer.RenderStrip, at 100%), for the Taskbar page's
/// preview, its Look button and picker: its number over its word, right-aligned like the clock, its look under the number
/// (a sample graph of the same shape every time; a level, blocks or ring filled to <see cref="Fill"/>), and its tile.
/// </summary>
public sealed class TaskbarReadingView : FrameworkElement
{
    public static readonly DependencyProperty LookProperty = Prop(nameof(Look), TrayLook.Number, measure: true);
    public static readonly DependencyProperty TextProperty = Prop(nameof(Text), "", measure: true);
    public static readonly DependencyProperty ReserveProperty = Prop(nameof(Reserve), "", measure: true);
    public static readonly DependencyProperty LabelProperty = Prop(nameof(Label), "", measure: true);
    public static readonly DependencyProperty NumberBrushProperty = Prop<Brush>(nameof(NumberBrush), Brushes.White);
    public static readonly DependencyProperty NameBrushProperty = Prop<Brush>(nameof(NameBrush), Brushes.White);
    public static readonly DependencyProperty TileProperty = Prop<Brush?>(nameof(Tile), null, measure: true);
    public static readonly DependencyProperty FillProperty = Prop(nameof(Fill), 0.0);
    public static readonly DependencyProperty TextTopProperty = Prop(nameof(TextTop), false);
    public static readonly DependencyProperty WidthModeProperty = Prop(nameof(WidthMode), TrayWidth.Standard, measure: true);
    public static readonly DependencyProperty LightProperty = Prop(nameof(Light), false);

    public TrayLook Look { get => (TrayLook)GetValue(LookProperty); set => SetValue(LookProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>The widest the number has been ("88°"): its place is kept that wide.</summary>
    public string Reserve { get => (string)GetValue(ReserveProperty); set => SetValue(ReserveProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush NumberBrush { get => (Brush)GetValue(NumberBrushProperty); set => SetValue(NumberBrushProperty, value); }
    public Brush NameBrush { get => (Brush)GetValue(NameBrushProperty); set => SetValue(NameBrushProperty, value); }
    /// <summary>The tile behind it (null: none).</summary>
    public Brush? Tile { get => (Brush?)GetValue(TileProperty); set => SetValue(TileProperty, value); }
    /// <summary>How full it is, 0 to 1 (the level, blocks and ring).</summary>
    public double Fill { get => (double)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>The text up at the top: something on the strip draws under its number.</summary>
    public bool TextTop { get => (bool)GetValue(TextTopProperty); set => SetValue(TextTopProperty, value); }
    public TrayWidth WidthMode { get => (TrayWidth)GetValue(WidthModeProperty); set => SetValue(WidthModeProperty, value); }
    /// <summary>On a light taskbar (tracks and unlit blocks in black, not white).</summary>
    public bool Light { get => (bool)GetValue(LightProperty); set => SetValue(LightProperty, value); }

    /// <summary>The strip's height at 100%: Windows 11's taskbar.</summary>
    public const double BarHeight = 48;

    private static readonly Typeface ValueFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NameFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private const double ValueSize = 12, NameSize = 9.5, LineGap = 1.33, Ring = 26;

    /// <summary>A graph's shape for the preview: the same made-up two minutes every time (0 the bottom, 1 the top).</summary>
    private static readonly double[] Sample =
        [0.30, 0.34, 0.31, 0.42, 0.55, 0.48, 0.52, 0.71, 0.66, 0.58, 0.62, 0.80, 0.74, 0.69, 0.77, 0.92, 0.85, 0.79, 0.64, 0.70, 0.83, 0.88, 0.95, 0.90, 0.76];

    private double Pad => Tile is null ? 0 : WidthMode switch { TrayWidth.Compact => 3, TrayWidth.Roomy => 6, _ => 5 };
    private double GraphWidth => WidthMode switch { TrayWidth.Compact => 0, TrayWidth.Roomy => 57, _ => 38 };

    private FormattedText Value(string text) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, ValueFace, ValueSize, NumberBrush, 1);
    private FormattedText Word(string text) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, NameFace, NameSize, NameBrush, 1);

    /// <summary>Its number's place and what its look needs beside or under it (as the agent's StripLayout).</summary>
    private double Column()
    {
        double text = Math.Max(Value(Text).WidthIncludingTrailingWhitespace, Value(Reserve).WidthIncludingTrailingWhitespace);
        double words = Math.Max(text, Word(Label).WidthIncludingTrailingWhitespace);
        return Look switch
        {
            TrayLook.Number => words,
            TrayLook.Ring => Ring + 6 + words,
            TrayLook.Blocks => Math.Max(GraphWidth > 0 ? Math.Max(GraphWidth, 46) : 0, words),
            _ => Math.Max(GraphWidth, words),
        };
    }

    protected override Size MeasureOverride(Size available) => new(Column() + 2 * Pad, BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        double h = BarHeight, pad = Pad, column = Column(), w = column + 2 * pad;
        double tileTop = Tile is null ? 0 : 4, tileHeight = h - 2 * tileTop;
        double valueHeight = ValueSize * LineGap, nameHeight = NameSize * LineGap;
        double valueTop = TextTop ? tileTop + 1 : (h - valueHeight - nameHeight) / 2;
        double left = pad, right = pad + column;
        var ink = Light ? Colors.Black : Colors.White;
        var tile = new RectangleGeometry(new Rect(0, tileTop, w, tileHeight), 4, 4);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Tile is { } tileBrush)
        {
            dc.DrawGeometry(tileBrush, null, tile);
            dc.PushClip(tile);
        }
        var color = (NameBrush as SolidColorBrush)?.Color ?? ink;
        double top = valueTop + valueHeight, bottom = Tile is null ? h - 3 : tileTop + tileHeight;
        var box = Tile is null ? new Rect(left, top, column, Math.Max(0, bottom - top)) : new Rect(0, top, w, Math.Max(0, bottom - top));
        switch (Look)
        {
            case TrayLook.Area: Graph(dc, box, color, fill: true, baseLine: Tile is null); break;
            case TrayLook.Line: Graph(dc, box, color, fill: false, baseLine: true); break;
            case TrayLook.Bars:
            {
                double bw = 2, gap = 1, margin = 2;
                int n = Math.Max(1, (int)((box.Width - 2 * margin + gap) / (bw + gap)));
                var brush = Frozen(new SolidColorBrush(Color.FromArgb(179, color.R, color.G, color.B)));
                for (int i = 0; i < n; i++)
                {
                    double v = Sample[(int)((double)i / n * Sample.Length)], bh = Math.Max(1, v * box.Height);
                    dc.DrawRectangle(brush, null, new Rect(box.Left + margin + i * (bw + gap), box.Bottom - bh, bw, bh));
                }
                break;
            }
            case TrayLook.Level:
                dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromArgb(31, ink.R, ink.G, ink.B))), null, new Rect(box.Left, box.Bottom - 2, box.Width, 2));
                dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromArgb(82, color.R, color.G, color.B))), null, new Rect(box.Left, box.Top, box.Width * Fill, box.Height));
                dc.DrawRectangle(Frozen(new SolidColorBrush(color)), null, new Rect(box.Left, box.Bottom - 2, box.Width * Fill, 2));
                break;
            case TrayLook.Blocks:
            {
                double y = valueTop + valueHeight + nameHeight + 1.5, bh = Math.Max(3, bottom - 3 - y), gap = 1.5, bw = (column - 9 * gap) / 10;
                int lit = (int)Math.Round(Fill * 10);
                var on = Frozen(new SolidColorBrush(color));
                var off = Frozen(new SolidColorBrush(Color.FromArgb(41, ink.R, ink.G, ink.B)));
                for (int i = 0; i < 10; i++)
                    dc.DrawRoundedRectangle(i < lit ? on : off, null, new Rect(left + i * (bw + gap), y, Math.Max(0, bw), bh), Math.Min(1.5, bw / 2), Math.Min(1.5, bw / 2));
                break;
            }
            case TrayLook.Ring:
            {
                double stroke = 3.5, r = Ring / 2 - stroke / 2;
                var middle = new Point(left + Ring / 2, h / 2);
                dc.DrawEllipse(null, new Pen(Frozen(new SolidColorBrush(Color.FromArgb(36, ink.R, ink.G, ink.B))), stroke), middle, r, r);
                if (Fill > 0.005) dc.DrawGeometry(null, new Pen(Frozen(new SolidColorBrush(color)), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(middle, r, Fill));
                break;
            }
        }
        var number = Value(Text);
        dc.DrawText(number, new Point(right - number.WidthIncludingTrailingWhitespace, valueTop));
        if (Label.Length > 0)
        {
            var word = Word(Label);
            dc.DrawText(word, new Point(right - word.WidthIncludingTrailingWhitespace, valueTop + valueHeight));
        }
        if (Tile is not null) dc.Pop();
    }

    private static void Graph(DrawingContext dc, Rect box, Color color, bool fill, bool baseLine)
    {
        if (box.Height <= 0) return;
        if (baseLine) dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B))), null, new Rect(box.Left, box.Bottom - 1, box.Width, 1));
        double head = fill ? 0 : 1;
        var points = Sample.Select((v, i) => new Point(box.Left + (double)i / (Sample.Length - 1) * box.Width, box.Bottom - v * (box.Height - head))).ToList();
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, box.Bottom), true, true);
                ctx.PolyLineTo([.. points, new Point(points[^1].X, box.Bottom)], true, true);
            }
            dc.DrawGeometry(Frozen(new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B))), null, area);
        }
        dc.DrawGeometry(null, new Pen(Frozen(new SolidColorBrush(Color.FromArgb(fill ? (byte)200 : (byte)255, color.R, color.G, color.B))), fill ? 1.2 : 1.4)
            { LineJoin = PenLineJoin.Round }, line);
    }

    /// <summary>A ring's arc from the top, clockwise, <paramref name="fill"/> of the way round.</summary>
    private static Geometry Arc(Point middle, double r, double fill)
    {
        fill = Math.Clamp(fill, 0, 0.9999);
        double a = fill * 2 * Math.PI;
        var start = new Point(middle.X, middle.Y - r);
        var end = new Point(middle.X + r * Math.Sin(a), middle.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fill > 0.5, SweepDirection.Clockwise, true, true);
        }
        return g;
    }

    internal static T Frozen<T>(T b) where T : Freezable
    {
        b.Freeze();
        return b;
    }

    private static DependencyProperty Prop<T>(string name, T fallback, bool measure = false) =>
        DependencyProperty.Register(name, typeof(T), typeof(TaskbarReadingView), new FrameworkPropertyMetadata(fallback,
            measure ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender : FrameworkPropertyMetadataOptions.AffectsRender));
}

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

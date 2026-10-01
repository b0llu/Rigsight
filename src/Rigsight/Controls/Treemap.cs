using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Services;

namespace Rigsight.Controls;

/// <summary>Squarified treemap of a folder's contents: area is proportional to size. Click a folder to open it.</summary>
public sealed class Treemap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<FolderNode>), typeof(Treemap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<FolderNode>? Items { get => (IReadOnlyList<FolderNode>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    public event Action<FolderNode>? ItemActivated;

    private static readonly Brush[] Palette =
    [
        ChartPaint.Frozen(0x5B, 0x8C, 0xFF), ChartPaint.Frozen(0x3D, 0xDC, 0x97), ChartPaint.Frozen(0xB1, 0x8C, 0xFF),
        ChartPaint.Frozen(0xFB, 0xBF, 0x24), ChartPaint.Frozen(0xF4, 0x72, 0xB6), ChartPaint.Frozen(0x22, 0xD3, 0xEE),
        ChartPaint.Frozen(0xF8, 0x71, 0x71), ChartPaint.Frozen(0x7C, 0x83, 0xFD),
    ];
    private static Brush Dim => ChartPaint.Brush(ChartPaint.Res("BgColor", 0), 0.44);
    private static readonly Brush Highlight = ChartPaint.Frozen(0xFF, 0xFF, 0xFF, 0x30);
    private static readonly Brush Ink = ChartPaint.Frozen(0x0B, 0x0E, 0x14);

    private List<(Rect Rect, FolderNode Node, int Color)> _layout = [];
    private int _hover = -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        int i = _layout.FindIndex(l => l.Rect.Contains(p));
        if (i != _hover) { _hover = i; InvalidateVisual(); }
        Cursor = i >= 0 && _layout[i].Node.Children.Count > 0 ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_hover >= 0 && _hover < _layout.Count) ItemActivated?.Invoke(_layout[_hover].Node);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        var items = Grouped(Items ?? [], bounds.Width * bounds.Height);
        if (items.Count == 0 || bounds.Width < 10 || bounds.Height < 10)
        {
            _layout = [];
            return;
        }

        double total = items.Sum(i => (double)i.Size);
        double scale = bounds.Width * bounds.Height / total;
        var scaled = items.Select((n, idx) => (Area: n.Size * scale, Node: n, Color: idx % Palette.Length)).ToList();
        _layout = [];
        Squarify(scaled, bounds, _layout);

        for (int i = 0; i < _layout.Count; i++)
        {
            var (rect, node, color) = _layout[i];
            var r = new Rect(rect.X + 1.5, rect.Y + 1.5, Math.Max(0, rect.Width - 3), Math.Max(0, rect.Height - 3));
            dc.DrawRoundedRectangle(Palette[color], null, r, 6, 6);
            if (node.IsBucket) dc.DrawRoundedRectangle(Dim, null, r, 6, 6);
            if (i == _hover) dc.DrawRoundedRectangle(Highlight, null, r, 6, 6);

            Label(dc, r, node);
        }

        if (_hover >= 0 && _hover < _layout.Count)
        {
            var (rect, node, _) = _layout[_hover];
            var lines = new List<(string, Brush, bool)>
            {
                (node.Name, ChartPaint.TextBrush, true),
                ($"{Units.Bytes(node.Size)}  ·  {node.Size / total:P1}", ChartPaint.Muted, false),
            };
            if (node.Name == "Others" && node.IsBucket) lines.Add(($"{node.Children.Count} smaller items", ChartPaint.Muted, false));
            else if (node.Files > 0) lines.Add(($"{node.Files:N0} files", ChartPaint.Muted, false));
            if (node.Children.Count > 0) lines.Add(("Click to open", ChartPaint.Muted, false));
            ChartPaint.InfoBox(dc, this, lines, Mouse.GetPosition(this), bounds);
        }
    }

    /// <summary>Blocks under this share of the map are too small to read or click: they're shown together as "Others"…</summary>
    internal const double MinShare = 0.015;

    /// <summary>…and so are blocks under this many square pixels, wherever the map is small enough for that to be more.</summary>
    internal const double MinArea = 2600;

    /// <summary>
    /// A block's name and size, as much as fits: both across when it's wide enough, the name cut short ("Progr…") when
    /// it's narrow, and the name up its side when it's tall but thin. Nothing only on a block too small for a word.
    /// </summary>
    private void Label(DrawingContext dc, Rect r, FolderNode node)
    {
        dc.PushClip(new RectangleGeometry(r));
        if (r.Width >= 36 && r.Height >= 22)
        {
            dc.DrawText(Fitted(node.Name, 12, r.Width - 16, bold: true), new Point(r.X + 8, r.Y + 6));
            if (r.Height >= 38) dc.DrawText(Fitted(Units.Bytes(node.Size), 11, r.Width - 16), new Point(r.X + 8, r.Y + 22));
        }
        else if (r.Height >= 56 && r.Width >= 16)
        {
            var name = Fitted(node.Name, 12, r.Height - 16, bold: true);
            dc.PushTransform(new RotateTransform(-90, r.X, r.Bottom));
            dc.DrawText(name, new Point(r.X + 8, r.Bottom + (r.Width - name.Height) / 2));
            dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>One line of text, cut short with "…" past <paramref name="width"/>.</summary>
    private FormattedText Fitted(string text, double size, double width, bool bold = false)
    {
        var ft = ChartPaint.Format(this, text, size, Ink, bold);
        ft.MaxTextWidth = Math.Max(1, width);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        return ft;
    }

    /// <summary>
    /// What the map shows, biggest first: every item big enough to read, and the rest (two or more) as one "Others" block
    /// that opens to show them. The list view keeps every item.
    /// </summary>
    internal static List<FolderNode> Grouped(IReadOnlyList<FolderNode> nodes, double area = 0)
    {
        var items = nodes.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        double total = items.Sum(i => (double)i.Size);
        double share = area > 0 ? Math.Max(MinShare, MinArea / area) : MinShare;
        var small = items.Where(i => i.Size < total * share).ToList();
        if (small.Count < 2) return items;
        var parent = small[0].Parent;
        var others = new FolderNode
        {
            Name = "Others", Path = parent?.Path ?? small[0].Path, Parent = parent, IsBucket = true,
            Size = small.Sum(i => i.Size), Files = small.Sum(i => i.Files), Children = small,
        };
        return [.. items.Except(small), others];
    }

    private static void Squarify(List<(double Area, FolderNode Node, int Color)> items, Rect rect, List<(Rect, FolderNode, int)> output)
    {
        var row = new List<(double Area, FolderNode Node, int Color)>();
        int i = 0;
        while (i < items.Count)
        {
            double side = Math.Min(rect.Width, rect.Height);
            if (side <= 0) break;
            if (row.Count == 0 || Worst(row, side) >= Worst([.. row, items[i]], side))
            {
                row.Add(items[i]);
                i++;
            }
            else
            {
                rect = LayoutRow(row, rect, output);
                row.Clear();
            }
        }
        if (row.Count > 0) LayoutRow(row, rect, output);
    }

    private static double Worst(List<(double Area, FolderNode Node, int Color)> row, double side)
    {
        double sum = row.Sum(r => r.Area), max = row.Max(r => r.Area), min = row.Min(r => r.Area);
        return Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
    }

    private static Rect LayoutRow(List<(double Area, FolderNode Node, int Color)> row, Rect rect, List<(Rect, FolderNode, int)> output)
    {
        double sum = row.Sum(r => r.Area);
        if (rect.Width >= rect.Height)
        {
            double colW = sum / rect.Height, y = rect.Y;
            foreach (var r in row)
            {
                double h = r.Area / colW;
                output.Add((new Rect(rect.X, y, colW, h), r.Node, r.Color));
                y += h;
            }
            return new Rect(rect.X + colW, rect.Y, Math.Max(0, rect.Width - colW), rect.Height);
        }
        else
        {
            double rowH = sum / rect.Width, x = rect.X;
            foreach (var r in row)
            {
                double w = r.Area / rowH;
                output.Add((new Rect(x, rect.Y, w, rowH), r.Node, r.Color));
                x += w;
            }
            return new Rect(rect.X, rect.Y + rowH, rect.Width, Math.Max(0, rect.Height - rowH));
        }
    }
}

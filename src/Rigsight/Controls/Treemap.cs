using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Services;

namespace Rigsight.Controls;

/// <summary>
/// Squarified treemap of a folder's contents: area is proportional to size, each block coloured by the kind of files
/// taking most of it (the key under the map), and a big folder's own biggest items drawn inside it, a level deeper
/// (approved mock-up 2026-10-24). Click a block to open it; <see cref="Hot"/> lights the one pointed at in the list.
/// </summary>
public sealed class Treemap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<FolderNode>), typeof(Treemap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<FolderNode>? Items { get => (IReadOnlyList<FolderNode>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    /// <summary>The item pointed at in the list beside the map: its block is outlined.</summary>
    public static readonly DependencyProperty HotProperty = DependencyProperty.Register(
        nameof(Hot), typeof(FolderNode), typeof(Treemap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FolderNode? Hot { get => (FolderNode?)GetValue(HotProperty); set => SetValue(HotProperty, value); }

    public event Action<FolderNode>? ItemActivated;
    /// <summary>The block under the pointer changed (null when off the map), for the list to light the same row.</summary>
    public event Action<FolderNode?>? HoverChanged;

    /// <summary>
    /// A kind's colour, from the app's palette (never green, amber or red, which mean good and bad elsewhere): game data
    /// the CPU blue, video the GPU cyan, photos and music pink, programs violet, archives a muted grey, other files faint.
    /// With an accent picked, the first four turn into its shades (Accents.TakeOver) and stay four different ones; archives
    /// were sky, which the accent turns into the same colour as game data.
    /// </summary>
    internal static Brush KindBrush(FileKind kind) => (Brush)(Application.Current?.TryFindResource(kind switch
    {
        FileKind.GameData => "CpuBrush",
        FileKind.Video => "GpuBrush",
        FileKind.PhotosAndMusic => "PinkBrush",
        FileKind.Programs => "PurpleBrush",
        FileKind.Archives => "MutedBrush",
        _ => "FaintBrush",
    }) ?? Brushes.SlateGray);
    private static Brush Dim => ChartPaint.Brush(ChartPaint.Res("BgColor", 0), 0.44);
    private static Brush Inner => ChartPaint.Brush(ChartPaint.Res("BgColor", 0), 0.22);
    private static Pen HotPen => new(ChartPaint.TextBrush, 2);
    private static readonly Brush Highlight = ChartPaint.Frozen(0xFF, 0xFF, 0xFF, 0x30);
    private static readonly Brush Ink = ChartPaint.Frozen(0x0B, 0x0E, 0x14);
    private static readonly Brush LightInk = ChartPaint.Frozen(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Dark or light text, whichever reads on a block of <paramref name="kind"/>'s colour (dimmed for a bucket or an inner
    /// block): a picked accent makes some kinds deeper shades, where the dark names didn't read.
    /// </summary>
    private static Brush InkOn(FileKind kind, bool dimmed)
    {
        if (KindBrush(kind) is not SolidColorBrush { Color: var c }) return Ink;
        if (dimmed) c = Services.Accents.Mix(c, ChartPaint.Res("BgColor", 0), 0.35);
        return Converters.InkOnConverter.ReadsDark(c) ? Ink : LightInk;
    }

    private List<(Rect Rect, FolderNode Node, int Color)> _layout = [];
    private int _hover = -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        int i = _layout.FindIndex(l => l.Rect.Contains(p));
        if (i != _hover) { _hover = i; InvalidateVisual(); HoverChanged?.Invoke(i >= 0 ? _layout[i].Node : null); }
        Cursor = i >= 0 && _layout[i].Node.CanOpen ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
        HoverChanged?.Invoke(null);
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
        var scaled = items.Select(n => (Area: n.Size * scale, Node: n, Color: (int)n.Kind)).ToList();
        _layout = [];
        Squarify(scaled, bounds, _layout);

        for (int i = 0; i < _layout.Count; i++)
        {
            var (rect, node, color) = _layout[i];
            var r = new Rect(rect.X + 1.5, rect.Y + 1.5, Math.Max(0, rect.Width - 3), Math.Max(0, rect.Height - 3));
            dc.DrawRoundedRectangle(KindBrush((FileKind)color), null, r, 6, 6);
            if (node.IsBucket) dc.DrawRoundedRectangle(Dim, null, r, 6, 6);
            // A level deeper inside a big folder: its own biggest items, under its name.
            if (!node.IsBucket && node.Children.Count > 1 && r.Width >= 120 && r.Height >= 90) Nested(dc, r, node);
            if (i == _hover) dc.DrawRoundedRectangle(Highlight, null, r, 6, 6);
            if (ReferenceEquals(node, Hot)) dc.DrawRoundedRectangle(null, HotPen, r, 6, 6);

            Label(dc, r, node, InkOn((FileKind)color, node.IsBucket));
        }

        if (_hover >= 0 && _hover < _layout.Count)
        {
            var (rect, node, _) = _layout[_hover];
            var lines = new List<(string, Brush, bool)>
            {
                (node.Name, ChartPaint.TextBrush, true),
                ($"{Units.Bytes(node.Size)}  ·  {node.Size / total:P1}", ChartPaint.Muted, false),
            };
            if (node.IsBucket && node.Name == Lang.T("Others")) lines.Add((Lang.T($"{node.Children.Count} smaller items"), ChartPaint.Muted, false));
            else if (node.Files > 0) lines.Add((Lang.T($"{node.Files:N0} files"), ChartPaint.Muted, false));
            if (node.Children.Count > 0) lines.Add((Lang.T("Click to open"), ChartPaint.Muted, false));
            ChartPaint.InfoBox(dc, this, lines, Mouse.GetPosition(this), bounds);
        }
    }

    /// <summary>A folder's own biggest items inside its block, below its name, each in its kind's colour.</summary>
    private void Nested(DrawingContext dc, Rect r, FolderNode node)
    {
        var inside = new Rect(r.X + 5, r.Y + 40, Math.Max(0, r.Width - 10), Math.Max(0, r.Height - 45));
        if (inside.Width < 40 || inside.Height < 30) return;
        var kids = Grouped(node.Children, inside.Width * inside.Height * 4);
        double total = kids.Sum(k => (double)k.Size);
        if (total <= 0) return;
        double scale = inside.Width * inside.Height / total;
        var laid = new List<(Rect, FolderNode, int)>();
        Squarify([.. kids.Select(k => (k.Size * scale, k, (int)k.Kind))], inside, laid);
        foreach (var (rect, kid, color) in laid)
        {
            var kr = new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 2));
            dc.DrawRoundedRectangle(KindBrush((FileKind)color), null, kr, 4, 4);
            dc.DrawRoundedRectangle(Inner, null, kr, 4, 4);
            if (kr.Width >= 50 && kr.Height >= 18)
            {
                dc.PushClip(new RectangleGeometry(kr));
                dc.DrawText(Fitted(kid.Name, 10.5, kr.Width - 10, ink: InkOn((FileKind)color, true)), new Point(kr.X + 5, kr.Y + 3));
                dc.Pop();
            }
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
    private void Label(DrawingContext dc, Rect r, FolderNode node, Brush ink)
    {
        dc.PushClip(new RectangleGeometry(r));
        if (r.Width >= 36 && r.Height >= 22)
        {
            dc.DrawText(Fitted(node.Name, 12, r.Width - 16, bold: true, ink: ink), new Point(r.X + 8, r.Y + 6));
            if (r.Height >= 38) dc.DrawText(Fitted(Units.Bytes(node.Size), 11, r.Width - 16, ink: ink), new Point(r.X + 8, r.Y + 22));
        }
        else if (r.Height >= 56 && r.Width >= 16)
        {
            var name = Fitted(node.Name, 12, r.Height - 16, bold: true, ink: ink);
            dc.PushTransform(new RotateTransform(-90, r.X, r.Bottom));
            dc.DrawText(name, new Point(r.X + 8, r.Bottom + (r.Width - name.Height) / 2));
            dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>One line of text, cut short with "…" past <paramref name="width"/>.</summary>
    private FormattedText Fitted(string text, double size, double width, bool bold = false, Brush? ink = null)
    {
        var ft = ChartPaint.Format(this, text, size, ink ?? Ink, bold);
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
            Name = Lang.T("Others"), Path = parent?.Path ?? small[0].Path, Parent = parent, IsBucket = true,
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

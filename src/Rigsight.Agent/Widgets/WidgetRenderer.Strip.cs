using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// One reading on the taskbar strip, as drawn: "56°" in its colour, the word under it ("VRAM"), and the widest text its
/// place is kept for ("88°"), so it doesn't move the strip each time a digit comes or goes. Its look (see
/// <see cref="TrayLook"/>) with what it needs: a graph of its last minutes, or how full it is (0 to 1); its tile's colour
/// (null: no tile). <see cref="More"/>: the "+N" place for readings waiting for room.
/// </summary>
internal readonly record struct StripValue(string Text, Color Color, string Label = "", string Reserve = "", StripGraph? Graph = null, Color? LabelColor = null,
    TrayLook Look = TrayLook.Number, float Fill = 0, Color? Tile = null, bool More = false);

/// <summary>
/// A reading's graph of the last minutes, filled in behind its number and word (as taskbar-monitor draws them): its points, each across (time, 0 = the
/// oldest kept, 1 = now) and up (0 = the graph's bottom, 1 = its top; NaN where there was no reading), in its colour.
/// </summary>
internal sealed record StripGraph(IReadOnlyList<PointF> Points, Color Color);

/// <summary>A part on the taskbar strip: its readings side by side, each with its word under it in the part's colour.</summary>
internal sealed record StripCell(string Name, Color NameColor, IReadOnlyList<StripValue> Values);

/// <summary>A reading in two rows: its word ("CPU", or "C") in its colour, its number in its colour, and the widest it's been ("88°").</summary>
internal readonly record struct TwoRowLine(string Label, Color LabelColor, string Text, Color Color, string Reserve = "");

/// <summary>A column in two rows: one reading or two, stacked, on its tile (null: none).</summary>
internal sealed record TwoRowColumn(IReadOnlyList<TwoRowLine> Lines, Color? Tile);

/// <summary>
/// How the strip is laid out: on tiles or not, how much room (see <see cref="TrayWidth"/>), and on a light taskbar (the
/// ink of tracks and unlit blocks).
/// </summary>
internal readonly record struct StripLook(bool Tiles, TrayWidth Width, bool Light)
{
    /// <summary>Room inside a tile on each side, between readings of one part, and between parts (at 96 dpi).</summary>
    public (float Pad, float Inner, float Gap) Spacing => Tiles
        ? Width switch { TrayWidth.Compact => (3, 2, 6), TrayWidth.Roomy => (6, 4, 10), _ => (5, 3, 8) }
        : Width == TrayWidth.Compact ? (0, 5, 12) : (0, 7, 16);
}

/// <summary>The taskbar strip (see TaskbarStrip): readings drawn like the taskbar's clock, on nothing or on tiles.</summary>
internal static partial class WidgetRenderer
{
    /// <summary>Below this height (at 96 dpi) a word doesn't fit under the numbers: the part's name goes before them, on one line.</summary>
    internal const float StripTwoLineHeight = 36;

    /// <summary>A graph's least width on the strip (at 96 dpi): room for two minutes' shape behind its number.</summary>
    internal const float StripGraphWidth = 38;

    /// <summary>The widest a graph gets (at 96 dpi), on the widest taskbars.</summary>
    internal const float StripGraphMaxWidth = 76;

    /// <summary>
    /// A graph's width (at 96 dpi) on a taskbar <paramref name="barWidth"/> pixels wide at <paramref name="scale"/>:
    /// the least on a 1920 taskbar and narrower, wider in step with a wider one (a 2560 gets about 51, an ultrawide
    /// 3440 about 68), so a wide screen's room shows more of the two minutes.
    /// </summary>
    internal static float StripGraphWidthFor(int barWidth, float scale)
    {
        float logical = barWidth / Math.Max(scale, 0.5f);
        return Math.Clamp(StripGraphWidth * logical / 1920f, StripGraphWidth, StripGraphMaxWidth);
    }

    /// <summary>
    /// The graph's width (at 96 dpi) for a <see cref="TrayWidth"/>: Standard as <see cref="StripGraphWidthFor"/>; Compact
    /// none of its own (a graph as wide as its words); Roomy half as wide again, at most 96.
    /// </summary>
    internal static float StripGraphWidthFor(int barWidth, float scale, TrayWidth width) => width switch
    {
        TrayWidth.Compact => 0,
        TrayWidth.Roomy => Math.Min(StripGraphWidthFor(barWidth, scale) * 1.5f, 96),
        _ => StripGraphWidthFor(barWidth, scale),
    };

    /// <summary>How far from the top edge the numbers sit (at 96 dpi) on a strip with a graph.</summary>
    internal const float StripGraphTextTop = 1;

    /// <summary>A ring's size (at 96 dpi), beside its number.</summary>
    private const float StripRing = 26;

    /// <summary>How strong a graph's fill is (of 255): plainly there, the white number still clear over it.</summary>
    internal const int GraphFillAlpha = 90;

    /// <summary>A reading's look as drawn: one given a graph but no look of its own draws it filled (as before looks).</summary>
    private static TrayLook LookOf(StripValue v) => v.Look == TrayLook.Number && v.Graph is not null ? TrayLook.Area : v.Look;

    /// <summary>A reading draws something under its number besides its word (a graph, a level, blocks): the text then sits at the top.</summary>
    private static bool DrawsUnder(StripValue v) => !v.More && LookOf(v) is not (TrayLook.Number or TrayLook.Ring);

    /// <summary>
    /// The strip at <paramref name="height"/> pixels (the taskbar's) and <paramref name="scale"/> (its DPI / 96): each
    /// reading over its word, like the clock's time over its date, the part's name under its first ("CPU") and a
    /// short one under the rest ("Load"), all bold in the part's colour (the colour groups them: a quieter word only
    /// looked faded at taskbar size); on a short taskbar, "CPU 48° 7%" on one line. Each reading on a tile of its own
    /// when <paramref name="look"/> has tiles, and its look drawn under its number (never over it).
    /// <paramref name="cells"/>' right edges come back (for tooltips). Transparent but for the text and tiles, with an
    /// all-but-invisible background so clicks between the letters still land on it.
    /// </summary>
    public static Bitmap RenderStrip(IReadOnlyList<StripCell> cells, int height, float scale, out float[] edges, float graphWide = StripGraphWidth, StripLook look = default)
    {
        using var layout = new StripLayout(cells, height, scale, graphWide, look);
        edges = layout.Edges;
        var bmp = new Bitmap(layout.Width, Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using (var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(catcher, 0, 0, layout.Width, height);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        layout.Draw(g);
        return bmp;
    }

    /// <summary>The strip's width in pixels, as <see cref="RenderStrip"/> would draw it (to fit it before drawing).</summary>
    public static int MeasureStrip(IReadOnlyList<StripCell> cells, int height, float scale, float graphWide, StripLook look)
    {
        using var layout = new StripLayout(cells, height, scale, graphWide, look);
        return layout.Width;
    }

    /// <summary>Where everything on the strip goes, worked out once for measuring and for drawing.</summary>
    private sealed class StripLayout : IDisposable
    {
        private readonly IReadOnlyList<StripCell> _cells;
        private readonly int _height;
        private readonly float _scale, _pad, _gap, _inner, _tilePad, _graphWidth;
        private readonly StripLook _look;
        private readonly bool _twoLines;
        private readonly FontFamily _valueFamily = new("Segoe UI Semibold"), _nameFamily = new("Segoe UI");
        private readonly Font _valueFont, _nameFont;
        private readonly StringFormat _format = StringFormat.GenericTypographic;
        private readonly Bitmap _measuring = new(1, 1);
        private readonly Graphics _mg;
        private readonly float[][] _columns;
        private readonly float[] _widths;

        public int Width { get; }
        public float[] Edges { get; }

        public StripLayout(IReadOnlyList<StripCell> cells, int height, float scale, float graphWide, StripLook look)
        {
            _cells = cells;
            _height = height;
            _scale = scale;
            _look = look;
            _valueFont = new Font(_valueFamily, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _nameFont = new Font(_nameFamily, 9.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _mg = Graphics.FromImage(_measuring);
            _twoLines = height >= StripTwoLineHeight * scale;
            var (tilePad, inner, gap) = look.Spacing;
            _pad = 4 * scale;
            _gap = gap * scale;
            _inner = inner * scale;
            _tilePad = tilePad * scale;
            _graphWidth = graphWide * scale;
            // Each reading keeps a place as wide as the widest it's been (its number or, on two lines, its word), numbers
            // and words to its right like the clock's: a digit more or less moves nothing. One line: the name, then the numbers.
            _columns = [.. cells.Select(c => c.Values.Select(v => _twoLines ? Math.Max(Number(v), TextWidth(v.Label, _nameFont)) : Number(v)).ToArray())];
            _widths = [.. cells.Select((c, k) => _columns[k].Sum() + 2 * _tilePad * c.Values.Count + _inner * Math.Max(0, c.Values.Count - 1)
                + (_twoLines ? 0 : TextWidth(c.Name, _nameFont) + _inner))];
            Width = Math.Max(1, (int)Math.Ceiling(_pad * 2 + _widths.Sum() + _gap * Math.Max(0, cells.Count - 1)));
            Edges = new float[cells.Count];
            float x = _pad;
            for (int k = 0; k < cells.Count; k++)
            {
                x += _widths[k];
                Edges[k] = k == cells.Count - 1 ? Width : x + _gap / 2; // halfway to the next; the last, to the end
                x += _gap;
            }
        }

        private float TextWidth(string text, Font font) => text.Length == 0 ? 0 : _mg.MeasureString(text, font, PointF.Empty, _format).Width;

        /// <summary>A reading's place for its number and what its look needs beside or under it.</summary>
        private float Number(StripValue v)
        {
            float text = Math.Max(TextWidth(v.Text, _valueFont), TextWidth(v.Reserve, _valueFont));
            float words = _twoLines ? Math.Max(text, TextWidth(v.Label, _nameFont)) : text;
            return LookOf(v) switch
            {
                _ when v.More => text,
                TrayLook.Number => text,
                TrayLook.Ring when _twoLines => RingSize + 6 * _scale + words,
                TrayLook.Blocks when _twoLines => Math.Max(_graphWidth > 0 ? Math.Max(_graphWidth, 46 * _scale) : 0, text),
                _ => Math.Max(_graphWidth, text),
            };
        }

        private float RingSize => Math.Min(StripRing * _scale, _height - 14 * _scale);

        public void Draw(Graphics g)
        {
            // Lines placed as the clock's: the pair centred in the bar, or the one line.
            float valueHeight = _valueFont.GetHeight(g), nameHeight = _nameFont.GetHeight(g);
            float tileTop = _look.Tiles ? 4 * _scale : 0, tileHeight = _height - 2 * tileTop;
            float valueTop = _twoLines ? (_height - (valueHeight + nameHeight)) / 2 : (_height - valueHeight) / 2;
            // With anything drawn under the numbers, all the text up at the top (every part alike, so they stay in
            // line), as taskbar-monitor has it: the graph below and behind the words gets the most room.
            if (_twoLines && _cells.Any(c => c.Values.Any(DrawsUnder)))
                valueTop = Math.Min(valueTop, tileTop + StripGraphTextTop * _scale);
            float nameTop = _twoLines ? valueTop + valueHeight : valueTop + (valueHeight - nameHeight) / 2 + 0.5f * _scale;
            var ink = _look.Light ? Color.Black : Color.White;

            float x = _pad;
            for (int k = 0; k < _cells.Count; k++)
            {
                var cell = _cells[k];
                float vx = x;
                // One line: the part's name first, on the first reading's tile (the tile reaches back under it).
                float nameWidth = _twoLines ? 0 : TextWidth(cell.Name, _nameFont) + _inner;
                for (int i = 0; i < cell.Values.Count; i++)
                {
                    var value = cell.Values[i];
                    float column = _columns[k][i];
                    float tileX = vx, tileW = column + 2 * _tilePad + (i == 0 ? nameWidth : 0);
                    float right = tileX + tileW - _tilePad, left = right - column;
                    using var tile = RoundedRect(new RectangleF(tileX, tileTop, tileW, tileHeight), 4 * _scale);
                    var state = g.Save();
                    if (_look.Tiles && value.Tile is { } tileColor)
                    {
                        using var tileBrush = new SolidBrush(tileColor);
                        g.FillPath(tileBrush, tile);
                    }
                    if (_look.Tiles) g.SetClip(tile);
                    if (i == 0 && !_twoLines)
                    {
                        using var nameBrush = new SolidBrush(cell.NameColor);
                        g.DrawString(cell.Name, _nameFont, nameBrush, tileX + _tilePad, nameTop, _format);
                    }
                    if (!value.More) DrawLook(g, value, left, right, column, tileX, tileW, tileTop + tileHeight, valueTop + valueHeight, nameTop + nameHeight, ink);
                    using (var brush = new SolidBrush(value.Color))
                        g.DrawString(value.Text, _valueFont, brush, right - TextWidth(value.Text, _valueFont), valueTop, _format);
                    if (_twoLines && value.Label.Length > 0)
                    {
                        // Each word in its own reading's colour (its part's unless one was picked).
                        using var labelBrush = new SolidBrush(value.LabelColor ?? cell.NameColor);
                        g.DrawString(value.Label, _nameFont, labelBrush, right - TextWidth(value.Label, _nameFont), nameTop, _format);
                    }
                    g.Restore(state);
                    vx += tileW + _inner;
                }
                x += _widths[k] + _gap;
            }
        }

        /// <summary>
        /// A reading's look, under its number: a graph or a level from the number's bottom down (behind the word, never
        /// over the number), blocks under the word, a ring beside the number. On a tile it reaches the tile's edges;
        /// without one, its column, down to just above the bar's edge.
        /// </summary>
        private void DrawLook(Graphics g, StripValue v, float left, float right, float column, float tileX, float tileW, float tileBottom, float numberBottom, float wordBottom, Color ink)
        {
            var color = v.Graph?.Color ?? v.LabelColor ?? ink;
            float bottom = _look.Tiles ? tileBottom : _height - 3 * _scale;
            // One line: no room under the number, so the graph is the bar's height behind it.
            float top = _twoLines ? numberBottom : 3 * _scale;
            var box = _look.Tiles ? new RectangleF(tileX, top, tileW, bottom - top) : new RectangleF(left, top, column, bottom - top);
            var look = LookOf(v);
            if (!_twoLines && look is TrayLook.Level or TrayLook.Blocks or TrayLook.Ring) look = TrayLook.Area;
            switch (look)
            {
                case TrayLook.Area when v.Graph is { } graph:
                    DrawGraph(g, graph.Points, color, box, _scale, fill: true, baseLine: !_look.Tiles);
                    break;
                case TrayLook.Line when v.Graph is { } graph:
                    DrawGraph(g, graph.Points, color, box, _scale, fill: false, baseLine: true);
                    break;
                case TrayLook.Bars when v.Graph is { } graph:
                    DrawBars(g, graph.Points, color, box, _scale);
                    break;
                case TrayLook.Level:
                {
                    using (var track = new SolidBrush(Color.FromArgb(31, ink))) g.FillRectangle(track, box.Left, box.Bottom - 2 * _scale, box.Width, 2 * _scale);
                    using (var band = new SolidBrush(Color.FromArgb(82, color))) g.FillRectangle(band, box.Left, box.Top, box.Width * v.Fill, box.Height);
                    using var edge = new SolidBrush(color);
                    g.FillRectangle(edge, box.Left, box.Bottom - 2 * _scale, box.Width * v.Fill, 2 * _scale);
                    break;
                }
                case TrayLook.Blocks:
                {
                    // Ten blocks under the word, lit up to how full it is.
                    float y = wordBottom + 1.5f * _scale, h = Math.Max(3 * _scale, bottom - 3 * _scale - y);
                    float gap = 1.5f * _scale, w = (column - 9 * gap) / 10;
                    int lit = (int)Math.Round(v.Fill * 10);
                    using var on = new SolidBrush(color);
                    using var off = new SolidBrush(Color.FromArgb(41, ink));
                    for (int b = 0; b < 10; b++)
                    {
                        using var block = RoundedRect(new RectangleF(left + b * (w + gap), y, w, h), Math.Min(1.5f * _scale, w / 2));
                        g.FillPath(b < lit ? on : off, block);
                    }
                    break;
                }
                case TrayLook.Ring:
                {
                    float d = RingSize, stroke = 3.5f * _scale, r = d / 2 - stroke / 2;
                    var middle = new PointF(left + d / 2, _height / 2f);
                    var ring = new RectangleF(middle.X - r, middle.Y - r, 2 * r, 2 * r);
                    using (var track = new Pen(Color.FromArgb(36, ink), stroke)) g.DrawEllipse(track, ring);
                    if (v.Fill > 0.005f)
                    {
                        using var arc = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawArc(arc, ring, -90, 360 * v.Fill);
                    }
                    break;
                }
            }
        }

        public void Dispose()
        {
            _valueFont.Dispose();
            _nameFont.Dispose();
            _valueFamily.Dispose();
            _nameFamily.Dispose();
            _mg.Dispose();
            _measuring.Dispose();
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// A reading's graph in <paramref name="box"/>, behind its number: the room under its line filled (for a filled graph),
    /// light enough for the number to read over it, the line on top, a faint base line so an empty or flat graph still
    /// shows where it is. A gap in the readings is a gap in the graph.
    /// </summary>
    private static void DrawGraph(Graphics g, IReadOnlyList<PointF> points, Color color, RectangleF box, float scale, bool fill, bool baseLine)
    {
        if (baseLine)
            using (var basePen = new Pen(Color.FromArgb(70, color), Math.Max(1, scale)))
                g.DrawLine(basePen, box.Left, box.Bottom, box.Right, box.Bottom);
        var run = new List<PointF>();
        using var pen = new Pen(Color.FromArgb(fill ? 200 : 255, color), (fill ? 1.2f : 1.4f) * scale) { LineJoin = LineJoin.Round };
        using var area = new SolidBrush(Color.FromArgb(GraphFillAlpha, color));
        void Flush()
        {
            if (run.Count >= 2)
            {
                if (fill) g.FillPolygon(area, [.. run, new(run[^1].X, box.Bottom), new(run[0].X, box.Bottom)]);
                g.DrawLines(pen, [.. run]);
            }
            else if (run.Count == 1)
            {
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, run[0].X - scale, run[0].Y - scale, 2 * scale, 2 * scale);
            }
            run.Clear();
        }
        // On a tile the line keeps a pixel from its top edge, so a peak isn't cut by the tile's corner.
        float head = fill ? 0 : scale;
        foreach (var p in points)
        {
            if (!float.IsFinite(p.Y)) { Flush(); continue; }
            run.Add(new PointF(box.Left + Math.Clamp(p.X, 0, 1) * box.Width, box.Bottom - Math.Clamp(p.Y, 0, 1) * (box.Height - head)));
        }
        Flush();
    }

    /// <summary>
    /// The last minutes as thin bars across <paramref name="box"/>: two pixels wide, a pixel apart (at 96 dpi), each the
    /// highest reading of its few seconds; none where there was no reading.
    /// </summary>
    private static void DrawBars(Graphics g, IReadOnlyList<PointF> points, Color color, RectangleF box, float scale)
    {
        float width = 2 * scale, gap = scale, margin = 2 * scale;
        int count = Math.Max(1, (int)((box.Width - 2 * margin + gap) / (width + gap)));
        var highest = new float[count];
        Array.Fill(highest, float.NaN);
        foreach (var p in points)
        {
            if (!float.IsFinite(p.Y)) continue;
            int i = Math.Clamp((int)(Math.Clamp(p.X, 0, 1) * count), 0, count - 1);
            highest[i] = float.IsNaN(highest[i]) ? p.Y : Math.Max(highest[i], p.Y);
        }
        using var brush = new SolidBrush(Color.FromArgb(179, color));
        for (int i = 0; i < count; i++)
        {
            if (float.IsNaN(highest[i])) continue;
            float h = Math.Max(scale, Math.Clamp(highest[i], 0, 1) * box.Height);
            g.FillRectangle(brush, box.Left + margin + i * (width + gap), box.Bottom - h, width, h);
        }
    }

    /// <summary>
    /// The readings in two rows (Pulsar's text mode): <paramref name="columns"/> side by side, each one or two readings
    /// stacked, the word on the left in its colour and the number on the right (its place kept as wide as it has been),
    /// on a tile per column when <paramref name="look"/> has tiles. No wider than the strip's numbers alone. Each column's right
    /// edge comes back (for tooltips).
    /// </summary>
    public static Bitmap RenderTwoRows(IReadOnlyList<TwoRowColumn> columns, int height, float scale, out float[] edges, StripLook look)
    {
        using var layout = new TwoRowLayout(columns, height, scale, look);
        edges = layout.Edges;
        var bmp = new Bitmap(layout.Width, Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using (var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(catcher, 0, 0, layout.Width, height);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        layout.Draw(g);
        return bmp;
    }

    /// <summary>The two rows' width in pixels, as <see cref="RenderTwoRows"/> would draw them.</summary>
    public static int MeasureTwoRows(IReadOnlyList<TwoRowColumn> columns, int height, float scale, StripLook look)
    {
        using var layout = new TwoRowLayout(columns, height, scale, look);
        return layout.Width;
    }

    private sealed class TwoRowLayout : IDisposable
    {
        private readonly IReadOnlyList<TwoRowColumn> _columns;
        private readonly int _height;
        private readonly float _scale, _pad, _gap, _tilePad, _mid;
        private readonly bool _tiles;
        private readonly FontFamily _valueFamily = new("Segoe UI Semibold"), _nameFamily = new("Segoe UI");
        private readonly Font _valueFont, _nameFont;
        private readonly StringFormat _format = StringFormat.GenericTypographic;
        private readonly Bitmap _measuring = new(1, 1);
        private readonly Graphics _mg;
        private readonly (float Words, float Numbers, float Width)[] _sizes;

        public int Width { get; }
        public float[] Edges { get; }

        public TwoRowLayout(IReadOnlyList<TwoRowColumn> columns, int height, float scale, StripLook look)
        {
            _columns = columns;
            _height = height;
            _scale = scale;
            _tiles = look.Tiles;
            // Two lines of 11.5 px numbers fit a 40 px tile; the words a little smaller, as on the strip.
            _valueFont = new Font(_valueFamily, 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _nameFont = new Font(_nameFamily, 9.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _mg = Graphics.FromImage(_measuring);
            _pad = 4 * scale;
            _tilePad = look.Tiles ? look.Spacing.Pad * scale : 0;
            _gap = (look.Tiles ? 4 : 12) * scale;
            _mid = 5 * scale;
            _sizes = [.. columns.Select(c =>
            {
                float words = c.Lines.Max(l => TextWidth(l.Label, _nameFont));
                float numbers = c.Lines.Max(l => Math.Max(TextWidth(l.Text, _valueFont), TextWidth(l.Reserve, _valueFont)));
                return (words, numbers, words + (words > 0 && numbers > 0 ? _mid : 0) + numbers + 2 * _tilePad);
            })];
            Width = Math.Max(1, (int)Math.Ceiling(_pad * 2 + _sizes.Sum(s => s.Width) + _gap * Math.Max(0, columns.Count - 1)));
            Edges = new float[columns.Count];
            float x = _pad;
            for (int k = 0; k < columns.Count; k++)
            {
                x += _sizes[k].Width;
                Edges[k] = k == columns.Count - 1 ? Width : x + _gap / 2;
                x += _gap;
            }
        }

        private float TextWidth(string text, Font font) => text.Length == 0 ? 0 : _mg.MeasureString(text, font, PointF.Empty, _format).Width;

        public void Draw(Graphics g)
        {
            float tileTop = _tiles ? 4 * _scale : 0, tileHeight = _height - 2 * tileTop;
            // The two lines in the middle of the bar, each number's and word's bottoms on one line.
            float line = Math.Min(15 * _scale, (_height - 4 * _scale) / 2), top = (_height - 2 * line) / 2;
            float valueHeight = _valueFont.GetHeight(g);
            float x = _pad;
            for (int k = 0; k < _columns.Count; k++)
            {
                var column = _columns[k];
                float width = _sizes[k].Width;
                if (_tiles && column.Tile is { } tile)
                {
                    using var brush = new SolidBrush(tile);
                    using var shape = RoundedRect(new RectangleF(x, tileTop, width, tileHeight), 4 * _scale);
                    g.FillPath(brush, shape);
                }
                for (int i = 0; i < column.Lines.Count; i++)
                {
                    var l = column.Lines[i];
                    float valueTop = top + i * line + (line - valueHeight) / 2;
                    // The word's baseline on the number's: lower by the difference in their ascents.
                    float nameTop = valueTop + (_valueFont.Size - _nameFont.Size) * 1.079f;
                    using (var nameBrush = new SolidBrush(l.LabelColor)) g.DrawString(l.Label, _nameFont, nameBrush, x + _tilePad, nameTop, _format);
                    using var brush = new SolidBrush(l.Color);
                    g.DrawString(l.Text, _valueFont, brush, x + width - _tilePad - TextWidth(l.Text, _valueFont), valueTop, _format);
                }
                x += width + _gap;
            }
        }

        public void Dispose()
        {
            _valueFont.Dispose();
            _nameFont.Dispose();
            _valueFamily.Dispose();
            _nameFamily.Dispose();
            _mg.Dispose();
            _measuring.Dispose();
        }
    }

    /// <summary>
    /// The icon styles drawn by RigSight itself (on the taskbar's left, where Windows puts no tray icons): each icon as
    /// the notification area would show it (<see cref="RenderTrayIcon"/>, <paramref name="iconSize"/> pixels), side by
    /// side <paramref name="slot"/> apart, in the middle of the bar's height. Each icon's right edge comes back (for tooltips).
    /// </summary>
    public static Bitmap RenderIconRow(IReadOnlyList<(IReadOnlyList<(string Text, Color Color)> Lines, Color? Tile)> icons, int height, int iconSize, int slot, out float[] edges)
    {
        int width = Math.Max(1, icons.Count * slot);
        var bmp = new Bitmap(width, Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using (var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(catcher, 0, 0, width, height);
        edges = new float[icons.Count];
        for (int i = 0; i < icons.Count; i++)
        {
            using var icon = RenderTrayIcon(icons[i].Lines, iconSize, tile: icons[i].Tile);
            g.DrawImage(icon, i * slot + (slot - iconSize) / 2, (height - iconSize) / 2, iconSize, iconSize);
            edges[i] = (i + 1) * slot;
        }
        return bmp;
    }
}

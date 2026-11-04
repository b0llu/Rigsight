using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Rigsight.Core;
using Rigsight.Core.Settings;

#if RIGSIGHT_APP
// Linked into the app for the Taskbar page's preview, in a namespace of its own there (the tests see both assemblies).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

/// <summary>
/// One reading on the taskbar strip, as drawn: "56°" in its colour, the word under it ("VRAM"), and the widest text its
/// place is kept for ("88°"), so it doesn't move the strip each time a digit comes or goes. Its look (see
/// <see cref="TrayLook"/>) with what it needs: a graph of its last minutes, or how full it is (0 to 1); its tile's colour
/// (null: no tile). <see cref="More"/>: the "+N" place for readings waiting for room.
/// </summary>
/// <remarks>Since 1.3: <see cref="Second"/>, the upload speed beside a download (Down and up), with its graph
/// (<see cref="Graph2"/>, the mirrored graph's lower half); <see cref="Cores"/>, each core's load 0 to 1 (Each core).</remarks>
internal readonly record struct StripValue(string Text, Color Color, string Label = "", string Reserve = "", StripGraph? Graph = null, Color? LabelColor = null,
    TrayLook Look = TrayLook.Number, float Fill = 0, Color? Tile = null, bool More = false, string Second = "", string SecondReserve = "",
    StripGraph? Graph2 = null, float[]? Cores = null);

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

    /// <summary>From a line's top to its baseline, as DrawString places it with the typographic format.</summary>
    private static float Ascent(Font font) =>
        font.Size * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);

    /// <summary>How tall the digits stand (Segoe UI's are seven tenths of the font's size): what is centred, not the line.</summary>
    private static float DigitHeight(Font font) => font.Size * 0.7f;

    /// <summary>A reading's look as drawn: one given a graph but no look of its own draws it filled (as before looks).</summary>
    private static TrayLook LookOf(StripValue v) => v.Look == TrayLook.Number && v.Graph is not null ? TrayLook.Area : v.Look;

    /// <summary>The most cores Each core draws (more would be too narrow to tell apart).</summary>
    private const int MaxCores = 16;

    /// <summary>
    /// The strip at <paramref name="height"/> pixels (the taskbar's) and <paramref name="scale"/> (its DPI / 96): each
    /// reading over its word, like the clock's time over its date, the part's name under its first ("CPU") and a
    /// short one under the rest ("Load"), all bold in the part's colour (the colour groups them: a quieter word only
    /// looked faded at taskbar size); on a short taskbar, "CPU 48° 7%" on one line. Each reading on a tile of its own
    /// when <paramref name="look"/> has tiles, and its look drawn under its number (never over it).
    /// <paramref name="cells"/>' right edges come back (for tooltips). Transparent but for the text and tiles, with an
    /// all-but-invisible background so clicks between the letters still land on it.
    /// </summary>
    public static Bitmap RenderStrip(IReadOnlyList<StripCell> cells, int height, float scale, out float[] edges, float graphWide = StripGraphWidth, StripLook look = default) =>
        RenderStrip(cells, height, scale, out edges, out _, graphWide, look);

    /// <summary>As above, with each reading's right edge within its part too (<paramref name="valueEdges"/>), for clicks.</summary>
    public static Bitmap RenderStrip(IReadOnlyList<StripCell> cells, int height, float scale, out float[] edges, out float[][] valueEdges, float graphWide = StripGraphWidth,
        StripLook look = default)
    {
        using var layout = new StripLayout(cells, height, scale, graphWide, look);
        edges = layout.Edges;
        valueEdges = layout.ValueEdges;
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

    /// <summary>
    /// Where everything on the strip goes, worked out once for measuring and for drawing. Since 1.3 nothing is drawn on
    /// top of the text: each reading is its text (its number over its word, or on one line its word then its number),
    /// with its look beside it (a ring, a pie, cores or a level meter on its left; a graph on its right) or under it
    /// (blocks), and its tile is as wide as what it holds. Text is left-aligned in a place kept as wide as the widest it
    /// has been, so a digit more or less moves nothing.
    /// </summary>
    private sealed class StripLayout : IDisposable
    {
        private readonly IReadOnlyList<StripCell> _cells;
        private readonly int _height;
        private readonly float _scale, _pad, _gap, _inner, _tilePad, _graphWidth;
        private readonly StripLook _look;
        private readonly bool _twoLines;
        private readonly FontFamily _valueFamily = new("Segoe UI Semibold"), _nameFamily = new("Segoe UI");
        private readonly Font _valueFont, _nameFont, _bigFont;
        private readonly StringFormat _format = StringFormat.GenericTypographic;
        private readonly Bitmap _measuring = new(1, 1);
        private readonly Graphics _mg;
        private readonly Room[][] _rooms;
        private readonly float[] _widths;

        /// <summary>A reading's room: its look on the left, its text, its look on the right (0: none), and the gap beside a look.</summary>
        private readonly record struct Room(float Left, float Text, float Right, float Gap)
        {
            public float Width => Left + (Left > 0 ? Gap : 0) + Text + (Right > 0 ? Gap : 0) + Right;
        }

        public int Width { get; }
        public float[] Edges { get; }

        /// <summary>Each reading's right edge within its part (the last: the part's own edge, see <see cref="Edges"/>).</summary>
        public float[][] ValueEdges { get; }

        public StripLayout(IReadOnlyList<StripCell> cells, int height, float scale, float graphWide, StripLook look)
        {
            _cells = cells;
            _height = height;
            _scale = scale;
            _look = look;
            _valueFont = new Font(_valueFamily, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _nameFont = new Font(_nameFamily, 9.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            // Big number: a third bigger on a usual taskbar; on a short one as big as still leaves room above and below.
            _bigFont = new Font(_valueFamily, Math.Min(16f * scale, Math.Max(12f * scale, (height - 10 * scale) * 0.75f)), FontStyle.Regular, GraphicsUnit.Pixel);
            _mg = Graphics.FromImage(_measuring);
            _twoLines = height >= StripTwoLineHeight * scale;
            var (tilePad, inner, gap) = look.Spacing;
            _pad = 4 * scale;
            _gap = gap * scale;
            _inner = inner * scale;
            _tilePad = tilePad * scale;
            // A graph keeps a width of its own beside the text (Compact: a narrow one, never none).
            _graphWidth = Math.Max(graphWide, 24) * scale;
            _rooms = [.. cells.Select(c => c.Values.Select(RoomOf).ToArray())];
            _widths = [.. cells.Select((c, k) => _rooms[k].Sum(r => r.Width) + 2 * _tilePad * c.Values.Count + _inner * Math.Max(0, c.Values.Count - 1))];
            Width = Math.Max(1, (int)Math.Ceiling(_pad * 2 + _widths.Sum() + _gap * Math.Max(0, cells.Count - 1)));
            Edges = new float[cells.Count];
            ValueEdges = new float[cells.Count][];
            float x = _pad;
            for (int k = 0; k < cells.Count; k++)
            {
                // Each reading's right edge too (halfway to the next of its part): a click opens the card of the one under it.
                ValueEdges[k] = new float[cells[k].Values.Count];
                float vx = x;
                for (int i = 0; i < cells[k].Values.Count; i++)
                {
                    vx += _rooms[k][i].Width + 2 * _tilePad;
                    ValueEdges[k][i] = i == cells[k].Values.Count - 1 ? float.MaxValue : vx + _inner / 2;
                    vx += _inner;
                }
                x += _widths[k];
                Edges[k] = k == cells.Count - 1 ? Width : x + _gap / 2; // halfway to the next; the last, to the end
                x += _gap;
            }
        }

        private float TextWidth(string text, Font font) => text.Length == 0 ? 0 : _mg.MeasureString(text, font, PointF.Empty, _format).Width;

        /// <summary>A text's place: as wide as it is now or as the widest it's been (<paramref name="reserve"/>).</summary>
        private float Slot(string text, string reserve, Font font) => Math.Max(TextWidth(text, font), TextWidth(reserve, font));

        /// <summary>The room between a look and the text, and between a word and its number on one line.</summary>
        private float LookGap => (_twoLines ? 6 : 5) * _scale;

        private float RingSize => Math.Min(StripRing * _scale, _height - 14 * _scale);

        /// <summary>A level meter's width, beside the text.</summary>
        private float MeterWidth => 5 * _scale;

        /// <summary>An arrow before a speed (Down and up), and the gap after it.</summary>
        private float ArrowWidth => 7 * _scale;

        /// <summary>Each core's thin bar and the gaps between them (narrower bars when there are many).</summary>
        private (float Bar, float Gap) CoreBar(StripValue v) => (v.Cores?.Length ?? 8) > 8 ? (2 * _scale, 1 * _scale) : (3 * _scale, 1.5f * _scale);

        private float CoresWidth(StripValue v)
        {
            int n = Math.Clamp(v.Cores?.Length ?? 8, 1, MaxCores);
            var (bar, gap) = CoreBar(v);
            return n * bar + (n - 1) * gap;
        }

        /// <summary>The look as drawn: Number and spark is a line graph beside the number now.</summary>
        private static TrayLook Shown(StripValue v) => v.More ? TrayLook.Number : LookOf(v) == TrayLook.Spark ? TrayLook.Line : LookOf(v);

        /// <summary>A reading's text: two lines, its number over its word; one line, its word then its number.</summary>
        private float TextOf(StripValue v)
        {
            var look = Shown(v);
            if (look == TrayLook.Big) return Slot(v.Text, v.Reserve, _bigFont);
            float number = Slot(v.Text, v.Reserve, _valueFont);
            if (look == TrayLook.DownUp)
            {
                float second = Slot(v.Second, v.SecondReserve, _valueFont), arrow = ArrowWidth + 4 * _scale;
                return _twoLines ? arrow + Math.Max(number, second) : arrow + number + LookGap + arrow + second;
            }
            float word = TextWidth(v.Label, _nameFont);
            float text = _twoLines ? Math.Max(number, word) : (word > 0 ? word + LookGap : 0) + number;
            // Ten blocks need room to be told apart: under a short number ("7%") the place is a little wider.
            return look == TrayLook.Blocks ? Math.Max(text, 48 * _scale) : text;
        }

        private Room RoomOf(StripValue v)
        {
            var look = Shown(v);
            float left = look switch
            {
                TrayLook.Ring or TrayLook.Half or TrayLook.Pie => RingSize,
                TrayLook.Cores => CoresWidth(v),
                TrayLook.Level => MeterWidth,
                _ => 0,
            };
            float right = look is TrayLook.Area or TrayLook.Line or TrayLook.Bars or TrayLook.Mirror ? _graphWidth : 0;
            return new Room(left, TextOf(v), right, LookGap);
        }

        /// <summary>Blocks under the text: 6 px tall at 100% (4 on a 40 px taskbar), a little apart from it.</summary>
        private float BlocksHeight => (_height >= 44 * _scale ? 5 : 4) * _scale;
        private float BlocksGap => 3 * _scale;

        public void Draw(Graphics g)
        {
            float tileTop = _look.Tiles ? 4 * _scale : 0, tileHeight = _height - 2 * tileTop, middle = tileTop + tileHeight / 2;
            var ink = _look.Light ? Color.Black : Color.White;
            float valueHeight = _valueFont.GetHeight(g);
            float x = _pad;
            for (int k = 0; k < _cells.Count; k++)
            {
                var cell = _cells[k];
                float vx = x;
                for (int i = 0; i < cell.Values.Count; i++)
                {
                    var value = cell.Values[i];
                    var room = _rooms[k][i];
                    float tileX = vx, tileW = room.Width + 2 * _tilePad;
                    float left = tileX + _tilePad;
                    using var tile = RoundedRect(new RectangleF(tileX, tileTop, tileW, tileHeight), 4 * _scale);
                    var state = g.Save();
                    if (_look.Tiles && value.Tile is { } tileColor)
                    {
                        using var tileBrush = new SolidBrush(tileColor);
                        g.FillPath(tileBrush, tile);
                        g.SetClip(tile);
                    }
                    var look = Shown(value);
                    var word = value.LabelColor ?? cell.NameColor;
                    var color = value.Graph?.Color ?? word;
                    float textX = left + (room.Left > 0 ? room.Left + room.Gap : 0);
                    // The look's own box: the bar's height less a little at the top and bottom.
                    float lookTop = tileTop + 4 * _scale, lookBottom = tileTop + tileHeight - 4 * _scale;

                    // Its look on the left.
                    if (look is TrayLook.Ring or TrayLook.Half or TrayLook.Pie)
                        DrawFillUp(g, look, new PointF(left + RingSize / 2, middle), RingSize, value.Fill, color, ink);
                    else if (look == TrayLook.Cores)
                    {
                        float h = Math.Min(_twoLines ? 26 * _scale : 16 * _scale, lookBottom - lookTop);
                        DrawCores(g, value, new RectangleF(left, middle - h / 2, CoresWidth(value), h), color, ink);
                    }
                    else if (look == TrayLook.Level)
                        DrawMeter(g, new RectangleF(left, lookTop, MeterWidth, lookBottom - lookTop), value.Fill, color, ink);

                    // Its text, left-aligned, in the middle of the bar (with blocks under it, the two together).
                    bool blocks = look == TrayLook.Blocks;
                    float under = blocks ? BlocksGap + BlocksHeight : 0;
                    if (look == TrayLook.Big)
                    {
                        float big = DigitHeight(_bigFont);
                        Text(g, value.Text, _bigFont, value.Color, textX, middle + big / 2 - Ascent(_bigFont));
                    }
                    else if (look == TrayLook.DownUp)
                        DrawDownUp(g, value, textX, middle, word);
                    else if (_twoLines)
                    {
                        // Digit tops to the word's baseline is what's centred (the font's empty line above isn't).
                        float pair = DigitHeight(_valueFont) + (valueHeight - Ascent(_valueFont)) + Ascent(_nameFont);
                        float digitTop = middle - (pair + under) / 2;
                        float valueTop = digitTop - (Ascent(_valueFont) - DigitHeight(_valueFont));
                        Text(g, value.Text, _valueFont, value.Color, textX, valueTop);
                        if (value.Label.Length > 0) Text(g, value.Label, _nameFont, word, textX, valueTop + valueHeight);
                        if (blocks) DrawBlocks(g, new RectangleF(textX, digitTop + pair + BlocksGap, room.Text, BlocksHeight), value.Fill, color, ink);
                    }
                    else
                    {
                        float digits = DigitHeight(_valueFont);
                        float baseline = middle - (digits + under) / 2 + digits;
                        float numberX = textX;
                        if (value.Label.Length > 0)
                        {
                            Text(g, value.Label, _nameFont, word, textX, baseline - Ascent(_nameFont));
                            numberX = textX + TextWidth(value.Label, _nameFont) + LookGap;
                        }
                        Text(g, value.Text, _valueFont, value.Color, numberX, baseline - Ascent(_valueFont));
                        if (blocks) DrawBlocks(g, new RectangleF(textX, baseline + BlocksGap, room.Text, BlocksHeight), value.Fill, color, ink);
                    }

                    // Its look on the right: a graph of the last two minutes, in a box of its own.
                    if (room.Right > 0)
                    {
                        var box = new RectangleF(textX + room.Text + room.Gap, lookTop, room.Right, lookBottom - lookTop);
                        if (look == TrayLook.Mirror) DrawMirror(g, value, box, color);
                        else if (value.Graph is { } graph)
                        {
                            if (look == TrayLook.Bars) DrawBars(g, graph.Points, color, box, _scale);
                            else DrawGraph(g, graph.Points, color, box, _scale, fill: look == TrayLook.Area, baseLine: true);
                        }
                    }
                    g.Restore(state);
                    vx += tileW + _inner;
                }
                x += _widths[k] + _gap;
            }
        }

        private void Text(Graphics g, string text, Font font, Color color, float x, float y)
        {
            if (text.Length == 0) return;
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, x, y, _format);
        }

        /// <summary>
        /// Both speeds: two lines, download over upload; one line, "▼ 12 MB/s ▲ 0.3 MB/s". Each speed after a solid arrow
        /// in the word's colour (the font's arrows are a hairline at taskbar size).
        /// </summary>
        private void DrawDownUp(Graphics g, StripValue v, float x, float middle, Color word)
        {
            float digits = DigitHeight(_valueFont), arrow = ArrowWidth + 4 * _scale;
            if (_twoLines)
            {
                float between = 6 * _scale;
                float first = middle - (2 * digits + between) / 2 + digits, second = first + digits + between;
                DrawArrow(g, x, first, down: true, word);
                DrawArrow(g, x, second, down: false, word);
                Text(g, v.Text, _valueFont, v.Color, x + arrow, first - Ascent(_valueFont));
                Text(g, v.Second, _valueFont, v.Color, x + arrow, second - Ascent(_valueFont));
                return;
            }
            float baseline = middle + digits / 2;
            DrawArrow(g, x, baseline, down: true, word);
            Text(g, v.Text, _valueFont, v.Color, x + arrow, baseline - Ascent(_valueFont));
            float upX = x + arrow + Slot(v.Text, v.Reserve, _valueFont) + LookGap;
            DrawArrow(g, upX, baseline, down: false, word);
            Text(g, v.Second, _valueFont, v.Color, upX + arrow, baseline - Ascent(_valueFont));
        }

        /// <summary>A solid arrow on a speed's digits (<paramref name="baseline"/>): down for download, up for upload.</summary>
        private void DrawArrow(Graphics g, float x, float baseline, bool down, Color color)
        {
            float digits = DigitHeight(_valueFont), w = 6 * _scale, h = Math.Min(digits, 6 * _scale);
            float mid = baseline - digits / 2, top = mid - h / 2, bottom = mid + h / 2;
            PointF[] points = down ? [new(x, top), new(x + w, top), new(x + w / 2, bottom)] : [new(x, bottom), new(x + w, bottom), new(x + w / 2, top)];
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, points);
        }

        /// <summary>The mirrored graph in its box: download above the middle, upload under it, each on its own scale.</summary>
        private void DrawMirror(Graphics g, StripValue v, RectangleF box, Color color)
        {
            float mid = box.Top + box.Height / 2;
            var upper = new RectangleF(box.Left, box.Top, box.Width, mid - box.Top);
            if (v.Graph is { } down) DrawGraph(g, down.Points, color, upper, _scale, fill: true, baseLine: false);
            if (v.Graph2 is { } up)
            {
                var state = g.Save();
                g.TranslateTransform(0, 2 * mid);
                g.ScaleTransform(1, -1);
                DrawGraph(g, up.Points, Color.FromArgb(200, color), upper, _scale, fill: true, baseLine: false);
                g.Restore(state);
            }
            using var line = new Pen(Color.FromArgb(90, color), Math.Max(1, _scale));
            g.DrawLine(line, box.Left, mid, box.Right, mid);
        }

        /// <summary>A level meter: an upright bar beside the text, filled from the bottom to how full the reading is.</summary>
        private void DrawMeter(Graphics g, RectangleF box, float fill, Color color, Color ink)
        {
            float r = Math.Min(2 * _scale, box.Width / 2);
            using (var track = RoundedRect(box, r))
            using (var off = new SolidBrush(Color.FromArgb(41, ink))) g.FillPath(off, track);
            float h = box.Height * Math.Clamp(fill, 0, 1);
            if (h < 0.5f) return;
            using var on = new SolidBrush(color);
            using var lit = RoundedRect(new RectangleF(box.Left, box.Bottom - h, box.Width, h), r);
            g.FillPath(on, lit);
        }

        /// <summary>A ring around <paramref name="middle"/>, its arc filled to <paramref name="fill"/> from the top.</summary>
        private void DrawRing(Graphics g, PointF middle, float d, float fill, Color color, Color ink)
        {
            // 3.5 px at the full 26, thinner with a smaller ring (a short taskbar's), never under 2.5.
            float stroke = Math.Max(2.5f * _scale, d * 3.5f / StripRing), r = d / 2 - stroke / 2;
            var ring = new RectangleF(middle.X - r, middle.Y - r, 2 * r, 2 * r);
            using (var track = new Pen(Color.FromArgb(36, ink), stroke)) g.DrawEllipse(track, ring);
            if (fill > 0.005f)
            {
                using var arc = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, ring, -90, 360 * fill);
            }
        }

        /// <summary>
        /// A fill-up look round <paramref name="middle"/>, <paramref name="d"/> across, filled to <paramref name="fill"/>: a ring
        /// from the top; a half ring (a gauge) from the left round to the right; a pie from the top.
        /// </summary>
        private void DrawFillUp(Graphics g, TrayLook look, PointF middle, float d, float fill, Color color, Color ink)
        {
            if (look == TrayLook.Ring) { DrawRing(g, middle, d, fill, color, ink); return; }
            if (look == TrayLook.Pie)
            {
                float pr = d / 2 - _scale;
                var pie = new RectangleF(middle.X - pr, middle.Y - pr, 2 * pr, 2 * pr);
                using (var track = new SolidBrush(Color.FromArgb(36, ink))) g.FillEllipse(track, pie);
                if (fill > 0.005f)
                {
                    using var slice = new SolidBrush(color);
                    if (fill >= 0.995f) g.FillEllipse(slice, pie);
                    else g.FillPie(slice, pie.X, pie.Y, pie.Width, pie.Height, -90, 360 * fill);
                }
                return;
            }
            // Half ring: the upper half, its middle a little under the reading's, so the arch sits in the middle of the bar.
            float stroke = Math.Max(2.5f * _scale, d * 3.5f / StripRing), r = d / 2 - stroke / 2;
            var arch = new RectangleF(middle.X - r, middle.Y - r + d / 4, 2 * r, 2 * r);
            using (var track = new Pen(Color.FromArgb(36, ink), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(track, arch, 180, 180);
            if (fill > 0.005f)
            {
                using var arc = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, arch, 180, 180 * fill);
            }
        }

        /// <summary>Each core's load as a thin bar across <paramref name="box"/>, filled from the bottom (the busiest stand out).</summary>
        private void DrawCores(Graphics g, StripValue v, RectangleF box, Color color, Color ink)
        {
            var cores = v.Cores is { Length: > 0 } c ? c.Take(MaxCores).ToArray() : [v.Fill];
            var (w, gap) = CoreBar(v);
            using var on = new SolidBrush(color);
            using var off = new SolidBrush(Color.FromArgb(36, ink));
            for (int i = 0; i < cores.Length; i++)
            {
                float x = box.Left + i * (w + gap), h = Math.Max(_scale, Math.Clamp(cores[i], 0, 1) * box.Height);
                g.FillRectangle(off, x, box.Top, w, box.Height);
                g.FillRectangle(on, x, box.Bottom - h, w, h);
            }
        }

        /// <summary>Ten blocks across <paramref name="box"/>, lit up to <paramref name="fill"/>.</summary>
        private void DrawBlocks(Graphics g, RectangleF box, float fill, Color color, Color ink)
        {
            float gap = 1.5f * _scale, w = (box.Width - 9 * gap) / 10;
            int lit = (int)Math.Round(fill * 10);
            using var on = new SolidBrush(color);
            using var off = new SolidBrush(Color.FromArgb(41, ink));
            for (int b = 0; b < 10; b++)
            {
                using var block = RoundedRect(new RectangleF(box.Left + b * (w + gap), box.Top, w, box.Height), Math.Min(1.5f * _scale, w / 2));
                g.FillPath(b < lit ? on : off, block);
            }
        }

        public void Dispose()
        {
            _bigFont.Dispose();
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
        private readonly bool _tiles, _roomy;
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
            // Two lines of 11.5 px numbers fit a 40 px tile; the words a little smaller, as on the strip. A short taskbar's
            // 24 px tile (Windows' smaller buttons) takes 9.5 and 8, with room above, between and below (issue #5's mock-up).
            _roomy = height >= StripTwoLineHeight * scale;
            _valueFont = new Font(_valueFamily, (_roomy ? 11.5f : 9.5f) * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _nameFont = new Font(_nameFamily, (_roomy ? 9.5f : 8f) * scale, FontStyle.Bold, GraphicsUnit.Pixel);
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
            // The two lines' digits centred in the tile as a pair, each word on its number's baseline (a box of one reading
            // keeps it on the top line, where its pair's first would be).
            float digits = DigitHeight(_valueFont), between = (_roomy ? 7 : 3.5f) * _scale;
            float firstBaseline = tileTop + tileHeight / 2 - (2 * digits + between) / 2 + digits;
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
                    float baseline = firstBaseline + i * (digits + between);
                    using (var nameBrush = new SolidBrush(l.LabelColor)) g.DrawString(l.Label, _nameFont, nameBrush, x + _tilePad, baseline - Ascent(_nameFont), _format);
                    using var brush = new SolidBrush(l.Color);
                    g.DrawString(l.Text, _valueFont, brush, x + width - _tilePad - TextWidth(l.Text, _valueFont), baseline - Ascent(_valueFont), _format);
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
    public static Bitmap RenderIconRow(IReadOnlyList<(IReadOnlyList<(string Text, Color Color)> Lines, Color? Tile, IconLook? Look)> icons, int height, int iconSize, int slot, out float[] edges)
    {
        int width = Math.Max(1, icons.Count * slot);
        var bmp = new Bitmap(width, Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using (var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(catcher, 0, 0, width, height);
        edges = new float[icons.Count];
        for (int i = 0; i < icons.Count; i++)
        {
            using var icon = RenderTrayIcon(icons[i].Lines, iconSize, tile: icons[i].Tile, look: icons[i].Look);
            g.DrawImage(icon, i * slot + (slot - iconSize) / 2, (height - iconSize) / 2, iconSize, iconSize);
            edges[i] = (i + 1) * slot;
        }
        return bmp;
    }
}

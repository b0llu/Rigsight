using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// One reading on the taskbar strip, as drawn: "56°" in its colour, the word under it ("VRAM"), and the widest text its
/// place is kept for ("88°"), so it doesn't move the strip each time a digit comes or goes.
/// </summary>
internal readonly record struct StripValue(string Text, Color Color, string Label = "", string Reserve = "", StripGraph? Graph = null);

/// <summary>
/// A reading's graph of the last minutes, filled in behind its number and word (as taskbar-monitor draws them): its points, each across (time, 0 = the
/// oldest kept, 1 = now) and up (0 = the graph's bottom, 1 = its top; NaN where there was no reading), in its colour.
/// </summary>
internal sealed record StripGraph(IReadOnlyList<PointF> Points, Color Color);

/// <summary>A part on the taskbar strip: its readings side by side, each with its word under it in the part's colour.</summary>
internal sealed record StripCell(string Name, Color NameColor, IReadOnlyList<StripValue> Values);

/// <summary>The taskbar strip (see TaskbarStrip): readings drawn like the taskbar's clock, on nothing.</summary>
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

    /// <summary>How far from the top edge the numbers sit (at 96 dpi) on a strip with a graph.</summary>
    internal const float StripGraphTextTop = 1;

    /// <summary>
    /// The strip at <paramref name="height"/> pixels (the taskbar's) and <paramref name="scale"/> (its DPI / 96): each
    /// reading over its word, like the clock's time over its date, the part's name under its first ("CPU") and a
    /// short one under the rest ("Load"), all bold in the part's colour (the colour groups them: a quieter word only
    /// looked faded at taskbar size); on a short taskbar, "CPU 48° 7%" on one line.
    /// <paramref name="cells"/>' right edges come back (for tooltips). Transparent but for the text, with an
    /// all-but-invisible background so clicks between the letters still land on it.
    /// </summary>
    public static Bitmap RenderStrip(IReadOnlyList<StripCell> cells, int height, float scale, out float[] edges, float graphWide = StripGraphWidth)
    {
        using var valueFamily = new FontFamily("Segoe UI Semibold");
        using var nameFamily = new FontFamily("Segoe UI");
        using var valueFont = new Font(valueFamily, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var nameFont = new Font(nameFamily, 9.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        bool twoLines = height >= StripTwoLineHeight * scale;
        float pad = 4 * scale, gap = 16 * scale, inner = 7 * scale;
        var format = StringFormat.GenericTypographic;

        using var measuring = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measuring);
        float Width(string text, Font font) => text.Length == 0 ? 0 : mg.MeasureString(text, font, PointF.Empty, format).Width;
        // Each reading keeps a place as wide as the widest it's been (its number or, on two lines, its word), numbers
        // and words to its right like the clock's: a digit more or less moves nothing. One line: the name, then the numbers.
        float graphWidth = graphWide * scale;
        float Number(StripValue v) => Math.Max(v.Graph is null ? 0 : graphWidth, Math.Max(Width(v.Text, valueFont), Width(v.Reserve, valueFont)));
        float Column(StripValue v, int i) => Math.Max(Number(v), Width(v.Label, nameFont));
        var columns = cells.Select(c => c.Values.Select((v, i) => twoLines ? Column(v, i) : Number(v)).ToArray()).ToArray();
        var widths = cells.Select((c, k) => columns[k].Sum() + inner * Math.Max(0, c.Values.Count - 1)
            + (twoLines ? 0 : Width(c.Name, nameFont) + inner)).ToArray();

        int width = Math.Max(1, (int)Math.Ceiling(pad * 2 + widths.Sum() + gap * Math.Max(0, cells.Count - 1)));
        var bmp = new Bitmap(width, Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using (var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(catcher, 0, 0, width, height);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Lines placed as the clock's: the pair centred in the bar, or the one line.
        float valueHeight = valueFont.GetHeight(g), nameHeight = nameFont.GetHeight(g);
        float valueTop = twoLines ? (height - (valueHeight + nameHeight)) / 2 : (height - valueHeight) / 2;
        // With a graph on the strip, all the text up at the top edge (every part alike, so they stay in line), as
        // taskbar-monitor has it: the graph below and behind the words gets the most room.
        if (twoLines && cells.Any(c => c.Values.Any(v => v.Graph is not null)))
            valueTop = Math.Min(valueTop, StripGraphTextTop * scale);
        float nameTop = twoLines ? valueTop + valueHeight : valueTop + (valueHeight - nameHeight) / 2 + 0.5f * scale;

        edges = new float[cells.Count];
        float x = pad;
        for (int k = 0; k < cells.Count; k++)
        {
            var cell = cells[k];
            using var nameBrush = new SolidBrush(cell.NameColor);
            float vx = x;
            if (!twoLines)
            {
                g.DrawString(cell.Name, nameFont, nameBrush, x, nameTop, format);
                vx += Width(cell.Name, nameFont) + inner;
            }
            for (int i = 0; i < cell.Values.Count; i++)
            {
                var value = cell.Values[i];
                using var brush = new SolidBrush(value.Color);
                float right = vx + columns[k][i];
                // The graph first, behind the word, the width of the column: its top is the number's bottom, so at its
                // highest it reaches up to the number and never over it (one line: no room under it, the bar's height).
                if (value.Graph is { } graph)
                {
                    float top = twoLines ? valueTop + valueHeight : 3 * scale;
                    DrawGraph(g, graph, new RectangleF(right - columns[k][i], top, columns[k][i], height - 3 * scale - top), scale);
                }
                g.DrawString(value.Text, valueFont, brush, right - Width(value.Text, valueFont), valueTop, format);
                if (twoLines && value.Label.Length > 0)
                    g.DrawString(value.Label, nameFont, nameBrush, right - Width(value.Label, nameFont), nameTop, format);
                vx += columns[k][i] + inner;
            }
            x += widths[k];
            edges[k] = k == cells.Count - 1 ? width : x + gap / 2; // halfway to the next; the last, to the end
            x += gap;
        }
        return bmp;
    }

    /// <summary>
    /// A reading's graph in <paramref name="box"/>, behind its number: the room under its line filled, light enough for
    /// the number to read over it, the line on top, a faint base line so an empty or flat graph still shows where it
    /// is. A gap in the readings is a gap in the graph.
    /// </summary>
    /// <summary>How strong a graph's fill is (of 255): plainly there, the white number still clear over it.</summary>
    internal const int GraphFillAlpha = 90;

    private static void DrawGraph(Graphics g, StripGraph graph, RectangleF box, float scale)
    {
        using (var basePen = new Pen(Color.FromArgb(70, graph.Color), Math.Max(1, scale)))
            g.DrawLine(basePen, box.Left, box.Bottom, box.Right, box.Bottom);
        var run = new List<PointF>();
        using var pen = new Pen(Color.FromArgb(200, graph.Color), 1.2f * scale) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(Color.FromArgb(GraphFillAlpha, graph.Color));
        void Flush()
        {
            if (run.Count >= 2)
            {
                var area = new List<PointF>(run) { new(run[^1].X, box.Bottom), new(run[0].X, box.Bottom) };
                g.FillPolygon(fill, [.. area]);
                g.DrawLines(pen, [.. run]);
            }
            else if (run.Count == 1)
            {
                using var dot = new SolidBrush(graph.Color);
                g.FillEllipse(dot, run[0].X - scale, run[0].Y - scale, 2 * scale, 2 * scale);
            }
            run.Clear();
        }
        foreach (var p in graph.Points)
        {
            if (!float.IsFinite(p.Y)) { Flush(); continue; }
            run.Add(new PointF(box.Left + Math.Clamp(p.X, 0, 1) * box.Width, box.Bottom - Math.Clamp(p.Y, 0, 1) * box.Height));
        }
        Flush();
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// One reading on the taskbar strip, as drawn: "56°" in its colour, the word under it ("VRAM"), and the widest text its
/// place is kept for ("88°"), so it doesn't move the strip each time a digit comes or goes.
/// </summary>
internal readonly record struct StripValue(string Text, Color Color, string Label = "", string Reserve = "");

/// <summary>A part on the taskbar strip: its readings side by side, each with its word under it in the part's colour.</summary>
internal sealed record StripCell(string Name, Color NameColor, IReadOnlyList<StripValue> Values);

/// <summary>The taskbar strip (see TaskbarStrip): readings drawn like the taskbar's clock, on nothing.</summary>
internal static partial class WidgetRenderer
{
    /// <summary>Below this height (at 96 dpi) a word doesn't fit under the numbers: the part's name goes before them, on one line.</summary>
    internal const float StripTwoLineHeight = 36;

    /// <summary>
    /// The strip at <paramref name="height"/> pixels (the taskbar's) and <paramref name="scale"/> (its DPI / 96): each
    /// reading over its word, like the clock's time over its date, the part's name under its first ("CPU") and a
    /// short one under the rest ("Load"), all in the part's colour; on a short taskbar, "CPU 48° 7%" on one line.
    /// <paramref name="cells"/>' right edges come back (for tooltips). Transparent but for the text, with an
    /// all-but-invisible background so clicks between the letters still land on it.
    /// </summary>
    public static Bitmap RenderStrip(IReadOnlyList<StripCell> cells, int height, float scale, out float[] edges)
    {
        using var valueFamily = new FontFamily("Segoe UI Semibold");
        using var nameFamily = new FontFamily("Segoe UI");
        using var valueFont = new Font(valueFamily, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var nameFont = new Font(nameFamily, 9.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelFont = new Font(nameFamily, 9.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        bool twoLines = height >= StripTwoLineHeight * scale;
        float pad = 4 * scale, gap = 16 * scale, inner = 7 * scale;
        var format = StringFormat.GenericTypographic;

        using var measuring = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measuring);
        float Width(string text, Font font) => text.Length == 0 ? 0 : mg.MeasureString(text, font, PointF.Empty, format).Width;
        // Each reading keeps a place as wide as the widest it's been (its number or, on two lines, its word), numbers
        // and words to its right like the clock's: a digit more or less moves nothing. One line: the name, then the numbers.
        float Number(StripValue v) => Math.Max(Width(v.Text, valueFont), Width(v.Reserve, valueFont));
        float Column(StripValue v, int i) => Math.Max(Number(v), Width(v.Label, i == 0 ? nameFont : labelFont));
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
        float nameTop = twoLines ? valueTop + valueHeight : valueTop + (valueHeight - nameHeight) / 2 + 0.5f * scale;

        edges = new float[cells.Count];
        float x = pad;
        for (int k = 0; k < cells.Count; k++)
        {
            var cell = cells[k];
            using var nameBrush = new SolidBrush(cell.NameColor);
            // The words after the part's name a little quieter, so the name still heads the group.
            using var labelBrush = new SolidBrush(Color.FromArgb(205, cell.NameColor));
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
                g.DrawString(value.Text, valueFont, brush, right - Width(value.Text, valueFont), valueTop, format);
                if (twoLines && value.Label.Length > 0)
                {
                    var font = i == 0 ? nameFont : labelFont;
                    g.DrawString(value.Label, font, i == 0 ? nameBrush : labelBrush, right - Width(value.Label, font), nameTop, format);
                }
                vx += columns[k][i] + inner;
            }
            x += widths[k];
            edges[k] = k == cells.Count - 1 ? width : x + gap / 2; // halfway to the next; the last, to the end
            x += gap;
        }
        return bmp;
    }
}

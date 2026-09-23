using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

internal static partial class WidgetRenderer
{
    /// <summary>
    /// One number on the overlay. <paramref name="Template"/> is the widest text the value usually
    /// takes, so the overlay doesn't jitter as numbers change (62° → 100°).
    /// </summary>
    private sealed record Cell(string Value, string Unit, Color Color, string Template, float Px = OverlayValuePx);

    private sealed record OverlayRow(string Label, Color LabelColor, List<Cell> Cells);

    private const float OverlayValuePx = 15, OverlayUnitPx = 11, OverlayLabelPx = 11;
    private const float OverlayPad = 8, OverlayRowHeight = 23, OverlayCellGap = 12, OverlayGroupGap = 20, OverlayLabelWidth = 34;

    /// <summary>Distance from the top of a line of text to its baseline, as GDI+ draws it.</summary>
    private static float Ascent(float px, FontStyle style, bool semibold)
    {
        using var font = MakeFont(px, style, semibold);
        var f = font.FontFamily;
        return px * f.GetCellAscent(font.Style) / f.GetEmHeight(font.Style);
    }

    private static readonly Palette OverlayPalette = For(WidgetTheme.Black);

    /// <summary>The panel's own see-through-ness at 100% opacity (the same in our window and in RivaTuner).</summary>
    private const int OverlayPanelAlpha = 190;

    private static List<OverlayRow> OverlayRows(OverlaySettings o, WidgetData d)
    {
        var p = OverlayPalette;
        bool Has(OverlayMetric m) => o.Metrics.Contains(m);
        var rows = new List<OverlayRow>();

        // Only while a game is drawing frames (RivaTuner measures them), so the desktop shows no empty FPS row.
        var game = new List<Cell>();
        if (d.Frame is { } f)
        {
            if (Has(OverlayMetric.Fps)) game.Add(new($"{f.Fps:0}", "", p.Text, "888"));
            if (Has(OverlayMetric.FrameTime)) game.Add(new($"{f.FrameTimeMs:0.0}", "ms", p.Text, "88.8"));
            if (Has(OverlayMetric.OnePercentLow) && f.OnePercentLow is double low) game.Add(new($"{low:0}", "1% low", p.Text, "888"));
        }
        if (game.Count > 0) rows.Add(new("FPS", Fps, game));

        var cpu = new List<Cell>();
        if (Has(OverlayMetric.CpuTemp)) cpu.Add(TempCell(d.CpuTemp));
        if (Has(OverlayMetric.CpuLoad)) cpu.Add(LoadCell(d.CpuLoad));
        if (Has(OverlayMetric.CpuClock)) cpu.Add(ClockCell(d.CpuClock));
        if (Has(OverlayMetric.CpuPower)) cpu.Add(PowerCell(d.CpuPower));
        if (cpu.Count > 0) rows.Add(new("CPU", Cpu, cpu));

        var gpu = new List<Cell>();
        if (Has(OverlayMetric.GpuTemp)) gpu.Add(TempCell(d.GpuTemp));
        if (Has(OverlayMetric.GpuHotSpot) && d.GpuHotSpot is not null) gpu.Add(TempCell(d.GpuHotSpot, "hot"));
        if (Has(OverlayMetric.GpuLoad)) gpu.Add(LoadCell(d.GpuLoad));
        if (Has(OverlayMetric.GpuClock)) gpu.Add(ClockCell(d.GpuClock));
        if (Has(OverlayMetric.GpuPower)) gpu.Add(PowerCell(d.GpuPower));
        if (Has(OverlayMetric.GpuMemory) && d.VramUsedMb is double vu)
            gpu.Add(MemoryCell(vu / 1024, d.VramTotalMb / 1024, "VRAM"));
        if (gpu.Count > 0) rows.Add(new("GPU", Gpu, gpu));

        if (Has(OverlayMetric.Ram) && d.RamUsedGb is double ru)
            rows.Add(new("RAM", Ram, [MemoryCell(ru, d.RamTotalGb, "")]));

        // The last row has no label: the app in front, how long you've been on it, and the time.
        var other = new List<Cell>();
        var a = d.Activity;
        if (Has(OverlayMetric.Session) && !a.Paused && a.Name is not null)
        {
            string app = a.Name.Length > 24 ? a.Name[..23] + "…" : a.Name;
            other.Add(new(app, "", p.Muted, "", 12.5f));
            other.Add(new(a.SessionActiveSec > 0 ? Units.Duration(a.SessionActiveSec) : "0m", "", p.Text, "8h 88m"));
        }
        if (Has(OverlayMetric.Clock))
            other.Add(new(DateTime.Now.ToString("t", CultureInfo.CurrentCulture), "", p.Muted, "88:88"));
        if (other.Count > 0) rows.Add(new("", p.Muted, other));

        return rows;

        Cell TempCell(double? c, string unit = "") =>
            new(c is double v ? $"{Units.Temp(v):0}°" : "—", unit, TempColor(c, p), "188°");
        Cell LoadCell(double? v) => new(v is double l ? $"{l:0}" : "—", "%", p.Text, "100");
        Cell ClockCell(double? mhz) => new(mhz is double v ? $"{v / 1000:0.00}" : "—", "GHz", p.Text, "8.88");
        Cell PowerCell(double? w) => new(w is double v ? $"{v:0}" : "—", "W", p.Text, "888");
        Cell MemoryCell(double usedGb, double? totalGb, string label)
        {
            string unit = totalGb is double t && t > 0 ? $"/ {t:0} GB" : "GB";
            if (label.Length > 0) unit += " " + label;
            return new($"{usedGb:0.0}", unit, p.Text, "88.8");
        }
    }

    /// <summary>
    /// The same readout in RivaTuner's hypertext, styled like our own window: Segoe UI, a rounded
    /// translucent panel, coloured values with smaller grey units, pinned to the chosen corner.
    /// Tag meanings follow RTSS's SDK (RTSSSharedMemorySample and the OverlayEditor plugin source):
    /// &lt;P0/2/6/8&gt;&lt;Ln&gt; sticky corner layer, &lt;M&gt; margins, &lt;C=AARRGGBB&gt;&lt;B=0,0,Rr&gt;\b rounded
    /// background, &lt;A=±n&gt; alignment in symbols (negative = right), &lt;S=-n&gt; n% subscript size.
    /// </summary>
    public static string RtssText(OverlaySettings o, WidgetData? data)
    {
        var rows = OverlayRows(o, data ?? new WidgetData());
        if (rows.Count == 0) return "";
        // Our window fades everything by the opacity setting, text included; RTSS colours take an alpha too.
        int textAlpha = (int)Math.Round(255 * o.Opacity);
        string Hex(Color c) => textAlpha >= 255 ? $"{c.R:X2}{c.G:X2}{c.B:X2}" : $"{textAlpha:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        static string Clean(string text) => text.Replace("<", "").Replace(">", "");

        string Row(OverlayRow row)
        {
            var parts = new List<string>();
            // Labels share a left-aligned column so the numbers line up, as in the window.
            if (row.Label.Length > 0) parts.Add($"<A=4><S=-80><C={Hex(row.LabelColor)}>{row.Label}<C><S><A>");
            for (int i = 0; i < row.Cells.Count; i++)
            {
                var cell = row.Cells[i];
                string value = Clean(cell.Value);
                // Numbers are right-aligned to their usual width, so they don't jump around (62° → 100°).
                // RTSS sizes the panel from these widths, so a value wider than its slot ("5:38 PM") must widen
                // the slot, or it runs past the panel (and off-screen in right-hand corners). The unlabelled
                // last row starts at the left edge, as in the window.
                bool align = cell.Template.Length > 0 && !(row.Label.Length == 0 && i == 0);
                string cellText = align
                    ? $"<A=-{Math.Max(cell.Template.Length, value.Length)}><C={Hex(cell.Color)}>{value}<C><A>"
                    : $"<C={Hex(cell.Color)}>{(cell.Px < OverlayValuePx ? $"<S=-85>{value}<S>" : value)}<C>";
                if (cell.Unit.Length > 0)
                {
                    string unit = (char.IsLetterOrDigit(cell.Unit[0]) || cell.Unit[0] == '/' ? " " : "") + Clean(cell.Unit);
                    cellText += $"<S=-72><C={Hex(OverlayPalette.Muted)}>{unit}<C><S>";
                }
                parts.Add(cellText);
            }
            return RtssLeftSpacer + string.Join("  ", parts);
        }

        int corner = o.Corner switch
        {
            OverlayCorner.TopRight => 2,
            OverlayCorner.BottomLeft => 6,
            OverlayCorner.BottomRight => 8,
            _ => 0,
        };
        int fontHeight = -(int)Math.Round(8 * o.Scale);
        int alpha = (int)Math.Round(OverlayPanelAlpha * o.Opacity);
        bool right = o.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        bool bottom = o.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
        var (left, top, rightM, bottomM) = RtssMargins(right, bottom, o.Scale);
        string header =
            $"<FNT=Segoe UI Semibold,{fontHeight},600,{RtssZoom}>" +   // our font instead of RivaTuner's default
            $"<P{corner}><L0><M={left},{top},{rightM},{bottomM}>" +    // our corner, gap and padding (see RtssMargins)
            $"<C={alpha:X2}080A0E><B=0,0,R8>\b<C>";                   // rounded translucent panel behind the text
        return header + RtssTopSpacer + string.Join(o.Layout == OverlayLayout.Line ? "    " : "\n", rows.Select(Row));
    }

    /// <summary>RTSS draws at this zoom ratio (set by our &lt;FNT&gt; tag): one margin unit is this many screen pixels.</summary>
    private const int RtssZoom = 2;

    /// <summary>Gap between the panel and the screen edges, as for our own window (screen pixels).</summary>
    internal static int RtssGapPx = 16;

    /// <summary>Space between the text and the panel's right edge (screen pixels at size M).</summary>
    internal static int RtssPadPx = 10;

    /// <summary>Space between the text and the panel's bottom edge (screen pixels at size M).</summary>
    internal static int RtssPadBottomPx = 11;

    /// <summary>Starts each line: the panel's left padding (margins can't add it; see RtssMargins).</summary>
    internal static string RtssLeftSpacer = "  ";

    /// <summary>Starts the text: the panel's top padding, as a thin empty line (38% of a line).</summary>
    internal static string RtssTopSpacer = "<S=-38> <S>\n";

    /// <summary>
    /// &lt;M=left,top,right,bottom&gt; for a corner. Measured in RTSS 7.3.7 (sticky corner layers, content-sized):
    /// with W the text width and k the zoom, the layer is W − k(L+R) wide and pinned to its corner; the text
    /// starts at kL from the layer's left and the panel spans from there to W − kR. So left/top margins move
    /// the text and panel together, and right/bottom ones only grow or shrink the panel. Solving for "panel
    /// <see cref="RtssGapPx"/> from both screen edges, text at its start, <see cref="RtssPadPx"/> spare at its end":
    /// near edge L = gap/k (or −gap/k when pinned to the far edge), far edge R = −(gap+pad)/k (or (gap−pad)/k).
    /// Left and top padding come from spacers instead (<see cref="RtssLeftSpacer"/>, <see cref="RtssTopSpacer"/>).
    /// The spacing was tuned to match our own window, and checked across corners, layouts and readings.
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom) RtssMargins(bool right, bool bottom, double scale)
    {
        int near = RtssGapPx / RtssZoom;
        int Far(bool pinnedFar, int padAt1) { int pad = (int)Math.Round(padAt1 * scale); return pinnedFar ? (RtssGapPx - pad) / RtssZoom : -(RtssGapPx + pad) / RtssZoom; }
        return (right ? -near : near, bottom ? -near : near, Far(right, RtssPadPx), Far(bottom, RtssPadBottomPx));
    }

    /// <summary>A unit that starts with a number ("1% low") needs a space after the value; others sit close ("6.9ms" reads as "6.9 ms").</summary>
    private static string UnitText(Cell c) => char.IsDigit(c.Unit[0]) ? " " + c.Unit : c.Unit;

    private static readonly Color Fps = Color.FromArgb(251, 191, 36);

    /// <summary>Draws the overlay readout. Returns a tiny transparent bitmap when nothing is chosen.</summary>
    public static Bitmap RenderOverlay(OverlaySettings o, WidgetData? data, float scale)
    {
        var d = data ?? new WidgetData();
        var rows = OverlayRows(o, d);
        bool line = o.Layout == OverlayLayout.Line;

        using var probe = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(probe);
        float LabelWidth(OverlayRow r) => r.Label.Length == 0 ? 0
            : Math.Max(OverlayLabelWidth, Measure(mg, r.Label, OverlayLabelPx, FontStyle.Bold) + 8);
        float CellWidth(Cell c) =>
            Math.Max(Measure(mg, c.Template, c.Px, FontStyle.Bold, semibold: true), Measure(mg, c.Value, c.Px, FontStyle.Bold, semibold: true))
            + (c.Unit.Length > 0 ? 2 + Measure(mg, UnitText(c), OverlayUnitPx, FontStyle.Regular) : 0);
        float RowWidth(OverlayRow r) => LabelWidth(r) + r.Cells.Sum(CellWidth) + OverlayCellGap * (r.Cells.Count - 1);

        // In rows, labels share one column so the numbers line up. The unlabelled last row starts at the edge.
        float labelColumn = rows.Count > 0 ? rows.Max(LabelWidth) : 0;
        float Indent(OverlayRow r) => r.Label.Length == 0 ? 0 : labelColumn;
        float w, h;
        if (rows.Count == 0) { w = 1; h = 1; }
        else if (line)
        {
            w = OverlayPad * 2 + rows.Sum(RowWidth) + OverlayGroupGap * (rows.Count - 1);
            h = OverlayPad * 2 + OverlayRowHeight;
        }
        else
        {
            w = OverlayPad * 2 + rows.Max(r => Indent(r) + RowWidth(r) - LabelWidth(r));
            h = OverlayPad * 2 + OverlayRowHeight * rows.Count;
        }

        var bmp = new Bitmap(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale)), PixelFormat.Format32bppArgb);
        if (rows.Count == 0) return bmp;
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(scale, scale);

        using (var path = RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 10))
        using (var bg = new SolidBrush(Color.FromArgb(OverlayPanelAlpha, 8, 10, 14)))
        using (var border = new Pen(Color.FromArgb(30, 255, 255, 255), 1))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        // Every piece of text in a row sits on one baseline, placed so the numbers' capitals are
        // centred in the row (Segoe UI's capitals are 0.7 em tall).
        float labelAscent = Ascent(OverlayLabelPx, FontStyle.Bold, false);
        float unitAscent = Ascent(OverlayUnitPx, FontStyle.Regular, false);
        float x = OverlayPad, y = OverlayPad;
        foreach (var row in rows)
        {
            float baseline = y + OverlayRowHeight / 2 + OverlayValuePx * 0.7f / 2;
            float cx = x;
            if (row.Label.Length > 0) Text(g, row.Label, OverlayLabelPx, FontStyle.Bold, row.LabelColor, cx, baseline - labelAscent);
            cx += line ? LabelWidth(row) : Indent(row);
            foreach (var cell in row.Cells)
            {
                Text(g, cell.Value, cell.Px, FontStyle.Bold, cell.Color, cx, baseline - Ascent(cell.Px, FontStyle.Bold, true), semibold: true);
                float vw = Measure(g, cell.Value, cell.Px, FontStyle.Bold, semibold: true);
                if (cell.Unit.Length > 0)
                    Text(g, UnitText(cell), OverlayUnitPx, FontStyle.Regular, OverlayPalette.Muted, cx + vw + 2, baseline - unitAscent);
                cx += CellWidth(cell) + OverlayCellGap;
            }
            if (line) x = cx - OverlayCellGap + OverlayGroupGap;
            else y += OverlayRowHeight;
        }
        return bmp;
    }
}

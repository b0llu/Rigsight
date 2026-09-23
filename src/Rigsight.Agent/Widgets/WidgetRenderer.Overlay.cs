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

    private static List<OverlayRow> OverlayRows(OverlaySettings o, WidgetData d)
    {
        var p = OverlayPalette;
        bool Has(OverlayMetric m) => o.Metrics.Contains(m);
        var rows = new List<OverlayRow>();

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
        static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
        static string Clean(string text) => text.Replace("<", "").Replace(">", "");

        string Row(OverlayRow row)
        {
            var parts = new List<string>();
            // Labels share a left-aligned column so the numbers line up, as in the window.
            if (row.Label.Length > 0) parts.Add($"<A=4><S=-80><C={Hex(row.LabelColor)}>{row.Label}<C><S><A>");
            foreach (var cell in row.Cells)
            {
                string value = Clean(cell.Value);
                // Numbers are right-aligned to their usual width, so they don't jump around (62° → 100°).
                string cellText = cell.Template.Length > 0
                    ? $"<A=-{cell.Template.Length}><C={Hex(cell.Color)}>{value}<C><A>"
                    : $"<C={Hex(cell.Color)}>{(cell.Px < OverlayValuePx ? $"<S=-85>{value}<S>" : value)}<C>";
                if (cell.Unit.Length > 0)
                {
                    string unit = (char.IsLetter(cell.Unit[0]) || cell.Unit[0] == '/' ? " " : "") + Clean(cell.Unit);
                    cellText += $"<S=-72><C={Hex(OverlayPalette.Muted)}>{unit}<C><S>";
                }
                parts.Add(cellText);
            }
            return string.Join("  ", parts);
        }

        int corner = o.Corner switch
        {
            OverlayCorner.TopRight => 2,
            OverlayCorner.BottomLeft => 6,
            OverlayCorner.BottomRight => 8,
            _ => 0,
        };
        int fontHeight = -(int)Math.Round(8 * o.Scale);
        int alpha = (int)Math.Round(200 * o.Opacity);
        string header =
            $"<FNT=Segoe UI Semibold,{fontHeight},600,2>" +      // our font instead of RivaTuner's default
            $"<P{corner}><L0><M=10,6,10,6>" +                      // our corner; inner margins pad the text inside the panel
            $"<C={alpha:X2}080A0E><B=0,0,R8>\b<C>";            // rounded translucent panel behind it
        return header + string.Join(o.Layout == OverlayLayout.Line ? "    " : "\n", rows.Select(Row));
    }

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
            + (c.Unit.Length > 0 ? 2 + Measure(mg, c.Unit, OverlayUnitPx, FontStyle.Regular) : 0);
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
        using (var bg = new SolidBrush(Color.FromArgb(190, 8, 10, 14)))
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
                    Text(g, cell.Unit, OverlayUnitPx, FontStyle.Regular, OverlayPalette.Muted, cx + vw + 2, baseline - unitAscent);
                cx += CellWidth(cell) + OverlayCellGap;
            }
            if (line) x = cx - OverlayCellGap + OverlayGroupGap;
            else y += OverlayRowHeight;
        }
        return bmp;
    }
}

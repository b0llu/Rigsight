using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// An icon in a rich hover: an app's own picture (<see cref="Image"/>), a glyph of Windows' icon font in a tinted square
/// (<see cref="Ink"/> on <see cref="Back"/>, a driver's kind), or a letter on a grey square when there's neither.
/// </summary>
public sealed record RichIcon(ImageSource? Image = null, string? Glyph = null, Brush? Ink = null, Brush? Back = null, string? Letter = null);

/// <summary>A row of a rich hover's parts: a round dot in the part's own colour, its name, its figure, and a grey line under it.</summary>
public sealed record RichRow(Brush Dot, string Name, string Value, string? Under = null);

/// <summary>
/// A fact in the rich hover's shaded box: a small caps label over its value, two to a row, or a row of its own
/// (<see cref="Wide"/>) with an icon and a grey <see cref="Detail"/> after the value ("Windows' graphics system · dxgkrnl.sys").
/// </summary>
public sealed record RichFact(string Label, string Value, bool Wide = false, RichIcon? Icon = null, string? Detail = null, Brush? Brush = null);

/// <summary>A line of a rich hover's note: its icon, its words and a figure at its right.</summary>
public sealed record RichItem(RichIcon? Icon, string Text, string? Right = null);

/// <summary>A shaded note under the facts: a small caps label ("IN FRONT", "ALSO BUSY THIS HOUR") and its lines.</summary>
public sealed record RichNote(string Label, IReadOnlyList<RichItem> Items);

/// <summary>
/// What a rich hover says (1.22.0, the approved mock-up's richTip): a title and a grey line under it (with an icon, a game
/// in front), one big figure with its word, a split bar in the parts' colours and their rows, the facts in a shaded box,
/// notes of what's behind it with their icons, a line in the update colour, and what a click does under a thin line. Any
/// part left empty is left out.
/// </summary>
public sealed record RichTip(string Title)
{
    public string? Sub { get; init; }
    public RichIcon? SubIcon { get; init; }
    public string Big { get; init; } = "";
    public string BigWord { get; init; } = "";
    public Brush? BigBrush { get; init; }
    public IReadOnlyList<(Brush Brush, double Value)> Split { get; init; } = [];
    public IReadOnlyList<RichRow> Rows { get; init; } = [];
    public IReadOnlyList<RichFact> Facts { get; init; } = [];
    public IReadOnlyList<RichNote> Notes { get; init; } = [];
    public string? Mark { get; init; }
    public string? Foot { get; init; }
}

/// <summary>
/// The rich hover box any chart can draw (1.22.0: Latency's hours, Power's days; the quality bar is Reports' Active time
/// hover, see <see cref="DailyBars"/>): about 340 px wide so names stay on one line, beside the lit column of the bar
/// hovered, kept inside the bounds it's given. Dots and icons keep their size; a line that doesn't fit is cut with "…",
/// never wrapped over the figure beside it.
/// </summary>
internal static class RichHover
{
    public const double Width = 340;
    private const double PadX = 14, PadY = 12, BoxPad = 10, Corner = 10, IconSize = 20;

    private static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>The hovered bar's column, lit faintly behind it (as Reports and the mock-up do).</summary>
    public static void Lit(DrawingContext dc, Rect column)
    {
        var lit = ChartPaint.Brush(ChartPaint.Res("TextColor", 0xFF), 0.05);
        dc.DrawRoundedRectangle(lit, null, column, 6, 6);
    }

    /// <summary>
    /// Draws <paramref name="tip"/> beside <paramref name="bar"/> (its right, or its left near the right edge), its top level
    /// with the bar's top and moved up to fit inside <paramref name="bounds"/>. Returns where it was drawn.
    /// </summary>
    public static Rect Draw(DrawingContext dc, Visual v, RichTip tip, Rect bar, Rect bounds)
    {
        double h = Lay(null, v, tip, 0, 0);
        double x = bar.Right + 12;
        if (x + Width > bounds.Right) x = bar.Left - 12 - Width;
        if (x < bounds.Left) x = bounds.Left;
        double y = Math.Max(bounds.Top, Math.Min(bar.Top, bounds.Bottom - h));
        x = Math.Round(x) + 0.5;
        y = Math.Round(y) + 0.5;
        dc.DrawRoundedRectangle(ChartPaint.TooltipBg, ChartPaint.TooltipBorder, new Rect(x, y, Width, h), Corner, Corner);
        Lay(dc, v, tip, x, y);
        return new Rect(x, y, Width, h);
    }

    /// <summary>How tall the box is for <paramref name="tip"/> (what <see cref="Draw"/> would draw).</summary>
    public static double Height(Visual v, RichTip tip) => Lay(null, v, tip, 0, 0);

    // Lays the box out from its top left: draws with a drawing context, only measures without one. Returns its height.
    private static double Lay(DrawingContext? dc, Visual v, RichTip t, double x, double y)
    {
        double inner = Width - 2 * PadX, left = x + PadX, top = y + PadY, ty = top;

        // The title, and the grey line under it (an icon in front of it: the game in front).
        var title = One(v, t.Title, 13, ChartPaint.TextBrush, inner, bold: true);
        dc?.DrawText(title, new Point(left, ty));
        ty += title.Height;
        if (t.Sub is { Length: > 0 } subText)
        {
            ty += 2;
            double iconW = t.SubIcon is null ? 0 : 16 + 6;
            var sub = One(v, subText, 12, ChartPaint.Muted, inner - iconW);
            double lineH = Math.Max(sub.Height, t.SubIcon is null ? 0 : 16);
            if (t.SubIcon is { } si && dc is not null) DrawIcon(dc, v, si, new Rect(left, ty + (lineH - 16) / 2, 16, 16));
            dc?.DrawText(sub, new Point(left + iconW, ty + (lineH - sub.Height) / 2));
            ty += lineH;
        }

        // The big figure and its word on one baseline.
        if (t.Big.Length > 0)
        {
            ty += 8;
            var big = One(v, t.Big, 22, t.BigBrush ?? ChartPaint.TextBrush, inner, bold: true);
            var word = One(v, t.BigWord, 12, ChartPaint.Muted, Math.Max(10, inner - big.Width - 6));
            if (dc is not null)
            {
                dc.DrawText(big, new Point(left, ty));
                if (t.BigWord.Length > 0) dc.DrawText(word, new Point(left + big.Width + 6, ty + big.Baseline - word.Baseline));
            }
            ty += big.Height;
        }

        // The split: one thin bar of the parts in their colours, a 2 px gap between them.
        double all = t.Split.Sum(s => Math.Max(0, s.Value));
        if (all > 0)
        {
            ty += 10;
            if (dc is not null)
            {
                dc.PushClip(new RectangleGeometry(new Rect(left, ty, inner, 6), 3, 3));
                double bx = left;
                foreach (var (brush, value) in t.Split)
                {
                    if (value <= 0) continue;
                    double w = inner * value / all;
                    dc.DrawRectangle(brush, null, new Rect(bx, ty, Math.Max(0, w - 2), 6));
                    bx += w;
                }
                dc.Pop();
            }
            ty += 6;
        }

        // The parts' rows: a dot, the name, its grey line after it where there's room (else under it), the figure at the right.
        if (t.Rows.Count > 0)
        {
            ty += 9;
            for (int i = 0; i < t.Rows.Count; i++)
            {
                var r = t.Rows[i];
                var value = One(v, r.Value, 12.5, ChartPaint.TextBrush, inner / 2, bold: true);
                double room = inner - 18 - value.Width - 8;
                var name = One(v, r.Name, 12.5, ChartPaint.TextBrush, room);
                var under = r.Under is { Length: > 0 } u ? One(v, u, 11.5, ChartPaint.Faint, inner - 18) : null;
                bool beside = under is not null && name.Width + 8 + under.Width <= room;
                double rowH = Math.Max(name.Height, value.Height);
                if (dc is not null)
                {
                    dc.DrawEllipse(r.Dot, null, new Point(left + 4, ty + rowH / 2), 4, 4);
                    dc.DrawText(name, new Point(left + 18, ty + (rowH - name.Height) / 2));
                    if (beside) dc.DrawText(under!, new Point(left + 18 + name.Width + 8, ty + (rowH - name.Height) / 2 + name.Baseline - under!.Baseline));
                    dc.DrawText(value, new Point(left + inner - value.Width, ty + (rowH - value.Height) / 2));
                }
                ty += rowH;
                if (under is not null && !beside)
                {
                    dc?.DrawText(under, new Point(left + 18, ty - 1));
                    ty += under.Height - 1;
                }
                if (i < t.Rows.Count - 1) ty += 5;
            }
        }

        // The facts in a shaded box: two to a row, a wide one on a row of its own.
        if (t.Facts.Count > 0)
        {
            ty += 10;
            double boxTop = ty, colGap = 10, colW = (inner - 2 * BoxPad - colGap) / 2, fy = ty + 9;
            var rows = new List<List<RichFact>>();
            foreach (var f in t.Facts)
            {
                if (f.Wide || rows.Count == 0 || rows[^1].Count == 2 || rows[^1][0].Wide) rows.Add([f]);
                else rows[^1].Add(f);
            }
            // Measured first (the box goes under them), drawn after it.
            var drawn = new List<(FormattedText Text, Point At)>();
            var icons = new List<(RichIcon Icon, Rect At)>();
            for (int r = 0; r < rows.Count; r++)
            {
                double rowH = 0;
                // The row's values on one line, under its tallest label (one may take two lines).
                var labels = rows[r].Select(f => One(v, f.Label.ToUpper(CultureInfo.CurrentCulture), 10, ChartPaint.Faint, f.Wide ? inner - 2 * BoxPad : colW, bold: true, lines: 2)).ToList();
                double labelH = labels.Max(l => l.Height);
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var f = rows[r][c];
                    double cw = f.Wide ? inner - 2 * BoxPad : colW, cx = left + BoxPad + c * (colW + colGap);
                    var label = labels[c];
                    drawn.Add((label, new Point(cx, fy)));
                    double vy = fy + labelH + 2, h;
                    if (f.Icon is { } icon)
                    {
                        double tx = cx + IconSize + 8, room = cw - IconSize - 8;
                        var val = One(v, f.Value, 12.5, f.Brush ?? ChartPaint.TextBrush, room);
                        double lineH = Math.Max(IconSize, val.Height);
                        icons.Add((icon, new Rect(cx, vy + (lineH - IconSize) / 2, IconSize, IconSize)));
                        drawn.Add((val, new Point(tx, vy + (lineH - val.Height) / 2)));
                        if (f.Detail is { Length: > 0 } detail && room - val.Width > 30)
                        {
                            var d = One(v, $" · {detail}", 12.5, ChartPaint.Faint, room - val.Width);
                            drawn.Add((d, new Point(tx + val.Width, vy + (lineH - d.Height) / 2)));
                        }
                        h = labelH + 2 + lineH;
                    }
                    else
                    {
                        var val = One(v, f.Value, 12.5, f.Brush ?? ChartPaint.TextBrush, cw);
                        drawn.Add((val, new Point(cx, vy)));
                        h = labelH + 2 + val.Height;
                    }
                    rowH = Math.Max(rowH, h);
                }
                fy += rowH + (r < rows.Count - 1 ? 8 : 0);
            }
            double boxH = fy - boxTop + 9;
            if (dc is not null)
            {
                dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(left, boxTop, inner, boxH), 8, 8);
                foreach (var (icon, at) in icons) DrawIcon(dc, v, icon, at);
                foreach (var (text, at) in drawn) dc.DrawText(text, at);
            }
            ty += boxH;
        }

        // Each note in a shaded box of its own: its label, then its lines with their icons and figures.
        foreach (var note in t.Notes)
        {
            if (note.Items.Count == 0) continue;
            ty += 8;
            double boxTop = ty, ny = ty + 9, w = inner - 2 * BoxPad;
            var drawn = new List<(FormattedText Text, Point At)>();
            var icons = new List<(RichIcon Icon, Rect At)>();
            var label = One(v, note.Label.ToUpper(CultureInfo.CurrentCulture), 10, ChartPaint.Faint, w, bold: true);
            drawn.Add((label, new Point(left + BoxPad, ny)));
            ny += label.Height;
            foreach (var item in note.Items)
            {
                ny += 5;
                double ix = left + BoxPad, iconW = item.Icon is null ? 0 : IconSize + 8;
                var right = item.Right is { Length: > 0 } r ? One(v, r, 12.5, ChartPaint.TextBrush, w / 2, bold: true) : null;
                var text = One(v, item.Text, 12.5, ChartPaint.TextBrush, w - iconW - (right is null ? 0 : right.Width + 8));
                double lineH = Math.Max(item.Icon is null ? 0 : IconSize, text.Height);
                if (item.Icon is { } icon) icons.Add((icon, new Rect(ix, ny + (lineH - IconSize) / 2, IconSize, IconSize)));
                drawn.Add((text, new Point(ix + iconW, ny + (lineH - text.Height) / 2)));
                if (right is not null) drawn.Add((right, new Point(ix + w - right.Width, ny + (lineH - right.Height) / 2)));
                ny += lineH;
            }
            double boxH = ny - boxTop + 9;
            if (dc is not null)
            {
                dc.DrawRoundedRectangle(ChartPaint.Track, null, new Rect(left, boxTop, inner, boxH), 8, 8);
                foreach (var (icon, at) in icons) DrawIcon(dc, v, icon, at);
                foreach (var (text, at) in drawn) dc.DrawText(text, at);
            }
            ty += boxH;
        }

        // A line in the update colour (dropped readings, a change that day).
        if (t.Mark is { Length: > 0 } mark)
        {
            ty += 9;
            var m = Wrapped(v, mark, 12, ChartPaint.FpsMarkText, inner);
            dc?.DrawText(m, new Point(left, ty));
            ty += m.Height;
        }

        // What a click does, under a thin line across the box.
        if (t.Foot is { Length: > 0 } foot)
        {
            ty += 10;
            if (dc is not null) dc.DrawLine(ChartPaint.TooltipBorder, new Point(x + 1, Math.Round(ty) + 0.5), new Point(x + Width - 1, Math.Round(ty) + 0.5));
            ty += 1 + 7;
            var f = One(v, foot, 11.5, ChartPaint.Faint, inner);
            dc?.DrawText(f, new Point(left, ty));
            ty += f.Height;
        }
        return ty - y + PadY;
    }

    // One line, cut with "…" where it doesn't fit (a name beside a figure never wraps under it); a label may take two.
    private static FormattedText One(Visual v, string text, double size, Brush brush, double width, bool bold = false, int lines = 1)
    {
        var ft = ChartPaint.Format(v, text, size, brush, bold);
        ft.MaxTextWidth = Math.Max(1, width);
        ft.MaxLineCount = lines;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        return ft;
    }

    private static FormattedText Wrapped(Visual v, string text, double size, Brush brush, double width)
    {
        var ft = ChartPaint.Format(v, text, size, brush);
        ft.MaxTextWidth = Math.Max(1, width);
        return ft;
    }

    /// <summary>An icon in its square: an app's picture, a glyph on its kind's tint, or its letter on grey.</summary>
    private static void DrawIcon(DrawingContext dc, Visual v, RichIcon icon, Rect at)
    {
        if (icon.Image is { } image)
        {
            dc.DrawImage(image, at);
            return;
        }
        dc.DrawRoundedRectangle(icon.Back ?? ChartPaint.Track, null, at, 6, 6);
        if (icon.Glyph is { Length: > 0 } glyph)
        {
            var g = new FormattedText(glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, IconFace, Math.Round(at.Height * 0.48),
                icon.Ink ?? ChartPaint.Muted, VisualTreeHelper.GetDpi(v).PixelsPerDip);
            dc.DrawText(g, new Point(at.Left + (at.Width - g.Width) / 2, at.Top + (at.Height - g.Height) / 2));
        }
        else if (icon.Letter is { Length: > 0 } letter)
        {
            var l = ChartPaint.Format(v, letter, Math.Round(at.Height * 0.46), icon.Ink ?? ChartPaint.Muted, bold: true);
            dc.DrawText(l, new Point(at.Left + (at.Width - l.Width) / 2, at.Top + (at.Height - l.Height) / 2));
        }
    }
}

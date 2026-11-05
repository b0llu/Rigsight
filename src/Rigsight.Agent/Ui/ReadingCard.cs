using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Ui;

/// <summary>A reading shown big in its card: its value now, its last minutes, the span they cover and today's highest.</summary>
internal sealed record CardBig(string Name, SensorKind Kind, double? Value, Color ValueColor, IReadOnlyList<(long Ms, float Value)> History, string Span,
    string LowAvgHigh, string? Highest);

/// <summary>A line of words and a value in a card (one of the part's readings, or a temperature the fans cool).</summary>
internal sealed record CardLine(string Label, string Value, Color? Color = null);

/// <summary>An app in a card's list: its name, how much (for its bar, against the list's most) and the words for it.</summary>
internal sealed record CardApp(string Name, double Amount, string Value);

/// <summary>
/// A part's half of a card (see <see cref="ReadingCards"/>): its name and hardware, the clicked readings big, the part's
/// other readings, what uses it (or, for fans, what they cool) and the page its button opens.
/// </summary>
internal sealed record CardSection(string Title, string Hardware, Color PartColor, IReadOnlyList<CardBig> Big, IReadOnlyList<CardLine> Rows,
    string? AppsTitle, IReadOnlyList<CardApp> Apps, string? CoolsTitle, IReadOnlyList<CardLine> Cools, string Button, string Page);

/// <summary>
/// What a click on a taskbar reading opens (issue #5, approved mock-up v5): a card above it, in RigSight's theme, with a
/// section per part clicked side by side. Pointing at a graph reads it; the button opens the page; a click anywhere else
/// (it loses the focus), Esc, or the same reading again closes it. Kept up to date each second while open
/// (<see cref="Renew"/>). UI thread only.
/// </summary>
internal sealed class ReadingCard : Form
{
    private readonly Action<string> _openPage;
    private IReadOnlyList<CardSection> _sections = [];
    private float _s = 1; // the taskbar's DPI / 96
    private Color _accent, _onAccent;
    private Rectangle _anchor;
    private readonly List<(RectangleF Box, CardBig Big)> _graphs = [];
    private readonly List<(RectangleF Box, string Page)> _buttons = [];
    private Point _mouse = new(-1, -1);
    private long _closedAt;
    private string? _closedKey;

    /// <summary>What the card shows (the clicked readings' ids, joined), or null when closed.</summary>
    public string? Key { get; private set; }

    public ReadingCard(Action<string> openPage)
    {
        _openPage = openPage;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    /// <summary>Closes the card when it shows <paramref name="key"/> (a click on the same readings again); false when it doesn't.</summary>
    public bool CloseIfShowing(string key)
    {
        if (!Visible || Key != key) return false;
        CloseCard();
        return true;
    }

    /// <summary>Whether the card for <paramref name="key"/> was just closed (by the click on its own reading): that click doesn't open it again.</summary>
    public bool JustClosed(string key) => _closedKey == key && Environment.TickCount64 - _closedAt < 500;

    /// <summary>Opens the card above <paramref name="anchor"/> (the clicked reading, on the screen) with <paramref name="sections"/>.</summary>
    public void Open(string key, IReadOnlyList<CardSection> sections, Rectangle anchor, float scale, Color accent, Color onAccent)
    {
        Fill(key, sections, anchor, scale, accent, onAccent);
        if (!Visible) Show();
        Round();
        Activate();
        Win32Foreground(Handle);
    }

    /// <summary>Lays the card out for <paramref name="sections"/> above <paramref name="anchor"/>, without showing it (<see cref="Open"/> does).</summary>
    internal void Fill(string key, IReadOnlyList<CardSection> sections, Rectangle anchor, float scale, Color accent, Color onAccent)
    {
        Key = key;
        _anchor = anchor;
        _s = scale <= 0 ? 1 : scale;
        (_accent, _onAccent) = (accent, onAccent);
        _sections = sections;
        _mouse = new Point(-1, -1);
        Relayout();
    }

    /// <summary>New readings for the card that's open (the same readings, a second later).</summary>
    public void Renew(string key, IReadOnlyList<CardSection> sections)
    {
        if (!Visible || key != Key) return;
        _sections = sections;
        Relayout();
    }

    private void CloseCard()
    {
        if (!Visible) return;
        _closedKey = Key;
        _closedAt = Environment.TickCount64;
        Key = null;
        Hide();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        CloseCard();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) CloseCard();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool overButton = _buttons.Any(b => b.Box.Contains(e.Location));
        Cursor = overButton ? Cursors.Hand : Cursors.Default;
        _mouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var (box, page) in _buttons)
            if (box.Contains(e.Location))
            {
                CloseCard();
                _openPage(page);
                return;
            }
    }

    // ── Layout and drawing (sizes at 96 dpi, times the taskbar's scale) ──

    // Roomier since 1.3 (the user: "it feels crowded"): more padding, the graph the section's full width under the
    // value, the low, average and high as three labelled columns, and as many app rows kept as can show, so the card
    // never grows or shrinks while it's open as apps come and go ("layout shifts are lot").
    private const float Pad = 18, SectionWidth = 300, SectionGap = 18, Gap = 16, GraphHeight = 48, RowHeight = 22;

    private Font F(float px, FontStyle style = FontStyle.Regular, string family = "Segoe UI") => new(family, px * _s, style, GraphicsUnit.Pixel);

    private static Color Ink => DarkMenuRenderer.TextColor;
    private static Color Muted => Blend(DarkMenuRenderer.TextColor, DarkMenuRenderer.Bg, 0.62f);
    private static Color Soft => Blend(DarkMenuRenderer.TextColor, DarkMenuRenderer.Bg, 0.8f);
    private static Color Line => DarkMenuRenderer.Border;

    private static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));

    /// <summary>
    /// Sizes the card to its sections and puts it above the clicked reading, inside its screen. While the same card stays
    /// open it never gets shorter (a second's new readings can only need the room it has: rows are kept for every app and
    /// note it could show), so nothing in it moves.
    /// </summary>
    private void Relayout()
    {
        using var g = CreateGraphics();
        float height = _sections.Count == 0 ? 0 : _sections.Max(sec => Section(g, sec, 0, 0, draw: false));
        int w = (int)Math.Ceiling((2 * Pad + _sections.Count * SectionWidth + Math.Max(0, _sections.Count - 1) * (2 * SectionGap + 1)) * _s);
        int h = (int)Math.Ceiling(height + 2 * Pad * _s);
        if (Visible && Key == _laidOutKey) h = Math.Max(h, Height);
        _laidOutKey = Key;
        var area = Screen.FromPoint(new Point(_anchor.Left + _anchor.Width / 2, _anchor.Top)).WorkingArea;
        int gap = (int)(10 * _s);
        int x = Math.Clamp(_anchor.Left + _anchor.Width / 2 - w / 2, area.Left + gap, Math.Max(area.Left + gap, area.Right - gap - w));
        // Above a taskbar at the bottom (the usual), under one at the top.
        int y = _anchor.Top >= area.Bottom - 1 ? area.Bottom - gap - h : area.Top + gap;
        Bounds = new Rectangle(x, y, w, h);
        Invalidate();
    }

    /// <summary>The card last laid out (its readings), so a renewal of it keeps its height.</summary>
    private string? _laidOutKey;

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(DarkMenuRenderer.Bg);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _graphs.Clear();
        _buttons.Clear();
        float x = Pad * _s, top = Pad * _s;
        for (int i = 0; i < _sections.Count; i++)
        {
            if (i > 0)
            {
                // A line between two parts.
                x += SectionGap * _s;
                using (var pen = new Pen(Line, Math.Max(1, _s))) g.DrawLine(pen, x, top, x, Height - Pad * _s);
                x += (1 + SectionGap) * _s;
            }
            Section(g, _sections[i], x, top, draw: true);
            x += SectionWidth * _s;
        }
        using (var border = new Pen(Line, 1)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        DrawHover(g);
    }

    /// <summary>A section laid out (and drawn when <paramref name="draw"/>) from (x, y); its height comes back.</summary>
    private float Section(Graphics g, CardSection sec, float x, float y, bool draw)
    {
        float w = SectionWidth * _s, y0 = y;
        using var title = F(14, FontStyle.Bold, "Segoe UI Semibold");
        using var small = F(12);
        using var tiny = F(11.5f);
        using var body = F(13);
        using var bold = F(13, FontStyle.Regular, "Segoe UI Semibold");
        using var big = F(28, FontStyle.Regular, "Segoe UI Semibold");
        using var caption = F(11, FontStyle.Bold);

        // The part: its colour, name and hardware.
        if (draw)
        {
            using var dot = new SolidBrush(sec.PartColor);
            using var shape = Rounded(new RectangleF(x, y + 4 * _s, 10 * _s, 10 * _s), 3 * _s);
            g.FillPath(dot, shape);
            Write(g, sec.Title, title, Ink, x + 18 * _s, y, w - 18 * _s);
            Write(g, sec.Hardware, small, Muted, x + 18 * _s, y + 19 * _s, w - 18 * _s);
        }
        y += 38 * _s;

        // Each clicked reading: its value and what it is, its graph the full width under them, the time it covers, then
        // its low, average and high in three columns.
        foreach (var b in sec.Big)
        {
            y += Gap * _s;
            string value = Units.StripText(b.Kind, b.Value); // as the taskbar writes it
            if (draw)
            {
                Write(g, value, big, b.ValueColor, x, y, w);
                Write(g, Lang.T($"{b.Name} now"), small, Muted, x, y + 36 * _s, w);
            }
            y += 58 * _s;
            var graph = new RectangleF(x, y, w, GraphHeight * _s);
            if (draw)
            {
                DrawGraph(g, graph, b, sec.PartColor);
                _graphs.Add((graph, b));
                Write(g, b.Span, tiny, Muted, x, y + (GraphHeight + 6) * _s, w);
            }
            y += (GraphHeight + 6 + 22) * _s;
            var real = b.History.Select(p => p.Value).Where(float.IsFinite).ToList();
            string[] labels = [Lang.T("Lowest"), Lang.T("Average"), Lang.T("Highest")];
            string[] values = real.Count == 0 ? ["–", "–", "–"]
                : [Units.StripText(b.Kind, real.Min()), Units.StripText(b.Kind, real.Average()), Units.StripText(b.Kind, real.Max())];
            if (draw)
                for (int i = 0; i < 3; i++)
                {
                    float cx = x + i * w / 3;
                    Write(g, labels[i], tiny, Muted, cx, y, w / 3 - 6 * _s);
                    Write(g, values[i], bold, Ink, cx, y + 16 * _s, w / 3 - 6 * _s);
                }
            y += 38 * _s;
        }

        // The part's other readings, then today's highest (two lines kept for each, so a long one moves nothing).
        var notes = sec.Big.Select(b => b.Highest).OfType<string>().ToList();
        if (sec.Rows.Count > 0 || notes.Count > 0)
        {
            y += (Gap - 4) * _s;
            if (draw) Divider(g, x, y, w);
            y += 12 * _s;
            foreach (var r in sec.Rows)
            {
                if (draw)
                {
                    Write(g, r.Label, body, Soft, x, y, w * 0.55f);
                    WriteRight(g, r.Value, bold, r.Color ?? Ink, x + w, y, w * 0.45f);
                }
                y += RowHeight * _s;
            }
            foreach (var n in notes)
            {
                if (draw)
                    TextRenderer.DrawText(g, n, small, new Rectangle((int)x, (int)(y + 1 * _s), (int)w, 2 * (small.Height + 2)), Muted,
                        TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
                y += 36 * _s;
            }
        }

        // What uses the part: always its rows, as many as it can list, so apps coming and going move nothing; "nothing
        // much" in the first while none is (or what the fans cool).
        if (sec.AppsTitle is { } appsTitle)
        {
            y += (Gap - 4) * _s;
            if (draw) Divider(g, x, y, w);
            y += 12 * _s;
            if (draw) Write(g, appsTitle.ToUpperInvariant(), caption, Muted, x, y, w);
            y += 22 * _s;
            double most = sec.Apps.Count == 0 ? 1 : Math.Max(sec.Apps.Max(a => a.Amount), 1e-9);
            if (draw && sec.Apps.Count == 0) Write(g, Lang.T("Nothing much right now"), body, Muted, x, y, w);
            for (int i = 0; i < sec.Apps.Count && i < ReadingCards.MaxApps; i++)
            {
                var a = sec.Apps[i];
                if (draw)
                {
                    float meterX = x + w - 64 * _s - 74 * _s;
                    Write(g, a.Name, body, Ink, x, y + i * RowHeight * _s, meterX - x - 10 * _s);
                    float ry = y + i * RowHeight * _s;
                    using (var track = new SolidBrush(Blend(Ink, DarkMenuRenderer.Bg, 0.12f)))
                    using (var trackShape = Rounded(new RectangleF(meterX, ry + 7 * _s, 64 * _s, 5 * _s), 2.5f * _s)) g.FillPath(track, trackShape);
                    float fill = (float)Math.Clamp(a.Amount / most, 0.04, 1);
                    using (var bar = new SolidBrush(sec.PartColor))
                    using (var barShape = Rounded(new RectangleF(meterX, ry + 7 * _s, 64 * _s * fill, 5 * _s), 2.5f * _s)) g.FillPath(bar, barShape);
                    WriteRight(g, a.Value, body, Soft, x + w, ry, 70 * _s);
                }
            }
            y += ReadingCards.MaxApps * RowHeight * _s;
        }
        if (sec.CoolsTitle is { } coolsTitle && sec.Cools.Count > 0)
        {
            y += (Gap - 4) * _s;
            if (draw) Divider(g, x, y, w);
            y += 12 * _s;
            if (draw) Write(g, coolsTitle.ToUpperInvariant(), caption, Muted, x, y, w);
            y += 22 * _s;
            foreach (var c in sec.Cools)
            {
                if (draw)
                {
                    Write(g, c.Label, body, Ink, x, y, w * 0.6f);
                    WriteRight(g, c.Value, bold, c.Color ?? Ink, x + w, y, w * 0.4f);
                }
                y += RowHeight * _s;
            }
        }

        // The page it belongs to.
        y += (Gap + 2) * _s;
        float buttonWidth = Measure(g, sec.Button, bold) + 32 * _s, buttonHeight = 34 * _s;
        // At the card's foot when drawing: a card kept taller than its readings now need (it never shrinks while open)
        // keeps its button where it was, and side-by-side parts' buttons line up.
        if (draw) y = Math.Max(y, Height - Pad * _s - buttonHeight);
        var button = new RectangleF(x, y, buttonWidth, buttonHeight);
        if (draw)
        {
            bool hover = button.Contains(_mouse);
            // The label's box in the button's own colour: drawn on the plain accent, it showed as a patch over the
            // hovered (blended) button (the user, 2026-11-05).
            var face = hover ? Blend(_accent, DarkMenuRenderer.Bg, 0.85f) : _accent;
            using var fill = new SolidBrush(face);
            using var shape = Rounded(button, 8 * _s);
            g.FillPath(fill, shape);
            TextRenderer.DrawText(g, sec.Button, bold, Rectangle.Round(button), _onAccent, face,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            _buttons.Add((button, sec.Page));
        }
        y += buttonHeight;
        return y - y0;
    }

    /// <summary>The graph of a reading's last minutes, filled in its part's colour, with a dot at now.</summary>
    private void DrawGraph(Graphics g, RectangleF box, CardBig b, Color color)
    {
        using (var baseLine = new Pen(Line, Math.Max(1, _s))) g.DrawLine(baseLine, box.Left, box.Bottom - 0.5f, box.Right, box.Bottom - 0.5f);
        var pts = Points(box, b);
        if (pts.Count < 2) return;
        using var area = new GraphicsPath();
        area.AddLine(pts[0].X, box.Bottom, pts[0].X, pts[0].Y);
        area.AddLines([.. pts]);
        area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, box.Bottom);
        area.CloseFigure();
        using (var fill = new SolidBrush(Color.FromArgb(70, color))) g.FillPath(fill, area);
        using (var pen = new Pen(color, 1.6f * _s) { LineJoin = LineJoin.Round }) g.DrawLines(pen, [.. pts]);
        using var dot = new SolidBrush(color);
        g.FillEllipse(dot, pts[^1].X - 2.5f * _s, pts[^1].Y - 2.5f * _s, 5 * _s, 5 * _s);
    }

    /// <summary>A reading's history across <paramref name="box"/>: the oldest at the left, now at the right, its range with room above and below.</summary>
    private static List<PointF> Points(RectangleF box, CardBig b)
    {
        var real = b.History.Where(p => float.IsFinite(p.Value)).ToList();
        if (real.Count < 2) return [];
        float lo = real.Min(p => p.Value), hi = real.Max(p => p.Value), room = Math.Max((hi - lo) * 0.3f, Math.Max(Math.Abs(hi) * 0.05f, 0.5f));
        float bottom = b.Kind is SensorKind.Temperature ? lo - room : Math.Max(0, lo - room), top = hi + room;
        long first = real[0].Ms, span = Math.Max(1, real[^1].Ms - first);
        return [.. real.Select(p => new PointF(box.Left + (p.Ms - first) / (float)span * box.Width,
            box.Bottom - 2 - (p.Value - bottom) / (top - bottom) * (box.Height - 4)))];
    }

    /// <summary>Pointing at a graph: the reading there and how long ago, in a box over it.</summary>
    private void DrawHover(Graphics g)
    {
        foreach (var (box, b) in _graphs)
        {
            if (!box.Contains(_mouse)) continue;
            var real = b.History.Where(p => float.IsFinite(p.Value)).ToList();
            if (real.Count < 2) return;
            long first = real[0].Ms, last = real[^1].Ms;
            long at = first + (long)((_mouse.X - box.Left) / box.Width * (last - first));
            var near = real.MinBy(p => Math.Abs(p.Ms - at));
            long ago = (last - near.Ms) / 1000;
            string when = ago < 1 ? Lang.T("now") : ago < 60 ? Lang.T($"{ago}s ago") : Lang.T($"{ago / 60}m {ago % 60:00}s ago");
            string text = $"{Units.StripText(b.Kind, near.Value)} · {when}";
            using var font = F(11.5f);
            float tw = Measure(g, text, font) + 14 * _s, th = 20 * _s;
            float tx = Math.Clamp(_mouse.X - tw / 2, 2, Width - tw - 2), ty = box.Top - th - 2 * _s;
            var pts = Points(box, b);
            var mark = pts.MinBy(p => Math.Abs(p.X - _mouse.X));
            using (var pen = new Pen(Blend(Ink, DarkMenuRenderer.Bg, 0.35f), Math.Max(1, _s))) g.DrawLine(pen, mark.X, box.Top, mark.X, box.Bottom);
            using (var bg = new SolidBrush(Blend(Ink, DarkMenuRenderer.Bg, 0.16f)))
            using (var shape = Rounded(new RectangleF(tx, ty, tw, th), 6 * _s)) g.FillPath(bg, shape);
            TextRenderer.DrawText(g, text, font, Rectangle.Round(new RectangleF(tx, ty, tw, th)), Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }
    }

    private void Divider(Graphics g, float x, float y, float w)
    {
        using var pen = new Pen(Line, Math.Max(1, _s));
        g.DrawLine(pen, x, y, x + w, y);
    }

    private static float Measure(Graphics g, string text, Font font) =>
        TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

    private static string Eights(string text) => string.Concat(text.Select(c => char.IsAsciiDigit(c) ? '8' : c));

    private static void Write(Graphics g, string text, Font font, Color color, float x, float y, float width) =>
        TextRenderer.DrawText(g, text, font, new Rectangle((int)x, (int)y, Math.Max(1, (int)width), font.Height + 2), color,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);

    private static void WriteRight(Graphics g, string text, Font font, Color color, float right, float y, float width) =>
        TextRenderer.DrawText(g, text, font, new Rectangle((int)(right - width), (int)y, Math.Max(1, (int)width), font.Height + 2), color,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.Right);

    private static GraphicsPath Rounded(RectangleF r, float radius)
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

    /// <summary>Windows 11's rounded corners for the card (Windows 10 keeps square ones).</summary>
    private void Round()
    {
        int round = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)); }
        catch { }
    }

    private static void Win32Foreground(IntPtr handle)
    {
        try { SetForegroundWindow(handle); }
        catch { }
    }

    private const int WS_EX_TOOLWINDOW = 0x00000080, CS_DROPSHADOW = 0x00020000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}

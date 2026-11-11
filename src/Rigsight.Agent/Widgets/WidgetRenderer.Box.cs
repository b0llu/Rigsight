using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using Rigsight.Core;
using Rigsight.Core.Settings;

#if RIGSIGHT_APP
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

/// <summary>
/// A widget as a box of tiles (see <see cref="WidgetTiles"/>): each tile drawn in its own place on the box's grid, the
/// panel behind them all or behind each one, the look the person picked. The approved mock-up is 8Bfo1Ce7KFJs4g1wxHQP1U.
/// </summary>
internal static partial class WidgetRenderer
{
    // ── The app's own look, for widgets on "Like RigSight" ───────────────────

    private static (string? Theme, string? Dark, string? Light, string? Accent) _appLook = ("dark", "dark", "light", AccentColors.Mono);

    /// <summary>The app's theme and accent (from the settings), for widgets on the "Like RigSight" theme.</summary>
    internal static void UseAppLook(string? theme, string? darkTheme, string? lightTheme, string? accent) =>
        _appLook = (theme, darkTheme, lightTheme, accent);

    private static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

    /// <summary>The app theme in use, as the app works it out ("system" follows Windows' mode).</summary>
    private static ThemeCatalog.Theme AppTheme()
    {
        var (theme, dark, light, _) = _appLook;
        string key = theme == ThemeCatalog.System ? WindowsUsesLight() ? light ?? ThemeCatalog.DefaultLight : dark ?? ThemeCatalog.DefaultDark : theme ?? ThemeCatalog.DefaultDark;
        return ThemeCatalog.Find(key) ?? ThemeCatalog.Find(ThemeCatalog.DefaultDark)!;
    }

    private static Palette AppPalette()
    {
        var theme = AppTheme();
        if (!ThemePalettes.ByFile.TryGetValue(theme.File, out var c)) return DarkPalette;
        var accent = AccentColors.Of(_appLook.Accent, theme.IsLight) is { } a ? Color.FromArgb(a.R, a.G, a.B) : Hex(c.Accent);
        return new Palette(Hex(c.Surface), Hex(c.Stroke), Hex(c.Text), Hex(c.Muted), Hex(c.Faint), Hex(c.Surface2), theme.IsLight)
        {
            CpuColor = Hex(c.Cpu), GpuColor = Hex(c.Gpu), AccentColor = accent,
        };
    }

    /// <summary>What marks things on a box (today on a calendar, a clock's second hand): the widget's own pick, else its theme's.</summary>
    private static Color BoxAccent(WidgetConfig cfg, Palette p) =>
        Gray ? p.Text : cfg.Accent is { } hex ? Hex(hex) : p.AccentColor ?? p.Text;

    // ── The box ───────────────────────────────────────────────────────────

    /// <summary>The room between tiles, in pixels at size M.</summary>
    private static float GapOf(WidgetConfig cfg) => cfg.Gap switch
    {
        WidgetGap.Small => 4,
        WidgetGap.Normal => 8,
        WidgetGap.Large => 14,
        _ => 0,
    };

    /// <summary>The panel's corner radius, in the widget's own pixels (the window puts its close badge on that curve).</summary>
    internal static float CornerRadius(WidgetConfig cfg, float height) =>
        cfg.Tiles is not null ? RadiusOf(cfg, height) : WidgetCatalog.LayoutOf(cfg) == WidgetLayout.Bar ? (height - 1) / 2 : 14;

    private static float RadiusOf(WidgetConfig cfg, float height) => cfg.Corners switch
    {
        WidgetCorners.Square => 0,
        WidgetCorners.Slight => 6,
        _ => height <= WidgetTiles.CellHeight + WidgetTiles.Edge * 2 + 0.5f ? height / 2 : 14,
    };

    /// <summary>
    /// A tile's place on the box: where the grid says, inside the panel's edge.
    /// </summary>
    private static RectangleF TileRect(WidgetTile t, WidgetConfig cfg)
    {
        float gap = GapOf(cfg);
        return new RectangleF(WidgetTiles.Edge + t.X * WidgetTiles.CellWidth + gap / 2, WidgetTiles.Edge + t.Y * WidgetTiles.CellHeight + gap / 2,
            t.W * WidgetTiles.CellWidth - gap, t.H * WidgetTiles.CellHeight - gap);
    }

    /// <summary>The panel: one behind every tile, or one for each.</summary>
    private static GraphicsPath BoxPanel(WidgetConfig cfg, float w, float h)
    {
        if (!cfg.TileBoxes || cfg.Tiles is not { Count: > 0 } tiles) return RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), RadiusOf(cfg, h));
        var path = new GraphicsPath(FillMode.Winding);
        foreach (var t in tiles)
        {
            var r = TileRect(t, cfg);
            using var one = RoundRect(r, RadiusOf(cfg, r.Height + WidgetTiles.Edge * 2));
            path.AddPath(one, false);
        }
        return path;
    }

    private static void DrawBox(Graphics g, WidgetConfig cfg, WidgetData d, Palette p)
    {
        var accent = BoxAccent(cfg, p);
        var tiles = cfg.Tiles ?? [];
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            _tileIndex = i;
            var r = TileRect(t, cfg);
            if (cfg.TileLines && !cfg.TileBoxes)
            {
                using var line = new Pen(p.Track, 1);
                if (t.X > 0) g.DrawLine(line, r.Left, r.Top + 6, r.Left, r.Bottom - 6);
                if (t.Y > 0) g.DrawLine(line, r.Left + 6, r.Top, r.Right - 6, r.Top);
            }
            var state = g.Save();
            g.SetClip(r);
            try { DrawTile(g, t, r, d, p, accent); }
            finally { g.Restore(state); }
        }
    }

    /// <summary>Inside a tile: 10 px of room at the sides, 8 above and below (none above and below on a tile one row high).</summary>
    private static RectangleF Inner(WidgetTile t, RectangleF r) => t.H == 1 ? RectangleF.Inflate(r, -10, 0) : RectangleF.Inflate(r, -10, -8);

    private static void DrawTile(Graphics g, WidgetTile t, RectangleF r, WidgetData d, Palette p, Color accent)
    {
        var align = t.Align ?? WidgetTiles.Kind(t.Kind)?.Align ?? TileAlign.Left;
        var inner = Inner(t, r);
        switch (t.Kind)
        {
            case WidgetTiles.Clock: ClockTile(g, t, inner, p, accent, align); break;
            case WidgetTiles.Date: DateTile(g, t, inner, p, accent, align); break;
            case WidgetTiles.Calendar: CalendarTile(g, inner, p, accent); break;
            case WidgetTiles.Reading: ReadingTile(g, t, inner, d, p, align); break;
            case WidgetTiles.Line: LineTile(g, t, inner, d, p); break;
            case WidgetTiles.Graph:
                var readings = (t.Items ?? []).Select(i => Read(i, d, p)).ToList();
                Within(g, r, r.Width, r.Height, () => DrawGraph(g, readings, d, p, r.Width, r.Height));
                break;
            case WidgetTiles.Fps:
                Within(g, r, 184, 100, () => DrawTiles(g, [Read(new WidgetItem { Id = nameof(OverlayMetric.Fps) }, d, p)], d, p, 184, ""));
                break;
            case WidgetTiles.Today: Within(g, r, 256, 140, () => DrawToday(g, d, p)); break;
            case WidgetTiles.NowPlaying: Within(g, r, 310, 88, () => DrawNowPlaying(g, d, p, 310, 88)); break;
            case WidgetTiles.Notes: NotesTile(g, t, inner, p, accent); break;
            case WidgetTiles.Todo: TodoTile(g, t, inner, p, accent); break;
            case WidgetTiles.Timer: TimerTile(g, t, inner, p, accent); break;
            case WidgetTiles.Media: MediaTile(g, inner, d, p, accent); break;
            case WidgetTiles.Weather: WeatherTile(g, t, inner, p, accent); break;
            case WidgetTiles.Shortcuts: ShortcutsTile(g, t, inner, p); break;
            case WidgetTiles.Photos: PhotosTile(g, t, r, p); break;
        }
    }

    /// <summary>A drawing made for a size of its own, in the middle of a tile, made smaller where the tile is.</summary>
    private static void Within(Graphics g, RectangleF r, float w, float h, Action draw)
    {
        float s = Math.Min(1, Math.Min(r.Width / w, r.Height / h));
        var state = g.Save();
        g.TranslateTransform(r.Left + (r.Width - w * s) / 2, r.Top + (r.Height - h * s) / 2);
        g.ScaleTransform(s, s);
        try { draw(); }
        finally { g.Restore(state); }
    }

    // ── Ink: what is really drawn, so a tile can be centred by it ─────────────

    private static readonly Dictionary<(string Text, string Font, float Px, FontStyle Style, bool Semibold, float Scale), RectangleF> InkCache = [];

    /// <summary>
    /// Where a text's ink lies against the point it's drawn at, read off the text drawn once on its own at the same size and
    /// scale: a line box carries room above and below the letters, and Windows fits small letters to whole pixels, so
    /// neither the line box nor the glyphs' outline says where the ink really is (both left text a few pixels off the
    /// middle when measured, 2026-11-10).
    /// </summary>
    private static RectangleF InkOf(Graphics g, string text, float px, FontStyle style, bool semibold)
    {
        if (text.Trim().Length == 0) return RectangleF.Empty;
        float scale = g.Transform.Elements[0] is > 0 and var s0 ? s0 : 1;
        var key = (text, FontName ?? "", px, style, semibold, scale);
        if (InkCache.TryGetValue(key, out var ink)) return ink;
        if (InkCache.Count > 600) InkCache.Clear();
        var font = MakeFont(px, style, semibold);
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        int pad = (int)Math.Ceiling(px * scale);
        int bw = (int)Math.Ceiling((Measure(g, text, px, style, semibold) + px) * scale) + pad * 2, bh = (int)Math.Ceiling(px * 2 * scale) + pad * 2;
        using var bmp = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var gg = Graphics.FromImage(bmp))
        {
            gg.Clear(Color.Black);
            gg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gg.ScaleTransform(scale, scale);
            gg.DrawString(text, font, Brushes.White, pad / scale, pad / scale, fmt);
        }
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        var data = bmp.LockBits(new Rectangle(0, 0, bw, bh), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[bw];
            for (int y = 0; y < bh; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, bw);
                for (int x = 0; x < bw; x++)
                    if ((row[x] >> 8 & 0xFF) > 60) (l, t, r, b) = (Math.Min(l, x), Math.Min(t, y), Math.Max(r, x), Math.Max(b, y));
            }
        }
        finally { bmp.UnlockBits(data); }
        ink = r < 0 ? RectangleF.Empty : RectangleF.FromLTRB((l - pad) / scale, (t - pad) / scale, (r + 1 - pad) / scale, (b + 1 - pad) / scale);
        InkCache[key] = ink;
        return ink;
    }

    /// <summary>Parts of a tile laid out against each other, then moved as one so their ink sits where the tile wants it.</summary>
    private sealed class Ink(Graphics measure)
    {
        private readonly List<(RectangleF Box, Action<Graphics, float, float> Draw)> _parts = [];

        public void Text(string text, float px, FontStyle style, Color color, float x, float y, bool semibold = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            // On a whole screen pixel, as its ink was measured: Windows fits small letters to the pixel they start on.
            float device = measure.Transform.Elements[0] is > 0 and var d0 ? d0 : 1;
            (x, y) = (MathF.Round(x * device) / device, MathF.Round(y * device) / device);
            var ink = InkOf(measure, text, px, style, semibold);
            if (ink.IsEmpty) return;
            ink.Offset(x, y);
            _parts.Add((ink, (g, dx, dy) => WidgetRenderer.Text(g, text, px, style, color, x + dx, y + dy, semibold)));
        }

        public void Shape(RectangleF box, Action<Graphics, float, float> draw) => _parts.Add((box, draw));

        public RectangleF Box => _parts.Count == 0 ? RectangleF.Empty : _parts.Select(p => p.Box).Aggregate(RectangleF.Union);

        /// <summary>Draws it in <paramref name="r"/>: across by <paramref name="align"/>, in the middle from top to bottom (or at the top).</summary>
        public void Draw(Graphics g, RectangleF r, TileAlign align, bool top = false)
        {
            var box = Box;
            if (box.IsEmpty) return;
            float dx = align switch
            {
                TileAlign.Left => r.Left - box.Left,
                TileAlign.Right => r.Right - box.Right,
                _ => r.Left + (r.Width - box.Width) / 2 - box.Left,
            };
            float dy = top ? r.Top - box.Top : r.Top + (r.Height - box.Height) / 2 - box.Top;
            // On whole screen pixels, so the same tile comes out the same at every place.
            float device = g.Transform.Elements[0];
            if (device > 0) (dx, dy) = (MathF.Round(dx * device) / device, MathF.Round(dy * device) / device);
            foreach (var (_, draw) in _parts) draw(g, dx, dy);
        }
    }

    // ── Clock, date, calendar ─────────────────────────────────────────────

    /// <summary>The time the clocks show (a test sets its own).</summary>
    internal static Func<DateTime> Now = () => DateTime.Now;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    private static bool Uses24(WidgetTile t) => t.Hours24 ?? Culture.DateTimeFormat.ShortTimePattern.Contains('H');

    /// <summary>The time as digits ("9:14" or "21:14", with seconds when asked) and its AM or PM ("" in 24 hours).</summary>
    private static (string Time, string Half) TimeText(WidgetTile t, DateTime now)
    {
        bool h24 = Uses24(t);
        int hour = h24 ? now.Hour : now.Hour % 12 == 0 ? 12 : now.Hour % 12;
        string time = (h24 ? hour.ToString("00", CultureInfo.InvariantCulture) : hour.ToString(CultureInfo.InvariantCulture))
                      + ":" + now.Minute.ToString("00", CultureInfo.InvariantCulture)
                      + (t.Seconds ? ":" + now.Second.ToString("00", CultureInfo.InvariantCulture) : "");
        string half = h24 ? "" : now.Hour < 12 ? Culture.DateTimeFormat.AMDesignator : Culture.DateTimeFormat.PMDesignator;
        return (time, half.Length > 0 ? half : now.Hour < 12 ? "AM" : "PM");
    }

    private static string DayName(DateTime d) => Culture.TextInfo.ToTitleCase(d.ToString("dddd", Culture));
    private static string DayMonth(DateTime d) => d.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture);

    private static void ClockTile(Graphics g, WidgetTile t, RectangleF r, Palette p, Color accent, TileAlign align)
    {
        var now = Now();
        var (time, half) = TimeText(t, now);
        string style = t.Style ?? WidgetTiles.ClockStyles[0];
        var ink = new Ink(g);

        if (t.H == 1)
        {
            // One row high: the time and, where there's room, the day.
            ink.Text(time + (half.Length > 0 ? " " + half : ""), 14, FontStyle.Bold, p.Text, 0, 0, semibold: true);
            if (t.ShowDate && r.Width >= 150) ink.Text(now.ToString("ddd d MMM", Culture), 12.5f, FontStyle.Regular, p.Muted, ink.Box.Right + 8, 1.5f);
            ink.Draw(g, r, align);
            return;
        }

        switch (style)
        {
            case "Analog":
            {
                float s = Math.Min(r.Width, r.Height);
                ink.Shape(new RectangleF(0, 0, s, s), (gg, dx, dy) => AnalogFace(gg, new RectangleF(dx, dy, s, s), now, t.Seconds, p, accent));
                if (t.ShowDate && r.Width - s >= 86)
                {
                    ink.Text(DayName(now), 13, FontStyle.Bold, p.Text, s + 12, s / 2 - 17, semibold: true);
                    ink.Text(DayMonth(now), 12.5f, FontStyle.Regular, p.Muted, s + 12, s / 2 + 2);
                }
                break;
            }
            case "Flip":
            {
                float fh = Math.Min(r.Height, Math.Min((r.Width - 14) / 3.1f, 56)), fw = MathF.Round(fh * 0.72f), x = 0;
                // Two cards for the hour and two for the minutes, as a flip clock has ("09 14").
                int hour = Uses24(t) ? now.Hour : now.Hour % 12 == 0 ? 12 : now.Hour % 12;
                string digits = hour.ToString("00", CultureInfo.InvariantCulture) + now.Minute.ToString("00", CultureInfo.InvariantCulture);
                for (int i = 0; i < 4; i++)
                {
                    float cx = x;
                    string digit = digits[i].ToString();
                    string tag = i == 0 ? half : "";
                    ink.Shape(new RectangleF(cx, 0, fw, fh), (gg, dx, dy) => FlipCard(gg, new RectangleF(cx + dx, dy, fw, fh), digit, tag, p));
                    x += fw + (i == 1 ? 10 : 4);
                }
                break;
            }
            case "In words":
            {
                float px = r.Width >= 200 ? 19 : 15;
                ink.Text(TimeInWords(now), px, FontStyle.Bold, p.Text, 0, 0, semibold: true);
                if (t.ShowDate) ink.Text(DayName(now) + ", " + DayMonth(now), 12.5f, FontStyle.Regular, p.Muted, 0, px * 1.15f + 3);
                break;
            }
            case "Day and time":
            {
                float px = Math.Clamp(r.Width / 7.4f, 13, 30);
                string day = DayName(now).ToUpper(Culture);
                // Letter by letter, with room between: the spaced capitals of a desk clock.
                float x = 0, spacing = px * 0.22f;
                foreach (char c in day)
                {
                    string ch = c.ToString();
                    ink.Text(ch, px, FontStyle.Bold, p.Text, x, 0, semibold: true);
                    x += Measure(g, ch, px, FontStyle.Bold, semibold: true) + spacing;
                }
                ink.Text(DayMonth(now) + "  ·  " + time + (half.Length > 0 ? " " + half : ""), 12.5f, FontStyle.Regular, p.Muted, 0, px * 1.1f + 4);
                break;
            }
            default:
            {
                float px = Math.Clamp(Math.Min(r.Height * 0.62f, r.Width / 5), 18, 56);
                ink.Text(time, px, FontStyle.Bold, p.Text, 0, 0, semibold: true);
                float after = Measure(g, time, px, FontStyle.Bold, semibold: true) + px * 0.12f;
                // AM or PM sits on the time's baseline, smaller and muted.
                if (half.Length > 0) ink.Text(half, px * 0.42f, FontStyle.Regular, p.Muted, after, px * 0.58f * 1.0f);
                if (t.ShowDate && r.Width >= 200 && r.Height < 70)
                {
                    float x = ink.Box.Right + 14;
                    ink.Text(DayName(now), 12.5f, FontStyle.Regular, p.Muted, x, px * 0.12f);
                    ink.Text(DayMonth(now), 12.5f, FontStyle.Regular, p.Muted, x, px * 0.12f + 16);
                }
                else if (t.ShowDate && r.Height >= 44)
                    ink.Text(DayName(now) + ", " + DayMonth(now), 12.5f, FontStyle.Regular, p.Muted, 0, px * 1.05f + 4);
                break;
            }
        }
        ink.Draw(g, r, align);
    }

    private static void AnalogFace(Graphics g, RectangleF r, DateTime now, bool seconds, Palette p, Color accent)
    {
        float c = r.Width / 2, cx = r.Left + c, cy = r.Top + c;
        using (var ring = new Pen(p.Track, 1.5f)) g.DrawEllipse(ring, r.Left + 1, r.Top + 1, r.Width - 2, r.Height - 2);
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6;
            float r1 = c - 3, r2 = c - (i % 3 == 0 ? 9 : 6);
            using var tick = new Pen(i % 3 == 0 ? p.Muted : p.Faint, i % 3 == 0 ? 1.6f : 1f);
            g.DrawLine(tick, cx + (float)Math.Sin(a) * r1, cy - (float)Math.Cos(a) * r1, cx + (float)Math.Sin(a) * r2, cy - (float)Math.Cos(a) * r2);
        }
        void Hand(double degrees, float length, float width, Color color)
        {
            double a = degrees * Math.PI / 180;
            using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, cx, cy, cx + (float)Math.Sin(a) * length, cy - (float)Math.Cos(a) * length);
        }
        Hand((now.Hour % 12 + now.Minute / 60.0) * 30, c * 0.5f, 3, p.Text);
        Hand((now.Minute + now.Second / 60.0) * 6, c * 0.72f, 2.2f, p.Text);
        if (seconds) Hand(now.Second * 6, c * 0.8f, 1, accent);
        using var dot = new SolidBrush(accent);
        g.FillEllipse(dot, cx - 2.6f, cy - 2.6f, 5.2f, 5.2f);
    }

    private static void FlipCard(Graphics g, RectangleF r, string digit, string tag, Palette p)
    {
        using (var card = new SolidBrush(p.Track))
        using (var path = RoundRect(r, 6)) g.FillPath(card, path);
        float px = r.Height * 0.62f;
        var ink = InkOf(g, digit, px, FontStyle.Bold, true);
        Text(g, digit, px, FontStyle.Bold, p.Text, r.Left + (r.Width - ink.Width) / 2 - ink.Left, r.Top + (r.Height - ink.Height) / 2 - ink.Top, semibold: true);
        using (var split = new Pen(p.Bg, 1)) g.DrawLine(split, r.Left, r.Top + r.Height / 2, r.Right, r.Top + r.Height / 2);
        if (tag.Length > 0) Text(g, tag, Math.Max(7, r.Height * 0.14f), FontStyle.Bold, p.Muted, r.Left + 4, r.Top + 3, semibold: true);
    }

    /// <summary>The time in words, to the nearest five minutes ("Quarter past nine"); each sentence whole, for translation.</summary>
    internal static string TimeInWords(DateTime now)
    {
        int minutes = (int)Math.Round(now.Minute / 5.0) * 5;
        int hour = now.Hour % 12, next = (hour + 1) % 12;
        if (minutes == 60) { minutes = 0; hour = next; next = (hour + 1) % 12; }
        string h = HourWord(hour), n = HourWord(next);
        string words = minutes switch
        {
            0 => Lang.T($"{h} o'clock"),
            5 => Lang.T($"Five past {h}"),
            10 => Lang.T($"Ten past {h}"),
            15 => Lang.T($"Quarter past {h}"),
            20 => Lang.T($"Twenty past {h}"),
            25 => Lang.T($"Twenty-five past {h}"),
            30 => Lang.T($"Half past {h}"),
            35 => Lang.T($"Twenty-five to {n}"),
            40 => Lang.T($"Twenty to {n}"),
            45 => Lang.T($"Quarter to {n}"),
            50 => Lang.T($"Ten to {n}"),
            _ => Lang.T($"Five to {n}"),
        };
        return words.Length > 0 ? char.ToUpper(words[0], Culture) + words[1..] : words;
    }

    /// <summary>An hour as a word, as it goes into a time said in words (a language pack may give it its article).</summary>
    private static string HourWord(int hour) => hour switch
    {
        1 => Lang.T("one (the hour)"),
        2 => Lang.T("two (the hour)"),
        3 => Lang.T("three (the hour)"),
        4 => Lang.T("four (the hour)"),
        5 => Lang.T("five (the hour)"),
        6 => Lang.T("six (the hour)"),
        7 => Lang.T("seven (the hour)"),
        8 => Lang.T("eight (the hour)"),
        9 => Lang.T("nine (the hour)"),
        10 => Lang.T("ten (the hour)"),
        11 => Lang.T("eleven (the hour)"),
        _ => Lang.T("twelve (the hour)"),
    } is var w && w.EndsWith(" (the hour)", StringComparison.Ordinal) ? w[..^" (the hour)".Length] : w;

    private static void DateTile(Graphics g, WidgetTile t, RectangleF r, Palette p, Color accent, TileAlign align)
    {
        var now = Now();
        var ink = new Ink(g);
        if (t.H == 1)
        {
            ink.Text(now.ToString("ddd d MMM", Culture), 14, FontStyle.Bold, p.Text, 0, 0, semibold: true);
            ink.Draw(g, r, align);
            return;
        }
        float px = Math.Clamp(r.Height * 0.7f, 22, 56);
        string day = now.Day.ToString(Culture);
        ink.Text(day, px, FontStyle.Bold, p.Text, 0, 0, semibold: true);
        float x = Measure(g, day, px, FontStyle.Bold, semibold: true) + 8;
        var dayInk = InkOf(g, day, px, FontStyle.Bold, true);
        float mid = dayInk.Top + dayInk.Height / 2;
        ink.Text(DayName(now).ToUpper(Culture), 10.5f, FontStyle.Bold, Gray ? p.Muted : accent, x, mid - 15);
        ink.Text(now.ToString("MMMM", Culture), 12.5f, FontStyle.Regular, p.Muted, x, mid + 1);
        ink.Draw(g, r, align);
    }

    private static void CalendarTile(Graphics g, RectangleF r, Palette p, Color accent)
    {
        var now = Now();
        var first = new DateTime(now.Year, now.Month, 1);
        var dtf = Culture.DateTimeFormat;
        int start = (int)dtf.FirstDayOfWeek, lead = ((int)first.DayOfWeek - start + 7) % 7, days = DateTime.DaysInMonth(now.Year, now.Month);
        Text(g, Culture.TextInfo.ToTitleCase(first.ToString("Y", Culture)), 12.5f, FontStyle.Bold, p.Text, r.Left + 3, r.Top, semibold: true, maxWidth: r.Width - 6);
        float top = r.Top + 22, cw = r.Width / 7, ch = Math.Max(14, (r.Bottom - top) / 7);
        for (int i = 0; i < 7; i++)
        {
            string name = dtf.GetShortestDayName((DayOfWeek)((start + i) % 7));
            name = name.Length > 0 ? name[..1].ToUpper(Culture) : "";
            TextCentered(g, name, 10, FontStyle.Regular, p.Faint, r.Left + cw * (i + 0.5f), top + (ch - 13) / 2);
        }
        using var mark = new SolidBrush(accent);
        for (int d = 1; d <= days; d++)
        {
            int cell = lead + d - 1, col = cell % 7, row = cell / 7 + 1;
            float cx = r.Left + cw * (col + 0.5f), cy = top + ch * row + ch / 2;
            string text = d.ToString(Culture);
            bool today = d == now.Day;
            if (today)
            {
                float s = Math.Min(cw, ch) - 1;
                g.FillEllipse(mark, cx - s / 2, cy - s / 2, s, s);
            }
            var ink = InkOf(g, text, 11.5f, today ? FontStyle.Bold : FontStyle.Regular, today);
            var color = today ? (p.Light || Gray ? Color.White : Color.FromArgb(17, 17, 17)) : p.Text;
            if (today && !Gray && Brightness(accent) < 0.6f) color = Color.White;
            Text(g, text, 11.5f, today ? FontStyle.Bold : FontStyle.Regular, color, cx - ink.Width / 2 - ink.Left, cy - ink.Height / 2 - ink.Top, semibold: today);
        }
    }

    private static float Brightness(Color c) => (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;

    // ── Readings ─────────────────────────────────────────────────────────

    private static Color PartColor(Reading r, Palette p) =>
        Gray ? p.Muted : r.Accent == Cpu ? p.CpuColor ?? Cpu : r.Accent == Gpu ? p.GpuColor ?? Gpu : r.Accent;

    /// <summary>The room under a big ring for its load line.</summary>
    private const float LoadRoom = 20;

    private static void ReadingTile(Graphics g, WidgetTile t, RectangleF r, WidgetData d, Palette p, TileAlign align)
    {
        var item = new WidgetItem { Id = t.Item ?? nameof(OverlayMetric.CpuTemp), Label = t.Label };
        var reading = Read(item, d, p);
        var part = PartColor(reading, p);
        string look = t.Look ?? WidgetTiles.Number;
        float frac = reading.Value is double v ? (float)Math.Clamp(v / 100, 0, 1) : 0;
        var full = reading.Kind == SensorKind.Temperature ? reading.ValueColor : Gray ? p.Text : part;
        var ink = new Ink(g);

        if (t.H == 1)
        {
            ink.Shape(new RectangleF(0, 4, 7, 7), (gg, dx, dy) => { using var b = new SolidBrush(part); gg.FillEllipse(b, dx, 4 + dy, 7, 7); });
            ink.Text(reading.Label, 12, FontStyle.Bold, p.Muted, 13, 0, semibold: true);
            ink.Text(reading.Text, 14, FontStyle.Regular, reading.ValueColor, 13 + Measure(g, reading.Label, 12, FontStyle.Bold, true) + 6, -1.5f);
            ink.Draw(g, r, align);
            return;
        }

        if (look == WidgetTiles.Ring)
        {
            if (r.Height >= 72)
            {
                // A big ring with the value and name inside it; a CPU's or GPU's temperature with its load under it, as
                // the ready-made Gauges widget drew it (the user, 2026-11-10: the load went once the widget became tiles).
                string? load = reading.Metric switch
                {
                    OverlayMetric.CpuTemp => Lang.T($"load {Units.Short(SensorKind.Load, d.CpuLoad)}"),
                    OverlayMetric.GpuTemp => Lang.T($"load {Units.Short(SensorKind.Load, d.GpuLoad)}"),
                    _ => null,
                };
                if (load is not null && r.Height < 72 + LoadRoom) load = null;
                float s = Math.Min(r.Width, r.Height - (load is null ? 0 : LoadRoom)), sw = Math.Max(5, MathF.Round(s / 12));
                if (load is not null)
                {
                    var loadInk = InkOf(g, load, 10.5f, FontStyle.Regular, false);
                    ink.Text(load, 10.5f, FontStyle.Regular, p.Muted, (s - loadInk.Width) / 2 - loadInk.Left, s + 6);
                }
                ink.Shape(new RectangleF(0, 0, s, s), (gg, dx, dy) =>
                {
                    Ring(gg, new RectangleF(dx, dy, s, s), sw, frac, full, p);
                    float vpx = MathF.Round(s / 4.3f);
                    var vi = InkOf(g, reading.Text, vpx, FontStyle.Regular, false);
                    var li = InkOf(g, reading.Label, 10.5f, FontStyle.Bold, true);
                    float block = vi.Height + 5 + li.Height, top = dy + (s - block) / 2;
                    Text(gg, reading.Text, vpx, FontStyle.Regular, reading.ValueColor, dx + (s - vi.Width) / 2 - vi.Left, top - vi.Top);
                    Text(gg, reading.Label, 10.5f, FontStyle.Bold, part, dx + (s - li.Width) / 2 - li.Left, top + vi.Height + 5 - li.Top, semibold: true);
                });
            }
            else
            {
                float s = Math.Min(r.Height, 40);
                ink.Shape(new RectangleF(0, 0, s, s), (gg, dx, dy) => Ring(gg, new RectangleF(dx, dy, s, s), 5, frac, full, p));
                ink.Text(reading.Label, 10.5f, FontStyle.Bold, part, s + 8, s / 2 - 17, semibold: true);
                ink.Text(reading.Text, 18, FontStyle.Regular, reading.ValueColor, s + 8, s / 2 - 4);
            }
            ink.Draw(g, r, align);
            return;
        }

        float valuePx = look == WidgetTiles.Big ? Math.Clamp(Math.Min(r.Height - 16, r.Width / Math.Max(3, reading.Text.Length * 0.6f)), 20, 60) : 19;
        ink.Text(reading.Label, 10.5f, FontStyle.Bold, part, 0, 0, semibold: true);
        ink.Text(reading.Text, valuePx, FontStyle.Regular, reading.ValueColor, -1, 15);
        float under = 15 + valuePx * 1.25f + 4;
        if (look == WidgetTiles.LoadAndPower && Detail(reading, d) is { } detail)
        {
            ink.Text(detail, 11.5f, FontStyle.Regular, p.Muted, 0, under);
            under += 20;
        }
        bool graph = look is WidgetTiles.LineGraph or WidgetTiles.FilledGraph or WidgetTiles.BarGraph || look == WidgetTiles.LoadAndPower;
        float room = r.Height - under;
        if (look == WidgetTiles.Level && room >= 5)
            ink.Shape(new RectangleF(0, under + 4, r.Width, 5), (gg, dx, dy) => Bar(gg, new RectangleF(dx, under + 4 + dy, r.Width, 5), frac, full, p));
        else if (look == WidgetTiles.Blocks && room >= 6)
            ink.Shape(new RectangleF(0, under + 4, r.Width, 6), (gg, dx, dy) => Blocks(gg, new RectangleF(dx, under + 4 + dy, r.Width, 6), frac, full, p));
        else if (graph && room >= 10 && History(reading, d) is { Length: > 1 } history)
        {
            float gh = Math.Min(room, 40);
            ink.Shape(new RectangleF(0, under, r.Width, gh), (gg, dx, dy) =>
            {
                var area = new RectangleF(dx, under + dy, r.Width, gh);
                var color = Gray ? p.Text : part;
                if (look == WidgetTiles.BarGraph) Bars(gg, history, area, color);
                else if (look == WidgetTiles.LineGraph) Line(gg, history, area, color);
                else Sparkline(gg, history, area, color);
            });
        }
        ink.Draw(g, r, align);
    }

    private static void Ring(Graphics g, RectangleF r, float width, float frac, Color color, Palette p)
    {
        var arc = RectangleF.Inflate(r, -width / 2 - 1, -width / 2 - 1);
        using (var track = new Pen(p.Track, width) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(track, arc, 135, 270);
        if (frac <= 0.005f) return;
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, arc, 135, Math.Max(2, 270 * frac));
    }

    private static void Bar(Graphics g, RectangleF r, float frac, Color color, Palette p)
    {
        using (var track = new SolidBrush(p.Track))
        using (var path = RoundRect(r, r.Height / 2)) g.FillPath(track, path);
        if (frac <= 0.005f) return;
        using var fill = new SolidBrush(color);
        using var done = RoundRect(new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * frac), r.Height), r.Height / 2);
        g.FillPath(fill, done);
    }

    private static void Blocks(Graphics g, RectangleF r, float frac, Color color, Palette p)
    {
        const int n = 10;
        float gap = 2, bw = (r.Width - gap * (n - 1)) / n;
        using var on = new SolidBrush(color);
        using var off = new SolidBrush(p.Track);
        for (int i = 0; i < n; i++)
        {
            using var path = RoundRect(new RectangleF(r.X + i * (bw + gap), r.Y, bw, r.Height), 1.5f);
            g.FillPath(i < MathF.Round(frac * n) ? on : off, path);
        }
    }

    private static void Line(Graphics g, float[] values, RectangleF r, Color color)
    {
        var pts = Points(values, r);
        if (pts.Length < 2) return;
        using var pen = new Pen(color, 1.5f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    private static void Bars(Graphics g, float[] values, RectangleF r, Color color)
    {
        var finite = values.Where(float.IsFinite).ToArray();
        if (finite.Length < 2) return;
        int n = Math.Max(8, (int)(r.Width / 5));
        var last = finite.Length > n ? finite[^n..] : finite;
        float lo = last.Min() * 0.9f, hi = Math.Max(last.Max(), lo + 1), bw = r.Width / last.Length;
        using var b = new SolidBrush(color);
        for (int i = 0; i < last.Length; i++)
        {
            float h = Math.Max(1, (last[i] - lo) / (hi - lo) * r.Height);
            g.FillRectangle(b, r.X + i * bw + 0.5f, r.Bottom - h, Math.Max(1, bw - 1), h);
        }
    }

    private static PointF[] Points(float[] values, RectangleF r)
    {
        var finite = values.Where(float.IsFinite).ToList();
        if (finite.Count < 2) return [];
        float lo = finite.Min(), hi = finite.Max();
        if (hi - lo < 1) { lo -= 0.5f; hi += 0.5f; }
        var pts = new List<PointF>();
        for (int i = 0; i < values.Length; i++)
            if (float.IsFinite(values[i])) pts.Add(new PointF(r.Left + r.Width * i / (values.Length - 1), r.Bottom - (values[i] - lo) / (hi - lo) * r.Height));
        return [.. pts];
    }

    /// <summary>A line of readings, spread across the tile: dot, name, value for each.</summary>
    private static void LineTile(Graphics g, WidgetTile t, RectangleF r, WidgetData d, Palette p)
    {
        var readings = (t.Items ?? []).Select(i => Read(i, d, p)).ToList();
        if (readings.Count == 0) return;
        ReadingRow(g, readings, p, rd => PartColor(rd, p)).Draw(g, r, TileAlign.Center);
    }

    /// <summary>
    /// Readings in one row, laid out as a web page's flex row with its items centred both ways (the user, 2026-11-10:
    /// "display flex align items center justify content center"): each reading's dot, name and value one after another
    /// at fixed gaps, every part centred on the same middle line by its own ink (not its line box), and the whole row
    /// then put in the middle of the space it's drawn in, with as much room on the left as on the right.
    /// </summary>
    private static Ink ReadingRow(Graphics g, List<Reading> readings, Palette? p, Func<Reading, Color> dot)
    {
        const float Dot = 6, DotGap = 6, NameGap = 5, Gap = 16;
        var row = new Ink(g);
        float x = 0;
        for (int i = 0; i < readings.Count; i++)
        {
            var rd = readings[i];
            if (i > 0) x += Gap;
            float at = x;
            var color = dot(rd);
            row.Shape(new RectangleF(at, -Dot / 2, Dot, Dot), (gg, dx, dy) =>
            {
                using var b = new SolidBrush(color);
                gg.FillEllipse(b, at + dx, -Dot / 2 + dy, Dot, Dot);
            });
            x += Dot + DotGap;
            var name = InkOf(g, rd.Label, 11, FontStyle.Bold, false);
            if (!name.IsEmpty)
            {
                row.Text(rd.Label, 11, FontStyle.Bold, p?.Muted ?? Color.Gray, x - name.Left, -(name.Top + name.Height / 2));
                x += name.Width + NameGap;
            }
            var value = InkOf(g, rd.Text, 13, FontStyle.Bold, true);
            if (!value.IsEmpty)
            {
                row.Text(rd.Text, 13, FontStyle.Bold, rd.ValueColor, x - value.Left, -(value.Top + value.Height / 2), semibold: true);
                x += value.Width;
            }
        }
        return row;
    }

    // ── Everyday: a note, a to-do list, a timer (approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U, part two) ──

    /// <summary>
    /// A place on a tile that does something when clicked on the desktop (a to-do's box, a timer's Start), in the
    /// widget's own pixels at size M: the window turns it into its own pixels and acts on a click there. Everywhere
    /// else on the widget a press still drags it.
    /// </summary>
    internal readonly record struct TileHit(int Tile, string Action, int Index, RectangleF Rect);

    internal const string HitNote = "note", HitTodo = "todo", HitTodoAdd = "todo-add", HitGo = "go", HitReset = "reset";

    /// <summary>The clickable places of the widget last drawn on this thread (see <see cref="Render"/>).</summary>
    [ThreadStatic] internal static List<TileHit>? Hits;
    [ThreadStatic] private static int _tileIndex;
    [ThreadStatic] private static string? _widgetId;

    /// <summary>
    /// What's being typed into a note or a to-do list on the desktop, drawn by the widget itself in its own font and
    /// colours with a caret (the user, 2026-11-11: a box laid over it didn't follow the widget's look, and an item added
    /// with Enter went behind it). Set by the agent while a tile takes the keyboard; the app never sets it.
    /// </summary>
    internal sealed record TypingNow(string WidgetId, int Tile, string Text, int Caret, bool Adding);

    internal static TypingNow? Typing;

    /// <summary>How far each note or to-do list is scrolled (rows of a list, lines of a note), by widget and tile; the agent's, kept while it runs.</summary>
    internal static readonly Dictionary<(string Widget, int Tile), int> Scrolls = [];

    /// <summary>The parts of the widget last drawn on this thread that scroll with the wheel, by tile.</summary>
    [ThreadStatic] internal static List<(int Tile, RectangleF Rect)>? ScrollAreas;

    private static TypingNow? TypingHere(bool adding) =>
        Typing is { } ty && ty.WidgetId == _widgetId && ty.Tile == _tileIndex && ty.Adding == adding ? ty : null;

    /// <summary>This tile's scroll, kept within 0 and <paramref name="max"/> (and saved back so the wheel never runs on past the end).</summary>
    private static int ScrollOf(int max)
    {
        var key = (_widgetId ?? "", _tileIndex);
        int at = Math.Clamp(Scrolls.GetValueOrDefault(key), 0, Math.Max(0, max));
        if (Scrolls.ContainsKey(key) || at != 0) Scrolls[key] = at;
        return at;
    }

    private static void SetScroll(int at) => Scrolls[(_widgetId ?? "", _tileIndex)] = at;

    // Once a tile, as a click's places are (a box can be drawn more than once in one picture).
    private static void AddScrollArea(RectangleF r)
    {
        if (ScrollAreas is null || ScrollAreas.Any(a => a.Tile == _tileIndex)) return;
        ScrollAreas.Add((_tileIndex, r));
    }

    /// <summary>A thin bar at a list's or a note's right edge, while it holds more than it shows.</summary>
    private static void ScrollBar(Graphics g, RectangleF track, int first, int shown, int total, Palette p)
    {
        if (total <= shown || track.Height < 12) return;
        float h = Math.Max(10, track.Height * shown / total), y = track.Y + (track.Height - h) * first / Math.Max(1, total - shown);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundRect(new RectangleF(track.Right - 3, y, 3, h), 1.5f))
        using (var fill = new SolidBrush(p.Faint))
            g.FillPath(fill, path);
        g.SmoothingMode = smooth;
    }

    private static readonly Dictionary<(string, float, FontStyle), float> TextMiddles = [];

    /// <summary>
    /// Where to draw a line of text so it sits in the middle of a row by its ink: halfway between the middle of its
    /// capitals and the middle of its small letters, measured on the font (the user, 2026-11-11: a to-do's text sat lower
    /// than its box).
    /// </summary>
    private static float TextTopFor(float centreY, float px, FontStyle style)
    {
        var font = MakeFont(px, style, false);
        var key = (font.FontFamily.Name, px, style);
        if (!TextMiddles.TryGetValue(key, out float middle))
        {
            float Mid(string s)
            {
                using var path = new GraphicsPath();
                path.AddString(s, font.FontFamily, (int)style, px, PointF.Empty, StringFormat.GenericTypographic);
                var b = path.GetBounds();
                return b.Top + b.Height / 2;
            }
            middle = (Mid("H") + Mid("x")) / 2;
            TextMiddles[key] = middle;
        }
        return centreY - middle;
    }

    private static void AddHit(string action, int index, RectangleF r)
    {
        if (Hits is null || Hits.Any(h => h.Tile == _tileIndex && h.Action == action && h.Index == index)) return;
        Hits.Add(new TileHit(_tileIndex, action, index, r));
    }

    private static long NowMs() => new DateTimeOffset(Now()).ToUnixTimeMilliseconds();

    /// <summary>Text over several lines in a box, cut at a word with "…" where it runs out; with the shadow other text has.</summary>
    private static void TextIn(Graphics g, string text, float px, Color color, RectangleF box, FontStyle style = FontStyle.Regular)
    {
        var font = MakeFont(px, style, false);
        using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit };
        if (TextShadow.A > 0)
        {
            float device = g.Transform.Elements[0];
            float o = Math.Max(1, MathF.Round(px / 16f * device)) / device;
            using var shadow = new SolidBrush(TextShadow);
            g.DrawString(text, font, shadow, new RectangleF(box.X + o, box.Y + o, box.Width, box.Height), fmt);
        }
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, box, fmt);
    }

    private static readonly Dictionary<float, FontFamily?> IconFamilies = [];

    /// <summary>An icon of Windows' icon font, its drawn shape centred in <paramref name="box"/>.</summary>
    private static void IconIn(Graphics g, string glyph, float px, Color color, RectangleF box)
    {
        if (!IconFamilies.TryGetValue(0, out var family))
        {
            family = FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons") ?? FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets");
            IconFamilies[0] = family;
        }
        if (family is null) return;
        using var path = new GraphicsPath();
        path.AddString(glyph, family, (int)FontStyle.Regular, px, PointF.Empty, StringFormat.GenericTypographic);
        var ink = path.GetBounds();
        if (ink.IsEmpty) return;
        using var move = new Matrix();
        move.Translate(box.X + (box.Width - ink.Width) / 2 - ink.X, box.Y + (box.Height - ink.Height) / 2 - ink.Y);
        path.Transform(move);
        using var brush = new SolidBrush(color);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
        g.SmoothingMode = smooth;
    }

    /// <summary>What reads on the accent (a tick in a box, a timer's play button): white on a dark one, near-black on a light one.</summary>
    private static Color OnAccent(Color accent) => Brightness(accent) > 0.6f ? Color.FromArgb(20, 20, 20) : Color.White;

    /// <summary>
    /// A note: typed straight onto the desktop (a click on it takes the keyboard, see WidgetManager.TileClicked), drawn
    /// with a caret while it's typed in, and scrolled with the wheel when it's longer than the tile.
    /// </summary>
    private static void NotesTile(Graphics g, WidgetTile t, RectangleF r, Palette p, Color accent)
    {
        Caption(g, Lang.T("NOTE"), r.Left, r.Top, p.Faint);
        var area = RectangleF.FromLTRB(r.Left, r.Top + 16, r.Right, r.Bottom);
        if (area.Height < 8) return;
        AddHit(HitNote, 0, area);
        AddScrollArea(area);
        float px = WidgetTiles.TextPx(t);
        var typing = TypingHere(adding: false);
        string text = typing?.Text ?? t.Text ?? "";
        if (typing is null && string.IsNullOrWhiteSpace(text))
        {
            TextIn(g, Lang.T("Click here to write."), px, p.Faint, area);
            return;
        }
        var font = MakeFont(px, FontStyle.Regular, false);
        float line = font.GetHeight(g);
        var body = RectangleF.FromLTRB(area.Left, area.Top, area.Right - 6, area.Bottom);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.None };
        fmt.FormatFlags &= ~StringFormatFlags.NoWrap;
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        // A line for the caret after a last empty line, as a text box has.
        string measured = text.EndsWith('\n') ? text + " " : text.Length == 0 ? " " : text;
        float height = g.MeasureString(measured, font, new SizeF(body.Width, 100_000), fmt).Height;
        int lines = Math.Max(1, (int)Math.Round(height / line)), shown = Math.Max(1, (int)(body.Height / line));
        int first = ScrollOf(lines - shown);
        // The caret, where the text has it: its line kept in sight while it's typed.
        RectangleF? caret = null;
        if (typing is not null)
        {
            caret = CaretAt(g, text, Math.Clamp(typing.Caret, 0, text.Length), font, body, fmt, line);
            int caretLine = (int)Math.Round((caret.Value.Y - body.Y) / line);
            if (caretLine < first) first = caretLine;
            else if (caretLine >= first + shown) first = caretLine - shown + 1;
            first = Math.Clamp(first, 0, Math.Max(0, lines - shown));
            SetScroll(first);
        }
        var state = g.Save();
        g.SetClip(area);
        g.TranslateTransform(0, -first * line);
        var box = new RectangleF(body.X, body.Y, body.Width, lines * line + line);
        if (TextShadow.A > 0)
        {
            float device = g.Transform.Elements[0];
            float o = Math.Max(1, MathF.Round(px / 16f * device)) / device;
            using var shadow = new SolidBrush(TextShadow);
            g.DrawString(text, font, shadow, new RectangleF(box.X + o, box.Y + o, box.Width, box.Height), fmt);
        }
        using (var brush = new SolidBrush(p.Text)) g.DrawString(text, font, brush, box, fmt);
        if (caret is { } c)
        {
            using var pen = new Pen(accent, Math.Max(1.5f, px / 9f));
            g.DrawLine(pen, c.X, c.Y + 1, c.X, c.Y + line - 1);
        }
        g.Restore(state);
        ScrollBar(g, area, first, shown, lines, p);
    }

    /// <summary>The caret's place in wrapped text: after the character before it, or at the start of a line.</summary>
    private static RectangleF CaretAt(Graphics g, string text, int at, Font font, RectangleF body, StringFormat fmt, float line)
    {
        if (at == 0) return new RectangleF(body.X, body.Y, 0, line);
        int i = at - 1;
        if (text[i] == '\n')
        {
            // After a line break: the start of the next line, one line under the break's own line.
            var before = CharRect(g, text, Math.Max(0, i - 1), font, body, fmt);
            float y = i == 0 || text[i - 1] == '\n' ? LineTopOfBreak(g, text, i, font, body, fmt, line) : before.Y;
            return new RectangleF(body.X, y + line, 0, line);
        }
        var r = CharRect(g, text, i, font, body, fmt);
        return new RectangleF(r.Right, r.Y, 0, line);
    }

    // A break's own line: the line under the text before it (a run of empty lines counted one by one).
    private static float LineTopOfBreak(Graphics g, string text, int i, Font font, RectangleF body, StringFormat fmt, float line)
    {
        int breaks = 0, j = i;
        while (j >= 0 && text[j] == '\n') { breaks++; j--; }
        if (j < 0) return body.Y + (breaks - 1) * line;
        return CharRect(g, text, j, font, body, fmt).Y + (breaks - 1) * line;
    }

    private static RectangleF CharRect(Graphics g, string text, int i, Font font, RectangleF body, StringFormat fmt)
    {
        using var f = (StringFormat)fmt.Clone();
        f.SetMeasurableCharacterRanges([new CharacterRange(i, 1)]);
        var regions = g.MeasureCharacterRanges(text, font, new RectangleF(body.X, body.Y, body.Width, 100_000), f);
        var b = regions.Length > 0 ? regions[0].GetBounds(g) : RectangleF.Empty;
        foreach (var region in regions) region.Dispose();
        return b;
    }

    /// <summary>
    /// A to-do list: a box to tick on each line, and a line to add one at the end; scrolled with the wheel when it holds
    /// more than it shows (a thin bar at its right says so), and kept at its end while an item is typed.
    /// </summary>
    private static void TodoTile(Graphics g, WidgetTile t, RectangleF r, Palette p, Color accent)
    {
        var items = t.Todos ?? [];
        Caption(g, Lang.T("TO-DO"), r.Left, r.Top, p.Faint);
        if (items.Count > 0)
        {
            string tally = Lang.T($"{items.Count(i => i.Done)} of {items.Count}");
            Text(g, tally, 9, FontStyle.Bold, p.Faint, r.Right - Measure(g, tally, 9, FontStyle.Bold), r.Top);
        }
        float px = WidgetTiles.TextPx(t), row = MathF.Round(px * 1.75f), top = r.Top + 18, box = MathF.Round(px + 1);
        int fit = Math.Max(1, (int)((r.Bottom - top) / row));
        int total = items.Count + 1; // the items and the line to add one
        var typing = TypingHere(adding: true);
        int first = ScrollOf(total - fit);
        if (typing is not null && first < total - fit) SetScroll(first = Math.Max(0, total - fit));
        var list = RectangleF.FromLTRB(r.Left, top, r.Right, top + fit * row);
        AddScrollArea(list);
        float right = total > fit ? r.Right - 8 : r.Right;
        var state = g.Save();
        g.SetClip(RectangleF.FromLTRB(r.Left - 2, top, r.Right + 2, r.Bottom));
        float y = top;
        for (int i = first; i < Math.Min(items.Count, first + fit); i++)
        {
            var line = new RectangleF(r.Left, y, right - r.Left, row);
            var b = new RectangleF(r.Left + 0.5f, y + MathF.Round((row - box) / 2) + 0.5f, box - 1, box - 1);
            using (var path = RoundRect(b, 3.5f))
            {
                if (items[i].Done)
                {
                    using var fill = new SolidBrush(accent);
                    g.FillPath(fill, path);
                    IconIn(g, "\uE73E", box * 0.62f, OnAccent(accent), b);
                }
                else
                {
                    using var pen = new Pen(p.Muted, 1.5f);
                    g.DrawPath(pen, path);
                }
            }
            float tx = r.Left + box + 8;
            var style = items[i].Done ? FontStyle.Strikeout : FontStyle.Regular;
            Text(g, items[i].Text, px, style, items[i].Done ? p.Faint : p.Text, tx, TextTopFor(b.Y + b.Height / 2, px, style), maxWidth: right - tx);
            AddHit(HitTodo, i, line);
            y += row;
        }
        if (first + fit > items.Count)
        {
            var line = new RectangleF(r.Left, y, right - r.Left, row);
            var b = new RectangleF(r.Left, y + MathF.Round((row - box) / 2), box, box);
            float tx = r.Left + box + 8, ty = TextTopFor(b.Y + b.Height / 2, px, FontStyle.Regular);
            IconIn(g, "\uE710", box * 0.7f, typing is null ? p.Faint : accent, b);
            if (typing is null) Text(g, Lang.T("Add an item"), px, FontStyle.Regular, p.Faint, tx, ty, maxWidth: right - tx);
            else
            {
                // What's typed, and the caret after the part before it.
                Text(g, typing.Text, px, FontStyle.Regular, p.Text, tx, ty, maxWidth: right - tx);
                float cx = tx + (typing.Caret > 0 ? Measure(g, typing.Text[..Math.Min(typing.Text.Length, typing.Caret)], px, FontStyle.Regular) : 0);
                using var pen = new Pen(accent, Math.Max(1.5f, px / 9f));
                g.DrawLine(pen, Math.Min(cx, right), b.Y - 1, Math.Min(cx, right), b.Bottom + 1);
            }
            AddHit(HitTodoAdd, 0, line);
        }
        g.Restore(state);
        ScrollBar(g, list, first, fit, total, p);
    }

    /// <summary>A timer (counts down), a stopwatch (counts up), each with Start or Pause and Reset; or the days to a day.</summary>
    private static void TimerTile(Graphics g, WidgetTile t, RectangleF r, Palette p, Color accent)
    {
        bool wide = r.Width >= 160;
        if (WidgetTimer.IsCountdown(t))
        {
            Caption(g, Lang.T("COUNTDOWN"), r.Left, r.Top, p.Faint);
            float y = r.Top + 16;
            if (WidgetTimer.DaysLeft(t, Now()) is not int days)
            {
                Text(g, Lang.T("No day picked"), 13, FontStyle.Regular, p.Faint, r.Left, y, maxWidth: r.Width);
                return;
            }
            string big = days switch { > 1 => Lang.T($"{days} days"), 1 => Lang.T("1 day"), 0 => Lang.T("Today"), -1 => Lang.T("1 day ago"), _ => Lang.T($"{-days} days ago") };
            string date = t.Until!.Value.ToString("d MMMM", Culture);
            string small = days >= 0 ? Lang.T($"until {date}") : Lang.T($"since {date}");
            float bigPx = wide ? 24 : 19;
            Text(g, big, bigPx, FontStyle.Regular, p.Text, r.Left, y, semibold: true);
            float bx = r.Left + Measure(g, big, bigPx, FontStyle.Regular, true) + 8;
            if (bx + 40 < r.Right) Text(g, small, 12, FontStyle.Regular, p.Muted, bx, y + bigPx * 1.33f - 12 * 1.33f - 1, maxWidth: r.Right - bx);
            else Text(g, small, 11.5f, FontStyle.Regular, p.Muted, r.Left, y + bigPx * 1.4f, maxWidth: r.Width);
            return;
        }
        long now = NowMs();
        bool running = WidgetTimer.Running(t), up = WidgetTimer.IsUp(t, now);
        Caption(g, (WidgetTimer.IsStopwatch(t) ? Lang.T("STOPWATCH") : Lang.T("TIMER")), r.Left, r.Top, p.Faint);
        float size = Math.Min(28, Math.Max(20, r.Height - 18)), gap = 6;
        float bigPx2 = wide ? 26 : 20;
        Text(g, WidgetTimer.Shown(t, now), bigPx2, FontStyle.Regular, up ? accent : p.Text, r.Left, r.Top + 14);
        // The buttons on the right, level with the time.
        float cy = r.Top + 14 + bigPx2 * 0.66f;
        var go = new RectangleF(r.Right - size, cy - size / 2, size, size);
        if (wide)
        {
            var reset = go;
            go.Offset(-(size + gap), 0);
            using (var track = new SolidBrush(p.Track)) g.FillEllipse(track, reset);
            IconIn(g, "\uE72C", size * 0.4f, p.Muted, reset);
            AddHit(HitReset, 0, reset);
        }
        using (var fill = new SolidBrush(accent)) g.FillEllipse(fill, go);
        IconIn(g, running ? "\uE769" : "\uE768", size * 0.4f, OnAccent(accent), go);
        AddHit(HitGo, 0, go);
    }

    // ── What a box shows, as text: the same text means the same picture ───

    /// <summary>
    /// A box's change key (see <see cref="ChangeKey"/>): what every tile would say, when every tile is only text and
    /// shapes from that text (a clock, a date, a number, a ring); null when one draws a history (a graph, a game's
    /// frame rate, Today), which is drawn every time.
    /// </summary>
    private static string? BoxKey(WidgetConfig cfg, WidgetData d, Palette p)
    {
        var sb = new System.Text.StringBuilder($"{Lang.Code}|{p.Light}|{p.Bg.ToArgb()}|{BoxAccent(cfg, p).ToArgb()}");
        var now = Now();
        foreach (var t in cfg.Tiles ?? [])
        {
            switch (t.Kind)
            {
                case WidgetTiles.Clock:
                    sb.Append('|').Append(TimeText(t, now).Time).Append(now.Date.Ticks);
                    break;
                case WidgetTiles.Date or WidgetTiles.Calendar:
                    sb.Append('|').Append(now.Date.Ticks);
                    break;
                case WidgetTiles.Reading when t.Look is null or WidgetTiles.Number or WidgetTiles.Big or WidgetTiles.Ring or WidgetTiles.Level or WidgetTiles.Blocks:
                    var r = Read(new WidgetItem { Id = t.Item ?? "", Label = t.Label }, d, p);
                    sb.Append('|').Append(r.Label).Append('=').Append(r.Text).Append('#').Append(r.ValueColor.ToArgb());
                    break;
                case WidgetTiles.Line:
                    foreach (var i in t.Items ?? [])
                    {
                        var x = Read(i, d, p);
                        sb.Append('|').Append(x.Label).Append('=').Append(x.Text).Append('#').Append(x.ValueColor.ToArgb());
                    }
                    break;
                case WidgetTiles.Notes:
                    sb.Append('|').Append(t.TextSize).Append(':').Append(t.Text);
                    break;
                case WidgetTiles.Todo:
                    sb.Append('|').Append(t.TextSize);
                    foreach (var i in t.Todos ?? []) sb.Append('|').Append(i.Done ? '+' : '-').Append(i.Text);
                    break;
                case WidgetTiles.Timer:
                    sb.Append('|').Append(WidgetTimer.KindOf(t)).Append(WidgetTimer.Running(t)).Append(WidgetTimer.Shown(t, NowMs())).Append(t.Until?.Ticks).Append(now.Date.Ticks);
                    break;
                default:
                    if (EverydayKey(t, d) is not { } key) return null;
                    sb.Append('|').Append(key);
                    break;
            }
        }
        return sb.ToString();
    }
}

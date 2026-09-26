using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>Everything a widget can show, captured once per update.</summary>
internal sealed class WidgetData
{
    public double? CpuTemp, CpuLoad, CpuPower, CpuClock, GpuTemp, GpuLoad, GpuPower, GpuHotSpot, GpuClock, RamLoad, RamUsedGb, RamTotalGb;
    public double? VramUsedMb, VramTotalMb;
    /// <summary>The game in front's frame rate, from RivaTuner (null when it isn't drawing in it).</summary>
    public FrameStats? Frame;
    public float[] CpuHistory = [];
    public float[] GpuHistory = [];
    public ActivityInfo Activity = new();
    /// <summary>The overlay's chosen sensors, in order: label, kind and current value.</summary>
    public List<OverlaySensorReading> Sensors = [];
    public TodayInfo Today = new();

    public WidgetData Copy() => (WidgetData)MemberwiseClone();
}

internal sealed record OverlaySensorReading(string Label, SensorKind Kind, double? Value);

/// <summary>Draws widget bitmaps with GDI+ (per-pixel alpha, no UI framework needed).</summary>
internal static partial class WidgetRenderer
{
    private sealed record Palette(Color Bg, Color Border, Color Text, Color Muted, Color Faint, Color Track, bool Light);

    private static readonly Color Cpu = Color.FromArgb(91, 140, 255);
    private static readonly Color Gpu = Color.FromArgb(61, 220, 151);
    private static readonly Color Ram = Color.FromArgb(177, 140, 255);

    // Black and white, like the app's own dark and light themes.
    private static readonly Palette DarkPalette = new(Color.FromArgb(10, 10, 10), Color.FromArgb(38, 38, 38), Color.White,
        Color.FromArgb(163, 163, 163), Color.FromArgb(107, 107, 107), Color.FromArgb(38, 38, 38), false);
    private static readonly Palette LightPalette = new(Color.White, Color.FromArgb(222, 222, 222), Color.Black,
        Color.FromArgb(85, 85, 85), Color.FromArgb(140, 140, 140), Color.FromArgb(230, 230, 230), true);

    private static Palette For(WidgetTheme theme) => theme switch
    {
        WidgetTheme.Light => LightPalette,
        WidgetTheme.System => WindowsUsesLight() ? LightPalette : DarkPalette,
        _ => DarkPalette,
    };

    // Widgets redraw every second or so: read Windows' app mode at most every few seconds.
    private static bool _windowsLight;
    private static long _windowsLightChecked;

    private static bool WindowsUsesLight()
    {
        long now = Environment.TickCount64;
        if (now - _windowsLightChecked < 3000) return _windowsLight;
        _windowsLightChecked = now;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _windowsLight = key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { _windowsLight = false; }
        return _windowsLight;
    }

    private static Color TempColor(double? c, Palette p)
    {
        if (c is not double t) return p.Faint;
        if (p.Light)
            return t < 45 ? Color.FromArgb(2, 132, 199) : t < 70 ? Color.FromArgb(5, 150, 105) : t < 85 ? Color.FromArgb(217, 119, 6) : Color.FromArgb(220, 38, 38);
        return t < 45 ? Color.FromArgb(56, 189, 248) : t < 70 ? Color.FromArgb(52, 211, 153) : t < 85 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(248, 113, 113);
    }

    internal static readonly RecentIcons IconCache = new();

    /// <summary>Base size of each style in device-independent pixels.</summary>
    private static SizeF BaseSize(WidgetStyle style, WidgetData? d, Graphics measure) => style switch
    {
        WidgetStyle.Compact => new(276, 122),
        WidgetStyle.Pill => new(PillWidth(d, measure), 34),
        WidgetStyle.Gauges => new(248, 150),
        WidgetStyle.NowPlaying => new(310, 88),
        WidgetStyle.Today => new(256, 140),
        _ => new(300, 138),
    };

    public static Bitmap Render(WidgetConfig cfg, WidgetData? data, float scale, bool hover, out RectangleF closeRect)
    {
        var pal = For(cfg.Theme);
        TextShadow = ShadowFor(cfg.BackgroundOpacity, pal.Light);
        SizeF size;
        using (var tmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(tmp))
            size = BaseSize(cfg.Style, data, mg);

        var bmp = new Bitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), PixelFormat.Format32bppArgb);
        // The readings go on their own layer, so they can have their own opacity (see Compose).
        using var content = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(content);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);
        g.ScaleTransform(scale, scale);

        float w = size.Width, h = size.Height;
        bool pill = cfg.Style == WidgetStyle.Pill;
        var panel = RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), pill ? (h - 1) / 2 : 14);

        var d = data ?? new WidgetData();
        switch (cfg.Style)
        {
            case WidgetStyle.Compact: DrawCompact(g, d, pal, w); break;
            case WidgetStyle.Pill: DrawPill(g, d, pal); break;
            case WidgetStyle.Gauges: DrawGauges(g, d, pal, w); break;
            case WidgetStyle.NowPlaying: DrawNowPlaying(g, d, pal, w, h); break;
            case WidgetStyle.Today: DrawToday(g, d, pal); break;
            default: DrawGraph(g, d, pal, w, h); break;
        }

        using (var final = Graphics.FromImage(bmp))
        {
            Compose(final, content, panel, scale, pal.Bg, pal.Border, cfg.BackgroundOpacity, cfg.ContentOpacity);
            // The close button stays fully visible, whatever the opacity: it's how you get rid of the widget.
            var close = pill ? new RectangleF(w - 24, (h - 18) / 2, 18, 18) : new RectangleF(w - 26, 6, 20, 20);
            if (hover && !cfg.Locked)
            {
                final.SmoothingMode = SmoothingMode.AntiAlias;
                final.ScaleTransform(scale, scale);
                DrawClose(final, close, pal);
            }
            closeRect = new RectangleF(close.X * scale, close.Y * scale, close.Width * scale, close.Height * scale);
        }
        panel.Dispose();
        return bmp;
    }

    /// <summary>
    /// Paints the panel at the background opacity, then the readings layer at the content opacity. The panel never
    /// goes fully transparent (1 of 255 at 0%): a layered window lets clicks through where it's clear, and a widget
    /// with no background should still be draggable anywhere on it, not only by its text.
    /// </summary>
    private static void Compose(Graphics g, Bitmap content, GraphicsPath panel, float scale, Color bg, Color border,
        double backgroundOpacity, double contentOpacity)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var state = g.Save();
        g.ScaleTransform(scale, scale);
        int bgAlpha = Math.Max(1, (int)Math.Round(bg.A * backgroundOpacity));
        using (var fill = new SolidBrush(Color.FromArgb(bgAlpha, bg)))
            g.FillPath(fill, panel);
        int borderAlpha = (int)Math.Round(border.A * backgroundOpacity);
        if (borderAlpha > 0)
            using (var pen = new Pen(Color.FromArgb(borderAlpha, border), 1))
                g.DrawPath(pen, panel);
        g.Restore(state);

        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)contentOpacity });
        g.DrawImage(content, new Rectangle(0, 0, content.Width, content.Height), 0, 0, content.Width, content.Height, GraphicsUnit.Pixel, attrs);
    }

    /// <summary>
    /// With little or no panel behind them, the readings sit straight on a wallpaper or a game, so they get a soft
    /// shadow in the opposite colour (dark behind light text, light behind the light theme's dark text). It grows as
    /// the background fades below 40%; with a solid enough panel there's none.
    /// </summary>
    private static Color ShadowFor(double backgroundOpacity, bool lightTheme)
    {
        if (backgroundOpacity >= 0.4) return Color.Empty;
        int alpha = (int)Math.Round(170 * (1 - backgroundOpacity / 0.4));
        return lightTheme ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 0, 0, 0);
    }

    [ThreadStatic] private static Color TextShadow;

    // ── Styles ────────────────────────────────────────────────────────────

    private static void DrawCompact(Graphics g, WidgetData d, Palette p, float w)
    {
        Caption(g, "RIGSIGHT", 14, 10, p.Faint);
        float colW = (w - 14 * 3) / 2;
        Column(14, "CPU", Cpu, d.CpuTemp, d.CpuLoad, d.CpuPower, d.CpuHistory);
        Column(14 * 2 + colW, "GPU", Gpu, d.GpuTemp, d.GpuLoad, d.GpuPower, d.GpuHistory);
        using var div = new Pen(p.Track, 1);
        g.DrawLine(div, 14 + colW + 7, 32, 14 + colW + 7, 110);

        void Column(float x, string label, Color accent, double? temp, double? load, double? power, float[] hist)
        {
            Text(g, label, 10.5f, FontStyle.Bold, accent, x, 30);
            Text(g, Units.TempShort(temp), 26, FontStyle.Bold, TempColor(temp, p), x - 1, 42, semibold: true);
            Text(g, $"{Units.Short(SensorKind.Load, load)}  ·  {Units.Short(SensorKind.Power, power)}", 11, FontStyle.Regular, p.Muted, x, 76);
            Sparkline(g, hist, new RectangleF(x, 95, colW, 15), accent);
        }
    }

    private static readonly (string Label, Color Accent)[] PillItems = [("CPU", Cpu), ("GPU", Gpu), ("RAM", Ram)];

    private static string[] PillValues(WidgetData? d) =>
    [
        Units.TempShort(d?.CpuTemp),
        Units.TempShort(d?.GpuTemp),
        Units.Short(SensorKind.Load, d?.RamLoad),
    ];

    private static float PillWidth(WidgetData? d, Graphics g)
    {
        var values = PillValues(d);
        float x = 16;
        for (int i = 0; i < PillItems.Length; i++)
            x += 11 + Measure(g, PillItems[i].Label, 11, FontStyle.Bold) + 5 + Measure(g, values[i], 13, FontStyle.Bold, semibold: true) + 16;
        return x + 18;
    }

    private static void DrawPill(Graphics g, WidgetData d, Palette p)
    {
        var values = PillValues(d);
        float x = 16;
        for (int i = 0; i < PillItems.Length; i++)
        {
            var (label, accent) = PillItems[i];
            using (var dot = new SolidBrush(accent)) g.FillEllipse(dot, x, 14, 6, 6);
            x += 11;
            Text(g, label, 11, FontStyle.Bold, p.Muted, x, 9);
            x += Measure(g, label, 11, FontStyle.Bold) + 5;
            var color = i < 2 ? TempColor(i == 0 ? d.CpuTemp : d.GpuTemp, p) : p.Text;
            Text(g, values[i], 13, FontStyle.Bold, color, x, 7.5f, semibold: true);
            x += Measure(g, values[i], 13, FontStyle.Bold, semibold: true) + 16;
        }
    }

    private static void DrawGauges(Graphics g, WidgetData d, Palette p, float w)
    {
        Caption(g, "RIGSIGHT", 14, 10, p.Faint);
        Gauge(w * 0.27f, "CPU", Cpu, d.CpuTemp, d.CpuLoad);
        Gauge(w * 0.73f, "GPU", Gpu, d.GpuTemp, d.GpuLoad);

        void Gauge(float cx, string label, Color accent, double? temp, double? load)
        {
            const float cy = 78, r = 40, t = 8;
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            using (var track = new Pen(p.Track, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, rect, 135, 270);
            float frac = temp is double v ? (float)Math.Clamp(v / 100, 0, 1) : 0;
            if (frac > 0.005f)
            {
                using var pen = new Pen(TempColor(temp, p), t) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, rect, 135, 270 * frac);
            }
            TextCentered(g, Units.TempShort(temp), 22, FontStyle.Bold, TempColor(temp, p), cx, cy - 15, semibold: true);
            TextCentered(g, label, 10, FontStyle.Bold, accent, cx, cy + 12);
            TextCentered(g, $"load {Units.Short(SensorKind.Load, load)}", 10.5f, FontStyle.Regular, p.Muted, cx, cy + r + 6);
        }
    }

    private static void DrawNowPlaying(Graphics g, WidgetData d, Palette p, float w, float h)
    {
        var a = d.Activity;
        if (a.Paused || a.Name is null)
        {
            Caption(g, a.Paused ? "TRACKING PAUSED" : "NOTHING IN FOCUS", 16, 20, p.Faint);
            Text(g, a.Paused ? "Resume from the tray menu" : "Rigsight is keeping an eye on things", 12, FontStyle.Regular, p.Muted, 16, 38);
            return;
        }

        var icon = IconFor(a.Path);
        var iconRect = new RectangleF(16, (h - 42) / 2, 42, 42);
        if (icon is not null) g.DrawImage(icon, iconRect);
        else
        {
            using var b = new SolidBrush(p.Track);
            using var path = RoundRect(iconRect, 10);
            g.FillPath(b, path);
        }

        bool game = a.Category == AppCategory.Game;
        string caption = !a.Present ? "AWAY" : game ? "NOW PLAYING" : "IN USE";
        Caption(g, caption, 70, 16, game && a.Present ? Gpu : p.Faint);

        float nameWidth = w - 70 - 96;
        Text(g, a.Name, 15, FontStyle.Bold, p.Text, 70, 29, semibold: true, maxWidth: nameWidth);
        string time = a.SessionActiveSec > 0 ? $"for {Units.Duration(a.SessionActiveSec)}" : "just started";
        Text(g, time, 11.5f, FontStyle.Regular, p.Muted, 70, 52);

        float rx = w - 88;
        Caption(g, "SESSION PEAK", rx, 16, p.Faint);
        Pair(g, "CPU", Units.TempShort(a.SessionCpuMax), TempColor(a.SessionCpuMax, p), rx, 32, p);
        Pair(g, "GPU", Units.TempShort(a.SessionGpuMax), TempColor(a.SessionGpuMax, p), rx, 51, p);
    }

    private static void DrawToday(Graphics g, WidgetData d, Palette p)
    {
        var t = d.Today;
        Caption(g, "TODAY", 14, 10, p.Faint);
        Text(g, Units.Duration(t.ActiveSec), 26, FontStyle.Bold, p.Text, 13, 24, semibold: true);
        float after = 13 + Measure(g, Units.Duration(t.ActiveSec), 26, FontStyle.Bold, semibold: true) + 6;
        Text(g, "active", 11.5f, FontStyle.Regular, p.Muted, after, 38);
        Text(g, $"On for {Units.Duration(t.OnSec)}  ·  away {Units.Duration(t.IdleSec)}", 11, FontStyle.Regular, p.Muted, 14, 60);

        Caption(g, "MOST USED", 14, 84, p.Faint);
        string top = t.TopApp is null ? "—" : $"{t.TopApp}  ·  {Units.Duration(t.TopAppSec)}";
        Text(g, top, 12.5f, FontStyle.Bold, p.Text, 14, 97, semibold: true, maxWidth: 228);

        Pair(g, "CPU peak", Units.TempShort(t.CpuPeak), TempColor(t.CpuPeak, p), 14, 117, p);
        Pair(g, "GPU peak", Units.TempShort(t.GpuPeak), TempColor(t.GpuPeak, p), 134, 117, p);
    }

    private static void DrawGraph(Graphics g, WidgetData d, Palette p, float w, float h)
    {
        Caption(g, "LAST 5 MINUTES", 14, 10, p.Faint);
        string cpuText = $"CPU {Units.TempShort(d.CpuTemp)}", gpuText = $"GPU {Units.TempShort(d.GpuTemp)}";
        float gx = w - 34 - Measure(g, gpuText, 11, FontStyle.Bold);
        Text(g, gpuText, 11, FontStyle.Bold, Gpu, gx, 8);
        Text(g, cpuText, 11, FontStyle.Bold, Cpu, gx - 12 - Measure(g, cpuText, 11, FontStyle.Bold), 8);

        var chart = new RectangleF(14, 32, w - 28, h - 44);
        var all = d.CpuHistory.Concat(d.GpuHistory).Where(float.IsFinite).Select(v => (float)Units.Temp(v)).ToList();
        float lo = all.Count > 0 ? MathF.Floor((all.Min() - 2) / 5) * 5 : 30;
        float hi = all.Count > 0 ? MathF.Ceiling((all.Max() + 2) / 5) * 5 : 80;
        if (hi - lo < 10) hi = lo + 10;

        using var grid = new Pen(p.Track, 1) { DashStyle = DashStyle.Dash };
        for (int i = 0; i <= 2; i++)
        {
            float y = chart.Bottom - chart.Height * i / 2;
            g.DrawLine(grid, chart.Left, y, chart.Right, y);
            Text(g, $"{lo + (hi - lo) * i / 2:0}°", 9, FontStyle.Regular, p.Faint, chart.Left + 1, y - 13);
        }
        Line(d.CpuHistory, Cpu);
        Line(d.GpuHistory, Gpu);

        void Line(float[] values, Color color)
        {
            if (values.Length < 2) return;
            using var pen = new Pen(color, 1.8f) { LineJoin = LineJoin.Round };
            PointF? prev = null;
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.IsFinite(values[i])) { prev = null; continue; }
                var pt = new PointF(chart.Left + chart.Width * i / (values.Length - 1),
                    chart.Bottom - ((float)Units.Temp(values[i]) - lo) / (hi - lo) * chart.Height);
                if (prev is PointF pp) g.DrawLine(pen, pp, pt);
                prev = pt;
            }
        }
    }

    // ── Primitives ────────────────────────────────────────────────────────

    // Fonts are cached: widgets and the overlay redraw every second and measure the same few sizes over and
    // over. Only a handful of combinations are ever used. UI thread only; never disposed.
    private static readonly Dictionary<(float Px, FontStyle Style, bool Semibold), Font> Fonts = [];

    private static Font MakeFont(float px, FontStyle style, bool semibold)
    {
        if (!Fonts.TryGetValue((px, style, semibold), out var font))
        {
            font = semibold ? new Font("Segoe UI Semibold", px, style & ~FontStyle.Bold, GraphicsUnit.Pixel)
                            : new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
            Fonts[(px, style, semibold)] = font;
        }
        return font;
    }

    private static void Text(Graphics g, string text, float px, FontStyle style, Color color, float x, float y,
        bool semibold = false, float maxWidth = 0)
    {
        var font = MakeFont(px, style, semibold);
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        if (maxWidth > 0) fmt.Trimming = StringTrimming.EllipsisCharacter;
        void Draw(Brush b, float dx, float dy)
        {
            if (maxWidth > 0) g.DrawString(text, font, b, new RectangleF(x + dx, y + dy, maxWidth, px * 1.6f), fmt);
            else g.DrawString(text, font, b, x + dx, y + dy, fmt);
        }
        if (TextShadow.A > 0)
        {
            // Scaled with the text, so it reads the same at every size (see ShadowFor).
            float o = Math.Max(0.6f, px / 16f);
            using var shadow = new SolidBrush(TextShadow);
            Draw(shadow, o, o);
            using var soft = new SolidBrush(Color.FromArgb(TextShadow.A / 2, TextShadow));
            Draw(soft, 0, o * 1.6f);
        }
        using var brush = new SolidBrush(color);
        Draw(brush, 0, 0);
    }

    private static void TextCentered(Graphics g, string text, float px, FontStyle style, Color color, float cx, float y, bool semibold = false) =>
        Text(g, text, px, style, color, cx - Measure(g, text, px, style, semibold) / 2, y, semibold);

    private static float Measure(Graphics g, string text, float px, FontStyle style, bool semibold = false)
    {
        var font = MakeFont(px, style, semibold);
        return g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width;
    }

    private static void Caption(Graphics g, string text, float x, float y, Color color) =>
        Text(g, text, 9, FontStyle.Bold, color, x, y);

    private static void Pair(Graphics g, string label, string value, Color valueColor, float x, float y, Palette p)
    {
        Text(g, label, 11, FontStyle.Regular, p.Muted, x, y);
        Text(g, value, 12, FontStyle.Bold, valueColor, x + Measure(g, label, 11, FontStyle.Regular) + 6, y - 0.5f, semibold: true);
    }

    private static void Sparkline(Graphics g, float[] values, RectangleF rect, Color color)
    {
        var finite = values.Where(float.IsFinite).ToList();
        if (finite.Count < 2) return;
        float lo = finite.Min(), hi = finite.Max();
        if (hi - lo < 1) { lo -= 0.5f; hi += 0.5f; }

        var pts = new List<PointF>();
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i])) continue;
            pts.Add(new PointF(rect.Left + rect.Width * i / (values.Length - 1), rect.Bottom - (values[i] - lo) / (hi - lo) * rect.Height));
        }
        if (pts.Count < 2) return;

        using (var fillPath = new GraphicsPath())
        {
            fillPath.AddLines([new PointF(pts[0].X, rect.Bottom), .. pts, new PointF(pts[^1].X, rect.Bottom)]);
            using var fill = new LinearGradientBrush(new RectangleF(rect.X, rect.Y - 1, rect.Width, rect.Height + 2),
                Color.FromArgb(70, color), Color.FromArgb(0, color), LinearGradientMode.Vertical);
            g.FillPath(fill, fillPath);
        }
        using var pen = new Pen(color, 1.5f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts.ToArray());
    }

    private static void DrawClose(Graphics g, RectangleF r, Palette p)
    {
        using (var bg = new SolidBrush(p.Track)) g.FillEllipse(bg, r);
        using var pen = new Pen(p.Text, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float inset = r.Width * 0.33f;
        g.DrawLine(pen, r.Left + inset, r.Top + inset, r.Right - inset, r.Bottom - inset);
        g.DrawLine(pen, r.Right - inset, r.Top + inset, r.Left + inset, r.Bottom - inset);
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Bitmap? IconFor(string? path) => path is null ? null : IconCache.Get(path, p =>
    {
        try
        {
            using var icon = Icon.ExtractIcon(p, 0, 64) ?? Icon.ExtractAssociatedIcon(p);
            return icon?.ToBitmap();
        }
        catch
        {
            return null; // no icon available
        }
    });
}

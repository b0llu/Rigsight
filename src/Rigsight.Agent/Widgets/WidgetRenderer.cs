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
    /// <summary>RivaTuner is running (the FPS widget says what's missing when there's no frame rate).</summary>
    public bool RtssRunning;
    public float[] CpuHistory = [];
    public float[] GpuHistory = [];
    /// <summary>Five minutes of the other readings a widget's tiles or graph show (see WidgetCatalog.GraphMetrics).</summary>
    public Dictionary<OverlayMetric, float[]> Histories = [];
    /// <summary>The sensors on widgets, by identifier: label, kind and current value.</summary>
    public Dictionary<string, OverlaySensorReading> Readings = [];
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
    private static readonly Color Gpu = Color.FromArgb(34, 211, 238);
    private static readonly Color Ram = Color.FromArgb(177, 140, 255);

    // Black and white, like the app's own dark and light themes.
    private static readonly Palette DarkPalette = new(Color.FromArgb(10, 10, 10), Color.FromArgb(38, 38, 38), Color.White,
        Color.FromArgb(163, 163, 163), Color.FromArgb(107, 107, 107), Color.FromArgb(38, 38, 38), false);
    private static readonly Palette LightPalette = new(Color.White, Color.FromArgb(222, 222, 222), Color.Black,
        Color.FromArgb(85, 85, 85), Color.FromArgb(140, 140, 140), Color.FromArgb(230, 230, 230), true);
    // As the app's Grey theme: its card, border, grey text and faint text.
    private static readonly Palette GreyPalette = new(Color.FromArgb(38, 38, 38), Color.FromArgb(59, 59, 59), Color.White,
        Color.FromArgb(173, 173, 173), Color.FromArgb(138, 138, 138), Color.FromArgb(59, 59, 59), false);

    private static Palette For(WidgetTheme theme) => theme switch
    {
        WidgetTheme.Light => LightPalette,
        WidgetTheme.Grey => GreyPalette,
        WidgetTheme.System => WindowsUsesLight() ? LightPalette : DarkPalette,
        _ => DarkPalette,
    };

    // Widgets redraw every second or so: read Windows' app mode at most every few seconds.
    private static bool _windowsLight;
    private static long _windowsLightChecked;

    internal static bool WindowsUsesLight()
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
        if (Gray) return p.Text;
        if (p.Light)
            return t < 45 ? Color.FromArgb(2, 132, 199) : t < 70 ? Color.FromArgb(5, 150, 105) : t < 85 ? Color.FromArgb(217, 119, 6) : Color.FromArgb(220, 38, 38);
        return t < 45 ? Color.FromArgb(56, 189, 248) : t < 70 ? Color.FromArgb(52, 211, 153) : t < 85 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(248, 113, 113);
    }

    internal static readonly RecentIcons IconCache = new();

    /// <summary>Base size of a widget in device-independent pixels: its layout's, fitted to its readings.</summary>
    private static SizeF BaseSize(WidgetConfig cfg, List<Reading> readings, Graphics measure) => WidgetCatalog.LayoutOf(cfg) switch
    {
        WidgetLayout.Bar => new(BarWidth(readings, measure), 34),
        WidgetLayout.Tiles => TilesSize(readings.Count),
        WidgetLayout.Gauges => new(20 + Math.Max(1, readings.Count) * 114, 150),
        WidgetLayout.Graph => new(Math.Max(300, 34 + LegendWidth(readings, measure) + 14), 138),
        WidgetLayout.NowPlaying => new(310, 88),
        _ => new(256, 140),
    };

    public static Bitmap Render(WidgetConfig cfg, WidgetData? data, float scale, bool hover, out RectangleF closeRect)
    {
        Gray = cfg.Grayscale;
        try
        {
            return RenderStyle(cfg, data, scale, hover, out closeRect);
        }
        finally
        {
            Gray = false; // the overlay draws on this thread too, with its own grayscale
        }
    }

    /// <summary>
    /// Everything a widget would draw, as text, for the kinds that are nothing but their text and colours (one line of
    /// readings, Now playing, Today): the same key means the same picture, so the widget isn't drawn and put on screen
    /// again (about 2.5 ms each time, every reading, for a Today that changes once a minute). Null for the kinds that
    /// draw more than their text (a gauge, a mini chart, a graph's lines): those are drawn every time.
    /// </summary>
    public static string? ChangeKey(WidgetConfig cfg, WidgetData? data)
    {
        var d = data ?? new WidgetData();
        var p = For(cfg.Theme);
        Gray = cfg.Grayscale;
        try
        {
            switch (WidgetCatalog.LayoutOf(cfg))
            {
                case WidgetLayout.Bar:
                    return $"{p.Light}|" + string.Join('|', WidgetCatalog.ItemsOf(cfg).Select(i => Read(i, d, p)).Select(r => $"{r.Label}={r.Text}#{r.ValueColor.ToArgb():X}"));
                case WidgetLayout.NowPlaying:
                    var a = d.Activity;
                    return $"{p.Light}|{a.Paused}|{a.Name}|{a.Path}|{a.Category}|{a.Present}|{Units.Duration(a.SessionActiveSec)}"
                        + $"|{Units.TempShort(a.SessionCpuMax)}#{TempColor(a.SessionCpuMax, p).ToArgb():X}|{Units.TempShort(a.SessionGpuMax)}#{TempColor(a.SessionGpuMax, p).ToArgb():X}";
                case WidgetLayout.Today:
                    var t = d.Today;
                    return $"{p.Light}|{Units.Duration(t.ActiveSec)}|{Units.Duration(t.OnSec)}|{Units.Duration(t.IdleSec)}|{t.TopApp}|{Units.Duration(t.TopAppSec)}"
                        + $"|{Units.TempShort(t.CpuPeak)}#{TempColor(t.CpuPeak, p).ToArgb():X}|{Units.TempShort(t.GpuPeak)}#{TempColor(t.GpuPeak, p).ToArgb():X}";
                default:
                    return null;
            }
        }
        finally
        {
            Gray = false;
        }
    }

    // Grayscale: labels, dots and captions grey, readings white (temperatures too); see WidgetConfig.Grayscale.
    [ThreadStatic] private static bool Gray;

    /// <summary>A label's colour (CPU blue, GPU green…), or grey in grayscale.</summary>
    private static Color Accent(Color color, Palette p) => Gray ? p.Muted : color;

    private static Bitmap RenderStyle(WidgetConfig cfg, WidgetData? data, float scale, bool hover, out RectangleF closeRect)
    {
        var pal = For(cfg.Theme);
        TextShadow = ShadowFor(cfg.BackgroundOpacity, pal.Light);
        var d = data ?? new WidgetData();
        var layout = WidgetCatalog.LayoutOf(cfg);
        var readings = WidgetCatalog.ItemsOf(cfg).Select(i => Read(i, d, pal)).ToList();
        SizeF size;
        using (var tmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(tmp))
            size = BaseSize(cfg, readings, mg);

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
        bool pill = layout == WidgetLayout.Bar;
        var panel = RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), pill ? (h - 1) / 2 : 14);
        // Built-in widgets are signed "Rigsight"; the user's own carry their name.
        string caption = cfg.Style == WidgetStyle.Custom ? WidgetCatalog.Title(cfg).ToUpperInvariant() : "RIGSIGHT";

        switch (layout)
        {
            case WidgetLayout.Bar: DrawBar(g, readings, pal); break;
            case WidgetLayout.Tiles: DrawTiles(g, readings, d, pal, w, caption); break;
            case WidgetLayout.Gauges: DrawGauges(g, readings, d, pal, caption); break;
            case WidgetLayout.Graph: DrawGraph(g, readings, d, pal, w, h); break;
            case WidgetLayout.NowPlaying: DrawNowPlaying(g, d, pal, w, h); break;
            default: DrawToday(g, d, pal); break;
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

    // ── Readings ─────────────────────────────────────────────────────────

    /// <summary>
    /// One reading as a widget draws it: its label and colour, its value as text and the value's colour
    /// (temperatures by how hot), the number itself (for gauges and graphs), and what it is.
    /// </summary>
    private readonly record struct Reading(string Label, Color Accent, string Text, Color ValueColor, double? Value, SensorKind Kind, OverlayMetric? Metric);

    private static readonly Color SensorAccent = Color.FromArgb(56, 189, 248);

    private static Reading Read(WidgetItem item, WidgetData d, Palette p)
    {
        if (WidgetCatalog.Metric(item.Id) is { } m)
        {
            var (accent, kind, value) = m switch
            {
                OverlayMetric.CpuTemp => (Cpu, SensorKind.Temperature, d.CpuTemp),
                OverlayMetric.CpuLoad => (Cpu, SensorKind.Load, d.CpuLoad),
                OverlayMetric.CpuClock => (Cpu, SensorKind.Clock, d.CpuClock),
                OverlayMetric.CpuPower => (Cpu, SensorKind.Power, d.CpuPower),
                OverlayMetric.GpuTemp => (Gpu, SensorKind.Temperature, d.GpuTemp),
                OverlayMetric.GpuHotSpot => (Gpu, SensorKind.Temperature, d.GpuHotSpot),
                OverlayMetric.GpuLoad => (Gpu, SensorKind.Load, d.GpuLoad),
                OverlayMetric.GpuClock => (Gpu, SensorKind.Clock, d.GpuClock),
                OverlayMetric.GpuPower => (Gpu, SensorKind.Power, d.GpuPower),
                OverlayMetric.GpuMemory => (Gpu, SensorKind.SmallData, d.VramUsedMb),
                OverlayMetric.Ram => (Ram, SensorKind.Load, d.RamLoad),
                OverlayMetric.Fps => (Fps, SensorKind.Frequency, d.Frame?.Fps),
                OverlayMetric.FrameTime => (Fps, SensorKind.Factor, d.Frame?.FrameTimeMs),
                OverlayMetric.OnePercentLow => (Fps, SensorKind.Frequency, d.Frame?.OnePercentLow),
                _ => (SensorAccent, SensorKind.Factor, (double?)null), // the time of day
            };
            string text = m switch
            {
                OverlayMetric.Fps or OverlayMetric.OnePercentLow => value is double f ? $"{f:0}" : "—",
                OverlayMetric.FrameTime => value is double t ? $"{t:0.0} ms" : "—",
                OverlayMetric.GpuMemory => value is double mb ? Units.Megabytes(mb) : "—",
                OverlayMetric.Clock => DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture),
                _ => Units.Short(kind, value),
            };
            Color color = kind == SensorKind.Temperature ? TempColor(value, p) : value is null && m != OverlayMetric.Clock ? p.Faint : p.Text;
            return new Reading(item.Label ?? WidgetCatalog.ShortLabel(m), accent, text, color, value, kind, m);
        }
        var sensor = WidgetCatalog.Sensor(item.Id) is { } id ? d.Readings.GetValueOrDefault(id) : null;
        var sensorKind = sensor?.Kind ?? SensorKind.Factor;
        return new Reading(item.Label ?? sensor?.Label ?? "Sensor", SensorAccent, Units.Short(sensorKind, sensor?.Value),
            sensorKind == SensorKind.Temperature ? TempColor(sensor?.Value, p) : sensor?.Value is null ? p.Faint : p.Text, sensor?.Value, sensorKind, null);
    }

    /// <summary>The last five minutes of a reading, where the agent keeps them (key temperatures and loads).</summary>
    private static float[] History(Reading r, WidgetData d) => r.Metric switch
    {
        OverlayMetric.CpuTemp => d.CpuHistory,
        OverlayMetric.GpuTemp => d.GpuHistory,
        { } m => d.Histories.GetValueOrDefault(m, []),
        _ => [],
    };

    // ── Layouts ──────────────────────────────────────────────────────────

    /// <summary>The slim bar's shape: dot, label, value, for each reading, on one line.</summary>
    private static float BarWidth(List<Reading> readings, Graphics g)
    {
        float x = 16;
        foreach (var r in readings)
            x += 11 + Measure(g, r.Label, 11, FontStyle.Bold) + 5 + Measure(g, r.Text, 13, FontStyle.Bold, semibold: true) + 16;
        return Math.Max(x + 18, 120);
    }

    private static void DrawBar(Graphics g, List<Reading> readings, Palette p)
    {
        float x = 16;
        foreach (var r in readings)
        {
            using (var dot = new SolidBrush(Accent(r.Accent, p))) g.FillEllipse(dot, x, 14, 6, 6);
            x += 11;
            Text(g, r.Label, 11, FontStyle.Bold, p.Muted, x, 9);
            x += Measure(g, r.Label, 11, FontStyle.Bold) + 5;
            Text(g, r.Text, 13, FontStyle.Bold, r.ValueColor, x, 7.5f, semibold: true);
            x += Measure(g, r.Text, 13, FontStyle.Bold, semibold: true) + 16;
        }
    }

    private const float TileHeight = 90, TileGap = 14;

    /// <summary>A grid of tiles, two across (one alone is a single tile); a caption on top when there are several.</summary>
    private static SizeF TilesSize(int count)
    {
        int columns = count <= 1 ? 1 : 2, rows = Math.Max(1, (count + columns - 1) / columns);
        return new SizeF(columns == 1 ? 184 : 276, (count > 1 ? 30 : 10) + rows * TileHeight + (count > 1 ? 2 : 0));
    }

    private static void DrawTiles(Graphics g, List<Reading> readings, WidgetData d, Palette p, float w, string caption)
    {
        int columns = readings.Count <= 1 ? 1 : 2;
        float top = readings.Count > 1 ? 30 : 10;
        if (readings.Count > 1) Caption(g, caption, 14, 10, p.Faint);
        float colW = (w - TileGap * (columns + 1)) / columns;
        using var div = new Pen(p.Track, 1);
        for (int i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            float x = TileGap + (i % columns) * (colW + TileGap), y = top + (i / columns) * TileHeight;
            if (i % columns == 1) g.DrawLine(div, x - TileGap / 2, y + 2, x - TileGap / 2, y + TileHeight - 12);
            Text(g, r.Label, 10.5f, FontStyle.Bold, Accent(r.Accent, p), x, y);
            Text(g, r.Text, 26, FontStyle.Bold, r.ValueColor, x - 1, y + 12, semibold: true, maxWidth: colW);
            if (Detail(r, d) is { } detail) Text(g, detail, 11, FontStyle.Regular, p.Muted, x, y + 46, maxWidth: colW);
            var history = History(r, d);
            if (history.Length > 1) Sparkline(g, history, new RectangleF(x, y + 65, colW, 15), Gray ? p.Text : r.Accent);
        }
    }

    /// <summary>The small line under a tile's value: what goes with it (a CPU's load and power, a game's frame time).</summary>
    private static string? Detail(Reading r, WidgetData d) => r.Metric switch
    {
        OverlayMetric.CpuTemp => $"{Units.Short(SensorKind.Load, d.CpuLoad)}  ·  {Units.Short(SensorKind.Power, d.CpuPower)}",
        OverlayMetric.GpuTemp => $"{Units.Short(SensorKind.Load, d.GpuLoad)}  ·  {Units.Short(SensorKind.Power, d.GpuPower)}",
        OverlayMetric.Ram when d.RamUsedGb is double used && d.RamTotalGb is double total => $"{used:0.0} of {total:0} GB",
        OverlayMetric.Fps when d.Frame is FrameStats f => f.OnePercentLow is double low ? $"{f.FrameTimeMs:0.0} ms  ·  1% low {low:0}" : $"{f.FrameTimeMs:0.0} ms",
        OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow when d.Frame is null =>
            d.RtssRunning ? "Waiting for a game" : "Needs RivaTuner",
        _ => null,
    };

    private static void DrawGauges(Graphics g, List<Reading> readings, WidgetData d, Palette p, string caption)
    {
        Caption(g, caption, 14, 10, p.Faint);
        for (int i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            float cx = 10 + 57 + i * 114;
            const float cy = 78, radius = 40, t = 8;
            var rect = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            using (var track = new Pen(p.Track, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, rect, 135, 270);
            float frac = r.Value is double v ? (float)Math.Clamp(v / 100, 0, 1) : 0;
            var color = r.Kind == SensorKind.Temperature ? r.ValueColor : Gray ? p.Text : r.Accent;
            if (frac > 0.005f)
            {
                using var pen = new Pen(color, t) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, rect, 135, 270 * frac);
            }
            TextCentered(g, r.Text, 22, FontStyle.Bold, r.ValueColor, cx, cy - 15, semibold: true);
            TextCentered(g, r.Label, 10, FontStyle.Bold, Accent(r.Accent, p), cx, cy + 12);
            string? below = r.Metric switch
            {
                OverlayMetric.CpuTemp => $"load {Units.Short(SensorKind.Load, d.CpuLoad)}",
                OverlayMetric.GpuTemp => $"load {Units.Short(SensorKind.Load, d.GpuLoad)}",
                _ => null,
            };
            if (below is not null) TextCentered(g, below, 10.5f, FontStyle.Regular, p.Muted, cx, cy + radius + 6);
        }
    }

    private static readonly Color HotSpotLine = Color.FromArgb(177, 140, 255);

    /// <summary>A graph line's colour: its reading's, told apart where two share one (a load next to its temperature).</summary>
    private static Color LineColor(Reading r, Palette p, int index)
    {
        if (Gray) return index switch { 0 => p.Text, 1 => p.Muted, _ => p.Faint };
        return r.Metric switch
        {
            OverlayMetric.GpuHotSpot => HotSpotLine,
            OverlayMetric.CpuLoad => Color.FromArgb(147, 197, 253),
            OverlayMetric.GpuLoad => Color.FromArgb(165, 243, 252),
            _ => r.Accent,
        };
    }

    private static float LegendWidth(List<Reading> readings, Graphics g) =>
        readings.Sum(r => Measure(g, $"{r.Label} {r.Text}", 11, FontStyle.Bold) + 12);

    private static void DrawGraph(Graphics g, List<Reading> readings, WidgetData d, Palette p, float w, float h)
    {
        Caption(g, "LAST 5 MINUTES", 14, 10, p.Faint);
        float gx = w - 34;
        for (int i = readings.Count - 1; i >= 0; i--)
        {
            string text = $"{readings[i].Label} {readings[i].Text}";
            gx -= Measure(g, text, 11, FontStyle.Bold);
            Text(g, text, 11, FontStyle.Bold, LineColor(readings[i], p, i), gx, 8);
            gx -= 12;
        }

        // Temperatures in the chosen unit; anything else (loads) as it is. The axis says ° only when all are temperatures.
        bool temps = readings.All(r => r.Kind == SensorKind.Temperature);
        float Value(Reading r, float v) => r.Kind == SensorKind.Temperature ? (float)Units.Temp(v) : v;
        var chart = new RectangleF(14, 32, w - 28, h - 44);
        var all = readings.SelectMany(r => History(r, d).Where(float.IsFinite).Select(v => Value(r, v))).ToList();
        float lo = all.Count > 0 ? MathF.Floor((all.Min() - 2) / 5) * 5 : 30;
        float hi = all.Count > 0 ? MathF.Ceiling((all.Max() + 2) / 5) * 5 : 80;
        if (hi - lo < 10) hi = lo + 10;

        using var grid = new Pen(p.Track, 1) { DashStyle = DashStyle.Dash };
        for (int i = 0; i <= 2; i++)
        {
            float y = chart.Bottom - chart.Height * i / 2;
            g.DrawLine(grid, chart.Left, y, chart.Right, y);
            Text(g, $"{lo + (hi - lo) * i / 2:0}{(temps ? "°" : "")}", 9, FontStyle.Regular, p.Faint, chart.Left + 1, y - 13);
        }
        for (int i = 0; i < readings.Count; i++) Line(readings[i], LineColor(readings[i], p, i));

        void Line(Reading r, Color color)
        {
            var values = History(r, d);
            if (values.Length < 2) return;
            using var pen = new Pen(color, 1.8f) { LineJoin = LineJoin.Round };
            PointF? prev = null;
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.IsFinite(values[i])) { prev = null; continue; }
                var pt = new PointF(chart.Left + chart.Width * i / (values.Length - 1),
                    chart.Bottom - (Value(r, values[i]) - lo) / (hi - lo) * chart.Height);
                if (prev is PointF pp) g.DrawLine(pen, pp, pt);
                prev = pt;
            }
        }
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

    // ── Now playing and Today ─────────────────────────────────────────────

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
        Caption(g, caption, 70, 16, game && a.Present ? Accent(Gpu, p) : p.Faint);

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

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

#if RIGSIGHT_APP
// Linked into the app, which draws widgets with the same code for its Widgets page (see Rigsight.csproj).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

/// <summary>An app on a live list: its name, its program (for its icon) and its figure as written.</summary>
internal sealed record TileApp(string Name, string? Path, string Value);

/// <summary>A fan as the Fans tile lists it: its name and speed (null: not read yet).</summary>
internal sealed record FanNow(string Name, double? Rpm);

/// <summary>A fixed drive: its letter, how full it is (0 to 1), its free bytes, and its temperature (null: none read).</summary>
internal sealed record DriveNow(string Letter, double UsedShare, long Free, double? Temp);

/// <summary>
/// What the PC's live tiles show (the PC tiles mock-up, 2026-11-11), filled by the agent only for the tiles a widget that
/// is on has: a list left null wasn't read (nothing is drawn for it, never a line saying there's none).
/// </summary>
internal sealed class PcNow
{
    public List<FanNow>? Fans;
    public List<DriveNow>? Drives;
    /// <summary>Each core's load, 0 to 1, in the CPU's order.</summary>
    public float[]? Cores;
    public List<TileApp>? TopCpu, TopMemory;
    /// <summary>The apps using the internet now, fastest first (null: not known here, or no admin rights to know it).</summary>
    public List<TileApp>? NetApps;
    /// <summary>Whether each app's internet is read (it is with admin rights).</summary>
    public bool NetPerApp;
    /// <summary>The speeds now, in bytes a second (null: not read), and the last minute of them, oldest first.</summary>
    public double? NetDown, NetUp;
    public float[] DownHistory = [], UpHistory = [];
}

// The PC's tiles (approved mock-up "PC tiles", 2026-11-11): live, internet, and the day from RigSight's records. Each is
// laid out as the mock-up's page lays it out (its line heights, paddings and gaps), so they come out the same size.
internal static partial class WidgetRenderer
{
    private static readonly Color NetColor = Color.FromArgb(56, 189, 248);
    private static readonly Color DriveColor = Color.FromArgb(244, 114, 182);

    /// <summary>The colours of a status (good and bad), as the temperatures' are: never a part's.</summary>
    private static Color Good(Palette p) => Gray ? p.Muted : p.Light ? Color.FromArgb(4, 120, 87) : Color.FromArgb(52, 211, 153);
    private static Color Bad(Palette p) => Gray ? p.Text : p.Light ? Color.FromArgb(185, 28, 28) : Color.FromArgb(248, 113, 113);
    private static Color Warm(Palette p) => Gray ? p.Text : p.Light ? Color.FromArgb(180, 83, 9) : Color.FromArgb(251, 191, 36);

    /// <summary>A bar's or graph's fill in a part's colour; white (the text's) in grayscale, as a reading's ring is.</summary>
    private static Color Fill(Color part, Palette p) => Gray ? p.Text : part;

    private static Color CpuOf(Palette p) => p.CpuColor ?? Cpu;
    private static Color GpuOf(Palette p) => p.GpuColor ?? Gpu;

    /// <summary>A line of text as a web page sets it: <paramref name="lineTop"/> is the top of its line box, <paramref name="lineH"/> its height.</summary>
    private static void TextLine(Graphics g, string text, float px, Color color, float x, float lineTop, float lineH, bool semibold = false, float maxWidth = 0)
    {
        if (text.Length == 0) return;
        Text(g, text, px, semibold ? FontStyle.Bold : FontStyle.Regular, color, x, lineTop + (lineH - px * 1.33f) / 2 - InkLift, semibold, maxWidth);
    }

    /// <summary>The top of a line box of a <paramref name="px"/> font whose baseline is <paramref name="baseline"/>.</summary>
    private static float TopForBaseline(float baseline, float px) => baseline - px * 1.079f - InkLift;

    /// <summary>
    /// How much higher GDI+ is asked to draw a line than its baseline works out: measured against the mock-up on a picture,
    /// its letters came out a pixel under the browser's at every size (2026-11-11).
    /// </summary>
    private const float InkLift = 1;

    /// <summary>A space's width in Segoe UI (GDI+ leaves a space at a line's end out of its measure).</summary>
    private static float Space(float px) => px * 0.274f;

    private static float W(Graphics g, string text, float px, bool semibold = false) =>
        text.Length == 0 ? 0 : Measure(g, text, px, semibold ? FontStyle.Bold : FontStyle.Regular, semibold);

    /// <summary>Right-aligned text ending at <paramref name="right"/>; its width.</summary>
    private static float TextRight(Graphics g, string text, float px, Color color, float right, float lineTop, float lineH, bool semibold = false)
    {
        float w = W(g, text, px, semibold);
        TextLine(g, text, px, color, right - w, lineTop, lineH, semibold);
        return w;
    }

    private const float Body = 12.5f, BodyLine = Body * 1.25f, CapPx = 10.5f, CapHeight = CapPx * 1.25f + 3;

    /// <summary>A tile's heading (the mock-up's ".k"): small semibold capitals, spaced a little, in the faint colour; its height with the room under it.</summary>
    private static float Cap(Graphics g, string text, float x, float top, Palette p, float right = 0)
    {
        float spacing = CapPx * 0.06f, at = x, y = top + (CapPx * 1.25f - CapPx * 1.33f) / 2 - InkLift;
        foreach (char c in text)
        {
            string ch = c.ToString();
            float w = c == ' ' ? CapPx * 0.27f : Measure(g, ch, CapPx, FontStyle.Bold, true);
            if (right > 0 && at + w > right) break;
            if (c != ' ') Text(g, ch, CapPx, FontStyle.Bold, p.Faint, at, y, semibold: true);
            at += w + spacing;
        }
        return CapHeight;
    }

    private static float CapWidth(Graphics g, string text) =>
        text.Sum(c => (c == ' ' ? CapPx * 0.27f : Measure(g, c.ToString(), CapPx, FontStyle.Bold, true)) + CapPx * 0.06f);

    /// <summary>A rounded bar's track and its filled part from the left (the mock-up's level).</summary>
    private static void Level(Graphics g, RectangleF r, double share, Color fill, Palette p)
    {
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundRect(r, r.Height / 2))
        using (var b = new SolidBrush(p.Track))
            g.FillPath(b, path);
        float w = (float)Math.Round(Math.Clamp(share, 0, 1) * r.Width);
        if (w >= 1)
        {
            using var path = RoundRect(r with { Width = Math.Max(w, Math.Min(r.Height, r.Width)) }, r.Height / 2);
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }
        g.SmoothingMode = smooth;
    }

    private static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundRect(r, Math.Min(radius, Math.Min(r.Width, r.Height) / 2)))
        using (var b = new SolidBrush(color))
            g.FillPath(b, path);
        g.SmoothingMode = smooth;
    }

    /// <summary>A shape with only its top corners rounded (a bar standing on its foot).</summary>
    private static void FillTopRound(Graphics g, RectangleF r, float radius, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height * 2));
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        if (d < 0.5f) path.AddRectangle(r);
        else
        {
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            path.CloseFigure();
        }
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
        g.SmoothingMode = smooth;
    }

    // ── Icons of the apps on the lists ───────────────────────────────────────

    /// <summary>The apps' icons on the PC tiles' lists, small and kept apart from the other icons (more apps than they hold).</summary>
    internal static readonly RecentIcons ListIcons = new(48);

    private static Bitmap? SmallIcon(string? path) => path is null ? null : ListIcons.Get(path, p =>
    {
        try
        {
            using var icon = Icon.ExtractIcon(p, 0, 32) ?? Icon.ExtractAssociatedIcon(p);
            return icon?.ToBitmap();
        }
        catch
        {
            return null; // no icon to be had
        }
    });

    private static readonly Dictionary<string, Color?> IconColors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An app's own colour, the most of one hue in its icon (Chrome's blue, Discord's violet), for its bar on Most used;
    /// null for an icon with no colour to speak of.
    /// </summary>
    private static Color? IconColor(string? path)
    {
        if (path is null) return null;
        if (IconColors.TryGetValue(path, out var known)) return known;
        Color? found = null;
        if (SmallIcon(path) is { } bmp)
        {
            try
            {
                var weight = new double[12];
                var sum = new (double R, double G, double B)[12];
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (c.A < 128) continue;
                        float sat = c.GetSaturation(), light = c.GetBrightness();
                        if (sat < 0.35f || light < 0.2f || light > 0.85f) continue;
                        int bin = (int)(c.GetHue() / 30) % 12;
                        double wgt = sat * c.A / 255.0;
                        weight[bin] += wgt;
                        sum[bin] = (sum[bin].R + c.R * wgt, sum[bin].G + c.G * wgt, sum[bin].B + c.B * wgt);
                    }
                int best = Array.IndexOf(weight, weight.Max());
                if (weight[best] > 8)
                    found = Color.FromArgb((int)(sum[best].R / weight[best]), (int)(sum[best].G / weight[best]), (int)(sum[best].B / weight[best]));
            }
            catch (ArgumentException) { } // let go of meanwhile: no colour this time
        }
        if (IconColors.Count > 200) IconColors.Clear();
        IconColors[path] = found;
        return found;
    }

    /// <summary>An app's icon in a square of <paramref name="size"/>, or the first letter of its name on the track's colour.</summary>
    private static void AppIcon(Graphics g, string name, string? path, RectangleF box, Palette p, float radius = 4)
    {
        if (path is not null && SmallIcon(path) is { } icon && Alive(icon))
        {
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, box);
            g.InterpolationMode = mode;
            return;
        }
        FillRound(g, box, radius, p.Track);
        string letter = name.Trim().Length > 0 ? char.ToUpper(name.Trim()[0], CultureInfo.CurrentCulture).ToString() : "?";
        float px = box.Height * 0.6f;
        var ink = InkOf(g, letter, px, FontStyle.Bold, true);
        if (!ink.IsEmpty)
            Text(g, letter, px, FontStyle.Bold, p.Muted, box.X + (box.Width - ink.Width) / 2 - ink.Left, box.Y + (box.Height - ink.Height) / 2 - ink.Top, semibold: true);
    }

    // ── The rows of a list ─────────────────────────────────────────────────

    /// <summary>The mock-up's list row: 2.5 px above and below its line (16 px with an icon).</summary>
    private const float RowH = BodyLine + 5, AppRowH = 21;

    /// <summary>How many rows fit under a heading (the mock-up's fitsN): one at least, <paramref name="most"/> at most.</summary>
    private static int Fits(float height, float head, float row, int most) => Math.Clamp((int)Math.Floor((height - head) / row), 1, Math.Max(1, most));

    /// <summary>An app's row: its icon and name on the left (cut with "…"), its figure on the right.</summary>
    private static void AppRow(Graphics g, TileApp app, float y, RectangleF r, Palette p, Color? valueColor = null)
    {
        float vw = TextRight(g, app.Value, Body, valueColor ?? p.Text, r.Right, y + 2.5f, 16);
        AppIcon(g, app.Name, app.Path, new RectangleF(r.Left, y + 2.5f, 16, 16), p);
        float nx = r.Left + 23;
        TextLine(g, app.Name, Body, p.Text, nx, y + 2.5f, 16, maxWidth: Math.Max(1, r.Right - vw - 10 - nx));
    }

    /// <summary>A heading and a list of apps under it (Top CPU users, Using the internet now…).</summary>
    private static void AppList(Graphics g, string caption, IReadOnlyList<TileApp>? apps, RectangleF r, Palette p, int most, string? none)
    {
        float y = r.Top + Cap(g, caption, r.Left, r.Top, p, r.Right);
        if (apps is null) return;
        if (apps.Count == 0)
        {
            if (none is not null) TextIn(g, none, Body, p.Muted, RectangleF.FromLTRB(r.Left, y + 2.5f, r.Right, r.Bottom));
            return;
        }
        int n = Math.Min(apps.Count, Fits(r.Height, 16, AppRowH, most));
        for (int i = 0; i < n; i++, y += AppRowH) AppRow(g, apps[i], y, r, p);
    }

    /// <summary>Puts a block of <paramref name="height"/> in the middle of the tile, top to bottom (the mock-up's tiles that aren't "top").</summary>
    private static float Middle(RectangleF r, float height) => r.Top + (r.Height - height) / 2;

    // ── Live ───────────────────────────────────────────────────────────────

    /// <summary>A CPU's or GPU's temperature as a big ring, coloured by how hot it is, with its load under it where there's room.</summary>
    private static void GaugeTile(Graphics g, bool gpu, RectangleF r, WidgetData d, Palette p)
    {
        var reading = Read(new WidgetItem { Id = gpu ? nameof(OverlayMetric.GpuTemp) : nameof(OverlayMetric.CpuTemp) }, d, p);
        var part = PartColor(reading, p);
        bool withLoad = r.Height >= 104;
        float room = withLoad ? r.Height - 20 : r.Height;
        float s = MathF.Round(Math.Min(r.Width, room)), sw = Math.Max(5, MathF.Round(s / 12));
        float frac = reading.Value is double v ? (float)Math.Clamp(v / 100, 0, 1) : 0;
        string load = Lang.T($"Load {Units.Short(SensorKind.Load, gpu ? d.GpuLoad : d.CpuLoad)}");
        float block = s + (withLoad ? 4 + BodyLine : 0);
        float top = MathF.Round(Middle(r, block)), left = MathF.Round(r.Left + (r.Width - s) / 2);
        var ring = new RectangleF(left, top, s, s);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var arc = RectangleF.Inflate(ring, -sw / 2 - 1, -sw / 2 - 1);
        using (var track = new Pen(p.Track, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(track, arc, 135, 270);
        if (reading.Value is not null)
            using (var pen = new Pen(Gray ? p.Text : reading.ValueColor, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, 135, Math.Max(2, 270 * frac));
        g.SmoothingMode = smooth;
        // The value and the name inside, as one block in the ring's middle, centred by their ink.
        float vpx = MathF.Round(s / 4.3f);
        var vi = InkOf(g, reading.Text, vpx, FontStyle.Regular, false);
        var li = InkOf(g, reading.Label, CapPx, FontStyle.Bold, true);
        float inner = vi.Height + 5 + li.Height, iy = ring.Top + (s - inner) / 2;
        Text(g, reading.Text, vpx, FontStyle.Regular, reading.ValueColor, ring.Left + (s - vi.Width) / 2 - vi.Left, iy - vi.Top);
        Text(g, reading.Label, CapPx, FontStyle.Bold, part, ring.Left + (s - li.Width) / 2 - li.Left, iy + vi.Height + 5 - li.Top, semibold: true);
        if (withLoad)
        {
            var ink = InkOf(g, load, Body, FontStyle.Regular, false);
            TextLine(g, load, Body, p.Muted, r.Left + (r.Width - ink.Width) / 2 - ink.Left, ring.Bottom + 4, BodyLine);
        }
    }

    private static void FansTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float y = r.Top + Cap(g, Lang.T("FANS"), r.Left, r.Top, p, r.Right);
        if (d.Pc?.Fans is not { } fans) return;
        if (fans.Count == 0)
        {
            TextIn(g, Lang.T("No spinning fans reported."), Body, p.Muted, RectangleF.FromLTRB(r.Left, y + 2.5f, r.Right, r.Bottom));
            return;
        }
        int n = Math.Min(fans.Count, Fits(r.Height, 16, AppRowH, fans.Count));
        for (int i = 0; i < n; i++, y += RowH)
        {
            string rpm = fans[i].Rpm is double v ? Units.Format(SensorKind.Fan, v) : "—";
            float vw = TextRight(g, rpm, Body, fans[i].Rpm is null ? p.Faint : p.Text, r.Right, y + 2.5f, BodyLine);
            TextLine(g, fans[i].Name, Body, p.Text, r.Left, y + 2.5f, BodyLine, maxWidth: Math.Max(1, r.Width - vw - 10));
        }
    }

    private static void DrivesTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float y = r.Top + Cap(g, Lang.T("DRIVES"), r.Left, r.Top, p, r.Right);
        if (d.Pc?.Drives is not { Count: > 0 } drives) return;
        int n = Math.Min(drives.Count, Fits(r.Height, 16, 31, drives.Count));
        var fill = Fill(DriveColor, p);
        for (int i = 0; i < n; i++)
        {
            var dr = drives[i];
            float line = y + 1;
            string name = Lang.T($"{dr.Letter} drive"), free = Lang.T($"{Units.Bytes(dr.Free)} free");
            float fw = TextRight(g, free, 11.5f, p.Muted, r.Right, line, BodyLine);
            float room = Math.Max(1, r.Width - fw - 10);
            TextLine(g, name, Body, p.Text, r.Left, line, BodyLine, maxWidth: room);
            if (dr.Temp is double t)
            {
                float x = r.Left + W(g, name, Body) + Space(Body);
                if (x + W(g, Units.TempShort(t), Body) < r.Left + room) TextLine(g, Units.TempShort(t), Body, p.Faint, x, line, BodyLine);
            }
            Level(g, new RectangleF(r.Left, line + BodyLine + 3, r.Width, 4), dr.UsedShare, fill, p);
            y += 29.625f;
        }
    }

    private static void CoresTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float bh = Math.Max(4, r.Height - 22), top = Middle(r, RowH - 5 + 5 + bh) + 0.0f;
        top = MathF.Round(top);
        Cap(g, Lang.T("CPU CORES"), r.Left, top + 1.25f, p, r.Right - 60);
        // The CPU's load and temperature on the right of the heading.
        string temp = Units.TempShort(d.CpuTemp), load = Units.Short(SensorKind.Load, d.CpuLoad), dot = "·";
        float x = r.Right;
        x -= TextRight(g, temp, Body, TempColor(d.CpuTemp, p), x, top, BodyLine) + Space(Body);
        x -= TextRight(g, dot, Body, p.Faint, x, top, BodyLine) + Space(Body);
        TextRight(g, load, Body, p.Text, x, top, BodyLine);
        if (d.Pc?.Cores is not { Length: > 0 } cores) return;
        float gap = 3, bw = (r.Width - gap * (cores.Length - 1)) / cores.Length, by = top + BodyLine + 5;
        if (bw < 1) { gap = 1; bw = Math.Max(0.5f, (r.Width - gap * (cores.Length - 1)) / cores.Length); }
        var fill = Fill(CpuOf(p), p);
        for (int i = 0; i < cores.Length; i++)
        {
            var bar = new RectangleF(r.Left + i * (bw + gap), by, bw, bh);
            FillRound(g, bar, 2, p.Track);
            float h = Math.Max(2, MathF.Round(Math.Clamp(cores[i], 0, 1) * bh));
            FillRound(g, new RectangleF(bar.X, bar.Bottom - h, bw, h), 2, fill);
        }
    }

    // ── Internet ───────────────────────────────────────────────────────────

    /// <summary>"12.4 MB/s" as its number and its unit.</summary>
    private static (string Number, string Unit) Split(string speed) =>
        speed.LastIndexOf(' ') is > 0 and var at ? (speed[..at], speed[(at + 1)..]) : (speed, "");

    private static void NetSpeedTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var pc = d.Pc;
        bool up = r.Height >= 60, foot = r.Height >= 90;
        float gw = r.Width - 92, gh = Math.Max(20, r.Height - (foot ? 18 : 0));
        // The text column: Down and its speed, then Up and its.
        float upTop = 14.375f + 20.9f + (foot ? 6 : 1);
        float textH = up ? upTop + 14.375f + 15.4f : 14.375f + 20.9f;
        float blockH = Math.Max(textH, gw >= 50 ? gh : 0) + (foot ? 3 + 13.125f : 0);
        float top = MathF.Round(Middle(r, blockH));
        var net = Fill(NetColor, p);
        TextLine(g, Lang.T("Down"), 11.5f, p.Muted, r.Left, top, 14.375f);
        var (dn, du) = Split(pc?.NetDown is double down ? Units.Speed(down) : "—");
        float baseline = top + 14.375f + (20.9f - 19 * 1.33f) / 2 + 19 * 1.079f;
        Text(g, dn, 19, FontStyle.Regular, Gray ? p.Text : NetColor, r.Left, TopForBaseline(baseline, 19));
        if (du.Length > 0) Text(g, du, 11, FontStyle.Regular, Gray ? p.Text : NetColor, r.Left + W(g, dn, 19) + Space(19), TopForBaseline(baseline, 11));
        if (up)
        {
            TextLine(g, Lang.T("Up"), 11.5f, p.Muted, r.Left, top + upTop, 14.375f);
            var (un, uu) = Split(pc?.NetUp is double u ? Units.Speed(u) : "—");
            float ub = top + upTop + 14.375f + (15.4f - 14 * 1.33f) / 2 + 14 * 1.079f;
            Text(g, un, 14, FontStyle.Regular, p.Text, r.Left, TopForBaseline(ub, 14));
            if (uu.Length > 0) Text(g, uu, 11, FontStyle.Regular, p.Muted, r.Left + W(g, un, 14) + Space(14), TopForBaseline(ub, 11));
        }
        if (gw >= 50)
        {
            var area = new RectangleF(r.Left + 92, top, gw, gh);
            var downs = pc?.DownHistory ?? [];
            var ups = pc?.UpHistory ?? [];
            // Room above the highest point, as the mock-up's graph has: a peak never runs into the top.
            float hi = Math.Max(1024, Math.Max(downs.Where(float.IsFinite).DefaultIfEmpty(0).Max(), ups.Where(float.IsFinite).DefaultIfEmpty(0).Max())) * 1.2f;
            PointF[] Pts(float[] v) => v.Length < 2 ? [] : [.. v.Select((x, i) => new PointF(area.Left + area.Width * i / (v.Length - 1), area.Bottom - (float.IsFinite(x) ? Math.Clamp(x, 0, hi) : 0) / hi * area.Height))];
            var smooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var dp = Pts(downs);
            if (dp.Length > 1)
            {
                using var path = new GraphicsPath();
                path.AddLines([.. dp, new PointF(area.Right, area.Bottom), new PointF(area.Left, area.Bottom)]);
                using (var b = new SolidBrush(Color.FromArgb(56, net))) g.FillPath(b, path);
                using var pen = new Pen(net, 1.6f) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, dp);
            }
            var upPts = Pts(ups);
            if (upPts.Length > 1)
                using (var pen = new Pen(p.Muted, 1.3f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, upPts);
            g.SmoothingMode = smooth;
        }
        if (foot) TextRight(g, Lang.T("Last minute"), CapPx, p.Faint, r.Right, top + Math.Max(textH, gh) + 3, 13.125f);
    }

    private static void NetNowTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var pc = d.Pc;
        string? none = pc is null ? null : pc.NetPerApp ? Lang.T("No app is using the internet right now.") : Lang.T("Each app's speed shows here while RigSight runs with admin rights.");
        AppList(g, Lang.T("USING THE INTERNET NOW"), pc is null ? null : pc.NetApps ?? [], r, p, 8, none);
    }

    private static void NetTodayTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var f = d.Facts;
        bool has = f is { NetHasData: true } && (f.Holds & PcNeeds.Internet) != 0;
        float blockH = has ? CapHeight + 24.8f + 5 : f is null ? CapHeight : CapHeight + BodyLine;
        float top = Middle(r, blockH);
        float y = top + Cap(g, Lang.T("INTERNET TODAY"), r.Left, top, p, r.Right);
        if (f is null || (f.Holds & PcNeeds.Internet) == 0) return;
        if (!has)
        {
            TextLine(g, Lang.T("No internet use yet today."), Body, p.Muted, r.Left, y, BodyLine, maxWidth: r.Width);
            return;
        }
        float baseline = y + 17.37f;
        string total = Units.Data(f!.NetDown + f.NetUp);
        Text(g, total, 19, FontStyle.Regular, p.Text, r.Left, TopForBaseline(baseline, 19));
        // Down and up on the right; the background use too where there's room for it.
        string both = $"{Lang.T($"Down {Units.Data(f.NetDown)}")} · {Lang.T($"Up {Units.Data(f.NetUp)}")}";
        string all = $"{both} · {Lang.T($"Background {Units.Data(f.NetBackground)}")}";
        float room = r.Width - W(g, total, 19) - 10;
        string side = W(g, all, 11.5f) <= room ? all : both;
        Text(g, side, 11.5f, FontStyle.Regular, p.Muted, r.Right - W(g, side, 11.5f), TopForBaseline(baseline, 11.5f), maxWidth: W(g, side, 11.5f) <= room ? 0 : Math.Max(1, room));
        var bar = new RectangleF(r.Left, y + 24.8f, r.Width, 5);
        FillRound(g, bar, 3, p.Track);
        long sum = f.NetDown + f.NetUp;
        if (sum <= 0) return;
        var state = g.Save();
        using (var clip = RoundRect(bar, 2.5f)) g.SetClip(clip, CombineMode.Intersect);
        float dw = (float)(bar.Width * f.NetDown / sum);
        using (var b = new SolidBrush(Fill(NetColor, p))) g.FillRectangle(b, bar.X, bar.Y, dw, bar.Height);
        using (var b = new SolidBrush(p.Muted)) g.FillRectangle(b, bar.X + dw, bar.Y, bar.Width - dw, bar.Height);
        g.Restore(state);
    }

    private static void NetAppsTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Internet) != 0 ? x : null;
        AppList(g, Lang.T("TOP INTERNET USERS TODAY"), f?.NetApps.Select(a => new TileApp(a.Name, a.Path, a.Amount)).ToList(), r, p, PcFacts.ListMost,
            Lang.T("No internet use yet today."));
    }

    private static void NetHoursTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float y = r.Top + Cap(g, Lang.T("INTERNET USE BY HOUR"), r.Left, r.Top, p, r.Right);
        float bh = Math.Max(4, r.Height - 34);
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Internet) != 0 ? x : null;
        float gap = 2, bw = (r.Width - gap * 23) / 24;
        if (f is not null)
        {
            long most = Math.Max(1, Enumerable.Range(0, 24).Max(h => f.HourTop[h] + f.HourRest[h]));
            var net = Fill(NetColor, p);
            for (int h = 0; h < 24; h++)
            {
                float x0 = r.Left + h * (bw + gap);
                float top = MathF.Round((float)(f.HourTop[h] * (double)bh / most)), rest = MathF.Round((float)(f.HourRest[h] * (double)bh / most));
                if (f.HourTop[h] + f.HourRest[h] > 0 && top + rest < 1) top = 1;
                using (var b = new SolidBrush(net)) g.FillRectangle(b, x0, y + bh - top, bw, top);
                FillTopRound(g, new RectangleF(x0, y + bh - top - rest, bw, rest), 2, p.Muted);
            }
        }
        using (var line = new Pen(p.Track, 1)) g.DrawLine(line, r.Left, y + bh + 0.5f, r.Right, y + bh + 0.5f);
        float ly = y + bh + 1 + 2;
        var day = Now().Date;
        TextLine(g, HourText(day), CapPx, p.Faint, r.Left, ly, 13.125f);
        string noon = HourText(day.AddHours(12));
        TextLine(g, noon, CapPx, p.Faint, r.Left + (r.Width - W(g, noon, CapPx)) / 2, ly, 13.125f);
        TextRight(g, HourText(day.AddHours(23)), CapPx, p.Faint, r.Right, ly, 13.125f);
    }

    private static void NetDropsTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Internet) != 0 ? x : null;
        float blockH = CapHeight + 18.75f + 6 + 5;
        float top = Middle(r, blockH);
        float y = top + Cap(g, Lang.T("CONNECTION TODAY"), r.Left, top, p, r.Right);
        if (f is { NetHasData: true }) TextLine(g, DropWords.Count(f.Drops.Count), 15, p.Text, r.Left, y, 18.75f, maxWidth: r.Width);
        var strip = new RectangleF(r.Left, y + 18.75f + 6, r.Width, 5);
        var state = g.Save();
        FillRound(g, strip, 2.5f, p.Track);
        using (var clip = RoundRect(strip, 2.5f)) g.SetClip(clip, CombineMode.Intersect);
        if (f is { NetHasData: true })
        {
            // The quarter hours the PC was online, in the colour of "all well"; each drop over them in the colour of a problem.
            float q = strip.Width / 96;
            using (var good = new SolidBrush(Good(p)))
                for (int i = 0; i < f.Quarters.Length && i < 96; i++)
                {
                    if (!f.Quarters[i] || i > 0 && f.Quarters[i - 1]) continue;
                    int end = i;
                    while (end + 1 < 96 && end + 1 < f.Quarters.Length && f.Quarters[end + 1]) end++;
                    g.FillRectangle(good, strip.X + i * q, strip.Y, (end - i + 1) * q, strip.Height);
                }
            var day = Now().Date;
            using var bad = new SolidBrush(Bad(p));
            foreach (var (from, to) in f.Drops)
            {
                float a = (float)((from - day).TotalMinutes / 1440 * strip.Width), b = (float)((to - day).TotalMinutes / 1440 * strip.Width);
                a = Math.Clamp(a, 0, strip.Width);
                b = Math.Clamp(Math.Max(b, a + 2), 0, strip.Width);
                g.FillRectangle(bad, strip.X + a, strip.Y, b - a, strip.Height);
            }
        }
        g.Restore(state);
    }

    // ── Your day ───────────────────────────────────────────────────────────

    private static void TodaySoFarTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var t = d.Today;
        (string Cap, string Value)[] cols = [(Lang.T("ACTIVE"), Units.Duration(t.ActiveSec)), (Lang.T("PC ON"), Units.Duration(t.OnSec)), (Lang.T("AWAY"), Units.Duration(t.IdleSec))];
        float top = Middle(r, CapHeight + 22), cw = (r.Width - 12) / 3;
        for (int i = 0; i < 3; i++)
        {
            float x = r.Left + i * (cw + 6);
            Cap(g, cols[i].Cap, x, top, p, x + cw);
            Big(g, cols[i].Value, x, top + CapHeight, p.Text, cw);
        }
    }

    /// <summary>A big figure (the mock-up's ".big" at 22 px, regular): its line box is as tall as the font.</summary>
    private static void Big(Graphics g, string text, float x, float top, Color color, float maxWidth = 0) =>
        Text(g, text, 22, FontStyle.Regular, color, x, top + (22 - 22 * 1.33f) / 2 - InkLift, maxWidth: maxWidth);

    /// <summary>A big figure and a few muted words after it on one line (8h 04m active); the line's height with the room under it.</summary>
    private static float BigAndWords(Graphics g, string big, string words, float x, float top, Palette p, float right)
    {
        float baseline = top + 20.11f;
        Text(g, big, 22, FontStyle.Regular, p.Text, x, TopForBaseline(baseline, 22));
        float wx = x + W(g, big, 22) + Space(Body);
        if (wx < right - 10) Text(g, words, Body, FontStyle.Regular, p.Muted, wx, TopForBaseline(baseline, Body), maxWidth: right - wx);
        return 22.75f;
    }

    private static void MostUsedTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float y = r.Top + Cap(g, Lang.T("MOST USED TODAY"), r.Left, r.Top, p, r.Right);
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Today) != 0 ? x : null;
        if (f is null) return;
        if (f.MostUsed.Count == 0)
        {
            TextIn(g, Lang.T("Nothing yet today."), Body, p.Muted, RectangleF.FromLTRB(r.Left, y + 2.5f, r.Right, r.Bottom));
            return;
        }
        int n = Math.Min(f.MostUsed.Count, Fits(r.Height, 16, 31, f.MostUsed.Count));
        for (int i = 0; i < n; i++)
        {
            var a = f.MostUsed[i];
            float row = y + 1;
            float vw = TextRight(g, a.Amount, Body, p.Text, r.Right, row, 16);
            AppIcon(g, a.Name, a.Path, new RectangleF(r.Left, row, 16, 16), p);
            TextLine(g, a.Name, Body, p.Text, r.Left + 23, row, 16, maxWidth: Math.Max(1, r.Right - vw - 10 - r.Left - 23));
            Level(g, new RectangleF(r.Left, row + 16 + 3, r.Width, 4), a.Share, Fill(IconColor(a.Path) ?? BoxAccentNow, p), p);
            y += 30;
        }
    }

    /// <summary>The accent of the box being drawn (for a bar with no colour of its own).</summary>
    [ThreadStatic] private static Color BoxAccentNow;

    private static void StandOutTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Today) != 0 ? x : null;
        var lines = f?.StandOut ?? [];
        int n = f is null ? 0 : Math.Min(Math.Max(1, lines.Count), Fits(r.Height, 16, 22, Math.Max(1, lines.Count)));
        float top = Middle(r, CapHeight + n * (BodyLine + 6));
        float y = top + Cap(g, Lang.T("WHAT STANDS OUT"), r.Left, top, p, r.Right);
        if (f is null) return;
        if (lines.Count == 0)
        {
            TextLine(g, Lang.T("Highlights appear once there's a bit of today to talk about."), Body, p.Muted, r.Left, y + 3, BodyLine, maxWidth: r.Width);
            return;
        }
        for (int i = 0; i < n; i++, y += BodyLine + 6)
        {
            using (var dot = new SolidBrush(p.Muted))
            {
                var smooth = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(dot, r.Left, y + 3 + (BodyLine - 7) / 2, 7, 7);
                g.SmoothingMode = smooth;
            }
            TextLine(g, lines[i], Body, p.Text, r.Left + 16, y + 3, BodyLine, maxWidth: Math.Max(1, r.Width - 16));
        }
    }

    private static void PeaksTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var t = d.Today;
        float top = Middle(r, CapHeight + 22 + 3 + 14.375f), cw = (r.Width - 10) / 2;
        (string Cap, double? Peak, string? Line)[] cols = [(Lang.T("CPU PEAK"), t.CpuPeak, t.CpuPeakLine), (Lang.T("GPU PEAK"), t.GpuPeak, t.GpuPeakLine)];
        for (int i = 0; i < 2; i++)
        {
            float x = r.Left + i * (cw + 10);
            Cap(g, cols[i].Cap, x, top, p, x + cw);
            Big(g, Units.TempShort(cols[i].Peak), x, top + CapHeight, TempColor(cols[i].Peak, p), cw);
            if (cols[i].Line is { Length: > 0 } line) TextLine(g, line, 11.5f, p.Muted, x, top + CapHeight + 22 + 3, 14.375f, maxWidth: cw);
        }
    }

    private static void YesterdayTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float y = r.Top + Cap(g, Lang.T("YESTERDAY"), r.Left, r.Top, p, r.Right);
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Yesterday) != 0 ? x : null;
        if (f is null) return;
        if (!f.YesterdayHasData)
        {
            TextIn(g, f.YesterdayWhy ?? Lang.T($"Not recorded {Lang.T("yesterday")}"), Body, p.Muted, RectangleF.FromLTRB(r.Left, y + 2.5f, r.Right, r.Bottom));
            return;
        }
        y += BigAndWords(g, Units.Duration(f.YesterdayActiveSec), Lang.T("active"), r.Left, y, p, r.Right) + 4;
        if (f.YesterdayApps.Count == 0) return;
        int n = Math.Min(f.YesterdayApps.Count, Fits(r.Height, 46, AppRowH, f.YesterdayApps.Count));
        for (int i = 0; i < n && y + AppRowH <= r.Bottom + 2; i++, y += AppRowH)
            AppRow(g, new TileApp(f.YesterdayApps[i].Name, f.YesterdayApps[i].Path, f.YesterdayApps[i].Amount), y, r, p);
    }

    private static void CrashesTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float y = r.Top + Cap(g, Lang.T("CRASHES"), r.Left, r.Top, p, r.Right);
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Crashes) != 0 ? x : null;
        if (f is null) return;
        y += BigAndWords(g, f.Crashes.ToString(CultureInfo.CurrentCulture), Lang.T("in the last 30 days"), r.Left, y, p, r.Right);
        if (f.LatestCrash is not { } latest || y + 8 + CapHeight + 16 > r.Bottom + 4) return;
        y += 8;
        y += Cap(g, Lang.T("LATEST"), r.Left, y, p, r.Right);
        // The sentence over as many lines as there's room for, cut at a word with "…" where it runs out.
        float lines = Math.Max(1, MathF.Floor((r.Bottom - y + 2) / 16.25f));
        TextIn(g, latest, Body, p.Text, new RectangleF(r.Left, y + (16.25f - BodyLine) / 2 - 1, r.Width, lines * 16.25f + 2));
    }

    private static void HealthTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float y = r.Top + Cap(g, Lang.T("PC HEALTH"), r.Left, r.Top, p, r.Right);
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Health) != 0 ? x : null;
        if (f is null || f.Health.Count == 0) return;
        int n = Math.Min(f.Health.Count, Fits(r.Height, 16, 23, f.Health.Count));
        for (int i = 0; i < n; i++, y += BodyLine + 7)
        {
            var line = f.Health[i];
            var color = line.NeedsLook ? Warm(p) : p.Text;
            if (line.Icon.Length > 0) IconIn(g, line.Icon, 12, line.NeedsLook ? color : p.Muted, new RectangleF(r.Left, y + 3.5f + (BodyLine - 14) / 2, 14, 14));
            TextLine(g, line.Text, Body, color, r.Left + 23, y + 3.5f, BodyLine, maxWidth: Math.Max(1, r.Width - 23));
        }
    }

    private static void ChangedTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        float y = r.Top + Cap(g, Lang.T("WHAT CHANGED LATELY"), r.Left, r.Top, p, r.Right);
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Changes) != 0 ? x : null;
        if (f is null) return;
        if (f.Changes.Count == 0)
        {
            TextLine(g, Lang.T("Nothing has changed in the last 30 days."), Body, p.Muted, r.Left, y + 2.5f, BodyLine, maxWidth: r.Width);
            return;
        }
        int n = Math.Min(f.Changes.Count, Fits(r.Height, 16, 21, f.Changes.Count));
        for (int i = 0; i < n; i++, y += RowH)
        {
            var c = f.Changes[i];
            var color = c.Tone is null ? p.Text : c.Tone == EffectTone.Worse ? Warm(p) : Good(p);
            float ww = TextRight(g, c.When, Body, p.Muted, r.Right, y + 2.5f, BodyLine);
            TextLine(g, c.Text, Body, color, r.Left, y + 2.5f, BodyLine, maxWidth: Math.Max(1, r.Width - ww - 10));
        }
    }

    private static void WeekTile(Graphics g, RectangleF r, WidgetData d, Palette p, Color accent)
    {
        float bh = Math.Max(4, r.Height - 36);
        float top = Middle(r, CapHeight + bh + 3 + 13.125f);
        float y = top + Cap(g, Lang.T("THE LAST 7 DAYS"), r.Left, top, p, r.Right);
        var f = d.Facts is { } x && (x.Holds & PcNeeds.Week) != 0 ? x : null;
        var today = Now().Date;
        var days = new List<(DateTime Day, double Sec)>();
        for (int i = 6; i >= 1; i--)
            days.Add((today.AddDays(-i), f?.Days.FirstOrDefault(v => v.Day == today.AddDays(-i)).ActiveSec ?? 0));
        days.Add((today, d.Today.ActiveSec));
        double scale = Math.Max(3600, days.Max(v => v.Sec)) * 1.04;
        float gap = 6, bw = (r.Width - gap * 6) / 7;
        var dtf = Culture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
        {
            float x0 = r.Left + i * (bw + gap);
            if (f is not null || i == 6)
            {
                float h = Math.Max(3, MathF.Round((float)(days[i].Sec / scale * bh)));
                var color = i == 6 ? (Gray ? p.Text : accent) : Color.FromArgb(140, p.Muted);
                FillTopRound(g, new RectangleF(x0, y + bh - h, bw, h), 3, color);
            }
            string name = dtf.GetShortestDayName(days[i].Day.DayOfWeek);
            name = name.Length > 0 ? name[..1].ToUpper(Culture) : "";
            TextLine(g, name, CapPx, p.Faint, x0 + (bw - W(g, name, CapPx)) / 2, y + bh + 3, 13.125f);
        }
    }

    private static void LastGameTile(Graphics g, RectangleF r, WidgetData d, Palette p)
    {
        var f = d.Facts is { } x && (x.Holds & PcNeeds.LastGame) != 0 ? x : null;
        var game = f?.LastGame;
        float s = MathF.Round(Math.Min(r.Height, 40));
        float midH = CapHeight + 17.5f;
        float top = MathF.Round(Middle(r, Math.Max(s, midH)));
        float my = top + (Math.Max(s, midH) - midH) / 2;
        float tx = r.Left + s + 10;
        bool average = r.Width >= 260 && game?.Fps is not null;
        string fps = game?.Fps is double v ? Units.Format(SensorKind.FrameRate, v) : "";
        float right = r.Right;
        if (average)
        {
            float aw = Math.Max(CapWidth(g, Lang.T("AVERAGE")), W(g, fps, Body));
            float ax = r.Right - aw;
            Cap(g, Lang.T("AVERAGE"), ax, my, p);
            TextLine(g, fps, Body, p.Text, ax, my + CapHeight, 17.5f);
            right = ax - 10;
        }
        var iconBox = new RectangleF(r.Left, top + (Math.Max(s, midH) - s) / 2, s, s);
        if (game is null)
        {
            GlyphSquare(g, iconBox, "", p, 9);
            Cap(g, Lang.T("LAST GAME"), tx, my, p, right);
            if (f is not null) TextLine(g, Lang.T("No games played yet"), Body, p.Muted, tx, my + CapHeight, 17.5f, maxWidth: Math.Max(1, right - tx));
            return;
        }
        if (IconFor(game.Path) is { } icon && Alive(icon))
        {
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, iconBox);
            g.InterpolationMode = mode;
        }
        else AppIcon(g, game.Name, null, iconBox, p, 9);
        Cap(g, Lang.T("LAST GAME"), tx, my, p, right);
        // The game's name, then when and for how long, on one line, cut with "…" where it runs out.
        float baseline = my + CapHeight + (17.5f - 14 * 1.33f) / 2 + 14 * 1.079f;
        float nameW = Math.Min(W(g, game.Name, 14, true), Math.Max(1, right - tx));
        Text(g, game.Name, 14, FontStyle.Bold, p.Text, tx, TopForBaseline(baseline, 14), semibold: true, maxWidth: Math.Max(1, right - tx));
        float wx = tx + nameW + Space(14);
        if (wx < right - 12)
            Text(g, PlayedWhen(game, Now()), 12, FontStyle.Regular, p.Muted, wx, TopForBaseline(baseline, 12), maxWidth: right - wx);
    }

    /// <summary>"yesterday, 1h 12m": when the last play was, and how long it was in front.</summary>
    internal static string PlayedWhen(LastPlayLine game, DateTime now)
    {
        int days = (now.Date - game.Ended.Date).Days;
        string when = days <= 0 ? Lang.T("today") : days == 1 ? Lang.T("yesterday") : days < 7 ? DayName(game.Ended) : game.Ended.ToString("d MMM", Culture);
        return Lang.T($"{when}, {Units.Duration(game.ActiveSec)}");
    }

    /// <summary>Draws one of the PC's tiles; false for any other kind.</summary>
    private static bool PcTile(Graphics g, WidgetTile t, RectangleF r, WidgetData d, Palette p, Color accent)
    {
        BoxAccentNow = accent;
        switch (t.Kind)
        {
            case WidgetTiles.CpuGauge: GaugeTile(g, false, r, d, p); break;
            case WidgetTiles.GpuGauge: GaugeTile(g, true, r, d, p); break;
            case WidgetTiles.Fans: FansTile(g, r, d, p); break;
            case WidgetTiles.Drives: DrivesTile(g, r, d, p); break;
            case WidgetTiles.Cores: CoresTile(g, r, d, p); break;
            case WidgetTiles.TopCpu: AppList(g, Lang.T("TOP CPU USERS"), d.Pc?.TopCpu, r, p, WidgetTiles.RowsOf(t), null); break;
            case WidgetTiles.TopMemory: AppList(g, Lang.T("TOP MEMORY USERS"), d.Pc?.TopMemory, r, p, 8, null); break;
            case WidgetTiles.NetSpeed: NetSpeedTile(g, r, d, p); break;
            case WidgetTiles.NetNow: NetNowTile(g, r, d, p); break;
            case WidgetTiles.NetToday: NetTodayTile(g, r, d, p); break;
            case WidgetTiles.NetApps: NetAppsTile(g, r, d, p); break;
            case WidgetTiles.NetHours: NetHoursTile(g, r, d, p); break;
            case WidgetTiles.NetDrops: NetDropsTile(g, r, d, p); break;
            case WidgetTiles.TodaySoFar: TodaySoFarTile(g, r, d, p); break;
            case WidgetTiles.MostUsed: MostUsedTile(g, r, d, p); break;
            case WidgetTiles.StandOut: StandOutTile(g, r, d, p); break;
            case WidgetTiles.Peaks: PeaksTile(g, r, d, p); break;
            case WidgetTiles.Yesterday: YesterdayTile(g, r, d, p); break;
            case WidgetTiles.Crashes: CrashesTile(g, r, d, p); break;
            case WidgetTiles.Health: HealthTile(g, r, d, p); break;
            case WidgetTiles.Changed: ChangedTile(g, r, d, p); break;
            case WidgetTiles.Week: WeekTile(g, r, d, p, accent); break;
            case WidgetTiles.LastGame: LastGameTile(g, r, d, p); break;
            default: return false;
        }
        return true;
    }

    /// <summary>
    /// The part of a box's change key for the PC's tiles (null: not one of them): everything the tile shows, as text, so
    /// a widget is drawn again only when what it shows changes (a list's order, a figure as written, a fact read again).
    /// </summary>
    private static string? PcKey(WidgetTile t, WidgetData d, Palette p)
    {
        var pc = d.Pc;
        var f = d.Facts;
        string Apps(IEnumerable<TileApp>? apps) => apps is null ? "-" : string.Join(";", apps.Select(a => a.Name + "=" + a.Value + "@" + a.Path));
        string Lines(IEnumerable<AppLine>? apps) => apps is null ? "-" : string.Join(";", apps.Select(a => a.Name + "=" + a.Amount + "@" + a.Share.ToString("0.00", CultureInfo.InvariantCulture)));
        string Holds(PcNeeds need) => f is null || (f.Holds & need) == 0 ? "-" : "+";
        return t.Kind switch
        {
            WidgetTiles.CpuGauge => $"{Units.TempShort(d.CpuTemp)}#{TempColor(d.CpuTemp, p).ToArgb()}|{Units.Short(SensorKind.Load, d.CpuLoad)}",
            WidgetTiles.GpuGauge => $"{Units.TempShort(d.GpuTemp)}#{TempColor(d.GpuTemp, p).ToArgb()}|{Units.Short(SensorKind.Load, d.GpuLoad)}",
            WidgetTiles.Fans => pc?.Fans is { } fans ? string.Join(";", fans.Select(x => x.Name + "=" + (x.Rpm is double v ? Units.Format(SensorKind.Fan, v) : "-"))) : "-",
            WidgetTiles.Drives => pc?.Drives is { } drives ? string.Join(";", drives.Select(x => $"{x.Letter}={x.UsedShare:0.000}/{Units.Bytes(x.Free)}/{Units.TempShort(x.Temp)}")) : "-",
            WidgetTiles.Cores => $"{Units.Short(SensorKind.Load, d.CpuLoad)}|{Units.TempShort(d.CpuTemp)}#{TempColor(d.CpuTemp, p).ToArgb()}|"
                + (pc?.Cores is { } cores ? string.Join(",", cores.Select(c => ((int)(c * 100)).ToString(CultureInfo.InvariantCulture))) : "-"),
            WidgetTiles.TopCpu => $"{t.Rows}|{Apps(pc?.TopCpu)}",
            WidgetTiles.TopMemory => Apps(pc?.TopMemory),
            // The graph moves every second: drawn each time (a key that changes each second says the same).
            WidgetTiles.NetSpeed => $"{pc?.NetDown}|{pc?.NetUp}|{pc?.DownHistory.Length}|{string.Join(",", pc?.DownHistory.TakeLast(3) ?? [])}",
            WidgetTiles.NetNow => $"{pc?.NetPerApp}|{(pc is null ? "-" : Apps(pc.NetApps ?? []))}",
            WidgetTiles.NetToday or WidgetTiles.NetApps or WidgetTiles.NetHours or WidgetTiles.NetDrops => Holds(PcNeeds.Internet) + (f is null ? "" :
                $"{f.NetHasData}|{f.NetDown}|{f.NetUp}|{f.NetBackground}|{Lines(f.NetApps)}|{string.Join(",", f.HourTop)}|{string.Join(",", f.HourRest)}|{string.Join("", f.Quarters.Select(q => q ? '1' : '0'))}|{string.Join(",", f.Drops.Select(x => x.Start.Ticks + "-" + x.End.Ticks))}|{Now().Date.Ticks}"),
            WidgetTiles.TodaySoFar => $"{Units.Duration(d.Today.ActiveSec)}|{Units.Duration(d.Today.OnSec)}|{Units.Duration(d.Today.IdleSec)}",
            WidgetTiles.MostUsed => Holds(PcNeeds.Today) + Lines(f?.MostUsed),
            WidgetTiles.StandOut => Holds(PcNeeds.Today) + string.Join(";", f?.StandOut ?? []),
            WidgetTiles.Peaks => $"{Units.TempShort(d.Today.CpuPeak)}#{TempColor(d.Today.CpuPeak, p).ToArgb()}|{d.Today.CpuPeakLine}|{Units.TempShort(d.Today.GpuPeak)}#{TempColor(d.Today.GpuPeak, p).ToArgb()}|{d.Today.GpuPeakLine}",
            WidgetTiles.Yesterday => Holds(PcNeeds.Yesterday) + $"{f?.YesterdayHasData}|{f?.YesterdayActiveSec:0}|{f?.YesterdayWhy}|{Lines(f?.YesterdayApps)}",
            WidgetTiles.Crashes => Holds(PcNeeds.Crashes) + $"{f?.Crashes}|{f?.LatestCrash}",
            WidgetTiles.Health => Holds(PcNeeds.Health) + string.Join(";", f?.Health.Select(h => h.Icon + h.Text + h.NeedsLook) ?? []),
            WidgetTiles.Changed => Holds(PcNeeds.Changes) + string.Join(";", f?.Changes.Select(c => c.Text + "|" + c.When + "|" + c.Tone) ?? []),
            WidgetTiles.Week => Holds(PcNeeds.Week) + string.Join(",", f?.Days.Select(x => (int)(x.ActiveSec / 60)) ?? []) + $"|{(int)(d.Today.ActiveSec / 60)}|{Now().Date.Ticks}",
            WidgetTiles.LastGame => Holds(PcNeeds.LastGame) + (f?.LastGame is { } lg ? $"{lg.Name}|{lg.Path}|{PlayedWhen(lg, Now())}|{lg.Fps:0}" : "-"),
            _ => null,
        };
    }
}

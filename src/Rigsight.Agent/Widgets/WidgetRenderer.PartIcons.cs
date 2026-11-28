using System.Drawing;
using System.Drawing.Drawing2D;

#if RIGSIGHT_APP
// Linked into the app for the Taskbar page's preview, in a namespace of its own there (the tests see both assemblies).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

/// <summary>
/// The parts' icons on the taskbar (2026-11-20, the user: icons for every reading, from the approved mock-up): in place of a
/// reading's word, under its number or beside it. One a part (see TrayParts.IconKey). A CPU, GPU, RAM stick, drive, board,
/// the network's arrows, ping and frame rate are drawn here, as outlines on a 13 px grid: Windows' icon font has none that
/// reads at this size. A mouse, keyboard, headset, controller, fan and any other device come from that font (Segoe Fluent
/// Icons). Lines sit on whole pixels at every scale, so they're as sharp as the clock beside them.
/// </summary>
internal static partial class WidgetRenderer
{
    private enum Stroke { Line, Rect, Round, Circle, Dot, Poly, Fill }

    private readonly record struct Mark(Stroke Kind, float[] P);

    private static Mark L(float x1, float y1, float x2, float y2) => new(Stroke.Line, [x1, y1, x2, y2]);
    private static Mark R(float x, float y, float w, float h) => new(Stroke.Rect, [x, y, w, h]);
    private static Mark Rr(float x, float y, float w, float h) => new(Stroke.Round, [x, y, w, h]);
    private static Mark C(float x, float y, float w, float h) => new(Stroke.Circle, [x, y, w, h]);
    private static Mark D(float x, float y, float w, float h) => new(Stroke.Dot, [x, y, w, h]);
    private static Mark P(params float[] points) => new(Stroke.Poly, points);
    private static Mark F(params float[] points) => new(Stroke.Fill, points);

    // Each icon: its width on the 13-high grid, and its marks. Lines on whole numbers: a 1 px pen at 100% lands on whole pixels.
    private static readonly Dictionary<string, (int Width, Mark[] Marks)> Drawn = new()
    {
        ["cpu"] = (13, [Rr(3, 3, 6, 6), L(5, 1, 5, 2), L(7, 1, 7, 2), L(5, 10, 5, 11), L(7, 10, 7, 11), L(1, 5, 2, 5), L(1, 7, 2, 7), L(10, 5, 11, 5), L(10, 7, 11, 7)]),
        ["gpu"] = (15, [Rr(1, 3, 12, 6), C(4, 4, 4, 4), L(11, 5, 11, 7), L(3, 9, 3, 11)]),
        ["ram"] = (15, [R(1, 3, 12, 5), L(3, 5, 4, 5), L(6, 5, 7, 5), L(9, 5, 10, 5), L(3, 9, 3, 10), L(6, 9, 6, 10), L(9, 9, 9, 10), L(12, 9, 12, 10)]),
        ["drive"] = (13, [Rr(1, 3, 10, 6), D(7.5f, 5.5f, 2, 2), L(3, 6, 5, 6)]),
        ["board"] = (13, [Rr(1, 1, 10, 10), R(3, 3, 3, 3), L(8, 3, 8, 8), L(3, 8, 6, 8)]),
        ["down"] = (11, [L(5, 1, 5, 10), P(2, 7, 5, 10, 8, 7)]),
        ["up"] = (11, [L(5, 11, 5, 2), P(2, 5, 5, 2, 8, 5)]),
        ["ping"] = (15, [P(1, 7, 4, 7, 5, 3, 8, 10, 9, 7, 13, 7)]),
        ["fps"] = (15, [Rr(1, 1, 12, 8), L(5, 11, 9, 11), L(7, 9, 7, 11), F(6, 3, 9, 5, 6, 7)]),
    };

    /// <summary>The ones from Windows' icon font.</summary>
    private static string? FontIcon(string key) => key switch
    {
        "mouse" => "\uE962",
        "keyboard" => "\uE765",
        "headset" => "\uE7F6",
        "controller" => "\uE7FC",
        "fan" => "\uEDA8",
        "device" => "\uE83F",
        _ => null,
    };

    private static readonly Dictionary<float, Font?> IconFonts = [];

    /// <summary>Windows' icon font at <paramref name="px"/> pixels (Segoe Fluent Icons, else Segoe MDL2 Assets on Windows 10).</summary>
    private static Font? IconFont(float px)
    {
        lock (IconFonts)
        {
            if (IconFonts.TryGetValue(px, out var cached)) return cached;
            var family = FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons") ?? FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets");
            // Bold (DirectWrite thickens the font's one weight): at 13 px its 1 px outlines read thin and faint beside the
            // number's semibold digits (the user, 2026-11-20).
            var font = family is null ? null : new Font(family, px, FontStyle.Bold, GraphicsUnit.Pixel);
            IconFonts[px] = font;
            return font;
        }
    }

    /// <summary>An icon's height in pixels: <paramref name="px"/> at 100%, at the screen's <paramref name="scale"/>, on whole pixels.</summary>
    internal static float PartIconHeight(float px, float scale) => MathF.Round(px * scale);

    /// <summary>An icon's width at height <paramref name="h"/>, on whole pixels.</summary>
    internal static float PartIconWidth(string key, float h) => MathF.Round(h * (Drawn.TryGetValue(key, out var d) ? d.Width : 13) / 13f);

    /// <summary>Draws icon <paramref name="key"/> with its top left at (<paramref name="x"/>, <paramref name="y"/>), <paramref name="h"/> px tall.</summary>
    internal static void DrawPartIcon(Graphics g, string key, float x, float y, float h, Color color)
    {
        x = MathF.Round(x);
        y = MathF.Round(y);
        float w = PartIconWidth(key, h);
        if (FontIcon(key) is { } glyph)
        {
            // Through DirectWrite, as the taskbar's text: hinted to the pixel grid, so as sharp as the clock. The icon font's
            // glyphs sit in their em square on the baseline; centred across by the pixels they ink.
            var font = IconFont(h);
            if (font is not null)
            {
                float left = DirectWriteText.Ink(glyph, font) is { } ink ? x + (w - (ink.Right - ink.Left)) / 2 - ink.Left : x;
                if (DirectWriteText.Draw(g, glyph, font, color, MathF.Round(left), (int)(y + h))) return;
            }
            IconIn(g, glyph, h, color, new RectangleF(x, y, w, h));
            return;
        }
        if (!Drawn.TryGetValue(key, out var icon)) return;
        float k = h / 13f;
        float pen = MathF.Max(1, MathF.Floor(k + 0.25f));
        // A 1 px line on a whole number is on a pixel's middle; a 2 px one between two pixels.
        float shift = pen % 2 == 1 ? 0 : 0.5f;
        float X(float v) => x + MathF.Round(v * k) + shift;
        float Y(float v) => y + MathF.Round(v * k) + shift;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.None;
        using var stroke = new Pen(color, pen) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(color);
        foreach (var m in icon.Marks)
        {
            var p = m.P;
            switch (m.Kind)
            {
                case Stroke.Line:
                    g.DrawLine(stroke, X(p[0]), Y(p[1]), X(p[2]), Y(p[3]));
                    break;
                case Stroke.Rect:
                    g.DrawRectangle(stroke, X(p[0]), Y(p[1]), X(p[0] + p[2]) - X(p[0]), Y(p[1] + p[3]) - Y(p[1]));
                    break;
                case Stroke.Round:
                {
                    float l = X(p[0]), t = Y(p[1]), r = X(p[0] + p[2]), b = Y(p[1] + p[3]), c = MathF.Max(1, MathF.Round(1.5f * k));
                    using var path = new GraphicsPath();
                    path.AddArc(l, t, 2 * c, 2 * c, 180, 90);
                    path.AddArc(r - 2 * c, t, 2 * c, 2 * c, 270, 90);
                    path.AddArc(r - 2 * c, b - 2 * c, 2 * c, 2 * c, 0, 90);
                    path.AddArc(l, b - 2 * c, 2 * c, 2 * c, 90, 90);
                    path.CloseFigure();
                    g.DrawPath(stroke, path);
                    break;
                }
                case Stroke.Circle:
                    g.DrawEllipse(stroke, X(p[0]), Y(p[1]), X(p[0] + p[2]) - X(p[0]), Y(p[1] + p[3]) - Y(p[1]));
                    break;
                case Stroke.Dot:
                    g.FillEllipse(fill, x + p[0] * k, y + p[1] * k, p[2] * k, p[3] * k);
                    break;
                case Stroke.Poly:
                case Stroke.Fill:
                {
                    var points = new PointF[p.Length / 2];
                    for (int i = 0; i < points.Length; i++) points[i] = new PointF(X(p[2 * i]), Y(p[2 * i + 1]));
                    if (m.Kind == Stroke.Fill) g.FillPolygon(fill, points);
                    else g.DrawLines(stroke, points);
                    break;
                }
            }
        }
        g.Restore(state);
    }

    // ── The charging bolt (1.22.0) ───────────────────────────────────────

    /// <summary>
    /// What a reading's number ends with while its device charges or is full on power (see DeviceCharge.TileBolt): drawn
    /// as a bolt of RigSight's own after the digits, in the number's colour, never as the font's character (the strip's
    /// fonts have none, and an emoji would come in colour).
    /// </summary>
    internal const char BoltMark = '\u26A1';

    /// <summary>A number's text without its bolt, and whether it had one.</summary>
    internal static (string Text, bool Bolt) SplitBolt(string text) =>
        text.Length > 0 && text[^1] == BoltMark ? (text[..^1], true) : (text, false);

    /// <summary>The room a bolt takes after a number in a font <paramref name="size"/> px tall: the gap before it and its width, on whole pixels.</summary>
    internal static float BoltWidth(float size) => BoltGap(size) + MathF.Round(size * 0.42f);

    private static float BoltGap(float size) => MathF.Max(1, MathF.Round(size * 0.1f));

    /// <summary>A bolt as tall as the digits of a font <paramref name="size"/> px tall, standing on <paramref name="baseline"/>, after its gap from <paramref name="x"/>.</summary>
    internal static void DrawBolt(Graphics g, float x, float baseline, float size, Color color)
    {
        float w = MathF.Round(size * 0.42f), h = MathF.Round(size * 0.72f);
        float l = MathF.Round(x + BoltGap(size)), t = MathF.Round(baseline) - h;
        PointF[] bolt =
        [
            new(l + w * 0.66f, t), new(l + w * 0.04f, t + h * 0.58f), new(l + w * 0.44f, t + h * 0.58f),
            new(l + w * 0.30f, t + h), new(l + w * 0.96f, t + h * 0.40f), new(l + w * 0.56f, t + h * 0.40f),
        ];
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.None;
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, bolt);
        g.Restore(state);
    }
}

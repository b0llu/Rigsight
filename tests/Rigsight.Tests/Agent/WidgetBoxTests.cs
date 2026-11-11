using System.Drawing;
using System.Drawing.Imaging;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Widgets as boxes of tiles (approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U): every kind of tile draws at every size, in every
/// look, and a clock or a date set to the middle is in the middle by its ink, measured on the picture (the user,
/// 2026-11-10: "none of the clocks are aligned properly at center, just make sure you do that when building").
/// </summary>
[Collection("Drawing")]
public class WidgetBoxTests : IDisposable
{
    private readonly Func<DateTime> _now = WidgetRenderer.Now;

    public WidgetBoxTests() => WidgetRenderer.Now = () => new DateTime(2026, 11, 10, 9, 14, 32);

    public void Dispose() => WidgetRenderer.Now = _now;

    private static WidgetConfig Box(params WidgetTile[] tiles) => new() { Style = WidgetStyle.Custom, Id = "custom-test", Tiles = [.. tiles], BackgroundOpacity = 1 };

    private static WidgetTile Tile(string kind, int w = 0, int h = 0, Action<WidgetTile>? set = null)
    {
        var t = WidgetTiles.New(WidgetTiles.Kind(kind)!);
        if (w > 0) t.W = w;
        if (h > 0) t.H = h;
        set?.Invoke(t);
        return t;
    }

    /// <summary>Pictures of what's drawn, for a person to look at: %TEMP%\rigsight-tests\boxes.</summary>
    private static void Keep(Bitmap bmp, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "boxes");
        Directory.CreateDirectory(dir);
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
    }

    public static TheoryData<string> Kinds()
    {
        var data = new TheoryData<string>();
        foreach (var k in WidgetTiles.Kinds) data.Add(k.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_tile_draws_at_every_size_in_every_look(string kind)
    {
        var k = WidgetTiles.Kind(kind)!;
        foreach (var (name, data) in AllData())
            foreach (var (w, h) in new[] { (k.MinW, k.MinH), (k.W, k.H), (k.W * 2, k.H * 2), (k.MinW, 1 + k.MinH * 3) })
                foreach (var theme in Enum.GetValues<WidgetTheme>())
                    foreach (bool gray in new[] { false, true })
                    {
                        var cfg = Box(Tile(kind, w, h, t =>
                        {
                            t.Style = WidgetTiles.ClockStyles[(w + h) % WidgetTiles.ClockStyles.Count];
                            t.Look = WidgetTiles.Looks(t.Item ?? nameof(OverlayMetric.CpuTemp)) is var looks && looks.Count > 0 ? looks[(w + h) % looks.Count] : null;
                        }));
                        cfg.Theme = theme;
                        cfg.Grayscale = gray;
                        cfg.TileBoxes = gray;
                        cfg.Gap = gray ? WidgetGap.Normal : WidgetGap.None;
                        cfg.Corners = (WidgetCorners)((w + h) % 3);
                        using var bmp = WidgetRenderer.Render(cfg, data, 1.5f, hover: true, out _);
                        var (pw, ph) = WidgetTiles.BoxPixels(cfg.Tiles!);
                        Assert.True(Math.Abs(bmp.Width - pw * 1.5) <= 1 && Math.Abs(bmp.Height - ph * 1.5) <= 1, $"{kind} {w}x{h} {theme} {name}: {bmp.Width}x{bmp.Height}");
                    }
    }

    [Fact]
    public void Every_clock_style_draws_every_way()
    {
        foreach (var style in WidgetTiles.ClockStyles)
            foreach (bool h24 in new[] { false, true })
                foreach (bool seconds in new[] { false, true })
                    foreach (bool date in new[] { false, true })
                    {
                        var cfg = Box(Tile(WidgetTiles.Clock, set: t => (t.Style, t.Hours24, t.Seconds, t.ShowDate) = (style, h24, seconds, date)));
                        using var bmp = WidgetRenderer.Render(cfg, Full(), 1, hover: false, out _);
                        if (!h24 && !seconds && date) Keep(bmp, "clock-" + style.Replace(' ', '-'));
                    }
    }

    [Fact]
    public void A_box_of_many_tiles_draws_with_a_box_for_each_and_lines_between()
    {
        var tiles = new List<WidgetTile>();
        foreach (var k in WidgetTiles.Kinds) WidgetTiles.Place(tiles, WidgetTiles.New(k));
        foreach (var each in new[] { false, true })
        {
            var cfg = Box([.. tiles]);
            (cfg.TileBoxes, cfg.TileLines, cfg.Gap, cfg.Theme) = (each, !each, each ? WidgetGap.Small : WidgetGap.None, WidgetTheme.App);
            using var bmp = WidgetRenderer.Render(cfg, Full(), 1, hover: false, out _);
            Keep(bmp, each ? "box-each" : "box-lines");
        }
    }

    public static TheoryData<string, int, int> CentredTiles() => new()
    {
        { "Digital", 6, 2 }, { "Analog", 3, 4 }, { "Flip", 6, 2 }, { "In words", 6, 2 }, { "Day and time", 6, 2 },
        { "Digital", 4, 3 }, { "Digital", 4, 1 }, { "date", 3, 2 }, { "date", 3, 1 },
    };

    /// <summary>The ink inside the tile: how far it stays from each side, read off the picture.</summary>
    private static (int Left, int Right, int Top, int Bottom) InkGaps(Bitmap bmp, Rectangle tile, Color panel)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = tile.Top; y < tile.Bottom; y++)
            for (int x = tile.Left; x < tile.Right; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (Math.Abs(c.R - panel.R) + Math.Abs(c.G - panel.G) + Math.Abs(c.B - panel.B) <= 60) continue;
                (l, t, r, b) = (Math.Min(l, x), Math.Min(t, y), Math.Max(r, x), Math.Max(b, y));
            }
        Assert.True(r >= 0, "nothing drawn");
        return (l - tile.Left, tile.Right - 1 - r, t - tile.Top, tile.Bottom - 1 - b);
    }

    [Theory]
    [MemberData(nameof(CentredTiles))]
    public void A_clock_or_a_date_in_the_middle_is_in_the_middle_by_its_ink(string style, int w, int h)
    {
        bool date = style == "date";
        var cfg = Box(Tile(date ? WidgetTiles.Date : WidgetTiles.Clock, w, h, t => t.Style = date ? null : style));
        foreach (float scale in new[] { 1f, 1.5f })
        {
            using var bmp = WidgetRenderer.Render(cfg, Full(), scale, hover: false, out _);
            Keep(bmp, $"centre-{style.Replace(' ', '-')}-{w}x{h}-{scale}");
            // The tile's area on the picture, inside the panel's line and clear of its rounded corners' curve.
            int edge = (int)Math.Ceiling(WidgetTiles.Edge * scale) + 1;
            var tile = Rectangle.FromLTRB(edge, edge, bmp.Width - edge, bmp.Height - edge);
            var panel = bmp.GetPixel(bmp.Width / 2, edge);
            var (left, right, top, bottom) = InkGaps(bmp, tile, panel);
            // One pixel either way at 100%; two at 150%, where ClearType's coloured edge lies a pixel further out on one
            // side than the other (a third of a pixel of the widget's own size).
            int slack = scale > 1 ? 2 : 1;
            Assert.True(Math.Abs(left - right) <= slack, $"{style} {w}x{h} at {scale}x: {left} px left of the ink, {right} right");
            Assert.True(Math.Abs(top - bottom) <= slack, $"{style} {w}x{h} at {scale}x: {top} px above the ink, {bottom} below");
        }
    }

    // A row of readings is centred both ways, as a web page's flex row with its items centred (the user, 2026-11-10):
    // the slim bar keeps the same room on the left as on the right, now that the close cross is outside the widget, and a
    // line of readings sits in the middle of its tile however wide the tile is.
    [Theory]
    [InlineData("bar", 0)]
    [InlineData("line", 7)]
    [InlineData("line", 12)]
    public void A_row_of_readings_is_in_the_middle_by_its_ink(string kind, int cells)
    {
        var cfg = kind == "bar"
            ? new WidgetConfig { Style = WidgetStyle.Pill, Id = nameof(WidgetStyle.Pill), BackgroundOpacity = 1 }
            : Box(Tile(WidgetTiles.Line, cells, 1, t => t.Items = WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.Ram)));
        foreach (float scale in new[] { 1f, 1.5f })
        {
            using var bmp = WidgetRenderer.Render(cfg, Full(), scale, hover: false, out _);
            Keep(bmp, $"row-{kind}-{cells}-{scale}");
            // Inside the panel's line, in the rows clear of a pill's rounded ends.
            int edge = (int)Math.Ceiling(3 * scale) + 1;
            var area = Rectangle.FromLTRB(edge, edge, bmp.Width - edge, bmp.Height - edge);
            var panel = bmp.GetPixel(bmp.Width / 2, edge);
            var (left, right, top, bottom) = InkGaps(bmp, area, panel);
            int slack = scale > 1 ? 2 : 1;
            Assert.True(Math.Abs(left - right) <= slack, $"{kind} {cells} at {scale}x: {left} px left of the ink, {right} right");
            Assert.True(Math.Abs(top - bottom) <= slack, $"{kind} {cells} at {scale}x: {top} px above the ink, {bottom} below");
        }
    }

    [Fact]
    public void A_box_is_drawn_again_only_when_what_it_says_changes()
    {
        var cfg = Box(Tile(WidgetTiles.Clock), Tile(WidgetTiles.Reading, set: t => t.Look = WidgetTiles.Ring));
        cfg.Tiles![1].X = 6;
        string? a = WidgetRenderer.ChangeKey(cfg, Full());
        Assert.NotNull(a);
        Assert.Equal(a, WidgetRenderer.ChangeKey(cfg, Full()));
        WidgetRenderer.Now = () => new DateTime(2026, 11, 10, 9, 15, 2);
        Assert.NotEqual(a, WidgetRenderer.ChangeKey(cfg, Full()));
        // A graph is drawn every time.
        cfg.Tiles.Add(Tile(WidgetTiles.Graph, set: t => t.Y = 2));
        Assert.Null(WidgetRenderer.ChangeKey(cfg, Full()));
    }

    [Fact]
    public void The_time_in_words_is_to_the_nearest_five_minutes()
    {
        Assert.Equal("Quarter past nine", WidgetRenderer.TimeInWords(new DateTime(2026, 1, 1, 9, 14, 0)));
        Assert.Equal("Nine o'clock", WidgetRenderer.TimeInWords(new DateTime(2026, 1, 1, 21, 1, 0)));
        Assert.Equal("Ten o'clock", WidgetRenderer.TimeInWords(new DateTime(2026, 1, 1, 9, 58, 0)));
        Assert.Equal("Quarter to one", WidgetRenderer.TimeInWords(new DateTime(2026, 1, 1, 12, 44, 0)));
        Assert.Equal("Half past twelve", WidgetRenderer.TimeInWords(new DateTime(2026, 1, 1, 0, 30, 0)));
    }
}

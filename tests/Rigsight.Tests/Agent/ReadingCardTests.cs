using System.Drawing;
using System.Windows.Forms;
using Rigsight.Agent.Ui;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Issue #5: a click on a taskbar reading opens its card (a section per part, side by side, above the taskbar), the
/// click finds the reading under it, and on a short taskbar every reading sits on one line in a tile of the same height.
/// </summary>
public sealed class ReadingCardTests
{
    private static readonly Color Blue = Color.FromArgb(91, 140, 255), Cyan = Color.FromArgb(34, 211, 238);
    private static readonly Color Tile = Color.FromArgb(20, Color.White);
    private static readonly StripLook Tiles = new(true, TrayWidth.Standard, false);

    private static CardSection Section(string title, Color color)
    {
        var history = Enumerable.Range(0, 60).Select(i => ((long)i * 10_000, 40f + i % 7)).ToList();
        return new CardSection(title, "Test hardware", color,
            [new CardBig("Temperature", SensorKind.Temperature, 46, Color.White, history, "Last 10 minutes", "low 40° · average 43° · high 46°", "Highest today 52°")],
            [new CardLine("Load", "35%"), new CardLine("Clock", "4.2 GHz")],
            "Using the CPU most now", [new CardApp("game.exe", 30, "30%"), new CardApp("chrome.exe", 5, "5%")],
            null, [], "Open Temperatures", "temperatures");
    }

    private static bool Near(Color c, Color want) => c.A > 200 && Math.Abs(c.R - want.R) + Math.Abs(c.G - want.G) + Math.Abs(c.B - want.B) < 40;

    [Fact]
    public void A_card_shows_each_clicked_part_side_by_side_above_the_taskbar()
    {
        using var _ = Lang.UseHere(null);
        var area = Screen.PrimaryScreen!.WorkingArea;
        var anchor = new Rectangle(area.Right - 30, area.Bottom, 30, 40); // a reading at the right end of a taskbar at the bottom
        using var card = new ReadingCard(_ => { });

        card.Fill("cpu", [Section("CPU", Blue)], anchor, 1, Blue, Color.Black);
        int one = card.Width;
        card.Fill("cpu\ngpu", [Section("CPU", Blue), Section("GPU", Cyan)], anchor, 1, Blue, Color.Black);
        Assert.Equal("cpu\ngpu", card.Key);
        Assert.True(card.Width > one * 1.8, $"two parts {card.Width} px, one {one} px");
        Assert.True(card.Height > 300, $"the card is {card.Height} px tall");
        // Above the taskbar and inside the screen, even for a reading at its far end.
        Assert.True(card.Right <= area.Right && card.Left >= area.Left, $"card {card.Bounds}, screen {area}");
        Assert.True(card.Bottom <= area.Bottom, $"card {card.Bounds}, screen {area}");

        // Drawn: the CPU's colour on the left, the graphics card's on the right only.
        using var bmp = new Bitmap(card.Width, card.Height);
        card.DrawToBitmap(bmp, new Rectangle(Point.Empty, card.Size));
        int Count(Color want, int from, int to) =>
            Enumerable.Range(from, to - from).Sum(x => Enumerable.Range(0, bmp.Height).Count(y => Near(bmp.GetPixel(x, y), want)));
        int half = bmp.Width / 2;
        Assert.True(Count(Blue, 0, half) > 100, "the CPU's graph and bars");
        Assert.True(Count(Cyan, half, bmp.Width) > 100, "the graphics card's graph and bars");
        Assert.True(Count(Cyan, 0, half) < 5, "nothing of the graphics card's on the CPU's side");
    }

    [Fact]
    public void A_card_with_no_history_yet_still_draws()
    {
        var empty = Section("RAM", Blue) with { Big = [new CardBig("In use", SensorKind.Load, null, Color.White, [], "", "", null)], Apps = [] };
        using var card = new ReadingCard(_ => { });
        card.Fill("ram", [empty], new Rectangle(100, 2000, 30, 40), 1.5f, Blue, Color.Black);
        using var bmp = new Bitmap(card.Width, card.Height);
        card.DrawToBitmap(bmp, new Rectangle(Point.Empty, card.Size));
        Assert.True(card.Width > 300 * 1.5f);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(48)]
    public void A_click_on_the_strip_finds_the_reading_under_it(int height)
    {
        StripValue V(string text, string label) => new(text, Color.White, label, "88%", null, Blue, TrayLook.Number, 0.5f, Tile);
        using var bmp = WidgetRenderer.RenderStrip([new StripCell("CPU", Blue, [V("62°", "Temp"), V("35%", "Load")]), new StripCell("GPU", Blue, [V("71°", "Temp")])],
            height, 1, out var edges, out var valueEdges, WidgetRenderer.StripGraphWidth, Tiles);
        Assert.Equal(2, valueEdges.Length);
        Assert.Equal(2, valueEdges[0].Length);
        Assert.Single(valueEdges[1]);
        Assert.Equal(float.MaxValue, valueEdges[0][1]); // past the first reading is the second, up to the cell's edge
        float between = valueEdges[0][0];
        Assert.InRange(between, 10, edges[0] - 10);
        // The edge falls between the two readings' tiles: a number on each side, none across it.
        bool Digits(int from, int to) => Enumerable.Range(from, to - from).Any(x => Enumerable.Range(0, height).Any(y => bmp.GetPixel(x, y) is { A: > 200, R: > 220, G: > 220, B: > 220 }));
        Assert.True(Digits(0, (int)between) && Digits((int)between, (int)edges[0]));
        Assert.False(Digits((int)between - 1, (int)between + 2), "a number across the edge");
    }

    /// <summary>The rows with white digits in them.</summary>
    private static List<int> DigitRows(Bitmap bmp) =>
        [.. Enumerable.Range(0, bmp.Height).Where(y => Enumerable.Range(0, bmp.Width).Any(x => bmp.GetPixel(x, y) is { A: > 200, R: > 220, G: > 220, B: > 220 }))];

    [Theory]
    [InlineData(TrayLook.Number)]
    [InlineData(TrayLook.Level)]
    [InlineData(TrayLook.Ring)]
    public void On_one_line_a_reading_with_nothing_under_it_sits_in_the_middle(TrayLook look)
    {
        using var bmp = WidgetRenderer.RenderStrip([new StripCell("CPU", Blue, [new StripValue("62%", Color.White, "Load", "88%", null, Blue, look, 0.6f, Tile)])],
            32, 1, out _, WidgetRenderer.StripGraphWidth, Tiles);
        var rows = DigitRows(bmp);
        Assert.NotEmpty(rows);
        Assert.InRange((rows[0] + rows[^1]) / 2f, 14.5f, 17.5f);
    }

    [Fact]
    public void On_one_line_blocks_go_under_the_name_and_number()
    {
        Bitmap Draw(TrayLook look) => WidgetRenderer.RenderStrip([new StripCell("CPU", Blue, [new StripValue("62%", Color.White, "Load", "88%", null, Blue, look, 0.8f, Tile)])],
            32, 1, out _, WidgetRenderer.StripGraphWidth, Tiles);
        using var blocks = Draw(TrayLook.Blocks);
        using var number = Draw(TrayLook.Number);
        int bottom = DigitRows(blocks)[^1];
        int BlueBelow(Bitmap bmp, int from) => Enumerable.Range(from, bmp.Height - from).Sum(y => Enumerable.Range(0, bmp.Width).Count(x => bmp.GetPixel(x, y) is { A: > 60 } c && c.B > 150 && c.R < 160));
        Assert.True(BlueBelow(blocks, bottom + 1) > 20, "the blocks under the line");
        Assert.True(BlueBelow(number, DigitRows(number)[^1] + 1) < 5, "nothing under a plain number");
        Assert.True(bottom < 24, $"the line ends at {bottom} px, leaving the blocks room inside the tile");
    }

    [Theory]
    [InlineData(TrayLook.Number)]
    [InlineData(TrayLook.Ring)]
    public void On_two_lines_a_reading_with_nothing_under_it_sits_in_the_middle_of_its_tile(TrayLook look)
    {
        // The user's report on the test copy (2026-11-04): beside a reading with Blocks under it, "Load 8%" and "RAM 61%"
        // sat up at the top of their tiles, where the blocks' reading has its text.
        StripValue V(string text, string label, TrayLook l) => new(text, Color.White, label, "88%", null, Blue, l, 0.6f, Tile);
        using var bmp = WidgetRenderer.RenderStrip([new StripCell("CPU", Blue, [V("4%", "CPU", TrayLook.Blocks)]), new StripCell("RAM", Blue, [V("61%", "RAM", look)])],
            48, 1, out var edges, WidgetRenderer.StripGraphWidth, Tiles);
        // Ink (the white number, the blue word) in each part's columns, top to bottom.
        List<int> Ink(int from, int to, Func<Color, bool> what) =>
            [.. Enumerable.Range(0, bmp.Height).Where(y => Enumerable.Range(from, to - from).Any(x => what(bmp.GetPixel(x, y))))];
        static bool White(Color c) => c is { A: > 200, R: > 220, G: > 220, B: > 220 };
        static bool Word(Color c) => c is { A: > 150 } && c.B > 200 && c.R < 140 && c.G is > 110 and < 170;
        int start = (int)edges[0] + 2, end = bmp.Width - 2;
        // A ring's own blue sits beside the text, in the middle already: the text alone, from the number's right edge.
        var digits = Ink(start, end, White);
        int textLeft = look == TrayLook.Ring ? Enumerable.Range(start, end - start).First(x => Enumerable.Range(0, bmp.Height).Any(y => White(bmp.GetPixel(x, y)))) : start;
        var word = Ink(textLeft, end, Word);
        float middle = (digits[0] + word[^1]) / 2f;
        Assert.InRange(middle, 22.5f, 25.5f);
        Assert.True(Ink(0, (int)edges[0], White)[0] < digits[0] - 3, "the Blocks reading's text stays up top, its blocks under");
    }

    [Fact]
    public void On_a_short_taskbar_two_rows_leave_room_around_both_lines()
    {
        TwoRowColumn Box() => new([new TwoRowLine("CPU", Blue, "62°", Color.White, "88°"), new TwoRowLine("Load", Blue, "35%", Color.White, "88%")], Tile);
        using var rows = WidgetRenderer.RenderTwoRows([Box()], 32, 1, out _, Tiles);
        var ink = DigitRows(rows);
        // The tile runs from 4 to 28 px: both lines inside it with a pixel or more to spare, one in each half.
        Assert.True(ink[0] >= 6 && ink[^1] <= 26, $"digits from {ink[0]} to {ink[^1]} px");
        Assert.Contains(ink, y => y < 16);
        Assert.Contains(ink, y => y >= 16);
        Assert.Contains(Enumerable.Range(ink[0], ink[^1] - ink[0]), y => !ink.Contains(y)); // a gap between the lines
    }
}

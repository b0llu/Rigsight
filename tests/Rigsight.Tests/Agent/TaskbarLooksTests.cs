using System.Drawing;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The taskbar's tiles and looks (issue #4): each reading on a tile of its own, drawn as a number, a graph, a level,
/// blocks or a ring under its number; the strip as roomy as asked; readings that don't fit waiting in "+N"; and the icons
/// on a tile in their part's colour, every number the same height.
/// </summary>
public sealed class TaskbarLooksTests
{
    private static readonly Color Blue = Color.FromArgb(91, 140, 255);
    private static readonly List<PointF> Rising = [.. Enumerable.Range(0, 11).Select(i => new PointF(i / 10f, i / 10f))];

    private static StripValue Reading(TrayLook look, float fill = 0.5f, Color? tile = null) =>
        new("62%", Color.White, "CPU", "88%", look is TrayLook.Area or TrayLook.Line or TrayLook.Bars ? new StripGraph(Rising, Blue) : null, Blue, look, fill, tile);

    private static Bitmap Draw(StripValue v, bool tiles = false, TrayWidth width = TrayWidth.Standard) =>
        WidgetRenderer.RenderStrip([new StripCell("CPU", Blue, [v])], 48, 1, out _, WidgetRenderer.StripGraphWidthFor(1920, 1, width), new StripLook(tiles, width, Light: false));

    /// <summary>The part's blue drawn under the number (rows past <paramref name="from"/>), as solid ink.</summary>
    private static int BlueUnder(Bitmap bmp, int from = 20) =>
        Enumerable.Range(from, bmp.Height - from).Sum(y => Enumerable.Range(0, bmp.Width).Count(x => bmp.GetPixel(x, y) is { A: > 60 } c && c.B > 150 && c.R < 160));

    [Fact]
    public void A_tile_sits_behind_each_reading_only_when_asked()
    {
        var tile = Color.FromArgb(TrayColors.NeutralTileAlpha(false), Color.White);
        using var on = Draw(Reading(TrayLook.Number, tile: tile), tiles: true);
        using var off = Draw(Reading(TrayLook.Number, tile: tile), tiles: false);
        // Inside the tile, away from the text: the tile's faint white (the bar's own colour shows through it).
        var inside = on.GetPixel(on.Width - 6, on.Height - 7);
        Assert.InRange(inside.A, 10, 40);
        Assert.Equal(1, off.GetPixel(off.Width - 6, off.Height - 7).A); // nothing there but the click catcher
        // The tile keeps a few pixels clear of the bar's top and bottom.
        Assert.Equal(1, on.GetPixel(on.Width / 2, 1).A);
        Assert.True(on.Width > off.Width, "the tile has room inside it");
    }

    [Fact]
    public void Each_look_draws_under_its_number_and_never_over_it()
    {
        using var number = Draw(Reading(TrayLook.Number)); // only the word under it
        foreach (var look in new[] { TrayLook.Area, TrayLook.Line, TrayLook.Bars, TrayLook.Level, TrayLook.Blocks, TrayLook.Ring })
        {
            using var bmp = Draw(Reading(look, fill: 0.8f));
            Assert.True(BlueUnder(bmp) > BlueUnder(number), $"{look} draws something");
            // The white digits are still there, whole.
            Assert.NotEmpty(Enumerable.Range(0, 18).SelectMany(y => Enumerable.Range(0, bmp.Width).Where(x => bmp.GetPixel(x, y) is { A: > 200, R: > 220, G: > 220, B: > 220 })));
        }
    }

    [Fact]
    public void A_level_blocks_and_ring_fill_up_with_the_reading()
    {
        foreach (var look in new[] { TrayLook.Level, TrayLook.Blocks, TrayLook.Ring })
        {
            using var low = Draw(Reading(look, fill: 0.1f));
            using var high = Draw(Reading(look, fill: 0.9f));
            Assert.True(BlueUnder(high, from: 0) > BlueUnder(low, from: 0) * 2, $"{look}: fuller at 90% than at 10%");
        }
    }

    [Fact]
    public void A_ring_sits_beside_its_number_and_takes_room_for_it()
    {
        using var number = Draw(Reading(TrayLook.Number));
        using var ring = Draw(Reading(TrayLook.Ring, fill: 1));
        Assert.True(ring.Width >= number.Width + 26, "room for the ring");
        // The ring is to the left of the digits: blue on the left, digits on the right.
        int digitsLeft = Enumerable.Range(0, ring.Width).First(x => Enumerable.Range(0, 18).Any(y => ring.GetPixel(x, y) is { A: > 200, R: > 220, G: > 220, B: > 220 }));
        Assert.True(Enumerable.Range(0, digitsLeft).Any(x => ring.GetPixel(x, 24) is { A: > 150 } c && c.B > 150 && c.R < 160));
    }

    [Theory]
    [InlineData(TrayLook.Area)]
    [InlineData(TrayLook.Blocks)]
    public void Compact_is_narrower_than_standard_and_roomy_wider(TrayLook look)
    {
        int Width(TrayWidth w) => WidgetRenderer.MeasureStrip([new StripCell("CPU", Blue, [Reading(look)])], 48, 1,
            WidgetRenderer.StripGraphWidthFor(1920, 1, w), new StripLook(true, w, false));
        Assert.True(Width(TrayWidth.Compact) < Width(TrayWidth.Standard), "compact");
        Assert.True(Width(TrayWidth.Roomy) > Width(TrayWidth.Standard), "roomy");
        Assert.Equal(0, WidgetRenderer.StripGraphWidthFor(2560, 1, TrayWidth.Compact));
        Assert.Equal(WidgetRenderer.StripGraphWidthFor(2560, 1) * 1.5, WidgetRenderer.StripGraphWidthFor(2560, 1, TrayWidth.Roomy), 1);
    }

    [Fact]
    public void Without_tiles_at_standard_width_the_strip_is_as_before()
    {
        // The same reading, given a graph as before looks: the same width, drawn the same way, whatever its look says.
        var before = new StripValue("62%", Color.White, "CPU", "88%", new StripGraph(Rising, Blue), Blue);
        var now = before with { Look = TrayLook.Area };
        using var a = WidgetRenderer.RenderStrip([new StripCell("CPU", Blue, [before])], 48, 1, out _);
        using var b = WidgetRenderer.RenderStrip([new StripCell("CPU", Blue, [now])], 48, 1, out _, WidgetRenderer.StripGraphWidth, new StripLook(false, TrayWidth.Standard, false));
        Assert.Equal(a.Size, b.Size);
        Assert.Equal(BlueUnder(a, from: 0), BlueUnder(b, from: 0));
    }

    [Fact]
    public void Readings_that_wait_for_room_show_as_plus_n()
    {
        var more = new StripValue("+3", Color.White, "more", "+88", LabelColor: Color.FromArgb(160, Color.White), More: true);
        using var bmp = WidgetRenderer.RenderStrip([new StripCell("", Color.White, [more])], 48, 1, out var edges);
        Assert.Single(edges);
        Assert.InRange(bmp.Width, 15, 40); // small: it always fits
        Assert.NotEmpty(Enumerable.Range(0, 18).SelectMany(y => Enumerable.Range(0, bmp.Width).Where(x => bmp.GetPixel(x, y) is { A: > 200, R: > 220 })));
    }

    [Fact]
    public void An_icon_on_a_tile_has_no_line_and_the_tile_fills_it()
    {
        var tile = Color.FromArgb(TrayColors.TileAlpha(false, false), Blue);
        using var bmp = WidgetRenderer.RenderTrayIcon([("62", Color.White)], 32, mark: Color.Red, tile: tile);
        // The tile in its colour at the edge (where the line used to be): no red line.
        var edge = bmp.GetPixel(1, 16);
        Assert.True(edge.B > edge.R && edge.A is > 20 and < 120, $"{edge}");
        Assert.DoesNotContain(Enumerable.Range(0, 32).SelectMany(y => Enumerable.Range(0, 32).Select(x => bmp.GetPixel(x, y))), c => c.A > 200 && c.R > 200 && c.G < 60);
        // The corners are rounded off: nothing in the very corner.
        Assert.True(bmp.GetPixel(0, 0).A < 20);
    }

    [Fact]
    public void Icons_drawn_in_the_taskbar_are_half_as_big_again_as_windows_own()
    {
        // 30 at 100% and 45 at 150% on Windows 11's 48-pixel taskbar, 8 apart (1.3: 24 grew to 30 so the digits keep their
        // size inside the tile's wider edge); a short taskbar keeps them clear of its edges.
        Assert.Equal((30, 38), (Rigsight.Agent.Ui.TaskbarStrip.IconSize(48, 1), Rigsight.Agent.Ui.TaskbarStrip.IconSlot(48, 1)));
        Assert.Equal(45, Rigsight.Agent.Ui.TaskbarStrip.IconSize(72, 1.5f));
        Assert.Equal(20, Rigsight.Agent.Ui.TaskbarStrip.IconSize(32, 1));
        // On its tile the number keeps a tenth of the icon clear on each side; Windows' own 16-pixel icons keep a pixel.
        using var bmp = WidgetRenderer.RenderTrayIcon([("88", Color.White)], 24, tile: Color.FromArgb(60, Blue));
        var ink = Enumerable.Range(0, 24).SelectMany(y => Enumerable.Range(0, 24).Where(x => bmp.GetPixel(x, y) is { A: > 200, R: > 220, G: > 220 })).ToList();
        Assert.True(ink.Min() >= 2 && ink.Max() <= 21, $"digits from {ink.Min()} to {ink.Max()}");
        Assert.Equal(1, TrayParts.IconInset(16));
    }

    [Fact]
    public void Every_number_on_an_icon_stands_as_tall_as_two_digits()
    {
        int Height(string text)
        {
            using var bmp = WidgetRenderer.RenderTrayIcon([(text, Color.White)], 32);
            var rows = Enumerable.Range(0, 32).Where(y => Enumerable.Range(0, 32).Any(x => bmp.GetPixel(x, y).A > 128)).ToList();
            return rows.Max() - rows.Min();
        }
        // "184" is drawn narrower, not smaller: as tall as "72". Only far wider text has to shrink as well.
        Assert.InRange(Height("184"), Height("72") - 1, Height("72") + 1);
    }

    [Theory]
    [InlineData(SensorKind.Temperature, TrayLook.Ring, TrayLook.Ring)]
    [InlineData(SensorKind.Load, TrayLook.Blocks, TrayLook.Blocks)]
    [InlineData(SensorKind.Power, TrayLook.Level, TrayLook.Area)] // no top: a filled graph instead
    [InlineData(SensorKind.Throughput, TrayLook.Ring, TrayLook.Area)]
    [InlineData(SensorKind.Clock, TrayLook.Line, TrayLook.Line)]
    public void A_fill_up_look_needs_a_top(SensorKind kind, TrayLook picked, TrayLook drawn)
    {
        Assert.Equal(drawn, TrayParts.LookFor(kind, picked));
        Assert.Equal(TrayParts.HasTop(kind) ? 10 : 5, TrayParts.LooksFor(kind).Count); // 1.3: a big number, a half ring and a pie too
    }

    [Theory]
    [InlineData(TrayLook.Level)]
    [InlineData(TrayLook.Blocks)]
    [InlineData(TrayLook.Ring)]
    public void On_a_short_taskbar_each_look_draws_as_picked_not_as_a_graph(TrayLook look)
    {
        // Windows' smaller taskbar buttons: a 32-pixel bar, each reading on one line ("CPU 62%"). Before issue #5 these
        // looks became a filled graph there (and a reading kept no graph for them, so nothing showed but the number).
        Bitmap Short(TrayLook l, float fill) => WidgetRenderer.RenderStrip([new StripCell("CPU", Blue, [new StripValue("62%", Color.White, "Load", "88%", null, Blue, l, fill,
            Color.FromArgb(20, Color.White))])], 32, 1, out _, WidgetRenderer.StripGraphWidth, new StripLook(true, TrayWidth.Standard, false));
        using var low = Short(look, 0.1f);
        using var high = Short(look, 0.9f);
        Assert.True(BlueUnder(high, from: 0) > BlueUnder(low, from: 0) + 20, $"{look} fills up with the reading on one line");
        // The ring has its own room beside the number, as on two lines: the strip is wider by about the ring.
        if (look == TrayLook.Ring)
        {
            using var number = Short(TrayLook.Number, 0.9f);
            Assert.True(high.Width >= number.Width + 18, $"ring {high.Width} px, number only {number.Width} px");
        }
    }

    [Theory]
    [InlineData(32, TrayLook.Number)]
    [InlineData(32, TrayLook.Area)]
    [InlineData(32, TrayLook.Ring)]
    [InlineData(32, TrayLook.Blocks)]
    [InlineData(32, TrayLook.Level)]
    [InlineData(48, TrayLook.Number)]
    [InlineData(48, TrayLook.Area)]
    [InlineData(48, TrayLook.Ring)]
    [InlineData(48, TrayLook.Blocks)]
    [InlineData(48, TrayLook.Level)]
    public void A_wide_reading_is_never_cut_by_its_tile(int height, TrayLook look)
    {
        // The widest a speed gets ("188.8 MB/s") under a long word: its number stays clear of the tile's padding on both
        // sides, so nothing is clipped (the user's report on the mock-up, 2026-11-04: "readings getting cut").
        var white = Color.White;
        var strip = new[] { new StripCell("Network", Blue, [new StripValue("188.8 MB/s", white, "Down", "888.8 MB/s", look is TrayLook.Area ? new StripGraph(Rising, Blue) : null,
            Blue, look, 0.5f, Color.FromArgb(20, white))]) };
        using var bmp = WidgetRenderer.RenderStrip(strip, height, 1, out _, WidgetRenderer.StripGraphWidth, new StripLook(true, TrayWidth.Standard, false));
        AssertNoInkIn(bmp, 4, 9, "left padding");
        AssertNoInkIn(bmp, bmp.Width - 9, bmp.Width - 4, "right padding");

        TwoRowColumn Box() => new([new TwoRowLine("Down", Blue, "188.8 MB/s", white, "888.8 MB/s"), new TwoRowLine("Up", Blue, "88.8 MB/s", white, "888.8 MB/s")], Color.FromArgb(20, white));
        using var rows = WidgetRenderer.RenderTwoRows([Box()], height, 1, out _, new StripLook(true, TrayWidth.Standard, false));
        AssertNoInkIn(rows, rows.Width - 9, rows.Width - 4, "Two rows' right padding");
    }

    /// <summary>No white digit's pixel between <paramref name="from"/> and <paramref name="to"/> (columns), top to bottom.</summary>
    private static void AssertNoInkIn(Bitmap bmp, int from, int to, string where)
    {
        var hits = Enumerable.Range(Math.Max(0, from), Math.Min(bmp.Width, to) - Math.Max(0, from))
            .SelectMany(x => Enumerable.Range(0, bmp.Height).Where(y => bmp.GetPixel(x, y) is { A: > 150, R: > 200, G: > 200, B: > 200 })).Count();
        Assert.True(hits == 0, $"{hits} pixels of the number in the {where}");
    }

    [Fact]
    public void Two_rows_pair_readings_in_the_lists_order_and_one_on_its_own_gets_a_box()
    {
        string[] list = ["a", "b", "c", "d", "e"];
        string Boxes(params string[] alone) => string.Join(" ", TrayParts.Boxes(list, r => alone.Contains(r)).Select(b => string.Concat(b)));
        Assert.Equal("ab cd e", Boxes());
        Assert.Equal("ab c de", Boxes("c")); // the next one starts a new box
        Assert.Equal("a bc de", Boxes("a"));
        Assert.Equal("a b c d e", Boxes(list));
        Assert.Empty(TrayParts.Boxes(Array.Empty<string>(), _ => false));
    }

    [Theory]
    [InlineData("CPU", "Temp", null, false, false, "CPU")]                   // a box's top: its part
    [InlineData("CPU", "Load", "Cpu", false, false, "Load")]                 // under its part's first: what it is
    [InlineData("GPU", "Temp", "Cpu", false, false, "GPU")]                  // under another part's: its part
    [InlineData("GPU 2", "Temp", null, false, true, "G2")]                   // one letter, the number kept
    [InlineData("Network", "Down", null, false, false, "Down")]              // speeds say their direction
    [InlineData("Network", "Up", "Network:/nic/0", false, false, "Up")]
    [InlineData("CPU", "Pump", null, true, false, "Pump")]                    // a short name given on All sensors
    [InlineData("CPU", "Load", "Cpu", false, true, "L")]
    public void A_readings_word_in_its_box(string partName, string shortLabel, string? aboveGroup, bool custom, bool letter, string word)
    {
        var (group, kind) = partName switch
        {
            "Network" => ("Network:/nic/0", SensorKind.Throughput),
            "GPU 2" => ("Gpu:/gpu/1", SensorKind.Temperature),
            "GPU" => ("Gpu:/gpu/0", SensorKind.Temperature),
            _ => ("Cpu", shortLabel == "Load" ? SensorKind.Load : SensorKind.Temperature),
        };
        Assert.Equal(word, TrayParts.BoxLabel(partName, group, kind, shortLabel, custom, aboveGroup, letter));
    }

    [Fact]
    public void Two_rows_take_no_more_room_than_the_strip_and_far_less_than_its_graphs()
    {
        StripValue V(string text, string label, TrayLook look) =>
            new(text, Color.White, label, "88%", look == TrayLook.Area ? new StripGraph(Rising, Blue) : null, Blue, look);
        StripCell[] Strip(TrayLook look) => [new("CPU", Blue, [V("62°", "CPU", look), V("35%", "Load", look)]), new("GPU", Blue, [V("71°", "GPU", look), V("98%", "Load", look)])];
        TwoRowColumn Box(string a, string b) => new([new TwoRowLine("CPU", Blue, a, Color.White, "88°"), new TwoRowLine("Load", Blue, b, Color.White, "88%")], Color.FromArgb(20, Color.White));
        var look = new StripLook(true, TrayWidth.Standard, false);
        int numbers = WidgetRenderer.MeasureStrip(Strip(TrayLook.Number), 48, 1, 0, look);
        int graphs = WidgetRenderer.MeasureStrip(Strip(TrayLook.Area), 48, 1, WidgetRenderer.StripGraphWidthFor(1920, 1), look);
        using var rows = WidgetRenderer.RenderTwoRows([Box("62°", "35%"), Box("71°", "98%")], 48, 1, out var edges, look);
        Assert.True(rows.Width <= numbers, $"two rows {rows.Width} px, the strip's numbers {numbers} px");
        Assert.True(rows.Width < graphs * 0.7, $"two rows {rows.Width} px, the strip's graphs {graphs} px");
        Assert.Equal(2, edges.Length);
        // A tile per box, the two numbers one over the other: white ink in the top half and the bottom half.
        bool Ink(int y0, int y1) => Enumerable.Range(y0, y1 - y0).Any(y => Enumerable.Range(0, rows.Width).Any(x => rows.GetPixel(x, y) is { A: > 200, R: > 220, G: > 220, B: > 220 }));
        Assert.True(Ink(6, 24) && Ink(24, 42));
        // The first box's tile, see-through, somewhere in it (1.3 fits each tile to what it holds).
        Assert.Contains(Enumerable.Range(0, (int)edges[0]).SelectMany(x => Enumerable.Range(0, rows.Height).Select(y => (int)rows.GetPixel(x, y).A)), a => a is >= 10 and <= 40);
    }

    [Theory]
    [InlineData(62.0, 0.62f)]
    [InlineData(140.0, 1f)]
    [InlineData(-5.0, 0f)]
    public void How_full_a_reading_is_out_of_100(double value, float fill) => Assert.Equal(fill, TrayParts.Fill(value), 3);
}

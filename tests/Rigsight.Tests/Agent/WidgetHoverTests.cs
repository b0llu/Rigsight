using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Hover on the tiles with bars or a graph, a widget's own background colour, and the Fans, Drives and line tiles'
/// switches and names (approved mock-up XcuVN6VYHFqAbEb33kYRYx, 2026-11-12). Each hover is drawn to
/// %TEMP%\rigsight-tests\hover\*.png to set beside the mock-up.
/// </summary>
[Collection("Drawing")]
public class WidgetHoverTests : IDisposable
{
    private readonly Func<DateTime> _now = WidgetRenderer.Now;
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "hover");

    public WidgetHoverTests()
    {
        WidgetRenderer.Now = () => DateTime.Today.AddHours(9).AddMinutes(14);
        Directory.CreateDirectory(Dir);
    }

    public void Dispose() => WidgetRenderer.Now = _now;

    private static WidgetConfig Box(WidgetTile tile, WidgetTheme theme = WidgetTheme.Dark) =>
        new() { Style = WidgetStyle.Custom, Id = "custom-hover", Tiles = [tile], BackgroundOpacity = 0.94, Theme = theme };

    private static WidgetTile Tile(string kind) => WidgetTiles.New(WidgetTiles.Kind(kind)!);

    /// <summary>The mock-up's figures: the week with each day's on time and top app, the internet by hour, a drop at 2:31.</summary>
    private static WidgetData Data()
    {
        var d = Full();
        var f = d.Facts!;
        var day = DateTime.Today;
        f.DayLines = [.. f.Days.Select((x, i) => new DayLine(x.Day, x.ActiveSec, x.ActiveSec + 5400, i == 2 ? "Google Chrome" : "Dota 2", 6480))];
        f.HourTop = new long[24];
        f.HourRest = new long[24];
        (f.HourTop[8], f.HourRest[8], f.HourApp[8]) = ((long)(1.1 * (1L << 30)), 300L << 20, "Steam");
        (f.HourTop[9], f.HourRest[9], f.HourApp[9]) = (330L << 20, 80L << 20, "Steam");
        f.HourOff[2] = "PC was off";
        f.Drops = [(day.AddHours(2).AddMinutes(31), day.AddHours(2).AddMinutes(31).AddSeconds(50))];
        return d;
    }

    /// <summary>
    /// The widget drawn with the pointer at <paramref name="share"/> of the way across the tile's bars or graph (part
    /// <paramref name="part"/>), its hover box over it: what the window does. Saved as hover/<paramref name="name"/>.png.
    /// </summary>
    private static (WidgetRenderer.HoverOn? On, WidgetRenderer.HoverTip? Tip) Point(WidgetConfig cfg, WidgetData d, float share, string name, int part = 0)
    {
        const float scale = 2;
        using (WidgetRenderer.Render(cfg, d, scale, hover: false, out _)) { }
        var areas = WidgetRenderer.HoverAreas!.ToList();
        var area = areas.First(a => a.Part == part);
        // Low on the bars or graph, as a pointer on a bar is: the box above it where there's room.
        var at = new PointF(area.Rect.Left + area.Rect.Width * share, area.Rect.Top + area.Rect.Height * 0.8f);
        var on = WidgetRenderer.HoverAt(areas, at);
        WidgetRenderer.Hover = on;
        try
        {
            using var bmp = WidgetRenderer.Render(cfg, d, scale, hover: false, out _);
            var tip = WidgetRenderer.Tip;
            using var shot = new Bitmap(bmp.Width + 40, bmp.Height + 40);
            using (var g = Graphics.FromImage(shot))
            {
                g.Clear(Color.FromArgb(58, 58, 68));
                g.DrawImage(bmp, 20, 20);
                g.TranslateTransform(20, 20);
                if (tip is not null) WidgetRenderer.DrawTip(g, tip, scale, new SizeF(bmp.Width / scale, bmp.Height / scale));
            }
            shot.Save(Path.Combine(Dir, name + ".png"), ImageFormat.Png);
            return (on, tip);
        }
        finally
        {
            WidgetRenderer.Hover = null;
        }
    }

    private static string[] Lines(WidgetRenderer.HoverTip? tip) => tip is null ? [] : [tip.Title, .. tip.Lines.Select(l => l.Text)];

    // ── Where the pointer is ──────────────────────────────────────────────

    [Fact]
    public void The_pointer_snaps_to_the_nearest_bar_or_moment()
    {
        var bars = new WidgetRenderer.HoverArea(0, 0, new RectangleF(10, 10, 70, 20), 7, Points: false);
        Assert.Equal(0, WidgetRenderer.IndexAt(bars, new PointF(10, 15)));
        Assert.Equal(0, WidgetRenderer.IndexAt(bars, new PointF(19.9f, 15)));
        Assert.Equal(1, WidgetRenderer.IndexAt(bars, new PointF(20.1f, 15)));
        Assert.Equal(6, WidgetRenderer.IndexAt(bars, new PointF(80, 15)));
        var points = bars with { Count = 61, Points = true };
        Assert.Equal(0, WidgetRenderer.IndexAt(points, new PointF(10.5f, 15)));
        Assert.Equal(30, WidgetRenderer.IndexAt(points, new PointF(45, 15)));
        Assert.Equal(60, WidgetRenderer.IndexAt(points, new PointF(79.6f, 15)));
        // A strip's drops: only on one, nothing between them.
        var drops = bars with { Count = 2, Spans = [0.1f, 0.2f, 0.5f, 0.55f] };
        Assert.Equal(0, WidgetRenderer.IndexAt(drops, new PointF(10 + 70 * 0.15f, 15)));
        Assert.Null(WidgetRenderer.IndexAt(drops, new PointF(10 + 70 * 0.3f, 15)));
        Assert.Equal(1, WidgetRenderer.IndexAt(drops, new PointF(10 + 70 * 0.52f, 15)));
        // A few pixels' grace at the ends; nothing above or below the bars.
        Assert.Equal(0, WidgetRenderer.HoverAt([bars], new PointF(8, 15))?.Index);
        Assert.Null(WidgetRenderer.HoverAt([bars], new PointF(40, 35)));
        Assert.Null(WidgetRenderer.HoverAt([bars], new PointF(2, 15)));
    }

    [Fact]
    public void The_box_stays_inside_the_widget_above_the_pointer_or_under_it_near_the_top()
    {
        var widget = new SizeF(240, 140);
        var tip = new SizeF(120, 60);
        var above = WidgetRenderer.TipPlace(tip, widget, 120, 100);
        Assert.Equal(new PointF(60, 28), above);
        var below = WidgetRenderer.TipPlace(tip, widget, 120, 40);
        Assert.Equal(56, below.Y); // under the pointer, 16 below it
        var left = WidgetRenderer.TipPlace(tip, widget, 5, 100);
        var right = WidgetRenderer.TipPlace(tip, widget, 239, 100);
        Assert.Equal(6, left.X);
        Assert.Equal(widget.Width - tip.Width - 6, right.X);
        // A widget smaller than the box: still from its edge, never off it to the left or top.
        var tiny = WidgetRenderer.TipPlace(tip, new SizeF(80, 40), 40, 20);
        Assert.True(tiny.X >= 6 && tiny.Y >= 6);
    }

    // ── What each tile's box says ────────────────────────────────────────

    [Fact]
    public void The_last_7_days_says_each_days_figures_and_today_so_far()
    {
        var d = Data();
        var cfg = Box(Tile(WidgetTiles.Week));
        var (on, tip) = Point(cfg, d, 0.97f, "week-today");
        Assert.Equal(6, on?.Index);
        Assert.Equal(["Today, so far", $"Active {Units.Duration(d.Today.ActiveSec)}", $"PC on {Units.Duration(d.Today.OnSec)}",
            $"Mostly Cyberpunk 2077, {Units.Duration(d.Today.TopAppSec)}"], Lines(tip));
        var (first, tip0) = Point(cfg, d, 0.02f, "week-first-day");
        Assert.Equal(0, first?.Index);
        var day = DateTime.Today.AddDays(-6);
        Assert.Equal(day.ToString("dddd"), tip0!.Title.Split(',')[0], ignoreCase: true);
        Assert.Contains($"Active {Units.Duration(6.2 * 3600)}", Lines(tip0));
        Assert.Contains("Mostly Dota 2, 1h 48m", Lines(tip0));
        Point(Box(Tile(WidgetTiles.Week), WidgetTheme.Light), d, 0.97f, "week-today-light");
        // A day not recorded says so, and nothing else.
        d.Facts!.DayLines[1] = d.Facts.DayLines[1] with { ActiveSec = 0, OnSec = 0, TopApp = null };
        var (_, off) = Point(cfg, d, 1.5f / 7, "week-not-recorded");
        Assert.Equal(["Not recorded"], Lines(off).Skip(1));
    }

    [Fact]
    public void Internet_by_hour_says_the_total_the_biggest_app_and_the_rest()
    {
        var d = Data();
        var cfg = Box(Tile(WidgetTiles.NetHours));
        var (on, tip) = Point(cfg, d, 8.5f / 24, "nethours-8am");
        Assert.Equal(8, on?.Index);
        Assert.Equal([$"{Units.Data((long)(1.1 * (1L << 30)) + (300L << 20))} in all", $"Steam {Units.Data((long)(1.1 * (1L << 30)))}", "Other apps 300 MB"], Lines(tip).Skip(1));
        Assert.EndsWith(" to " + DateTime.Today.AddHours(9).ToString("h tt"), tip!.Title);
        Assert.Equal([null, NetDot(tip), Color.FromArgb(163, 163, 163)], tip.Lines.Select(l => l.Dot));
        Assert.Equal(["No internet use"], Lines(Point(cfg, d, 5.5f / 24, "nethours-empty").Tip).Skip(1));
        Assert.Equal(["PC was off"], Lines(Point(cfg, d, 2.5f / 24, "nethours-off").Tip).Skip(1));
        // An hour still to come has no box.
        Assert.Null(Point(cfg, d, 20.5f / 24, "nethours-later").Tip);
        Point(Box(Tile(WidgetTiles.NetHours), WidgetTheme.Light), d, 8.5f / 24, "nethours-8am-light");
    }

    private static Color? NetDot(WidgetRenderer.HoverTip tip) => tip.Lines[1].Dot;

    [Fact]
    public void Cores_say_the_cores_number_and_load()
    {
        var d = Data();
        var (on, tip) = Point(Box(Tile(WidgetTiles.Cores)), d, 2.5f / 16, "cores");
        Assert.Equal(2, on?.Index);
        Assert.Equal(["Core 3", "Load 41%"], Lines(tip));
    }

    [Fact]
    public void Internet_speed_says_how_long_ago_and_both_speeds()
    {
        var d = Data();
        var cfg = Box(Tile(WidgetTiles.NetSpeed));
        var (_, now) = Point(cfg, d, 1, "netspeed-now");
        Assert.Equal("Now", now!.Title);
        Assert.Equal([$"Down {Units.Speed(d.Pc!.DownHistory[^1])}", $"Up {Units.Speed(d.Pc.UpHistory[^1])}"], Lines(now).Skip(1));
        var (on, half) = Point(cfg, d, 0.5f, "netspeed-half");
        Assert.Equal($"{59 - on!.Value.Index} seconds ago", half!.Title);
        // With the times the agent read them at, from those.
        d.Pc.NetTimes = [.. Enumerable.Range(0, 60).Select(i => 1000L + i * 2)];
        Assert.Equal("1m 58s ago", Point(cfg, d, 0, "netspeed-times").Tip!.Title);
    }

    [Fact]
    public void A_reading_as_a_graph_and_the_temperature_graph_say_how_long_ago_and_the_readings()
    {
        var d = Data();
        d.HistorySeconds = 300;
        var reading = Tile(WidgetTiles.Reading);
        (reading.Item, reading.Look, reading.W, reading.H) = (nameof(OverlayMetric.CpuTemp), WidgetTiles.FilledGraph, 6, 3);
        var (_, tip) = Point(Box(reading), d, 0, "reading-graph-oldest");
        Assert.Equal("5m 00s ago", tip!.Title);
        Assert.StartsWith("CPU ", Assert.Single(tip.Lines).Text);
        Assert.Equal("Now", Point(Box(reading), d, 1, "reading-graph-now").Tip!.Title);
        reading.Look = WidgetTiles.BarGraph;
        Assert.StartsWith("CPU ", Point(Box(reading), d, 0.5f, "reading-bars").Tip!.Lines[0].Text);

        var graph = Tile(WidgetTiles.Graph);
        var (on, both) = Point(Box(graph), d, 0.5f, "temperature-graph");
        Assert.NotNull(on);
        Assert.Equal(["CPU", "GPU"], both!.Lines.Select(l => l.Text.Split(' ')[0]));
        Assert.All(both.Lines, l => Assert.NotNull(l.Dot));
        // A reading switched off on the graph isn't in its box.
        graph.Items![1].Hidden = true;
        Assert.Equal(["CPU"], Point(Box(graph), d, 0.5f, "temperature-graph-cpu").Tip!.Lines.Select(l => l.Text.Split(' ')[0]));

        // The built-in graph and Compact, drawn as before tiles, too.
        Assert.Equal(2, Point(new WidgetConfig { Style = WidgetStyle.Graph, Id = "Graph" }, d, 0.3f, "builtin-graph").Tip!.Lines.Count);
        Assert.StartsWith("CPU ", Point(new WidgetConfig { Style = WidgetStyle.Compact, Id = "Compact" }, d, 0.6f, "builtin-compact").Tip!.Lines[0].Text);
    }

    [Fact]
    public void Connection_today_says_when_a_drop_began_and_how_long_it_lasted_only_on_a_drop()
    {
        var d = Data();
        var cfg = Box(Tile(WidgetTiles.NetDrops));
        float at = (2 * 60 + 31.4f) / 1440;
        var (on, tip) = Point(cfg, d, at, "netdrops");
        Assert.Equal(0, on?.Index);
        Assert.Equal([$"Dropped at {DateTime.Today.AddHours(2).AddMinutes(31).ToString(TimeText.Fit("h:mm tt"))}", "Back after 50 seconds"], Lines(tip));
        Assert.Null(Point(cfg, d, 0.8f, "netdrops-elsewhere").On);
    }

    [Fact]
    public void Tiles_without_bars_or_a_graph_have_no_hover()
    {
        var d = Data();
        foreach (var kind in new[] { WidgetTiles.Fans, WidgetTiles.Drives, WidgetTiles.MostUsed, WidgetTiles.Clock, WidgetTiles.Peaks, WidgetTiles.TopCpu })
        {
            using (WidgetRenderer.Render(Box(Tile(kind)), d, 1, hover: false, out _)) { }
            Assert.Empty(WidgetRenderer.HoverAreas!);
        }
    }

    [Fact]
    public void The_hover_box_has_the_mock_ups_colours_on_dark_and_light()
    {
        var d = Data();
        var dark = Point(Box(Tile(WidgetTiles.Cores)), d, 0.2f, "tip-dark").Tip!;
        // The mock-up's #1d1f23 box, #3a3d44 edge, #ededed text on its dark widget, near enough.
        Assert.InRange(dark.Box.R, 25, 35);
        Assert.InRange(dark.Border.R, 50, 65);
        Assert.InRange(dark.Text.R, 230, 245);
        var light = Point(Box(Tile(WidgetTiles.Cores), WidgetTheme.Light), d, 0.2f, "tip-light").Tip!;
        Assert.True(light.Box.GetBrightness() > 0.9f && light.Text.GetBrightness() < 0.2f);
    }

    // ── A widget's own background colour ─────────────────────────────────

    [Theory]
    [InlineData("#0B0C0E", false)]
    [InlineData("#1B2A3A", false)]
    [InlineData("#16302B", false)]
    [InlineData("#3A1F24", false)]
    [InlineData("#E9EDF2", true)]
    [InlineData("#F6E7C8", true)]
    [InlineData("#CFE3F7", true)]
    [InlineData("#5B8CFF", true)]
    [InlineData("#808080", true)]
    [InlineData("#777777", true)]
    [InlineData("#FF0000", true)]
    [InlineData("#0000FF", false)]
    [InlineData("#FFFFFF", true)]
    [InlineData("#000000", false)]
    public void Text_turns_dark_on_a_light_colour_and_light_on_a_dark_one_and_stays_readable(string hex, bool light)
    {
        var bg = ColorTranslator.FromHtml(hex);
        var c = WidgetRenderer.ColorsOn(bg);
        Assert.Equal(light, c.Light);
        Assert.Equal(light ? Color.Black.ToArgb() : Color.White.ToArgb(), c.Text.ToArgb());
        double back = WidgetRenderer.Luminance(bg);
        Assert.True(WidgetRenderer.Contrast(WidgetRenderer.Luminance(c.Text), back) >= 4.5, $"text on {hex}");
        Assert.True(WidgetRenderer.Contrast(WidgetRenderer.Luminance(c.Muted), back) >= 4.5, $"muted on {hex}");
        Assert.True(WidgetRenderer.Contrast(WidgetRenderer.Luminance(c.Faint), back) >= 3, $"faint on {hex}");
    }

    [Fact]
    public void A_widget_on_its_own_colour_is_drawn_on_it_and_the_backdrop_still_applies()
    {
        var d = Data();
        foreach (var (hex, name) in new[] { ("#1B2A3A", "navy"), ("#F6E7C8", "cream") })
        {
            var cfg = Box(Tile(WidgetTiles.Fans));
            cfg.BackgroundColor = hex;
            cfg.BackgroundOpacity = 1;
            using var bmp = WidgetRenderer.Render(cfg, d, 2, hover: false, out _);
            bmp.Save(Path.Combine(Dir, $"background-{name}.png"), ImageFormat.Png);
            var px = bmp.GetPixel(bmp.Width - 8, bmp.Height / 2);
            Assert.Equal(ColorTranslator.FromHtml(hex).ToArgb(), px.ToArgb());
            cfg.BackgroundOpacity = 0.5;
            using var half = WidgetRenderer.Render(cfg, d, 2, hover: false, out _);
            Assert.InRange(half.GetPixel(half.Width - 8, half.Height / 2).A, 120, 135);
            // The text and lines follow: drawn differently from the theme's own.
            Assert.NotEqual(WidgetRenderer.ChangeKey(cfg, d), WidgetRenderer.ChangeKey(Box(Tile(WidgetTiles.Fans)), d));
        }
        // A colour that isn't one (an old or hand-edited file): the theme's.
        var odd = Box(Tile(WidgetTiles.Fans));
        odd.BackgroundColor = "red";
        Assert.Equal(WidgetRenderer.ChangeKey(Box(Tile(WidgetTiles.Fans)), d), WidgetRenderer.ChangeKey(odd, d));
    }

    // ── Fans, drives and readings: names and switches ────────────────────

    [Fact]
    public void A_fans_tile_leaves_out_the_fans_switched_off_and_shows_a_new_fan()
    {
        var d = Data();
        d.Pc!.Fans = [new("CPU fan", 820, "/lpc/fan/0"), new("Case fan 1", 640, "/lpc/fan/1"), new("GPU fans", 0, "/gpu/fan/0")];
        var tile = Tile(WidgetTiles.Fans);
        var all = WidgetRenderer.ChangeKey(Box(tile), d)!;
        Assert.Contains("Case fan 1", all);
        tile.FansHidden = ["/lpc/fan/1"];
        var some = WidgetRenderer.ChangeKey(Box(tile), d)!;
        Assert.DoesNotContain("Case fan 1", some);
        Assert.Contains("CPU fan", some);
        Assert.Contains("GPU fans", some);
        using (var bmp = WidgetRenderer.Render(Box(tile), d, 2, hover: false, out _)) bmp.Save(Path.Combine(Dir, "fans-one-off.png"), ImageFormat.Png);
        // A fan that turns up later shows: the tile keeps what it leaves out, not what it shows.
        d.Pc.Fans.Add(new("Pump", 2100, "/cooler/fan/0"));
        Assert.Contains("Pump", WidgetRenderer.ChangeKey(Box(tile), d)!);
        // Every one switched off: the heading alone, never "No spinning fans reported".
        tile.FansHidden = [.. d.Pc.Fans.Select(f => f.Id!)];
        using var none = WidgetRenderer.Render(Box(tile), d, 1, hover: false, out _);
        using var heading = WidgetRenderer.Render(Box(Tile(WidgetTiles.Fans)), new WidgetData { Pc = new PcNow { Fans = null } }, 1, hover: false, out _);
        Assert.Equal(Pixels(heading).Select(p => p.ToArgb()), Pixels(none).Select(p => p.ToArgb()));
    }

    [Fact]
    public void A_drives_tile_and_a_line_of_readings_leave_out_what_is_switched_off()
    {
        var d = Data();
        var drives = Tile(WidgetTiles.Drives);
        drives.DrivesHidden = ["e"];
        var key = WidgetRenderer.ChangeKey(Box(drives), d)!;
        Assert.Contains("C=", key);
        Assert.DoesNotContain("E=", key);

        var line = Tile(WidgetTiles.Line);
        line.Items = WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp);
        line.Items[1].Hidden = true;
        var lineKey = WidgetRenderer.ChangeKey(Box(line), d)!;
        Assert.Contains("CPU=", lineKey);
        Assert.DoesNotContain("GPU=", lineKey);
    }

    [Fact]
    public void A_reading_renamed_for_the_whole_app_has_that_name_on_every_tile_that_names_it()
    {
        var d = Data();
        d.Names[OverlayMetric.CpuTemp] = "Package";
        var reading = Tile(WidgetTiles.Reading);
        reading.Item = nameof(OverlayMetric.CpuTemp);
        Assert.Contains("Package=", WidgetRenderer.ChangeKey(Box(reading), d));
        Assert.Contains("Package", WidgetRenderer.ChangeKey(Box(Tile(WidgetTiles.CpuGauge)), d));
        var line = Tile(WidgetTiles.Line);
        line.Items = WidgetCatalog.Items(OverlayMetric.CpuTemp);
        Assert.Contains("Package=", WidgetRenderer.ChangeKey(Box(line), d));
        Assert.Equal("Package", Point(Box(Tile(WidgetTiles.Graph)), d, 0.5f, "graph-renamed").Tip!.Lines[0].Text.Split(' ')[0]);
        // The old slim bar too.
        Assert.Contains("Package=", WidgetRenderer.ChangeKey(new WidgetConfig { Style = WidgetStyle.Pill, Id = "Pill" }, d));
        // A tile's own short name from before still comes first.
        reading.Label = "Mine";
        Assert.Contains("Mine=", WidgetRenderer.ChangeKey(Box(reading), d));
    }

    // ── What a hover costs ────────────────────────────────────────────────

    // A hover draws the widget again once per bar or moment the pointer moves onto (and when the pointer moves up or down
    // by a band): measured here per drawing on a box of the hover tiles, written to hover\cost.txt.
    [Fact]
    public void A_hover_redraw_costs_a_few_milliseconds()
    {
        var d = Data();
        var hours = Tile(WidgetTiles.NetHours);
        hours.Y = 3;
        var cfg = new WidgetConfig { Style = WidgetStyle.Custom, Id = "custom-cost", BackgroundOpacity = 0.94, Tiles = [Tile(WidgetTiles.Week), hours] };
        WidgetTiles.Clean(cfg);
        for (int i = 0; i < 5; i++) using (WidgetRenderer.Render(cfg, d, 1.5f, hover: false, out _)) { }
        var areas = WidgetRenderer.HoverAreas!.ToList();
        var week = areas.First();
        const int n = 60;
        var sw = Stopwatch.StartNew();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        for (int i = 0; i < n; i++)
        {
            WidgetRenderer.Hover = WidgetRenderer.HoverAt(areas, new PointF(week.Rect.Left + week.Rect.Width * (i % 7 + 0.5f) / 7, week.Rect.Top + 5));
            using var bmp = WidgetRenderer.Render(cfg, d, 1.5f, hover: false, out _);
            if (WidgetRenderer.Tip is { } tip)
                using (var g = Graphics.FromImage(bmp)) WidgetRenderer.DrawTip(g, tip, 1.5f, new SizeF(bmp.Width / 1.5f, bmp.Height / 1.5f));
        }
        WidgetRenderer.Hover = null;
        double ms = sw.Elapsed.TotalMilliseconds / n, cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / n;
        File.WriteAllText(Path.Combine(Dir, "cost.txt"), $"hover redraw: {ms:0.0} ms wall, {cpuMs:0.0} ms CPU per drawing (week + internet by hour, 150%)\n");
        Assert.True(ms < 60, $"{ms:0.0} ms a drawing");
    }
}

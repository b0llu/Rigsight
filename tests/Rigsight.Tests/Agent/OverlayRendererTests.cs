using System.Drawing;
using System.Text.RegularExpressions;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>The in-game overlay: our own window's bitmap, and the same readout as RivaTuner text.</summary>
[Collection("Drawing")]
public partial class OverlayRendererTests
{
    private const float Pad = 8, RowHeight = 23;

    private static OverlaySettings Only(params OverlayMetric[] metrics) => new() { Metrics = [.. metrics] };

    // ── Our own window ──

    [Fact]
    public void The_users_sensors_show_even_with_no_readings_chosen()
    {
        using var bmp = WidgetRenderer.RenderOverlay(Only(), Full(), 1);
        Assert.Equal((int)Math.Ceiling(Pad * 2 + RowHeight * Full().Sensors.Count), bmp.Height);
    }

    [Fact]
    public void Nothing_chosen_draws_a_tiny_clear_bitmap()
    {
        var d = Full();
        d.Sensors = [];
        using var bmp = WidgetRenderer.RenderOverlay(Only(), d, 2);
        Assert.Equal(new Size(2, 2), bmp.Size); // one pixel, at the scale
        Assert.All(Pixels(bmp), p => Assert.Equal(0, p.A));
    }

    [Theory]
    [InlineData(OverlayLayout.Rows)]
    [InlineData(OverlayLayout.Line)]
    public void Every_layout_corner_size_and_opacity_draws_any_data(OverlayLayout layout)
    {
        foreach (var corner in Enum.GetValues<OverlayCorner>())
            foreach (bool gray in new[] { false, true })
                foreach (var (name, data) in AllData())
                    foreach (float scale in new[] { 0.6f, 1f, 1.5f, 2f, 2.5f })
                        foreach (var (bg, content) in new[] { (0.0, 0.2), (0.35, 1.0), (1.0, 1.0) })
                        {
                            var o = AllMetrics(layout);
                            (o.Corner, o.Grayscale, o.BackgroundOpacity, o.ContentOpacity) = (corner, gray, bg, content);
                            using var bmp = WidgetRenderer.RenderOverlay(o, data, scale);
                            string where = $"{layout}/{corner}/{name}/{scale}x/{bg}/{content}";
                            Assert.InRange(bmp.Height, (Pad * 2 + RowHeight) * scale, 1000 * scale);
                            Assert.InRange(bmp.Width, 40 * scale, 3000 * scale);
                            Assert.True(Coverage(bmp) > 0.5, where);
                        }
    }

    [Fact]
    public void Rows_are_one_per_part_and_the_line_layout_is_one_line()
    {
        var data = Full();
        data.Sensors = [];
        using var rows = WidgetRenderer.RenderOverlay(AllMetrics(), data, 1);
        using var line = WidgetRenderer.RenderOverlay(AllMetrics(OverlayLayout.Line), data, 1);
        // FPS, CPU, GPU, RAM, then the app and the time.
        Assert.Equal((int)Math.Ceiling(Pad * 2 + RowHeight * 5), rows.Height);
        Assert.Equal((int)Math.Ceiling(Pad * 2 + RowHeight), line.Height);
        Assert.True(line.Width > rows.Width);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(8, 3)]
    [InlineData(10, 4)]
    public void In_the_line_layout_sensors_go_four_to_a_line(int sensors, int lines)
    {
        var data = Full();
        data.Sensors = [.. Enumerable.Range(0, sensors).Select(i => new OverlaySensorReading($"S{i}", SensorKind.Temperature, 40 + i))];
        using var bmp = WidgetRenderer.RenderOverlay(AllMetrics(OverlayLayout.Line), data, 1);
        Assert.Equal((int)Math.Ceiling(Pad * 2 + RowHeight * lines), bmp.Height);
        using var rows = WidgetRenderer.RenderOverlay(AllMetrics(), data, 1);
        Assert.Equal((int)Math.Ceiling(Pad * 2 + RowHeight * (5 + sensors)), rows.Height);
    }

    [Fact]
    public void The_size_follows_the_scale()
    {
        using var one = WidgetRenderer.RenderOverlay(AllMetrics(), Full(), 1);
        using var two = WidgetRenderer.RenderOverlay(AllMetrics(), Full(), 2);
        Assert.InRange(two.Width, one.Width * 2 - 2, one.Width * 2 + 2);
        Assert.InRange(two.Height, one.Height * 2 - 2, one.Height * 2 + 2);
    }

    [Fact]
    public void Numbers_changing_dont_make_the_overlay_jump()
    {
        var o = Only(OverlayMetric.CpuTemp, OverlayMetric.CpuLoad, OverlayMetric.CpuClock, OverlayMetric.CpuPower,
            OverlayMetric.GpuTemp, OverlayMetric.GpuLoad, OverlayMetric.Fps, OverlayMetric.FrameTime, OverlayMetric.OnePercentLow);
        var widths = new HashSet<int>();
        foreach (var (t, l, fps) in new[] { (5.0, 0.0, 30.0), (62, 37, 144), (99, 100, 240), (100, 8, 999), (150, 100, 60) })
        {
            var d = new WidgetData { CpuTemp = t, GpuTemp = t, CpuLoad = l, GpuLoad = l, CpuClock = 4000 + l, CpuPower = l, Frame = new FrameStats(fps, 1000 / fps, fps * 0.8) };
            using var bmp = WidgetRenderer.RenderOverlay(o, d, 1);
            widths.Add(bmp.Width);
        }
        Assert.Single(widths);
    }

    [Fact]
    public void A_long_app_name_is_shortened()
    {
        var o = Only(OverlayMetric.Session);
        var d = Full();
        d.Activity.Name = new string('W', 24);
        using var fits = WidgetRenderer.RenderOverlay(o, d, 1);
        d.Activity.Name = new string('W', 200);
        using var cut = WidgetRenderer.RenderOverlay(o, d, 1);
        Assert.InRange(cut.Width, fits.Width - 30, fits.Width + 30);
    }

    [Fact]
    public void Frame_rate_shows_only_while_a_game_draws()
    {
        var o = Only(OverlayMetric.Fps, OverlayMetric.CpuTemp);
        var d = Full();
        using var game = WidgetRenderer.RenderOverlay(o, d, 1);
        d.Frame = null;
        using var desktop = WidgetRenderer.RenderOverlay(o, d, 1);
        Assert.Equal(game.Height - (int)RowHeight, desktop.Height);
    }

    [Fact]
    public void Grayscale_has_no_colour()
    {
        var o = AllMetrics();
        o.Grayscale = true;
        o.BackgroundOpacity = 0;
        var d = Full();
        d.CpuTemp = d.GpuTemp = 99; // red when coloured
        using var bmp = WidgetRenderer.RenderOverlay(o, d, 2);
        Assert.All(Pixels(bmp).Where(p => p.A > 200), p => Assert.True(Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)) < 40, p.ToString()));
    }

    [Fact]
    public void The_time_shows_when_chosen()
    {
        using var bmp = WidgetRenderer.RenderOverlay(Only(OverlayMetric.Clock), Empty(), 1);
        Assert.Equal((int)Math.Ceiling(Pad * 2 + RowHeight), bmp.Height);
    }

    // ── RivaTuner text ──

    private static string Rtss(OverlaySettings o, WidgetData? d) => WidgetRenderer.RtssText(o, d);

    [Fact]
    public void Nothing_chosen_is_no_text()
    {
        var d = Full();
        d.Sensors = [];
        Assert.Equal("", Rtss(Only(), d));
        Assert.Equal("", Rtss(Only(OverlayMetric.Fps, OverlayMetric.Ram, OverlayMetric.Session), Empty())); // nothing to show yet
    }

    [Theory]
    [InlineData(OverlayCorner.TopLeft, "<P0>", "<M=8,8,-13,-13>")]
    [InlineData(OverlayCorner.TopRight, "<P2>", "<M=-8,8,3,-13>")]
    [InlineData(OverlayCorner.BottomLeft, "<P6>", "<M=8,-8,-13,2>")]
    [InlineData(OverlayCorner.BottomRight, "<P8>", "<M=-8,-8,3,2>")]
    public void The_text_is_pinned_to_the_chosen_corner(OverlayCorner corner, string position, string margins)
    {
        var o = AllMetrics();
        o.Corner = corner;
        string text = Rtss(o, Full());
        Assert.StartsWith("<FNT=Segoe UI Semibold,-8,600,2>" + position + "<L0>" + margins, text);
    }

    [Theory]
    [InlineData(1.0, "-8", "<M=8,8,-13,-13>")]
    [InlineData(2.0, "-16", "<M=8,8,-18,-19>")]
    [InlineData(0.6, "-5", "<M=8,8,-11,-11>")]
    public void The_size_sets_the_font_and_the_padding(double scale, string font, string margins)
    {
        var o = AllMetrics();
        o.Scale = scale;
        string text = Rtss(o, Full());
        Assert.StartsWith($"<FNT=Segoe UI Semibold,{font},600,2><P0><L0>{margins}", text);
    }

    [Theory]
    [InlineData(1.0, "BE")]
    [InlineData(0.5, "5F")]
    [InlineData(0.0, "00")]
    public void The_panel_takes_the_background_opacity(double opacity, string alpha)
    {
        var o = AllMetrics();
        o.BackgroundOpacity = opacity;
        Assert.Contains($"<C={alpha}080A0E><B=0,0,R8>\b<C>", Rtss(o, Full()));
    }

    [Fact]
    public void Colours_carry_the_content_opacity_only_when_faded()
    {
        var o = AllMetrics();
        Assert.Contains("<C=5B8CFF>CPU<C>", Rtss(o, Full()));
        o.ContentOpacity = 0.5;
        Assert.Contains("<C=805B8CFF>CPU<C>", Rtss(o, Full()));
        Assert.DoesNotMatch(ColourWithoutAlpha(), Rtss(o, Full()).Split("\b<C>")[1]);
    }

    [GeneratedRegex("<C=[0-9A-F]{6}>")]
    private static partial Regex ColourWithoutAlpha();

    [Fact]
    public void The_readings_are_all_there()
    {
        string text = Rtss(AllMetrics(), Full());
        foreach (var part in new[] { ">FPS<", ">144<", ">6.9<", " 1% low<", ">CPU<", ">62°<", ">37<", "%<", ">4.45<", " GHz<", " W<",
            ">GPU<", ">71°<", " hot<", ">84°<", ">97<", ">320<", ">9.6<", " / 12 GB VRAM<", ">RAM<", ">18.6<", " / 32 GB<", ">Cyberpunk 2077<", ">1h 30m<" })
            Assert.Contains(part, text);
    }

    [Fact]
    public void Missing_readings_show_a_dash_and_optional_ones_are_left_out()
    {
        var d = new WidgetData { Activity = new ActivityInfo { Name = "Game" } };
        string text = Rtss(AllMetrics(), d);
        Assert.Contains(">—<", text);
        Assert.DoesNotContain("FPS", text);    // no game drawing frames
        Assert.DoesNotContain(" hot", text);   // no hot-spot sensor
        Assert.DoesNotContain("VRAM", text);
        Assert.DoesNotContain(">RAM<", text);
        Assert.Contains(">0m<", text);         // a session just started
    }

    [Fact]
    public void Grayscale_text_is_white_and_grey()
    {
        var o = AllMetrics();
        o.Grayscale = true;
        var colours = Regex.Matches(Rtss(o, Full()), "<C=([0-9A-F]{6})>").Select(m => m.Groups[1].Value).Distinct().Order();
        Assert.Equal(["AAB0BE", "FFFFFF"], colours); // the panel's colour carries its opacity, so it has 8 digits
    }

    [Fact]
    public void Tags_in_names_are_removed()
    {
        var d = Full();
        d.Activity.Name = "<C=FF0000>Evil<B=0,0>";
        d.Sensors = [new("<P8>Pump>", SensorKind.Fan, 1200)];
        string text = Rtss(AllMetrics(), d);
        Assert.Contains(">C=FF0000EvilB=0,0<", text);
        Assert.Contains(">P8Pump<", text);
        Assert.DoesNotContain("<P8>", text);
    }

    [Fact]
    public void A_long_app_name_is_cut_at_24_letters()
    {
        var d = Full();
        d.Activity.Name = new string('a', 30);
        Assert.Contains(">" + new string('a', 23) + "…<", Rtss(Only(OverlayMetric.Session), d));
        d.Activity.Name = new string('b', 24);
        Assert.Contains(">" + new string('b', 24) + "<", Rtss(Only(OverlayMetric.Session), d));
    }

    [Fact]
    public void Paused_hides_the_app()
    {
        var d = Full();
        d.Sensors = [];
        d.Activity.Paused = true;
        Assert.Equal("", Rtss(Only(OverlayMetric.Session), d));
    }

    [Theory]
    [InlineData(OverlayLayout.Rows, 0, 5)]
    [InlineData(OverlayLayout.Rows, 3, 8)]
    [InlineData(OverlayLayout.Line, 0, 1)]
    [InlineData(OverlayLayout.Line, 4, 2)]
    [InlineData(OverlayLayout.Line, 9, 4)]
    public void Lines_of_text_match_the_layout(OverlayLayout layout, int sensors, int lines)
    {
        var d = Full();
        d.Sensors = [.. Enumerable.Range(0, sensors).Select(i => new OverlaySensorReading($"S{i}", SensorKind.Load, i))];
        string text = Rtss(AllMetrics(layout), d);
        Assert.Equal(lines, text.Split('\n').Length - 1); // after the top spacer's line
    }

    [Fact]
    public void A_sensor_of_every_kind_has_its_unit()
    {
        var d = new WidgetData
        {
            Sensors = [.. Enum.GetValues<SensorKind>().Select(k => new OverlaySensorReading(k.ToString(), k, 1234.5678))],
        };
        string text = Rtss(Only(), d);
        foreach (var unit in new[] { "°<", "%<", " GHz<", " W<", " V<", " A<", " RPM<", " L/h<", " GB<", " MB<", " Hz<", " dBA<" })
            Assert.Contains(unit, text);
        Assert.Contains(">1235<", text);   // fan
        Assert.Contains(">1.23<", text);   // clock in GHz
        Assert.Contains(">1234.568<", text);  // voltage
        d.Sensors = [.. Enum.GetValues<SensorKind>().Select(k => new OverlaySensorReading(k.ToString(), k, null))];
        text = Rtss(Only(), d);
        Assert.Equal(Enum.GetValues<SensorKind>().Length, Regex.Matches(text, ">—<").Count);
    }

    [Fact]
    public void RivaTuner_text_fits_its_slot_for_any_data()
    {
        // RivaTuner's slot holds 4,095 characters.
        foreach (var layout in Enum.GetValues<OverlayLayout>())
            foreach (var (_, data) in AllData())
                Assert.True(Rtss(AllMetrics(layout), data).Length < 4096);
    }

    // ── Notices inside games ──

    [Fact]
    public void A_notice_goes_bottom_right_on_its_own_layer()
    {
        string text = WidgetRenderer.RtssNoticeText("TEMPERATURE ALERT", Color.FromArgb(248, 113, 113), "Running hot", "GPU is hot.", top: false, scale: 1);
        Assert.StartsWith("<FNT=Segoe UI Semibold,-8,600,2><P8><L6><M=-8,-8,3,2>", text);
        Assert.Contains("<C=F87171>TEMPERATURE ALERT<C>", text);
        Assert.Contains(">Running hot<", text);
        Assert.Contains(">GPU is hot.<", text);
        Assert.StartsWith("<FNT=Segoe UI Semibold,-12,600,2><P2><L6><M=-8,8,0,-16>",
            WidgetRenderer.RtssNoticeText("X", Color.Red, "T", "B", top: true, scale: 1.5));
    }

    [Fact]
    public void A_notice_body_is_wrapped_at_word_breaks()
    {
        string body = string.Join(' ', Enumerable.Repeat("word", 40)) + " " + new string('z', 60) + " end";
        string text = WidgetRenderer.RtssNoticeText("CAP", Color.White, "Title", body, false, 1);
        var lines = Regex.Matches(text, "<S=-80><C=[0-9A-F]{6}>([^<]*)<C><S>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(body, string.Join(' ', lines));
        Assert.All(lines.Where(l => !l.Contains('z')), l => Assert.InRange(l.Length, 1, 44));
        Assert.Contains(new string('z', 60), lines); // a word longer than a line gets a line of its own
    }

    [Fact]
    public void A_notice_drops_tags_and_empty_text()
    {
        string text = WidgetRenderer.RtssNoticeText("<CAP>", Color.White, "<Title>", "  a  <b>  ", false, 1);
        Assert.Contains(">CAP<", text);
        Assert.Contains(">Title<", text);
        Assert.Contains(">a b<", text);
        Assert.DoesNotContain("<CAP>", text);
        string empty = WidgetRenderer.RtssNoticeText("C", Color.White, "T", "", false, 1);
        Assert.DoesNotContain("<S=-80>", empty);
    }
}

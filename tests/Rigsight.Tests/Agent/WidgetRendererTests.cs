using System.Drawing;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>The desktop widgets, drawn to bitmaps (never shown): every style, theme, size and opacity, any data.</summary>
[Collection("Drawing")]
public class WidgetRendererTests
{
    private static readonly Dictionary<WidgetStyle, (int W, int H)> BaseSizes = new()
    {
        [WidgetStyle.Compact] = (276, 122), [WidgetStyle.Gauges] = (248, 150), [WidgetStyle.NowPlaying] = (310, 88),
        [WidgetStyle.Today] = (256, 140), [WidgetStyle.Graph] = (300, 138),
    };

    public static TheoryData<WidgetStyle, WidgetTheme> StylesAndThemes()
    {
        var data = new TheoryData<WidgetStyle, WidgetTheme>();
        foreach (var style in Enum.GetValues<WidgetStyle>())
            foreach (var theme in Enum.GetValues<WidgetTheme>())
                data.Add(style, theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(StylesAndThemes))]
    public void Every_style_and_theme_draws_any_data_at_any_size_and_opacity(WidgetStyle style, WidgetTheme theme)
    {
        foreach (var (name, data) in AllData())
            foreach (float scale in new[] { 0.6f, 1f, 1.5f, 2.25f })
                foreach (var (bg, content) in new[] { (0.0, 0.2), (0.3, 1.0), (0.4, 0.6), (1.0, 1.0) })
                {
                    var cfg = new WidgetConfig { Style = style, Theme = theme, BackgroundOpacity = bg, ContentOpacity = content, Enabled = true };
                    using var bmp = WidgetRenderer.Render(cfg, data, scale, hover: true, out var close);
                    string where = $"{style}/{theme}/{name} at {scale}x, {bg}/{content}";
                    if (BaseSizes.TryGetValue(style, out var size))
                    {
                        Assert.Equal((int)Math.Ceiling(size.W * scale), bmp.Width);
                        Assert.Equal((int)Math.Ceiling(size.H * scale), bmp.Height);
                    }
                    else
                    {
                        Assert.Equal((int)Math.Ceiling(34 * scale), bmp.Height);
                        Assert.InRange(bmp.Width, 120 * scale, 420 * scale);
                    }
                    Assert.True(new RectangleF(0, 0, bmp.Width, bmp.Height).Contains(close), $"close button outside {where}");
                    Assert.True(Coverage(bmp) > 0.5, $"mostly blank: {where}");
                }
    }

    [Theory]
    [InlineData(WidgetStyle.Compact)]
    [InlineData(WidgetStyle.Pill)]
    [InlineData(WidgetStyle.Graph)]
    public void Readings_in_Fahrenheit_draw_too(WidgetStyle style)
    {
        bool before = Units.Fahrenheit;
        try
        {
            Units.Fahrenheit = true;
            using var bmp = WidgetRenderer.Render(new WidgetConfig { Style = style }, Extreme(), 1, false, out _);
            Assert.True(Coverage(bmp) > 0.5);
        }
        finally
        {
            Units.Fahrenheit = before;
        }
    }

    [Fact]
    public void Without_a_background_the_widget_is_still_there_to_drag()
    {
        // Fully clear pixels let clicks through a layered window: the panel keeps at least 1/255.
        var cfg = new WidgetConfig { Style = WidgetStyle.Compact, BackgroundOpacity = 0, ContentOpacity = 1 };
        using var bmp = WidgetRenderer.Render(cfg, Empty(), 1, false, out _);
        var inside = bmp.GetPixel(bmp.Width / 2, bmp.Height - 6);
        Assert.InRange(inside.A, 1, 40);
        Assert.Equal(0, bmp.GetPixel(0, 0).A); // the rounded corner stays clear
    }

    [Theory]
    [InlineData(WidgetTheme.Dark, false)]
    [InlineData(WidgetTheme.Black, false)]
    [InlineData(WidgetTheme.Light, true)]
    public void The_panel_takes_the_theme(WidgetTheme theme, bool light)
    {
        var cfg = new WidgetConfig { Style = WidgetStyle.Today, Theme = theme, BackgroundOpacity = 1 };
        using var bmp = WidgetRenderer.Render(cfg, Empty(), 1, false, out _);
        var p = bmp.GetPixel(bmp.Width - 20, bmp.Height - 4);
        Assert.Equal(255, p.A);
        if (light) Assert.True(p.R > 240 && p.G > 240 && p.B > 240, p.ToString());
        else Assert.True(p.R < 20 && p.G < 20 && p.B < 20, p.ToString());
    }

    [Fact]
    public void The_background_opacity_fades_the_panel_only()
    {
        using var solid = WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.Today, BackgroundOpacity = 1 }, Full(), 1, false, out _);
        using var faint = WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.Today, BackgroundOpacity = 0.5 }, Full(), 1, false, out _);
        Assert.Equal(255, solid.GetPixel(solid.Width - 20, solid.Height - 4).A);
        Assert.InRange(faint.GetPixel(faint.Width - 20, faint.Height - 4).A, 120, 135);
    }

    [Fact]
    public void The_content_opacity_fades_the_readings()
    {
        static int Ink(double content)
        {
            var cfg = new WidgetConfig { Style = WidgetStyle.Compact, BackgroundOpacity = 0, ContentOpacity = content };
            using var bmp = WidgetRenderer.Render(cfg, Full(), 1, false, out _);
            return Pixels(bmp).Max(p => p.A);
        }
        Assert.True(Ink(1) > 240);
        Assert.InRange(Ink(0.2), 30, 70);
    }

    [Theory]
    [InlineData(WidgetStyle.Compact)]
    [InlineData(WidgetStyle.Pill)]
    [InlineData(WidgetStyle.NowPlaying)]
    public void The_close_button_shows_on_hover_unless_locked(WidgetStyle style)
    {
        var cfg = new WidgetConfig { Style = style };
        using var plain = WidgetRenderer.Render(cfg, Full(), 1.5f, hover: false, out var r1);
        using var hover = WidgetRenderer.Render(cfg, Full(), 1.5f, hover: true, out var r2);
        Assert.False(SamePixels(plain, hover));
        Assert.Equal(r1, r2);

        cfg.Locked = true;
        using var locked = WidgetRenderer.Render(cfg, Full(), 1.5f, hover: true, out _);
        Assert.True(SamePixels(plain, locked));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void The_close_button_is_scaled_with_the_widget(float scale)
    {
        WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.Compact }, null, scale, false, out var close).Dispose();
        Assert.Equal(new RectangleF((276 - 26) * scale, 6 * scale, 20 * scale, 20 * scale), close);
        WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.Pill }, null, scale, false, out var pill).Dispose();
        Assert.Equal(18 * scale, pill.Width);
        Assert.Equal((34 - 18) / 2f * scale, pill.Y);
    }

    [Fact]
    public void The_pill_is_as_wide_as_its_readings()
    {
        var cfg = new WidgetConfig { Style = WidgetStyle.Pill };
        using var empty = WidgetRenderer.Render(cfg, Empty(), 1, false, out _);
        using var hot = WidgetRenderer.Render(cfg, new WidgetData { CpuTemp = 100, GpuTemp = 100, RamLoad = 100 }, 1, false, out _);
        using var cool = WidgetRenderer.Render(cfg, new WidgetData { CpuTemp = 5, GpuTemp = 5, RamLoad = 5 }, 1, false, out _);
        Assert.True(hot.Width > cool.Width);
        Assert.True(cool.Width >= empty.Width - 10);
    }

    [Fact]
    public void Drawing_doesnt_change_the_data()
    {
        var data = Full();
        var cpu = data.CpuHistory.ToArray();
        WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.Graph }, data, 1, false, out _).Dispose();
        Assert.Equal(cpu, data.CpuHistory);
        Assert.Equal("Cyberpunk 2077", data.Activity.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1800)]
    public void Any_length_of_history_draws(int n)
    {
        var data = new WidgetData { CpuHistory = Wave(n), GpuHistory = [.. Enumerable.Repeat(float.NaN, n)] };
        foreach (var style in new[] { WidgetStyle.Compact, WidgetStyle.Graph })
            WidgetRenderer.Render(new WidgetConfig { Style = style }, data, 1, false, out _).Dispose();
    }

    [Fact]
    public void A_flat_history_draws()
    {
        var data = new WidgetData { CpuHistory = [.. Enumerable.Repeat(50f, 300)], GpuHistory = [.. Enumerable.Repeat(50f, 300)] };
        foreach (var style in new[] { WidgetStyle.Compact, WidgetStyle.Graph })
            WidgetRenderer.Render(new WidgetConfig { Style = style }, data, 1, false, out _).Dispose();
    }

    [Fact]
    [Trait("Category", "Machine")]
    public void The_app_in_front_shows_its_icon()
    {
        var data = Full();
        using var without = WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.NowPlaying }, data, 1, false, out _);
        data.Activity.Path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        using var with = WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.NowPlaying }, data, 1, false, out _);
        Assert.False(SamePixels(without, with));
        data.Activity.Path = @"C:\no\such\app.exe";
        WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.NowPlaying }, data, 1, false, out _).Dispose();
    }

    [Theory]
    [InlineData(true, false)]   // paused
    [InlineData(false, false)]  // nothing in front
    [InlineData(false, true)]   // away
    public void Now_playing_draws_every_state(bool paused, bool away)
    {
        var data = Full();
        data.Activity = paused ? new ActivityInfo { Paused = true, Name = "Game" }
            : away ? new ActivityInfo { Name = "Game", Present = false, SessionActiveSec = 0 }
            : new ActivityInfo();
        using var bmp = WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.NowPlaying }, data, 1, false, out _);
        Assert.Equal(310, bmp.Width);
    }
}

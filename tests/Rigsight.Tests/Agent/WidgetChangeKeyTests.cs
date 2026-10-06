using Rigsight.Agent.Widgets;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The widgets that are only their text (one line of readings, Now playing, Today) aren't drawn again while they'd
/// look the same; the ones that draw more (gauges, tiles with charts, graphs) always are.
/// </summary>
public class WidgetChangeKeyTests
{
    private static string? Key(WidgetStyle style, WidgetData data, Action<WidgetConfig>? set = null)
    {
        var cfg = new WidgetConfig { Style = style };
        set?.Invoke(cfg);
        return WidgetRenderer.ChangeKey(cfg, data);
    }

    [Theory]
    [InlineData(WidgetStyle.Pill)]
    [InlineData(WidgetStyle.NowPlaying)]
    [InlineData(WidgetStyle.Today)]
    public void The_same_readings_are_the_same_picture(WidgetStyle style)
    {
        var key = Key(style, Full());
        Assert.NotNull(key);
        Assert.Equal(key, Key(style, Full()));
        Assert.NotEqual(key, Key(style, Empty()));
        Assert.NotEqual(key, Key(style, Extreme()));
    }

    [Theory]
    [InlineData(WidgetStyle.Compact)]
    [InlineData(WidgetStyle.Gauges)]
    [InlineData(WidgetStyle.Graph)]
    public void A_widget_that_draws_more_than_its_text_is_always_drawn(WidgetStyle style) => Assert.Null(Key(style, Full()));

    [Fact]
    public void Today_changes_with_what_it_says_not_with_every_reading()
    {
        var a = Full();
        var b = Full();
        (b.CpuTemp, b.GpuLoad, b.CpuLoad) = (a.CpuTemp + 7, 3, 99); // live readings it doesn't show
        Assert.Equal(Key(WidgetStyle.Today, a), Key(WidgetStyle.Today, b));
        b.Today.ActiveSec += 20;   // within the minute shown
        b.Today.ActiveSec = Math.Floor(a.Today.ActiveSec / 60) * 60 + 5;
        a.Today.ActiveSec = Math.Floor(a.Today.ActiveSec / 60) * 60 + 50;
        Assert.Equal(Key(WidgetStyle.Today, a), Key(WidgetStyle.Today, b));
        b.Today.ActiveSec += 3600; // an hour on: another picture
        Assert.NotEqual(Key(WidgetStyle.Today, a), Key(WidgetStyle.Today, b));
    }

    [Fact]
    public void A_temperature_crossing_into_another_colour_is_another_picture_though_it_reads_the_same()
    {
        var (cool, warm) = (Full(), Full());
        (cool.Today.CpuPeak, warm.Today.CpuPeak) = (69.6, 70.4); // both "70°", green then amber
        Assert.NotEqual(Key(WidgetStyle.Today, cool), Key(WidgetStyle.Today, warm));
        // In grayscale temperatures aren't coloured: the same picture.
        Assert.Equal(Key(WidgetStyle.Today, cool, c => c.Grayscale = true), Key(WidgetStyle.Today, warm, c => c.Grayscale = true));
    }

    [Fact]
    public void The_line_of_readings_changes_when_a_reading_does()
    {
        var (a, b) = (Full(), Full());
        b.CpuTemp = a.CpuTemp + 0.2; // the same whole degree
        if (Math.Round(a.CpuTemp!.Value) == Math.Round(b.CpuTemp.Value)) Assert.Equal(Key(WidgetStyle.Pill, a), Key(WidgetStyle.Pill, b));
        b.CpuTemp = a.CpuTemp + 9;
        Assert.NotEqual(Key(WidgetStyle.Pill, a), Key(WidgetStyle.Pill, b));
    }
}

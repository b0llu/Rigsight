using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>Ctrl + wheel zooms the app window: 100, 110, 120 or 130% only (the user's word, 2026-10-20).</summary>
public sealed class AppZoomTests
{
    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.2, 1, 1.3)]
    [InlineData(1.3, 1, 1.3)] // the most
    [InlineData(1.0, -1, 1.0)] // no zooming out
    [InlineData(1.1000000001, 1, 1.2)] // whole steps, whatever a double held
    [InlineData(1.31, 0, 1.3)] // a 131% from before is a 130%
    [InlineData(1.5, -1, 1.2)] // a 150% from the first build: one step out of 130
    [InlineData(double.NaN, 1, 1.1)] // an unreadable setting: from 100%
    public void Each_step_is_a_tenth_from_100_to_130_percent(double zoom, int steps, double expected) =>
        Assert.Equal(expected, AppZoom.Stepped(zoom, steps), 6);

    [Theory]
    [InlineData(1.3, 2.0, 1.3)] // a big window: all of it
    [InlineData(1.3, 1.31, 1.3)]
    [InlineData(1.3, 1.29, 1.2)] // the whole step it has room for, never 129% or 131%
    [InlineData(1.3, 0.9, 1.0)] // the smallest window: never in
    [InlineData(1.2, double.NaN, 1.2)] // not laid out yet
    public void Zooming_in_stops_a_whole_step_before_the_pages_would_clip(double chosen, double room, double used) =>
        Assert.Equal(used, AppZoom.Used(chosen, room), 6);

    [Fact]
    public void Only_the_four_steps_are_ever_used()
    {
        for (double chosen = 0.5; chosen <= 2; chosen += 0.013)
            for (double room = 0.5; room <= 2; room += 0.017)
                Assert.Contains(Math.Round(AppZoom.Used(chosen, room) * 100), new double[] { 100, 110, 120, 130 });
    }

    [Fact]
    public void A_new_install_is_at_100_percent_and_the_zoom_is_kept()
    {
        Assert.Equal(1, new RigsightSettings().Zoom);
        var s = new RigsightSettings { Zoom = 1.2 };
        Assert.Equal(1.2, SettingsStore.Deserialize(SettingsStore.Serialize(s)).Zoom);
    }
}

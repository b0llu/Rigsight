using System.Drawing;
using Rigsight.Agent.Widgets;

namespace Rigsight.Tests.Agent;

/// <summary>
/// A widget goes back to its saved spot when enough of it shows on a screen (2026-11-06: a report of widgets back in the
/// corner after every update). The screens' whole areas count, the taskbar's strip and the edges included.
/// </summary>
public sealed class WidgetSpotTests
{
    private static readonly Rectangle[] OneScreen = [new(0, 0, 2560, 1440)];
    private static readonly Rectangle[] TwoScreens = [new(0, 0, 2560, 1440), new(2560, 0, 1920, 1080)];

    [Fact]
    public void A_widget_over_the_taskbar_or_partly_off_an_edge_keeps_its_spot()
    {
        Assert.True(WidgetManager.OnAScreen(new Rectangle(2200, 1400, 300, 40), OneScreen)); // over the taskbar at the bottom
        Assert.True(WidgetManager.OnAScreen(new Rectangle(2500, 20, 300, 60), OneScreen));   // half off the right edge
        Assert.True(WidgetManager.OnAScreen(new Rectangle(-200, -10, 260, 60), OneScreen));  // most of it off the top left
    }

    [Fact]
    public void A_widget_on_a_screen_that_isnt_there_waits_for_it()
    {
        var onSecond = new Rectangle(3000, 100, 300, 60);
        Assert.True(WidgetManager.OnAScreen(onSecond, TwoScreens));
        Assert.False(WidgetManager.OnAScreen(onSecond, OneScreen));      // the second screen not there yet
        Assert.False(WidgetManager.OnAScreen(new Rectangle(2550, 100, 300, 60), OneScreen)); // a sliver of 10 px is not enough
    }
}

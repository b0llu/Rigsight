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

    // Dragged near an edge, the middle or another widget, a widget is pulled onto it, 12 pixels in or apart; further
    // away it stays where it was let go.
    [Fact]
    public void A_dragged_widget_snaps_to_edges_the_middle_and_other_widgets()
    {
        var area = new Rectangle(0, 0, 2560, 1392); // the main screen above its taskbar
        Rectangle Panel(int x, int y) => new(x, y, 300, 100);
        Assert.Equal((12, 12), WidgetManager.SnapPanel(Panel(18, 5), area, [], 12, 10));                  // the top left corner
        Assert.Equal((2560 - 12 - 300, 1392 - 12 - 100), WidgetManager.SnapPanel(Panel(2240, 1285), area, [], 12, 10)); // bottom right
        Assert.Equal(((2560 - 300) / 2, 400), WidgetManager.SnapPanel(Panel(1126, 400), area, [], 12, 10)); // the middle
        Assert.Equal((700, 400), WidgetManager.SnapPanel(Panel(700, 400), area, [], 12, 10));              // nothing near: as let go

        var other = new Rectangle(1000, 200, 200, 80);
        Assert.Equal((1212, 200), WidgetManager.SnapPanel(Panel(1218, 205), area, [other], 12, 10));     // beside it, tops level
        Assert.Equal((1000, 292), WidgetManager.SnapPanel(Panel(995, 297), area, [other], 12, 10));      // under it, lefts level
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

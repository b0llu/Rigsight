using System.Drawing;
using Rigsight.Agent.Widgets;

namespace Rigsight.Tests.Agent;

/// <summary>
/// A widget on the desktop placed on the taskbar sits in front of it (the user, 2026-11-11: "they no longer come over
/// taskbar"): the part of the screen its work area leaves out, on whichever side the taskbar is.
/// </summary>
public class WidgetTaskbarTests
{
    private static readonly Rectangle Screen = new(0, 0, 2560, 1440);

    [Fact]
    public void A_widget_overlapping_the_taskbar_is_on_it()
    {
        var bottom = new Rectangle(0, 0, 2560, 1392); // a 48-pixel taskbar at the bottom
        Assert.True(WidgetForm.IsOnTaskbar(new Rectangle(1900, 1396, 300, 40), Screen, bottom));
        Assert.True(WidgetForm.IsOnTaskbar(new Rectangle(1900, 1380, 300, 40), Screen, bottom)); // partly
        Assert.False(WidgetForm.IsOnTaskbar(new Rectangle(1900, 1300, 300, 80), Screen, bottom));
        // On the left, and a taskbar that hides itself (the work area is the whole screen): never on it.
        Assert.True(WidgetForm.IsOnTaskbar(new Rectangle(10, 400, 40, 40), Screen, new Rectangle(60, 0, 2500, 1440)));
        Assert.False(WidgetForm.IsOnTaskbar(new Rectangle(10, 1400, 40, 40), Screen, Screen));
    }
}

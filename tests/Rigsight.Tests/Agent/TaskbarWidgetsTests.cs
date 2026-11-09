using System.Drawing;
using Rigsight.Agent.Ui;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The readings by the clock keep clear of Windows' Widgets (the weather) when Windows put it beside the tray: with the
/// taskbar's icons aligned left it moves there from the bar's left end (2026-11-08: a person's readings were drawn over
/// "Mostly cloudy"). Measured on a 1080-pixel-high screen 1920 wide, the tray starting at 1700.
/// </summary>
public sealed class TaskbarWidgetsTests
{
    private const int Tray = 1700;
    private static readonly Rectangle StartCentred = new(820, 1032, 44, 48), StartLeft = new(12, 1032, 44, 48);

    [Fact]
    public void Icons_aligned_left_put_Widgets_beside_the_tray_and_the_readings_keep_clear_of_it()
    {
        var weather = new Rectangle(1560, 1032, 132, 48); // "23°C Mostly cloudy", 8 px before the tray
        Assert.Equal(140, TaskbarButtons.WidgetsBeforeTray(StartLeft, weather, Tray));
        // Start not found: told by how near the tray it ends.
        Assert.Equal(140, TaskbarButtons.WidgetsBeforeTray(Rectangle.Empty, weather, Tray));
    }

    [Fact]
    public void Centred_icons_keep_Widgets_at_the_left_end_and_the_readings_next_to_the_tray()
    {
        var weather = new Rectangle(8, 1032, 132, 48);
        Assert.Equal(0, TaskbarButtons.WidgetsBeforeTray(StartCentred, weather, Tray));
        Assert.Equal(0, TaskbarButtons.WidgetsBeforeTray(Rectangle.Empty, weather, Tray));
    }

    [Fact]
    public void No_Widgets_or_no_tray_found_leaves_the_readings_where_they_were()
    {
        Assert.Equal(0, TaskbarButtons.WidgetsBeforeTray(StartLeft, Rectangle.Empty, Tray));
        Assert.Equal(0, TaskbarButtons.WidgetsBeforeTray(StartLeft, new Rectangle(1560, 1032, 132, 48), null));
        // Past the tray's left edge (a stale look while the tray grew): nothing to keep clear of.
        Assert.Equal(0, TaskbarButtons.WidgetsBeforeTray(StartLeft, new Rectangle(1710, 1032, 132, 48), Tray));
    }
}

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

    // A widget that grows past its screen's edge is moved back in (2026-11-10: widgets measured at 100% before landing on
    // a 150% screen, and boxes that grew tile by tile, ended partly off the screen); one left partly off an edge stays.
    [Fact]
    public void A_widget_that_grows_past_the_edge_moves_back_onto_its_screen()
    {
        var area = new Rectangle(0, 0, 2560, 1392);
        // Measured at 100% at the top right (128 wide), then drawn at 150% (192 wide): it ends at the edge, not past it.
        Assert.Equal(new Point(2368, 44), WidgetForm.StaysOnScreen(new Rectangle(2420, 44, 128, 88), new Rectangle(2420, 44, 192, 132), area));
        // Tiles added downward past the bottom.
        Assert.Equal(new Point(100, 1392 - 500), WidgetForm.StaysOnScreen(new Rectangle(100, 1000, 300, 200), new Rectangle(100, 1000, 300, 500), area));
        // Still fits: where it was.
        Assert.Equal(new Point(100, 100), WidgetForm.StaysOnScreen(new Rectangle(100, 100, 300, 200), new Rectangle(100, 100, 400, 260), area));
        // Left partly off the right edge by the person: where they left it.
        Assert.Equal(new Point(2500, 100), WidgetForm.StaysOnScreen(new Rectangle(2500, 100, 300, 60), new Rectangle(2500, 100, 400, 60), area));
        // Bigger than the screen: its top left on the screen.
        Assert.Equal(new Point(0, 0), WidgetForm.StaysOnScreen(new Rectangle(10, 10, 2000, 1000), new Rectangle(10, 10, 3000, 1500), area));
    }

    // The close badge sits over the widget's top right as a count sits on a bell (the user, 2026-11-10: it floated
    // outside a slim bar): its middle inside the panel's own shape, on the rounded corner's curve and a little in, and
    // the whole badge inside the window.
    [Theory]
    [InlineData("bar")]
    [InlineData("round")]
    [InlineData("slight")]
    [InlineData("square")]
    public void The_close_badge_sits_over_the_widgets_corner(string shape)
    {
        const float scale = 1.5f;
        const int dpi = 144, room = 17;
        var cfg = shape == "bar"
            ? new Rigsight.Core.Settings.WidgetConfig { Style = Rigsight.Core.Settings.WidgetStyle.Pill }
            : new Rigsight.Core.Settings.WidgetConfig
            {
                Style = Rigsight.Core.Settings.WidgetStyle.Custom, Tiles = [new() { Kind = "clock", W = 6, H = 4 }],
                Corners = shape switch { "slight" => Rigsight.Core.Settings.WidgetCorners.Slight, "square" => Rigsight.Core.Settings.WidgetCorners.Square, _ => Rigsight.Core.Settings.WidgetCorners.Round },
            };
        var panel = shape == "bar" ? new Size(300, 51) : new Size(372, 204);
        var badge = WidgetForm.BadgeAt(cfg, panel, scale, dpi, room);
        float cx = badge.X + badge.Width / 2, cy = badge.Y + badge.Height / 2;
        float radius = WidgetRenderer.CornerRadius(cfg, panel.Height / scale) * scale;
        // Inside the panel: left of its right edge and under its top, and inside the corner's circle.
        Assert.True(cx < panel.Width && cy > room, $"{shape}: middle at {cx},{cy}");
        if (radius > 0)
        {
            float ox = panel.Width - radius, oy = room + radius;
            Assert.True(MathF.Sqrt((cx - ox) * (cx - ox) + (cy - oy) * (cy - oy)) < radius, $"{shape}: middle off the curve's inside");
        }
        Assert.True(badge.Top >= 0 && badge.Right <= panel.Width + room, $"{shape}: badge {badge} outside the window");

        // A picture to look at: the widget as drawn, with the badge where the window puts it.
        using var drawn = WidgetRenderer.Render(cfg, null, scale, hover: false, out _);
        var at = WidgetForm.BadgeAt(cfg, drawn.Size, scale, dpi, room);
        using var pic = new Bitmap(drawn.Width + room, drawn.Height + room);
        using (var g = Graphics.FromImage(pic))
        {
            g.Clear(Color.FromArgb(42, 51, 80));
            g.DrawImageUnscaled(drawn, 0, room);
            WidgetForm.DrawBadge(g, at, dpi / 96f);
        }
        var folder = Path.Combine(Path.GetTempPath(), "rigsight-tests", "badges");
        Directory.CreateDirectory(folder);
        pic.Save(Path.Combine(folder, $"badge-{shape}.png"));
    }
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

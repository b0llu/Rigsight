using System.Windows;
using System.Windows.Input;
using Rigsight.Controls;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The overlay's spot on the Overlay page: snapping to the edges and centre, or placed freely to the pixel (a user
/// couldn't put it exactly where they wanted while it snapped).
/// </summary>
[Collection("UI")]
public sealed class OverlayPositionTests
{
    private static double ScreenW => SystemParameters.PrimaryScreenWidth;
    private static double ScreenH => SystemParameters.PrimaryScreenHeight;

    /// <summary>The picture, laid out at the page's size, with the overlay at the centre and no preview (a 220 × 110 box).</summary>
    private static OverlayScreen Picture(bool snap)
    {
        var screen = new OverlayScreen { Width = 440, Snap = snap, Spot = new OverlaySpot(4, 0, 0) };
        screen.Measure(new Size(440, double.PositiveInfinity));
        screen.Arrange(new Rect(screen.DesiredSize));
        return screen;
    }

    [Fact]
    public void Snapping_pulls_a_drag_that_ends_near_an_edge_onto_it()
    {
        Ui.Run(() =>
        {
            var screen = Picture(snap: true);
            var start = screen.Position;
            // To 20 px from the left edge: within reach of the 16 px anchor line.
            var at = screen.Dragged(start, new Vector(20 - start.X, 0));
            Assert.Equal(OverlayScreen.Gap, at.X, 2);
        });
    }

    [Fact]
    public void Placed_freely_a_drag_ends_exactly_where_it_was_let_go()
    {
        Ui.Run(() =>
        {
            var screen = Picture(snap: false);
            var start = screen.Position;
            var at = screen.Dragged(start, new Vector(20 - start.X, 37 - start.Y));
            Assert.Equal(20, at.X, 2);
            Assert.Equal(37, at.Y, 2);

            // Right by the centre line too: not pulled onto it.
            at = screen.Dragged(start, new Vector(3, 0));
            Assert.Equal(start.X + 3, at.X, 2);
        });
    }

    [Fact]
    public void Placed_freely_it_can_go_right_off_the_screen()
    {
        Ui.Run(() =>
        {
            var screen = Picture(snap: false);
            var start = screen.Position;
            var at = screen.Dragged(start, new Vector(-start.X - 900, ScreenH));
            Assert.Equal(-900, at.X, 2); // wholly off the left
            Assert.Equal(start.Y + ScreenH, at.Y, 2); // and below the bottom

            // Saved there, and read back there (no wall when it's drawn again either).
            screen.Spot = SpotAt(at);
            Assert.Equal(-900, screen.Position.X, 2);
        });
    }

    [Fact]
    public void Snapping_keeps_a_sliver_on_the_screen()
    {
        Ui.Run(() =>
        {
            var screen = Picture(snap: true);
            var start = screen.Position;
            var at = screen.Dragged(start, new Vector(-start.X - 900, 0));
            Assert.Equal(OverlayPlacement.MinVisible - 220, at.X, 2);
        });
    }

    /// <summary>A spot for a top-left (the 220 × 110 box without a preview).</summary>
    private static OverlaySpot SpotAt(Point at)
    {
        var (anchor, x, y) = OverlayPlacement.FromPosition(at.X, at.Y, ScreenW, ScreenH, 220, 110, OverlayScreen.Gap);
        return new OverlaySpot(anchor, x, y);
    }

    [Fact]
    public void Off_the_screen_the_page_says_so_and_reset_brings_it_back()
    {
        var (settings, live) = Kit.Greeted();
        var vm = Ui.Run(() => new OverlayViewModel(settings, new AgentClient(Ui.Dispatcher), live));
        Ui.Run(() =>
        {
            vm.SnapPosition = false;
            vm.Spot = SpotAt(new Point(-2000, 40));
            Assert.True(vm.IsOffScreen);

            // Snapping again pulls it back to a sliver on the screen.
            vm.SnapPosition = true;
            Assert.False(vm.IsOffScreen);
            vm.SnapPosition = false;
            Assert.True(vm.IsOffScreen);

            vm.ResetPositionCommand.Execute(null);
            Assert.False(vm.IsOffScreen);
            Assert.Equal(new OverlaySpot(0, 0, 0), vm.Spot);
            Assert.Equal((0, 0.0, 0.0), (settings.Current.Overlay.Anchor ?? -1, settings.Current.Overlay.OffsetX, settings.Current.Overlay.OffsetY));
        });
    }

    [Fact]
    public void Placed_freely_arrow_keys_move_it_a_pixel_or_ten()
    {
        Ui.Run(() =>
        {
            var screen = Picture(snap: false);
            var start = screen.Position;
            screen.Nudge(Key.Right, far: false);
            Assert.Equal(start.X + 1, screen.Position.X, 2);
            screen.Nudge(Key.Down, far: true);
            Assert.Equal(start.Y + 10, screen.Position.Y, 2);
            screen.Nudge(Key.Left, far: false);
            Assert.Equal(start.X, screen.Position.X, 2);
        });
    }

    [Fact]
    public void Snapping_arrow_keys_move_it_half_a_percent_of_the_screen()
    {
        Ui.Run(() =>
        {
            var screen = Picture(snap: true);
            var start = screen.Position;
            screen.Nudge(Key.Right, far: false);
            Assert.Equal(start.X + 0.005 * ScreenW, screen.Position.X, 2);
            screen.Nudge(Key.Up, far: true);
            Assert.Equal(start.Y - 0.05 * ScreenH, screen.Position.Y, 2);
        });
    }

    [Fact]
    public void Snapping_is_on_by_default_and_the_switch_is_saved()
    {
        Assert.True(new OverlaySettings().SnapPosition);
        Assert.True(SettingsStore.Deserialize("""{ "SettingsVersion": 8, "Overlay": { "Anchor": 2 } }""").Overlay.SnapPosition);

        var (settings, live) = Kit.Greeted();
        var vm = Ui.Run(() => new OverlayViewModel(settings, new AgentClient(Ui.Dispatcher), live));
        Ui.Run(() =>
        {
            Assert.True(vm.SnapPosition);
            vm.SnapPosition = false;
            Assert.False(settings.Current.Overlay.SnapPosition);
        });
    }
}

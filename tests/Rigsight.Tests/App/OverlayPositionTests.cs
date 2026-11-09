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
    public void In_one_column_the_Look_card_comes_right_under_the_shortcut_card()
    {
        // 2026-11-09, the user: on a smaller screen Look went to the very end, though it's one of the most important cards.
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var view = new Rigsight.Views.OverlayView { DataContext = new OverlayViewModel(settings, new AgentClient(Ui.Dispatcher), live) };
            var window = new Window { Content = view, Left = -32000, Top = -32000, Width = 1000, Height = 900, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Ui.Pump(300);
                var left = (System.Windows.Controls.StackPanel)view.FindName("LeftColumn");
                var right = (System.Windows.Controls.StackPanel)view.FindName("RightColumn");
                var look = (FrameworkElement)view.FindName("LookCard");
                Assert.Equal(1, left.Children.IndexOf(look)); // one column: second, under the shortcut card
                Assert.Equal(Visibility.Collapsed, right.Visibility);

                window.Width = 1900; // two columns: beside them again
                Ui.Pump(300);
                Assert.Same(right, look.Parent);
                Assert.Equal(Visibility.Visible, right.Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("ShortcutPreview")).Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void With_two_screens_the_games_screen_is_picked_and_the_picture_takes_its_shape()
    {
        // 2026-11-09, the user: "how do we show when the user has 2 screens?" A 16:9 main screen with an ultrawide beside it
        // where games were last played (stand-ins: this PC has one screen).
        var (settings, live) = Kit.Greeted();
        var vm = Ui.Run(() => new OverlayViewModel(settings, new AgentClient(Ui.Dispatcher), live));
        Ui.Run(() =>
        {
            vm.SetScreens([
                new ScreenChoice.Monitor(@"\\.\DISPLAY1", 0, 0, 2560, 1440, 1, IsMain: true, IsGames: false),
                new ScreenChoice.Monitor(@"\\.\DISPLAY2", 2560, 0, 3440, 1440, 1, IsMain: false, IsGames: true),
            ]);
            Assert.True(vm.HasScreens);
            Assert.Equal([1, 2], vm.Screens.Select(s => s.Number));
            Assert.True(vm.Screens[1].IsPicked && !vm.Screens[0].IsPicked);
            Assert.Equal(new Size(3440, 1440), vm.PickedScreenSize);
            Assert.Equal("2560 × 1440  ·  main", vm.Screens[0].Detail);
            // Side by side on the little map, the ultrawide wider.
            Assert.True(vm.Screens[1].X >= vm.Screens[0].X + vm.Screens[0].W - 0.5 && vm.Screens[1].W > vm.Screens[0].W);

            vm.Screens[0].IsPicked = true;
            Assert.Equal(new Size(2560, 1440), vm.PickedScreenSize);
            Assert.False(vm.Screens[1].IsPicked);

            var screen = new OverlayScreen { Width = 440, Snap = true, Spot = new OverlaySpot(0, 0, 0), ScreenSize = new Size(3440, 1440) };
            screen.Measure(new Size(440, double.PositiveInfinity));
            Assert.Equal(440 * 1440 / 3440.0, screen.DesiredSize.Height, 1); // the ultrawide's shape

            vm.SetScreens([new ScreenChoice.Monitor(@"\\.\DISPLAY1", 0, 0, 2560, 1440, 1, IsMain: true, IsGames: false)]);
            Assert.False(vm.HasScreens); // one screen: no picker, the main screen's shape
            Assert.True(vm.PickedScreenSize.IsEmpty);
        });
    }

    [Fact]
    public void Each_size_shows_its_own_size_on_the_picture_even_for_one_line()
    {
        // 2026-11-09, the user: "I don't see the size changing". A one-line overlay is a few pixels tall on the picture,
        // so it's drawn larger, and that growth was worked out at each size: S to 3XL all came out 22 px tall.
        Ui.Run(() =>
        {
            // One line at size M as the agent draws it (twice size): 400 × 78 pixels, so 200 × 39.
            var oneLine = System.Windows.Media.Imaging.BitmapSource.Create(400, 78, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, new byte[400 * 78 * 4], 400 * 4);
            double Height(double scale)
            {
                var screen = new OverlayScreen { Width = 440, Snap = true, Spot = new OverlaySpot(2, 0, 0), Preview = oneLine, OverlayScale = scale };
                screen.Measure(new Size(440, double.PositiveInfinity));
                screen.Arrange(new Rect(screen.DesiredSize));
                return screen.Box().Height;
            }
            double s = Height(0.8), m = Height(1), xxxl = Height(2.5);
            Assert.True(m >= 22 - 0.01, $"M drawn {m:0.0} px tall"); // still big enough to see and grab
            Assert.Equal(0.8, s / m, 2);
            Assert.Equal(2.5, xxxl / m, 2);
        });
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

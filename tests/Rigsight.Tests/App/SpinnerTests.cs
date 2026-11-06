using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rigsight.Controls;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>The turning ring: it turns only while it is on screen, and leaves nothing running when it goes.</summary>
[Collection("UI")]
public sealed class SpinnerTests
{
    /// <summary>Its turning is on WPF's timeline (with Windows' animations off it never is: there's nothing to check).</summary>
    private static bool Turning(Spinner spinner) => spinner.HasAnimatedProperties;

    [Fact]
    public void The_arc_goes_round_with_no_jump_and_never_vanishes()
    {
        // A go ends where the next begins (its head and tail meet), and four of them end where the first began.
        double last = 0;
        for (double phase = 0; phase < 4; phase += 0.001)
        {
            var (from, to) = Spinner.ArcAt(phase);
            Assert.InRange(to - from, 0.029, 0.76);
            if (phase > 0) Assert.InRange(from - last, -0.02, 0.02);
            last = from;
        }
        var (a, _) = Spinner.ArcAt(3.99999);
        Assert.Equal(7.0, a, 2);
        Assert.Equal((0.0, 0.75), Spinner.ArcAt(0, still: true));
    }

    private static AnimationClock ClockOf(Spinner spinner) =>
        (AnimationClock)typeof(Spinner).GetField("_clock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(spinner)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_loader_that_showed_for_a_moment_leaves_nothing_running(bool late)
    {
        // An arc that repeats for ever and was only replaced by nothing stayed on WPF's timeline until the next garbage
        // collection: the window never came to rest for seconds after any loader had shown.
        Spinner spinner = null!;
        Window window = null!;
        try
        {
            var hidden = Ui.Run(() =>
            {
                spinner = new Spinner { Late = late };
                window = new Window
                {
                    Left = -32000, Top = -32000, Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Content = new Border { Child = spinner },
                };
                window.Show();
                window.UpdateLayout();
                Assert.True(spinner.IsVisible);
                Assert.True(Turning(spinner));
                var clock = ClockOf(spinner);
                spinner.Visibility = Visibility.Collapsed;
                Assert.False(Turning(spinner));
                return clock;
            });
            Ui.Pump(150); // a clock taken off the timeline says so at the timeline's next step
            Assert.Equal(ClockState.Stopped, Ui.Run(() => hidden.CurrentState));

            // And again when it comes back and its page is taken off the window with it still showing.
            var removed = Ui.Run(() =>
            {
                spinner.Visibility = Visibility.Visible;
                window.UpdateLayout();
                Assert.True(Turning(spinner));
                var clock = ClockOf(spinner);
                window.Content = null;
                Assert.False(Turning(spinner));
                return clock;
            });
            Ui.Pump(150);
            Assert.Equal(ClockState.Stopped, Ui.Run(() => removed.CurrentState));
        }
        finally
        {
            Ui.Run(() => window?.Close());
        }
    }
}

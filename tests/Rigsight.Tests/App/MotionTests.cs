using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// The animations really move, and where they can be seen: each is caught partway (a value between its two ends), which
/// an animation that jumps, or runs before anything is drawn, never shows. (The switch knob once jumped at the end of
/// its transition while its track faded fine; a first-visit page's fade once ran out while the page was being built.)
/// </summary>
[Collection("UI")]
public sealed class MotionTests
{
    /// <summary>Lets the window run (rendering, animations) for about <paramref name="ms"/>.</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static Window Host(UIElement content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        window.Show();
        Pump(150);
        return window;
    }

    /// <summary>Every 20 ms for about 400 ms: what <paramref name="read"/> says.</summary>
    private static List<double> Watch(Func<double> read)
    {
        var values = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            Pump(20);
            values.Add(read());
        }
        return values;
    }

    private static void Moves(List<double> values, double from, double to)
    {
        double lo = Math.Min(from, to), hi = Math.Max(from, to), margin = (hi - lo) * 0.05;
        Assert.Contains(values, v => v > lo + margin && v < hi - margin); // seen on its way, not only at an end
        Assert.Equal(to, values[^1], 2);
    }

    [Fact]
    public void A_switch_slides_its_knob_across_as_its_track_fills()
    {
        Ui.Run(() =>
        {
            var box = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), Content = "Something" };
            var window = Host(box);
            var knob = (FrameworkElement)box.Template.FindName("Knob", box);
            var track = (UIElement)box.Template.FindName("TrackOn", box);
            Assert.Equal((0.0, 0.0), (knob.RenderTransform.Value.OffsetX, track.Opacity));

            box.IsChecked = true;
            var knobPath = new List<double>();
            var trackPath = Watch(() => { knobPath.Add(knob.RenderTransform.Value.OffsetX); return track.Opacity; });
            Moves(knobPath, 0, 20);
            Moves(trackPath, 0, 1);

            box.IsChecked = false;
            Moves(Watch(() => knob.RenderTransform.Value.OffsetX), 20, 0);
            window.Close();
        });
    }

    [Fact]
    public void The_sidebars_accent_bar_grows_when_its_page_is_picked()
    {
        Ui.Run(() =>
        {
            var item = new RadioButton { Style = (Style)Application.Current.FindResource("NavItem"), Content = "Home", Tag = "" };
            var window = Host(item);
            var bar = (FrameworkElement)item.Template.FindName("Bar", item);
            Assert.Equal(0.0, bar.RenderTransform.Value.M22);
            item.IsChecked = true;
            Moves(Watch(() => bar.RenderTransform.Value.M22), 0, 1);
            window.Close();
        });
    }

    [Fact]
    public void A_page_seen_for_the_first_time_fades_in_once_it_is_drawn()
    {
        if (!Motion.Enabled) return; // Windows' animation effects are off on this PC: nothing animates, by design
        Ui.Run(() =>
        {
            var host = new ContentControl();
            var window = Host(host);
            // A page with plenty in it, as a real one: building and drawing it takes a while.
            var column = new StackPanel();
            for (int i = 0; i < 400; i++) column.Children.Add(new TextBlock { Text = $"Row {i}", Margin = new Thickness(4) });
            var page = new UserControl { Content = new ScrollViewer { Content = column } };

            host.Content = page;
            Motion.PageIn(host, page);
            Assert.Equal(0.0, host.Opacity); // hidden while it's built, rather than showing half-drawn
            Moves(Watch(() => host.Opacity), 0, 1);
            window.Close();
        });
    }
}

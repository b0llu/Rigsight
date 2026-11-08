using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.Controls;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// The wheel glides (SmoothWheel): a notch moves as far as WPF's own wheel, over a tenth of a second, a spin adding on;
/// a touchpad, Windows' animations off and a list that scrolls by the item keep WPF's wheel; a list inside a page still
/// hands the wheel on at its end; anything inside that takes the wheel itself keeps it.
/// </summary>
public sealed class SmoothWheelTests
{
    [Fact]
    public void A_notch_goes_as_far_as_WPFs_own_wheel_and_eases_into_place()
    {
        Assert.Equal(48, SmoothWheel.Step(-120, 3, 600));
        Assert.Equal(-48, SmoothWheel.Step(120, 3, 600));
        Assert.Equal(96, SmoothWheel.Step(-240, 3, 600));
        Assert.Equal(600, SmoothWheel.Step(-120, -1, 600)); // Windows set to a screen at a time
        Assert.Equal(0, SmoothWheel.Ease(0));
        Assert.Equal(1, SmoothWheel.Ease(1));
        Assert.Equal(1, SmoothWheel.Ease(2));
        Assert.True(SmoothWheel.Ease(0.25) > 0.25 && SmoothWheel.Ease(0.5) > SmoothWheel.Ease(0.25)); // fast first
    }

    private static (Window Window, ScrollViewer Page, StackPanel Panel) Window(params FrameworkElement[] inside)
    {
        var panel = new StackPanel();
        panel.Children.Add(new Border { Height = 150 });
        foreach (var e in inside) panel.Children.Add(e);
        panel.Children.Add(new Border { Height = 900 });
        var page = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SmoothWheel.SetEnabled(page, true);
        var window = new Window { Content = page, Left = -32000, Top = -32000, Width = 300, Height = 300, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        Ui.Pump(100);
        return (window, page, panel);
    }

    /// <summary>A turn of the wheel as Windows sends it: down from the window (Preview), then out from what's under the pointer.</summary>
    private static void Turn(UIElement over, int delta)
    {
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        over.RaiseEvent(wheel);
        if (wheel.Handled) return;
        wheel.RoutedEvent = UIElement.MouseWheelEvent;
        over.RaiseEvent(wheel);
    }

    private static ListBox List(ScrollUnit unit)
    {
        var list = new ListBox { Height = 100, ItemsSource = Enumerable.Range(0, 40).Select(i => $"Row {i}").ToList() };
        VirtualizingPanel.SetScrollUnit(list, unit);
        WheelChain.SetEnabled(list, true);
        return list;
    }

    [Fact]
    public void The_page_glides_a_notch_and_a_spin_adds_on()
    {
        Ui.Run(() =>
        {
            var (window, page, panel) = Window();
            try
            {
                var over = (UIElement)panel.Children[1];
                double step = SmoothWheel.Step(-120, SystemParameters.WheelScrollLines, page.ViewportHeight);
                Turn(over, -120);
                if (!Rigsight.Services.Motion.Enabled)
                {
                    // Windows' animation effects off: WPF's own wheel, as before.
                    Assert.False(SmoothWheel.IsGliding(page));
                    return;
                }
                Assert.True(SmoothWheel.IsGliding(page));
                Assert.True(page.VerticalOffset < step); // not there at once
                Ui.Pump(250);
                Assert.False(SmoothWheel.IsGliding(page));
                Assert.Equal(step, page.VerticalOffset, 1);

                // Two notches quickly: the second adds on to where the first was going.
                Turn(over, -120);
                Turn(over, -120);
                Ui.Pump(250);
                Assert.Equal(3 * step, page.VerticalOffset, 1);

                // A touchpad's small turns are WPF's own.
                Turn(over, -30);
                Assert.False(SmoothWheel.IsGliding(page));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_list_glides_and_at_its_end_hands_the_wheel_to_the_page_which_glides()
    {
        Ui.Run(() =>
        {
            long clock = 1_000_000;
            var realClock = WheelChain.Clock;
            WheelChain.Clock = () => clock;
            var list = List(ScrollUnit.Pixel);
            var (window, page, _) = Window(list);
            try
            {
                if (!Rigsight.Services.Motion.Enabled) return;
                var inner = Visuals.Descendants<ScrollViewer>(list).First();
                SmoothWheel.SetEnabled(inner, true);
                var row = Visuals.Descendants<ListBoxItem>(list).First();

                // The list under the pointer is the one that moves.
                Turn(row, -120);
                Assert.True(SmoothWheel.IsGliding(inner));
                Assert.False(SmoothWheel.IsGliding(page));
                Ui.Pump(250);
                Assert.Equal(SmoothWheel.Step(-120, SystemParameters.WheelScrollLines, inner.ViewportHeight), inner.VerticalOffset, 1);
                Assert.Equal(0, page.VerticalOffset);

                // At its end, a moment later: the page.
                inner.ScrollToBottom();
                Ui.Pump(30);
                clock += 1000;
                Turn(Visuals.Descendants<ListBoxItem>(list).Last(), -120);
                Assert.True(SmoothWheel.IsGliding(page));
                Assert.True(page.VerticalOffset < 1); // gliding, not jumped
                Ui.Pump(250);
                Assert.Equal(SmoothWheel.Step(-120, SystemParameters.WheelScrollLines, page.ViewportHeight), page.VerticalOffset, 1);
            }
            finally
            {
                WheelChain.Clock = realClock;
                window.Close();
            }
        });
    }

    /// <summary>
    /// A long list that builds only the rows in view (Processes, Memory, Apps, Fans) guesses its height from the rows it
    /// has built and corrects it as it goes: the glide still goes the whole way, notch after notch.
    /// </summary>
    [Fact]
    public void A_long_list_of_rows_of_different_heights_glides_the_whole_way()
    {
        Ui.Run(() =>
        {
            var list = new ListBox
            {
                Height = 300,
                ItemsSource = Enumerable.Range(0, 500).Select(i => new Border { Height = 20 + i * 7 % 41, Child = new TextBlock { Text = $"Row {i}" } }).ToList(),
            };
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
            var window = new Window { Content = list, Left = -32000, Top = -32000, Width = 300, Height = 400, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Ui.Pump(100);
                if (!Rigsight.Services.Motion.Enabled) return;
                var box = Visuals.Descendants<ScrollViewer>(list).First();
                SmoothWheel.SetEnabled(box, true);
                double step = SmoothWheel.Step(-120, SystemParameters.WheelScrollLines, box.ViewportHeight);
                for (int i = 1; i <= 20; i++)
                {
                    Turn(Visuals.Descendants<ListBoxItem>(list).First(), -120);
                    Ui.Pump(i % 4 == 0 ? 250 : 30); // a spin, then a rest
                }
                Ui.Pump(250);
                Assert.Equal(20 * step, box.VerticalOffset, 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_list_that_scrolls_by_the_item_keeps_WPFs_wheel()
    {
        Ui.Run(() =>
        {
            var byItem = List(ScrollUnit.Item);
            var byPixel = List(ScrollUnit.Pixel);
            var (window, page, _) = Window(byItem, byPixel);
            try
            {
                var itemBox = Visuals.Descendants<ScrollViewer>(byItem).First();
                var pixelBox = Visuals.Descendants<ScrollViewer>(byPixel).First();
                Assert.True(SmoothWheel.ByItem(itemBox));
                Assert.False(SmoothWheel.ByItem(pixelBox));
                Assert.False(SmoothWheel.ByItem(page)); // a page's box scrolls by the pixel itself

                SmoothWheel.SetEnabled(itemBox, true);
                Turn(Visuals.Descendants<ListBoxItem>(byItem).First(), -120);
                Assert.False(SmoothWheel.IsGliding(itemBox));
                Assert.False(SmoothWheel.IsGliding(page));
                Ui.Pump(30);
                Assert.True(itemBox.VerticalOffset > 0); // WPF's wheel moved it, by rows
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Something_inside_that_takes_the_wheel_itself_keeps_it()
    {
        Ui.Run(() =>
        {
            var own = new Border { Height = 100, Background = System.Windows.Media.Brushes.Transparent };
            own.MouseWheel += (_, e) => e.Handled = true; // as a sideways row or a zoom would
            var (window, page, _) = Window(own);
            try
            {
                Turn(own, -120);
                Ui.Pump(250);
                Assert.False(SmoothWheel.IsGliding(page));
                Assert.Equal(0, page.VerticalOffset);
            }
            finally
            {
                window.Close();
            }
        });
    }
}

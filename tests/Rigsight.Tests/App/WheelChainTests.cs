using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.Controls;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// A scrolling list inside a scrolling page (Memory's list of running apps): the wheel moves the list while it can go
/// further that way, and the page once it can't.
/// </summary>
public sealed class WheelChainTests
{
    [Fact]
    public void A_list_at_its_end_hands_the_wheel_to_the_page()
    {
        Ui.Run(() =>
        {
            long clock = 1_000_000;
            var realClock = WheelChain.Clock;
            WheelChain.Clock = () => clock;
            var list = new ListBox { Height = 100, ItemsSource = Enumerable.Range(0, 40).Select(i => $"Row {i}").ToList() };
            WheelChain.SetEnabled(list, true);
            var panel = new StackPanel();
            panel.Children.Add(new Border { Height = 150 });
            panel.Children.Add(list);
            panel.Children.Add(new Border { Height = 600 });
            var page = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var window = new Window { Content = page, Left = -32000, Top = -32000, Width = 300, Height = 300, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Ui.Pump(100);
                var inner = Visuals.Descendants<ScrollViewer>(list).First();
                Assert.True(inner.ScrollableHeight > 0 && page.ScrollableHeight > 0);

                // What the list makes of a turn of the wheel over it: true when it was handed on.
                bool Turn(int delta)
                {
                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                    inner.RaiseEvent(wheel);
                    Ui.Pump(30);
                    return wheel.Handled;
                }

                // In the middle of the list the wheel is the list's own, either way.
                inner.ScrollToVerticalOffset(inner.ScrollableHeight / 2);
                Ui.Pump(30);
                Assert.False(Turn(-120));
                Assert.False(Turn(120));
                Assert.Equal(0, page.VerticalOffset);

                // At its end, further down moves the page, but not with the turns that ran the list out: the page
                // holds still until the list has been at rest for a moment. Back up is the list's again.
                inner.ScrollToBottom();
                Ui.Pump(30);
                clock += 100;
                Assert.True(Turn(-120));
                clock += 200;
                Assert.True(Turn(-120));
                Assert.Equal(0, page.VerticalOffset);
                clock += 100;
                Assert.True(Turn(-120));
                Assert.True(page.VerticalOffset > 0);
                Assert.False(Turn(120));

                // At its start, further up moves the page back.
                inner.ScrollToTop();
                Ui.Pump(30);
                clock += 1000;
                double before = page.VerticalOffset;
                Assert.True(Turn(120));
                Assert.True(page.VerticalOffset < before);
                Assert.False(Turn(-120));
            }
            finally
            {
                WheelChain.Clock = realClock;
                window.Close();
            }
        });
    }
}

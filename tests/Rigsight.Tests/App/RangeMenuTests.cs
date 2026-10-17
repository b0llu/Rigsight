using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>The Temperatures graph's range menu (on the period's button): every range up to now, then Custom range….</summary>
[Collection("UI")]
public sealed class RangeMenuTests
{
    private static IEnumerable<T> Below<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var deeper in Below<T>(child)) yield return deeper;
        }
    }

    [Fact]
    public void The_menu_lists_every_range_and_ticks_the_one_shown()
    {
        var (_, live) = Kit.Greeted();
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            live.ChartWindowSeconds = 604800;
            var view = new Views.TemperaturesView { DataContext = live };
            var window = new Window { Content = view, Left = -32000, Top = -32000, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Ui.Pump(150);
                // Drawn without opening it (an open pop-up in a window test wakes accessibility tools; see MemoryLeakTests).
                var menu = (Popup)view.FindName("RangeMenu");
                var child = (FrameworkElement)menu.Child;
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                child.Arrange(new Rect(child.DesiredSize));
                child.UpdateLayout();
                var lines = ((StackPanel)((Border)child).Child).Children.OfType<Button>().ToList();
                Assert.Equal(["Last 5 minutes", "Last hour", "Last 6 hours", "Last 24 hours", "Today", "Last 7 days", "Last 30 days", "Last 12 months", "Custom range…"],
                    lines.Select(b => (string)b.Content));
                string Ticked() => string.Join(",", lines.Where(b => b.Template.FindName("Tick", b) is TextBlock { Visibility: Visibility.Visible }).Select(b => (string)b.Content));
                Assert.Equal("Last 7 days", Ticked());

                // A line picks its range, back to now.
                live.ChartPreviousDayCommand.Execute(null);
                lines[1].Command.Execute(lines[1].CommandParameter);
                Assert.Equal(3600, live.ChartWindowSeconds);
                Assert.Equal(DateTime.Today, live.ChartDay);
                child.UpdateLayout();
                Assert.Equal("Last hour", Ticked());
                // The period's button says the range, and its name says what a click does.
                var pager = (Controls.DatePager)view.FindName("ChartPager");
                Assert.Equal("Last hour", pager.Label);
                Assert.Equal("Pick a range", pager.LabelText);
                Assert.NotNull(pager.LabelCommand);
            }
            finally { window.Close(); }
        });
        Ui.AssertNoProblems("temperatures range menu");
    }
}

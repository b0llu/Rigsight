using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rigsight.Controls;
using Rigsight.Services;

namespace Rigsight;

/// <summary>
/// The sidebar's icons-only fold: its edge handle, or Ctrl+B. It changes width at once, with no animation (the user,
/// 2026-10-25: the page widened before the sidebar finished closing). Also: the fades at the ends of a sidebar that scrolls,
/// and the open page kept in view.
/// </summary>
public partial class MainWindow
{
    private void SetUpSidebar()
    {
        // Ctrl+B folds and opens it, as in most apps with a sidebar (not while typing).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.B || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBoxBase) return;
            _vm.Sidebar.ToggleIconsCommand.Execute(null);
            e.Handled = true;
        };
    }

    // ── A sidebar that scrolls ──

    /// <summary>A soft fade at the end that has more (top, bottom or both); none when everything fits.</summary>
    private void NavScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (NavScroll.ScrollableHeight < 0.5 || NavScroll.ActualHeight < 80) { NavScroll.OpacityMask = null; return; }
        bool above = NavScroll.VerticalOffset > 1, below = NavScroll.VerticalOffset < NavScroll.ScrollableHeight - 1;
        double f = 36 / NavScroll.ActualHeight;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(above ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, f));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
        mask.GradientStops.Add(new GradientStop(below ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        NavScroll.OpacityMask = mask;
    }

    /// <summary>The open page's entry (or its group's icon, with icons only) is scrolled into view.</summary>
    private void KeepOpenPageInView() => Dispatcher.BeginInvoke(() =>
    {
        if (Find(NavScroll) is { } item) item.BringIntoView();
    }, DispatcherPriority.Loaded);

    private static FrameworkElement? Find(DependencyObject from)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++)
        {
            var child = VisualTreeHelper.GetChild(from, i);
            if (child is RadioButton { IsChecked: true, IsVisible: true } picked) return picked;
            if (child is ToggleButton { DataContext: ViewModels.SidebarGroup { HasSelected: true }, IsVisible: true } group) return group;
            if (Find(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ── A group's list beside its icon (icons only) ──

    /// <summary>A page picked in a group's list: the list closes.</summary>
    private static void CloseRailFlyout(object sender)
    {
        for (DependencyObject? at = sender as DependencyObject; at is not null; at = LogicalTreeHelper.GetParent(at) ?? (at is Visual ? VisualTreeHelper.GetParent(at) : null))
            if (at is Popup popup) { popup.IsOpen = false; return; }
    }

    private void RailNewDashboard_Click(object sender, RoutedEventArgs e)
    {
        CloseRailFlyout(sender);
        _vm.Presets.OpenCommand.Execute(null);
    }
}

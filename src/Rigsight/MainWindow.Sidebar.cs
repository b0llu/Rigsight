using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
        // A theme change repaints the fades in the new sidebar colour (after the theme is in place).
        _vm.Settings.Changed += () => Dispatcher.BeginInvoke(() => { _fadeColor = null; NavScroll_ScrollChanged(NavScroll, null!); }, DispatcherPriority.Background);
        // A page can be dragged to another place, in any group, with nothing on screen to say so (the user, 2026-11-24).
        NavScroll.PreviewMouseLeftButtonDown += Drag_Down;
        NavScroll.PreviewMouseMove += Drag_Move;
        NavScroll.PreviewMouseLeftButtonUp += Drag_Up;
        NavScroll.LostMouseCapture += (_, _) => { if (_drag is { Started: true }) EndDrag(drop: false); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _drag is { Started: true }) { EndDrag(drop: false); e.Handled = true; } };
    }

    // ── Dragging a page in the sidebar (approved mock-up 2026-11-24) ──
    // It starts once the pointer has moved 6 px with the button held, so a click still just opens the page. A line shows
    // where it will land; it can land in any group, before any page there or at a group's end. Undo comes up below the pages.

    private sealed class DragState(RadioButton row, ViewModels.NavEntry entry, Point from)
    {
        public RadioButton Row { get; } = row;
        public ViewModels.NavEntry Entry { get; } = entry;
        public Point From { get; } = from;
        public bool Started { get; set; }
        public double Opacity { get; set; }
        public DropLine? Line { get; set; }
        public (ViewModels.SidebarGroup Group, ViewModels.NavEntry? Before)? Target { get; set; }
    }

    private DragState? _drag;

    /// <summary>The panel holding the pages: positions and the line are measured in it, so they scroll with it.</summary>
    private FrameworkElement NavPanel => (FrameworkElement)NavScroll.Content;

    private void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        _drag = null;
        if (_vm.Sidebar.IconsOnly) return;
        for (var at = e.OriginalSource as DependencyObject; at is not null && at != NavScroll; at = VisualTreeHelper.GetParent(at))
            if (at is RadioButton { DataContext: ViewModels.NavEntry entry } row)
            {
                _drag = new DragState(row, entry, e.GetPosition(NavPanel));
                return;
            }
    }

    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (_drag is not { } drag) return;
        if (e.LeftButton != MouseButtonState.Pressed) { if (drag.Started) EndDrag(drop: false); else _drag = null; return; }
        var at = e.GetPosition(NavPanel);
        if (!drag.Started)
        {
            if (Math.Abs(at.X - drag.From.X) < 6 && Math.Abs(at.Y - drag.From.Y) < 6) return;
            drag.Started = true;
            drag.Opacity = drag.Row.Opacity;
            drag.Row.Opacity = 0.35;
            Mouse.Capture(NavScroll, CaptureMode.SubTree);
            if (AdornerLayer.GetAdornerLayer(NavPanel) is { } layer) layer.Add(drag.Line = new DropLine(NavPanel));
            Mouse.OverrideCursor = Cursors.SizeNS;
        }
        drag.Target = FindDrop(at.Y, drag.Entry, out double lineY);
        drag.Line?.Show(lineY);
        e.Handled = true;
    }

    private void Drag_Up(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not { Started: true }) { _drag = null; return; }
        e.Handled = true;
        EndDrag(drop: true);
    }

    private void EndDrag(bool drop)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        drag.Row.Opacity = drag.Opacity;
        if (drag.Line is { } line) AdornerLayer.GetAdornerLayer(NavPanel)?.Remove(line);
        Mouse.OverrideCursor = null;
        if (Mouse.Captured == NavScroll) Mouse.Capture(null);
        if (drop && drag.Target is { } target) _vm.Sidebar.Drop(drag.Entry, target.Group, target.Before);
    }

    /// <summary>
    /// Where a page let go at <paramref name="y"/> lands: before the first page or heading whose middle is below the
    /// pointer (a heading means the end of the group above it), else at the end of the last group.
    /// </summary>
    private (ViewModels.SidebarGroup, ViewModels.NavEntry?) FindDrop(double y, ViewModels.NavEntry dragged, out double lineY)
    {
        var sidebar = _vm.Sidebar;
        var marks = new List<(double Top, double Bottom, ViewModels.NavEntry? Entry, ViewModels.SidebarGroup? Heading)>();
        void Walk(DependencyObject from)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++)
            {
                var child = VisualTreeHelper.GetChild(from, i);
                if (child is FrameworkElement { IsVisible: true } el)
                {
                    if (el is RadioButton { DataContext: ViewModels.NavEntry entry } && entry != dragged)
                    {
                        var top = el.TranslatePoint(new Point(0, 0), NavPanel).Y;
                        marks.Add((top, top + el.ActualHeight, entry, null));
                        continue;
                    }
                    if (el is Button { CommandParameter: string key } heading && ReferenceEquals(heading.Command, sidebar.ToggleSectionCommand)
                        && sidebar.Groups.FirstOrDefault(g => g.Key == key) is { } group)
                    {
                        var top = el.TranslatePoint(new Point(0, 0), NavPanel).Y;
                        marks.Add((top, top + el.ActualHeight, null, group));
                        continue;
                    }
                }
                Walk(child);
            }
        }
        Walk(NavPanel);
        marks.Sort((a, b) => a.Top.CompareTo(b.Top));
        foreach (var m in marks)
        {
            if (y >= (m.Top + m.Bottom) / 2) continue;
            lineY = m.Top - 2;
            if (m.Entry is { } before) return (sidebar.GroupOfEntry(before) ?? sidebar.MainGroup, before);
            int at = sidebar.Groups.ToList().IndexOf(m.Heading!);
            return (sidebar.Groups[Math.Max(0, at - 1)], null);
        }
        lineY = marks.Count > 0 ? marks[^1].Bottom + 2 : 0;
        return (sidebar.Groups[^1], null);
    }

    /// <summary>The line that shows where a dragged page will land: the accent colour, across the sidebar, with a ring at its start.</summary>
    private sealed class DropLine(UIElement panel) : Adorner(panel)
    {
        private double _y = -100;

        public void Show(double y) { _y = y; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            if (_y < -50) return;
            var brush = (Brush)(Application.Current.TryFindResource("AccentBrush") ?? Brushes.White);
            double width = ((FrameworkElement)AdornedElement).ActualWidth;
            dc.DrawRectangle(brush, null, new Rect(10, _y - 1, Math.Max(0, width - 14), 2));
            dc.DrawEllipse((Brush)(Application.Current.TryFindResource("SidebarBrush") ?? Brushes.Black), new Pen(brush, 2), new Point(8, _y), 3.5, 3.5);
        }
    }

    // ── A sidebar that scrolls ──

    /// <summary>
    /// A soft fade at the end that has more (top, bottom or both); none when everything fits. Drawn as two strips over the
    /// list in the sidebar's colour, from solid at the edge to clear: an opacity mask did the same but redrew the whole
    /// list off screen on every scroll (opening All sensors took about 37 ms longer once the list scrolled, 2026-11-25).
    /// </summary>
    private void NavScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool scrolls = NavScroll.ScrollableHeight >= 0.5 && NavScroll.ActualHeight >= 80;
        bool above = scrolls && NavScroll.VerticalOffset > 1, below = scrolls && NavScroll.VerticalOffset < NavScroll.ScrollableHeight - 1;
        if (above || below) PaintFades();
        NavFadeTop.Visibility = above ? Visibility.Visible : Visibility.Collapsed;
        NavFadeBottom.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
    }

    private Color? _fadeColor;

    /// <summary>The strips' brushes, made again only when the sidebar's colour changed (a theme switch).</summary>
    private void PaintFades()
    {
        var color = (TryFindResource("SidebarBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        if (_fadeColor == color) return;
        _fadeColor = color;
        var clear = Color.FromArgb(0, color.R, color.G, color.B);
        static LinearGradientBrush Fade(Color from, Color to)
        {
            var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            return brush;
        }
        NavFadeTop.Background = Fade(color, clear);
        NavFadeBottom.Background = Fade(clear, color);
    }

    /// <summary>
    /// The open page's entry (or its group's icon, with icons only) is scrolled into view, before the window is drawn: at
    /// Loaded priority it ran after the new page was drawn and drew the window a second time, which made opening a page
    /// whose row is out of sight (All sensors, once All pages made the list scroll) about 35 ms slower (2026-11-25).
    /// </summary>
    private void KeepOpenPageInView() => Dispatcher.BeginInvoke(() =>
    {
        NavScroll.UpdateLayout();
        if (Find(NavScroll) is { } item) item.BringIntoView();
    }, DispatcherPriority.Normal);

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

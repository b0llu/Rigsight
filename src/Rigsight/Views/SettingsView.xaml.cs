using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rigsight.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class SettingsView : UserControl
{
    private const double EditorWidth = 430;
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    private SidebarViewModel? _sidebar;

    public SettingsView()
    {
        InitializeComponent();
        // Each time Settings is opened (the page is kept between visits), and when something new needs a look while it's
        // open: go straight to that section.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) ScrollToAttention(); };
        // A list dropped from a search box closes on a click anywhere else. The popup's own closing can't be relied on:
        // the box takes the mouse on the press and lets it go on the release, after the list opened, and from then on
        // a click outside never reached the popup.
        PreviewMouseDown += (_, e) => ClosePickers(e.OriginalSource as DependencyObject);
        // Signed up while the page is in a window, and off again as it leaves: the window outlives the page (a theme change
        // builds a new one), and a handler left on it kept every old Settings page in memory (MemoryLeakTests).
        Window? watched = null;
        Loaded += (_, _) =>
        {
            if (watched is not null) watched.Deactivated -= OnWindowDeactivated;
            watched = Window.GetWindow(this);
            if (watched is not null) watched.Deactivated += OnWindowDeactivated;
        };
        Unloaded += (_, _) =>
        {
            if (watched is not null) watched.Deactivated -= OnWindowDeactivated;
            watched = null;
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelChanged;
            Watch((e.NewValue as SettingsViewModel)?.Sidebar);
        };
        // Esc puts a page being dragged back where it was; otherwise it closes the sidebar editor.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _sidebar is not { IsEditing: true }) return;
            if (_grip is { IsDragging: true }) _grip.CancelDrag();
            else _sidebar.IsEditing = false;
            e.Handled = true;
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AttentionSection) && IsVisible) ScrollToAttention();
    }

    private void ScrollToAttention()
    {
        if (DataContext is not SettingsViewModel { AttentionSection: { } section } vm) return;
        // The section is a tab of its own, or sits on one: show that tab first.
        vm.Tab = section;
        // After layout, so the section's position is known.
        Dispatcher.BeginInvoke(() =>
        {
            Attention.ScrollTo(Scroller, section);
            vm.Arrived();
        }, DispatcherPriority.Loaded);
    }

    // ── The sidebar editor ──

    private void Watch(SidebarViewModel? sidebar)
    {
        if (_sidebar is not null) _sidebar.PropertyChanged -= OnSidebarChanged;
        _sidebar = sidebar;
        if (sidebar is null) return;
        sidebar.PropertyChanged += OnSidebarChanged;
        ShowEditor(sidebar.IsEditing, animate: false);
    }

    private void OnSidebarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_sidebar is not null && e.PropertyName == nameof(SidebarViewModel.IsEditing)) ShowEditor(_sidebar.IsEditing, animate: true);
    }

    /// <summary>
    /// The editor slides in over the page from the right while the page dims a little behind it; closing slides it
    /// back out and takes it (and the dimming) away. The page itself never moves. (The widget editor's way: see WidgetsView.)
    /// </summary>
    private void ShowEditor(bool show, bool animate)
    {
        var duration = TimeSpan.FromMilliseconds(animate ? (show ? 260 : 200) : 0);
        if (show)
        {
            EditorPanel.Visibility = Visibility.Visible;
            EditorScrim.Visibility = Visibility.Visible;
            // The keys go to the editor from here on: Esc closes it, Tab walks its rows and stays among them.
            if (animate) EditorPanel.Focus();
        }
        var slide = new DoubleAnimation(show ? 0 : EditorWidth, duration) { EasingFunction = Ease };
        if (!show) slide.Completed += (_, _) =>
        {
            if (_sidebar is { IsEditing: true }) return; // opened again meanwhile
            EditorPanel.Visibility = Visibility.Collapsed;
            EditorScrim.Visibility = Visibility.Collapsed;
        };
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
        EditorScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 0.3 : 0, duration) { EasingFunction = Ease }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>A click outside the editor, on the page, closes it.</summary>
    private void EditorScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_sidebar is { IsEditing: true }) _sidebar.IsEditing = false;
        e.Handled = true;
    }

    // ── Dragging a page by its grip ──

    /// <summary>A place a dragged page can land: before a row, after a group's last row, or in an emptied group's box.</summary>
    private sealed record Slot(SidebarGroup Group, int Index, double Y, Shape? Box);

    private Thumb? _grip;
    private FrameworkElement? _draggedRow;
    private List<Slot> _slots = [];
    private Slot? _drop;

    private void Grip_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { DataContext: NavEntry } grip) return;
        _grip = grip;
        _slots = Slots(); // the rows stay where they are until the drop
        _draggedRow = RowOf(grip);
        if (_draggedRow is not null) _draggedRow.Opacity = 0.4;
    }

    /// <summary>The nearest place to the pointer is marked: a line between rows, or the outline of an emptied group's box.</summary>
    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_grip is null || _slots.Count == 0) return;
        double y = Mouse.GetPosition(EditorRows).Y;
        Mark(_slots.MinBy(s => Math.Abs(s.Y - y)));

        // Near the top or bottom of the panel, the list moves along so a place out of view can be reached.
        double inView = Mouse.GetPosition(EditorScroll).Y;
        if (inView < 28) EditorScroll.ScrollToVerticalOffset(EditorScroll.VerticalOffset - 14);
        else if (inView > EditorScroll.ActualHeight - 28) EditorScroll.ScrollToVerticalOffset(EditorScroll.VerticalOffset + 14);
    }

    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var (drop, entry) = (_drop, (_grip?.DataContext) as NavEntry);
        Mark(null);
        _draggedRow?.ClearValue(OpacityProperty);
        (_grip, _draggedRow, _slots) = (null, null, []);
        if (!e.Canceled && drop is not null && entry is not null && _sidebar?.MoveTo(entry, drop.Group, drop.Index) == true) FocusGrip(entry);
    }

    /// <summary>The arrow keys on a grip move its page one place, into the next group at an edge: dragging without a mouse.</summary>
    private void Grip_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || sender is not Thumb { DataContext: NavEntry entry, IsDragging: false } || _sidebar is null) return;
        e.Handled = true; // at the very top or bottom too: the arrow mustn't walk the focus off the grip
        if (_sidebar.MoveBy(entry, e.Key == Key.Up ? -1 : 1)) FocusGrip(entry);
    }

    /// <summary>
    /// A moved page's row is built anew in its new place: its grip takes the focus back, so the next arrow press
    /// carries on from there (and the keys stay with the editor: Esc still closes it).
    /// </summary>
    private void FocusGrip(NavEntry entry)
    {
        EditorRows.UpdateLayout();
        if (GripOf(entry) is not { } grip) return;
        grip.Focus();
        grip.BringIntoView();
    }

    private void Mark(Slot? slot)
    {
        if (_drop?.Box is { } was) was.SetResourceReference(Shape.StrokeProperty, "StrokeBrush");
        _drop = slot;
        if (slot?.Box is { } box) box.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        if (slot is null || slot.Box is not null)
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }
        DropLine.Width = EditorRows.ActualWidth;
        Canvas.SetTop(DropLine, slot.Y - 1);
        DropLine.Visibility = Visibility.Visible;
    }

    /// <summary>Every place a page can be dropped, with how far down the list it is.</summary>
    private List<Slot> Slots()
    {
        var slots = new List<Slot>();
        if (_sidebar is null) return slots;
        foreach (var group in _sidebar.Groups)
        {
            if (EditorGroups.ItemContainerGenerator.ContainerFromItem(group) is not ContentPresenter host) continue;
            if (group.Pages.Count == 0)
            {
                if (Part(host, "DropBox") is FrameworkElement { IsVisible: true } box)
                    slots.Add(new(group, 0, Place(box).Top + box.ActualHeight / 2, Part(host, "DropEdge") as Shape));
                continue;
            }
            if (Part(host, "Pages") is not ItemsControl pages) continue;
            for (int i = 0; i < group.Pages.Count; i++)
            {
                if (pages.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) continue;
                var at = Place(row);
                slots.Add(new(group, i, at.Top, null));
                if (i == group.Pages.Count - 1) slots.Add(new(group, i + 1, at.Bottom, null));
            }
        }
        return slots;
    }

    /// <summary>A named part of a group's template, or null while the group isn't drawn yet (FindName throws then).</summary>
    private static object? Part(ContentPresenter host, string name)
    {
        if (host.ContentTemplate is not { } template || !host.IsLoaded) return null;
        try { return template.FindName(name, host); }
        catch (InvalidOperationException) { return null; }
    }

    private Rect Place(FrameworkElement element) =>
        element.TransformToAncestor(EditorRows).TransformBounds(new Rect(element.RenderSize));

    private Thumb? GripOf(NavEntry entry)
    {
        foreach (var group in _sidebar?.Groups ?? [])
        {
            if (EditorGroups.ItemContainerGenerator.ContainerFromItem(group) is ContentPresenter host
                && Part(host, "Pages") is ItemsControl pages
                && pages.ItemContainerGenerator.ContainerFromItem(entry) is { } row)
                return Descendant<Thumb>(row);
        }
        return null;
    }

    /// <summary>The whole row a grip is in (the template's "Row").</summary>
    private static FrameworkElement? RowOf(DependencyObject grip)
    {
        for (var at = VisualTreeHelper.GetParent(grip); at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is FrameworkElement { Name: "Row" } row) return row;
        return null;
    }

    private static T? Descendant<T>(DependencyObject from) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++)
        {
            var child = VisualTreeHelper.GetChild(from, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => ClosePickers(null);

    /// <summary>Closes each open drop-down list unless the click was on the box that opened it (which keeps it open).</summary>
    private void ClosePickers(DependencyObject? clicked)
    {
        // A click on words in a line (a Run, as each model's name and size are) comes from the words, not a Visual:
        // taken from the line they are in, or it would count as a click outside and close the list before the pick.
        while (clicked is FrameworkContentElement words) clicked = words.Parent;
        bool On(FrameworkElement box) => clicked is Visual v && (v == box || box.IsAncestorOf(v));
        if (DataContext is not SettingsViewModel vm) return;
        // A click inside an open list (a popup, its own window) reaches here too: it is a pick, so the list stays open
        // until the pick closes it (closing it on the press lost every pick).
        if (clicked is Visual inside && PresentationSource.FromVisual(inside) is { } source && source != PresentationSource.FromVisual(this)) return;
        if (vm.Ai is { } ai)
        {
            if (ai.IsPickingModel && !On(ModelButton)) ai.IsPickingModel = false;
        }
        if (vm.Rewind is { IsPicking: true } rewind && !On(LeaveSearchBox)) rewind.IsPicking = false;
    }

    /// <summary>Settings → Rewind's "Never show": the list opens as the click on the search box lets go, or as it gets the keyboard.</summary>
    private void LeaveSearch_Open(object sender, System.Windows.Input.InputEventArgs e) =>
        (DataContext as ViewModels.SettingsViewModel)?.Rewind?.OpenPickerCommand.Execute(null);

    /// <summary>Reached with the keyboard, the list opens too; a click's own focus waits for the click to end.</summary>
    private void LeaveSearch_Focus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Released) LeaveSearch_Open(sender, e);
    }

    /// <summary>Settings › Ask RigSight → My own key: what's typed goes to the view model (the box keeps it out of bindings).</summary>
    private void ApiKey_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel { Ai: { } ai } && sender is System.Windows.Controls.PasswordBox box) ai.ApiKey = box.Password;
    }

    /// <summary>Settings › Ask RigSight: the model list opens with its search box ready to type in.</summary>
    private void ModelPopup_Opened(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => ModelSearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    private void LeaveSearch_Key(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel { Rewind: { } rewind }) return;
        if (e.Key == System.Windows.Input.Key.Escape) { rewind.IsPicking = false; e.Handled = true; }
        else rewind.OpenPickerCommand.Execute(null);
    }
}

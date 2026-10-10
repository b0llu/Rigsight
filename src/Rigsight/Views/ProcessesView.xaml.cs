using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Rigsight.Models;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
        // Keeping the list sorted and the totals up costs work on every update; only while this page is shown.
        Loaded += (_, _) => Vm?.SetShown(true);
        Unloaded += (_, _) => Vm?.SetShown(false);
        Scroller.SizeChanged += (_, _) => FitLiveList();
        Layout.SizeChanged += (_, _) => FitLiveList();
        LiveList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => AlignHeadings(e.ViewportWidth)));
    }

    private ProcessesViewModel? Vm => DataContext as ProcessesViewModel;

    /// <summary>What a row keeps clear on its right: its own margin (8) and the padding of its highlight (10).</summary>
    private const double RowInset = 18;

    /// <summary>
    /// The column headings end where the rows' figures end: the rows lose the scrollbar's width (and its gap) when the
    /// list is long enough to scroll, and the headings above the list don't.
    /// </summary>
    private void AlignHeadings(double viewportWidth)
    {
        if (viewportWidth <= 0) return;
        double right = RowInset + Math.Max(0, LiveList.ActualWidth - viewportWidth);
        if (Math.Abs(LiveHead.Margin.Right - right) > 0.1) LiveHead.Margin = new Thickness(LiveHead.Margin.Left, 0, right, 6);
    }

    /// <summary>
    /// The list fills what the window has left under the totals, as tall as the window allows, and stops shrinking at
    /// its MinHeight: a smaller window scrolls the page instead of squeezing the list to a few rows.
    /// </summary>
    private void FitLiveList()
    {
        double above = Layout.RowDefinitions[0].ActualHeight + Layout.RowDefinitions[1].ActualHeight;
        double room = Math.Floor(Scroller.ActualHeight - Layout.Margin.Top - Layout.Margin.Bottom - LiveCard.Margin.Top - above);
        double height = Math.Max(LiveCard.MinHeight, room);
        if (LiveCard.Height != height) LiveCard.Height = height;
    }

    // ── Opening an app ──

    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        // Ctrl or Shift with the click picks rows (the list does that itself); a plain click opens the app.
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;
        if (sender is FrameworkElement { DataContext: ProcRow row }) Vm?.ToggleCommand.Execute(row);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || vm.Question.IsOpen || e.OriginalSource is not ListBoxItem { DataContext: ProcRow row } item) return;
        if (e.Key is Key.Enter or Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            vm.ToggleCommand.Execute(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) || (e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            Pick(row);
            Open(vm.MenuFor(Picked()), item, PlacementMode.Bottom);
            e.Handled = true;
        }
    }

    // ── The menus: right-click a row, or its dots ──

    /// <summary>The rows picked, in the list's order.</summary>
    private List<ProcRow> Picked() => [.. LiveList.Items.Cast<ProcRow>().Where(LiveList.SelectedItems.Contains)];

    /// <summary>A row not among the picked ones becomes the only one picked; one that is leaves them as they are.</summary>
    private void Pick(ProcRow row)
    {
        if (LiveList.SelectedItems.Contains(row)) return;
        LiveList.SelectedItems.Clear();
        LiveList.SelectedItems.Add(row);
    }

    private void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement { DataContext: ProcRow row } head) return;
        e.Handled = true;
        if (row.IsEnding) return;
        Pick(row);
        Open(vm.MenuFor(Picked()), head, PlacementMode.MousePoint);
    }

    private void AppMore_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Button { DataContext: ProcRow row } button) return;
        Pick(row);
        Open(vm.MenuFor(Picked()), button, PlacementMode.Custom);
    }

    private void Process_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement { DataContext: ProcChild process } line || AppOf(line) is not { } app) return;
        e.Handled = true;
        if (process.IsEnding || app.IsEnding) return;
        Open(vm.MenuFor(app, process), line, PlacementMode.MousePoint);
    }

    private void ProcessMore_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Button { DataContext: ProcChild process } button || AppOf(button) is not { } app) return;
        Open(vm.MenuFor(app, process), button, PlacementMode.Custom);
    }

    /// <summary>The app a process's line belongs to: the row of the list it sits in.</summary>
    private ProcRow? AppOf(DependencyObject element) =>
        (ItemsControl.ContainerFromElement(LiveList, element) as ListBoxItem)?.DataContext as ProcRow;

    /// <summary>
    /// Shows a menu the page built. While it is open the list holds still (the page is told), so the row it belongs to
    /// stays under it. From the dots it opens under them, right edges lined up: they sit near the window's edge.
    /// </summary>
    private void Open(IReadOnlyList<ProcMenuEntry> entries, UIElement target, PlacementMode placement)
    {
        if (entries.Count == 0 || Vm is not { } vm) return;
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        if (placement == PlacementMode.Custom)
            menu.CustomPopupPlacementCallback = (popup, at, _) => [new CustomPopupPlacement(new Point(at.Width - popup.Width, at.Height + 4), PopupPrimaryAxis.Horizontal)];

        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case ProcMenuKind.Separator:
                    menu.Items.Add(new Separator());
                    break;
                case ProcMenuKind.Header:
                    menu.Items.Add(new MenuItem { Header = entry.Text, IsEnabled = false });
                    break;
                case ProcMenuKind.Note:
                    // Why the entry above it is off, in a few words that wrap.
                    menu.Items.Add(new MenuItem
                    {
                        IsEnabled = false,
                        Header = new TextBlock { Text = entry.Text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 },
                    });
                    break;
                default:
                    // Every item has an icon, so their words line up (see the Apps page's menu).
                    var icon = new TextBlock { Text = entry.Glyph, Style = (Style)Application.Current.FindResource("Icon"), FontSize = 14 };
                    var item = new MenuItem { Header = entry.Text, Icon = icon, InputGestureText = entry.Hint ?? "", IsEnabled = entry.Enabled, FocusVisualStyle = null };
                    // Ending is set apart from the rest: after the line, and in the colour of a warning.
                    if (entry.Danger && entry.Enabled)
                    {
                        item.SetResourceReference(ForegroundProperty, "HotBrush");
                        icon.SetResourceReference(TextBlock.ForegroundProperty, "HotBrush");
                    }
                    if (entry.Run is { } run) item.Click += (_, _) => _ = run();
                    menu.Items.Add(item);
                    break;
            }
        }

        menu.Opened += (_, _) => vm.SetMenuOpen(true);
        menu.Closed += (_, _) => vm.SetMenuOpen(false);
        menu.IsOpen = true;
    }
}

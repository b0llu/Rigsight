using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Controls;
using Rigsight.Core;

namespace Rigsight.Views;

public partial class GamesView : UserControl
{
    public GamesView()
    {
        InitializeComponent();
        Covers.LayoutUpdated += (_, _) => FitLibrary();
        // A game opened is its own page: it starts at its top, and the library keeps its place for the way back.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.GamesViewModel old) { old.PropertyChanged -= OnChanged; old.RunsRangeEditRequested -= OpenRunsRangeEditor; }
            if (e.NewValue is ViewModels.GamesViewModel vm) { vm.PropertyChanged += OnChanged; vm.RunsRangeEditRequested += OpenRunsRangeEditor; }
        };
        // "How it runs": the range menu and editor as on Temperatures, and a play or week clicked on the chart.
        RunsPager.LabelCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(OpenRunsMenu);
        RunsMenu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(target.Width - popup.Width + 8, target.Height + 6), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
        RunsRangeEditor.Applied += (from, to) => (DataContext as ViewModels.GamesViewModel)?.SetRunsCustom(from, to);
        Runs.Opened += point => (DataContext as ViewModels.GamesViewModel)?.OpenFromChart(point);
    }

    private long _runsMenuClosedAt;

    private void OpenRunsMenu()
    {
        // In a play the label only says which play; a click on it while the menu is open closes it and must not open it again.
        if (DataContext is not ViewModels.GamesViewModel { FramesView: false }) return;
        if (Environment.TickCount64 - _runsMenuClosedAt < 250) return;
        RunsMenu.IsOpen = true;
    }

    private void RunsMenu_Closed(object? sender, EventArgs e) => _runsMenuClosedAt = Environment.TickCount64;

    private void RunsMenu_Click(object sender, RoutedEventArgs e) => RunsMenu.IsOpen = false;

    private void OpenRunsRangeEditor()
    {
        if (DataContext is not ViewModels.GamesViewModel vm) return;
        var (from, to) = (vm.RunsFrom, vm.RunsTo);
        Dispatcher.BeginInvoke(() => RunsRangeEditor.Open(RunsPager, from, to, vm.RunsHistoryStart), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The library's search and buttons beside its title where they fit, else on a line of their own under it (at the
    /// window's narrowest they ran over the title and the search box was cut).
    /// </summary>
    private void LibraryHead_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        LibraryTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        LibraryTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool below = LibraryTitle.DesiredSize.Width + 20 + LibraryTools.DesiredSize.Width > LibraryHead.ActualWidth;
        Grid.SetRow(LibraryTools, below ? 1 : 0);
        Grid.SetColumn(LibraryTools, below ? 0 : 1);
        Grid.SetColumnSpan(LibraryTools, below ? 2 : 1);
        LibraryTools.HorizontalAlignment = below ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        LibraryTools.Margin = below ? new Thickness(0, 14, 0, 0) : new Thickness(20, 0, 0, 0);
    }

    private double _libraryOffset;
    private bool _wasGame;
    private string? _openKey;

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.GamesViewModel.Search) && sender is ViewModels.GamesViewModel searched)
        {
            // While searching, the box keeps the height it had: fewer covers (or none) don't pull the shelf up.
            bool searching = Core.TextMatch.Words(searched.Search).Length > 0;
            if (searching && LibraryBox.MinHeight == 0) LibraryBox.MinHeight = LibraryBox.ActualHeight;
            else if (!searching) LibraryBox.MinHeight = 0;
            return;
        }
        if (e.PropertyName != nameof(ViewModels.GamesViewModel.IsGame) || sender is not ViewModels.GamesViewModel vm) return;
        bool wasGame = _wasGame;
        _wasGame = vm.IsGame;
        string? key = vm.Opened?.Entry.Key;
        bool sameGame = wasGame && key == _openKey;
        _openKey = key;
        if (vm.IsGame)
        {
            if (!wasGame) _libraryOffset = PageScroll.VerticalOffset;
            // The page's own refresh opens the same game again: the reader stays where they were.
            if (!sameGame) PageScroll.ScrollToTop();
        }
        else if (wasGame)
        {
            double back = _libraryOffset;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => PageScroll.ScrollToVerticalOffset(back));
        }
    }

    /// <summary>
    /// The library box shows two full rows of covers and half of the third, so it's plain there's more to scroll and the
    /// shelf under it stays in sight; with fewer games it shrinks to them (no empty space).
    /// </summary>
    private void FitLibrary()
    {
        if (FindGrid(Covers) is not { RowHeight: > 0 } grid) return;
        double max = grid.RowHeight * 2.5 + grid.RowGap * 2 + 8;
        if (Math.Abs(LibraryBox.MaxHeight - max) > 0.5) LibraryBox.MaxHeight = max;
    }

    // ── How it's set: a list that plainly scrolls ──

    private const double SettingsTallest = 372, SettingsFade = 36;

    /// <summary>
    /// The list stops halfway through a line (a list that ended on a line's edge looked complete), the end with more fades as
    /// the sidebar's does, and "N more settings" counts the lines not wholly in sight; none of it when everything fits.
    /// </summary>
    private void SettingsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var box = SettingsScroll;
        double cut = SettingsTallest;
        int below = 0;
        for (int i = 0; i < SettingsList.Items.Count; i++)
        {
            if (SettingsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement line || line.ActualHeight <= 0) continue;
            double top = line.TranslatePoint(new Point(), SettingsList).Y;
            if (top < SettingsTallest && top + line.ActualHeight > SettingsTallest) cut = top + line.ActualHeight / 2;
            double inView = line.TranslatePoint(new Point(), box).Y;
            if (SettingsList.Items[i] is ViewModels.SettingLine { IsGroup: false } && inView + line.ActualHeight > box.ViewportHeight + 1) below++;
        }
        if (Math.Abs(box.MaxHeight - cut) > 0.5) box.MaxHeight = cut;

        if (box.ScrollableHeight < 0.5) { box.OpacityMask = null; ShowSettingsMore(false); return; }
        bool above = box.VerticalOffset > 1, more = box.VerticalOffset < box.ScrollableHeight - 1;
        double f = SettingsFade / Math.Max(box.ActualHeight, SettingsFade * 2);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(above ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, f));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
        mask.GradientStops.Add(new GradientStop(more ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        box.OpacityMask = mask;
        if (more && below > 0)
        {
            SettingsMoreText.Text = below == 1 ? Lang.T("1 more setting") : Lang.T($"{below} more settings");
            System.Windows.Automation.AutomationProperties.SetName(SettingsMore, SettingsMoreText.Text);
        }
        ShowSettingsMore(more && below > 0);
    }

    /// <summary>The button fades in and out (pop-ins fade only).</summary>
    private void ShowSettingsMore(bool show)
    {
        bool shown = SettingsMore.Visibility == Visibility.Visible && SettingsMore.Tag is not "hiding";
        if (show == shown) return;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(150));
        if (show) { if (SettingsMore.Visibility != Visibility.Visible) fade.From = 0; SettingsMore.Tag = null; SettingsMore.Visibility = Visibility.Visible; }
        else
        {
            SettingsMore.Tag = "hiding";
            fade.Completed += (_, _) => { if (SettingsMore.Tag is "hiding") { SettingsMore.Visibility = Visibility.Collapsed; SettingsMore.Tag = null; } };
        }
        SettingsMore.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>On by most of a box, so the line that was cut in half is whole at the top.</summary>
    private void SettingsMore_Click(object sender, RoutedEventArgs e) =>
        SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset + Math.Max(SettingsScroll.ViewportHeight - 60, 40));

    private static CoverGrid? FindGrid(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CoverGrid g) return g;
            if (FindGrid(child) is { } found) return found;
        }
        return null;
    }
}

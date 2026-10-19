using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Controls;

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
            if (e.OldValue is ViewModels.GamesViewModel old) old.PropertyChanged -= OnChanged;
            if (e.NewValue is ViewModels.GamesViewModel vm) vm.PropertyChanged += OnChanged;
        };
    }

    private double _libraryOffset;
    private bool _wasGame;

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
        if (vm.IsGame)
        {
            if (!wasGame) _libraryOffset = PageScroll.VerticalOffset;
            PageScroll.ScrollToTop();
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

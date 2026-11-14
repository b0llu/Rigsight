using System.Windows;
using System.Windows.Controls;
using Rigsight.Services;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class StorageView : UserControl
{
    public StorageView()
    {
        InitializeComponent();
        Map.ItemActivated += node =>
        {
            if (DataContext is StorageViewModel vm) vm.Open(node);
        };
        // The map and the list light the same item: the block pointed at lights its row, and back.
        Map.HoverChanged += node =>
        {
            if (DataContext is StorageViewModel vm) vm.Hot = node;
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is StorageViewModel old) { old.ScanDone -= ShowScanner; old.ReviewRequested -= ShowScanner; old.DriveOpened -= ToTop; }
            if (e.NewValue is StorageViewModel vm) { vm.ScanDone += ShowScanner; vm.ReviewRequested += ShowScanner; vm.DriveOpened += ToTop; }
        };
        InsideList.LayoutUpdated += (_, _) => FitInsideBox();
        // The drive's own readings: their headings keep clear of the list's scroll bar while it shows, so each heading
        // stays over its column (the user, 2026-11-14: "alignment not proper below the table header").
        ReadingsBox.ScrollChanged += (_, _) => KeepHeadOverColumns();
        ReadingsBox.SizeChanged += (_, _) => KeepHeadOverColumns();
    }

    /// <summary>A drive opened or closed: the page starts at its top.</summary>
    private void ToTop() => Page.ScrollToTop();

    /// <summary>
    /// Over six drives the table scrolls in its own box: as tall as the first six drives (with their letters) and half
    /// the seventh, so the cut line shows there is more (the user, 2026-11-14: "cut the last drive in half").
    /// </summary>
    private void FitInsideBox()
    {
        int count = InsideList.Items.Count;
        if (count <= StorageViewModel.MaxInsideRows)
        {
            if (!double.IsPositiveInfinity(InsideBox.MaxHeight)) InsideBox.MaxHeight = double.PositiveInfinity;
            return;
        }
        double height = 0;
        for (int i = 0; i <= StorageViewModel.MaxInsideRows; i++)
        {
            if (InsideList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || row.ActualHeight <= 0) return;
            height += i < StorageViewModel.MaxInsideRows ? row.ActualHeight : Math.Min(row.ActualHeight, 64) / 2;
        }
        if (Math.Abs(InsideBox.MaxHeight - height) > 0.5) InsideBox.MaxHeight = height;
    }

    private void KeepHeadOverColumns()
    {
        double bar = ReadingsBox.ComputedVerticalScrollBarVisibility == Visibility.Visible ? Math.Max(0, ReadingsBox.ActualWidth - ReadingsBox.ViewportWidth) : 0;
        if (Math.Abs(OwnHead.Margin.Right - bar) > 0.5) OwnHead.Margin = new Thickness(0, 0, bar, 0);
    }

    /// <summary>A scan is done, or a cleanup finding's Review was pressed: the page goes to the scan.</summary>
    private void ShowScanner() => Scanner.BringIntoView();

    private void Row_Enter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is StorageViewModel vm && (sender as FrameworkElement)?.DataContext is FolderNode node) vm.Hot = node;
    }

    private void Row_Leave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is StorageViewModel vm && (sender as FrameworkElement)?.DataContext is FolderNode node && ReferenceEquals(vm.Hot, node)) vm.Hot = null;
    }
}

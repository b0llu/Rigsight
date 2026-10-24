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
            if (e.OldValue is StorageViewModel old) { old.ScanDone -= ShowScanner; old.ReviewRequested -= ShowScanner; }
            if (e.NewValue is StorageViewModel vm) { vm.ScanDone += ShowScanner; vm.ReviewRequested += ShowScanner; }
        };
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

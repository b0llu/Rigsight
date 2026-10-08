using System.Windows.Controls;
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
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is StorageViewModel old) old.ScanDone -= ShowScanner;
            if (e.NewValue is StorageViewModel vm) vm.ScanDone += ShowScanner;
        };
    }

    /// <summary>A scan started from a drive's card is done: what it found is further down the page, so the page goes there.</summary>
    private void ShowScanner() => Scanner.BringIntoView();
}

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
    }
}

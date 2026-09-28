using System.Windows;
using System.Windows.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OpenRecap_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is ShellViewModel shell && DataContext is HomeViewModel home)
            shell.Navigate("reports", home.RecapArg);
    }
}

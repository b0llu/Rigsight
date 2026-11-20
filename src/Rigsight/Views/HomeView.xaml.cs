using System.Windows;
using System.Windows.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    /// <summary>The pointer or keyboard reached the Your usual link: read it ahead, so the panel opens with its lines.</summary>
    private void UsualLink_Near(object sender, RoutedEventArgs e) => (DataContext as HomeViewModel)?.PrefetchUsual?.Invoke();

    private void OpenRecap_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is ShellViewModel shell && DataContext is HomeViewModel home)
            shell.Navigate("reports", home.RecapArg);
    }
}

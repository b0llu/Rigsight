using System.Windows.Controls;

namespace Rigsight.Views;

public partial class CrashesView : UserControl
{
    public CrashesView()
    {
        InitializeComponent();
        // Leaving the page: back to the newest crashes, so opening it again lays out one page of cards, not every card
        // scrolled into view (a hundred and more froze the window on each visit).
        Unloaded += (_, _) =>
        {
            Scroller.ScrollToTop();
            (DataContext as ViewModels.CrashesViewModel)?.ShowFirstPage();
        };
    }
}

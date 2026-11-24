using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class PowerView : UserControl
{
    public PowerView() => InitializeComponent();

    /// <summary>The price box's currency list, closed: wide enough for the widest of every currency's short name ("NAf. ANG", 60 px of about 82), a test checks all of them.</summary>
    public const double CurrencyWidth = 132;

    /// <summary>Enter saves the price (when it reads as one), Escape closes the box.</summary>
    private void Price_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PowerViewModel vm) return;
        if (e.Key == Key.Enter && vm.SavePriceCommand.CanExecute(null)) { vm.SavePriceCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.CancelPriceCommand.Execute(null); e.Handled = true; }
    }
}

using System.Windows.Controls;
using System.Windows.Input;

namespace Rigsight.Views;

public partial class FansView : UserControl
{
    public FansView() => InitializeComponent();

    /// <summary>
    /// The fan list's height limit: about seven fans in two groups fit; more scroll inside it. Fixed, so scrolling
    /// depends on how many fans there are, not on which is picked (the details beside it change height).
    /// </summary>
    public const double ListMaxHeight = 480;

    /// <summary>Enter keeps a fan's new name, Escape puts the old one back.</summary>
    private void Rename_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }
}

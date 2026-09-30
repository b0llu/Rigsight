using System.Windows.Controls;
using System.Windows.Input;

namespace Rigsight.Views;

public partial class FansView : UserControl
{
    public FansView() => InitializeComponent();

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

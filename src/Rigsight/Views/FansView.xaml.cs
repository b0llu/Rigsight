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

    /// <summary>A Today fact under the pointer or the keyboard: its stretch marked on the strip above (the fact's id is its Tag).</summary>
    private void Fact_Enter(object sender, System.Windows.RoutedEventArgs e) => Strip.Mark = (sender as System.Windows.FrameworkElement)?.Tag as string;

    private void Fact_Leave(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement f && (f.IsMouseOver || f.IsKeyboardFocusWithin)) return;
        Strip.Mark = null;
    }

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

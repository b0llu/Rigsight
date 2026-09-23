using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Rigsight.Views;

public partial class SensorsView : UserControl
{
    public SensorsView() => InitializeComponent();

    private void Header_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.HardwareNode node } && DataContext is ViewModels.LiveData live)
            live.ToggleExpandedCommand.Execute(node);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Rigsight.Core.Apps;
using Rigsight.Core.Settings;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();

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

    /// <summary>The "⋯" menu: the app's category, whether it's tracked, and where its file is. Built on each open,
    /// so the ticks always match the app shown.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AppsViewModel vm || !vm.HasSelection || sender is not Button button) return;
        // Opens under the button, right edges lined up: the button sits at the right of the card, near the window's edge.
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
                [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.Horizontal)],
        };

        var category = new MenuItem { Header = "Category" };
        foreach (var c in vm.Categories)
        {
            var item = new MenuItem { Header = AppCatalog.Label(c), IsCheckable = true, IsChecked = c == vm.SelectedCategory };
            item.Click += (_, _) => vm.SelectedCategory = c;
            category.Items.Add(item);
        }
        menu.Items.Add(category);

        var track = new MenuItem { Header = "Don't track this app", IsCheckable = true, IsChecked = vm.SelectedExcluded,
            ToolTip = "Stop recording this app (its existing history stays until you clear it)." };
        track.Click += (_, _) => vm.SelectedExcluded = track.IsChecked;
        menu.Items.Add(track);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Open file location", Command = vm.OpenFileLocationCommand,
            IsEnabled = vm.Selected?.Path is { Length: > 0 } });
        menu.IsOpen = true;
    }
}

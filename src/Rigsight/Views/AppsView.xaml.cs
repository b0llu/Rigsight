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

        // Every item has an icon, so their words line up: Windows' menus leave room for a tick only beside items that can
        // be ticked, which pushed those right of the others. No focus outline either: the highlight already shows where
        // the keyboard is.
        static MenuItem Item(string header, string glyph) => new()
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Icon"), FontSize = 14 },
            FocusVisualStyle = null,
        };

        var category = Item("Category", ""); // tag
        foreach (var c in vm.Categories)
        {
            var item = new MenuItem { Header = AppCatalog.Label(c), IsCheckable = true, IsChecked = c == vm.SelectedCategory, FocusVisualStyle = null };
            item.Click += (_, _) => vm.SelectedCategory = c;
            category.Items.Add(item);
        }
        menu.Items.Add(category);

        // Says what picking it does, rather than a tick.
        bool excluded = vm.SelectedExcluded;
        var track = excluded ? Item("Track this app again", "") : Item("Don't track this app", ""); // eye / hide
        track.ToolTip = excluded ? "Start recording this app again." : "Stop recording this app (its existing history stays until you clear it).";
        track.Click += (_, _) => vm.SelectedExcluded = !excluded;
        menu.Items.Add(track);

        menu.Items.Add(new Separator());
        var open = Item("Open file location", ""); // folder
        open.Command = vm.OpenFileLocationCommand;
        open.IsEnabled = vm.Selected?.Path is { Length: > 0 };
        menu.Items.Add(open);
        menu.IsOpen = true;
    }
}

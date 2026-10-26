using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Rigsight.Core;
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

        // What it is, asked of the AI: first, while the AI is on and Apps is shared (mock-up, 2026-10-26).
        if (vm.Selected?.Name is { Length: > 0 } name && new Services.AiQuestion(Lang.T($"What is {name}, and how do I use it on my PC?"), Core.Ai.AiPages.Apps) is var ask
            && Services.AiGate.Shows(ask))
        {
            var ai = new MenuItem { Header = Lang.T("Ask what this is"), Icon = ProcessesView.AiIcon(), FocusVisualStyle = null };
            ai.Click += (_, _) => Services.AiGate.Ask.Execute(ask);
            menu.Items.Add(ai);
            menu.Items.Add(new Separator());
        }

        var category = Item(Lang.T("Category"), ""); // tag
        foreach (var c in vm.Categories)
        {
            var item = new MenuItem { Header = AppCatalog.Label(c), IsCheckable = true, IsChecked = c == vm.SelectedCategory, FocusVisualStyle = null };
            item.Click += (_, _) => vm.SelectedCategory = c;
            category.Items.Add(item);
        }
        menu.Items.Add(category);

        // Says what picking it does, rather than a tick.
        bool excluded = vm.SelectedExcluded;
        var track = excluded ? Item(Lang.T("Track this app again"), "") : Item(Lang.T("Don't track this app"), ""); // eye / hide
        track.ToolTip = excluded ? Lang.T("Start recording this app again.") : Lang.T("Stop recording this app (its existing history stays until you clear it).");
        track.Click += (_, _) => vm.SelectedExcluded = !excluded;
        menu.Items.Add(track);

        menu.Items.Add(new Separator());
        var open = Item(Lang.T("Open file location"), ""); // folder
        open.Command = vm.OpenFileLocationCommand;
        open.IsEnabled = vm.Selected?.Path is { Length: > 0 };
        menu.Items.Add(open);
        menu.IsOpen = true;
    }
}

/// <summary>
/// The selected app's groups (Time, Hardware, Internet): side by side in equal columns, all as tall as the tallest so
/// their boxes line up, or one above the other when narrower than <see cref="Threshold"/>, where three columns would cut
/// their figures off.
/// </summary>
public sealed class GroupRow : Panel
{
    /// <summary>
    /// The width from which the groups sit side by side, as the user had them (Time | Hardware | Internet). At 680 they
    /// stacked at an ordinary window size; below 520 a column is too narrow for its figures.
    /// </summary>
    public double Threshold { get; set; } = 520;

    /// <summary>The space between two groups, either way.</summary>
    public double Gap { get; set; } = 12;

    /// <summary>Whether a panel this wide puts the groups one above the other.</summary>
    internal bool Stacks(double width) => !double.IsInfinity(width) && width < Threshold;

    private double ColumnWidth(double width) => Math.Max(0, (width - Gap * (InternalChildren.Count - 1)) / InternalChildren.Count);

    protected override Size MeasureOverride(Size available)
    {
        int n = InternalChildren.Count;
        if (n == 0) return default;
        if (Stacks(available.Width))
        {
            double height = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(available.Width, double.PositiveInfinity));
                height += child.DesiredSize.Height;
            }
            return new Size(available.Width, height + Gap * (n - 1));
        }
        double column = double.IsInfinity(available.Width) ? double.PositiveInfinity : ColumnWidth(available.Width);
        double tallest = 0, widths = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(column, double.PositiveInfinity));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
            widths += child.DesiredSize.Width;
        }
        return new Size(double.IsInfinity(available.Width) ? widths + Gap * (n - 1) : available.Width, tallest);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int n = InternalChildren.Count;
        if (n == 0) return final;
        if (Stacks(final.Width))
        {
            double y = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(0, y, final.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height + Gap;
            }
            return final;
        }
        double column = ColumnWidth(final.Width);
        for (int i = 0; i < n; i++)
            InternalChildren[i].Arrange(new Rect(i * (column + Gap), 0, column, final.Height));
        return final;
    }
}

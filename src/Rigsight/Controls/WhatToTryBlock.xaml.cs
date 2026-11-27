using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>
/// What to try under a finding: see the XAML. Pages don't place it directly: a ContentControl holds the finding's
/// TryBlockView and the template in Theme.xaml makes this block only when there is one, so a page with many rows and
/// nothing to try builds none (the gate, 2026-11-26: a block in every drive row made switching to Storage slower;
/// building the block's insides late, on its DataContext, set the window's thread spinning on Temperatures).
/// </summary>
public partial class WhatToTryBlock : UserControl
{
    public WhatToTryBlock() => InitializeComponent();

    /// <summary>The question opens under its button with their right edges in line, whatever the button's width.</summary>
    private void Question_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Popup pop)
            pop.CustomPopupPlacementCallback = (popup, target, _) => [new CustomPopupPlacement(new Point(target.Width - popup.Width + 8, target.Height - 2), PopupPrimaryAxis.Horizontal)];
    }

    /// <summary>Focus starts on the box itself, so Enter presses nothing and Esc closes it.</summary>
    private void Question_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: Border box }) box.Focus();
    }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (sender as FrameworkElement)?.DataContext is not TryStepView step) return;
        step.CancelCommand.Execute(null);
        e.Handled = true;
    }
}

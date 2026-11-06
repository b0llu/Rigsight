using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.Core.Settings;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class OverlayView : UserControl
{
    public OverlayView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        // Clicking elsewhere (or leaving the window) stops recording.
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false) (DataContext as OverlayViewModel)?.CancelRecording();
        };
    }

    /// <summary>Two columns from this page width: the left one keeps room for the longest row of reading chips.</summary>
    private const double WideFrom = 1180, RightWidth = 620;

    private bool _wide;

    private void Columns_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = Controls.PageColumns.Arrange(Columns, LeftColumn, RightColumn, RightWidth, WideFrom);
        if (wide != _wide || e.PreviousSize.Width == 0)
        {
            _wide = wide;
            // Two columns: the Look card beside shows the overlay, so the shortcut card doesn't, and the position
            // preview takes the Look card's width with its settings under it. One column: as before.
            ShortcutPreview.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            ShortcutGap.Width = new GridLength(wide ? 0 : 24);
            ShortcutPreviewColumn.Width = wide ? new GridLength(0) : new GridLength(1.2, GridUnitType.Star);
            PositionLabel.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            PositionScreen.Width = wide ? double.NaN : 440;
            Grid.SetColumn(PositionScreen, wide ? 0 : 1);
            Grid.SetColumnSpan(PositionScreen, wide ? 3 : 1);
            Grid.SetRow(PositionSide, wide ? 1 : 0);
            Grid.SetColumn(PositionSide, wide ? 0 : 2);
            Grid.SetColumnSpan(PositionSide, wide ? 3 : 1);
            PositionSide.Margin = wide ? new Thickness(0, 16, 0, 0) : new Thickness(16, 0, 0, 0);
        }
        Controls.PageColumns.Follow(PageScroll, Columns, LeftColumn, RightColumn, RightShift, _wide);
    }

    private void PageScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        Controls.PageColumns.Follow(PageScroll, Columns, LeftColumn, RightColumn, RightShift, _wide);

    /// <summary>Keys are read by the page itself, so it takes focus while recording.</summary>
    private void OnRecordClick(object sender, RoutedEventArgs e) => Focus();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not OverlayViewModel { IsRecording: true } vm) return;
        e.Handled = true;

        // With Alt held, WPF reports the key as "System".
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            vm.CancelRecording();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None)
            return;

        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        vm.Record(KeyInterop.VirtualKeyFromKey(key), mods);
    }
}

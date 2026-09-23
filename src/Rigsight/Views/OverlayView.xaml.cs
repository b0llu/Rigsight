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

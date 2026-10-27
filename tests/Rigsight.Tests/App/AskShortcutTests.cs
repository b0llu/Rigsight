using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Settings › Ask RigSight › Shortcut: Set listens for the next keys, takes a key with Ctrl, Alt or Shift, never the
/// overlay's, Esc cancels, Clear removes it (2026-10-26: the row's Set button did nothing).
/// </summary>
public sealed class AskShortcutTests
{
    private const int KeyA = 0x41, KeyO = 0x4F, KeyUnused = 0x07;

    private static (AiSettingsViewModel Vm, Services.SettingsModel Settings) Make()
    {
        var settings = Kit.OfflineSettings(new RigsightSettings());
        return (new AiSettingsViewModel(settings, new AskViewModel(settings)), settings);
    }

    [Fact]
    public void Set_listens_and_takes_a_key_with_ctrl_alt_or_shift()
    {
        var (vm, settings) = Make();
        // Ctrl+K out of the box (2026-10-27); Set replaces it.
        Assert.True(vm.HasShortcut);
        Assert.Equal("Ctrl+K", settings.Current.Ai.Shortcut);
        vm.SetShortcutCommand.Execute(null);
        Assert.True(vm.IsRecordingShortcut);
        Assert.Equal("Press the new shortcut…", vm.ShortcutText);

        vm.RecordShortcut(KeyA, HotkeyModifiers.None);
        Assert.True(vm.IsRecordingShortcut);
        Assert.StartsWith("Add Ctrl, Alt or Shift", vm.ShortcutHint);

        vm.RecordShortcut(KeyA, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift);
        Assert.False(vm.IsRecordingShortcut);
        Assert.Equal(new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, KeyA).ToString(), settings.Current.Ai.Shortcut);
        Assert.Equal(settings.Current.Ai.Shortcut, vm.ShortcutText);
        Assert.Empty(vm.ShortcutHint);
    }

    [Fact]
    public void Never_the_overlays_shortcut_and_esc_or_clear_leave_none()
    {
        var (vm, settings) = Make();
        settings.Update(s => { s.Overlay.Enabled = true; s.Overlay.Hotkey = new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, KeyO).ToString(); });
        vm.SetShortcutCommand.Execute(null);
        vm.RecordShortcut(KeyO, HotkeyModifiers.Alt | HotkeyModifiers.Shift);
        Assert.True(vm.IsRecordingShortcut);
        Assert.Equal("That's the overlay's shortcut. Pick another.", vm.ShortcutHint);
        Assert.Equal("Ctrl+K", settings.Current.Ai.Shortcut);

        // Esc keeps the one it had.
        vm.CancelShortcut();
        Assert.False(vm.IsRecordingShortcut);
        Assert.Equal("Ctrl+K", vm.ShortcutText);

        vm.SetShortcutCommand.Execute(null);
        vm.RecordShortcut(KeyUnused, HotkeyModifiers.Ctrl);
        Assert.StartsWith("That key can't be used", vm.ShortcutHint);
        vm.RecordShortcut(KeyA, HotkeyModifiers.Alt);
        Assert.True(vm.HasShortcut);
        // Another program holds it: said, until it's changed or cleared.
        vm.ShortcutTaken = true;
        Assert.Contains("Another program already uses", vm.ShortcutWarning);
        vm.ClearShortcutCommand.Execute(null);
        Assert.False(vm.HasShortcut);
        Assert.Equal("", settings.Current.Ai.Shortcut);
        Assert.Empty(vm.ShortcutWarning);
        // Cleared on purpose: the next start doesn't hand Ctrl+K back.
        Assert.True(settings.Current.Ai.ShortcutGiven);
    }
}

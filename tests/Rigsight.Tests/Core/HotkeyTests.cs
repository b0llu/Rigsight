using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

public sealed class HotkeyTests
{
    private const HotkeyModifiers Ctrl = HotkeyModifiers.Ctrl, Alt = HotkeyModifiers.Alt, Shift = HotkeyModifiers.Shift, Win = HotkeyModifiers.Win;

    /// <summary>Every key a shortcut can use, by virtual-key code, with its name in settings.</summary>
    public static TheoryData<int, string> SupportedKeys()
    {
        var data = new TheoryData<int, string>();
        for (int c = 'A'; c <= 'Z'; c++) data.Add(c, ((char)c).ToString());
        for (int c = '0'; c <= '9'; c++) data.Add(c, ((char)c).ToString());
        for (int f = 1; f <= 24; f++) data.Add(0x6F + f, $"F{f}");
        for (int n = 0; n <= 9; n++) data.Add(0x60 + n, $"Num{n}");
        data.Add(0x20, "Space");
        data.Add(0x21, "PageUp");
        data.Add(0x22, "PageDown");
        data.Add(0x23, "End");
        data.Add(0x24, "Home");
        data.Add(0x25, "Left");
        data.Add(0x26, "Up");
        data.Add(0x27, "Right");
        data.Add(0x28, "Down");
        data.Add(0x2D, "Insert");
        data.Add(0x2E, "Delete");
        data.Add(0x13, "Pause");
        data.Add(0x91, "ScrollLock");
        data.Add(0xC0, "`");
        return data;
    }

    [Theory]
    [MemberData(nameof(SupportedKeys))]
    public void Every_supported_key_round_trips_with_a_modifier(int key, string name)
    {
        Assert.True(Hotkey.IsSupportedKey(key));
        var hotkey = new Hotkey(Alt | Shift, key);
        Assert.True(hotkey.IsValid);
        Assert.Equal($"Alt+Shift+{name}", hotkey.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var parsed));
        Assert.Equal(hotkey, parsed);
    }

    [Theory]
    [MemberData(nameof(SupportedKeys))]
    public void Key_names_are_read_in_any_case(int key, string name)
    {
        Assert.True(Hotkey.TryParse($"ctrl+{name.ToLowerInvariant()}", out var lower));
        Assert.True(Hotkey.TryParse($"CTRL+{name.ToUpperInvariant()}", out var upper));
        Assert.Equal(new Hotkey(Ctrl, key), lower);
        Assert.Equal(new Hotkey(Ctrl, key), upper);
    }

    [Theory]
    [MemberData(nameof(SupportedKeys))]
    public void Only_function_keys_Pause_ScrollLock_and_Insert_work_alone(int key, string name)
    {
        bool alone = key is >= 0x70 and <= 0x87 or 0x13 or 0x91 or 0x2D;
        Assert.Equal(alone, new Hotkey(HotkeyModifiers.None, key).IsValid);
        Assert.Equal(alone, Hotkey.TryParse(name, out _));
    }

    public static TheoryData<HotkeyModifiers> AllModifierSets()
    {
        var data = new TheoryData<HotkeyModifiers>();
        for (int m = 1; m < 16; m++) data.Add((HotkeyModifiers)m);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllModifierSets))]
    public void Every_modifier_combination_round_trips(HotkeyModifiers mods)
    {
        var hotkey = new Hotkey(mods, 'K');
        Assert.True(hotkey.IsValid);
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var parsed));
        Assert.Equal(hotkey, parsed);
    }

    [Fact]
    public void Modifiers_are_always_written_Ctrl_Alt_Shift_Win()
    {
        Assert.Equal("Ctrl+Alt+Shift+Win+K", new Hotkey(Win | Shift | Alt | Ctrl, 'K').ToString());
        Assert.True(Hotkey.TryParse("Win+Shift+Alt+Ctrl+K", out var h));
        Assert.Equal("Ctrl+Alt+Shift+Win+K", h.ToString());
    }

    [Theory]
    [InlineData("Alt+Shift+O", Alt | Shift, (int)'O')]
    [InlineData("shift+alt+o", Alt | Shift, (int)'O')]
    [InlineData("Control+X", Ctrl, (int)'X')]
    [InlineData("control+x", Ctrl, (int)'X')]
    [InlineData("CONTROL+ALT+DELETE", Ctrl | Alt, 0x2E)]
    [InlineData(" Ctrl + X ", Ctrl, (int)'X')]
    [InlineData("Ctrl++X", Ctrl, (int)'X')]
    [InlineData("+Ctrl+X+", Ctrl, (int)'X')]
    [InlineData("Ctrl+Ctrl+X", Ctrl, (int)'X')]
    [InlineData("X+Ctrl", Ctrl, (int)'X')]
    [InlineData("Win+`", Win, 0xC0)]
    [InlineData("Alt+Num5", Alt, 0x65)]
    [InlineData("Ctrl+5", Ctrl, (int)'5')]
    [InlineData("F12", HotkeyModifiers.None, 0x7B)]
    [InlineData("f24", HotkeyModifiers.None, 0x87)]
    [InlineData("Pause", HotkeyModifiers.None, 0x13)]
    [InlineData("scrolllock", HotkeyModifiers.None, 0x91)]
    [InlineData("Insert", HotkeyModifiers.None, 0x2D)]
    [InlineData("Shift+F1", Shift, 0x70)]
    [InlineData("Alt+F4", Alt, 0x73)]
    public void Parses(string text, HotkeyModifiers mods, int key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(mods, key), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("++")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Alt+Shift+Win")]
    [InlineData("A")]
    [InlineData("1")]
    [InlineData("Space")]
    [InlineData("Num0")]
    [InlineData("Delete")]
    [InlineData("`")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+F1+F2")]
    [InlineData("F1+F2")]
    [InlineData("Ctrl+A+A")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+Num10")]
    [InlineData("Fn+A")]
    [InlineData("Cmd+A")]
    [InlineData("Ctrl+?")]
    [InlineData("Ctrl-A")]
    [InlineData("Ctrl A")]
    [InlineData("Ctrl+ä")]
    public void Rejects(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Nothing_parsed_leaves_the_default()
    {
        Assert.False(Hotkey.TryParse("", out var h));
        Assert.Equal(default, h);
        Assert.False(default(Hotkey).IsValid);
    }

    [Theory]
    [InlineData(0x1B)] // Esc
    [InlineData(0x09)] // Tab
    [InlineData(0x0D)] // Enter
    [InlineData(0x10)] // Shift itself
    [InlineData(0x5B)] // Win key itself
    [InlineData(0x88)] // after F24
    [InlineData(0x6A)] // numpad *
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0x10000)]
    public void Unsupported_keys_are_never_valid(int key)
    {
        Assert.False(Hotkey.IsSupportedKey(key));
        Assert.False(new Hotkey(Ctrl | Alt, key).IsValid);
        Assert.EndsWith("+?", new Hotkey(Ctrl, key).ToString());
    }

    [Fact]
    public void The_overlay_default_is_valid() =>
        Assert.True(Hotkey.TryParse(OverlaySettings.DefaultHotkey, out var h) && h == new Hotkey(Alt | Shift, 'O'));
}

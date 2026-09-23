namespace Rigsight.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    // Same values as RegisterHotKey's MOD_* flags.
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global keyboard shortcut such as "Alt+Shift+O", stored as text in settings.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    // Keys that can be part of a shortcut, by Windows virtual-key code.
    private static readonly Dictionary<int, string> Names = BuildNames();

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>();
        for (int c = 'A'; c <= 'Z'; c++) names[c] = ((char)c).ToString();
        for (int c = '0'; c <= '9'; c++) names[c] = ((char)c).ToString();
        for (int f = 1; f <= 24; f++) names[0x6F + f] = $"F{f}";
        for (int n = 0; n <= 9; n++) names[0x60 + n] = $"Num{n}";
        names[0x20] = "Space";
        names[0x21] = "PageUp";
        names[0x22] = "PageDown";
        names[0x23] = "End";
        names[0x24] = "Home";
        names[0x25] = "Left";
        names[0x26] = "Up";
        names[0x27] = "Right";
        names[0x28] = "Down";
        names[0x2D] = "Insert";
        names[0x2E] = "Delete";
        names[0x13] = "Pause";
        names[0x91] = "ScrollLock";
        names[0xC0] = "`";
        return names;
    }

    public static bool IsSupportedKey(int key) => Names.ContainsKey(key);

    /// <summary>A shortcut needs a modifier, except for keys games rarely use on their own (F-keys, Pause…).</summary>
    public bool IsValid => Names.ContainsKey(Key) &&
        (Modifiers != HotkeyModifiers.None || Key is >= 0x70 and <= 0x87 || Key is 0x13 or 0x91 or 0x2D);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Names.GetValueOrDefault(Key, "?"));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win": mods |= HotkeyModifiers.Win; break;
                default:
                    var match = Names.FirstOrDefault(kv => kv.Value.Equals(raw, StringComparison.OrdinalIgnoreCase));
                    if (match.Value is null || key is not null) return false;
                    key = match.Key;
                    break;
            }
        }
        if (key is null) return false;
        hotkey = new Hotkey(mods, key.Value);
        return hotkey.IsValid;
    }
}

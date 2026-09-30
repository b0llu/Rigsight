using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Rigsight.Services;

/// <summary>
/// The app's accent (selected page bar, buttons, switches, picked dates): black and white by default, a colour the
/// user picks in Settings, or Windows' own accent. Each colour has a lighter shade for the dark themes and a deeper
/// one for Light, so it reads on both.
/// </summary>
public static class Accents
{
    public const string Mono = "mono", Windows = "windows";

    /// <param name="Key">What settings.json stores.</param>
    public sealed record Accent(string Key, string Name, Color OnDark, Color OnLight);

    public static readonly IReadOnlyList<Accent> All =
    [
        new(Mono, "Black and white", Colors.White, Colors.Black),
        new("blue", "Blue", Hex("#60A5FA"), Hex("#2563EB")),
        new("teal", "Teal", Hex("#2DD4BF"), Hex("#0D9488")),
        new("green", "Green", Hex("#4ADE80"), Hex("#16A34A")),
        new("purple", "Purple", Hex("#A78BFA"), Hex("#7C3AED")),
        new("pink", "Pink", Hex("#F472B6"), Hex("#DB2777")),
        new("red", "Red", Hex("#F87171"), Hex("#DC2626")),
        new("orange", "Orange", Hex("#FB923C"), Hex("#EA580C")),
        new("yellow", "Yellow", Hex("#FACC15"), Hex("#CA8A04")),
    ];

    public static string Normalize(string? key) =>
        key == Windows || All.Any(a => a.Key == key) ? key! : Mono;

    /// <summary>The colour for this accent on a light or dark page, or null for the palette's own black/white.</summary>
    public static Color? ColorFor(string? key, bool light)
    {
        key = Normalize(key);
        if (key == Mono) return null;
        if (key == Windows) return WindowsAccent(light);
        var accent = All.First(a => a.Key == key);
        return light ? accent.OnLight : accent.OnDark;
    }

    /// <summary>
    /// Windows' accent in the shade Windows itself uses for this mode (lighter on dark, deeper on light), from its
    /// accent palette; null if it can't be read.
    /// </summary>
    public static Color? WindowsAccent(bool light)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            // Eight RGBA colours: light 3, 2, 1, the accent, dark 1, 2, 3, and one more.
            if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
            {
                int i = light ? 4 : 1;
                return Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Black text on a bright accent, white on a deep one.</summary>
    public static Color OnAccent(Color c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B) > 0.3 ? Colors.Black : Colors.White;

    /// <summary>Puts the accent into a freshly loaded palette (before it's merged in), keeping each brush's opacity.</summary>
    public static void ApplyTo(ResourceDictionary palette, Color accent)
    {
        var on = OnAccent(accent);
        palette["AccentColor"] = accent;
        palette["OnAccentColor"] = on;
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorLight1",
                     "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            palette[key] = accent;
        foreach (var key in new[] { "AccentBrush", "AccentSoftBrush", "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush",
                     "AccentFillColorTertiaryBrush", "AccentFillColorSelectedTextBackgroundBrush", "AccentTextFillColorPrimaryBrush",
                     "AccentTextFillColorSecondaryBrush" })
            palette[key] = Brush(accent, (palette[key] as SolidColorBrush)?.Opacity ?? 1);
        foreach (var key in new[] { "OnAccentBrush", "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush",
                     "TextOnAccentFillColorSelectedTextBrush" })
            palette[key] = Brush(on, 1);
    }

    private static SolidColorBrush Brush(Color c, double opacity)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static double Linear(byte v)
    {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

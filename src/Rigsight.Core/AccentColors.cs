using Microsoft.Win32;

namespace Rigsight.Core;

/// <summary>
/// The app's accent colours (Settings → the accent), in one place for the app (Services.Accents, its buttons and bars)
/// and the agent (its reading cards' buttons): the theme's own black and white, a named colour with a lighter shade for
/// the dark themes and a deeper one for Light, or Windows' own accent.
/// </summary>
public static class AccentColors
{
    public const string Mono = "mono", Windows = "windows";

    /// <summary>Each named accent: what settings.json stores, its name (a language key), and its two shades.</summary>
    public static readonly IReadOnlyList<(string Key, string Name, string OnDark, string OnLight)> Named =
    [
        ("blue", Lang.Key("Blue"), "#66A9FA", "#215BD7"),
        ("teal", Lang.Key("Teal"), "#2DD4BF", "#0F7169"),
        ("green", Lang.Key("Green"), "#4ADE80", "#137437"),
        ("purple", Lang.Key("Purple"), "#B198FB", "#7838E6"),
        ("pink", Lang.Key("Pink"), "#F580BD", "#BE185D"),
        ("red", Lang.Key("Red"), "#F98484", "#B91C1C"),
        ("orange", Lang.Key("Orange"), "#FB923C", "#B33C0B"),
        ("yellow", Lang.Key("Yellow"), "#FACC15", "#8E5706"),
    ];

    /// <summary>A known accent's key, else <see cref="Mono"/>.</summary>
    public static string Normalize(string? key) => key == Windows || Named.Any(a => a.Key == key) ? key! : Mono;

    /// <summary>The accent's colour on a light or dark page; null for the theme's own black and white.</summary>
    public static (byte R, byte G, byte B)? Of(string? key, bool light)
    {
        key = Normalize(key);
        if (key == Mono) return null;
        if (key == Windows) return WindowsAccent(light);
        var a = Named.First(n => n.Key == key);
        return Hex(light ? a.OnLight : a.OnDark);
    }

    /// <summary>
    /// Windows' accent in the shade Windows itself uses for this mode (lighter on dark, deeper on light), from its accent
    /// palette; null if it can't be read.
    /// </summary>
    public static (byte R, byte G, byte B)? WindowsAccent(bool light)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            // Eight RGBA colours: light 3, 2, 1, the accent, dark 1, 2, 3, and one more.
            if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
            {
                int i = light ? 4 : 1;
                return (p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Whether text on the accent is black (a bright accent) or white (a deep one).</summary>
    public static bool DarkTextOn(byte r, byte g, byte b) => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b) > 0.3;

    private static double Linear(byte c)
    {
        double v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static (byte R, byte G, byte B) Hex(string hex) =>
        (Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
}

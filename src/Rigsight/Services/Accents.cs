using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Rigsight.Core;

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
        new(Mono, Lang.Key("The theme's own"), Colors.White, Colors.Black),
        new("blue", Lang.Key("Blue"), Hex("#60A5FA"), Hex("#2563EB")),
        new("teal", Lang.Key("Teal"), Hex("#2DD4BF"), Hex("#0D9488")),
        new("green", Lang.Key("Green"), Hex("#4ADE80"), Hex("#16A34A")),
        new("purple", Lang.Key("Purple"), Hex("#A78BFA"), Hex("#7C3AED")),
        new("pink", Lang.Key("Pink"), Hex("#F472B6"), Hex("#DB2777")),
        new("red", Lang.Key("Red"), Hex("#F87171"), Hex("#DC2626")),
        new("orange", Lang.Key("Orange"), Hex("#FB923C"), Hex("#EA580C")),
        new("yellow", Lang.Key("Yellow"), Hex("#FACC15"), Hex("#CA8A04")),
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
        foreach (var key in new[] { "AccentBrush", "AccentSoftBrush", "RamBrush", "RamSoftBrush", "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush",
                     "AccentFillColorTertiaryBrush", "AccentFillColorSelectedTextBackgroundBrush", "AccentTextFillColorPrimaryBrush",
                     "AccentTextFillColorSecondaryBrush" })
            palette[key] = Brush(accent, (palette[key] as SolidColorBrush)?.Opacity ?? 1);
        foreach (var key in new[] { "OnAccentBrush", "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush",
                     "TextOnAccentFillColorSelectedTextBrush" })
            palette[key] = Brush(on, 1);
    }

    /// <summary>
    /// Windows' own (Fluent) dropdowns and right-click menus in the theme's colours: left alone they are a neutral
    /// grey with a grey mark, which on Ember, Forest or Midnight (or under a picked accent) read as boxes from another
    /// app. The box and an open list are the tile shade, the row under the pointer the hover shade, the mark beside
    /// the picked row the accent. For every palette, after any accent was put in.
    /// </summary>
    public static void Popups(ResourceDictionary palette)
    {
        Color tile = (Color)palette["Surface2Color"], hover = (Color)palette["HoverColor"], stroke = (Color)palette["StrokeColor"];
        var accent = (Color)palette["AccentColor"];
        foreach (var key in new[] { "ComboBoxBackground", "ComboBoxDropDownBackground", "ContextMenuBackground", "FlyoutBackground" })
            palette[key] = Brush(tile, 1);
        foreach (var key in new[] { "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "ComboBoxItemBackgroundSelected", "SubtleFillColorSecondaryBrush" })
            palette[key] = Brush(hover, 1);
        palette["SubtleFillColorTertiaryBrush"] = Brush(Mix(hover, tile, 0.5), 1);
        foreach (var key in new[] { "ComboBoxDropDownBorderBrush", "ContextMenuBorderBrush", "FlyoutBorderBrush" })
            palette[key] = Brush(stroke, 1);
        palette["ComboBoxItemPillFillBrush"] = Brush(accent, 1);
    }

    /// <summary>
    /// A picked accent takes over the whole palette: on top of <see cref="ApplyTo"/>, every colour that says which
    /// part or kind something is (CPU, GPU, memory, data, the internet) becomes a shade of the accent, lighter and
    /// deeper ones so lines on one chart still differ, and the picked segment is the accent itself. Green, amber and
    /// red stay in the palette: they are what warnings are drawn in (readings have colours of their own, see
    /// <see cref="ReadingShades"/>).
    /// </summary>
    /// <param name="goodToo">Green ("all is well": a fan card's All OK, a healthy drive) becomes the accent as well.
    /// Amber, orange and red never do: a warning has to stand out from everything else.</param>
    public static void TakeOver(ResourceDictionary palette, Color accent, bool goodToo)
    {
        ApplyTo(palette, accent);
        if (goodToo) Set(palette, "Good", accent);
        var text = (Color)palette["TextColor"];
        var bg = (Color)palette["BgColor"];
        Color lighter = Mix(accent, text, 0.58), deeper = Mix(accent, bg, 0.55), palest = Mix(accent, text, 0.28);
        Set(palette, "Cpu", accent);
        Set(palette, "Gpu", lighter);
        Set(palette, "Purple", deeper);
        Set(palette, "Pink", palest);
        Set(palette, "Sky", accent);
        palette["SegmentOnBrush"] = Brush(accent, 1);
        palette["SegmentOnTextBrush"] = Brush(OnAccent(accent), 1);
        palette["ListBarBrush"] = Brush(accent, 1);
    }

    /// <summary>
    /// The Games page's colours, all from one: the frame rate's own blue, or the picked accent. The Play button and the
    /// FPS lines are drawn in it; the update markers on the chart and the change boxes between plays are deep shades of
    /// it with pale text, so they turn with the accent too (they were written in blue and stayed blue).
    /// </summary>
    public static void Fps(ResourceDictionary palette, Color fps)
    {
        var text = (Color)palette["TextColor"];
        var bg = (Color)palette["BgColor"];
        void Put(string name, Color c)
        {
            palette[name + "Color"] = c;
            palette[name + "Brush"] = Brush(c, 1);
        }
        Put("Fps", fps);
        palette["FpsSoftBrush"] = Brush(fps, 0.10);
        palette["OnFpsBrush"] = Brush(OnAccent(fps), 1);
        Put("FpsMark", Mix(fps, bg, 0.18));
        Put("FpsMarkStroke", Mix(fps, bg, 0.33));
        Put("FpsMarkText", Mix(fps, text, 0.60));
        Put("FpsNote", Mix(fps, bg, 0.09));
        Put("FpsNoteStroke", Mix(fps, bg, 0.20));
        Put("FpsNoteTile", Mix(fps, bg, 0.22));
        Put("FpsNoteIcon", Mix(fps, text, 0.80));
        Put("FpsNoteText", Mix(fps, text, 0.40));
    }

    /// <summary>
    /// The four colours a reading is drawn in by how it is doing (low, fine, warm, hot) once they follow the accent:
    /// shades of it, in place of blue, green, amber and red. Warnings never use these: they keep their own colours.
    /// </summary>
    public static (Color Cool, Color Good, Color Warm, Color Hot) ReadingShades(Color accent, Color text, Color bg) =>
        (Mix(accent, bg, 0.55), accent, Mix(accent, text, 0.58), Mix(accent, text, 0.28));

    /// <summary>One named colour of a palette with its brush and, where it has one, its soft brush.</summary>
    private static void Set(ResourceDictionary palette, string name, Color color)
    {
        palette[name + "Color"] = color;
        palette[name + "Brush"] = Brush(color, 1);
        if (palette[name + "SoftBrush"] is SolidColorBrush soft) palette[name + "SoftBrush"] = Brush(color, soft.Opacity);
    }

    /// <summary>A colour between two: <paramref name="share"/> of the first, the rest of the second.</summary>
    public static Color Mix(Color a, Color b, double share) => Color.FromRgb(
        (byte)Math.Round(a.R * share + b.R * (1 - share)), (byte)Math.Round(a.G * share + b.G * (1 - share)),
        (byte)Math.Round(a.B * share + b.B * (1 - share)));

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

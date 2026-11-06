namespace Rigsight.Core.Settings;

/// <summary>
/// Every theme the app has, in one place for the app (its palette files, Settings' groups), the settings check and the
/// agent (its menus and cards go light with a light theme). Three groups (the user, 2026-11-05: "5 for dark, 5 for light, and 5 color full themes", then "do 10 of each");
/// a new theme is a palette file in Themes plus one line here.
/// </summary>
public static class ThemeCatalog
{
    /// <summary>Which group a theme sits in. Colorful themes are dark ones with a coloured page and accent.</summary>
    public enum Group { Dark, Light, Colorful }

    /// <param name="Key">What settings.json stores ("midnight").</param>
    /// <param name="File">Its palette file in Themes ("Midnight" for Themes/Midnight.xaml).</param>
    /// <param name="Name">Its name as Settings shows it (a language key).</param>
    public sealed record Theme(string Key, string File, string Name, Group Group)
    {
        public bool IsLight => Group == Group.Light;
    }

    /// <summary>Following Windows: its dark mode shows <see cref="RigsightSettings.DarkTheme"/>, its light mode <see cref="RigsightSettings.LightTheme"/>.</summary>
    public const string System = "system";

    public const string DefaultDark = "dark", DefaultLight = "light";

    /// <summary>Every theme, in the order Settings shows each group (ten each since 2026-11-05).</summary>
    public static readonly IReadOnlyList<Theme> All =
    [
        new("dark", "Dark", Lang.Key("Dark"), Group.Dark),
        new("grey", "Grey", Lang.Key("Gray"), Group.Dark),
        new("carbon", "Carbon", Lang.Key("Carbon"), Group.Dark),
        new("graphite", "Graphite", Lang.Key("Graphite"), Group.Dark),
        new("slate", "Slate", Lang.Key("Slate"), Group.Dark),
        new("ink", "Ink", Lang.Key("Ink"), Group.Dark),
        new("coal", "Coal", Lang.Key("Coal"), Group.Dark),
        new("stone", "Stone", Lang.Key("Stone"), Group.Dark),
        new("dim", "Dim", Lang.Key("Dim"), Group.Dark),
        new("contrast", "Contrast", Lang.Key("Contrast"), Group.Dark),
        new("light", "Light", Lang.Key("Light"), Group.Light),
        new("soft", "Soft", Lang.Key("Soft"), Group.Light),
        new("cloud", "Cloud", Lang.Key("Cloud"), Group.Light),
        new("mist", "Mist", Lang.Key("Mist"), Group.Light),
        new("paper", "Paper", Lang.Key("Paper"), Group.Light),
        new("sand", "Sand", Lang.Key("Sand"), Group.Light),
        new("sage", "Sage", Lang.Key("Sage"), Group.Light),
        new("blush", "Blush", Lang.Key("Blush"), Group.Light),
        new("lavender", "Lavender", Lang.Key("Lavender"), Group.Light),
        new("bright", "Bright", Lang.Key("Bright"), Group.Light),
        new("midnight", "Midnight", Lang.Key("Midnight"), Group.Colorful),
        new("indigo", "Indigo", Lang.Key("Indigo"), Group.Colorful),
        new("ocean", "Ocean", Lang.Key("Ocean"), Group.Colorful),
        new("forest", "Forest", Lang.Key("Forest"), Group.Colorful),
        new("lime", "Lime", Lang.Key("Lime"), Group.Colorful),
        new("ember", "Ember", Lang.Key("Ember"), Group.Colorful),
        new("mocha", "Mocha", Lang.Key("Mocha"), Group.Colorful),
        new("rose", "Rose", Lang.Key("Rose"), Group.Colorful),
        new("plum", "Plum", Lang.Key("Plum"), Group.Colorful),
        new("neon", "Neon", Lang.Key("Neon"), Group.Colorful),
    ];

    /// <summary>The themes of a group, in order.</summary>
    public static IEnumerable<Theme> In(Group group) => All.Where(t => t.Group == group);

    public static Theme? Find(string? key) => All.FirstOrDefault(t => t.Key == key);

    /// <summary>A theme's key or "system" (the app always writes lower case).</summary>
    public static bool IsKnown(string? key) => key == System || Find(key) is not null;

    /// <summary>A light theme by its key; false for "system" (that one depends on Windows).</summary>
    public static bool IsLight(string? key) => Find(key)?.IsLight == true;

    /// <summary>A theme that may stand in for Windows' dark mode: a dark or a colorful one.</summary>
    public static bool IsDarkKind(string? key) => Find(key) is { IsLight: false };
}

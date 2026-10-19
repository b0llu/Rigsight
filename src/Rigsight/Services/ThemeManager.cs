using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Rigsight.Controls;
using Rigsight.Core.Apps;
using Rigsight.Core.Settings;

namespace Rigsight.Services;

/// <summary>
/// The app's theme: swaps the palette dictionary (Themes/Dark.xaml, Grey.xaml, Midnight.xaml, Forest.xaml, Ember.xaml
/// or Light.xaml), puts a picked accent into it, switches the Fluent controls to match, and reloads the colors the
/// custom-drawn charts use.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark", Grey = "grey", Midnight = "midnight", Forest = "forest", Ember = "ember", Light = "light", System = "system";

    /// <summary>The themes with a palette file of their own: what settings.json calls each, and its file's name.</summary>
    private static readonly Dictionary<string, string> Palettes = new()
    {
        [Dark] = "Dark", [Grey] = "Grey", [Midnight] = "Midnight", [Forest] = "Forest", [Ember] = "Ember", [Light] = "Light",
    };

    private static string _mode = Dark;
    private static string _accent = Accents.Mono;
    private static string? _palette;
    private static Color? _accentColor;
    private static bool _keepStatus = true;
    private static bool _listening;
    private static readonly Dictionary<AppCategory, string> Categories = [];

    public static bool IsLight { get; private set; }

    public static bool IsGrey => _palette == "Grey";

    /// <summary>An accent is picked, so every part's colour is a shade of it (see <see cref="Accents.TakeOver"/>).</summary>
    public static bool AccentTakesOver => _accentColor is not null;

    /// <summary>
    /// The accent, while the colours of a reading (a temperature, how full memory is) follow it; null while they are
    /// blue, green, amber and red. Warnings are never part of this: they keep their colours whatever is picked.
    /// </summary>
    public static Color? ReadingAccent => _keepStatus ? null : _accentColor;

    /// <summary>The accent the theme itself comes with (white on Dark, blue on Midnight…), whatever accent is picked.</summary>
    public static Color OwnAccent { get; private set; } = Colors.White;

    /// <summary>Goes up on every change, so anything that caches colors knows to make them again.</summary>
    public static int Version { get; private set; }

    /// <summary>Raised after the theme changed (not on the first apply).</summary>
    public static event Action? Changed;

    /// <param name="mode">A theme's name ("dark", "grey", "midnight", "forest", "ember", "light") or "system" (follow Windows' app mode).</param>
    /// <param name="accent">A key from <see cref="Accents.All"/>, or "windows" (follow Windows' accent).</param>
    /// <param name="keepStatus">With an accent picked: readings stay blue, green, amber and red by how they are doing (warnings always do).</param>
    public static void Apply(string? mode, string? accent = Accents.Mono, bool keepStatus = true)
    {
        _mode = mode is not null && (mode == System || Palettes.ContainsKey(mode)) ? mode : Dark;
        _accent = Accents.Normalize(accent);
        if ((_mode == System || _accent == Accents.Windows) && !_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        bool light = _mode == Light || (_mode == System && WindowsUsesLight());
        string name = _mode == System ? (light ? "Light" : "Dark") : Palettes[_mode];
        var accentColor = Accents.ColorFor(_accent, light);
        if (name == _palette && accentColor == _accentColor && keepStatus == _keepStatus) return;

        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;
        int index = dictionaries.ToList().FindIndex(d => d.Source?.OriginalString is { } s &&
            Palettes.Values.Any(p => s.EndsWith($"Themes/{p}.xaml", StringComparison.OrdinalIgnoreCase)));
        // By its full address, so it's found from any application (the tests host the app's windows in their own).
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Rigsight;component/Themes/{name}.xaml") };
        var own = (Color)palette["AccentColor"];
        if (accentColor is { } color) Accents.TakeOver(palette, color, goodToo: !keepStatus);
        // A theme with a coloured accent of its own: the palette's keys for the Fluent controls are written in black and white.
        else if (own != Colors.White && own != Colors.Black) Accents.ApplyTo(palette, own);
        Accents.Popups(palette);
        // The frame rate keeps its own blue (as parts keep theirs) until an accent is picked, then it is the accent.
        Accents.Fps(palette, accentColor ?? (light ? Color.FromRgb(0x25, 0x6F, 0xE8) : Color.FromRgb(0x4C, 0x9A, 0xFF)));
        if (index >= 0) dictionaries[index] = palette;
        else dictionaries.Insert(0, palette);

        app.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
        // The Fluent dictionary is merged in by ThemeMode; keep ours after it so the monochrome accent wins.
        var fluent = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Fluent", StringComparison.OrdinalIgnoreCase) == true);
        if (fluent is not null && dictionaries.IndexOf(fluent) > dictionaries.IndexOf(palette))
        {
            dictionaries.Remove(fluent);
            dictionaries.Insert(0, fluent);
        }

        bool first = Version == 0;
        IsLight = light;
        _palette = name;
        _accentColor = accentColor;
        _keepStatus = keepStatus;
        OwnAccent = own;
        Categories.Clear();
        Version++;
        ChartPaint.Load();
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        if (!first) Changed?.Invoke();
    }

    /// <summary>
    /// An app category's colour as "#RRGGBB": its own (see <see cref="AppCatalog.Color"/>), or a shade of the accent
    /// once one is picked, lighter and deeper ones so the categories still differ. System and Other stay grey either way.
    /// </summary>
    public static string CategoryHex(AppCategory category)
    {
        if (_accentColor is not { } accent || category is AppCategory.System or AppCategory.Other) return AppCatalog.Color(category);
        if (Categories.TryGetValue(category, out var hex)) return hex;
        Color text = ChartPaint.Res("TextColor", 0xFF), bg = ChartPaint.Res("BgColor", 0);
        var c = category switch
        {
            AppCategory.Game => Accents.Mix(accent, text, 0.80),
            AppCategory.Development => Accents.Mix(accent, bg, 0.55),
            AppCategory.Communication => Accents.Mix(accent, bg, 0.75),
            AppCategory.Media => Accents.Mix(accent, text, 0.40),
            AppCategory.Launcher => Accents.Mix(accent, text, 0.58),
            AppCategory.Productivity => Accents.Mix(accent, bg, 0.36),
            _ => accent,
        };
        return Categories[category] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>The window's title bar in the page background and text colors.</summary>
    public static void ApplyTitleBar(Window window) =>
        WindowTheme.ApplyTitleBar(window, !IsLight, ChartPaint.Res("BgColor", 0), ChartPaint.Res("TextColor", 0xFF));

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // A change of Windows' accent arrives as General (or Color on some builds).
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        if (_mode != System && _accent != Accents.Windows) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(_mode, _accent, _keepStatus));
    }

    /// <summary>Windows' own app mode is the light one.</summary>
    public static bool WindowsUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }
}

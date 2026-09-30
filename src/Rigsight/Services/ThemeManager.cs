using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Rigsight.Controls;

namespace Rigsight.Services;

/// <summary>
/// Dark, grey or light: swaps the palette dictionary (Themes/Dark.xaml, Grey.xaml or Light.xaml), switches the Fluent
/// controls to match, and reloads the colors the custom-drawn charts use.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark", Grey = "grey", Light = "light", System = "system";

    private static string _mode = Dark;
    private static string _accent = Accents.Mono;
    private static string? _palette;
    private static Color? _accentColor;
    private static bool _listening;

    public static bool IsLight { get; private set; }

    public static bool IsGrey => _palette == "Grey";

    /// <summary>Goes up on every change, so anything that caches colors knows to make them again.</summary>
    public static int Version { get; private set; }

    /// <summary>Raised after the theme changed (not on the first apply).</summary>
    public static event Action? Changed;

    /// <param name="mode">"dark", "grey", "light" or "system" (follow Windows' app mode).</param>
    /// <param name="accent">A key from <see cref="Accents.All"/>, or "windows" (follow Windows' accent).</param>
    public static void Apply(string? mode, string? accent = Accents.Mono)
    {
        _mode = mode is Grey or Light or System ? mode : Dark;
        _accent = Accents.Normalize(accent);
        if ((_mode == System || _accent == Accents.Windows) && !_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        bool light = _mode == Light || (_mode == System && WindowsUsesLight());
        string name = light ? "Light" : _mode == Grey ? "Grey" : "Dark";
        var accentColor = Accents.ColorFor(_accent, light);
        if (name == _palette && accentColor == _accentColor) return;

        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;
        int index = dictionaries.ToList().FindIndex(d => d.Source?.OriginalString is { } s &&
            (s.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) || s.EndsWith("Themes/Grey.xaml", StringComparison.OrdinalIgnoreCase) ||
             s.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)));
        // By its full address, so it's found from any application (the tests host the app's windows in their own).
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Rigsight;component/Themes/{name}.xaml") };
        if (accentColor is { } color) Accents.ApplyTo(palette, color);
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
        Version++;
        ChartPaint.Load();
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        if (!first) Changed?.Invoke();
    }

    /// <summary>The window's title bar in the page background and text colors.</summary>
    public static void ApplyTitleBar(Window window) =>
        WindowTheme.ApplyTitleBar(window, !IsLight, ChartPaint.Res("BgColor", 0), ChartPaint.Res("TextColor", 0xFF));

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // A change of Windows' accent arrives as General (or Color on some builds).
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        if (_mode != System && _accent != Accents.Windows) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(_mode, _accent));
    }

    private static bool WindowsUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }
}

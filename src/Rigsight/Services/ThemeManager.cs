using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Rigsight.Controls;

namespace Rigsight.Services;

/// <summary>
/// Dark or light: swaps the palette dictionary (Themes/Dark.xaml or Light.xaml), switches the Fluent
/// controls to match, and reloads the colors the custom-drawn charts use.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark", Light = "light", System = "system";

    private static string _mode = Dark;
    private static bool _listening;

    public static bool IsLight { get; private set; }

    /// <summary>Goes up on every change, so anything that caches colors knows to make them again.</summary>
    public static int Version { get; private set; }

    /// <summary>Raised after the theme changed (not on the first apply).</summary>
    public static event Action? Changed;

    /// <param name="mode">"dark", "light" or "system" (follow Windows' app mode).</param>
    public static void Apply(string? mode)
    {
        _mode = mode is Light or System ? mode : Dark;
        if (_mode == System && !_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        bool light = _mode == Light || (_mode == System && WindowsUsesLight());
        if (Version > 0 && light == IsLight) return;

        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;
        int index = dictionaries.ToList().FindIndex(d => d.Source?.OriginalString is "Themes/Dark.xaml" or "Themes/Light.xaml");
        var palette = new ResourceDictionary { Source = new Uri($"Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative) };
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
        if (e.Category != UserPreferenceCategory.General || _mode != System) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(_mode));
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

using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>Out-of-range numbers, duplicates and hand edits in a current settings file are repaired on load.</summary>
public sealed class SettingsNormalizeTests
{
    private static RigsightSettings Load(string body) => SettingsStore.Deserialize($$"""{ "SettingsVersion": 6, {{body}} }""");

    [Fact]
    public void An_empty_file_gives_the_defaults()
    {
        var s = SettingsStore.Deserialize("{}");
        Assert.Equal(6, s.SettingsVersion);
        Assert.False(s.UseFahrenheit);
        Assert.Equal("dark", s.Theme);
        Assert.Equal(1000, s.LiveRefreshMs);
        Assert.Equal(300, s.ChartWindowSeconds);
        Assert.Equal("home", s.StartPage);
        Assert.True(s.AutoUpdate);
        Assert.Equal(Enum.GetValues<WidgetStyle>(), s.Widgets.Select(w => w.Style));
        Assert.Equal([WidgetStyle.Pill], s.Widgets.Where(w => w.Enabled).Select(w => w.Style));
        Assert.Equal(OverlaySettings.DefaultMetrics(), s.Overlay.Metrics);
        Assert.Equal(OverlaySettings.DefaultHotkey, s.Overlay.Hotkey);
        Assert.Equal(2000, s.Tracking.SensorIntervalMs);
        Assert.Equal(5, s.Tracking.ProcessIntervalSeconds);
        Assert.Equal(5, s.Tracking.IdleMinutes);
        Assert.Equal(730, s.Tracking.KeepHistoryDays);
        Assert.Equal(85, s.Alerts.CpuLimit);
        Assert.Equal(NotificationStyle.Card, s.Alerts.Style);
        Assert.Empty(s.CustomPages);
        Assert.Empty(s.MutedCrashApps);
    }

    [Fact]
    public void Defaults_need_no_repair()
    {
        var fresh = new RigsightSettings { SettingsVersion = 6 };
        Assert.Equal(SettingsStore.Serialize(fresh), SettingsStore.Serialize(SettingsStore.Deserialize(SettingsStore.Serialize(fresh))));
    }

    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("light", "light")]
    [InlineData("system", "system")]
    [InlineData("blue", "dark")]
    [InlineData("", "dark")]
    [InlineData("Light", "dark")] // the app always writes lower case
    public void Unknown_themes_fall_back_to_dark(string theme, string expected) =>
        Assert.Equal(expected, Load($"\"Theme\": \"{theme}\"").Theme);

    [Fact]
    public void A_null_theme_falls_back_to_dark() => Assert.Equal("dark", Load("\"Theme\": null").Theme);

    [Theory]
    [InlineData(0, 500)]
    [InlineData(-100, 500)]
    [InlineData(499, 500)]
    [InlineData(500, 500)]
    [InlineData(2000, 2000)]
    [InlineData(30000, 30000)]
    [InlineData(30001, 30000)]
    [InlineData(int.MaxValue, 30000)]
    public void Sensor_interval_is_clamped(int value, int expected) =>
        Assert.Equal(expected, Load($"\"Tracking\": {{ \"SensorIntervalMs\": {value} }}").Tracking.SensorIntervalMs);

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(int.MinValue, 2)]
    public void Process_interval_is_clamped(int value, int expected) =>
        Assert.Equal(expected, Load($"\"Tracking\": {{ \"ProcessIntervalSeconds\": {value} }}").Tracking.ProcessIntervalSeconds);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    [InlineData(120, 120)]
    [InlineData(121, 120)]
    public void Idle_minutes_are_clamped(int value, int expected) =>
        Assert.Equal(expected, Load($"\"Tracking\": {{ \"IdleMinutes\": {value} }}").Tracking.IdleMinutes);

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 90)]
    [InlineData(30, 90)]
    [InlineData(90, 90)]
    [InlineData(180, 90)]
    [InlineData(181, 365)]
    [InlineData(365, 365)]
    [InlineData(547, 365)]
    [InlineData(548, 730)]
    [InlineData(730, 730)]
    [InlineData(1500, 730)]
    [InlineData(1501, 0)]
    [InlineData(3649, 0)]
    [InlineData(3650, 0)]
    [InlineData(int.MaxValue, 0)]
    public void History_retention_snaps_to_the_offered_choices(int value, int expected) =>
        Assert.Equal(expected, Load($"\"Tracking\": {{ \"KeepHistoryDays\": {value} }}").Tracking.KeepHistoryDays);

    [Theory]
    [InlineData(0, 250)]
    [InlineData(249, 250)]
    [InlineData(250, 250)]
    [InlineData(1000, 1000)]
    [InlineData(10000, 10000)]
    [InlineData(10001, 10000)]
    [InlineData(-1, 250)]
    public void Live_refresh_is_clamped(int value, int expected) =>
        Assert.Equal(expected, Load($"\"LiveRefreshMs\": {value}").LiveRefreshMs);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(300, 300)]
    [InlineData(3600, 3600)]
    [InlineData(21600, 21600)]
    [InlineData(86400, 86400)]
    [InlineData(1, 300)]
    [InlineData(299, 300)]
    [InlineData(-5, 300)]
    [InlineData(301, 3600)]
    [InlineData(7200, 3600)]
    [InlineData(86401, 3600)]
    [InlineData(int.MaxValue, 3600)]
    public void Chart_window_snaps_to_the_offered_choices(int value, int expected) =>
        Assert.Equal(expected, Load($"\"ChartWindowSeconds\": {value}").ChartWindowSeconds);

    [Fact]
    public void Missing_widget_styles_are_added_off_and_duplicates_dropped_in_style_order()
    {
        var s = Load("""
            "Widgets": [
              { "Style": "Graph", "Enabled": true, "X": 1 },
              { "Style": "Compact", "Enabled": true },
              { "Style": "Graph", "Enabled": false, "X": 2 }
            ]
            """);
        Assert.Equal(Enum.GetValues<WidgetStyle>(), s.Widgets.Select(w => w.Style));
        var graph = s.Widgets.Single(w => w.Style == WidgetStyle.Graph);
        Assert.True(graph.Enabled);
        Assert.Equal(1, graph.X);
        Assert.Equal([WidgetStyle.Compact, WidgetStyle.Graph], s.Widgets.Where(w => w.Enabled).Select(w => w.Style));
    }

    [Fact]
    public void An_empty_widget_list_gets_every_style_off()
    {
        var s = Load("\"Widgets\": []");
        Assert.Equal(Enum.GetValues<WidgetStyle>(), s.Widgets.Select(w => w.Style));
        Assert.All(s.Widgets, w => Assert.False(w.Enabled));
    }

    [Theory]
    [InlineData("Black", WidgetTheme.Dark)]
    [InlineData("Dark", WidgetTheme.Dark)]
    [InlineData("Light", WidgetTheme.Light)]
    [InlineData("System", WidgetTheme.System)]
    public void The_retired_black_widget_theme_becomes_dark(string theme, WidgetTheme expected) =>
        Assert.Equal(expected, Load($$"""
            "Widgets": [{ "Style": "Pill", "Theme": "{{theme}}" }]
            """).Widgets.Single(w => w.Style == WidgetStyle.Pill).Theme);

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1, 1)]
    [InlineData(1.5, 1)]
    public void Background_opacity_is_clamped(double value, double expected)
    {
        var s = Load($$"""
            "Widgets": [{ "Style": "Pill", "BackgroundOpacity": {{value}} }], "Overlay": { "BackgroundOpacity": {{value}} }
            """);
        Assert.Equal(expected, s.Widgets.Single(w => w.Style == WidgetStyle.Pill).BackgroundOpacity);
        Assert.Equal(expected, s.Overlay.BackgroundOpacity);
    }

    [Theory]
    [InlineData(-1, 0.2)]
    [InlineData(0, 0.2)]
    [InlineData(0.19, 0.2)]
    [InlineData(0.2, 0.2)]
    [InlineData(0.75, 0.75)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    public void Content_opacity_never_goes_below_readable(double value, double expected)
    {
        var s = Load($$"""
            "Widgets": [{ "Style": "Pill", "ContentOpacity": {{value}} }], "Overlay": { "ContentOpacity": {{value}} }
            """);
        Assert.Equal(expected, s.Widgets.Single(w => w.Style == WidgetStyle.Pill).ContentOpacity);
        Assert.Equal(expected, s.Overlay.ContentOpacity);
    }

    [Theory]
    [InlineData(0, 0.6)]
    [InlineData(0.59, 0.6)]
    [InlineData(0.6, 0.6)]
    [InlineData(1.25, 1.25)]
    [InlineData(2, 2)]
    [InlineData(2.01, 2)]
    [InlineData(-3, 0.6)]
    public void Scale_is_clamped(double value, double expected)
    {
        var s = Load($$"""
            "Widgets": [{ "Style": "Pill", "Scale": {{value}} }], "Overlay": { "Scale": {{value}} }
            """);
        Assert.Equal(expected, s.Widgets.Single(w => w.Style == WidgetStyle.Pill).Scale);
        Assert.Equal(expected, s.Overlay.Scale);
    }

    [Theory]
    [InlineData("Alt+Shift+O", "Alt+Shift+O")]
    [InlineData("Ctrl+Alt+P", "Ctrl+Alt+P")]
    [InlineData("control+p", "control+p")] // valid, kept as written
    [InlineData("F9", "F9")]
    [InlineData("P", "Alt+Shift+O")]
    [InlineData("", "Alt+Shift+O")]
    [InlineData("Ctrl+Esc", "Alt+Shift+O")]
    [InlineData("Ctrl+A+B", "Alt+Shift+O")]
    public void An_unusable_overlay_shortcut_falls_back_to_the_default(string hotkey, string expected) =>
        Assert.Equal(expected, Load($$"""
            "Overlay": { "Hotkey": "{{hotkey}}" }
            """).Overlay.Hotkey);

    [Fact]
    public void A_null_overlay_shortcut_falls_back_to_the_default() =>
        Assert.Equal(OverlaySettings.DefaultHotkey, Load("\"Overlay\": { \"Hotkey\": null }").Overlay.Hotkey);

    [Fact]
    public void Overlay_metrics_are_deduplicated_and_put_in_screen_order()
    {
        var s = Load("""
            "Overlay": { "Metrics": ["Clock", "Ram", "CpuTemp", "Ram", "Fps", "Clock", 99, -1, 3] }
            """);
        Assert.Equal([OverlayMetric.Fps, OverlayMetric.CpuTemp, OverlayMetric.Ram, OverlayMetric.Clock], s.Overlay.Metrics);
    }

    [Fact]
    public void Overlay_sensors_are_cleaned_up()
    {
        var s = Load("""
            "Overlay": { "Sensors": [
              { "Id": "a", "Label": "  Pump  " },
              { "Id": "", "Label": "no id" },
              { "Id": "   " },
              { "Label": "missing id" },
              { "Id": "a", "Label": "duplicate" },
              { "Id": "b", "Label": "   " },
              { "Id": "c", "Label": "A label far too long for the overlay" },
              { "Id": "d", "Label": "Exactly18Character" },
              { "Id": "e", "Label": "  Nineteen characters  " },
              { "Id": "A", "Label": null }
            ] }
            """);
        Assert.Equal(["a", "b", "c", "d", "e", "A"], s.Overlay.Sensors.Select(x => x.Id));
        Assert.Equal(["Pump", null, "A label far too lo", "Exactly18Character", "Nineteen character", null], s.Overlay.Sensors.Select(x => x.Label));
        Assert.All(s.Overlay.Sensors, x => Assert.True(x.Label is null || x.Label.Length <= OverlaySettings.MaxLabelLength));
    }

    [Fact]
    public void The_overlay_shows_at_most_ten_sensors_keeping_the_first()
    {
        var sensors = string.Join(",", Enumerable.Range(0, 25).Select(i => $$"""{ "Id": "s{{i}}" }"""));
        var s = Load($$"""
            "Overlay": { "Sensors": [{{sensors}}] }
            """);
        Assert.Equal(OverlaySettings.MaxSensors, s.Overlay.Sensors.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"s{i}"), s.Overlay.Sensors.Select(x => x.Id));
    }

    [Fact]
    public void Duplicate_overlay_sensors_count_once_towards_the_limit()
    {
        var sensors = string.Join(",", Enumerable.Range(0, 30).Select(i => $$"""{ "Id": "s{{i / 3}}" }"""));
        var s = Load($$"""
            "Overlay": { "Sensors": [{{sensors}}] }
            """);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"s{i}"), s.Overlay.Sensors.Select(x => x.Id));
    }

    [Fact]
    public void Muted_crash_apps_drop_blanks_and_case_duplicates()
    {
        var s = Load("""
            "MutedCrashApps": ["Game.exe", "", "  ", null, "game.EXE", "other.exe", "GAME.exe"]
            """);
        Assert.Equal(["Game.exe", "other.exe"], s.MutedCrashApps);
        Assert.True(s.IsCrashMuted("GAME.EXE"));
        Assert.True(s.IsCrashMuted("Other.exe"));
        Assert.False(s.IsCrashMuted("third.exe"));
        Assert.False(s.IsCrashMuted(null));
        Assert.False(s.IsCrashMuted(""));
    }

    [Fact]
    public void App_names_and_categories_are_case_insensitive_after_loading()
    {
        var s = Load("""
            "AppNames": { "eldenring.exe": "Elden Ring" }, "AppCategories": { "Code.exe": "Game" }
            """);
        Assert.Equal("Elden Ring", s.AppNames["EldenRing.EXE"]);
        Assert.Equal(AppCategory.Game, s.AppCategories["code.exe"]);
        Assert.True(s.AppNames.ContainsKey("ELDENRING.EXE"));
    }

    [Fact]
    public void App_names_differing_only_in_case_do_not_lose_the_settings()
    {
        // A copy of the settings that lost its case-insensitive lookup (e.g. read straight from the pipe) can hold both.
        var s = Load("""
            "UseFahrenheit": true,
            "AppNames": { "chrome.exe": "Chrome", "Chrome.exe": "Browser" },
            "AppCategories": { "a.exe": "Game", "A.EXE": "Media" }
            """);
        Assert.True(s.UseFahrenheit);
        Assert.Single(s.AppNames);
        Assert.Equal("Browser", s.AppNames["CHROME.EXE"]);
        Assert.Single(s.AppCategories);
        Assert.Equal(AppCategory.Media, s.AppCategories["a.exe"]);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        var s = Load("""
            "UseFahrenheit": true, "FutureFeature": { "x": [1, 2, 3] }, "Tracking": { "IdleMinutes": 9, "Unknown": true },
            "Overlay": { "Corner": "BottomLeft", "Glow": 5 }, "Widgets": [{ "Style": "Pill", "Rainbow": true }]
            """);
        Assert.True(s.UseFahrenheit);
        Assert.Equal(9, s.Tracking.IdleMinutes);
        Assert.Equal(OverlayCorner.BottomLeft, s.Overlay.Corner);
    }

    [Theory]
    [InlineData("\"BottomRight\"", OverlayCorner.BottomRight)]
    [InlineData("\"bottomright\"", OverlayCorner.BottomRight)]
    [InlineData("3", OverlayCorner.BottomRight)]
    [InlineData("0", OverlayCorner.TopLeft)]
    public void Enums_are_read_as_names_in_any_case_or_as_numbers(string json, OverlayCorner expected) =>
        Assert.Equal(expected, Load($$"""
            "Overlay": { "Corner": {{json}} }
            """).Overlay.Corner);

    [Fact]
    public void Enums_are_written_as_names()
    {
        var json = SettingsStore.Serialize(new RigsightSettings());
        Assert.Contains("\"Corner\": \"TopLeft\"", json);
        Assert.Contains("\"Style\": \"Pill\"", json);
        Assert.Contains("\"Fps\"", json);
    }

    [Fact]
    public void An_unknown_enum_name_is_an_error_the_caller_sees() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Load("\"Overlay\": { \"Corner\": \"Middle\" }"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("{ \"Tracking\": 5 }")]
    [InlineData("{ \"SettingsVersion\": \"six\" }")]
    [InlineData("{ \"Widgets\": {} }")]
    public void Garbage_is_an_error_for_Deserialize(string json) =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SettingsStore.Deserialize(json));

    [Fact]
    public void A_literal_null_gives_the_defaults()
    {
        var s = SettingsStore.Deserialize("null");
        Assert.Equal(6, s.SettingsVersion);
        Assert.Equal(SettingsStore.Serialize(SettingsStore.Deserialize("{}")), SettingsStore.Serialize(s));
    }

    [Fact]
    public void Pause_states()
    {
        Assert.False(new TrackingSettings().IsPaused(1000));
        Assert.True(new TrackingSettings { PausedUntil = -1 }.IsPaused(1000));
        Assert.True(new TrackingSettings { PausedUntil = 1001 }.IsPaused(1000));
        Assert.False(new TrackingSettings { PausedUntil = 1000 }.IsPaused(1000));
        Assert.False(new TrackingSettings { PausedUntil = 999 }.IsPaused(1000));
        Assert.False(new TrackingSettings { PausedUntil = -2 }.IsPaused(1000));
    }

    [Theory]
    [InlineData("""{ "Tracking": { "Enabled": false } }""", -1)]
    [InlineData("""{ "Tracking": { "Enabled": false, "PausedUntil": 1900000000 } }""", -1)]
    [InlineData("""{ "Tracking": { "Enabled": true, "PausedUntil": 1900000000 } }""", 1900000000)]
    [InlineData("""{ "Tracking": { "Enabled": true } }""", 0)]
    public void The_old_record_switch_turned_off_is_paused_until_resumed(string json, long pausedUntil)
    {
        var s = SettingsStore.Deserialize(json);
        Assert.Equal(pausedUntil, s.Tracking.PausedUntil);
        Assert.Null(s.Tracking.Enabled);
        // Never written again, so resuming is for good.
        using (var doc = System.Text.Json.JsonDocument.Parse(SettingsStore.Serialize(s)))
            Assert.False(doc.RootElement.GetProperty("Tracking").TryGetProperty("Enabled", out _));
        s.Tracking.PausedUntil = 0;
        Assert.False(SettingsStore.Deserialize(SettingsStore.Serialize(s)).Tracking.IsPaused(1000));
    }
}

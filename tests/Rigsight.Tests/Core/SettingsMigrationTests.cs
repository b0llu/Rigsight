using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>Settings files written by every older version are brought up to date exactly once, as each step's comment says.</summary>
public sealed class SettingsMigrationTests
{
    private static RigsightSettings Load(string json) => SettingsStore.Deserialize(json);

    private const string CustomWidgets = """
        [
          { "Style": "Compact", "Enabled": true, "X": 10, "Y": 20, "Scale": 1.5 },
          { "Style": "Pill", "Enabled": false },
          { "Style": "Graph", "Enabled": true, "Visibility": "OnlyInFullscreen" },
          { "Style": "Gauges", "Enabled": true, "Visibility": "HideInFullscreen" }
        ]
        """;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Before_v2_widgets_are_reset_to_the_slim_bar(int version)
    {
        var s = Load($$"""{ "SettingsVersion": {{version}}, "Widgets": {{CustomWidgets}} }""");
        Assert.Equal(Enum.GetValues<WidgetStyle>(), s.Widgets.Select(w => w.Style));
        Assert.Equal([WidgetStyle.Pill], s.Widgets.Where(w => w.Enabled).Select(w => w.Style));
        Assert.All(s.Widgets, w => Assert.Null(w.X));
        Assert.All(s.Widgets, w => Assert.Equal(1.0, w.Scale));
        Assert.All(s.Widgets, w => Assert.Equal(WidgetVisibility.Always, w.Visibility));
    }

    [Fact]
    public void A_v1_file_without_widgets_gets_the_defaults()
    {
        var s = Load("""{ "SettingsVersion": 1 }""");
        Assert.Equal([WidgetStyle.Pill], s.Widgets.Where(w => w.Enabled).Select(w => w.Style));
    }

    [Fact]
    public void In_v2_only_in_fullscreen_widgets_become_always_but_off()
    {
        var s = Load($$"""{ "SettingsVersion": 2, "Widgets": {{CustomWidgets}} }""");
        var graph = s.Widgets.Single(w => w.Style == WidgetStyle.Graph);
        Assert.Equal(WidgetVisibility.Always, graph.Visibility);
        Assert.False(graph.Enabled);

        // Everything else is untouched.
        var compact = s.Widgets.Single(w => w.Style == WidgetStyle.Compact);
        Assert.True(compact.Enabled);
        Assert.Equal((10, 20), (compact.X, compact.Y));
        Assert.Equal(1.5, compact.Scale);
        var gauges = s.Widgets.Single(w => w.Style == WidgetStyle.Gauges);
        Assert.True(gauges.Enabled);
        Assert.Equal(WidgetVisibility.HideInFullscreen, gauges.Visibility);
        Assert.False(s.Widgets.Single(w => w.Style == WidgetStyle.Pill).Enabled);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void From_v3_only_in_fullscreen_is_not_migrated_again(int version)
    {
        var s = Load($$"""{ "SettingsVersion": {{version}}, "Widgets": {{CustomWidgets}} }""");
        var graph = s.Widgets.Single(w => w.Style == WidgetStyle.Graph);
        Assert.Equal(WidgetVisibility.OnlyInFullscreen, graph.Visibility);
        Assert.True(graph.Enabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void Before_v4_the_overlay_gains_the_frame_rate_once(int version)
    {
        var s = Load($$"""{ "SettingsVersion": {{version}}, "Overlay": { "Metrics": ["GpuTemp", "CpuTemp"] } }""");
        Assert.Equal([OverlayMetric.Fps, OverlayMetric.OnePercentLow, OverlayMetric.CpuTemp, OverlayMetric.GpuTemp], s.Overlay.Metrics);
    }

    [Fact]
    public void Before_v4_frame_rate_already_on_is_not_added_twice()
    {
        var s = Load("""{ "SettingsVersion": 3, "Overlay": { "Metrics": ["Fps", "CpuTemp"] } }""");
        Assert.Equal([OverlayMetric.Fps, OverlayMetric.OnePercentLow, OverlayMetric.CpuTemp], s.Overlay.Metrics);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void From_v4_frame_rate_the_user_turned_off_stays_off(int version)
    {
        var s = Load($$"""{ "SettingsVersion": {{version}}, "Overlay": { "Metrics": ["CpuTemp"] } }""");
        Assert.Equal([OverlayMetric.CpuTemp], s.Overlay.Metrics);
    }

    [Fact]
    public void Before_v4_an_empty_overlay_gets_just_the_frame_rate()
    {
        var s = Load("""{ "SettingsVersion": 3, "Overlay": { "Metrics": [] } }""");
        Assert.Equal([OverlayMetric.Fps, OverlayMetric.OnePercentLow], s.Overlay.Metrics);
    }

    [Theory]
    [InlineData(0, 3650, 0)]
    [InlineData(4, 3650, 0)]
    [InlineData(4, 5000, 0)]
    [InlineData(4, 730, 730)]
    [InlineData(4, 365, 365)]
    [InlineData(4, 90, 90)]
    [InlineData(4, 0, 0)]
    [InlineData(5, 3650, 0)]
    [InlineData(6, 730, 730)]
    public void In_v5_forever_stored_as_ten_years_becomes_0(int version, int stored, int expected)
    {
        var s = Load($$"""{ "SettingsVersion": {{version}}, "Tracking": { "KeepHistoryDays": {{stored}} } }""");
        Assert.Equal(expected, s.Tracking.KeepHistoryDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    public void The_old_single_opacity_sets_both_new_ones(int version)
    {
        var s = Load($$"""
            {
              "SettingsVersion": {{version}},
              "Widgets": [{ "Style": "Pill", "Enabled": true, "Opacity": 0.5 }, { "Style": "Compact", "BackgroundOpacity": 0.3, "ContentOpacity": 0.7 }],
              "Overlay": { "Opacity": 0.6, "BackgroundOpacity": 0.1 }
            }
            """);
        Assert.Equal((0.6, 0.6, (double?)null), (s.Overlay.BackgroundOpacity, s.Overlay.ContentOpacity, s.Overlay.Opacity));
        if (version >= 2)
        {
            var pill = s.Widgets.Single(w => w.Style == WidgetStyle.Pill);
            Assert.Equal((0.5, 0.5, (double?)null), (pill.BackgroundOpacity, pill.ContentOpacity, pill.Opacity));
            var compact = s.Widgets.Single(w => w.Style == WidgetStyle.Compact);
            Assert.Equal((0.3, 0.7), (compact.BackgroundOpacity, compact.ContentOpacity));
        }
        var json = SettingsStore.Serialize(s);
        Assert.DoesNotContain("\"Opacity\"", json);
    }

    [Fact]
    public void An_old_low_opacity_keeps_the_readings_readable()
    {
        var s = Load("""{ "SettingsVersion": 5, "Widgets": [{ "Style": "Pill", "Opacity": 0.1 }], "Overlay": { "Opacity": 0.05 } }""");
        var pill = s.Widgets.Single(w => w.Style == WidgetStyle.Pill);
        Assert.Equal((0.1, 0.2), (pill.BackgroundOpacity, pill.ContentOpacity));
        Assert.Equal((0.05, 0.2), (s.Overlay.BackgroundOpacity, s.Overlay.ContentOpacity));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(1000)]
    public void Every_version_ends_up_current(int version) =>
        Assert.Equal(6, Load($$"""{ "SettingsVersion": {{version}} }""").SettingsVersion);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Migrating_twice_changes_nothing(int version)
    {
        var once = Load(OldFile(version));
        var twice = SettingsStore.Deserialize(SettingsStore.Serialize(once));
        Assert.Equal(SettingsStore.Serialize(once), SettingsStore.Serialize(twice));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Everything_a_migration_doesnt_touch_is_kept(int version)
    {
        var s = Load(OldFile(version));
        Assert.True(s.UseFahrenheit);
        Assert.Equal("light", s.Theme);
        Assert.Equal(3000, s.Tracking.SensorIntervalMs);
        Assert.Equal(["game.exe"], s.Tracking.ExcludedApps);
        Assert.Equal(90, s.Alerts.CpuLimit);
        Assert.Equal("Elden Ring", s.AppNames["ELDENRING.EXE"]);
        Assert.Equal(AppCategory.Game, s.AppCategories["Minecraft.exe"]);
        Assert.Equal("CPU die", s.SensorLabels["/amdcpu/0/temperature/2"]);
        Assert.Equal(["crash.exe"], s.MutedCrashApps);
        Assert.Equal("Alt+Shift+P", s.Overlay.Hotkey);
        Assert.Equal(OverlayCorner.BottomRight, s.Overlay.Corner);
        Assert.Single(s.CustomPages);
    }

    /// <summary>A settings file as version <paramref name="version"/> could have written it.</summary>
    private static string OldFile(int version) => $$"""
        {
          "SettingsVersion": {{version}},
          "UseFahrenheit": true,
          "Theme": "light",
          "LiveRefreshMs": 2000,
          "ChartWindowSeconds": 3600,
          "Tracking": { "SensorIntervalMs": 3000, "KeepHistoryDays": 3650, "ExcludedApps": ["game.exe"] },
          "Alerts": { "CpuLimit": 90 },
          "Widgets": {{CustomWidgets}},
          "Overlay": { "Hotkey": "Alt+Shift+P", "Corner": "BottomRight", "Opacity": 0.8, "Metrics": ["CpuTemp", "Ram"],
                       "Sensors": [{ "Id": "/gpu/0/fan/0", "Label": " GPU fan " }] },
          "AppNames": { "eldenring.exe": "Elden Ring" },
          "AppCategories": { "minecraft.exe": "Game" },
          "SensorLabels": { "/amdcpu/0/temperature/2": "CPU die" },
          "MutedCrashApps": ["crash.exe"],
          "CustomPages": [{ "Id": "abc", "Name": "Games", "Tiles": [{ "Id": "t1", "Kind": "cpu-gauge", "X": 1, "Y": 1, "W": 2, "H": 1 }] }]
        }
        """;
}

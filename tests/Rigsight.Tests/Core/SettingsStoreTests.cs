using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

/// <summary>Loading and saving the file, hand-edited files with nulls in them, custom pages, and copies.</summary>
public sealed class SettingsStoreTests
{
    private static RigsightSettings Load(string body) => SettingsStore.Deserialize($$"""{ "SettingsVersion": 6, "UseFahrenheit": true, "LiveRefreshMs": 2000, {{body}} }""");

    // ---- Nulls where a section or list belongs (hand edits): that part gets its defaults, the rest is kept ----

    public static TheoryData<string> NullableSections() =>
    [
        "Widgets", "Overlay", "Tracking", "Alerts", "CustomPages", "AppNames", "AppCategories", "SensorLabels",
        "HiddenSensors", "CollapsedHardware", "HardwareOrder", "MutedCrashApps", "StartPage", "Theme",
    ];

    [Theory]
    [MemberData(nameof(NullableSections))]
    public void A_null_section_gets_its_defaults_and_keeps_the_rest(string section)
    {
        var s = Load($"\"{section}\": null");
        Assert.True(s.UseFahrenheit);
        Assert.Equal(2000, s.LiveRefreshMs);
        AssertComplete(s);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Null_sections_in_a_file_of_any_version_load(int version)
    {
        var s = SettingsStore.Deserialize($$"""
            {
              "SettingsVersion": {{version}}, "UseFahrenheit": true,
              "Widgets": null, "Overlay": null, "Tracking": null, "Alerts": null, "CustomPages": null, "AppNames": null,
              "AppCategories": null, "SensorLabels": null, "HiddenSensors": null, "CollapsedHardware": null,
              "HardwareOrder": null, "MutedCrashApps": null
            }
            """);
        Assert.True(s.UseFahrenheit);
        AssertComplete(s);
        Assert.Equal(730, s.Tracking.KeepHistoryDays);
        Assert.Equal(Enum.GetValues<WidgetStyle>(), s.Widgets.Select(w => w.Style));
    }

    [Fact]
    public void Null_widgets_get_the_default_widgets() =>
        Assert.Equal([WidgetStyle.Pill], Load("\"Widgets\": null").Widgets.Where(w => w.Enabled).Select(w => w.Style));

    [Fact]
    public void Null_entries_in_lists_are_dropped()
    {
        var s = Load("""
            "Widgets": [null, { "Style": "Graph", "Enabled": true }],
            "Overlay": { "Sensors": [null, { "Id": "x" }] },
            "CustomPages": [null, { "Id": "p", "Grid": 2, "Tiles": [null, { "Id": "t", "Kind": "sensor" }] }],
            "Tracking": { "ExcludedApps": null }
            """);
        Assert.True(s.Widgets.Single(w => w.Style == WidgetStyle.Graph).Enabled);
        Assert.Equal(["x"], s.Overlay.Sensors.Select(x => x.Id));
        Assert.Equal("t", Assert.Single(Assert.Single(s.CustomPages).Tiles).Id);
        AssertComplete(s);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Null_overlay_lists_load(int version)
    {
        var s = SettingsStore.Deserialize($$"""{ "SettingsVersion": {{version}}, "UseFahrenheit": true, "Overlay": { "Metrics": null, "Sensors": null, "Corner": "BottomLeft" } }""");
        Assert.True(s.UseFahrenheit);
        Assert.Equal(OverlayCorner.BottomLeft, s.Overlay.Corner);
        Assert.NotNull(s.Overlay.Metrics);
        Assert.NotNull(s.Overlay.Sensors);
    }

    [Fact]
    public void Null_tiles_load_as_an_empty_page()
    {
        var s = Load("""
            "CustomPages": [{ "Id": "p", "Name": "Mine", "Tiles": null }]
            """);
        var page = Assert.Single(s.CustomPages);
        Assert.Equal("Mine", page.Name);
        Assert.Empty(page.Tiles);
    }

    [Fact]
    public void The_agent_can_normalize_a_copy_with_nulls_from_the_app()
    {
        // The agent runs every settings message from the app through Serialize → Deserialize.
        var sent = new RigsightSettings { UseFahrenheit = true, Widgets = null!, Tracking = null!, AppNames = null!, CustomPages = null! };
        var s = SettingsStore.Deserialize(SettingsStore.Serialize(sent));
        Assert.True(s.UseFahrenheit);
        AssertComplete(s);
    }

    private static void AssertComplete(RigsightSettings s)
    {
        Assert.NotNull(s.Widgets);
        Assert.All(s.Widgets, Assert.NotNull);
        Assert.Equal(Enum.GetValues<WidgetStyle>().Length, s.Widgets.Count);
        Assert.NotNull(s.Overlay);
        Assert.NotNull(s.Overlay.Metrics);
        Assert.NotNull(s.Overlay.Sensors);
        Assert.NotNull(s.Overlay.Hotkey);
        Assert.NotNull(s.Tracking);
        Assert.NotNull(s.Tracking.ExcludedApps);
        Assert.NotNull(s.Alerts);
        Assert.NotNull(s.CustomPages);
        Assert.All(s.CustomPages, p => Assert.NotNull(p.Tiles));
        Assert.NotNull(s.AppNames);
        Assert.NotNull(s.AppCategories);
        Assert.NotNull(s.SensorLabels);
        Assert.NotNull(s.HiddenSensors);
        Assert.NotNull(s.CollapsedHardware);
        Assert.NotNull(s.HardwareOrder);
        Assert.NotNull(s.MutedCrashApps);
        Assert.NotNull(s.StartPage);
        Assert.NotNull(s.Theme);
        // Case-insensitive lookups work on the repaired dictionaries too.
        s.AppNames["A.exe"] = "x";
        Assert.True(s.AppNames.ContainsKey("a.EXE"));
        s.AppCategories["B.exe"] = AppCategory.Game;
        Assert.True(s.AppCategories.ContainsKey("b.exe"));
        // And the result survives another round.
        Assert.Equal(SettingsStore.Serialize(s), SettingsStore.Serialize(SettingsStore.Deserialize(SettingsStore.Serialize(s))));
    }

    // ---- Custom pages ----

    [Fact]
    public void Pages_from_the_first_grid_are_converted_to_twelve_columns_once()
    {
        var s = Load("""
            "CustomPages": [{ "Id": "p1", "Name": "Old", "Tiles": [
              { "Id": "a", "Kind": "cpu-gauge", "X": 0, "Y": 0, "W": 1, "H": 1 },
              { "Id": "b", "Kind": "sensor", "Sensor": "key:cpu-temp", "X": 1, "Y": 2, "W": 3, "H": 2 },
              { "Id": "c", "Kind": "most-used", "X": 3, "Y": 5, "W": 1, "H": 8 }
            ] }]
            """);
        var page = Assert.Single(s.CustomPages);
        Assert.Equal(CustomPageConfig.CurrentGrid, page.Grid);
        Assert.Equal([(0, 0, 3, 2), (3, 4, 9, 4), (9, 10, 3, 16)], page.Tiles.Select(t => (t.X, t.Y, t.W, t.H)));
        Assert.Equal("key:cpu-temp", page.Tiles[1].Sensor);

        var again = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal([(0, 0, 3, 2), (3, 4, 9, 4), (9, 10, 3, 16)], again.CustomPages[0].Tiles.Select(t => (t.X, t.Y, t.W, t.H)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void Only_pages_older_than_the_current_grid_are_converted(int grid, bool converted)
    {
        var s = Load($$"""
            "CustomPages": [{ "Id": "p", "Grid": {{grid}}, "Tiles": [{ "Id": "t", "Kind": "x", "X": 1, "Y": 1, "W": 2, "H": 2 }] }]
            """);
        var tile = s.CustomPages[0].Tiles[0];
        Assert.Equal(converted ? (3, 2, 6, 4) : (1, 1, 2, 2), (tile.X, tile.Y, tile.W, tile.H));
        Assert.Equal(converted ? CustomPageConfig.CurrentGrid : grid, s.CustomPages[0].Grid);
    }

    [Fact]
    public void A_page_saved_without_a_grid_reads_as_the_first_grid()
    {
        var s = Load("""
            "CustomPages": [{ "Id": "p", "Tiles": [{ "Id": "t", "Kind": "x", "X": 2, "W": 2 }] }]
            """);
        Assert.Equal((6, 6), (s.CustomPages[0].Tiles[0].X, s.CustomPages[0].Tiles[0].W));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, 1, 1)]
    [InlineData(-5, -5, 3, 3, 0, 0, 3, 3)]
    [InlineData(11, 0, 4, 1, 8, 0, 4, 1)]
    [InlineData(20, 0, 1, 1, 11, 0, 1, 1)]
    [InlineData(0, 0, 30, 1, 0, 0, 12, 1)]
    [InlineData(0, 0, 1, 99, 0, 0, 1, 16)]
    [InlineData(0, 1000, 1, 16, 0, 1000, 1, 16)]
    [InlineData(6, 3, 6, 4, 6, 3, 6, 4)]
    public void Tiles_are_kept_on_the_grid(int x, int y, int w, int h, int ex, int ey, int ew, int eh)
    {
        var s = Load($$"""
            "CustomPages": [{ "Id": "p", "Grid": 2, "Tiles": [{ "Id": "t", "Kind": "x", "X": {{x}}, "Y": {{y}}, "W": {{w}}, "H": {{h}} }] }]
            """);
        var t = s.CustomPages[0].Tiles[0];
        Assert.Equal((ex, ey, ew, eh), (t.X, t.Y, t.W, t.H));
    }

    [Fact]
    public void Tiles_without_a_kind_and_duplicate_tiles_are_dropped()
    {
        var s = Load("""
            "CustomPages": [{ "Id": "p", "Grid": 2, "Tiles": [
              { "Id": "a", "Kind": "cpu-gauge" }, { "Id": "b", "Kind": "" }, { "Id": "c", "Kind": null }, { "Id": "d" },
              { "Id": "a", "Kind": "gpu-gauge" }, { "Id": "e", "Kind": "sensor" }
            ] }]
            """);
        Assert.Equal([("a", "cpu-gauge"), ("e", "sensor")], s.CustomPages[0].Tiles.Select(t => (t.Id, t.Kind)));
    }

    [Fact]
    public void Pages_without_an_id_or_name_get_one_and_duplicate_pages_are_dropped()
    {
        var s = Load("""
            "CustomPages": [
              { "Id": "same", "Name": "First", "Grid": 2 },
              { "Id": "", "Name": "  ", "Grid": 2 },
              { "Id": null, "Name": null, "Grid": 2 },
              { "Id": "same", "Name": "Second", "Grid": 2 }
            ]
            """);
        Assert.Equal(3, s.CustomPages.Count);
        Assert.Equal("First", s.CustomPages[0].Name);
        Assert.All(s.CustomPages.Skip(1), p => Assert.Equal("Dashboard", p.Name));
        Assert.All(s.CustomPages, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
        Assert.Equal(3, s.CustomPages.Select(p => p.Id).Distinct().Count());
        Assert.Equal(10, s.CustomPages[1].Id.Length);
    }

    [Fact]
    public void New_ids_are_ten_characters_and_unique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => CustomPageConfig.NewId()).ToList();
        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{10}$", id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Matches("^[0-9a-f]{10}$", new TileConfig().Id);
    }

    [Fact]
    public void Pages_created_in_the_app_on_the_current_grid_are_not_converted()
    {
        var s = new RigsightSettings();
        s.CustomPages.Add(new CustomPageConfig { Grid = CustomPageConfig.CurrentGrid, Tiles = [new TileConfig { Kind = "x", X = 2, W = 4, H = 3 }] });
        var t = SettingsStore.Deserialize(SettingsStore.Serialize(s)).CustomPages[0].Tiles[0];
        Assert.Equal((2, 4, 3), (t.X, t.W, t.H));
    }

    // ---- Copies and round trips ----

    private static RigsightSettings Rich()
    {
        var s = SettingsStore.Deserialize("{}");
        s.UseFahrenheit = true;
        s.Theme = "system";
        s.ChartWindowSeconds = 21600;
        s.StartPage = "custom:abc";
        s.AutoUpdate = false;
        s.LastUpdateNotice = "0.6.0";
        s.LastRecapDay = "2026-09-24";
        s.StartupConfigured = true;
        s.Tracking.ExcludedApps.Add("secret.exe");
        s.Tracking.PausedUntil = -1;
        s.Alerts.Style = NotificationStyle.Windows;
        s.Alerts.GpuHotSpotLimit = 95.5;
        s.Widgets[0].X = -1920;
        s.Widgets[0].Y = 40;
        s.Widgets[0].Locked = true;
        s.Overlay.Sensors.Add(new OverlaySensor { Id = "/gpu/0/temp", Label = "Hot spot" });
        s.Overlay.Layout = OverlayLayout.Line;
        s.Overlay.Grayscale = true;
        s.AppNames["eldenring.exe"] = "Elden Ring";
        s.AppCategories["blender.exe"] = AppCategory.Productivity;
        s.SensorLabels["/cpu/0"] = "CPU";
        s.HiddenSensors.Add("/x");
        s.CollapsedHardware.Add("Motherboard");
        s.HardwareOrder.AddRange(["GPU", "CPU"]);
        s.MutedCrashApps.Add("crashy.exe");
        s.CustomPages.Add(new CustomPageConfig { Id = "abc", Name = "Games", Grid = 2, Tiles = [new TileConfig { Id = "t", Kind = "sensor", Sensor = "key:gpu", X = 3, W = 3, H = 2 }] });
        return s;
    }

    [Fact]
    public void Serialize_and_deserialize_is_stable()
    {
        var json = SettingsStore.Serialize(Rich());
        var back = SettingsStore.Deserialize(json);
        Assert.Equal(json, SettingsStore.Serialize(back));
        Assert.Equal("Elden Ring", back.AppNames["ELDENRING.exe"]);
        Assert.Equal(-1920, back.Widgets[0].X);
        Assert.Equal(-1, back.Tracking.PausedUntil);
        Assert.Equal("0.6.0", back.LastUpdateNotice);
        Assert.Equal(NotificationStyle.Windows, back.Alerts.Style);
    }

    [Fact]
    public void A_clone_is_equal_and_independent()
    {
        var original = Rich();
        var before = SettingsStore.Serialize(original);
        var clone = original.Clone();
        Assert.Equal(before, SettingsStore.Serialize(clone));

        clone.UseFahrenheit = false;
        clone.Widgets[0].Scale = 2;
        clone.Widgets.RemoveAt(1);
        clone.Overlay.Sensors[0].Label = "changed";
        clone.Overlay.Metrics.Clear();
        clone.Tracking.ExcludedApps.Clear();
        clone.Alerts.CpuLimit = 1;
        clone.AppNames["new.exe"] = "New";
        clone.AppCategories.Clear();
        clone.SensorLabels.Clear();
        clone.HiddenSensors.Clear();
        clone.CollapsedHardware.Clear();
        clone.HardwareOrder.Clear();
        clone.MutedCrashApps.Clear();
        clone.CustomPages[0].Tiles[0].W = 12;
        clone.CustomPages[0].Name = "changed";

        Assert.Equal(before, SettingsStore.Serialize(original));
    }

    [Fact]
    public void A_clone_keeps_case_insensitive_lookups()
    {
        var clone = Rich().Clone();
        Assert.True(clone.AppNames.ContainsKey("ELDENRING.EXE"));
        Assert.True(clone.AppCategories.ContainsKey("Blender.exe"));
    }

    // ---- The file ----

    [Fact]
    public void Save_then_Load_gives_the_same_settings()
    {
        var file = Path.Combine(TestEnvironment.NewFolder("settings"), "settings.json");
        var s = Rich();
        SettingsStore.Save(s, file);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".tmp"));
        Assert.Equal(SettingsStore.Serialize(s), SettingsStore.Serialize(SettingsStore.Load(file)));
    }

    [Fact]
    public void Save_creates_the_folder_and_replaces_the_old_file()
    {
        var file = Path.Combine(TestEnvironment.NewFolder("settings"), "a", "b", "settings.json");
        SettingsStore.Save(new RigsightSettings { UseFahrenheit = true }, file);
        SettingsStore.Save(new RigsightSettings { UseFahrenheit = false, Theme = "light" }, file);
        var s = SettingsStore.Load(file);
        Assert.False(s.UseFahrenheit);
        Assert.Equal("light", s.Theme);
    }

    [Fact]
    public void Save_to_an_unwritable_place_does_not_throw()
    {
        var folder = TestEnvironment.NewFolder("settings");
        SettingsStore.Save(new RigsightSettings(), folder); // a folder, not a file
        SettingsStore.Save(new RigsightSettings(), Path.Combine(folder, "bad\0name.json"));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void A_missing_file_loads_the_defaults()
    {
        var s = SettingsStore.Load(Path.Combine(TestEnvironment.NewFolder("settings"), "none.json"));
        Assert.Equal(SettingsStore.Serialize(SettingsStore.Deserialize("{}")), SettingsStore.Serialize(s));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ broken")]
    [InlineData("\0\0\0\0")]
    [InlineData("{ \"Overlay\": { \"Corner\": \"Middle\" } }")]
    public void A_broken_file_loads_the_defaults_without_throwing(string content)
    {
        var file = Path.Combine(TestEnvironment.NewFolder("settings"), "settings.json");
        File.WriteAllText(file, content);
        var s = SettingsStore.Load(file);
        Assert.Equal(SettingsStore.Serialize(SettingsStore.Deserialize("{}")), SettingsStore.Serialize(s));
    }

    [Fact]
    public void A_file_with_a_byte_order_mark_loads()
    {
        var file = Path.Combine(TestEnvironment.NewFolder("settings"), "settings.json");
        File.WriteAllText(file, """{ "SettingsVersion": 6, "UseFahrenheit": true }""", new System.Text.UTF8Encoding(true));
        Assert.True(SettingsStore.Load(file).UseFahrenheit);
    }

    [Fact]
    public void A_hand_edited_file_with_null_widgets_keeps_the_users_other_settings()
    {
        var file = Path.Combine(TestEnvironment.NewFolder("settings"), "settings.json");
        File.WriteAllText(file, """
            { "SettingsVersion": 6, "UseFahrenheit": true, "Theme": "light", "Widgets": null, "Overlay": null,
              "Tracking": null, "CustomPages": null, "AppNames": null,
              "MutedCrashApps": ["x.exe"], "SensorLabels": { "/cpu": "CPU" } }
            """);
        var s = SettingsStore.Load(file);
        Assert.True(s.UseFahrenheit);
        Assert.Equal("light", s.Theme);
        Assert.Equal(["x.exe"], s.MutedCrashApps);
        Assert.Equal("CPU", s.SensorLabels["/cpu"]);
    }

    [Fact]
    public void A_file_being_written_by_another_process_can_still_be_read()
    {
        var file = Path.Combine(TestEnvironment.NewFolder("settings"), "settings.json");
        SettingsStore.Save(new RigsightSettings { UseFahrenheit = true }, file);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.True(SettingsStore.Load(file).UseFahrenheit);
    }
}

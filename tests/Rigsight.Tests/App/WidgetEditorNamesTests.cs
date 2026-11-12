using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The editor's tile settings name the readings a tile shows and switch them on and off, and the Look tab picks a
/// widget's background colour (approved mock-up XcuVN6VYHFqAbEb33kYRYx, 2026-11-12). A reading that is a sensor is
/// renamed for the whole app: the same value as renaming it in All sensors, and a cleared name is its own again.
/// </summary>
[Collection("UI")]
public sealed class WidgetEditorNamesTests
{
    private const string CpuTemp = "/cpu/temperature/0", CpuLoad = "/cpu/load/0", Fan1 = "/lpc/fan/0", Fan2 = "/lpc/fan/1";

    private static (SettingsModel Settings, LiveData Live, WidgetEditorViewModel Editor) Open()
    {
        var start = SeedData.QuietSettings();
        start.Widgets.Add(new WidgetConfig
        {
            Style = WidgetStyle.Custom, Id = "custom-names", Name = "Names", Enabled = true,
            Tiles =
            [
                new WidgetTile { Kind = WidgetTiles.Reading, Item = nameof(OverlayMetric.CpuTemp), W = 4, H = 2 },
                new WidgetTile { Kind = WidgetTiles.Line, Y = 2, W = 8, H = 1, Items = [new WidgetItem { Id = nameof(OverlayMetric.CpuTemp) }, new WidgetItem { Id = WidgetCatalog.SensorPrefix + CpuLoad }] },
                new WidgetTile { Kind = WidgetTiles.Fans, Y = 3, W = 6, H = 3 },
                new WidgetTile { Kind = WidgetTiles.Drives, Y = 6, W = 6, H = 3 },
                new WidgetTile { Kind = WidgetTiles.Clock, Y = 9, W = 4, H = 2 },
                new WidgetTile { Kind = WidgetTiles.CpuGauge, X = 6, Y = 3, W = 3, H = 3 },
            ],
        });
        var (settings, live) = Kit.Greeted(start);
        // Every fan turning, so the Fans page (and the tile) lists them.
        Ui.Run(() => live.ApplyTick(Pc.Tick(TimeUtil.NowUnixMs(), 900)));
        var editor = Ui.Run(() => new WidgetEditorViewModel("custom-names", settings, live, () => { }));
        return (settings, live, editor);
    }

    private static WidgetConfig Saved(SettingsModel s) => s.Current.Widgets.Single(w => w.Id == "custom-names");

    [Fact]
    public void A_reading_renamed_in_the_editor_is_renamed_for_the_whole_app_and_back_when_cleared()
    {
        var (settings, live, ed) = Open();
        Ui.Run(() =>
        {
            ed.SelectTile(0);
            var row = Assert.Single(ed.NameRows);
            Assert.Equal(("CPU", "CPU", true, false), (row.Name, row.Usual, row.CanRename, row.HasSwitch));
            row.Name = "  Package  ";
            Assert.Equal("Package", settings.Current.SensorLabels[CpuTemp]);
            Assert.Equal("Package", live.CpuTemp!.DisplayName); // All sensors says the same
            Assert.Equal("Package", WidgetPictures.DataFrom(live).Names[OverlayMetric.CpuTemp]);
            Assert.Equal("Package", Assert.Single(ed.NameRows).Name);
            Assert.Null(Saved(settings).Tiles![0].Label); // the whole app's name, not the tile's own

            // Renamed in All sensors: the editor shows that name.
            live.CpuTemp.Label = "Die";
            Assert.Equal("Die", settings.Current.SensorLabels[CpuTemp]);
            Assert.Equal("Die", Assert.Single(ed.NameRows).Name);

            // Cleared, or its own name typed back: the rename goes, everywhere.
            ed.NameRows[0].Name = "";
            Assert.False(settings.Current.SensorLabels.ContainsKey(CpuTemp));
            Assert.Equal("CPU", Assert.Single(ed.NameRows).Name);
            Assert.False(WidgetPictures.DataFrom(live).Names.ContainsKey(OverlayMetric.CpuTemp));
            ed.NameRows[0].Name = "Mine";
            ed.NameRows[0].Name = "CPU";
            Assert.False(settings.Current.SensorLabels.ContainsKey(CpuTemp));

            // A gauge names the same reading, the same way.
            ed.SelectTile(5);
            Assert.Equal("CPU", Assert.Single(ed.NameRows).Name);
            ed.NameRows[0].Name = "Package";
            Assert.Equal("Package", settings.Current.SensorLabels[CpuTemp]);
        });
    }

    [Fact]
    public void A_line_of_readings_names_each_reading_and_switches_it_off_and_on()
    {
        var (settings, live, ed) = Open();
        Ui.Run(() =>
        {
            ed.SelectTile(1);
            Assert.Equal("READINGS", ed.NameRowsCaption);
            var rows = ed.NameRows;
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.True(r.HasSwitch && r.CanRemove && r.CanRename && r.Shown));
            Assert.Equal(live.AllSensors.Single(s => s.Id == CpuLoad).DisplayName, rows[1].Name);
            rows[1].Name = "Busy";
            Assert.Equal("Busy", settings.Current.SensorLabels[CpuLoad]);
            ed.NameRows[1].Shown = false;
            Assert.True(Saved(settings).Tiles![1].Items![1].Hidden);
            Assert.False(ed.NameRows[1].Shown);
            ed.NameRows[1].Shown = true;
            Assert.False(Saved(settings).Tiles![1].Items![1].Hidden);
            ed.NameRows[0].RemoveCommand.Execute(null);
            Assert.Single(Saved(settings).Tiles![1].Items!);
        });
    }

    [Fact]
    public void The_fans_tile_lists_the_fans_page_rows_each_with_a_name_and_a_switch()
    {
        var (settings, live, ed) = Open();
        Ui.Run(() =>
        {
            ed.SelectTile(2);
            var rows = ed.NameRows;
            Assert.Equal(WidgetPictures.FansFrom(live)!.Select(f => f.Name), rows.Select(r => r.Name));
            Assert.Contains(rows, r => r.Group == "Motherboard");
            var board = rows.First(r => r.Name == "Fan #1");
            board.Name = "Front intake";
            Assert.Equal("Front intake", settings.Current.SensorLabels[Fan1]);
            Assert.Contains("Front intake", WidgetPictures.FansFrom(live)!.Select(f => f.Name));
            ed.NameRows.First(r => r.Name == "Fan #2").Shown = false;
            Assert.Equal([Fan2], Saved(settings).Tiles![2].FansHidden);
            Assert.False(ed.NameRows.First(r => r.Name == "Fan #2").Shown);
            ed.NameRows.First(r => r.Name == "Fan #2").Shown = true;
            Assert.Null(Saved(settings).Tiles![2].FansHidden); // all shown: nothing kept
        });
    }

    [Fact]
    public void Drives_switch_on_and_off_and_a_clock_has_no_names()
    {
        var (settings, _, ed) = Open();
        Ui.Run(() =>
        {
            ed.SelectTile(3);
            var rows = ed.NameRows;
            Assert.All(rows, r => Assert.False(r.CanRename)); // a volume isn't a sensor: its letter stays
            if (rows.Count > 0)
            {
                rows[0].Shown = false;
                Assert.Single(Saved(settings).Tiles![3].DrivesHidden!);
            }
            ed.SelectTile(4);
            Assert.False(ed.HasNameRows);
            Assert.Empty(ed.NameRows);
        });
    }

    [Fact]
    public void The_background_is_the_themes_until_a_colour_is_picked()
    {
        var (settings, _, ed) = Open();
        Ui.Run(() =>
        {
            ed.SelectTile(-1);
            Assert.Equal("theme", ed.BackgroundKind);
            Assert.False(ed.HasOwnBackground);
            ed.BackgroundKind = "own";
            Assert.Equal(WidgetEditorViewModel.BackgroundColors[1].Hex, Saved(settings).BackgroundColor);
            ed.SetBackground("#e9edf2");
            Assert.Equal("#E9EDF2", Saved(settings).BackgroundColor);
            ed.BackgroundKind = "theme";
            Assert.Null(Saved(settings).BackgroundColor);
            ed.BackgroundKind = "own"; // the one picked last comes back
            Assert.Equal("#E9EDF2", Saved(settings).BackgroundColor);
            ed.AddBackgroundCustom("#123456");
            Assert.Contains("#123456", settings.Current.WidgetBackgroundColors);
            ed.UndoCommand.Execute(null);
            Assert.Null(Saved(settings).BackgroundColor);
        });
    }

    [Fact]
    public void The_new_settings_survive_a_save_and_an_old_file_starts_on_the_theme_with_every_fan()
    {
        var s = SeedData.QuietSettings();
        s.WidgetBackgroundColors = ["#123456", "nope", "#123456"];
        s.Widgets.Add(new WidgetConfig
        {
            Style = WidgetStyle.Custom, Id = "custom-saved", BackgroundColor = "#1b2a3a",
            Tiles =
            [
                new WidgetTile { Kind = WidgetTiles.Fans, W = 6, H = 3, FansHidden = ["/a", " ", "/a"] },
                new WidgetTile { Kind = WidgetTiles.Drives, Y = 3, W = 6, H = 3, DrivesHidden = ["E", "e"] },
                new WidgetTile { Kind = WidgetTiles.Line, Y = 6, W = 8, H = 1, Items = [new WidgetItem { Id = nameof(OverlayMetric.CpuTemp), Hidden = true }] },
                new WidgetTile { Kind = WidgetTiles.Clock, Y = 7, W = 4, H = 2, FansHidden = ["/x"] },
            ],
        });
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        var w = back.Widgets.Single(x => x.Id == "custom-saved");
        Assert.Equal("#1B2A3A", w.BackgroundColor);
        Assert.Equal(["/a"], w.Tiles![0].FansHidden);
        Assert.Single(w.Tiles[1].DrivesHidden!);
        Assert.True(w.Tiles[2].Items![0].Hidden);
        Assert.Null(w.Tiles[3].FansHidden); // only a Fans tile keeps it
        Assert.Equal(["#123456"], back.WidgetBackgroundColors);

        // A file from before: none of these, so every widget looks as it did.
        var node = System.Text.Json.Nodes.JsonNode.Parse(SettingsStore.Serialize(s))!;
        static void Strip(System.Text.Json.Nodes.JsonNode? n)
        {
            if (n is System.Text.Json.Nodes.JsonObject o)
            {
                foreach (var key in o.Select(kv => kv.Key).Where(k => k is "BackgroundColor" or "FansHidden" or "DrivesHidden" or "Hidden" or "WidgetBackgroundColors").ToList()) o.Remove(key);
                foreach (var kv in o.ToList()) Strip(kv.Value);
            }
            else if (n is System.Text.Json.Nodes.JsonArray a) foreach (var x in a) Strip(x);
        }
        Strip(node);
        var old = SettingsStore.Deserialize(node.ToJsonString());
        var ow = old.Widgets.Single(x => x.Id == "custom-saved");
        Assert.Null(ow.BackgroundColor);
        Assert.Null(ow.Tiles![0].FansHidden);
        Assert.False(ow.Tiles[2].Items![0].Hidden);
        Assert.All(old.Widgets, x => Assert.Null(x.BackgroundColor));
        // An odd colour from a hand edit: the theme's.
        s.Widgets[^1].BackgroundColor = "blue";
        Assert.Null(SettingsStore.Deserialize(SettingsStore.Serialize(s)).Widgets.Single(x => x.Id == "custom-saved").BackgroundColor);
    }
}

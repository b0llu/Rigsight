using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Taskbar page: which readings go in the taskbar, in what order, and how: a strip, an icon each, or an icon per
/// part, at either end, on tiles, each reading with its look and colours.
/// </summary>
[Collection("UI")]
public sealed class TaskbarPageTests
{
    private static (TaskbarViewModel Page, Services.SettingsModel Settings, ViewModels.LiveData Live) Make(RigsightSettings? start = null)
    {
        var (settings, live) = Kit.Greeted(start);
        return (Ui.Run(() => new TaskbarViewModel(settings, live)), settings, live);
    }

    /// <summary>A brush's colour without its alpha: a tile is its colour, faint.</summary>
    private static Color Rgb(Brush? b) => b is SolidColorBrush s ? Color.FromRgb(s.Color.R, s.Color.G, s.Color.B) : Colors.Transparent;

    [Fact]
    public void Starts_empty_in_the_strip_on_tiles_at_standard_width()
    {
        var (page, _, _) = Make();
        Ui.Run(() =>
        {
            Assert.False(page.HasSensors);
            Assert.True(page.InStrip);
            Assert.False(page.Separate || page.Grouped);
            Assert.Equal(TaskbarViewModel.StripSupported, page.ShowStrip);
            Assert.Empty(page.PreviewIcons);
            Assert.Empty(page.PreviewStrip);
            Assert.NotEmpty(page.AllSensors);
            Assert.Equal(("Tiles", "Standard"), (page.Background, page.WidthText));
            Assert.True(page.ShowCommon); // the common readings first in the Add list
            Assert.Equal("", page.WaitingText);
        });
    }

    [Fact]
    public void Grayscale_to_start_and_the_preview_follows_the_colors_switch()
    {
        var (page, settings, _) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "GPU temperature"));
            Assert.Equal(("Grayscale", true), (page.Colors, settings.Current.TrayGrayscale));
            var grey = TaskbarViewModel.MarkBrush(TrayPart.Other, page.TaskbarLight);
            Assert.Equal(grey, page.PreviewStrip.Single().Readings[0].LabelBrush);
            page.Separate = true;
            Assert.Equal(Rgb(grey), Rgb(page.PreviewIcons.Single().Tile)); // an icon's tile in its colour (grey in grayscale)

            page.Colors = "Color";
            Assert.False(settings.Current.TrayGrayscale);
            Assert.Equal(Rgb(TaskbarViewModel.MarkBrush(TrayPart.Gpu, page.TaskbarLight)), Rgb(page.PreviewIcons.Single().Tile));
            page.InStrip = true;
            Assert.Equal(TaskbarViewModel.MarkBrush(TrayPart.Gpu, page.TaskbarLight), page.PreviewStrip.Single().Readings[0].LabelBrush);

            page.Colors = "Grayscale";
            Assert.True(settings.Current.TrayGrayscale);
        });
    }

    [Fact]
    public void Readings_are_added_from_the_list_once_each_with_no_limit_and_kept_in_order()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            var picks = live.PickableSensors.Take(7).ToList();
            foreach (var s in picks) page.AddReadingCommand.Execute(s);
            Assert.Equal(picks.Select(s => s.Id), settings.Current.TraySensors);
            Assert.Equal(picks.Select(s => s.Id), page.Sensors.Select(r => r.Id));
            Assert.All(page.Sensors, r => Assert.True(r.Found));
            // The list ticks the ones already there; adding one again does nothing.
            Assert.All(page.AddList.Where(i => i.Sensor is { } s && picks.Contains(s)), i => Assert.False(i.CanAdd));
            page.AddReadingCommand.Execute(picks[0]);
            Assert.Equal(7, settings.Current.TraySensors.Count);

            page.MoveSensorDownCommand.Execute(page.Sensors[0]);
            Assert.Equal([picks[1].Id, picks[0].Id], settings.Current.TraySensors.Take(2));
            page.MoveSensorUpCommand.Execute(page.Sensors[0]); // already first: nothing
            Assert.Equal(picks[1].Id, settings.Current.TraySensors[0]);
            page.RemoveSensorCommand.Execute(page.Sensors[^1]);
            Assert.Equal(6, settings.Current.TraySensors.Count);
            Assert.DoesNotContain(picks[6].Id, settings.Current.TraySensors);
        });
    }

    [Fact]
    public void The_add_list_has_every_sensor_under_its_part_and_finds_what_is_typed()
    {
        var (page, _, _) = Make();
        Ui.Run(() =>
        {
            var all = page.AddList;
            Assert.True(all[0].IsHeading);
            Assert.Equal("CPU", all[0].Heading); // the parts in order, the CPU first
            Assert.Equal(page.AllSensors.Count, all.Count(i => !i.IsHeading));

            page.AddSearch = "GPU";
            Assert.False(page.ShowCommon); // typing: no pinned picks, just what matches
            Assert.NotEmpty(page.AddList);
            Assert.All(page.AddList.Where(i => !i.IsHeading), i => Assert.Contains("GPU",
                i.Sensor!.DisplayName + "|" + i.Sensor.HardwareShown + "|" + TrayParts.Label(TrayParts.PartOf(i.Sensor.HardwareType)) + "|" + i.TypeLabel,
                StringComparison.OrdinalIgnoreCase));

            page.AddSearch = "no such sensor anywhere";
            Assert.True(page.AddListEmpty);
        });
    }

    [Fact]
    public void Grouped_the_preview_has_an_icon_per_part_on_a_tile_in_its_colour()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            foreach (var label in new[] { "CPU load", "GPU temperature", "RAM in use", "CPU temperature", "GPU load" })
                page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == label));
            page.Colors = "Color";
            page.Separate = true;
            Assert.Equal(TrayStyle.Icons, settings.Current.TrayStyle);
            Assert.True(page.ShowIcons);
            Assert.Equal(["CPU", "GPU", "RAM", "CPU", "GPU"], page.PreviewIcons.Select(i => i.Label)); // an icon each, as listed

            page.Grouped = true;
            Assert.Equal(TrayStyle.Grouped, settings.Current.TrayStyle);
            Assert.False(page.Separate || page.InStrip);
            var icons = page.PreviewIcons;
            Assert.Equal(["CPU", "GPU", "RAM"], icons.Select(i => i.Label));
            Assert.Equal([live.CpuTemp!.Id, live.CpuLoad!.Id], icons[0].Rows.Select(r => r.Id)); // the temperature on top
            Assert.Equal([live.GpuTemp!.Id, live.GpuLoad!.Id], icons[1].Rows.Select(r => r.Id));
            Assert.Equal((true, false), (icons[0].HasSecond, icons[2].HasSecond));
            Assert.Equal(Rgb(TaskbarViewModel.MarkBrush(TrayPart.Cpu, page.TaskbarLight)), Rgb(icons[0].Tile));
            Assert.All(icons, i => Assert.True(i.ShowLabel)); // each part named once
            Assert.StartsWith(live.CpuTemp.HardwareName, icons[0].Tooltip);
            Assert.Equal(TrayPart.Gpu, page.Sensors.Single(r => r.Id == live.GpuLoad.Id).Part);

            // No tiles: the numbers alone.
            page.Background = "None";
            Assert.False(settings.Current.TrayTiles);
            Assert.All(page.PreviewIcons, i => Assert.Null(i.Tile));

            page.Separate = true;
            Assert.Equal(TrayStyle.Icons, settings.Current.TrayStyle);
            Assert.Equal(5, page.PreviewIcons.Count);
            // A part's icons side by side: named once, under the first.
            page.MoveSensorUpCommand.Execute(page.Sensors[3]);
            page.MoveSensorUpCommand.Execute(page.Sensors[2]);
            Assert.Equal(["CPU", "CPU", "GPU", "RAM", "GPU"], page.PreviewIcons.Select(i => i.Label));
            Assert.Equal([true, false, true, true, true], page.PreviewIcons.Select(i => i.ShowLabel));
        });
    }

    [Fact]
    public void The_strip_preview_has_each_part_once_with_all_its_readings_and_its_name()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            foreach (var label in new[] { "GPU load", "CPU temperature", "RAM in use", "GPU temperature", "CPU load", "GPU power" })
                page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == label));
            page.Colors = "Color";
            Assert.True(page.InStrip);
            var parts = page.PreviewStrip;
            Assert.Equal(["GPU", "CPU", "RAM"], parts.Select(p => p.Readings[0].Label));
            Assert.Equal([live.GpuTemp!.Id, live.GpuLoad!.Id, live.GpuPower!.Id], parts[0].Readings.Select(r => r.Row.Id)); // temperature, load, the rest
            Assert.Equal(TaskbarViewModel.MarkBrush(TrayPart.Gpu, page.TaskbarLight), parts[0].Readings[0].LabelBrush);
            Assert.StartsWith(live.GpuTemp.HardwareName, parts[0].Tooltip);
            Assert.Equal(TaskbarViewModel.StripSupported ? "" : "The strip needs Windows 11, so here the readings show as an icon each.", page.StripNote);
            // On tiles at Standard: 3 apart within a part, 8 between parts, none after the last.
            Assert.Equal([3.0, 3.0, 8.0], parts[0].Readings.Select(r => r.Gap));
            Assert.Equal(0, parts[^1].Readings[^1].Gap);
            Assert.All(parts.SelectMany(p => p.Readings), r => Assert.NotNull(r.Tile));

            page.Grouped = true; // and back to the strip from the page
            page.InStrip = true;
            Assert.Equal(TrayStyle.Strip, settings.Current.TrayStyle);
        });
    }

    [Fact]
    public void Width_and_background_are_saved_and_change_the_strip()
    {
        var (page, settings, _) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "CPU temperature"));
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "GPU temperature"));
            Assert.Equal(TaskbarViewModel.StripSupported, page.ShowWidth);

            page.WidthText = "Compact";
            Assert.Equal(TrayWidth.Compact, settings.Current.TrayWidth);
            Assert.Equal(TrayWidth.Compact, page.PreviewStrip[0].Readings[0].WidthMode);
            Assert.Equal(6, page.PreviewStrip[0].Readings[0].Gap); // tighter between parts
            page.WidthText = "Roomy";
            Assert.Equal(TrayWidth.Roomy, settings.Current.TrayWidth);
            page.WidthText = "nonsense";
            Assert.Equal(TrayWidth.Roomy, settings.Current.TrayWidth);

            page.Background = "None";
            Assert.False(settings.Current.TrayTiles);
            Assert.All(page.PreviewStrip.SelectMany(p => p.Readings), r => Assert.Null(r.Tile));
            Assert.Equal(16, page.PreviewStrip[0].Readings[0].Gap); // today's spacing without tiles
            page.Background = "Tiles";
            Assert.True(settings.Current.TrayTiles);

            page.Separate = true; // only the strip has a width
            Assert.False(page.ShowWidth);
        });
    }

    [Fact]
    public void The_preview_writes_readings_as_the_strip_does()
    {
        var convert = new Converters.TrayTextConverter();
        Assert.Equal("62°", convert.Convert([SensorKind.Temperature, (double?)61.6], typeof(string), "strip", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("62", convert.Convert([SensorKind.Temperature, (double?)61.6], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        var fill = new Converters.TrayFillConverter();
        Assert.Equal(0.62, (double)fill.Convert((double?)62, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal(1.0, (double)fill.Convert((double?)140, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.0, (double)fill.Convert(null, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_usual_readings_are_one_click_and_leave_the_list_once_added()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Assert.True(page.HasQuickPicks);
            var labels = page.QuickPicks.Select(p => p.Label).ToList();
            Assert.Contains("GPU temperature", labels);
            Assert.Contains("RAM in use", labels);
            var gpu = page.QuickPicks.Single(p => p.Label == "GPU temperature");
            Assert.Same(live.GpuTemp, gpu.Sensor);
            page.AddQuickCommand.Execute(gpu);
            Assert.Equal([live.GpuTemp!.Id], settings.Current.TraySensors);
            Assert.DoesNotContain(page.QuickPicks, p => p.Label == "GPU temperature");
            Assert.Equal("1 in the taskbar", page.CountText);
        });
    }

    [Fact]
    public void A_sensor_this_pc_doesnt_have_is_listed_as_not_found()
    {
        var (page, _, _) = Make(new RigsightSettings { TraySensors = ["/no/such/sensor"] });
        Ui.Run(() =>
        {
            var row = Assert.Single(page.Sensors);
            Assert.False(row.Found);
            Assert.False(row.ShowLookButton);
            Assert.Equal("Not found on this PC right now", row.Hardware);
            Assert.Empty(page.PreviewIcons); // nothing to draw for it
            Assert.Equal(TrayPart.Other, row.Part);
        });
    }

    [Fact]
    public void Every_style_goes_to_the_left_side_and_back()
    {
        var (page, settings, _) = Make();
        Ui.Run(() =>
        {
            Assert.Equal(("Right", false), (page.SideText, page.OnLeft)); // next to the clock to start
            Assert.Equal(TaskbarViewModel.StripSupported, page.ShowPlace);
            foreach (var style in new[] { TrayStyle.Strip, TrayStyle.Icons, TrayStyle.Grouped })
            {
                page.Style = style;
                page.SideText = "Left";
                Assert.Equal(TraySide.Left, settings.Current.TraySide);
                Assert.True(page.OnLeft);
                Assert.Equal(TaskbarViewModel.StripSupported && !page.IconsAlignedLeft, page.PreviewOnLeft);
                // Said only when Windows' icons fill the left end.
                Assert.Equal(TaskbarViewModel.StripSupported && page.IconsAlignedLeft, page.SideNote.Length > 0);
                page.SideText = "Right";
                Assert.Equal(TraySide.Right, settings.Current.TraySide);
                Assert.Equal("", page.SideNote);
            }
        });
    }

    [Fact]
    public void Each_reading_on_the_strip_has_a_look_picked_from_its_list()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "CPU temperature"));
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "GPU power"));
            var (temp, power) = (page.Sensors[0], page.Sensors[1]);
            Assert.Equal((TrayLook.Number, TrayLook.Number), (temp.Look, power.Look)); // numbers alone to start
            Assert.Equal(("Number only", TaskbarViewModel.StripSupported), (temp.LookName, temp.ShowLookButton));
            Assert.Equal(7, temp.Looks.Count); // a temperature has a top: every look
            Assert.Equal([TrayLook.Number, TrayLook.Area, TrayLook.Line, TrayLook.Bars], power.Looks.Select(l => l.Look)); // watts don't

            temp.PickLookCommand.Execute(TrayLook.Ring);
            Assert.Equal(TrayLook.Ring, settings.Current.TrayLooks[live.CpuTemp!.Id]);
            Assert.Equal(("Ring", true), (temp.LookName, temp.Looks.Single(l => l.Look == TrayLook.Ring).Selected));
            Assert.Equal(TrayLook.Ring, page.PreviewStrip.Single(p => p.Readings[0].Row == temp).Readings[0].Look);

            power.PickLookCommand.Execute(TrayLook.Bars);
            Assert.Equal(TrayLook.Bars, settings.Current.TrayLooks[live.GpuPower!.Id]);
            temp.PickLookCommand.Execute(TrayLook.Number); // back to the number: nothing kept for it
            Assert.False(settings.Current.TrayLooks.ContainsKey(live.CpuTemp.Id));

            // As icons there's no look to draw: no button.
            page.Separate = true;
            Assert.False(temp.ShowLookButton);
        });
    }

    [Fact]
    public void A_look_picked_from_the_taskbar_shows_on_the_page()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "GPU temperature"));
            settings.Update(s => s.TrayLooks[live.GpuTemp!.Id] = TrayLook.Blocks); // as the agent's right-click menu does
            page.LoadSensors();
            Assert.Equal(TrayLook.Blocks, page.Sensors.Single().Look);
        });
    }

    [Fact]
    public void A_tile_colour_is_saved_with_the_others_and_shows_on_the_preview()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "CPU load"));
            page.Colors = "Color";
            var row = page.Sensors.Single();
            page.SetColors(row, null, null, "#F472B6");
            Assert.Equal("#F472B6", settings.Current.TrayColors[live.CpuLoad!.Id].Tile);
            Assert.Equal(Color.FromRgb(0xF4, 0x72, 0xB6), Rgb(page.PreviewStrip.Single().Readings[0].Tile));
            Assert.True(row.HasTileColor);
            page.Separate = true;
            Assert.Equal(Color.FromRgb(0xF4, 0x72, 0xB6), Rgb(page.PreviewIcons.Single().Tile));

            page.AddCustomColor("tile", "#123456");
            Assert.Equal(["#123456"], settings.Current.TrayTileColors);

            page.SetColors(row, null, null, null); // all back to RigSight's
            Assert.False(settings.Current.TrayColors.ContainsKey(live.CpuLoad.Id));
        });
    }

    [Fact]
    public void Readings_waiting_for_room_are_named_over_the_list()
    {
        var (page, _, live) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "CPU temperature"));
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "GPU temperature"));
            var tick = Pc.Tick(1000);
            tick.TrayWaiting = [live.GpuTemp!.Id];
            live.ApplyTick(tick);
            Assert.Contains("1 reading doesn't fit", page.WaitingText);
            Assert.Contains(page.Sensors[1].Name, page.WaitingText);

            live.ApplyTick(Pc.Tick(2000)); // all fit again
            Assert.Equal("", page.WaitingText);
        });
    }

    [Fact]
    public void A_graph_from_before_becomes_a_filled_graph_and_tiles_start_on()
    {
        // Settings from before 1.2: a graph behind a reading, nothing about tiles.
        var s = SettingsStore.Deserialize("""{ "SettingsVersion": 11, "TraySensors": ["/cpu/0/load/0", "/gpu/0/temperature/0"], "TrayGraphs": ["/cpu/0/load/0"] }""");
        Assert.Equal(TrayLook.Area, s.TrayLooks["/cpu/0/load/0"]);
        Assert.False(s.TrayLooks.ContainsKey("/gpu/0/temperature/0"));
        Assert.Empty(s.TrayGraphs);
        Assert.True(s.TrayTiles);
        Assert.Equal(TrayWidth.Standard, s.TrayWidth);
        // Saved and read again: the same.
        var again = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(TrayLook.Area, again.TrayLooks["/cpu/0/load/0"]);
        // Nothing is added for someone who had no readings.
        Assert.Empty(SettingsStore.Deserialize("""{ "SettingsVersion": 11 }""").TraySensors);
    }

    [Fact]
    public void Two_rows_pair_the_readings_as_listed_and_one_can_have_a_box_of_its_own()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            foreach (var label in new[] { "CPU temperature", "GPU temperature", "CPU load", "GPU load" })
                page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == label));
            page.Colors = "Color";
            page.TwoRows = true;
            Assert.Equal(TrayStyle.TwoRows, settings.Current.TrayStyle);
            Assert.False(page.InStrip || page.Separate || page.Grouped);
            Assert.Equal(TaskbarViewModel.StripSupported, page.ShowBoxes);
            Assert.False(page.ShowWidth); // no graphs in two rows
            Assert.All(page.Sensors, r => Assert.Equal(page.ShowBoxes, r.InBoxes));
            Assert.All(page.Sensors, r => Assert.False(r.ShowLookButton));

            // Two to a box, in the list's order: whatever parts they are.
            string Boxes() => string.Join(" | ", page.Boxes.Select(b => string.Join(" ", b.Lines.Select(l => l.Label))));
            Assert.Equal("CPU GPU | CPU GPU", Boxes());
            Assert.Equal([live.CpuTemp!.Id, live.GpuTemp!.Id], page.Boxes[0].Rows.Select(r => r.Id));
            Assert.Equal(["Box 1", "Box 2"], page.Boxes.Select(b => b.Title));
            Assert.All(page.Boxes, b => Assert.NotNull(b.Tile));

            // Moved in the list: the CPU's two share a box, its load saying what it is.
            page.MoveSensorDownCommand.Execute(page.Sensors[1]);
            Assert.Equal("CPU Load | GPU Load", Boxes());

            // On its own: a box alone, and the rest pair on after it.
            page.Sensors[0].Alone = true;
            Assert.Equal([live.CpuTemp.Id], settings.Current.TrayAlone);
            Assert.Equal("CPU | CPU GPU | GPU", Boxes());
            Assert.Equal(["Box 1 · on its own", "Box 2", "Box 3 · one reading"], page.Boxes.Select(b => b.Title));

            // A colour picked keeps the boxes (the picker floats from a button in them); the tile changes in place.
            var box = page.Boxes[0];
            page.SetColors(page.Sensors[0], null, null, "#FF00AA");
            Assert.Same(box, page.Boxes[0]);
            Assert.Equal(Color.FromRgb(0xFF, 0x00, 0xAA), Rgb(box.Tile));

            page.NamesText = "Letter";
            Assert.True(settings.Current.TrayLetterNames);
            Assert.Equal("C | C G | G", Boxes());

            page.Sensors[0].Alone = false;
            Assert.Empty(settings.Current.TrayAlone);
            Assert.Equal("C L | G L", Boxes());
        });
    }

    [Fact]
    public void The_preview_shows_what_the_taskbar_will()
    {
        // The same short numbers as the real icons (the agent's drawing uses Units.TrayText too).
        Assert.Equal("62", new Converters.TrayTextConverter().Convert([SensorKind.Temperature, (double?)61.6], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("–", new Converters.TrayTextConverter().Convert([SensorKind.Load, null!], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("–", new Converters.TrayTextConverter().Convert([], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}

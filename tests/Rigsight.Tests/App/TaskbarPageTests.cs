using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Taskbar page (1.3): a row per spot (one reading, or two sharing it), each spot with its own style, size, colors,
/// background and names, each reading its look and colours; the whole taskbar's colours, background, place, width and
/// speeds; and the spots made from settings exactly as the agent makes them.
/// </summary>
[Collection("UI")]
public sealed class TaskbarPageTests
{
    private static (TaskbarViewModel Page, Services.SettingsModel Settings, ViewModels.LiveData Live) Make(RigsightSettings? start = null)
    {
        var (settings, live) = Kit.Greeted(start);
        return (Ui.Run(() => new TaskbarViewModel(settings, live)), settings, live);
    }

    private static void Quick(TaskbarViewModel page, string label) => page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == label));

    private static string[] SpotIds(TaskbarViewModel page) => [.. page.Spots.Select(s => string.Join("+", s.Rows.Select(r => r.Id)))];

    [Fact]
    public void Starts_empty_on_tiles_at_standard_width()
    {
        var (page, _, _) = Make();
        Ui.Run(() =>
        {
            Assert.False(page.HasSensors);
            Assert.Empty(page.Spots);
            Assert.Null(page.PreviewImage);
            Assert.NotEmpty(page.AllSensors);
            Assert.Equal(("Tiles", "Standard"), (page.Background, page.WidthText));
            Assert.True(page.ShowCommon); // the common readings first in the Add list
            Assert.Equal("", page.WaitingText);
        });
    }

    [Fact]
    public void Readings_are_added_once_each_in_order_and_spots_move_and_go()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            var picks = live.PickableSensors.Take(5).ToList();
            foreach (var s in picks) page.AddReadingCommand.Execute(s);
            Assert.Equal(picks.Select(s => s.Id), settings.Current.TraySensors);
            Assert.Equal(picks.Select(s => s.Id), page.Sensors.Select(r => r.Id));
            Assert.Equal(5, page.Spots.Count); // added one by one: a spot each
            Assert.All(page.Sensors, r => Assert.True(r.Found));
            Assert.NotNull(settings.Current.TrayPairs); // the page saves its spots from the first change
            // The list ticks the ones already there; adding one again does nothing.
            Assert.All(page.AddList.Where(i => i.Sensor is { } s && picks.Contains(s)), i => Assert.False(i.CanAdd));
            page.AddReadingCommand.Execute(picks[0]);
            Assert.Equal(5, settings.Current.TraySensors.Count);

            page.MoveSpot(page.Spots[0], 2);
            Assert.Equal([picks[1].Id, picks[2].Id, picks[0].Id], settings.Current.TraySensors.Take(3));
            page.MoveSpot(page.Spots[0], -3); // already first: nothing
            Assert.Equal(picks[1].Id, settings.Current.TraySensors[0]);
            page.Spots[^1].RemoveCommand.Execute(null);
            Assert.Equal(4, settings.Current.TraySensors.Count);
            Assert.DoesNotContain(picks[4].Id, settings.Current.TraySensors);
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
    public void Load_and_temperature_with_one_click_share_a_spot_that_can_part()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Quick(page, "CPU load and temperature");
            Assert.Equal([live.CpuTemp!.Id, live.CpuLoad!.Id], settings.Current.TraySensors); // the temperature first
            Assert.Equal([live.CpuTemp.Id], settings.Current.TrayPairs);
            var spot = Assert.Single(page.Spots);
            Assert.True(spot.IsPair);
            Assert.Equal("CPU temperature and load", spot.Title);
            Assert.Equal("CPU load", spot.PartnerText);
            Assert.Equal(("Strip", "Side by side"), (spot.ShowsText, spot.StripWord));
            Assert.Equal(2, spot.LookRows.Count); // side by side: a look each

            spot.ShowsText = "Icons"; // the whole spot: both readings
            Assert.All(page.Sensors, r => Assert.Equal(TrayStyle.Icons, settings.Current.TrayStyles[r.Id]));
            spot = Assert.Single(page.Spots); // still one icon of two
            Assert.Equal([live.CpuTemp.Id], spot.LookRows.Select(r => r.Id)); // the icon's look is its first's

            spot.ShowsText = "TwoRows";
            spot = Assert.Single(page.Spots);
            Assert.Empty(spot.LookRows); // two rows have no looks
            Assert.True(spot.ShowNames);

            page.SetPartner(spot, null); // None: a spot each
            Assert.Empty(settings.Current.TrayPairs!);
            Assert.Equal(2, page.Spots.Count);
            Assert.All(page.Spots, s => Assert.False(s.IsPair));
        });
    }

    [Fact]
    public void A_spot_paired_with_another_reading_takes_it_next_to_it_with_its_settings()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Quick(page, "CPU temperature");
            Quick(page, "RAM in use");
            Quick(page, "GPU load");
            page.SetSize(page.Spots[0], TraySize.Small);
            page.SetTiles(page.Spots[0], false);
            var items = page.PartnerItems(page.Spots[0], "");
            Assert.Equal("None", items[0].Name);
            Assert.True(items[0].Selected);
            // Two readings named "Memory" on this PC (RAM's and virtual memory's): the one in the taskbar says so, the other
            // its hardware, so the two can be told apart.
            Assert.Equal(["In the taskbar", "Virtual Memory"], items.Where(i => i.IsChoice && i.Name == live.RamLoad!.DisplayName).Select(i => i.Detail));
            Assert.DoesNotContain(items, i => i.IsChoice && i.Name == live.CpuTemp!.DisplayName); // not itself
            Assert.All(page.PartnerItems(page.Spots[0], "GPU").Where(i => i.IsChoice), i => Assert.NotEqual("None", i.Name)); // typing: no None

            items.Single(i => i.Id == live.GpuLoad!.Id).Pick!();
            Assert.Equal([live.CpuTemp!.Id, live.GpuLoad!.Id, live.RamLoad!.Id], settings.Current.TraySensors);
            Assert.Equal([live.CpuTemp.Id], settings.Current.TrayPairs);
            Assert.Equal(TraySize.Small, settings.Current.TraySizes[live.GpuLoad.Id]); // it took the spot's size
            Assert.False(settings.Current.TrayTilesOf[live.GpuLoad.Id]); // and its background
            var pair = page.Spots[0];
            Assert.True(pair.IsPair);
            Assert.Equal("CPU temperature and GPU load", pair.Title); // two parts: both named
            Assert.Equal("GPU load", pair.PartnerText);
            // A reading already in a pair can't be picked for another spot.
            Assert.False(page.PartnerItems(page.Spots[1], "").Single(i => i.Id == live.GpuLoad.Id).Enabled);

            // Another one for the same spot: the second leaves, alone where it is.
            page.PartnerItems(page.Spots[0], "").Single(i => i.Id == live.RamLoad.Id).Pick!();
            Assert.Equal([live.CpuTemp.Id, live.RamLoad.Id, live.GpuLoad.Id], settings.Current.TraySensors);
            Assert.Equal([$"{live.CpuTemp.Id}+{live.RamLoad.Id}", live.GpuLoad.Id], SpotIds(page));
        });
    }

    [Fact]
    public void A_reading_not_in_the_taskbar_yet_is_added_by_pairing_it()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Quick(page, "GPU temperature");
            Quick(page, "RAM in use");
            var power = page.PartnerItems(page.Spots[0], "power").Single(i => i.Id == live.GpuPower!.Id);
            Assert.True(power.Enabled);
            Assert.Equal(live.GpuPower!.HardwareShown, power.Detail);
            power.Pick!();
            Assert.Equal([live.GpuTemp!.Id, live.GpuPower.Id, live.RamLoad!.Id], settings.Current.TraySensors);
            Assert.True(page.Spots[0].IsPair);
        });
    }

    [Fact]
    public void Two_of_the_same_kind_say_their_own_names()
    {
        var (page, _, live) = Make();
        Ui.Run(() =>
        {
            var temps = live.PickableSensors.Where(s => s.Kind == SensorKind.Temperature && TrayParts.PartOf(s.HardwareType) == TrayPart.Cpu).Take(2).ToList();
            if (temps.Count < 2) return; // the stand-in PC has one CPU temperature only
            page.AddReadingCommand.Execute(temps[0]);
            page.PartnerItems(page.Spots[0], "").Single(i => i.Id == temps[1].Id).Pick!();
            var pair = page.Spots[0];
            Assert.Equal($"{temps[0].DisplayName} and {temps[1].DisplayName}", pair.Title);
            Assert.Equal([$"Look of {temps[0].DisplayName}", $"Look of {temps[1].DisplayName}"], pair.LookRows.Select(r => r.LookTitle));
        });
    }

    [Fact]
    public void Both_speeds_pair_only_side_by_side()
    {
        var (page, settings, _) = Make();
        Ui.Run(() =>
        {
            Quick(page, "Download and upload");
            Quick(page, "CPU temperature");
            var both = page.Spots[0];
            Assert.True(both.CanPair);
            page.PartnerItems(both, "").Single(i => i.Id == page.Spots[1].First.Id).Pick!();
            Assert.True(page.Spots[0].IsPair);

            page.Spots[0].ShowsText = "TwoRows"; // two lines already: they part
            Assert.Empty(settings.Current.TrayPairs!);
            Assert.All(page.Spots, s => Assert.False(s.IsPair));
            var speeds = page.Spots.Single(s => s.First.Id == TrayParts.NetBoth);
            Assert.False(speeds.CanPair);
            // And a spot in two rows can't take both speeds.
            page.Spots.Single(s => s != speeds).ShowsText = "TwoRows";
            var other = page.Spots.Single(s => s.First.Id != TrayParts.NetBoth);
            Assert.False(page.PartnerItems(other, "").Single(i => i.Id == speeds.First.Id).Enabled);
        });
    }

    [Fact]
    public void Colors_and_background_are_the_whole_taskbars_unless_a_spot_has_its_own()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Quick(page, "CPU load and temperature");
            Quick(page, "GPU temperature");
            page.Colors = "Color";
            var cpu = page.Spots[0];
            Assert.Equal(("Color", false), (cpu.ColorsText, cpu.ColorsOwn));
            Assert.Equal(("Tiles", false), (cpu.BackgroundText, cpu.BackgroundOwn));
            Assert.All(page.Sensors, r => Assert.False(r.IsGray));

            cpu.ColorsText = "Grayscale"; // its own: both of its readings
            Assert.True(settings.Current.TrayGrayOf[live.CpuTemp!.Id]);
            Assert.True(settings.Current.TrayGrayOf[live.CpuLoad!.Id]);
            cpu = page.Spots[0];
            Assert.Equal(("Grayscale", true), (cpu.ColorsText, cpu.ColorsOwn));
            Assert.Contains("grayscale", cpu.Summary);
            Assert.All(cpu.Rows, r => Assert.True(r.IsGray && !r.Colored)); // its colours wait while it's gray
            Assert.False(page.Spots[1].First.IsGray);

            page.Colors = "Grayscale"; // the taskbar's: the one with its own keeps it
            Assert.True(page.Spots[1].First.IsGray);
            page.Spots[0].ColorsText = "Color";
            Assert.False(page.Spots[0].First.IsGray);
            Assert.Contains("grayscale", page.Spots[0].ColorsHint); // the rest of the taskbar is
            page.Spots[0].ResetColorsCommand.Execute(null); // like the rest again
            Assert.Empty(settings.Current.TrayGrayOf);
            Assert.True(page.Spots[0].First.IsGray);
            Assert.False(page.Spots[0].ColorsOwn);

            page.Spots[1].BackgroundText = "None";
            Assert.False(settings.Current.TrayTilesOf[live.GpuTemp!.Id]);
            Assert.True(page.Spots[1].BackgroundOwn);
            Assert.Contains("no tile", page.Spots[1].Summary);
            page.Background = "None";
            Assert.False(settings.Current.TrayTiles);
            Assert.Equal("None", page.Spots[0].BackgroundText);
            page.Spots[1].ResetBackgroundCommand.Execute(null);
            Assert.Empty(settings.Current.TrayTilesOf);
        });
    }

  [Fact]
    public void Names_and_size_are_each_spots()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Quick(page, "GPU load and temperature");
            Quick(page, "CPU temperature");
            page.Spots[0].ShowsText = "TwoRows";
            var box = page.Spots[0];
            Assert.Equal("Full", box.NamesText);
            box.NamesText = "Letter";
            Assert.Equal(new[] { live.GpuTemp!.Id, live.GpuLoad!.Id }.Order(), settings.Current.TrayLetters.Order());
            Assert.Contains("one-letter names", page.Spots[0].Summary);
            Assert.False(page.Spots[1].ShowNames); // not in two rows: no names to set

            page.Spots[1].SizeText = "Large";
            Assert.Equal(TraySize.Large, settings.Current.TraySizes[live.CpuTemp!.Id]);
            Assert.False(settings.Current.TraySizes.ContainsKey(live.GpuTemp.Id));
            page.Spots[1].SizeText = "Auto";
            Assert.Empty(settings.Current.TraySizes);
        });
    }

    [Fact]
    public void Spots_from_before_13_are_as_the_taskbar_showed_them_and_saved_on_the_first_change()
    {
        var (page, settings, live) = Make();
        string gpuT = live.GpuTemp!.Id, gpuL = live.GpuLoad!.Id, cpuT = live.CpuTemp!.Id, ram = live.RamLoad!.Id;
        Ui.Run(() =>
        {
            // As 1.2 left them: no pairs saved, the strip grouping by part (a part's temperature first), names as one letter.
            settings.Update(s =>
            {
                s.TraySensors = [gpuL, cpuT, ram, gpuT];
                s.TrayPairs = null;
                s.TrayLetterNames = true;
            });
            page.LoadSensors();
            Assert.Equal([$"{gpuT}+{gpuL}", cpuT, ram], SpotIds(page));

            page.SetSize(page.Spots[2], TraySize.Small); // the first change: the spots saved as they show
            Assert.Equal([gpuT, gpuL, cpuT, ram], settings.Current.TraySensors);
            Assert.Equal([gpuT], settings.Current.TrayPairs);
            Assert.False(settings.Current.TrayLetterNames);
            Assert.Equal(4, settings.Current.TrayLetters.Count);
            Assert.Equal([$"{gpuT}+{gpuL}", cpuT, ram], SpotIds(page));

            // Two rows from before 1.3: two to a box in the list's order, one with a box of its own alone.
            settings.Update(s =>
            {
                s.TraySensors = [cpuT, gpuT, gpuL];
                s.TrayPairs = null;
                s.TrayStyle = TrayStyle.TwoRows;
                s.TrayStyles.Clear();
                s.TraySizes.Clear();
                s.TrayAlone = [cpuT];
            });
            page.LoadSensors();
            Assert.Equal([cpuT, $"{gpuT}+{gpuL}"], SpotIds(page));
        });
    }

    [Fact]
    public void Each_reading_has_a_look_picked_from_its_list()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Quick(page, "CPU temperature");
            Quick(page, "GPU power");
            var (temp, power) = (page.Sensors[0], page.Sensors[1]);
            Assert.Equal((TrayLook.Number, TrayLook.Number), (temp.Look, power.Look)); // numbers alone to start
            Assert.Equal("Number only", temp.LookName);
            Assert.Equal("Look", temp.LookTitle);
            var looks = temp.LookChoices();
            Assert.True(looks.Count > power.LookChoices().Count); // a temperature has a top: more looks than watts
            Assert.True(looks[0].Selected);

            looks.Single(l => l.Name == "Ring").Pick();
            Assert.Equal(TrayLook.Ring, settings.Current.TrayLooks[live.CpuTemp!.Id]);
            Assert.Equal("Ring", page.Sensors[0].LookName);
            Assert.Contains("ring", page.Spots[0].Summary);
            page.Sensors[0].LookChoices().Single(l => l.Name == "Number only").Pick(); // back to the number: nothing kept
            Assert.False(settings.Current.TrayLooks.ContainsKey(live.CpuTemp.Id));

            // As an icon: the icon's own looks.
            page.Spots[0].ShowsText = "Icons";
            Assert.All(page.Sensors[0].LookChoices(), l => Assert.Contains(l.Name, new[] { TrayParts.IconLookName(TrayLook.Number), TrayParts.IconLookName(TrayLook.Level), TrayParts.IconLookName(TrayLook.Ring) }));
        });
    }

    [Fact]
    public void A_look_picked_from_the_taskbar_shows_on_the_page()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Quick(page, "GPU temperature");
            settings.Update(s => s.TrayLooks[live.GpuTemp!.Id] = TrayLook.Blocks); // as the agent's right-click menu does
            page.LoadSensors();
            Assert.Equal(TrayLook.Blocks, page.Sensors.Single().Look);
        });
    }

    [Fact]
    public void A_sensor_this_pc_doesnt_have_is_listed_as_not_found()
    {
        var (page, _, _) = Make(new RigsightSettings { TraySensors = ["/no/such/sensor"] });
        Ui.Run(() =>
        {
            var spot = Assert.Single(page.Spots);
            Assert.False(spot.First.Found);
            Assert.Empty(spot.LookRows);
            Assert.Empty(spot.PickRows);
            Assert.Null(spot.Picture);
            Assert.Equal("Not found on this PC right now", spot.What);
            Assert.Equal(TrayPart.Other, spot.First.Part);
        });
    }

    [Fact]
    public void The_taskbar_goes_to_the_left_side_and_back()
    {
        var (page, settings, _) = Make();
        Ui.Run(() =>
        {
            Assert.Equal(("Right", false), (page.SideText, page.OnLeft)); // next to the clock to start
            Assert.Equal(TaskbarViewModel.StripSupported, page.ShowPlace);
            page.SideText = "Left";
            Assert.Equal(TraySide.Left, settings.Current.TraySide);
            Assert.True(page.OnLeft);
            Assert.Equal(TaskbarViewModel.StripSupported && !page.IconsAlignedLeft, page.PreviewOnLeft);
            // Said only when Windows' icons fill the left end.
            Assert.Equal(TaskbarViewModel.StripSupported && page.IconsAlignedLeft, page.SideNote.Length > 0);
            page.SideText = "Right";
            Assert.Equal(TraySide.Right, settings.Current.TraySide);
            Assert.Equal("", page.SideNote);
        });
    }

    [Fact]
    public void Width_and_speeds_show_with_what_needs_them()
    {
        var (page, settings, _) = Make();
        Ui.Run(() =>
        {
            Quick(page, "CPU temperature");
            Assert.Equal(TaskbarViewModel.StripSupported, page.ShowWidth);
            Assert.False(page.ShowSpeeds);
            page.WidthText = "Compact";
            Assert.Equal(TrayWidth.Compact, settings.Current.TrayWidth);
            page.WidthText = "nonsense";
            Assert.Equal(TrayWidth.Compact, settings.Current.TrayWidth);
            page.Spots[0].ShowsText = "Icons"; // only the strip has a width
            Assert.False(page.ShowWidth);

            Quick(page, "Download and upload");
            Assert.True(page.ShowSpeeds);
            page.SpeedsText = "Bits";
            Assert.True(settings.Current.TraySpeedBits);
        });
    }

    [Fact]
    public void A_tile_colour_is_saved_with_the_others()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            Quick(page, "CPU load");
            page.Colors = "Color";
            var row = page.Sensors.Single();
            page.SetColors(row, null, null, "#F472B6");
            Assert.Equal("#F472B6", settings.Current.TrayColors[live.CpuLoad!.Id].Tile);
            Assert.Equal("#F472B6", row.TileColor);

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
            Quick(page, "CPU temperature");
            Quick(page, "GPU temperature");
            var tick = Pc.Tick(1000);
            tick.TrayWaiting = [live.GpuTemp!.Id];
            live.ApplyTick(tick);
            Assert.Contains("doesn't fit in the taskbar on this screen, so it waits in the +1 tile.", page.WaitingText);
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
        Assert.Null(s.TrayPairs); // no pairs yet: the spots as before 1.3
        // Saved and read again: the same.
        var again = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(TrayLook.Area, again.TrayLooks["/cpu/0/load/0"]);
        Assert.Null(again.TrayPairs);
        // Nothing is added for someone who had no readings.
        Assert.Empty(SettingsStore.Deserialize("""{ "SettingsVersion": 11 }""").TraySensors);
    }

    [Fact]
    public void Pairs_and_each_readings_own_colours_background_and_names_are_kept()
    {
        var s = new RigsightSettings
        {
            TraySensors = ["/a", "/b", "/c"],
            TrayGrayscale = false, // the rest in colour (a new install's default is grayscale)
            TrayPairs = ["/a", "/a", " "],
            TrayGrayOf = new() { ["/a"] = true, [" "] = false },
            TrayTilesOf = new() { ["/c"] = false },
            TrayLetters = ["/b", "/b"],
        };
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(["/a"], back.TrayPairs); // once, and no blanks
        Assert.Equal(new Dictionary<string, bool> { ["/a"] = true }, back.TrayGrayOf);
        Assert.Equal(new Dictionary<string, bool> { ["/c"] = false }, back.TrayTilesOf);
        Assert.Equal(["/b"], back.TrayLetters);
        Assert.Equal((true, false, true), (TrayParts.GrayOf(back, "/a"), TrayParts.GrayOf(back, "/b"), TrayParts.TilesOf(back, "/a")));
        Assert.Equal((false, true), (TrayParts.TilesOf(back, "/c"), TrayParts.LettersOf(back, "/b")));
        Assert.Equal((true, false), (TrayParts.PairNextOf(back, "/a"), TrayParts.PairNextOf(back, "/b")));
        Assert.Null(TrayParts.PairNextOf(new RigsightSettings(), "/a"));
    }

    [Fact]
    public void Spots_pair_as_saved_two_at_most_in_two_rows_and_icons_and_both_speeds_alone_there()
    {
        TrayParts.SpotOf Of(string id, bool? next) => new(id, "g", SensorKind.Load, false, id == TrayParts.NetBoth, next);
        var run = new[] { Of("/a", true), Of("/b", true), Of("/c", false), Of("/d", true), Of(TrayParts.NetBoth, false) };
        string Show(TrayStyle style) => string.Join(" | ", TrayParts.Spots(run, style, r => r).Select(spot => string.Join("+", spot.Select(r => r.Id))));
        Assert.Equal($"/a+/b+/c | /d+{TrayParts.NetBoth}", Show(TrayStyle.Strip)); // the strip: as many as share a tile
        Assert.Equal($"/a+/b | /c | /d | {TrayParts.NetBoth}", Show(TrayStyle.TwoRows));
        Assert.Equal($"/a+/b | /c | /d | {TrayParts.NetBoth}", Show(TrayStyle.Icons));
    }
}

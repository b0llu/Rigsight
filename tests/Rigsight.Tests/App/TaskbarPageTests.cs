using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The Taskbar page: which readings go next to the clock, in what order, and how: a strip, an icon each, or an icon per part.</summary>
[Collection("UI")]
public sealed class TaskbarPageTests
{
    private static (TaskbarViewModel Page, Services.SettingsModel Settings, ViewModels.LiveData Live) Make(RigsightSettings? start = null)
    {
        var (settings, live) = Kit.Greeted(start);
        return (Ui.Run(() => new TaskbarViewModel(settings, live)), settings, live);
    }

    [Fact]
    public void Starts_empty_and_in_the_strip()
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
            Assert.False(page.AddSensorCommand.CanExecute(null)); // nothing picked
        });
    }

    [Fact]
    public void Grayscale_to_start_and_the_preview_follows_the_colors_switch()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "GPU temperature"));
            Assert.Equal(("Grayscale", true), (page.Colors, settings.Current.TrayGrayscale));
            var grey = TaskbarViewModel.MarkBrush(TrayPart.Other, page.TaskbarLight);
            Assert.Equal(grey, page.PreviewStrip.Single().NameBrush);
            Assert.Equal(grey, page.PreviewIcons.Single().Mark);

            page.Colors = "Color";
            Assert.False(settings.Current.TrayGrayscale);
            Assert.Equal(TaskbarViewModel.MarkBrush(TrayPart.Gpu, page.TaskbarLight), page.PreviewStrip.Single().NameBrush);
            Assert.Equal(TaskbarViewModel.MarkBrush(TrayPart.Gpu, page.TaskbarLight), page.PreviewIcons.Single().Mark);

            page.Colors = "Grayscale";
            Assert.True(settings.Current.TrayGrayscale);
        });
    }

    [Fact]
    public void Readings_are_added_once_each_with_no_limit_and_kept_in_order()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            var picks = live.AllSensors.Take(7).ToList();
            foreach (var s in picks)
            {
                page.SensorToAdd = s;
                page.AddSensorCommand.Execute(null);
                Assert.Null(page.SensorToAdd);
            }
            Assert.Equal(picks.Select(s => s.Id), settings.Current.TraySensors);
            Assert.Equal(picks.Select(s => s.Id), page.Sensors.Select(r => r.Id));
            Assert.All(page.Sensors, r => Assert.True(r.Found));

            page.SensorToAdd = picks[0]; // already there
            Assert.False(page.AddSensorCommand.CanExecute(null));

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
    public void Grouped_the_preview_has_an_icon_per_part_marked_with_its_colour()
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
            Assert.Equal((15.0, 30.0), (icons[0].NumberHeight, icons[2].NumberHeight));
            Assert.Equal(TaskbarViewModel.MarkBrush(TrayPart.Cpu, page.TaskbarLight), icons[0].Mark);
            Assert.StartsWith(live.CpuTemp.HardwareName, icons[0].Tooltip);
            Assert.Equal(TrayPart.Gpu, page.Sensors.Single(r => r.Id == live.GpuLoad.Id).Part);

            page.Separate = true;
            Assert.Equal(TrayStyle.Icons, settings.Current.TrayStyle);
            Assert.Equal(5, page.PreviewIcons.Count);
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
            Assert.Equal(["GPU", "CPU", "RAM"], parts.Select(p => p.Name));
            Assert.Equal([live.GpuTemp!.Id, live.GpuLoad!.Id, live.GpuPower!.Id], parts[0].Rows.Select(r => r.Id)); // temperature, load, the rest
            Assert.Equal(TaskbarViewModel.MarkBrush(TrayPart.Gpu, page.TaskbarLight), parts[0].NameBrush);
            Assert.StartsWith(live.GpuTemp.HardwareName, parts[0].Tooltip);
            Assert.Equal(TaskbarViewModel.StripSupported ? "" : "The strip needs Windows 11, so here the readings show as an icon each.", page.StripNote);

            page.Grouped = true; // and back to the strip from the page
            page.InStrip = true;
            Assert.Equal(TrayStyle.Strip, settings.Current.TrayStyle);
        });
    }

    [Fact]
    public void The_preview_writes_readings_as_the_strip_does()
    {
        var convert = new Converters.TrayTextConverter();
        Assert.Equal("62°", convert.Convert([SensorKind.Temperature, (double?)61.6], typeof(string), "strip", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("62", convert.Convert([SensorKind.Temperature, (double?)61.6], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
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
            Assert.Equal("Not found on this PC right now", row.Hardware);
            Assert.Empty(page.PreviewIcons); // nothing to draw for it
            Assert.Equal(TrayPart.Other, row.Part);
        });
    }

    [Fact]
    public void The_strip_goes_to_the_left_side_and_back()
    {
        var (page, settings, _) = Make();
        Ui.Run(() =>
        {
            Assert.Equal(("Right", false), (page.SideText, page.OnLeft)); // next to the clock to start
            page.SideText = "Left";
            Assert.Equal(TraySide.Left, settings.Current.TraySide);
            Assert.True(page.OnLeft);
            // Said only when Windows' icons fill the left end.
            Assert.Equal(page.ShowStrip && page.IconsAlignedLeft, page.SideNote.Length > 0);
            page.SideText = "Right";
            Assert.Equal(TraySide.Right, settings.Current.TraySide);
            Assert.Equal("", page.SideNote);
        });
    }

    [Fact]
    public void Each_reading_on_the_strip_can_have_a_graph_behind_it()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "CPU temperature"));
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "CPU load"));
            var (temp, load) = (page.Sensors[0], page.Sensors[1]);
            Assert.False(temp.Graph || load.Graph); // numbers alone to start
            Assert.Equal(TaskbarViewModel.StripSupported, temp.ShowShapeSwitch);

            page.ShowAsGraphCommand.Execute(load);
            Assert.Equal([live.CpuLoad!.Id], settings.Current.TrayGraphs);
            Assert.True(load.Graph);
            Assert.False(temp.Graph);
            Assert.Equal([false, true], page.PreviewStrip.Single().Readings.Select(r => r.Row.Graph));

            page.ShowAsGraphCommand.Execute(load); // once only
            Assert.Single(settings.Current.TrayGraphs);
            page.ShowAsNumberCommand.Execute(load);
            Assert.Empty(settings.Current.TrayGraphs);
            Assert.False(load.Graph);

            // The row's own switch (a click, keys or a screen reader), saved as the commands are.
            temp.Graph = true;
            Assert.Equal([live.CpuTemp!.Id], settings.Current.TrayGraphs);
            temp.Graph = false;
            Assert.Empty(settings.Current.TrayGraphs);

            // As icons there's no graph to draw: no switch.
            page.Separate = true;
            Assert.False(temp.ShowShapeSwitch);
        });
    }

    [Fact]
    public void A_graph_picked_from_the_taskbar_shows_on_the_page()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            page.AddQuickCommand.Execute(page.QuickPicks.Single(p => p.Label == "GPU temperature"));
            settings.Update(s => s.TrayGraphs.Add(live.GpuTemp!.Id)); // as the agent's right-click menu does
            page.LoadSensors();
            Assert.True(page.Sensors.Single().Graph);
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

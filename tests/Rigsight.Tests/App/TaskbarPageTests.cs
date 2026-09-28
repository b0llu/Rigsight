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
    public void The_preview_shows_what_the_taskbar_will()
    {
        // The same short numbers as the real icons (the agent's drawing uses Units.TrayText too).
        Assert.Equal("62", new Converters.TrayTextConverter().Convert([SensorKind.Temperature, (double?)61.6], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("–", new Converters.TrayTextConverter().Convert([SensorKind.Load, null!], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("–", new Converters.TrayTextConverter().Convert([], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}

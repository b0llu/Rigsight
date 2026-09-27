using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The Taskbar page: which readings go next to the clock, in what order, an icon each or all in one.</summary>
[Collection("UI")]
public sealed class TaskbarPageTests
{
    private static (TaskbarViewModel Page, Services.SettingsModel Settings, ViewModels.LiveData Live) Make(RigsightSettings? start = null)
    {
        var (settings, live) = Kit.Greeted(start);
        return (Ui.Run(() => new TaskbarViewModel(settings, live)), settings, live);
    }

    [Fact]
    public void Starts_empty_and_in_separate_icons()
    {
        var (page, _, _) = Make();
        Ui.Run(() =>
        {
            Assert.False(page.HasSensors);
            Assert.True(page.Separate);
            Assert.False(page.Combined);
            Assert.Empty(page.CombinedPreview);
            Assert.Equal("", page.CombinedNote);
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
    public void All_in_one_previews_two_and_says_they_take_turns()
    {
        var (page, settings, live) = Make();
        Ui.Run(() =>
        {
            foreach (var s in live.AllSensors.Take(3))
            {
                page.SensorToAdd = s;
                page.AddSensorCommand.Execute(null);
            }
            page.Combined = true;
            Assert.True(settings.Current.TrayCombined);
            Assert.False(page.Separate);
            Assert.Equal(2, page.CombinedPreview.Count);
            Assert.Contains("taking turns", page.CombinedNote);
            Assert.Contains("all 3", page.CombinedNote);
            page.Separate = true;
            Assert.False(settings.Current.TrayCombined);
            Assert.Equal("", page.CombinedNote);
        });
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
            Assert.Empty(page.CombinedPreview); // nothing to draw for it
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

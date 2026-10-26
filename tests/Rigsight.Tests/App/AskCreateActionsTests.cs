using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Board 7 (2026-10-26): an answer's "[do: widget …]", "[do: overlay …]" and "[do: taskbar …]" lines become a card that makes
/// or adds those readings with one press, only RigSight's own settings change, and Undo puts back exactly what was there.
/// </summary>
public sealed class AskCreateActionsTests
{
    private static readonly AiReading CpuPackage = new("CPU Package", SensorId: "/amdcpu/0/temperature/2", Part: "AMD Ryzen 7 7800X3D");
    private static readonly AiReading GpuHotSpotSensor = new("GPU Hot Spot", SensorId: "/gpu-nvidia/0/temperature/2", Part: "NVIDIA GeForce RTX 4080");
    private static readonly AiReading RamLoadSensor = new("Memory", SensorId: "/ram/load/0", Part: "Total Memory");

    private static AiActionContext Context(IReadOnlyList<AiReading>? sensors = null) =>
        new([], null, () => [], true, Sensors: sensors ?? [CpuPackage, GpuHotSpotSensor, RamLoadSensor],
            TrayOf: new Dictionary<OverlayMetric, AiReading> { [OverlayMetric.CpuTemp] = CpuPackage, [OverlayMetric.Ram] = RamLoadSensor });

    private static IReadOnlyList<AiAction> Resolve(params string[] markers) => AiActions.Resolve(markers, Context());

    private static (AskViewModel Window, SettingsModel Settings) Window(RigsightSettings? start = null, string rtss = "running")
    {
        var settings = Kit.OfflineSettings(start ?? new RigsightSettings());
        var window = new AskViewModel(settings) { RivaTuner = () => rtss };
        return (window, settings);
    }

    /// <summary>The card as the window shows it: built from the answer, then its chips read from the settings now.</summary>
    private static AskAction Card(AskViewModel window, SettingsModel settings, string marker, string rtss = "running")
    {
        var action = Assert.Single(Resolve(marker));
        var card = new AskAction(action);
        card.Setup!.Refresh(settings.Current, rtss);
        return card;
    }

    [Fact]
    public void Markers_of_all_three_kinds_become_cards_with_the_named_readings()
    {
        var widget = Assert.Single(Resolve("widget CPU temperature, gpu HOT SPOT, ram in use"));
        Assert.Equal(AiActionKind.CreateWidget, widget.Kind);
        Assert.True(widget.IsSetup);
        Assert.Equal("A widget with these readings", widget.Label);
        Assert.Equal([OverlayMetric.CpuTemp, OverlayMetric.GpuHotSpot, OverlayMetric.Ram], widget.Readings!.Select(r => r.Metric!.Value));

        var overlay = Assert.Single(Resolve("overlay Frame time, 1% low, CPU Package"));
        Assert.Equal(AiActionKind.AddOverlay, overlay.Kind);
        Assert.Equal("Add to your game overlay", overlay.Label);
        Assert.Equal(["FrameTime", "OnePercentLow", "sensor:/amdcpu/0/temperature/2"], overlay.Readings!.Select(r => r.Key));

        // The taskbar shows sensors: "CPU temperature" goes as the sensor that measures it here, named as asked.
        var taskbar = Assert.Single(Resolve("taskbar cpu temperature and GPU Hot Spot"));
        Assert.Equal(AiActionKind.AddTaskbar, taskbar.Kind);
        Assert.Equal("Add beside the clock", taskbar.Label);
        Assert.Equal([CpuPackage.SensorId, GpuHotSpotSensor.SensorId], taskbar.Readings!.Select(r => r.SensorId));
        Assert.Equal("CPU temperature", taskbar.Readings![0].Name);
    }

    [Fact]
    public void An_unknown_name_is_dropped_and_none_matched_means_no_card()
    {
        var widget = Assert.Single(Resolve("widget CPU temperature, Quantum flux, Frame rate (FPS)"));
        Assert.Equal([OverlayMetric.CpuTemp, OverlayMetric.Fps], widget.Readings!.Select(r => r.Metric!.Value));
        Assert.Empty(Resolve("widget Quantum flux, Warp core"));
        // The overlay doesn't show the widgets-only readings; the taskbar has no sensor for the frame rate.
        Assert.Empty(Resolve("overlay Screen time today"));
        Assert.Empty(Resolve("taskbar Frame rate (FPS)"));
        // A sensor by its part and name.
        Assert.Equal(GpuHotSpotSensor.SensorId, Assert.Single(Assert.Single(Resolve("overlay NVIDIA GeForce RTX 4080 GPU Hot Spot")).Readings!).SensorId);
    }

    [Fact]
    public void An_answer_draws_the_card_under_its_step_and_keeps_the_step_said()
    {
        var answer = AskViewModel.ToAnswer("Your GPU runs hot in games.\nWatch its hot spot while you play.\nNext step: keep an eye on it while you play.\n[do: overlay GPU hot spot, Frame rate (FPS)]",
            [], actions: m => AiActions.Resolve(m, Context()));
        var next = Assert.Single(answer.Parts.OfType<AskNext>());
        var card = Assert.Single(next.SetupActions);
        Assert.Equal(AiActionKind.AddOverlay, card.Kind);
        Assert.NotNull(card.Setup);
        Assert.Empty(next.LineActions);
        Assert.False(next.IsLines);
        Assert.True(next.IsWords);
    }

    [Fact]
    public void Nothing_changes_until_pressed_then_the_widget_is_made_on_and_Undo_removes_it()
    {
        Ui.Run(() =>
        {
            var (window, settings) = Window();
            int before = settings.Current.Widgets.Count;
            var card = Card(window, settings, "widget CPU temperature, GPU hot spot, CPU Package");
            Assert.Equal(before, settings.Current.Widgets.Count);
            Assert.Equal("As a slim bar, on your desktop, where you can drag it.", card.Setup!.Where);

            window.ActCommand.Execute(card);
            Assert.True(card.IsDone);
            Assert.Equal("Created and turned on", card.Setup.DoneText);
            var made = settings.Current.Widgets.Single(w => w.Id == card.Setup.CreatedId);
            Assert.Equal((WidgetStyle.Custom, WidgetLayout.Bar, true), (made.Style, made.Layout, made.Enabled));
            Assert.StartsWith("My widget ", made.Name);
            Assert.Equal(["CpuTemp", "GpuHotSpot", "sensor:/amdcpu/0/temperature/2"], made.Items!.Select(i => i.Id));

            window.UndoSetupCommand.Execute(card);
            Assert.True(card.IsReady);
            Assert.Equal(before, settings.Current.Widgets.Count);
            Assert.DoesNotContain(settings.Current.Widgets, w => w.Id == made.Id);
        });
    }

    [Fact]
    public void Customize_makes_the_widget_off_and_opens_it_in_the_editor()
    {
        Ui.Run(() =>
        {
            var (window, settings) = Window();
            (string Page, string? Focus)? opened = null;
            window.OpenPageAt = (page, focus) => opened = (page, focus);
            var card = Card(window, settings, "widget CPU temperature");
            window.SetupSecondCommand.Execute(card);
            var made = settings.Current.Widgets.Single(w => w.Id == card.Setup!.CreatedId);
            Assert.False(made.Enabled);
            Assert.Equal(("widgets", made.Id), opened);
        });
    }

    [Fact]
    public void Overlay_adds_readings_and_a_sensor_once_turns_it_on_and_Undo_restores_exactly()
    {
        Ui.Run(() =>
        {
            var start = new RigsightSettings();
            start.Overlay.Enabled = false;
            start.Overlay.Metrics = [OverlayMetric.CpuTemp, OverlayMetric.Ram];
            start.Overlay.Sensors = [new OverlaySensor { Id = "/x/1", Label = "Pump" }];
            var (window, settings) = Window(start);
            var card = Card(window, settings, "overlay GPU hot spot, CPU temperature, CPU Package");
            Assert.Equal([false, true, false], card.Setup!.Chips.Select(c => c.On));
            Assert.Contains("Shown with Alt+Shift+O in any game.", card.Setup.Where);
            Assert.Contains("CPU temperature already on.", card.Setup.Where);
            Assert.Contains("Adding turns the overlay on.", card.Setup.Where);
            Assert.False(settings.Current.Overlay.Enabled);

            window.ActCommand.Execute(card);
            var o = settings.Current.Overlay;
            Assert.True(card.IsDone);
            Assert.Equal("Added to the overlay", card.Setup.DoneText);
            Assert.True(o.Enabled);
            Assert.Equal([OverlayMetric.CpuTemp, OverlayMetric.GpuHotSpot, OverlayMetric.Ram], o.Metrics);
            Assert.Equal(["/x/1", CpuPackage.SensorId], o.Sensors.Select(x => x.Id));

            window.UndoSetupCommand.Execute(card);
            o = settings.Current.Overlay;
            Assert.True(card.IsReady);
            Assert.False(o.Enabled);
            Assert.Equal([OverlayMetric.CpuTemp, OverlayMetric.Ram], o.Metrics);
            Assert.Equal(("/x/1", "Pump"), (Assert.Single(o.Sensors).Id, o.Sensors[0].Label));
        });
    }

    [Fact]
    public void Overlay_keeps_to_its_ten_sensors()
    {
        Ui.Run(() =>
        {
            var start = new RigsightSettings();
            start.Overlay.Sensors = [.. Enumerable.Range(0, OverlaySettings.MaxSensors - 1).Select(i => new OverlaySensor { Id = $"/s/{i}" })];
            var (window, settings) = Window(start);
            var card = Card(window, settings, "overlay CPU Package, Memory");
            Assert.Equal("(no room)", card.Setup!.Chips[1].Note);
            window.ActCommand.Execute(card);
            var sensors = settings.Current.Overlay.Sensors;
            Assert.Equal(OverlaySettings.MaxSensors, sensors.Count);
            Assert.Contains(sensors, x => x.Id == CpuPackage.SensorId);
            Assert.DoesNotContain(sensors, x => x.Id == RamLoadSensor.SensorId);
        });
    }

    [Fact]
    public void Everything_already_on_offers_no_button()
    {
        Ui.Run(() =>
        {
            var (window, settings) = Window();
            var card = Card(window, settings, "overlay CPU temperature, RAM in use");
            Assert.True(card.Setup!.AllOn);
            Assert.All(card.Setup.Chips, c => Assert.True(c.On));
            window.ActCommand.Execute(card);
            Assert.Equal(OverlaySettings.DefaultMetrics(), settings.Current.Overlay.Metrics);
        });
    }

    [Fact]
    public void Taskbar_adds_sensors_and_Undo_puts_the_list_back()
    {
        Ui.Run(() =>
        {
            var start = new RigsightSettings { TraySensors = [RamLoadSensor.SensorId!] };
            var (window, settings) = Window(start);
            var card = Card(window, settings, "taskbar CPU temperature, RAM in use, GPU Hot Spot");
            Assert.Equal([false, true, false], card.Setup!.Chips.Select(c => c.On));
            Assert.StartsWith("In the taskbar, always in view.", card.Setup.Where);

            window.ActCommand.Execute(card);
            Assert.True(card.IsDone);
            Assert.Equal("Added to the taskbar", card.Setup.DoneText);
            Assert.Equal([RamLoadSensor.SensorId, CpuPackage.SensorId, GpuHotSpotSensor.SensorId], settings.Current.TraySensors);

            window.UndoSetupCommand.Execute(card);
            Assert.True(card.IsReady);
            Assert.Equal([RamLoadSensor.SensorId], settings.Current.TraySensors);
        });
    }

    [Fact]
    public void An_empty_taskbar_says_so()
    {
        Ui.Run(() =>
        {
            var (window, settings) = Window();
            var card = Card(window, settings, "taskbar CPU temperature");
            Assert.Equal("In the taskbar, always in view. Nothing is shown there yet.", card.Setup!.Where);
        });
    }

    [Fact]
    public void Frame_readings_say_they_need_RivaTuner_when_it_isnt_running()
    {
        Ui.Run(() =>
        {
            var start = new RigsightSettings();
            start.Overlay.Metrics = [OverlayMetric.CpuTemp];
            var (window, settings) = Window(start, rtss: "missing");
            var card = Card(window, settings, "widget Frame rate (FPS), Frame time, CPU temperature", rtss: "missing");
            Assert.Equal(["Frame rate (FPS) (needs RivaTuner)", "Frame time (needs RivaTuner)", "CPU temperature"], card.Setup!.Chips.Select(c => c.Text));
            card.Setup.Refresh(settings.Current, "running");
            Assert.All(card.Setup.Chips, c => Assert.Null(c.Note));
            // Not known yet: no warning.
            card.Setup.Refresh(settings.Current, "");
            Assert.All(card.Setup.Chips, c => Assert.Null(c.Note));
        });
    }
}

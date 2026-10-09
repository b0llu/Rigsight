using Rigsight.Core.Settings;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Models;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Live sensor data: the hello (sensor list, key sensors, history), ticks, today's ranges, and the All sensors page.</summary>
[Collection("UI")]
public sealed class LiveDataTests
{
    private static readonly long T0 = TimeUtil.NowUnixMs() - 60_000;

    // ── Hello ────────────────────────────────────────────────────────────

    [Fact]
    public void Hello_builds_every_hardware_card_and_sensor_in_order()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal(Pc.Layout.Select(h => h.Hardware), live.Hardware.Select(h => h.Name));
            Assert.Equal(Pc.Ids, live.AllSensors.Select(s => s.Id));
            Assert.True(live.HasHardware);
            var cpu = live.Hardware[0];
            Assert.Equal("Cpu", cpu.Type);
            Assert.All(cpu.Sensors, s => Assert.Equal("Test CPU", s.HardwareName));
            Assert.All(live.AllSensors, s => Assert.True(s.IsShown));
        });
    }

    [Fact]
    public void Hello_maps_every_key_sensor()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal("/cpu/temperature/0", live.CpuTemp?.Id);
            Assert.Equal("/cpu/temperature/1", live.CpuDieTemp?.Id);
            Assert.Equal("/cpu/load/0", live.CpuLoad?.Id);
            Assert.Equal("/cpu/power/0", live.CpuPower?.Id);
            Assert.Equal("/cpu/clock/0", live.CpuClock?.Id);
            Assert.Equal("/cpu/voltage/0", live.CpuVoltage?.Id);
            Assert.Equal("/gpu/temperature/0", live.GpuTemp?.Id);
            Assert.Equal("/gpu/temperature/1", live.GpuHotSpot?.Id);
            Assert.Equal("/gpu/temperature/2", live.GpuMemJunction?.Id);
            Assert.Equal("/gpu/load/0", live.GpuLoad?.Id);
            Assert.Equal("/gpu/power/0", live.GpuPower?.Id);
            Assert.Equal("/gpu/clock/0", live.GpuClock?.Id);
            Assert.Equal("/gpu/control/0", live.GpuFan?.Id);
            Assert.Equal("/gpu/load/1", live.GpuVramLoad?.Id);
            Assert.Equal("/gpu/smalldata/0", live.GpuVramUsed?.Id);
            Assert.Equal("/gpu/smalldata/1", live.GpuVramTotal?.Id);
            Assert.Equal("/ram/load/0", live.RamLoad?.Id);
            Assert.Equal("/ram/data/0", live.RamUsed?.Id);
            Assert.Equal("/ram/data/1", live.RamAvailable?.Id);
            // Windows' virtual memory is found by its card's name, not by a key.
            Assert.Equal("/vram/load/0", live.VirtualLoad?.Id);
            Assert.Equal("Test CPU", live.CpuName);
            Assert.Equal("Test GPU", live.GpuName);
            Assert.Equal("Test CPU  ·  Test GPU", live.SystemSummary);
        });
    }

    [Fact]
    public void Key_indexes_past_the_sensor_list_are_ignored()
    {
        var hello = Pc.Hello();
        hello.Keys![KeySensors.CpuTemp] = 9999;
        var (_, live) = Kit.Greeted(hello: hello);
        Ui.Run(() =>
        {
            Assert.Null(live.CpuTemp);
            Assert.Null(live.Resolve("key:" + KeySensors.CpuTemp));
            Assert.DoesNotContain(live.TempSeries, s => s.Label == "CPU");
        });
    }

    [Fact]
    public void A_pc_without_a_gpu_has_no_gpu_picks_or_gpu_lines()
    {
        var (_, live) = Kit.Greeted(hello: Pc.Hello("Test GPU"));
        Ui.Run(() =>
        {
            Assert.Null(live.GpuTemp);
            Assert.Null(live.GpuLoad);
            Assert.Equal("GPU", live.GpuName);
            Assert.Equal("Test CPU", live.SystemSummary);
            Assert.Equal(["CPU"], live.TempSeries.Select(s => s.Label));
        });
    }

    [Fact]
    public void A_pc_with_nothing_but_names_has_a_generic_summary()
    {
        var (_, live) = Kit.Greeted(hello: Pc.Hello("Test CPU", "Test GPU"));
        Ui.Run(() =>
        {
            Assert.Equal("CPU", live.CpuName);
            Assert.Equal("", live.SystemSummary);
            Assert.Empty(live.CpuThreads);
            Assert.Empty(live.TempSeries);
        });
    }

    [Fact]
    public void Temperature_chart_has_a_line_per_key_temperature()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal(["CPU", "GPU", "GPU hot spot", "GPU memory"], live.TempSeries.Select(s => s.Label));
            Assert.Same(live.CpuTemp, live.TempSeries[0].Sensor);
            Assert.Same(live.GpuMemJunction, live.TempSeries[3].Sensor);
        });
    }

    [Fact]
    public void Cpu_threads_are_the_per_core_loads_only()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() => Assert.Equal(["CPU Core #1", "CPU Core #2 Thread #1"], live.CpuThreads.Select(t => t.Name)));
    }

    [Fact]
    public void Drives_get_their_temperature_life_used_space_and_hours()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var drive = Assert.Single(live.Drives);
            Assert.Equal("Test SSD", drive.Name);
            Assert.Equal("/nvme/0/temperature/0", drive.Temperature?.Id);
            Assert.Equal("/nvme/0/level/20", drive.Life?.Id);
            Assert.Equal("/nvme/0/load/30", drive.UsedSpace?.Id);
            Assert.Equal("/nvme/0/factor/0", drive.PowerOnHours?.Id);
        });
    }

    [Fact]
    public void A_drive_without_a_composite_temperature_uses_its_first_real_one()
    {
        var hello = new AgentMessage
        {
            T = "hello",
            Hardware =
            [
                new HardwareMeta
                {
                    Name = "Old HDD", Type = "Storage",
                    Sensors =
                    [
                        new SensorMeta { Id = "/hdd/0/temperature/10", Name = "Warning Temperature", Kind = SensorKind.Temperature },
                        new SensorMeta { Id = "/hdd/0/temperature/11", Name = "Critical Temperature", Kind = SensorKind.Temperature },
                        new SensorMeta { Id = "/hdd/0/temperature/1", Name = "Temperature #1", Kind = SensorKind.Temperature },
                    ],
                },
                new HardwareMeta { Name = "Bare HDD", Type = "Storage", Sensors = [new SensorMeta { Id = "/hdd/1/load/0", Name = "Used Space", Kind = SensorKind.Load }] },
            ],
        };
        var (_, live) = Kit.Greeted(hello: hello);
        Ui.Run(() =>
        {
            Assert.Equal("/hdd/0/temperature/1", live.Drives[0].Temperature?.Id);
            Assert.Null(live.Drives[0].Life);
            Assert.Null(live.Drives[1].Temperature);
            Assert.Equal("/hdd/1/load/0", live.Drives[1].UsedSpace?.Id);
        });
    }

    [Fact]
    public void Board_fans_and_temperatures_come_from_the_sensor_chip()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal(["Fan #1", "Fan #2", "Fan #3"], live.Fans.Select(f => f.Name));
            Assert.Equal(["System", "Bogus"], live.BoardTemps.Select(t => t.Name));
        });
    }

    [Fact]
    public void Drive_health_from_the_hello_is_matched_by_name()
    {
        var hello = Pc.Hello();
        hello.Drives = [new DriveHealthInfo { Name = "Test SSD", Status = "Caution", ReallocatedSectors = 3 }, new DriveHealthInfo { Name = "Other" }];
        var (_, live) = Kit.Greeted(hello: hello);
        Ui.Run(() =>
        {
            Assert.Equal("Caution", live.Drives[0].Health?.Status);
            // A later hello without the drive's health clears nothing that isn't sent again.
            live.LoadHello(new AgentMessage { T = "hello", Drives = [new DriveHealthInfo { Name = "Nope", Status = "Bad" }] });
            Assert.Equal("Caution", live.Drives[0].Health?.Status);
            live.LoadHello(Pc.Hello());
            Assert.Null(live.Drives[0].Health);
        });
    }

    [Fact]
    public void Today_arrives_with_a_hello_before_the_hardware()
    {
        var settings = Kit.OfflineSettings();
        var live = Kit.Live(settings);
        Ui.Run(() =>
        {
            live.LoadHello(new AgentMessage { T = "hello", Today = new TodayInfo { ActiveSec = 1234, TopApp = "Steam" } });
            Assert.Equal(1234, live.Today.ActiveSec);
            Assert.False(live.HasHardware);
            Assert.Empty(live.Hardware);
            Assert.Empty(live.SensorRows);
        });
    }

    [Fact]
    public void The_same_sensors_again_keep_the_sensor_objects()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var before = live.AllSensors.ToList();
            live.ApplyTick(Pc.Tick(T0));
            int rebuilt = 0;
            live.SensorsRebuilt += () => rebuilt++;
            live.LoadHello(Pc.Hello());
            Assert.Equal(0, rebuilt);
            Assert.Equal(before, live.AllSensors);
            // History and readings survive a reconnect.
            Assert.Equal(1, live.CpuTemp!.History.Count);
            Assert.NotNull(live.CpuTemp.Value);
        });
    }

    [Fact]
    public void Different_sensors_rebuild_everything()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var oldCpu = live.CpuTemp;
            int rebuilt = 0;
            live.SensorsRebuilt += () => rebuilt++;
            live.LoadHello(Pc.Hello("Test Board"));
            Assert.Equal(1, rebuilt);
            Assert.NotSame(oldCpu, live.CpuTemp);
            Assert.Equal(Pc.Layout.Length - 1, live.Hardware.Count);
            Assert.Empty(live.Fans);
            Assert.Equal(4, live.TempSeries.Count);
        });
    }

    [Fact]
    public void Same_number_of_sensors_with_other_ids_rebuilds()
    {
        var (_, live) = Kit.Greeted();
        var hello = Pc.Hello();
        hello.Hardware![0].Sensors[0].Id = "/cpu/temperature/99";
        Ui.Run(() =>
        {
            int rebuilt = 0;
            live.SensorsRebuilt += () => rebuilt++;
            live.LoadHello(hello);
            Assert.Equal(1, rebuilt);
            Assert.Equal("/cpu/temperature/99", live.AllSensors[0].Id);
            Assert.Null(live.Resolve("/cpu/temperature/0"));
        });
    }

    [Fact]
    public void Hidden_renamed_and_collapsed_sensors_come_from_settings()
    {
        var start = SeedData.QuietSettings();
        start.HiddenSensors.Add("/cpu/power/0");
        start.SensorLabels["/cpu/temperature/0"] = "My CPU";
        start.CollapsedHardware.Add("Test GPU");
        var (_, live) = Kit.Greeted(start);
        Ui.Run(() =>
        {
            Assert.True(live.CpuPower!.IsHidden);
            Assert.False(live.CpuPower.IsShown);
            Assert.Equal("My CPU", live.CpuTemp!.DisplayName);
            Assert.Equal("Core (Tctl/Tdie)", live.CpuTemp.Name);
            Assert.False(live.Hardware.Single(h => h.Name == "Test GPU").IsExpanded);
            Assert.Equal(1, live.HiddenCount);
            Assert.Contains("1 hidden", live.Hardware[0].Summary);
        });
    }

    [Fact]
    public void History_seeds_key_sensors_and_drives_by_id()
    {
        var hello = Pc.Hello();
        long t = T0;
        hello.History =
        [
            new SeriesHistory { Key = KeySensors.CpuTemp, Times = [t, t + 1000, t + 2000], Values = [50, null, 52] },
            new SeriesHistory { Key = "id:/nvme/0/temperature/0", Times = [t, t + 60_000], Values = [35, 36] },
            new SeriesHistory { Key = "id:/does/not/exist", Times = [t], Values = [1] },
            new SeriesHistory { Key = "notAKey", Times = [t], Values = [1] },
        ];
        var (_, live) = Kit.Greeted(hello: hello);
        Ui.Run(() =>
        {
            var cpu = live.CpuTemp!.History;
            Assert.Equal(3, cpu.Count);
            Assert.Equal(50, cpu.ValueAt(0));
            Assert.True(double.IsNaN(cpu.ValueAt(1))); // a missing reading
            Assert.Equal(t + 2000, cpu.LastTime);
            var drive = live.Drives[0].Temperature!.History;
            Assert.Equal(2, drive.Count);
            Assert.Equal(36, drive.ValueAt(1));
            // Seeding never touches today's range.
            Assert.Null(live.CpuTemp.Min);
            Assert.Null(live.CpuTemp.Value);
        });
    }

    [Fact]
    public void History_is_seeded_only_into_empty_buffers()
    {
        var (_, live) = Kit.Greeted();
        var hello = Pc.Hello();
        hello.History = [new SeriesHistory { Key = KeySensors.CpuTemp, Times = [T0 - 5000, T0 - 4000], Values = [1, 2] }];
        Ui.Run(() =>
        {
            live.ApplyTick(Pc.Tick(T0));
            long tick = live.Tick;
            live.LoadHello(hello);
            Assert.Equal(1, live.CpuTemp!.History.Count);
            Assert.Equal(tick + 1, live.Tick);
        });
    }

    [Fact]
    public void History_without_keys_is_ignored()
    {
        var hello = Pc.Hello();
        hello.History = [new SeriesHistory { Key = KeySensors.CpuTemp, Times = [T0], Values = [1] }];
        hello.Keys = null;
        var (_, live) = Kit.Greeted(hello: hello);
        Ui.Run(() => Assert.All(live.AllSensors, s => Assert.Equal(0, s.History.Count)));
    }

    // ── Ticks ────────────────────────────────────────────────────────────

    [Fact]
    public void A_tick_sets_every_reading_and_its_history()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            long before = live.Tick;
            live.ApplyTick(Pc.Tick(T0, 40));
            Assert.Equal(before + 1, live.Tick);
            for (int i = 0; i < Pc.Count; i++)
            {
                var s = live.AllSensors[i];
                Assert.Equal(40 + i / 10f, s.Value!.Value, 3);
                Assert.Equal(1, s.History.Count);
                Assert.Equal(T0, s.History.LastTime);
                Assert.Equal(1, s.Version);
            }
        });
    }

    [Fact]
    public void Missing_and_invalid_readings_become_no_value()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Pc.Tick(T0, 40,
                ("/cpu/load/0", null), ("/cpu/power/0", float.NaN), ("/cpu/clock/0", float.PositiveInfinity),
                ("/cpu/voltage/0", float.NegativeInfinity), ("/gpu/temperature/0", 0), ("/gpu/temperature/1", -5)));
            Assert.Null(live.CpuLoad!.Value);
            Assert.Null(live.CpuPower!.Value);
            Assert.Null(live.CpuClock!.Value);
            Assert.Null(live.CpuVoltage!.Value);
            // A temperature of zero or below is a sensor that isn't really there.
            Assert.Null(live.GpuTemp!.Value);
            Assert.Null(live.GpuHotSpot!.Value);
            Assert.Equal("—", live.GpuTemp.FormattedValue);
            Assert.True(double.IsNaN(live.CpuLoad.History.ValueAt(0)));
            Assert.Null(live.CpuLoad.Min);
            // A zero load is a real reading.
            live.ApplyTick(Pc.Tick(T0 + 1000, 40, ("/cpu/load/0", 0)));
            Assert.Equal(0, live.CpuLoad.Value);
        });
    }

    [Fact]
    public void A_tick_for_another_sensor_list_is_ignored()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            long before = live.Tick;
            live.ApplyTick(new AgentMessage { T = "tick", Time = T0, Values = [1, 2, 3] });
            live.ApplyTick(new AgentMessage { T = "tick", Time = T0, Values = null });
            Assert.All(live.AllSensors, s => Assert.Null(s.Value));
            Assert.Equal(before + 2, live.Tick);
        });
    }

    [Fact]
    public void Extreme_readings_are_accepted()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Pc.Tick(T0, 40, ("/cpu/load/0", float.MaxValue), ("/cpu/power/0", float.MinValue), ("/cpu/clock/0", float.Epsilon)));
            Assert.Equal(float.MaxValue, live.CpuLoad!.Value!.Value, 1e30);
            Assert.Equal(float.MinValue, live.CpuPower!.Value!.Value, 1e30);
            Assert.NotEqual("—", live.CpuLoad.FormattedValue);
        });
    }

    [Fact]
    public void Min_max_and_average_follow_the_readings_since_opened()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            foreach (var (i, v) in new[] { 50f, 70f, 60f, float.NaN }.Index())
                live.ApplyTick(Pc.Tick(T0 + i * 1000, 40, ("/cpu/temperature/0", v)));
            var s = live.CpuTemp!;
            Assert.Equal(50, s.Min);
            Assert.Equal(70, s.Max);
            Assert.Equal(60, s.Average);
            Assert.Null(s.Value);
            Assert.Equal(4, s.History.Count);
            Assert.Equal("↓ 50°   ↑ 70°", s.MinMaxText);
        });
    }

    [Fact]
    public void Today_totals_follow_the_ticks()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var tick = Pc.Tick(T0);
            tick.Today = new TodayInfo { ActiveSec = 99, TopApp = "Dota 2" };
            live.ApplyTick(tick);
            Assert.Equal("Dota 2", live.Today.TopApp);
            live.ApplyTick(Pc.Tick(T0 + 1000));
            Assert.Equal("Dota 2", live.Today.TopApp); // not sent again: kept
        });
    }

    [Fact]
    public void A_long_silence_leaves_a_gap_in_the_history()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Pc.Tick(T0 - 60_000));
            live.ApplyTick(Pc.Tick(T0));
            var cpu = live.CpuTemp!.History;
            Assert.Equal(3, cpu.Count);
            Assert.True(double.IsNaN(cpu.ValueAt(1)));
            // Drives read every few minutes: a minute isn't a gap for them.
            Assert.Equal(2, live.Drives[0].Temperature!.History.Count);
        });
    }

    [Fact]
    public void Derived_texts_for_memory_and_video_memory()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal("", live.RamText);
            live.ApplyTick(Pc.Tick(T0, 40, ("/ram/data/0", 12.34f), ("/ram/data/1", 19.66f), ("/gpu/smalldata/0", 3072), ("/gpu/smalldata/1", 12288)));
            Assert.Equal("12.3 GB of 32.0 GB", live.RamText);
            Assert.Equal("32 GB", live.RamTotalText);
            Assert.Equal("3.0 / 12.0 GB", live.GpuVramText);
            Assert.Equal("12.3 GB", live.MemAppsText);
            // Without the video memory total, its load is shown instead.
            live.ApplyTick(Pc.Tick(T0 + 1000, 40, ("/gpu/smalldata/1", null), ("/gpu/load/1", 37.4f)));
            Assert.Equal("37%", live.GpuVramText);
            live.ApplyTick(Pc.Tick(T0 + 2000, 40, ("/gpu/smalldata/1", 0), ("/gpu/load/1", 12f)));
            Assert.Equal("12%", live.GpuVramText);
        });
    }

    [Fact]
    public void Fans_that_never_spun_and_nonsense_board_temperatures_go_after_three_readings()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            // Fan #2 is stopped now but spun earlier today (0 RPM fan at idle): it stays.
            var tick = Pc.Tick(T0, 40, ("/lpc/fan/1", 0), ("/lpc/fan/2", 0), ("/lpc/temperature/1", 130));
            tick.Extremes = new() { ["/lpc/fan/1"] = [0, 900] };
            tick.ExtremesFull = true;
            tick.ExtremesDay = "2026-01-01";
            live.ApplyTick(tick);
            live.ApplyTick(Pc.Tick(T0 + 1000, 40, ("/lpc/fan/1", 0), ("/lpc/fan/2", 0), ("/lpc/temperature/1", 130)));
            Assert.Equal(3, live.Fans.Count);
            live.ApplyTick(Pc.Tick(T0 + 2000, 40, ("/lpc/fan/1", 0), ("/lpc/fan/2", 0), ("/lpc/temperature/1", 130)));
            Assert.Equal(["Fan #1", "Fan #2"], live.Fans.Select(f => f.Name));
            Assert.Equal(["System"], live.BoardTemps.Select(t => t.Name));
            // Only once: a fan that stops later stays listed.
            for (int i = 3; i < 6; i++) live.ApplyTick(Pc.Tick(T0 + i * 1000, 40, ("/lpc/fan/0", 0)));
            Assert.Equal(2, live.Fans.Count);
        });
    }

    // ── Today's range (extremes) ─────────────────────────────────────────

    private static AgentMessage Extremes(string day, bool full, params (string Id, double[] Range)[] ranges)
    {
        var tick = new AgentMessage { T = "tick", Time = T0, ExtremesDay = day, ExtremesFull = full, Extremes = [] };
        foreach (var (id, range) in ranges) tick.Extremes[id] = range;
        return tick;
    }

    [Fact]
    public void Full_extremes_set_todays_range()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Extremes("2026-09-25", true, ("/cpu/temperature/0", [31, 88]), ("/gpu/temperature/0", [29, 77])));
            Assert.Equal((31, 88), (live.CpuTemp!.Min, live.CpuTemp.Max));
            Assert.Equal((29, 77), (live.GpuTemp!.Min, live.GpuTemp.Max));
            Assert.Equal("↓ 31°   ↑ 88°", live.CpuTemp.MinMaxText);
        });
    }

    [Fact]
    public void Extreme_deltas_update_only_what_changed_and_readings_widen_the_range()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Extremes("2026-09-25", true, ("/cpu/temperature/0", [31, 88]), ("/gpu/temperature/0", [29, 77])));
            live.ApplyTick(Extremes("2026-09-25", false, ("/cpu/temperature/0", [30, 88])));
            Assert.Equal(30, live.CpuTemp!.Min);
            Assert.Equal((29, 77), (live.GpuTemp!.Min, live.GpuTemp.Max));
            live.ApplyTick(Pc.Tick(T0 + 1000, 40, ("/cpu/temperature/0", 95)));
            Assert.Equal(95, live.CpuTemp.Max);
        });
    }

    [Fact]
    public void A_new_day_starts_the_ranges_afresh()
    {
        var settings = Kit.OfflineSettings();
        var live = Kit.Live(settings);
        Ui.Run(() =>
        {
            // Ranges are kept for sensors not known yet, and forgotten when the day changes.
            live.ApplyTick(Extremes("2026-09-24", true, ("/cpu/temperature/0", [31, 88]), ("/gpu/temperature/0", [29, 77])));
            live.ApplyTick(Extremes("2026-09-25", false, ("/cpu/temperature/0", [40, 41])));
            live.LoadHello(Pc.Hello());
            Assert.Equal((40, 41), (live.CpuTemp!.Min, live.CpuTemp.Max));
            Assert.Null(live.GpuTemp!.Min);
        });
    }

    [Fact]
    public void Ranges_that_arrive_before_the_sensors_are_applied_when_they_do()
    {
        var settings = Kit.OfflineSettings();
        var live = Kit.Live(settings);
        Ui.Run(() =>
        {
            live.ApplyTick(Extremes("2026-09-25", true, ("/cpu/temperature/0", [31, 88])));
            live.LoadHello(Pc.Hello());
            Assert.Equal((31, 88), (live.CpuTemp!.Min, live.CpuTemp.Max));
        });
    }

    [Fact]
    public void Malformed_ranges_are_skipped()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Extremes("2026-09-25", true, ("/cpu/temperature/0", [31]), ("/gpu/temperature/0", [1, 2, 3]), ("/nope", [1, 2]),
                ("/cpu/load/0", [0, 100])));
            Assert.Null(live.CpuTemp!.Min);
            Assert.Null(live.GpuTemp!.Min);
            Assert.Equal(100, live.CpuLoad!.Max);
        });
    }

    // ── Units ────────────────────────────────────────────────────────────

    [Fact]
    public void Fahrenheit_changes_every_temperature_text_and_nothing_else()
    {
        var (settings, live) = Kit.Greeted();
        try
        {
            Ui.Run(() =>
            {
                live.ApplyTick(Pc.Tick(T0, 40, ("/cpu/temperature/0", 50), ("/cpu/load/0", 50)));
                Assert.Equal("50.0 °C", live.CpuTemp!.FormattedValue);
                live.ApplySettings();
                settings.Update(s => s.UseFahrenheit = true);
                // The shell refreshes the sensors when settings change.
                var changed = Kit.Changes(live.CpuTemp, live.ApplySettings);
                Assert.Contains("", changed);
                Assert.True(Units.Fahrenheit);
                Assert.Equal("122.0 °F", live.CpuTemp.FormattedValue);
                Assert.Equal("122°", live.CpuTemp.ShortValue);
                Assert.Equal("50.0 %", live.CpuLoad!.FormattedValue);
            });
        }
        finally
        {
            Ui.Run(() => settings.Update(s => s.UseFahrenheit = false));
        }
        Assert.False(Units.Fahrenheit);
    }

    [Fact]
    public void ApplySettings_refreshes_the_sensors_only_when_names_hidden_or_units_changed()
    {
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplySettings();
            var quiet = Kit.Changes(live.CpuTemp!, () => { settings.Update(s => s.LiveRefreshMs = 2000); live.ApplySettings(); });
            Assert.Empty(quiet);
            var renamed = Kit.Changes(live.CpuTemp!, () => { settings.Update(s => s.SensorLabels["/cpu/temperature/0"] = "Hot rock"); live.ApplySettings(); });
            Assert.Contains("", renamed);
            Assert.Equal("Hot rock", live.CpuTemp!.DisplayName);
            settings.Update(s => s.HiddenSensors.Add("/gpu/temperature/0"));
            live.ApplySettings();
            Assert.True(live.GpuTemp!.IsHidden);
            Assert.Equal(1, live.HiddenCount);
        });
    }

    // ── Temperature chart range ──────────────────────────────────────────

    [Theory]
    [InlineData(300)]
    [InlineData(3600)]
    [InlineData(21600)]
    [InlineData(86400)]
    [InlineData(0)]
    [InlineData(604800)]
    [InlineData(2592000)]
    [InlineData(31536000)]
    public void Chart_window_is_saved_and_announced(int seconds)
    {
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            int raised = 0;
            live.ChartRangeChanged += () => raised++;
            var changed = Kit.Changes(live, () => live.ChartWindowSeconds = seconds);
            Assert.Equal(seconds, settings.Current.ChartWindowSeconds);
            Assert.Equal(seconds, live.ChartWindowSeconds);
            Assert.Equal(seconds == 0, live.IsChartDay);
            Assert.Equal(seconds > 86400, live.IsChartLong);
            Assert.Equal(1, raised);
            Assert.Contains(nameof(LiveData.ChartWindowSeconds), changed);
            Assert.Contains(nameof(LiveData.IsChartDay), changed);
        });
    }

    [Fact]
    public void A_week_or_a_month_is_picked_like_a_day()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var today = DateTime.Today;
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            Assert.False(live.IsChartPaged); // 5 minutes: it ends now
            live.ChartWindowSeconds = 604800;
            Assert.True(live.IsChartPaged);
            Assert.Equal((monday, monday.AddDays(7)), live.ChartPeriod);
            Assert.Equal("This week", live.ChartDayLabel);
            Assert.False(live.CanChartNextDay);
            int raised = 0;
            live.ChartRangeChanged += () => raised++;
            live.ChartPreviousDayCommand.Execute(null);
            Assert.Equal("Last week", live.ChartDayLabel);
            Assert.Equal((monday.AddDays(-7), monday), live.ChartPeriod);
            Assert.True(live.CanChartNextDay);
            // Back as far as the week the history starts in, and no further.
            live.ChartHistoryStart = monday.AddDays(-12);
            live.ChartPreviousDayCommand.Execute(null);
            Assert.Equal((monday.AddDays(-14), monday.AddDays(-7)), live.ChartPeriod);
            Assert.False(live.CanChartPreviousDay);
            live.ChartPreviousDayCommand.Execute(null);
            Assert.Equal((monday.AddDays(-14), monday.AddDays(-7)), live.ChartPeriod);
            Assert.Equal(2, raised);
            // The month that day is in; forward ends on this month.
            live.ChartHistoryStart = null;
            live.ChartDay = today;
            live.ChartWindowSeconds = 2592000;
            var first = new DateTime(today.Year, today.Month, 1);
            Assert.Equal((first, first.AddMonths(1)), live.ChartPeriod);
            Assert.Equal("This month", live.ChartDayLabel);
            Assert.False(live.CanChartNextDay);
            live.ChartPreviousDayCommand.Execute(null);
            Assert.Equal((first.AddMonths(-1), first), live.ChartPeriod);
            Assert.Equal("Last month", live.ChartDayLabel);
            live.ChartNextDayCommand.Execute(null);
            Assert.Equal((first, first.AddMonths(1)), live.ChartPeriod);
            live.ChartNextDayCommand.Execute(null);
            Assert.Equal((first, first.AddMonths(1)), live.ChartPeriod);
            Assert.True(live.IsChartMonth); // the picker offers months, as on Reports
            Assert.Equal(3600, live.ChartStepSeconds);
            // The year: picked by year, drawn from days.
            live.ChartWindowSeconds = 31536000;
            Assert.True(live.IsChartYear && !live.IsChartMonth && live.IsChartPaged);
            Assert.Equal((new DateTime(today.Year, 1, 1), new DateTime(today.Year + 1, 1, 1)), live.ChartPeriod);
            Assert.Equal("This year", live.ChartDayLabel);
            Assert.Equal(86400, live.ChartStepSeconds);
            Assert.False(live.CanChartNextDay);
            live.ChartPreviousDayCommand.Execute(null);
            Assert.Equal("Last year", live.ChartDayLabel);
            live.ChartNextDayCommand.Execute(null);
            Assert.Equal("This year", live.ChartDayLabel);
            // A day again: the day itself.
            live.ChartWindowSeconds = 0;
            Assert.False(live.IsChartMonth || live.IsChartYear);
            Assert.Equal(60, live.ChartStepSeconds);
            Assert.Equal("Today", live.ChartDayLabel);
        });
    }

    [Fact]
    public void Chart_day_never_goes_past_today()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal(DateTime.Today, live.ChartDay);
            Assert.Equal("Today", live.ChartDayLabel);
            Assert.False(live.CanChartNextDay);
            live.ChartDay = DateTime.Today.AddDays(5);
            Assert.Equal(DateTime.Today, live.ChartDay);
            live.ChartNextDayCommand.Execute(null);
            Assert.Equal(DateTime.Today, live.ChartDay);
        });
    }

    [Fact]
    public void Chart_steps_back_to_the_first_day_with_history_and_forward_to_today()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            int raised = 0;
            live.ChartRangeChanged += () => raised++;
            Assert.True(live.CanChartPreviousDay); // no history start known yet: allowed
            live.ChartHistoryStart = DateTime.Today.AddDays(-2);
            live.ChartPreviousDayCommand.Execute(null);
            Assert.Equal("Yesterday", live.ChartDayLabel);
            Assert.True(live.CanChartNextDay);
            live.ChartPreviousDayCommand.Execute(null);
            Assert.Equal(DateTime.Today.AddDays(-2), live.ChartDay);
            Assert.False(live.CanChartPreviousDay);
            live.ChartPreviousDayCommand.Execute(null);
            Assert.Equal(DateTime.Today.AddDays(-2), live.ChartDay);
            live.ChartNextDayCommand.Execute(null);
            live.ChartNextDayCommand.Execute(null);
            Assert.Equal(DateTime.Today, live.ChartDay);
            Assert.Equal(4, raised);
            // Setting the same day (with a time of day) again isn't a change.
            live.ChartDay = DateTime.Now;
            Assert.Equal(4, raised);
        });
    }

    [Fact]
    public void Minute_history_goes_to_every_temperature_line()
    {
        var (_, live) = Kit.Greeted();
        long start = TimeUtil.ToUnix(DateTime.Now.AddHours(-1));
        var minutes = Enumerable.Range(0, 30).Select(i => new SystemMinute { Ts = start + i * 60, CpuTemp = 50 + i, GpuTemp = 40, GpuHotMax = null, GpuMemMax = 70 }).ToList();
        Ui.Run(() =>
        {
            long before = live.Tick;
            live.LoadMinuteHistory(minutes);
            Assert.Equal(before + 1, live.Tick);
            Assert.All(live.TempSeries, s => Assert.Equal(30, s.Minutes.Count));
            Assert.Equal(79, live.TempSeries[0].Minutes.ValueAt(29));
            Assert.True(double.IsNaN(live.TempSeries[2].Minutes.ValueAt(0))); // the hot spot isn't in this history
            // Lines made later (a new sensor list) start with the history already loaded.
            live.LoadHello(Pc.Hello("Test Board"));
            Assert.Equal(30, live.TempSeries[0].Minutes.Count);
        });
    }

    [Fact]
    public void A_week_or_month_is_loaded_as_hours()
    {
        var (_, live) = Kit.Greeted();
        long start = TimeUtil.ToUnix(DateTime.Now.AddDays(-2)) / 3600 * 3600;
        // Three hours, then the PC off for five, then one more.
        var hours = new[] { 0, 1, 2, 8 }.Select(h => new SystemMinute { Ts = start + h * 3600, CpuTemp = 50 + h, CpuTempMax = 70 + h, CpuTempMin = 40 + h, GpuTemp = 45 }).ToList();
        Ui.Run(() =>
        {
            live.LoadMinuteHistory(hours, 3600);
            var cpu = live.TempSeries[0];
            Assert.Equal(3600, cpu.StepSeconds);
            // Each in the middle of its hour, with a break where the PC was off.
            Assert.Equal(5, cpu.Minutes.Count);
            Assert.Equal((start + 1800) * 1000, cpu.Minutes.TimeAt(0));
            Assert.True(double.IsNaN(cpu.Minutes.ValueAt(3)));
            Assert.Equal((start + 3 * 3600 + 1800) * 1000, cpu.Minutes.TimeAt(3));
            Assert.Equal((58.0, 78.0, 48.0), cpu.StatsAt(4));
            // Lines made later (a new sensor list) are hours too, and minutes again once minutes are loaded.
            live.LoadHello(Pc.Hello("Test Board"));
            Assert.Equal(3600, live.TempSeries[0].StepSeconds);
            live.LoadMinuteHistory([new SystemMinute { Ts = start, CpuTemp = 50 }]);
            Assert.Equal(60, live.TempSeries[0].StepSeconds);
            Assert.Equal((start + 30) * 1000, live.TempSeries[0].Minutes.TimeAt(0));
        });
    }

    [Fact]
    public void A_month_of_hours_and_a_year_of_days_are_kept_whole()
    {
        // Times kept as milliseconds from the first one run out after 24 days: the start of a month was dropped.
        var (_, live) = Kit.Greeted();
        long start = TimeUtil.ToUnix(DateTime.Today.AddDays(-400));
        Ui.Run(() =>
        {
            live.LoadMinuteHistory([.. Enumerable.Range(0, 31 * 24).Select(h => new SystemMinute { Ts = start + h * 3600L, CpuTemp = 50, CpuTempMax = 60 })], 3600);
            var cpu = live.TempSeries[0];
            Assert.Equal(31 * 24, cpu.Minutes.Count);
            Assert.Equal((start + 1800) * 1000, cpu.Minutes.FirstTime);
            Assert.Equal((start + 30 * 86400 + 23 * 3600 + 1800) * 1000, cpu.Minutes.LastTime);
            Assert.Equal((50.0, 60.0, (double?)null), cpu.StatsAt(0));
            live.LoadMinuteHistory([.. Enumerable.Range(0, 366).Select(d => new SystemMinute { Ts = start + d * 86400L, CpuTemp = 40 + d % 30 })], 86400);
            Assert.Equal(366, cpu.Minutes.Count);
            Assert.Equal((start + 43200) * 1000, cpu.Minutes.FirstTime);
            Assert.Equal((start + 365 * 86400L + 43200) * 1000, cpu.Minutes.LastTime);
            Assert.Equal(40 + 200 % 30, cpu.Minutes.ValueAt(200));
        });
    }

    [Fact]
    public void Each_minute_keeps_its_average_highest_and_lowest_for_the_hover_box()
    {
        var (_, live) = Kit.Greeted();
        long start = TimeUtil.ToUnix(DateTime.Now.AddHours(-1));
        // A processor that jumps about: 72 on average over the minute, 98 at its hottest, 61 at its coolest. The last
        // minute is from before the lowest was kept.
        var minutes = Enumerable.Range(0, 3).Select(i => new SystemMinute
        {
            Ts = start + i * 60, CpuTemp = 72, CpuTempMax = 98, CpuTempMin = i < 2 ? 61 : null,
            GpuTemp = 60, GpuTempMax = 66, GpuTempMin = i < 2 ? 58 : null, GpuHotMax = 80, GpuMemMax = 84,
        }).ToList();
        Ui.Run(() =>
        {
            live.LoadMinuteHistory(minutes);
            // The line is the average…
            Assert.Equal([72.0, 72.0, 72.0], Enumerable.Range(0, 3).Select(i => live.TempSeries[0].Minutes.ValueAt(i)));
            // …and the rest is beside it.
            Assert.Equal((72.0, 98.0, 61.0), live.TempSeries[0].StatsAt(0));
            Assert.Equal((72.0, 98.0, (double?)null), live.TempSeries[0].StatsAt(2));
            Assert.Equal((60.0, 66.0, 58.0), live.TempSeries[1].StatsAt(1));
            // The hot spot and the memory were only kept as the minute's highest: that is their line there.
            Assert.Equal(((double?)null, 80.0, (double?)null), live.TempSeries[2].StatsAt(0));
            Assert.Equal(((double?)null, 84.0, (double?)null), live.TempSeries[3].StatsAt(0));
            Assert.Equal(80, live.TempSeries[2].Minutes.ValueAt(0));
            // With their average kept, the line is the average, like the CPU's and the GPU's.
            live.LoadMinuteHistory([new SystemMinute { Ts = start, GpuTemp = 60, GpuHotMax = 80, GpuHotAvg = 71.5, GpuMemMax = 84, GpuMemAvg = 77 }]);
            Assert.Equal(71.5, live.TempSeries[2].Minutes.ValueAt(0));
            Assert.Equal(77, live.TempSeries[3].Minutes.ValueAt(0));
            Assert.Equal((71.5, 80.0, (double?)null), live.TempSeries[2].StatsAt(0));
            live.LoadMinuteHistory(minutes);
            Assert.Null(live.TempSeries[0].StatsAt(3));
            Assert.Null(live.TempSeries[0].StatsAt(-1));
        });
    }

    // ── All sensors page ─────────────────────────────────────────────────

    private static List<string> Headers(LiveData live) => [.. live.SensorRows.OfType<HardwareNode>().Select(n => n.Name)];

    [Fact]
    public void Sensor_rows_are_header_sensors_end_for_every_card()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var rows = live.SensorRows;
            Assert.Equal(Pc.Count + Pc.Layout.Length * 2, rows.Count);
            int i = 0;
            foreach (var node in live.Hardware)
            {
                Assert.Same(node, rows[i++]);
                foreach (var s in node.Sensors) Assert.Same(s, rows[i++]);
                Assert.Same(node.End, rows[i++]);
                Assert.Same(node, node.End.Owner);
            }
        });
    }

    [Fact]
    public void Collapsed_cards_show_only_their_header()
    {
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var gpu = live.Hardware[1];
            live.ToggleExpandedCommand.Execute(gpu);
            Assert.False(gpu.IsExpanded);
            Assert.DoesNotContain(live.GpuTemp, live.SensorRows);
            Assert.Contains(gpu, live.SensorRows);
            Assert.Contains(gpu.End, live.SensorRows);
            Assert.Equal(["Test GPU"], settings.Current.CollapsedHardware);
            live.ToggleExpandedCommand.Execute(null); // ignored
            live.SetAllExpandedCommand.Execute("False");
            Assert.Equal(Pc.Layout.Length * 2, live.SensorRows.Count);
            Assert.Equal(Pc.Layout.Length, settings.Current.CollapsedHardware.Count);
            live.SetAllExpandedCommand.Execute("True");
            Assert.Empty(settings.Current.CollapsedHardware);
            Assert.Equal(Pc.Count + Pc.Layout.Length * 2, live.SensorRows.Count);
        });
    }

    [Theory]
    [InlineData("Temperature", 9)]
    [InlineData("Load", 9)]
    [InlineData("Fan", 4)]
    [InlineData("Power", 2)]
    [InlineData("Clock", 2)]
    [InlineData("Voltage", 2)]
    [InlineData("All", 34)]
    [InlineData("Something else", 34)]
    public void Type_filter_keeps_that_kind(string filter, int expected)
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.TypeFilter = filter;
            var shown = live.SensorRows.OfType<SensorItem>().ToList();
            Assert.Equal(expected, shown.Count);
            Assert.Equal(expected, live.AllSensors.Count(s => s.IsShown));
            // Cards with nothing left are left out entirely.
            Assert.All(live.SensorRows.OfType<HardwareNode>(), n => Assert.True(n.HasVisibleSensors));
        });
    }

    [Fact]
    public void Search_matches_sensor_names_and_whole_cards()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.SearchText = "  hot SPOT ";
            Assert.Equal(["GPU Hot Spot"], live.SensorRows.OfType<SensorItem>().Select(s => s.Name));
            Assert.Equal(["Test GPU"], Headers(live));
            live.SearchText = "test ssd";
            Assert.Equal(5, live.SensorRows.OfType<SensorItem>().Count());
            live.SearchText = "zzz";
            Assert.Empty(live.SensorRows);
            live.SearchText = null!;
            Assert.Equal(Pc.Count, live.SensorRows.OfType<SensorItem>().Count());
        });
    }

    [Fact]
    public void Search_finds_a_sensor_by_its_new_name()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.CpuPower!.Label = "Wattage";
            live.SearchText = "watt";
            Assert.Same(live.CpuPower, Assert.Single(live.SensorRows.OfType<SensorItem>()));
        });
    }

    [Fact]
    public void Search_takes_words_in_any_order_from_the_sensor_its_part_or_its_tag()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            // A word from the part, a word from the sensor.
            live.SearchText = "spot gpu";
            Assert.Equal(["GPU Hot Spot"], live.SensorRows.OfType<SensorItem>().Select(s => s.Name));
            live.SearchText = "board fan";
            Assert.Equal(["Fan #1", "Fan #2", "Fan #3"], live.SensorRows.OfType<SensorItem>().Select(s => s.Name));
            // The tag on the card ("DRIVE").
            live.SearchText = "drive";
            Assert.Equal(["Test SSD"], Headers(live));
            // Every word has to be there.
            live.SearchText = "gpu zzz";
            Assert.Empty(live.SensorRows);
        });
    }

    [Fact]
    public void A_renamed_sensor_is_still_found_by_the_name_its_part_gives_it()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.CpuPower!.Label = "Wattage";
            live.SearchText = "package cpu";
            Assert.Same(live.CpuPower, Assert.Single(live.SensorRows.OfType<SensorItem>()));
            Assert.Equal("Package  ·  click to rename", live.CpuPower.NameTip);
            Assert.Equal("Click to rename", live.CpuTemp!.NameTip);
        });
    }

    [Fact]
    public void An_empty_list_says_why()
    {
        var settings = Kit.OfflineSettings();
        var live = Kit.Live(settings);
        Ui.Run(() =>
        {
            Assert.Equal("No sensors yet", live.SensorsEmptyText);
            live.LoadHello(Pc.Hello("Test Board"));
            Assert.Null(live.SensorsEmptyText);
            var told = Kit.Changes(live, () => live.SearchText = "zzz");
            Assert.Contains(nameof(LiveData.SensorsEmptyText), told);
            Assert.Equal("No sensors match", live.SensorsEmptyText);
            live.SearchText = "";
            live.TypeFilter = "Fan";
            Assert.Equal(["Test GPU"], Headers(live));
            live.AllSensors.Single(s => s.Kind == SensorKind.Control).ToggleHidden();
            Assert.Equal("No sensors of this kind", live.SensorsEmptyText);
            live.TypeFilter = "All";
            Assert.Null(live.SensorsEmptyText);
            foreach (var s in live.AllSensors.Where(s => !s.IsHidden).ToList()) s.ToggleHidden();
            Assert.Equal("Every sensor is hidden", live.SensorsEmptyText);
            live.ShowHidden = true;
            Assert.Null(live.SensorsEmptyText);
        });
    }

    [Fact]
    public void Each_temperature_is_coloured_by_its_own_parts_steps_and_a_limit_not_at_all()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            string? Scale(string id) => live.AllSensors.Single(s => s.Id == id).TempScale;
            Assert.Equal("45,70,85", Scale("/cpu/temperature/0"));
            Assert.Equal("45,70,83", Scale("/gpu/temperature/0"));
            Assert.Equal("50,80,95", Scale("/gpu/temperature/1"));
            Assert.Equal("55,85,100", Scale("/gpu/temperature/2"));
            Assert.Equal("35,50,65", Scale("/nvme/0/temperature/0"));
            Assert.Equal("40,60,80", Scale("/lpc/temperature/0"));
            // A drive's "Warning Temperature" is a fixed limit, not a reading.
            Assert.Null(Scale("/nvme/0/temperature/10"));
            Assert.False(live.AllSensors.Single(s => s.Id == "/nvme/0/temperature/10").HasTempScale);
            // Only temperatures have steps.
            Assert.All(live.AllSensors.Where(s => s.Kind != SensorKind.Temperature), s => Assert.Null(s.TempScale));

            // A drive at 48 degrees is "good" by a drive's steps (it would be the same colour as a hot CPU by the CPU's).
            var brush = new Rigsight.Converters.ScaledTempBrushConverter();
            object At(double value, string? scale) => brush.Convert([value, scale!], typeof(System.Windows.Media.Brush), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Same(Rigsight.Converters.TempToBrushConverter.Good, At(48, "35,50,65"));
            Assert.Same(Rigsight.Converters.TempToBrushConverter.Warm, At(55, "35,50,65"));
            Assert.Same(Rigsight.Converters.TempToBrushConverter.Good, At(55, "45,70,85"));
            Assert.Same(Rigsight.Converters.TempToBrushConverter.None, At(double.NaN, "45,70,85"));
        });
    }

    [Fact]
    public void Two_parts_with_one_name_fold_and_move_each_on_its_own()
    {
        var hello = Pc.Hello();
        var ssd = hello.Hardware!.Single(h => h.Name == "Test SSD");
        hello.Hardware!.Add(new HardwareMeta
        {
            Name = ssd.Name, Type = ssd.Type,
            Sensors = [.. ssd.Sensors.Select(s => new SensorMeta { Id = s.Id.Replace("/nvme/0/", "/nvme/1/"), Name = s.Name, Kind = s.Kind })],
        });
        var (settings, live) = Kit.Greeted(hello: hello);
        Ui.Run(() =>
        {
            var first = live.Hardware[4];
            var second = live.Hardware[^1];
            Assert.Equal("Test SSD", first.Key);
            Assert.Equal("Test SSD #2", second.Key);
            Assert.Equal(first.Name, second.Name);

            live.ToggleExpandedCommand.Execute(second);
            Assert.True(first.IsExpanded);
            Assert.Equal(["Test SSD #2"], settings.Current.CollapsedHardware);

            live.MoveHardware(second, live.Hardware[0]); // the second drive to the top; the first stays put
            Assert.Same(second, live.SensorRows[0]);
            Assert.Equal(4, live.SensorRows.OfType<HardwareNode>().ToList().IndexOf(first) - 1);
            Assert.Equal("Test SSD #2", settings.Current.HardwareOrder[0]);
        });

        // The same PC again: each drive is where it was left.
        var (_, again) = Kit.Greeted(settings.Current, hello);
        Ui.Run(() =>
        {
            Assert.False(again.Hardware[^1].IsExpanded);
            Assert.True(again.Hardware[4].IsExpanded);
            Assert.Same(again.Hardware[^1], again.SensorRows[0]);
        });
    }

    [Fact]
    public void A_hidden_sensor_is_not_offered_to_widgets_dashboards_the_overlay_or_the_taskbar()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal(live.AllSensors, live.PickableSensors);
            var told = Kit.Changes(live, () => live.CpuTemp!.ToggleHidden());
            Assert.Contains(nameof(LiveData.PickableSensors), told);
            Assert.DoesNotContain(live.CpuTemp, live.PickableSensors);
            Assert.Equal(Pc.Count - 1, live.PickableSensors.Count);
            // Still there for whatever already shows it.
            Assert.Contains(live.CpuTemp, live.AllSensors);
            // Searching the page doesn't change what is offered.
            told = Kit.Changes(live, () => live.SearchText = "gpu");
            Assert.DoesNotContain(nameof(LiveData.PickableSensors), told);
            live.UnhideAllCommand.Execute(null);
            Assert.Equal(live.AllSensors, live.PickableSensors);
        });
    }

    [Fact]
    public void A_folded_card_says_its_parts_key_readings()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Pc.Tick(1000, 40, ("/lpc/temperature/0", 38), ("/lpc/temperature/1", 61), ("/lpc/fan/2", 0)));
            string Says(int card) => string.Join(" | ", live.Hardware[card].Keys.Where(k => k.IsShown)
                .Select(k => k.Sensor is { } s ? (s.ShortValue + " " + k.Label).Trim() : k.Text));
            // CPU: temperature, load, power. GPU the same. A drive: temperature and how full. Memory: in use.
            Assert.Equal([live.CpuTemp, live.CpuLoad, live.CpuPower], live.Hardware[0].Keys.Select(k => k.Sensor));
            Assert.Equal([live.GpuTemp, live.GpuLoad, live.GpuPower], live.Hardware[1].Keys.Select(k => k.Sensor));
            Assert.EndsWith("load", live.Hardware[0].Keys[1].Label);
            Assert.Equal("in use", Assert.Single(live.Hardware[2].Keys).Label);
            Assert.Equal(["/nvme/0/temperature/0", "/nvme/0/load/30"], live.Hardware[4].Keys.Select(k => k.Sensor!.Id));
            // A sensor chip's readings have no names worth showing: its warmest, and how many of its fans spin.
            Assert.Equal("61° warmest | 2 fans spinning", Says(5));
            // They follow the readings.
            live.ApplyTick(Pc.Tick(2000, 40, ("/lpc/temperature/0", 70), ("/lpc/temperature/1", 61), ("/lpc/fan/2", 900)));
            Assert.Equal("70° warmest | 3 fans spinning", Says(5));
            // A reading with no value isn't shown as a dash; no fan spinning says nothing.
            live.ApplyTick(Pc.Tick(3000, 40, ("/cpu/power/0", null), ("/lpc/fan/0", 0), ("/lpc/fan/1", 0), ("/lpc/fan/2", 0)));
            Assert.False(live.Hardware[0].Keys[2].IsShown);
            Assert.DoesNotContain("spinning", Says(5));
        });
    }

    private static string Line(GlanceRow r) => $"{r.Sensor!.Id} | {r.Note}";

    [Fact]
    public void The_pane_ranks_the_warmest_parts_each_against_its_own_limit()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            // The drive reports its own limit (its "Warning Temperature" reads 81): 49 of 81. The CPU has none of its
            // own, so the step it turns red at stands in: 60 of 85. The GPU's hot spot at 76 of 95 is its nearest.
            live.ApplyTick(Pc.Tick(1000, 40, ("/nvme/0/temperature/0", 49), ("/nvme/0/temperature/10", 81), ("/cpu/temperature/0", 60), ("/cpu/temperature/1", 55),
                ("/gpu/temperature/0", 50), ("/gpu/temperature/1", 76), ("/gpu/temperature/2", 70), ("/lpc/temperature/0", 79), ("/lpc/temperature/1", 78)));
            Assert.Equal(["/gpu/temperature/1 | Test GPU", "/cpu/temperature/0 | Test CPU", "/nvme/0/temperature/0 | Test SSD · its limit is 81°"], Warm());
            Assert.Equal(76.0 / 95, live.Warmest[0].Bar, 6);
            Assert.Equal(49.0 / 81, live.Warmest[2].Bar, 6);
            Assert.All(live.Warmest, r => Assert.True(r.HasBar));
            // One reading a part; the limit a part reports is never itself ranked; a board chip's unnamed readings
            // aren't either ("Temperature #3" at 79 would lead the list, and nobody knows what it measures).
            Assert.DoesNotContain(live.Warmest, r => r.Sensor!.Id.StartsWith("/lpc/") || r.Sensor.Id == "/nvme/0/temperature/10");
            // Given a name, a board reading is one the user knows: ranked, against the step boards turn red at.
            live.AllSensors.Single(x => x.Id == "/lpc/temperature/0").Label = "VRM";
            live.ApplyTick(Pc.Tick(2000, 40, ("/nvme/0/temperature/0", 49), ("/nvme/0/temperature/10", 81), ("/cpu/temperature/0", 60),
                ("/gpu/temperature/1", 76), ("/lpc/temperature/0", 79)));
            Assert.Equal("/lpc/temperature/0 | Test Board", Warm()[0]);
            // The drive heats up: against its own 81 it stays behind a board at 79 of 80, where 65 would have put it first.
            live.ApplyTick(Pc.Tick(3000, 40, ("/nvme/0/temperature/0", 70), ("/nvme/0/temperature/10", 81), ("/lpc/temperature/0", 79)));
            Assert.Equal(["/lpc/temperature/0", "/nvme/0/temperature/0"], live.Warmest.Take(2).Select(r => r.Sensor!.Id));
            // A hidden sensor isn't in the pane.
            live.AllSensors.Single(x => x.Id == "/lpc/temperature/0").ToggleHidden();
            Assert.DoesNotContain(live.Warmest, r => r.Sensor!.Id == "/lpc/temperature/0");

            List<string> Warm() => [.. live.Warmest.Select(Line)];
        });
    }

    [Fact]
    public void The_pane_lists_the_main_loads_the_spinning_fans_and_the_power()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Pc.Tick(1000, 40, ("/cpu/load/0", 12), ("/gpu/load/0", 97), ("/gpu/load/1", 45), ("/ram/load/0", 64),
                ("/lpc/fan/0", 1750), ("/lpc/fan/1", 0), ("/lpc/fan/2", 2250), ("/cpu/power/0", 88), ("/gpu/power/0", 285)));
            Assert.Equal(["/gpu/load/0", "/ram/load/0", "/gpu/load/1", "/cpu/load/0"], live.Hardest.Select(r => r.Sensor!.Id));
            Assert.Equal(0.97, live.Hardest[0].Bar, 6);
            Assert.Equal("Test GPU", live.Hardest[0].Note);
            // Fans that spin, fastest first; one standing still isn't listed.
            Assert.Equal(["/lpc/fan/2", "/lpc/fan/0"], live.FastestFans.Select(r => r.Sensor!.Id));
            Assert.All(live.FastestFans, r => Assert.False(r.HasBar));
            Assert.Equal(["/gpu/power/0", "/cpu/power/0"], live.PowerDraw.Select(r => r.Sensor!.Id));
            Assert.Equal(Units.Format(SensorKind.Power, 373) + " together", live.PowerNote);

            // The rows stay the same objects from one second to the next (nothing is built again); only what they show moves.
            var rows = live.Hardest.ToList();
            live.ApplyTick(Pc.Tick(2000, 40, ("/cpu/load/0", 99), ("/gpu/load/0", 10), ("/gpu/load/1", 45), ("/ram/load/0", 64)));
            Assert.Equal(rows, live.Hardest);
            Assert.Equal("/cpu/load/0", live.Hardest[0].Sensor!.Id);

            // A PC without a graphics card has no GPU lines, and one power figure is not "together".
            live.LoadHello(Pc.Hello("Test GPU"));
            live.ApplyTick(new Rigsight.Core.Protocol.AgentMessage { T = "tick", Time = 3000, Values = [.. live.AllSensors.Select(_ => (float?)30)] });
            Assert.DoesNotContain(live.Hardest, r => r.Sensor!.Id.StartsWith("/gpu/"));
            Assert.Single(live.PowerDraw);
            Assert.Equal("", live.PowerNote);
        });
    }

    [Fact]
    public void A_line_of_the_pane_jumps_to_its_sensor_in_the_list()
    {
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyTick(Pc.Tick(1000, 40, ("/gpu/power/0", 285)));
            // The GPU's card is folded and a search is on: the row isn't in the list.
            live.ToggleExpandedCommand.Execute(live.Hardware[1]);
            live.SearchText = "ssd";
            live.TypeFilter = "Temperature";
            Assert.DoesNotContain(live.GpuPower, live.SensorRows);
            SensorItem? shown = null;
            live.Revealed += s => shown = s;

            live.RevealCommand.Execute(live.PowerDraw.First(r => r.Sensor == live.GpuPower));
            Assert.Same(live.GpuPower, shown);
            Assert.Contains(live.GpuPower, live.SensorRows);
            Assert.Equal("", live.SearchText);
            Assert.Equal("All", live.TypeFilter);
            Assert.True(live.Hardware[1].IsExpanded);
            Assert.DoesNotContain("Test GPU", settings.Current.CollapsedHardware);
            // Its row stays lit until another is jumped to.
            Assert.True(live.GpuPower!.IsSelected);
            live.RevealCommand.Execute(live.PowerDraw.First(r => r.Sensor == live.CpuPower));
            Assert.False(live.GpuPower.IsSelected);
            Assert.True(live.CpuPower!.IsSelected);
            live.RevealCommand.Execute(null); // nothing: nothing happens
            Assert.True(live.CpuPower.IsSelected);
        });
    }

    [Fact]
    public void A_sensors_menu_adds_it_to_and_removes_it_from_the_taskbar_and_the_overlay()
    {
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var sensor = live.GpuPower!;
            string id = sensor.Id;
            Assert.Equal(["The taskbar", "The overlay"], live.PlacesFor(sensor).Select(p => p.Name));
            Assert.All(live.PlacesFor(sensor), p => Assert.False(p.IsShown));

            live.PlacesFor(sensor)[0].ToggleCommand.Execute(null);
            live.PlacesFor(sensor)[1].ToggleCommand.Execute(null);
            Assert.Contains(id, settings.Current.TraySensors);
            Assert.Contains(settings.Current.Overlay.Sensors, o => o.Id == id);
            Assert.All(live.PlacesFor(sensor), p => Assert.True(p.IsShown));
            // Another sensor has its own places.
            Assert.All(live.PlacesFor(live.CpuLoad!), p => Assert.False(p.IsShown));

            live.PlacesFor(sensor)[0].ToggleCommand.Execute(null);
            live.PlacesFor(sensor)[1].ToggleCommand.Execute(null);
            Assert.DoesNotContain(id, settings.Current.TraySensors);
            Assert.DoesNotContain(settings.Current.Overlay.Sensors, o => o.Id == id);

            // The overlay takes ten: full, it can't be added to (one already on it can still be taken off).
            settings.Update(s => s.Overlay.Sensors = [.. live.AllSensors.Where(x => x.Id != id).Take(OverlaySettings.MaxSensors).Select(x => new OverlaySensor { Id = x.Id })]);
            Assert.False(live.PlacesFor(sensor)[1].CanToggle);
            live.PlacesFor(sensor)[1].ToggleCommand.Execute(null);
            Assert.Equal(OverlaySettings.MaxSensors, settings.Current.Overlay.Sensors.Count);
            var onIt = live.AllSensors.First(x => x.Id == settings.Current.Overlay.Sensors[0].Id);
            Assert.True(live.PlacesFor(onIt)[1].CanToggle);
            Assert.True(live.PlacesFor(onIt)[1].IsShown);
        });
    }

    [Fact]
    public void A_sensors_menu_offers_each_widget_that_is_on_and_can_show_it()
    {
        var start = SeedData.QuietSettings();
        // Two widgets on: a custom one of gauges (temperatures and percentages only) and a custom one of tiles (anything).
        start.Widgets.Add(new WidgetConfig { Style = WidgetStyle.Custom, Id = "custom-gauges", Name = "Dials", Layout = WidgetLayout.Gauges, Enabled = true, Items = [] });
        start.Widgets.Add(new WidgetConfig { Style = WidgetStyle.Custom, Id = "custom-tiles", Name = "Numbers", Layout = WidgetLayout.Tiles, Enabled = true, Items = [] });
        start.Widgets.Add(new WidgetConfig { Style = WidgetStyle.Custom, Id = "custom-off", Name = "Off", Layout = WidgetLayout.Tiles, Enabled = false, Items = [] });
        var (settings, live) = Kit.Greeted(start);
        Ui.Run(() =>
        {
            List<WidgetItem> Items(string id) => WidgetCatalog.ItemsOf(settings.Current.Widgets.Single(w => w.Id == id));
            // A temperature fits both; a widget that is off isn't offered. Watts don't fit a gauge.
            Assert.Equal(["The taskbar", "The overlay", "Widget · Dials", "Widget · Numbers"], live.PlacesFor(live.GpuTemp!).Select(p => p.Name));
            Assert.Equal(["The taskbar", "The overlay", "Widget · Numbers"], live.PlacesFor(live.GpuPower!).Select(p => p.Name));

            string item = WidgetCatalog.SensorPrefix + live.GpuPower!.Id;
            live.PlacesFor(live.GpuPower)[2].ToggleCommand.Execute(null);
            Assert.Contains(Items("custom-tiles"), i => i.Id == item);
            Assert.True(live.PlacesFor(live.GpuPower)[2].IsShown);
            live.PlacesFor(live.GpuPower)[2].ToggleCommand.Execute(null);
            Assert.DoesNotContain(Items("custom-tiles"), i => i.Id == item);
        });
    }

    [Fact]
    public void Copying_a_sensor_copies_its_reading_as_shown()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            string? copied = null;
            live.SetClipboard = text => copied = text;
            live.ApplyTick(Pc.Tick(1000, 40, ("/gpu/power/0", 285.4f)));
            Assert.True(live.Copy(live.GpuPower!));
            Assert.Equal(live.GpuPower!.FormattedValue, copied);
            // The clipboard held by another program: said by the answer, not thrown.
            live.SetClipboard = _ => throw new System.Runtime.InteropServices.COMException("busy");
            Assert.False(live.Copy(live.GpuPower));
        });
    }

    [Fact]
    public void Renaming_a_sensor_saves_its_label_and_blank_or_original_resets_it()
    {
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var s = live.CpuTemp!;
            s.Label = "  CPU die  ";
            Assert.Equal("CPU die", settings.Current.SensorLabels[s.Id]);
            Assert.Equal("CPU die", s.DisplayName);
            s.Label = "   ";
            Assert.False(settings.Current.SensorLabels.ContainsKey(s.Id));
            Assert.Equal(s.Name, s.Label);
            s.Label = "X";
            s.Label = s.Name;
            Assert.False(settings.Current.SensorLabels.ContainsKey(s.Id));
            Assert.Null(s.CustomLabel);
            // The same name again isn't a change (no settings write).
            int changes = 0;
            settings.Changed += () => changes++;
            s.Label = s.Name;
            s.Label = null!;
            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public void Hiding_and_showing_sensors()
    {
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal(0, live.HiddenCount);
            live.ToggleHiddenCommand.Execute(live.CpuTemp);
            live.ToggleHiddenCommand.Execute(live.Hardware[0]); // a recycled row bound to a header: ignored
            live.ToggleHiddenCommand.Execute(null);
            Assert.True(live.CpuTemp!.IsHidden);
            Assert.Contains(live.CpuTemp.Id, settings.Current.HiddenSensors);
            Assert.Equal(1, live.HiddenCount);
            Assert.Equal("Show 1 hidden sensor", live.ShowHiddenText);
            Assert.DoesNotContain(live.CpuTemp, live.SensorRows);
            live.ShowHidden = true;
            Assert.Contains(live.CpuTemp, live.SensorRows);
            live.GpuTemp!.ToggleHidden();
            Assert.Equal("Show 2 hidden sensors", live.ShowHiddenText);
            live.UnhideAllCommand.Execute(null);
            Assert.Empty(settings.Current.HiddenSensors);
            Assert.All(live.AllSensors, s => Assert.False(s.IsHidden));
            // Nothing hidden any more: "show hidden" switches itself off.
            Assert.False(live.ShowHidden);
        });
    }

    [Fact]
    public void Dragging_a_card_saves_the_new_order()
    {
        var (settings, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var h = live.Hardware;
            live.MoveHardware(h[5], h[0]); // board to the top
            Assert.Equal(["Test Board", "Test CPU", "Test GPU", "Total Memory", "Virtual Memory", "Test SSD"], Headers(live));
            Assert.Equal(Headers(live), settings.Current.HardwareOrder);
            live.MoveHardware(h[0], h[4]); // CPU down, to where the SSD is
            Assert.Equal(["Test Board", "Test GPU", "Total Memory", "Virtual Memory", "Test SSD", "Test CPU"], Headers(live));
            // Onto itself, or with a card that isn't there: nothing happens.
            live.MoveHardware(h[1], h[1]);
            live.MoveHardware(new HardwareNode("Ghost", "Cpu"), h[1]);
            Assert.Equal(["Test Board", "Test GPU", "Total Memory", "Virtual Memory", "Test SSD", "Test CPU"], Headers(live));
        });
    }

    [Fact]
    public void Cards_not_in_the_saved_order_follow_the_ones_that_are()
    {
        var start = SeedData.QuietSettings();
        start.HardwareOrder = ["Test SSD", "Gone card", "Test GPU"];
        var (_, live) = Kit.Greeted(start);
        Ui.Run(() => Assert.Equal(["Test SSD", "Test GPU", "Test CPU", "Total Memory", "Virtual Memory", "Test Board"], Headers(live)));
    }

    [Fact]
    public void While_reordering_every_card_shows_just_its_header()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.IsReordering = true;
            Assert.Equal(Pc.Layout.Length * 2, live.SensorRows.Count);
            Assert.Empty(live.SensorRows.OfType<SensorItem>());
            live.IsReordering = false;
            Assert.Equal(Pc.Count, live.SensorRows.OfType<SensorItem>().Count());
        });
    }

    [Fact]
    public void GroupOf_finds_the_card_of_any_row()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var gpu = live.Hardware[1];
            Assert.Same(gpu, live.GroupOf(gpu));
            Assert.Same(gpu, live.GroupOf(gpu.End));
            Assert.Same(gpu, live.GroupOf(live.GpuTemp));
            Assert.Null(live.GroupOf("text"));
            Assert.Null(live.GroupOf(null));
            Assert.Null(live.GroupOf(live.Compressed));
        });
    }

    [Fact]
    public void Card_summaries_count_sensors_and_hidden_ones()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Equal("9 sensors", live.Hardware[0].Summary);
            Assert.Equal("1 sensor", live.Hardware[3].Summary);
            live.CpuTemp!.ToggleHidden();
            live.CpuLoad!.ToggleHidden();
            Assert.Equal("9 sensors  ·  2 hidden", live.Hardware[0].Summary);
        });
    }

    [Fact]
    public void Resolve_finds_sensors_by_key_or_id()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            Assert.Same(live.GpuPower, live.Resolve("key:" + KeySensors.GpuPower));
            Assert.Same(live.Fans[0], live.Resolve("/lpc/fan/0"));
            Assert.Null(live.Resolve(null));
            Assert.Null(live.Resolve("key:nope"));
            Assert.Null(live.Resolve("key:"));
            Assert.Null(live.Resolve(""));
        });
    }

    [Fact]
    public void Duplicate_sensor_ids_resolve_to_the_first()
    {
        var hello = Pc.Hello();
        hello.Hardware![1].Sensors.Add(new SensorMeta { Id = "/gpu/temperature/0", Name = "GPU Core (again)", Kind = SensorKind.Temperature });
        var (_, live) = Kit.Greeted(hello: hello);
        Ui.Run(() => Assert.Equal("GPU Core", live.Resolve("/gpu/temperature/0")!.Name));
    }

    [Fact]
    public void Real_pc_fixture_loads_and_ticks()
    {
        var (_, live) = Kit.Greeted(hello: Fixtures.Hello());
        Ui.Run(() =>
        {
            Assert.Equal(Fixtures.SensorCount, live.AllSensors.Count);
            Assert.Equal("AMD Ryzen 7 5700X3D", live.CpuName);
            Assert.Equal("NVIDIA GeForce RTX 3080 Ti", live.GpuName);
            Assert.Equal(3, live.Drives.Count);
            Assert.All(live.Drives, d => Assert.Equal("Good", d.Health?.Status));
            Assert.Equal(214, live.CpuTemp!.History.Count);
            Assert.True(live.Drives[0].Temperature!.History.Count >= 2);
            live.ApplyTick(Fixtures.Tick());
            Assert.NotNull(live.CpuTemp.Value);
            Assert.NotEmpty(live.RamText);
            for (int i = 1; i < 5; i++) live.ApplyTick(Fixtures.Tick(i));
            Assert.All(live.Fans, f => Assert.True(f.Value > 0 || f.Max > 0));
        });
    }
}

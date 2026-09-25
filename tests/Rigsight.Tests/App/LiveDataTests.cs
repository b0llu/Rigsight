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
            Assert.Equal(1, raised);
            Assert.Contains(nameof(LiveData.ChartWindowSeconds), changed);
            Assert.Contains(nameof(LiveData.IsChartDay), changed);
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
            Assert.StartsWith("Click a name", live.HiddenHint);
            live.ToggleHiddenCommand.Execute(live.CpuTemp);
            live.ToggleHiddenCommand.Execute(live.Hardware[0]); // a recycled row bound to a header: ignored
            live.ToggleHiddenCommand.Execute(null);
            Assert.True(live.CpuTemp!.IsHidden);
            Assert.Contains(live.CpuTemp.Id, settings.Current.HiddenSensors);
            Assert.Equal(1, live.HiddenCount);
            Assert.Equal("Show 1 hidden sensor", live.ShowHiddenText);
            Assert.Contains("1 sensor is hidden", live.HiddenHint);
            Assert.DoesNotContain(live.CpuTemp, live.SensorRows);
            live.ShowHidden = true;
            Assert.Contains(live.CpuTemp, live.SensorRows);
            live.GpuTemp!.ToggleHidden();
            Assert.Equal("Show 2 hidden sensors", live.ShowHiddenText);
            Assert.Contains("2 sensors are hidden", live.HiddenHint);
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

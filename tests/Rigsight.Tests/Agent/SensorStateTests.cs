using System.Text.Json;
using Rigsight.Agent.Sensors;
using Rigsight.Core;

namespace Rigsight.Tests.Agent;

/// <summary>The last hour of key sensors, kept for the app's charts and the graph widget.</summary>
public class KeyHistoryTests
{
    private static Func<string, double?> All(double? value) => _ => value;

    [Fact]
    public void Starts_empty_with_a_series_for_every_history_key()
    {
        var history = new KeyHistory();
        var snapshot = history.Snapshot();
        Assert.Equal(KeySensors.HistoryKeys.Order(), snapshot.Select(s => s.Key).Order());
        Assert.All(snapshot, s => Assert.Empty(s.Times));
        Assert.All(snapshot, s => Assert.Empty(s.Values));
        Assert.Empty(history.Recent(KeySensors.CpuTemp, 300_000));
    }

    [Fact]
    public void Readings_are_kept_in_order_rounded_to_a_tenth()
    {
        var history = new KeyHistory();
        history.Add(1000, key => key == KeySensors.CpuTemp ? 45.26 : key == KeySensors.GpuTemp ? 50.04 : null);
        history.Add(2000, key => key == KeySensors.CpuTemp ? 46.0 : null);
        var cpu = history.Snapshot().Single(s => s.Key == KeySensors.CpuTemp);
        Assert.Equal([1000L, 2000L], cpu.Times);
        Assert.Equal(new float?[] { 45.3f, 46.0f }, cpu.Values);
        var gpu = history.Snapshot().Single(s => s.Key == KeySensors.GpuTemp);
        Assert.Equal(new float?[] { 50.0f, null }, gpu.Values);
    }

    [Fact]
    public void A_missing_reading_is_kept_as_a_gap()
    {
        var history = new KeyHistory();
        history.Add(1000, All(40));
        history.Add(2000, All(null));
        history.Add(3000, All(42));
        var s = history.Snapshot().Single(x => x.Key == KeySensors.RamLoad);
        Assert.Equal(new float?[] { 40f, null, 42f }, s.Values);
        var recent = history.Recent(KeySensors.RamLoad, 60_000);
        Assert.Equal(3, recent.Length);
        Assert.True(float.IsNaN(recent[1]));
    }

    [Fact]
    public void Only_the_last_1800_readings_are_kept()
    {
        var history = new KeyHistory();
        for (int i = 0; i < 2500; i++) history.Add(i * 1000L, All(i));
        var s = history.Snapshot().Single(x => x.Key == KeySensors.CpuTemp);
        Assert.Equal(1800, s.Times.Length);
        Assert.Equal(700_000, s.Times[0]);
        Assert.Equal(2_499_000, s.Times[^1]);
        Assert.Equal(700f, s.Values[0]);
        Assert.Equal(2499f, s.Values[^1]);
        for (int i = 1; i < s.Times.Length; i++) Assert.True(s.Times[i] > s.Times[i - 1]);
    }

    [Theory]
    [InlineData(1799)]
    [InlineData(1800)]
    [InlineData(1801)]
    [InlineData(3600)]
    public void Wrapping_around_keeps_the_newest_in_order(int count)
    {
        var history = new KeyHistory();
        for (int i = 0; i < count; i++) history.Add(i, All(i));
        var s = history.Snapshot().Single(x => x.Key == KeySensors.GpuLoad);
        int kept = Math.Min(count, 1800);
        Assert.Equal(kept, s.Times.Length);
        Assert.Equal(Enumerable.Range(count - kept, kept).Select(i => (long)i), s.Times);
    }

    [Fact]
    public void Recent_covers_the_window_before_the_newest_reading()
    {
        var history = new KeyHistory();
        for (int i = 0; i <= 600; i++) history.Add(1_000_000 + i * 1000L, All(i));
        var recent = history.Recent(KeySensors.CpuTemp, 300_000);
        Assert.Equal(301, recent.Length); // 300..600 s, both ends included
        Assert.Equal(300f, recent[0]);
        Assert.Equal(600f, recent[^1]);
        Assert.Single(history.Recent(KeySensors.CpuTemp, 0));
    }

    [Fact]
    public void Recent_of_a_key_without_history_is_empty()
    {
        var history = new KeyHistory();
        history.Add(1000, All(1));
        Assert.Empty(history.Recent(KeySensors.CpuPower, 300_000));
        Assert.Empty(history.Recent("nonsense", 300_000));
    }

    [Fact]
    public void Snapshots_are_copies()
    {
        var history = new KeyHistory();
        history.Add(1000, All(1));
        var first = history.Snapshot();
        first[0].Values[0] = 99;
        history.Add(2000, All(2));
        Assert.Single(first[0].Times);
        Assert.Equal(1f, history.Snapshot().Single(s => s.Key == first[0].Key).Values[0]);
    }

    [Fact]
    public async Task Reading_while_the_sampler_adds_is_safe()
    {
        // The app's connection thread builds a hello while the sampler thread adds readings.
        var history = new KeyHistory();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var writer = Task.Run(() =>
        {
            long t = 0;
            while (!stop.IsCancellationRequested) history.Add(t++, All(t % 90));
        });
        while (!stop.IsCancellationRequested)
        {
            foreach (var s in history.Snapshot()) Assert.Equal(s.Times.Length, s.Values.Length);
            history.Recent(KeySensors.CpuTemp, 300_000);
        }
        await writer;
    }

    [Fact]
    public void An_unopened_sensor_host_gives_gaps()
    {
        var history = new KeyHistory();
        history.Add(1000, new SensorHost());
        Assert.All(history.Snapshot(), s => Assert.Equal(new float?[] { null }, s.Values));
    }
}

/// <summary>Each sensor's lowest and highest reading since midnight, kept across restarts.</summary>
public class DailyExtremesTests
{
    private DateTime _now = new(2026, 6, 10, 15, 0, 0);
    private DailyExtremes New() => new(() => _now);

    [Fact]
    public void The_first_reading_is_both_the_low_and_the_high()
    {
        var x = New();
        Assert.False(x.Dirty);
        x.Observe("/cpu/0/temperature/0", 45);
        Assert.True(x.Dirty);
        Assert.Equal([45.0, 45.0], x.Snapshot()["/cpu/0/temperature/0"]);
        Assert.Equal("2026-06-10", x.Day);
    }

    [Fact]
    public void Readings_widen_the_range()
    {
        var x = New();
        foreach (var v in new[] { 50.0, 40, 60, 55, 39.5, 61 }) x.Observe("s", v);
        Assert.Equal([39.5, 61], x.Snapshot()["s"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(null)]
    public void Non_readings_are_ignored(double? value)
    {
        var x = New();
        x.Observe("s", value);
        Assert.Empty(x.Snapshot());
        Assert.False(x.Dirty);
        Assert.Null(x.TakeChanges());
    }

    [Fact]
    public void Negative_and_zero_readings_count()
    {
        var x = New();
        x.Observe("s", 0);
        x.Observe("s", -12.5);
        Assert.Equal([-12.5, 0], x.Snapshot()["s"]);
    }

    [Fact]
    public void Changes_are_handed_out_once()
    {
        var x = New();
        x.Observe("a", 1);
        x.Observe("b", 2);
        var changes = x.TakeChanges()!;
        Assert.Equal(["a", "b"], changes.Keys.Order());
        Assert.Null(x.TakeChanges());

        x.Observe("a", 0.5);   // wider
        x.Observe("b", 2);     // same: no change
        changes = x.TakeChanges()!;
        Assert.Equal(["a"], changes.Keys);
        Assert.Equal([0.5, 1], changes["a"]);
    }

    [Fact]
    public void A_reading_inside_the_range_is_no_change()
    {
        var x = New();
        x.Observe("a", 10);
        x.Observe("a", 20);
        x.TakeChanges();
        x.Dirty = false;
        x.Observe("a", 15);
        x.Observe("a", 10);
        x.Observe("a", 20);
        Assert.Null(x.TakeChanges());
        Assert.False(x.Dirty);
    }

    [Fact]
    public void Handed_out_ranges_are_copies()
    {
        var x = New();
        x.Observe("a", 10);
        var snapshot = x.Snapshot();
        var changes = x.TakeChanges()!;
        snapshot["a"][0] = -1;
        changes["a"][1] = 999;
        Assert.Equal([10.0, 10.0], x.Snapshot()["a"]);
    }

    [Fact]
    public void Include_widens_with_earlier_lows_and_highs()
    {
        var x = New();
        x.Observe("cpu", 50);
        x.Include("cpu", 35, 88);
        x.Include("cpu", null, 90);
        x.Include("cpu", 40, null);
        x.Include("gpu", null, null);
        Assert.Equal([35.0, 90], x.Snapshot()["cpu"]);
        Assert.False(x.Snapshot().ContainsKey("gpu"));
    }

    [Fact]
    public void Midnight_starts_a_new_day()
    {
        var x = New();
        x.Observe("a", 70);
        x.TakeChanges();
        x.Dirty = false;

        _now = new DateTime(2026, 6, 10, 23, 59, 59);
        x.BeginTick();
        Assert.Equal("2026-06-10", x.Day);
        Assert.Single(x.Snapshot());

        _now = new DateTime(2026, 6, 11, 0, 0, 1);
        x.BeginTick();
        Assert.Equal("2026-06-11", x.Day);
        Assert.Empty(x.Snapshot());
        Assert.True(x.Dirty);          // the empty day gets saved over yesterday's
        Assert.Null(x.TakeChanges());

        x.Observe("a", 30);
        Assert.Equal([30.0, 30.0], x.Snapshot()["a"]);
    }

    [Fact]
    public void A_new_day_is_noticed_by_every_read_even_without_a_tick()
    {
        var x = New();
        x.Observe("a", 70);
        _now = _now.AddDays(1);
        Assert.Empty(x.Snapshot());
        x.Observe("a", 1);
        _now = _now.AddDays(1);
        Assert.Null(x.TakeChanges());
        Assert.Equal("2026-06-12", x.Day);
    }

    [Fact]
    public void Several_days_asleep_start_a_fresh_day()
    {
        var x = New();
        x.Observe("a", 70);
        _now = _now.AddDays(5);
        x.BeginTick();
        Assert.Equal("2026-06-15", x.Day);
        Assert.Empty(x.Snapshot());
    }

    [Fact]
    public void The_clock_going_back_keeps_the_day()
    {
        var x = New();
        x.Observe("a", 70);
        _now = _now.AddHours(-20); // yesterday, by the clock
        x.BeginTick();
        Assert.Equal("2026-06-10", x.Day);
        Assert.Single(x.Snapshot());
    }

    [Fact]
    public void Saved_ranges_come_back_after_a_restart()
    {
        var x = New();
        x.Observe("cpu", 41.25);
        x.Observe("cpu", 88.5);
        x.Observe("gpu", 33);
        string json = x.Serialize();

        var y = New();
        y.Load(json);
        Assert.Equal([41.25, 88.5], y.Snapshot()["cpu"]);
        Assert.Equal([33.0, 33.0], y.Snapshot()["gpu"]);
        Assert.True(y.Dirty);
        Assert.Equal(["cpu", "gpu"], y.TakeChanges()!.Keys.Order());
    }

    [Fact]
    public void Loading_widens_what_was_seen_since_the_start()
    {
        var x = New();
        x.Observe("cpu", 40);
        x.Observe("cpu", 90);
        var y = New();
        y.Observe("cpu", 60);
        y.Observe("cpu", 95);
        y.Load(x.Serialize());
        Assert.Equal([40.0, 95], y.Snapshot()["cpu"]);
    }

    [Fact]
    public void Ranges_saved_on_another_day_are_ignored()
    {
        var x = New();
        x.Observe("cpu", 99);
        string json = x.Serialize();
        _now = _now.AddDays(1);
        var y = New();
        y.Load(json);
        Assert.Empty(y.Snapshot());
        Assert.False(y.Dirty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Day\":\"2026-06-10\"}")]
    [InlineData("{\"Day\":\"2026-06-10\",\"Values\":null}")]
    [InlineData("{\"Day\":\"2026-06-10\",\"Values\":{\"a\":\"x\"}}")]
    [InlineData("{\"Day\":42,\"Values\":{}}")]
    public void An_unreadable_save_only_costs_the_earlier_range(string? json)
    {
        var x = New();
        x.Observe("a", 5);
        x.Load(json);
        Assert.Equal([5.0, 5.0], x.Snapshot()["a"]);
    }

    [Fact]
    public void Malformed_entries_are_skipped_and_the_rest_kept()
    {
        var x = New();
        x.Load("{\"Day\":\"2026-06-10\",\"Values\":{\"bad\":[1],\"worse\":[1,2,3],\"empty\":[],\"good\":[3,7]}}");
        Assert.Equal(["good"], x.Snapshot().Keys);
        Assert.Equal([3.0, 7], x.Snapshot()["good"]);
    }

    [Fact]
    public void The_save_holds_the_day_and_every_range()
    {
        var x = New();
        x.Observe("/gpu-nvidia/0/temperature/0", 51);
        using var doc = JsonDocument.Parse(x.Serialize());
        Assert.Equal("2026-06-10", doc.RootElement.GetProperty("Day").GetString());
        var range = doc.RootElement.GetProperty("Values").GetProperty("/gpu-nvidia/0/temperature/0");
        Assert.Equal([51.0, 51.0], range.EnumerateArray().Select(e => e.GetDouble()));
    }

    [Fact]
    public void The_day_is_written_the_same_in_every_language()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA"); // another calendar
            Assert.Equal("2026-06-10", New().Day);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Many_sensors_over_a_day()
    {
        var x = New();
        var rnd = new Random(5);
        var expected = new Dictionary<string, (double Lo, double Hi)>();
        for (int tick = 0; tick < 3000; tick++)
        {
            x.BeginTick();
            for (int s = 0; s < 150; s++)
            {
                double v = rnd.NextDouble() * 100;
                string id = $"/s/{s}";
                x.Observe(id, v);
                expected[id] = expected.TryGetValue(id, out var r) ? (Math.Min(r.Lo, v), Math.Max(r.Hi, v)) : (v, v);
            }
        }
        var snapshot = x.Snapshot();
        Assert.Equal(150, snapshot.Count);
        foreach (var (id, (lo, hi)) in expected) Assert.Equal([lo, hi], snapshot[id]);
    }
}

/// <summary>The sensor host without hardware (the parts that don't need LibreHardwareMonitor to open).</summary>
public class SensorHostTests
{
    [Fact]
    public void Before_opening_there_are_no_sensors_and_no_readings()
    {
        var host = new SensorHost();
        Assert.Equal(0, host.SensorCount);
        Assert.Empty(host.Keys);
        Assert.Empty(host.Ids);
        Assert.Empty(host.Schema);
        Assert.Empty(host.ReadAll());
        Assert.Null(host.Read(KeySensors.CpuTemp));
        Assert.Null(host.Read(KeySensors.GpuTemp));
        Assert.Null(host.ReadSensor("/amdcpu/0/temperature/2"));
        Assert.False(host.IsFresh(0));
        Assert.Empty(host.DriveTemperatures());
        Assert.Empty(host.DriveHealth);
        Assert.Equal(default, host.ReadKeys());
        host.Close();
    }

    [Fact]
    public void Watching_unknown_sensors_is_harmless()
    {
        var host = new SensorHost();
        host.Watch(["/nvidiagpu/0/temperature/0", "nope"]);
        host.Watch([]);
        host.Update(everything: false, 1000);
        Assert.Empty(host.ReadAll());
    }

    [Fact]
    public void Drives_are_read_every_5_minutes_with_the_app_closed_and_every_10_seconds_with_it_open()
    {
        var host = new SensorHost();
        bool Due(bool live, long ms)
        {
            host.Update(live, ms);
            return host.DrivesUpdated;
        }
        Assert.False(Due(false, 1_000));
        Assert.False(Due(false, 299_999));
        Assert.True(Due(false, 300_000));
        Assert.False(Due(false, 301_000));
        Assert.False(Due(true, 309_999));
        Assert.True(Due(true, 310_000));
        Assert.False(Due(true, 315_000));
        Assert.True(Due(true, 320_000));
        Assert.False(Due(false, 600_000));
        Assert.True(Due(false, 620_000));
    }

    [Fact]
    public void Drives_are_read_soon_after_the_agent_starts()
    {
        // The sampler's clock starts at 0: the first drive reading mustn't wait for a long overflowed interval.
        var host = new SensorHost();
        host.Update(everything: true, 10_000);
        Assert.True(host.DrivesUpdated);
    }
}

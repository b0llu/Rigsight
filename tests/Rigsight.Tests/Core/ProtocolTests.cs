using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

public sealed class ProtocolTests
{
    private static T RoundTrip<T>(T value) => ProtocolJson.Deserialize<T>(ProtocolJson.Serialize(value))!;

    private static AgentMessage Full() => new()
    {
        T = "hello",
        Version = "0.5.12",
        IsAdmin = true,
        StartupEnabled = false,
        Hardware = [new HardwareMeta { Name = "Ryzen", Type = "Cpu", Sensors = [new SensorMeta { Id = "/amdcpu/0/temperature/2", Name = "Core (Tctl/Tdie)", Kind = SensorKind.Temperature }] }],
        Keys = new() { [KeySensors.CpuTemp] = 0 },
        History = [new SeriesHistory { Key = KeySensors.CpuTemp, Times = [1, 2, 3], Values = [40.5f, null, float.NaN] }],
        Drives = [new DriveHealthInfo { Name = "SSD", Status = "Caution", ReallocatedSectors = 5, PendingSectors = 0, UncorrectableSectors = null }],
        Settings = new RigsightSettings { UseFahrenheit = true, Theme = "light" },
        Time = 1_790_000_000_000,
        Values = [1.5f, null, float.PositiveInfinity, float.NegativeInfinity, float.NaN, -0f, float.MaxValue, float.Epsilon],
        Activity = new ActivityInfo
        {
            Exe = "eldenring.exe", Name = "Elden Ring", Path = @"D:\Games\eldenring.exe", Category = AppCategory.Game,
            Present = true, Fullscreen = true, Paused = true, SessionStart = 1_790_000_000, SessionActiveSec = 3600.5, SessionCpuMax = 80, SessionGpuMax = null,
        },
        Today = new TodayInfo
        {
            OnSec = 1, ActiveSec = 2, IdleSec = 3, TopApp = "Chrome", TopAppSec = 4, CpuPeak = 88.5, CpuPeakApp = "Dota 2",
            CpuPeakCategory = AppCategory.Game, GpuPeak = 70, GpuPeakApp = "Chrome", GpuPeakCategory = AppCategory.Browser,
        },
        Extremes = new() { ["/cpu"] = [30, 90], ["/nan"] = [double.NaN, double.PositiveInfinity] },
        ExtremesDay = "2026-09-25",
        ExtremesFull = true,
        Procs = [new ProcInfo { Exe = "chrome.exe", Name = "Chrome", Path = @"C:\chrome.exe", Count = 30, Cpu = 12.5, MemMB = 2048, HasWindow = true,
            Processes = [new ProcDetail { Pid = 42, Label = "Tab", Cpu = 1, MemMB = 100 }] }],
        OverlayVisible = true,
        OverlayHotkeyTaken = false,
        RtssState = "running",
        Page = "apps",
        Arg = "chrome.exe",
        UpdateStatus = "started",
    };

    [Fact]
    public void An_agent_message_with_every_field_survives_the_pipe()
    {
        var sent = Full();
        var json = ProtocolJson.Serialize(sent);
        var got = ProtocolJson.Deserialize<AgentMessage>(json)!;
        Assert.Equal(json, ProtocolJson.Serialize(got));

        Assert.Equal("hello", got.T);
        Assert.Equal("0.5.12", got.Version);
        Assert.True(got.IsAdmin);
        Assert.False(got.StartupEnabled);
        Assert.Equal(SensorKind.Temperature, got.Hardware![0].Sensors[0].Kind);
        Assert.Equal(0, got.Keys![KeySensors.CpuTemp]);
        Assert.Equal(3, got.History![0].Values.Length);
        Assert.Null(got.History[0].Values[1]);
        Assert.True(float.IsNaN(got.History[0].Values[2]!.Value));
        Assert.Equal(5, got.Drives![0].ReallocatedSectors);
        Assert.Null(got.Drives[0].UncorrectableSectors);
        Assert.True(got.Settings!.UseFahrenheit);
        Assert.Equal(1_790_000_000_000, got.Time);
        Assert.Equal(AppCategory.Game, got.Activity!.Category);
        Assert.Equal(3600.5, got.Activity.SessionActiveSec);
        Assert.Null(got.Activity.SessionGpuMax);
        Assert.Equal("Dota 2", got.Today!.CpuPeakApp);
        Assert.Equal([30.0, 90.0], got.Extremes!["/cpu"]);
        Assert.True(double.IsNaN(got.Extremes["/nan"][0]));
        Assert.True(double.IsPositiveInfinity(got.Extremes["/nan"][1]));
        Assert.True(got.ExtremesFull);
        Assert.Equal(42, got.Procs![0].Processes![0].Pid);
        Assert.True(got.OverlayVisible);
        Assert.False(got.OverlayHotkeyTaken);
        Assert.Equal(("running", "apps", "chrome.exe", "started"), (got.RtssState, got.Page, got.Arg, got.UpdateStatus));
    }

    [Fact]
    public void Special_float_readings_survive_the_pipe()
    {
        var got = RoundTrip(Full()).Values!;
        Assert.Equal(1.5f, got[0]);
        Assert.Null(got[1]);
        Assert.True(float.IsPositiveInfinity(got[2]!.Value));
        Assert.True(float.IsNegativeInfinity(got[3]!.Value));
        Assert.True(float.IsNaN(got[4]!.Value));
        Assert.True(float.IsNegative(got[5]!.Value));
        Assert.Equal(float.MaxValue, got[6]);
        Assert.Equal(float.Epsilon, got[7]);
        var json = ProtocolJson.Serialize(Full());
        Assert.Contains("\"NaN\"", json);
        Assert.Contains("\"Infinity\"", json);
        Assert.Contains("\"-Infinity\"", json);
    }

    [Fact]
    public void Nulls_are_left_out_so_ticks_stay_small()
    {
        var json = ProtocolJson.Serialize(new AgentMessage { T = "tick", Time = 5, Values = [1f, null] });
        Assert.Equal("""{"T":"tick","IsAdmin":false,"Time":5,"Values":[1,null],"ExtremesFull":false}""", json);
    }

    [Fact]
    public void Computed_phrases_are_not_sent()
    {
        var json = ProtocolJson.Serialize(Full());
        Assert.DoesNotContain("PeakWhile", json);
    }

    [Fact]
    public void Enums_travel_as_names()
    {
        var json = ProtocolJson.Serialize(Full());
        Assert.Contains("\"Kind\":\"Temperature\"", json);
        Assert.Contains("\"Category\":\"Game\"", json);
        Assert.Contains("\"CpuPeakCategory\":\"Game\"", json);
        Assert.Contains("\"Corner\":\"TopLeft\"", json);
    }

    [Fact]
    public void Unknown_fields_from_a_newer_agent_are_ignored()
    {
        var msg = ProtocolJson.Deserialize<AgentMessage>("""
            {"T":"tick","Time":7,"Future":{"a":[1,2]},"Values":[1.5,"NaN",null],"Activity":{"Exe":"a.exe","Mood":"happy"}}
            """)!;
        Assert.Equal("tick", msg.T);
        Assert.Equal(7, msg.Time);
        Assert.Equal(3, msg.Values!.Length);
        Assert.True(float.IsNaN(msg.Values[1]!.Value));
        Assert.Equal("a.exe", msg.Activity!.Exe);
    }

    [Fact]
    public void An_empty_object_is_a_message_with_no_type()
    {
        var msg = ProtocolJson.Deserialize<AgentMessage>("{}")!;
        Assert.Equal("", msg.T);
        Assert.Null(msg.Values);
        Assert.Null(ProtocolJson.Deserialize<AgentMessage>("null"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"T\":5}")]
    [InlineData("{\"Values\":[\"lots\"]}")]
    public void Broken_lines_are_errors_the_pipe_catches(string line) =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ProtocolJson.Deserialize<AgentMessage>(line));

    [Fact]
    public void A_message_is_one_line()
    {
        var full = Full();
        full.Activity!.Name = "multi\nline\r\nname";
        var json = ProtocolJson.Serialize(full);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
        Assert.Equal("multi\nline\r\nname", ProtocolJson.Deserialize<AgentMessage>(json)!.Activity!.Name);
    }

    [Fact]
    public void A_ui_message_with_every_field_survives_the_pipe()
    {
        var sent = new UiMessage { T = "cmd", Cmd = "procs-detail", Arg = "a.exe|b.exe", Settings = new RigsightSettings { UseFahrenheit = true } };
        var json = ProtocolJson.Serialize(sent);
        var got = ProtocolJson.Deserialize<UiMessage>(json)!;
        Assert.Equal(json, ProtocolJson.Serialize(got));
        Assert.Equal(("cmd", "procs-detail", "a.exe|b.exe"), (got.T, got.Cmd, got.Arg));
        Assert.True(got.Settings!.UseFahrenheit);
        Assert.Equal("""{"T":"pause"}""", ProtocolJson.Serialize(new UiMessage { T = "pause" }));
    }

    [Fact]
    public void Settings_sent_over_the_pipe_come_out_of_the_store_identical()
    {
        // What the agent does with the app's settings: through the store, for the same repairs as a file.
        var app = SettingsStore.Deserialize("{}");
        app.UseFahrenheit = true;
        app.AppNames["EldenRing.exe"] = "Elden Ring";
        app.Overlay.Sensors.Add(new OverlaySensor { Id = "x", Label = "X" });
        var viaPipe = ProtocolJson.Deserialize<UiMessage>(ProtocolJson.Serialize(new UiMessage { T = "settings", Settings = app }))!.Settings!;
        var agent = SettingsStore.Deserialize(SettingsStore.Serialize(viaPipe));
        Assert.Equal(SettingsStore.Serialize(app), SettingsStore.Serialize(agent));
        Assert.Equal("Elden Ring", agent.AppNames["eldenring.EXE"]);
    }

    [Fact]
    public void Settings_from_the_pipe_keep_case_insensitive_app_lookups()
    {
        // The app keeps the agent's copy as it arrives: app names must still match whatever case an exe is in.
        var s = new RigsightSettings();
        s.AppNames["eldenring.exe"] = "Elden Ring";
        s.AppCategories["blender.exe"] = AppCategory.Media;
        var got = ProtocolJson.Deserialize<AgentMessage>(ProtocolJson.Serialize(new AgentMessage { T = "settings", Settings = s }))!.Settings!;
        Assert.Equal("Elden Ring", got.AppNames["EldenRing.exe"]);
        Assert.Equal(AppCategory.Media, got.AppCategories["BLENDER.EXE"]);
    }

    [Theory]
    [InlineData("Dota 2", AppCategory.Game, "while playing Dota 2")]
    [InlineData("Chrome", AppCategory.Browser, "while browsing in Chrome")]
    [InlineData("Spotify", AppCategory.Media, "while Spotify was playing")]
    [InlineData("Discord", AppCategory.Communication, "while on Discord")]
    [InlineData("VS Code", AppCategory.Development, "while working in VS Code")]
    [InlineData("Steam", AppCategory.Launcher, "while in Steam")]
    [InlineData("Unknown", AppCategory.Other, "while using Unknown")]
    public void Today_says_what_you_were_doing_at_the_peak(string app, AppCategory category, string expected)
    {
        var today = new TodayInfo { CpuPeakApp = app, CpuPeakCategory = category, GpuPeakApp = app, GpuPeakCategory = category };
        Assert.Equal(expected, today.CpuPeakWhile);
        Assert.Equal(expected, today.GpuPeakWhile);
    }

    [Fact]
    public void Before_the_first_reading_there_is_no_peak_phrase()
    {
        var today = new TodayInfo { CpuPeak = 50, CpuPeakCategory = AppCategory.Game };
        Assert.Null(today.CpuPeakWhile);
        Assert.Null(today.GpuPeakWhile);
    }

    // ---- The hello captured from a real agent ----

    private static AgentMessage CapturedHello() => ProtocolJson.Deserialize<AgentMessage>(Fixtures.Read("hello-ryzen-rtx.json"))!;

    [Fact]
    public void A_real_hello_reads_completely()
    {
        var hello = CapturedHello();
        Assert.Equal("hello", hello.T);
        Assert.False(string.IsNullOrEmpty(hello.Version));
        Assert.True(hello.IsAdmin);
        Assert.Equal(11, hello.Hardware!.Count);
        Assert.Equal(202, hello.Hardware.Sum(h => h.Sensors.Count));
        Assert.Equal(20, hello.Keys!.Count);
        Assert.Equal(12, hello.History!.Count);
        Assert.Equal(3, hello.Drives!.Count);
        Assert.All(hello.Hardware, h => Assert.False(string.IsNullOrEmpty(h.Type)));
        Assert.All(hello.Hardware.SelectMany(h => h.Sensors), x => Assert.False(string.IsNullOrEmpty(x.Id)));
        // Identifiers are nearly unique: this NVIDIA driver reports "GPU Bus" and "GPU Memory" under the same one.
        var shared = hello.Hardware.SelectMany(h => h.Sensors).GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Equal(["/gpu-nvidia/0/load/3"], shared);
        Assert.All(hello.History, h => Assert.Equal(h.Times.Length, h.Values.Length));
        Assert.All(hello.History, h => Assert.True(h.Times.SequenceEqual(h.Times.Order()), $"{h.Key} times out of order"));
        Assert.All(KeySensors.HistoryKeys, k => Assert.Contains(hello.History, h => h.Key == k));
    }

    [Fact]
    public void Every_key_of_a_real_hello_points_at_a_sensor_of_the_right_kind()
    {
        var hello = CapturedHello();
        var flat = hello.Hardware!.SelectMany(h => h.Sensors.Select(x => (Hw: h.Type, Sensor: x))).ToList();
        foreach (var (key, index) in hello.Keys!)
        {
            Assert.InRange(index, 0, flat.Count - 1);
            var (hw, sensor) = flat[index];
            if (key.StartsWith("cpu")) Assert.Equal("Cpu", hw);
            if (key.StartsWith("gpu")) Assert.StartsWith("Gpu", hw);
            if (key.StartsWith("ram")) Assert.Equal("Memory", hw);
            if (key.EndsWith("Temp") || key is KeySensors.GpuHotSpot or KeySensors.GpuMemJunction) Assert.Equal(SensorKind.Temperature, sensor.Kind);
            if (key.EndsWith("Load")) Assert.Equal(SensorKind.Load, sensor.Kind);
            if (key.EndsWith("Power")) Assert.Equal(SensorKind.Power, sensor.Kind);
            if (key.EndsWith("Clock")) Assert.Equal(SensorKind.Clock, sensor.Kind);
            if (key.EndsWith("Voltage")) Assert.Equal(SensorKind.Voltage, sensor.Kind);
        }
    }

    [Fact]
    public void A_real_tick_has_one_value_per_sensor_of_its_hello()
    {
        var tick = ProtocolJson.Deserialize<AgentMessage>(Fixtures.Read("tick-ryzen-rtx.json"))!;
        Assert.Equal("tick", tick.T);
        Assert.Equal(CapturedHello().Hardware!.Sum(h => h.Sensors.Count), tick.Values!.Length);
        Assert.NotEmpty(tick.Extremes!);
        Assert.All(tick.Extremes!.Values, e => Assert.Equal(2, e.Length));
    }

    [Fact]
    public void A_real_hello_round_trips_unchanged()
    {
        var hello = CapturedHello();
        var json = ProtocolJson.Serialize(hello);
        Assert.Equal(json, ProtocolJson.Serialize(ProtocolJson.Deserialize<AgentMessage>(json)));
    }
}

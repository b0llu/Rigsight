using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Settings' Sensors card, the problem report, and what the sidebar says when the agent doesn't start.</summary>
public class SensorStatusTests
{
    private static SensorStatus Status(params (string Part, string[] By)[] paused) =>
        new() { Paused = [.. paused.Select(p => new SkippedSensors { Part = p.Part, Because = [.. p.By] })] };

    [Fact]
    public void Nothing_left_out_says_nothing()
    {
        Assert.Null(SettingsViewModel.PausedText(null));
        Assert.Null(SettingsViewModel.PausedText(new SensorStatus()));
        Assert.Null(SettingsViewModel.SkippedText(null));
        Assert.Null(SettingsViewModel.SkippedText(new SensorStatus()));
    }

    [Fact]
    public void Paused_parts_are_listed_per_program()
    {
        Assert.Equal("Not read right now: motherboard (Gigabyte Control Center).",
            SettingsViewModel.PausedText(Status(("Motherboard", ["Gigabyte Control Center"]))));
        Assert.Equal("Not read right now: motherboard (Gigabyte Control Center), fan hubs and power supply (iCUE).",
            SettingsViewModel.PausedText(Status(("Motherboard", ["Gigabyte Control Center"]), ("Fan hubs", ["iCUE"]), ("Power supply", ["iCUE"]))));
        Assert.Equal("Not read right now: motherboard, fan hubs and power supply (SignalRGB), fan hubs (iCUE).",
            SettingsViewModel.PausedText(Status(("Motherboard", ["SignalRGB"]), ("Fan hubs", ["SignalRGB", "iCUE"]), ("Power supply", ["SignalRGB"]))));
    }

    [Fact]
    public void Skipped_parts_say_why()
    {
        Assert.Contains("didn't finish", SettingsViewModel.SkippedText(new SensorStatus { SafeMode = true }));
        Assert.Contains("memory kept growing", SettingsViewModel.SkippedText(new SensorStatus { StoppedForMemory = true }));
        // Both: the memory reason is the more useful one.
        Assert.Contains("memory kept growing", SettingsViewModel.SkippedText(new SensorStatus { SafeMode = true, StoppedForMemory = true }));
    }

    [Fact]
    public void Stepping_aside_is_off_unless_turned_on_and_is_saved()
    {
        Assert.False(new RigsightSettings().YieldToHardwareApps);
        Assert.False(SettingsStore.Deserialize("{}").YieldToHardwareApps);
        var s = new RigsightSettings { YieldToHardwareApps = true };
        Assert.True(SettingsStore.Deserialize(SettingsStore.Serialize(s)).YieldToHardwareApps);
    }

    [Fact]
    public void The_sensor_status_travels_in_the_hello()
    {
        var hello = new AgentMessage { T = "hello", SensorStatus = Status(("Fan hubs", ["iCUE"])) };
        hello.SensorStatus.SafeMode = true;
        var back = ProtocolJson.Deserialize<AgentMessage>(ProtocolJson.Serialize(hello))!;
        Assert.True(back.SensorStatus!.SafeMode);
        Assert.Equal("Fan hubs", back.SensorStatus.Paused.Single().Part);
        Assert.Equal(["iCUE"], back.SensorStatus.Paused.Single().Because);
        // Older agents send none.
        Assert.Null(ProtocolJson.Deserialize<AgentMessage>("""{"T":"hello"}""")!.SensorStatus);
    }

    [Fact]
    public void The_problem_report_has_what_helps_and_skips_the_rest()
    {
        var status = Status(("Motherboard", ["Gigabyte Control Center"]));
        status.SafeMode = true;
        string report = ProblemReport.Build(new ProblemReport.Facts("v0.6.0", true, true, "Windows 11 (X64)",
            [("Cpu", "AMD Ryzen 7 5700X3D"), ("GpuNvidia", "NVIDIA GeForce RTX 3080 Ti"), ("Storage", "Samsung SSD 980"), ("Cpu", "AMD Ryzen 7 5700X3D")],
            status, ["[2026-09-26 10:00:00] [agent] Starting (admin: True)", "[2026-09-26 10:00:06] [agent] Sensors ready"]));
        Assert.StartsWith("Rigsight v0.6.0 problem report", report);
        Assert.Contains("Windows: Windows 11 (X64)", report);
        Assert.Contains("Agent: running with admin rights", report);
        Assert.Contains("  Cpu: AMD Ryzen 7 5700X3D", report);
        Assert.Single(report.Split('\n'), l => l.Contains("5700X3D")); // once
        Assert.DoesNotContain("Samsung", report); // drives add nothing here
        Assert.Contains("Not read: Motherboard (left to Gigabyte Control Center)", report);
        Assert.Contains("Safe mode", report);
        Assert.Contains("Last 2 log lines:", report);
        Assert.EndsWith("Sensors ready" + Environment.NewLine, report);

        string down = ProblemReport.Build(new ProblemReport.Facts("v0.6.0", false, false, "W", [], null, []));
        Assert.Contains("Agent: not running", down);
        Assert.DoesNotContain("Hardware:", down);
    }

    [Fact]
    public void The_log_tail_is_the_last_lines_even_while_the_file_is_being_written()
    {
        string path = Path.Combine(Path.GetTempPath(), "rigsight-tests", $"log-{Guid.NewGuid():N}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)))
            {
                for (int i = 1; i <= 400; i++) writer.WriteLine($"line {i}");
                writer.Flush();
                var tail = ProblemReport.ReadLogTail(path, 150);
                Assert.Equal(150, tail.Count);
                Assert.Equal("line 251", tail[0]);
                Assert.Equal("line 400", tail[^1]);
            }
            Assert.Empty(ProblemReport.ReadLogTail(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Not_starting_says_whether_the_agent_is_there_at_all()
    {
        Assert.Contains("running but not answering", ShellViewModel.NotStartedText(true));
        Assert.Contains("Restart your PC", ShellViewModel.NotStartedText(true));
        Assert.Contains("didn't start", ShellViewModel.NotStartedText(false));
        Assert.Contains("admin prompt", ShellViewModel.NotStartedText(false));
    }
}

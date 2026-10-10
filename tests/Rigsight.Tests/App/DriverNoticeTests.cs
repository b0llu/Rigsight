using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The PawnIO driver, which CPU temperature and power come through: Settings says when it is missing or not running
/// and offers the way out, and the copied logs say enough to tell why a reading is missing.
/// </summary>
[Collection("UI")]
public class DriverNoticeTests
{
    private static SensorStatus Driver(string? driver, string? install = null) => new() { Driver = driver, DriverInstall = install };

    [Fact]
    public void A_fine_driver_says_nothing_and_a_missing_or_stopped_one_is_pointed_at()
    {
        Assert.Null(SettingsViewModel.DriverWords(null));
        Assert.Null(SettingsViewModel.DriverWords(new SensorStatus()));
        Assert.Empty(SettingsViewModel.NoticeParts(new SensorStatus()));
        foreach (string state in new[] { "missing", "stopped" })
        {
            Assert.Equal([SettingsViewModel.DriverPart], SettingsViewModel.NoticeParts(Driver(state)));
            Assert.True(SettingsViewModel.NeedsAttention(Driver(state), null));
            Assert.Contains("temperature and power", SettingsViewModel.DriverWords(Driver(state)));
        }
        // Dealt with by its own buttons: "Got it" for other programs and "Try again" for a scan say nothing about it.
        Assert.True(SettingsViewModel.NeedsAttention(Driver("missing"), SettingsViewModel.Acknowledge(null, Driver("missing"), problems: true)));
        Assert.True(SettingsViewModel.NeedsAttention(Driver("missing"), SettingsViewModel.Acknowledge(null, Driver("missing"), problems: false)));
        Assert.False(SettingsViewModel.NeedsAttention(Driver("missing"), SettingsViewModel.DriverPart));
    }

    [Fact]
    public void The_button_installs_until_that_has_failed_and_then_opens_the_drivers_site()
    {
        Assert.False(SettingsViewModel.DriverOpensSite(Driver("missing")));
        Assert.False(SettingsViewModel.DriverOpensSite(Driver("missing", "installing")));
        Assert.True(SettingsViewModel.DriverOpensSite(Driver("missing", "failed")));
        Assert.True(SettingsViewModel.DriverOpensSite(Driver("missing", "no-winget")));
        // Installed already: installing it from here would change nothing.
        Assert.True(SettingsViewModel.DriverOpensSite(Driver("stopped")));
        Assert.Contains("Installing", SettingsViewModel.DriverWords(Driver("missing", "installing")));
        Assert.Contains("couldn't be installed", SettingsViewModel.DriverWords(Driver("missing", "failed")));
        Assert.Contains("Restarting your PC", SettingsViewModel.DriverWords(Driver("stopped")));
    }

    [Fact]
    public void The_notice_in_Settings_goes_from_Install_to_installing_to_gone()
    {
        var settings = Kit.OfflineSettings();
        Ui.Run(() =>
        {
            var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings));
            string? opened = null;
            vm.OpenInBrowser = url => opened = url;
            Assert.Null(vm.DriverTitle);
            Assert.False(vm.SensorsNeedAttention);

            vm.SensorStatus = Driver("missing");
            Assert.Equal("The PawnIO driver isn't installed", vm.DriverTitle);
            Assert.Equal("Install", vm.DriverButtonText);
            Assert.True(vm.SensorsNeedAttention);
            Assert.True(vm.ShowDriverNotNow);

            // "Not now": the notice stays, Settings is no longer pointed at, and the button to say so goes.
            vm.DismissDriverCommand.Execute(null);
            Assert.False(vm.SensorsNeedAttention);
            Assert.False(vm.ShowDriverNotNow);
            Assert.NotNull(vm.DriverTitle);

            // The agent says it is fetching it: a loader in place of the button.
            vm.SensorStatus = Driver("missing", "installing");
            Assert.True(vm.IsInstallingDriver);
            // It failed: the button now opens the driver's own site, and nothing else.
            vm.SensorStatus = Driver("missing", "failed");
            Assert.False(vm.IsInstallingDriver);
            Assert.Equal("Download", vm.DriverButtonText);
            vm.InstallDriverCommand.Execute(null);
            Assert.Equal(PawnIoDriver.Site, opened);

            // Installed and running: no notice, and it is forgotten, so going missing again is pointed at again.
            vm.SensorStatus = new SensorStatus();
            Assert.Null(vm.DriverTitle);
            vm.SensorStatus = Driver("stopped");
            Assert.Equal("The PawnIO driver isn't running", vm.DriverTitle);
            Assert.True(vm.SensorsNeedAttention);
        });
    }

    [Fact]
    public void The_driver_travels_in_the_hello_and_in_a_status_message()
    {
        var back = ProtocolJson.Deserialize<AgentMessage>(ProtocolJson.Serialize(new AgentMessage { T = "status", SensorStatus = Driver("missing", "installing") }))!;
        Assert.Equal(("missing", "installing"), (back.SensorStatus!.Driver, back.SensorStatus.DriverInstall));
        // An agent from before this says nothing: no notice.
        var old = ProtocolJson.Deserialize<AgentMessage>("""{"T":"hello","SensorStatus":{"SafeMode":false}}""")!;
        Assert.Null(old.SensorStatus!.Driver);
    }

    [Theory]
    [InlineData(true, "2.2.0.0", PawnIoDriver.State.Running, "installed 2.2.0.0, running")]
    [InlineData(true, null, PawnIoDriver.State.Stopped, "installed, stopped")]
    [InlineData(false, null, PawnIoDriver.State.NoService, "not installed")]
    // Its uninstaller can leave the service behind: said, since it reads as installed to other tools.
    [InlineData(false, "2.2.0.0", PawnIoDriver.State.Stopped, "not installed (its service is left behind, stopped)")]
    public void The_drivers_state_is_said_in_plain_words(bool installed, string? version, PawnIoDriver.State state, string expected) =>
        Assert.Equal(expected, PawnIoDriver.Describe(installed, version, state));

    [Fact]
    public void Asking_Windows_about_the_driver_never_throws()
    {
        // Whatever this PC has: an answer, and the same one in the words the log and the copied logs use.
        var state = PawnIoDriver.Running();
        Assert.True(Enum.IsDefined(state));
        Assert.StartsWith(PawnIoDriver.Installed ? "installed" : "not installed", PawnIoDriver.Describe());
        _ = PawnIoDriver.MemoryIntegrity;
    }

    [Fact]
    public void The_copied_logs_say_what_a_missing_reading_is_traced_with()
    {
        string report = ProblemReport.Build(new ProblemReport.Facts("v0.19.3", true, true, "Windows 11 (X64)",
            [("Cpu", "AMD Ryzen 5 4600H (13 load)"), ("GpuNvidia", "NVIDIA GeForce GTX 1650 (3 temperature, 5 load)")],
            Driver("missing", "failed"), ["[2026-10-10 07:45:20] [agent] Sensors ready"])
        {
            AgentVersion = "0.19.2",
            Details = ["PawnIO driver: not installed", "Memory integrity: on"],
            Readings = ["CPU (AMD Ryzen 5 4600H): temperature no sensor, load 12.0 %, power no sensor, clock no sensor"],
        });
        string[] lines = report.Split(Environment.NewLine);
        // The agent's own version beside the window's: the two differ right after an update.
        Assert.Equal("Agent: running with admin rights, version 0.19.2", lines[2]);
        Assert.Equal("PawnIO driver: not installed", lines[3]);
        Assert.Contains("  Cpu: AMD Ryzen 5 4600H (13 load)", lines);
        Assert.Contains("Readings now:", lines);
        Assert.Contains("  CPU (AMD Ryzen 5 4600H): temperature no sensor, load 12.0 %, power no sensor, clock no sensor", lines);
        Assert.Contains("Driver warning shown: missing (install: failed)", lines);
        // In that order, and the log last.
        Assert.True(Array.IndexOf(lines, "Hardware:") < Array.IndexOf(lines, "Readings now:"));
        Assert.True(Array.IndexOf(lines, "Readings now:") < Array.IndexOf(lines, "Last 1 log lines:"));

        // No agent: no version for it and no readings.
        string down = ProblemReport.Build(new ProblemReport.Facts("v0.19.3", false, false, "W", [], null, []) { AgentVersion = "0.19.2" });
        Assert.Contains("Agent: not running" + Environment.NewLine, down);
        Assert.DoesNotContain("Readings now:", down);
    }

    [Fact]
    public void The_log_says_what_should_have_been_found_and_wasnt()
    {
        static HardwareMeta Hw(string type, string name, params SensorKind[] kinds) =>
            new() { Type = type, Name = name, Sensors = [.. kinds.Select(k => new SensorMeta { Id = "/x", Name = "x", Kind = k })] };

        // The laptop from the report: a CPU read without its driver gives loads and nothing else.
        var laptop = new List<HardwareMeta>
        {
            Hw("Cpu", "AMD Ryzen 5 4600H", SensorKind.Load),
            Hw("Memory", "Total Memory", SensorKind.Load, SensorKind.Data),
            Hw("GpuAmd", "AMD Radeon(TM) Graphics", SensorKind.Load),
            Hw("GpuNvidia", "NVIDIA GeForce GTX 1650", SensorKind.Temperature, SensorKind.Load, SensorKind.Power),
            Hw("Storage", "SSD", SensorKind.Temperature),
            Hw("Battery", "L19M4PC0", SensorKind.Voltage),
        };
        // A laptop's missing board chip is not news: only the CPU is.
        Assert.Equal(["Cpu AMD Ryzen 5 4600H: no temperature, power, clock"], Rigsight.Agent.Sensors.SensorHost.NotFound(laptop));

        // Everything there: nothing to say.
        var desktop = new List<HardwareMeta>
        {
            Hw("Cpu", "AMD Ryzen 7 5700X3D", SensorKind.Temperature, SensorKind.Load, SensorKind.Power, SensorKind.Clock),
            Hw("Memory", "Total Memory", SensorKind.Load),
            Hw("GpuNvidia", "RTX 3080 Ti", SensorKind.Temperature, SensorKind.Load, SensorKind.Power),
            Hw("Storage", "Samsung SSD 980", SensorKind.Temperature),
            Hw("SuperIO", "ITE IT8686E", SensorKind.Fan, SensorKind.Voltage),
        };
        Assert.Empty(Rigsight.Agent.Sensors.SensorHost.NotFound(desktop));

        // A desktop with no board chip read, a drive with no temperature, and no graphics card at all.
        desktop.RemoveAt(4);
        desktop.RemoveAt(2);
        desktop[2] = Hw("Storage", "USB Drive", SensorKind.Data);
        Assert.Equal(["Storage USB Drive: no temperature", "no graphics card found", "no motherboard sensors (fans, voltages, board temperatures)"],
            Rigsight.Agent.Sensors.SensorHost.NotFound(desktop));
    }

    [Fact]
    public void The_log_goes_along_generously_with_repeats_folded_and_the_latest_start_kept()
    {
        // A line said over and over is said once, with how often it came again; lines of a stack trace are left alone.
        var folded = ProblemReport.Fold(
        [
            "[2026-10-09 07.42.20] [update] Check failed: No such host is known.",
            "[2026-10-09 07.42.50] [update] Check failed: No such host is known.",
            "[2026-10-09 07.43.20] [update] Check failed: No such host is known.",
            "   at System.Net.Http.Foo()",
            "   at System.Net.Http.Foo()",
            "[2026-10-09 07.44.00] [agent] Saved before Windows shut down",
        ]);
        Assert.Equal(
        [
            "[2026-10-09 07.42.20] [update] Check failed: No such host is known.",
            "    (the same line 2 more times, the last at 2026-10-09 07.43.20)",
            "   at System.Net.Http.Foo()",
            "   at System.Net.Http.Foo()",
            "[2026-10-09 07.44.00] [agent] Saved before Windows shut down",
        ], folded);

        string dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "rigsight.log");
            // The file moved aside goes along too: a log that has just been started afresh isn't nearly empty.
            File.WriteAllLines(path + ".old", ["[2026-10-01 08.00.00] [agent] Starting 0.19.3 (admin: True)", "[2026-10-01 08.00.03] [sensors] Found: Cpu X (13 load)"]);
            File.WriteAllLines(path, Enumerable.Range(0, 5).Select(i => $"[2026-10-02 09.00.0{i}] [network] line {i}"));
            var all = ProblemReport.LogForReport(path);
            Assert.Equal(7, all.Count);
            Assert.StartsWith("[2026-10-01 08.00.00] [agent] Starting", all[0]);

            // Too long to send whole: the last lines, and before them the latest start's own (what was found, what wasn't).
            var cut = ProblemReport.LogForReport(path, lines: 3);
            Assert.Equal(
            [
                "[2026-10-01 08.00.00] [agent] Starting 0.19.3 (admin: True)", "[2026-10-01 08.00.03] [sensors] Found: Cpu X (13 load)", "[2026-10-02 09.00.00] [network] line 0",
                "[2026-10-02 09.00.01] [network] line 1", "(lines between left out)",
                "[2026-10-02 09.00.02] [network] line 2", "[2026-10-02 09.00.03] [network] line 3", "[2026-10-02 09.00.04] [network] line 4",
            ], cut);
            // The start within the last lines already: just those.
            File.AppendAllLines(path, ["[2026-10-03 08.00.00] [agent] Starting 0.19.3 (admin: True)", "[2026-10-03 08.00.03] [agent] Sensors ready"]);
            Assert.Equal(3, ProblemReport.LogForReport(path, lines: 3).Count);
            // No log at all: nothing, not an error.
            Assert.Empty(ProblemReport.LogForReport(Path.Combine(dir, "none.log")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void This_PCs_lines_and_the_settings_line_name_no_one()
    {
        var lines = ProblemReport.ThisPc();
        Assert.Contains(lines, l => l.StartsWith("Copied: "));
        Assert.Contains(lines, l => l.StartsWith("PawnIO driver: "));
        Assert.Contains(lines, l => l.StartsWith("Data: history "));

        // Names the person typed (apps, labels, pages) stay out: only switches and figures.
        var s = new RigsightSettings { YieldToHardwareApps = true, UseFahrenheit = true };
        s.AppNames["secret.exe"] = "My Secret App";
        s.SensorLabels["/cpu/0"] = "Bedroom PC";
        s.Tracking.ExcludedApps.Add("hidden.exe");
        string line = ProblemReport.SettingsLine(s);
        Assert.StartsWith("Settings: readings every 1 s open and 2 s closed", line);
        Assert.Contains("step aside for RGB and fan apps on", line);
        Assert.Contains("°F", line);
        Assert.DoesNotContain("Secret", line);
        Assert.DoesNotContain("Bedroom", line);
        Assert.DoesNotContain("hidden", line);

        Assert.Equal("no sensor", ShellViewModel.Reading(null));
    }
}

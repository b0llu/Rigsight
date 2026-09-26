using Rigsight.Agent.Sensors;

namespace Rigsight.Tests.Agent;

/// <summary>Which RGB and fan-control programs are found, and what they're left.</summary>
public class HardwareAppsTests
{
    private static List<string> Names(params (string, string?)[] processes) => [.. HardwareApps.Detect(processes).Select(a => a.Name)];

    [Fact]
    public void Programs_are_found_by_process_name_in_any_case()
    {
        Assert.Equal(["iCUE"], Names(("icue", null)));
        Assert.Equal(["iCUE", "Gigabyte Control Center"], Names(("RGBFusion", null), ("explorer", @"C:\Windows\explorer.exe"), ("iCUE", null)));
        Assert.Equal(["NZXT CAM"], Names(("NZXT CAM", null)));
    }

    [Fact]
    public void Programs_are_found_by_their_install_folder_whatever_the_process_is_called()
    {
        Assert.Equal(["Gigabyte Control Center"], Names(("GccHelper", @"C:\Program Files\GIGABYTE\Control Center\Lib\GccHelper.exe")));
        Assert.Equal(["Armoury Crate"], Names(("AcPowerNotification", @"C:\Program Files\ASUS\ARMOURY CRATE Lite Service\x.exe")));
        Assert.Equal(["iCUE"], Names(("iCUEDevicePluginHost", @"C:\Program Files\Corsair\CORSAIR iCUE 5 Software\iCUEDevicePluginHost.exe")));
    }

    [Fact]
    public void Other_programs_are_left_alone()
    {
        Assert.Empty(Names(("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"), ("HWiNFO64", @"C:\Program Files\HWiNFO64\HWiNFO64.exe"),
            ("CorsairHelper", @"C:\Tools\corsair.exe"), ("GIGABYTE", null)));
    }

    [Fact]
    public void Each_program_is_listed_once_in_a_fixed_order()
    {
        var found = HardwareApps.Detect([("iCUE", null), ("Corsair.Service", null), ("OpenRGB", null), ("GCC", null)]);
        Assert.Equal(["iCUE", "Gigabyte Control Center", "OpenRGB"], found.Select(a => a.Name));
    }

    [Fact]
    public void Each_program_is_left_the_hardware_it_drives()
    {
        SensorParts Parts(params string[] names) => HardwareApps.PartsFor(HardwareApps.All.Where(a => names.Contains(a.Name)));
        Assert.Equal(SensorParts.FanHubs | SensorParts.PowerSupply, Parts("iCUE"));
        Assert.Equal(SensorParts.Motherboard, Parts("Gigabyte Control Center"));
        Assert.Equal(SensorParts.Risky, Parts("iCUE", "Gigabyte Control Center"));
        Assert.Equal(SensorParts.None, Parts());
        // CPU, GPU, RAM and drives are never among them: tracking and the key readings go on.
        Assert.All(HardwareApps.All, a => Assert.Equal(a.Parts, a.Parts & SensorParts.Risky));
    }

    [Fact]
    public void Parts_have_plain_names() =>
        Assert.Equal(["Motherboard", "Fan hubs", "Power supply"],
            new[] { SensorParts.Motherboard, SensorParts.FanHubs, SensorParts.PowerSupply }.Select(HardwareApps.PartName));

    [Fact]
    public void Looking_at_the_running_processes_works()
    {
        // Whatever this PC runs: the call itself mustn't throw (processes exiting meanwhile, ones that can't be opened).
        var found = HardwareApps.Running();
        Assert.All(found, a => Assert.Contains(a, HardwareApps.All));
    }
}

/// <summary>The marker that remembers an unfinished hardware scan, and the kernel-memory check during one.</summary>
public class ScanGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "scanguard-" + Guid.NewGuid().ToString("N"));
    private string Marker => Path.Combine(_dir, "sensors-scan.pending");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_scan_that_finishes_leaves_no_marker()
    {
        Assert.False(ScanGuard.LastScanUnfinished(Marker));
        using (new ScanGuard(Marker, kernelBytes: () => 100))
            Assert.True(ScanGuard.LastScanUnfinished(Marker)); // while it runs
        Assert.False(ScanGuard.LastScanUnfinished(Marker));
    }

    [Fact]
    public void A_scan_that_never_finishes_is_known_next_time()
    {
        _ = new ScanGuard(Marker, kernelBytes: () => null); // never disposed: the process hung or the PC was restarted
        Assert.True(ScanGuard.LastScanUnfinished(Marker));
        ScanGuard.Forget(Marker);
        Assert.False(ScanGuard.LastScanUnfinished(Marker));
    }

    [Fact]
    public void A_safe_mode_scan_keeps_the_marker_until_the_user_asks_again()
    {
        using (new ScanGuard(Marker, keepMarker: true, kernelBytes: () => 100)) { }
        Assert.True(ScanGuard.LastScanUnfinished(Marker));
    }

    [Fact]
    public void Kernel_memory_running_away_during_a_scan_trips_the_guard_once_and_keeps_the_marker()
    {
        long kernel = 1_000_000_000;
        int raised = 0;
        using (var guard = new ScanGuard(Marker, kernelBytes: () => kernel, limitBytes: 500_000_000, everyMs: 60_000))
        {
            guard.Exceeded += _ => raised++;
            kernel += 400_000_000;
            guard.Check();
            Assert.False(guard.Tripped);
            kernel += 200_000_000;
            guard.Check();
            guard.Check();
            Assert.True(guard.Tripped);
        }
        Assert.Equal(1, raised);
        Assert.True(ScanGuard.LastScanUnfinished(Marker));
    }

    [Fact]
    public void Without_a_kernel_reading_the_guard_only_keeps_the_marker()
    {
        using (var guard = new ScanGuard(Marker, kernelBytes: () => null, limitBytes: 1))
        {
            guard.Check();
            Assert.False(guard.Tripped);
        }
        Assert.False(ScanGuard.LastScanUnfinished(Marker));
    }

    [Fact]
    public void Windows_reports_its_kernel_memory() =>
        Assert.InRange(ScanGuard.KernelBytes() ?? 0, 1L << 20, 1L << 40);
}

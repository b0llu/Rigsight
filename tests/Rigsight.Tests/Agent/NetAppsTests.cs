using Rigsight.Agent.Network;
using Rigsight.Agent.Tracking;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>Which app a process using the network is: Windows services by their own name, not "Service Host".</summary>
public class NetAppsTests
{
    private static readonly Func<string, bool> NoneExcluded = _ => false;

    [Fact]
    public void A_service_host_is_named_after_its_service()
    {
        using var rig = new TrackerRig();
        var net = new NetApps(rig.Apps) { CommandLine = pid => pid == 40_000_000 ? @"C:\Windows\system32\svchost.exe -k NetworkService -p -s DoSvc" : null };
        var app = net.Of(40_000_000, new Dictionary<int, string> { [40_000_000] = "svchost.exe" }, NoneExcluded)!;
        Assert.Equal("svchost.exe:DoSvc", app.Exe);
        Assert.Equal("Delivery Optimization", app.Name);
        Assert.Equal(AppCategory.System, app.AutoCategory);
    }

    [Fact]
    public void A_shared_service_host_stays_one_Windows_service()
    {
        using var rig = new TrackerRig();
        var net = new NetApps(rig.Apps) { CommandLine = _ => @"C:\Windows\system32\svchost.exe -k netsvcs" };
        var app = net.Of(40_000_004, new Dictionary<int, string> { [40_000_004] = "svchost.exe" }, NoneExcluded)!;
        Assert.Equal("svchost.exe", app.Exe);
        Assert.Equal("Windows service", app.Name);
    }

    [Fact]
    public void Windows_own_kernel_traffic_is_the_system()
    {
        using var rig = new TrackerRig();
        var app = new NetApps(rig.Apps).Of(4, new Dictionary<int, string>(), NoneExcluded)!;
        Assert.Equal("Windows system", app.Name);
    }

    [Fact]
    public void A_reused_process_ID_is_looked_at_afresh()
    {
        using var rig = new TrackerRig();
        var net = new NetApps(rig.Apps);
        var running = new Dictionary<int, string> { [40_000_008] = "chrome.exe" };
        Assert.Equal("chrome.exe", net.Of(40_000_008, running, NoneExcluded)!.Exe);
        running[40_000_008] = "steam.exe";
        net.Prune(running);
        Assert.Equal("steam.exe", net.Of(40_000_008, running, NoneExcluded)!.Exe);
    }

    [Theory]
    [InlineData(500.0 * 1024 * 1024, "500 MB")]
    [InlineData(3.4 * 1024 * 1024 * 1024, "3.4 GB")]
    [InlineData(118.0 * 1024 * 1024 * 1024, "118 GB")]
    public void Data_is_said_the_way_Windows_counts_it(double bytes, string text) => Assert.Equal(text, Units.Data(bytes));

    [Theory]
    [InlineData(12_373_196, "11.8 MB/s", "99 Mbps")]
    [InlineData(319_488, "312 KB/s", "2.6 Mbps")]
    [InlineData(150_000_000, "143.1 MB/s", "1.2 Gbps")]
    public void Speeds_are_said_both_ways(double bytesPerSec, string speed, string mbps)
    {
        Assert.Equal(speed, Units.Speed(bytesPerSec));
        Assert.Equal(mbps, Units.Mbps(bytesPerSec));
    }
}

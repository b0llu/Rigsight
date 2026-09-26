using System.Diagnostics;
using Rigsight.Agent.Native;

namespace Rigsight.Agent.Sensors;

/// <summary>Kinds of hardware the agent can leave alone.</summary>
[Flags]
internal enum SensorParts
{
    None = 0,
    /// <summary>The motherboard's sensor chip (temperatures, fans, voltages), and on Gigabyte boards the controller in front of the second chip.</summary>
    Motherboard = 1,
    /// <summary>USB fan hubs, pump and lighting controllers (Corsair, NZXT, Aqua Computer…).</summary>
    FanHubs = 2,
    /// <summary>Power supplies with a USB link (Corsair, NZXT…).</summary>
    PowerSupply = 4,
    /// <summary>What the safe mode leaves out: everything below Windows' own readings except the CPU, GPU, RAM and drives.</summary>
    Risky = Motherboard | FanHubs | PowerSupply,
}

/// <summary>
/// RGB and fan-control programs that drive the same chips Rigsight reads. Two programs talking to a sensor chip or a
/// USB hub at once can leave it (or its driver) in a bad state: a hung scan, wrong readings, or a driver that keeps
/// using memory until Windows restarts. While one of these runs, the parts it controls are left to it.
/// </summary>
internal static class HardwareApps
{
    internal sealed record Known(string Name, SensorParts Parts, string[] Processes, string[] Folders);

    /// <summary>Process names (without ".exe") and install-folder fragments, matched case-insensitively.</summary>
    internal static readonly Known[] All =
    [
        new("iCUE", SensorParts.FanHubs | SensorParts.PowerSupply,
            ["iCUE", "Corsair.Service", "CorsairDeviceControlService"], [@"\Corsair\CORSAIR iCUE"]),
        new("Gigabyte Control Center", SensorParts.Motherboard,
            ["GCC", "GigabyteControlCenter", "RGBFusion", "SIV"], [@"\GIGABYTE\Control Center", @"\GIGABYTE\RGBFusion", @"\GIGABYTE\SIV"]),
        new("Armoury Crate", SensorParts.Motherboard,
            ["ArmouryCrate", "ArmouryCrate.Service", "ArmouryCrate.UserSessionHelper", "LightingService"], [@"\ASUS\ARMOURY CRATE", @"\ASUS\AacSDK"]),
        new("MSI Center", SensorParts.Motherboard,
            ["MSI Center", "MSI.CentralServer", "LEDKeeper2"], [@"\MSI\MSI Center", @"\MSI\One Dragon Center", @"\MSI\Mystic Light"]),
        new("NZXT CAM", SensorParts.FanHubs | SensorParts.PowerSupply,
            ["NZXT CAM"], [@"\NZXT CAM"]),
        new("SignalRGB", SensorParts.Motherboard | SensorParts.FanHubs,
            ["SignalRgb", "SignalRgbLauncher"], [@"\WhirlwindFX\SignalRgb"]),
        new("OpenRGB", SensorParts.Motherboard | SensorParts.FanHubs,
            ["OpenRGB"], []),
    ];

    /// <summary>The known programs among these processes (each once, in the order of <see cref="All"/>).</summary>
    internal static List<Known> Detect(IEnumerable<(string Name, string? Path)> processes)
    {
        var found = new HashSet<Known>();
        foreach (var (name, path) in processes)
            foreach (var app in All)
            {
                if (found.Contains(app)) continue;
                if (app.Processes.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase))
                    || path is not null && app.Folders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    found.Add(app);
            }
        return [.. All.Where(found.Contains)];
    }

    /// <summary>The known programs running now (names and install folders of every process: a few milliseconds).</summary>
    public static List<Known> Running()
    {
        var list = new List<(string, string?)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                list.Add((p.ProcessName, Win32.ProcessPath(p.Id)));
            }
            catch
            {
                // Exited meanwhile.
            }
            finally
            {
                p.Dispose();
            }
        }
        return Detect(list);
    }

    /// <summary>The parts to leave alone for these programs.</summary>
    public static SensorParts PartsFor(IEnumerable<Known> apps) =>
        apps.Aggregate(SensorParts.None, (all, a) => all | a.Parts);

    /// <summary>How the parts are named in Settings.</summary>
    public static string PartName(SensorParts part) => part switch
    {
        SensorParts.Motherboard => "Motherboard",
        SensorParts.FanHubs => "Fan hubs",
        SensorParts.PowerSupply => "Power supply",
        _ => part.ToString(),
    };
}

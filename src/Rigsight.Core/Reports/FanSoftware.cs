namespace Rigsight.Core.Reports;

/// <summary>
/// Programs that set fan speeds without a restart: a graphics card's (its maker's tool, MSI Afterburner) or the
/// motherboard's and fan hubs' (the board maker's centre, iCUE, Fan Control). A fan turning slower while one was
/// running may simply have been set so; the warning names it. (The BIOS needs a restart, see the fan rules.)
/// </summary>
public static class FanSoftware
{
    private static readonly (string Name, bool Gpu, bool Board, string[] Processes)[] Known =
    [
        ("MSI Afterburner", true, false, ["MSIAfterburner"]),
        ("EVGA Precision X1", true, false, ["PrecisionX_x64", "PrecisionX1"]),
        ("ASUS GPU Tweak", true, false, ["GPUTweakIII", "GPUTweakII"]),
        ("AMD Software", true, false, ["RadeonSoftware"]),
        ("Fan Control", true, true, ["FanControl"]),
        ("iCUE", false, true, ["iCUE"]),
        ("Gigabyte Control Center", false, true, ["GCC", "GigabyteControlCenter"]),
        ("Armoury Crate", false, true, ["ArmouryCrate"]),
        ("MSI Center", false, true, ["MSI Center", "MSI.CentralServer"]),
        ("NZXT CAM", false, true, ["NZXT CAM"]),
    ];

    /// <summary>The first known program among <paramref name="exes"/> that sets a graphics card's (or the board's) fans; null if none.</summary>
    public static string? Among(IEnumerable<string> exes, bool gpu)
    {
        var running = exes.Select(e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? e[..^4] : e).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Known.FirstOrDefault(k => (gpu ? k.Gpu : k.Board) && k.Processes.Any(running.Contains)).Name;
    }
}

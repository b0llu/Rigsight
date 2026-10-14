using System.Runtime.InteropServices;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// Two things asked of Windows itself with each reading of the sensors, for what an opened crash shows of its minutes:
/// how much memory Windows has promised to apps, and the battery. Neither needs admin rights or a driver.
/// </summary>
internal static partial class SystemState
{
    /// <summary>
    /// The memory promised to apps as a share of all Windows can promise (RAM and page file together), 0 to 100; null if
    /// Windows won't say. This is what runs out when apps fail with "out of memory": memory "in use" can sit at 70%
    /// while this is at 99.
    /// </summary>
    public static double? CommitPct()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? CommitPct(status.TotalPageFile, status.AvailPageFile) : null;
    }

    /// <summary>The sensors' readings with these two beside them, as the tracker keeps them for the minute.</summary>
    public static KeyValues With(KeyValues readings)
    {
        var (charge, onBattery) = Battery();
        return readings with { CommitPct = CommitPct(), Battery = charge, OnBattery = onBattery };
    }

    internal static double? CommitPct(ulong limit, ulong free) =>
        limit == 0 || free > limit ? null : 100.0 * (limit - free) / limit;

    /// <summary>The battery's charge (0 to 100) and whether the PC is running on it; both null on a PC without a battery.</summary>
    public static (int? Charge, bool? OnBattery) Battery() =>
        GetSystemPowerStatus(out var status) ? Battery(status.AcLine, status.BatteryFlag, status.BatteryPercent) : (null, null);

    /// <param name="acLine">0 on the battery, 1 plugged in, 255 not known.</param>
    /// <param name="flag">128 = no battery, 255 = not known.</param>
    /// <param name="percent">0 to 100, or 255 for not known.</param>
    internal static (int? Charge, bool? OnBattery) Battery(byte acLine, byte flag, byte percent)
    {
        if (flag == 255 || (flag & 128) != 0) return (null, null);
        return (percent <= 100 ? percent : null, acLine == 0 ? true : acLine == 1 ? false : null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLine, BatteryFlag, BatteryPercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);
}

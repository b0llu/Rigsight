using System.Runtime.InteropServices;

namespace Rigsight.Core.Ai;

/// <summary>
/// Whether this PC is a laptop or a desktop, for get_pc_info, from what the PC itself says: the chassis type its firmware
/// gives (SMBIOS, the same field as WMI's Win32_SystemEnclosure.ChassisTypes), then a battery and Windows' own power role
/// when the firmware doesn't say. Never from the CPU's name. Answers judged a laptop's heat at low load as a desktop's,
/// with no look-up that said which it was (audit Q029, Q110, Q129, 2026-11-17).
/// </summary>
public static partial class DeviceType
{
    /// <summary>The get_pc_info line: "Device type: Laptop (…)", "Device type: Desktop (…)" or "Device type: unknown (…)".</summary>
    public static string Line()
    {
        IReadOnlyList<int> chassis = [];
        bool? battery = null;
        int? role = null;
        try { chassis = Chassis(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("ai", $"device type: the firmware's chassis couldn't be read ({ex.Message})"); }
        try { battery = Battery(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("ai", $"device type: the battery couldn't be read ({ex.Message})"); }
        try { role = (int)PowerDeterminePlatformRoleEx(2); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("ai", $"device type: the power role couldn't be read ({ex.Message})"); }
        return "Device type: " + Decide(chassis, battery, role);
    }

    // SMBIOS chassis types (DMTF DSP0134, 7.4.1).
    private static readonly Dictionary<int, string> LaptopKinds = new()
    {
        [8] = "Portable", [9] = "Laptop", [10] = "Notebook", [11] = "Hand Held", [14] = "Sub Notebook",
        [30] = "Tablet", [31] = "Convertible", [32] = "Detachable",
    };

    private static readonly Dictionary<int, string> DesktopKinds = new()
    {
        [3] = "Desktop", [4] = "Low Profile Desktop", [5] = "Pizza Box", [6] = "Mini Tower", [7] = "Tower", [13] = "All in One",
        [15] = "Space-saving", [16] = "Lunch Box", [24] = "Sealed-case PC", [34] = "Embedded PC", [35] = "Mini PC", [36] = "Stick PC",
    };

    // Windows' power platform role (POWER_PLATFORM_ROLE), from the firmware's preferred power profile.
    private const int RoleDesktop = 1, RoleMobile = 2, RoleWorkstation = 3, RolePerformanceServer = 7, RoleSlate = 8;

    /// <summary>
    /// Laptop, Desktop or unknown, with what it rests on. The firmware's chassis type decides, unless a battery and Windows'
    /// power role both say the other (a board whose chassis field was left at "Desktop" in a laptop): then unknown. Without
    /// a chassis type, a battery and a mobile role together say laptop, neither says desktop; anything else is unknown.
    /// </summary>
    /// <param name="battery">Whether Windows sees a system battery (a UPS on USB can show as one); null: not known.</param>
    internal static string Decide(IReadOnlyList<int> chassis, bool? battery, int? role)
    {
        bool mobileRole = role is RoleMobile or RoleSlate, desktopRole = role is RoleDesktop or RoleWorkstation or RolePerformanceServer;
        string? laptop = chassis.Where(LaptopKinds.ContainsKey).Select(c => LaptopKinds[c]).FirstOrDefault();
        string? desktop = chassis.Where(DesktopKinds.ContainsKey).Select(c => DesktopKinds[c]).FirstOrDefault();
        string Battery() => battery switch { true => "; it has a battery", false => "; it has no battery", _ => "" };
        const string Guess = " Don't guess it from the CPU's name.";
        if (laptop is not null && desktop is null)
            return battery == false && desktopRole
                ? $"unknown (the firmware calls the case \"{laptop}\", but there's no battery and Windows treats it as a desktop).{Guess}"
                : $"Laptop (the PC's firmware calls its case \"{laptop}\"{Battery()}).";
        if (desktop is not null && laptop is null)
            return battery == true && mobileRole
                ? $"unknown (the firmware calls the case \"{desktop}\", but it has a battery and Windows treats it as a laptop).{Guess}"
                : $"Desktop (the PC's firmware calls its case \"{desktop}\"{Battery()}).";
        if (battery == true && mobileRole) return "Laptop (it has a battery and Windows treats it as a laptop; the firmware doesn't name the case).";
        if (battery == false && desktopRole) return "Desktop (it has no battery and Windows treats it as a desktop; the firmware doesn't name the case).";
        return $"unknown (the PC doesn't say clearly).{Guess}";
    }

    /// <summary>The chassis types in the firmware's SMBIOS tables (type 3, System Enclosure), from Windows' copy of them.</summary>
    internal static List<int> Chassis()
    {
        const uint Rsmb = 0x52534D42; // 'RSMB'
        uint size = GetSystemFirmwareTable(Rsmb, 0, IntPtr.Zero, 0);
        if (size == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetSystemFirmwareTable(Rsmb, 0, buffer, size) != size) return [];
            var raw = new byte[size];
            Marshal.Copy(buffer, raw, 0, (int)size);
            return ChassisIn(raw);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// The chassis types in Windows' RawSMBIOSData (an 8-byte header, then the structures): each structure's type and
    /// length, its formatted part, then its strings up to a double zero.
    /// </summary>
    internal static List<int> ChassisIn(byte[] raw)
    {
        var found = new List<int>();
        if (raw.Length < 8) return found;
        int end = Math.Min(raw.Length, 8 + BitConverter.ToInt32(raw, 4));
        int i = 8;
        while (i + 4 <= end)
        {
            int type = raw[i], length = raw[i + 1];
            if (length < 4 || type == 127) break;
            if (type == 3 && length > 5 && i + 5 < end) found.Add(raw[i + 5] & 0x7F);
            int j = i + length;
            while (j + 1 < end && (raw[j] != 0 || raw[j + 1] != 0)) j++;
            i = j + 2;
        }
        return found;
    }

    private static bool? Battery()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryFlag == 255) return null;
        return (status.BatteryFlag & 128) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLine, BatteryFlag, BatteryPercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetSystemFirmwareTable(uint provider, uint id, IntPtr buffer, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerDeterminePlatformRoleEx(uint version);
}

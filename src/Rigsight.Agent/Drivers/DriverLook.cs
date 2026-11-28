using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rigsight.Core;
using Rigsight.Core.Stability;

namespace Rigsight.Agent.Drivers;

/// <summary>
/// Every present device's driver as Windows' device list has it (1.22.0, "Drivers inside Timeline"): version, date,
/// maker, whether it comes with Windows, kind and name. Read through SetupAPI's device properties, which need no admin
/// rights and take milliseconds (Win32_PnPSignedDriver takes about 1.7 s). And Windows' own log of driver installs
/// (Kernel-PnP event 400), readable by any account, for the weeks before RigSight's first look and the moment of a change.
/// </summary>
internal static class DriverLook
{
    /// <summary>Every present device that has a driver; empty (and said in the log) when Windows wouldn't give the list.</summary>
    public static List<DriverSeen> Read()
    {
        var list = new List<DriverSeen>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (set == new IntPtr(-1))
        {
            Log.Write("driver history", $"Expected the list of devices from Windows, found error {Marshal.GetLastPInvokeError()}");
            return list;
        }
        int noVersion = 0;
        try
        {
            using var classes = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
            var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                string? version = Prop(set, ref info, DriverVersion);
                if (version is null)
                {
                    noVersion++;
                    continue;
                }
                string instance = InstanceId(set, ref info) ?? "";
                if (instance.Length == 0) continue;
                string classGuid = info.ClassGuid.ToString("B");
                string maker = DriverRecord.Maker(Prop(set, ref info, DriverProvider) ?? "");
                string inf = Prop(set, ref info, DriverInfPath) ?? "";
                bool inbox = inf.Length > 0 && !inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase);
                string name = Log.Printable(Text(set, ref info, SPDRP_FRIENDLYNAME) ?? Text(set, ref info, SPDRP_DEVICEDESC) ?? DriverRecord.KeyOf(instance));
                // AMD writes its own version beside Windows' one: the one people know ("25.11.1").
                string? radeon = null;
                if (DriverHistory.KindOf(classGuid) == DriverKind.Graphics && Text(set, ref info, SPDRP_DRIVER) is { } driverKey)
                {
                    try
                    {
                        using var key = classes?.OpenSubKey(driverKey);
                        radeon = key?.GetValue("RadeonSoftwareVersion") as string;
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
                list.Add(new DriverSeen(instance, name, classGuid, maker, version.Trim(), DriverRecord.Shown(maker, classGuid, version, radeon), Date(set, ref info), inbox));
            }
        }
        catch (Exception ex)
        {
            Log.Error("driver history", ex);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        if (list.Count == 0) Log.Write("driver history", $"Expected devices with drivers, found none ({noVersion} devices without a driver version)");
        return list;
    }

    /// <summary>The name Windows keeps for a device that isn't present now (its FriendlyName or DeviceDesc under Enum), or null.</summary>
    public static string? NameOf(string instance)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instance}");
            return DriverRecord.NameFromDesc(key?.GetValue("FriendlyName") as string) ?? DriverRecord.NameFromDesc(key?.GetValue("DeviceDesc") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    // ── Windows' log of driver installs ───────────────────────────────────

    private const string PnpLog = "Microsoft-Windows-Kernel-PnP/Configuration";

    /// <summary>
    /// Every "device configured" entry (event 400) from <paramref name="from"/> on, oldest first. Its properties, in order:
    /// [0] DeviceInstanceId, [2] ClassGuid, [3] DriverDate, [4] DriverVersion, [5] DriverProvider, [6] DriverInbox.
    /// </summary>
    public static List<PnpConfigured> ReadLog(DateTime from, out string said)
    {
        var list = new List<PnpConfigured>();
        int read = 0;
        string query = $"*[System[EventID=400 and TimeCreated[@SystemTime>='{from.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(PnpLog, PathType.LogName, query));
            while (true)
            {
                EventRecord? r;
                try { r = reader.ReadEvent(); }
                catch (Exception ex)
                {
                    said = $"stopped reading Windows' log of driver installs after {read} entries, at a read error: {ex.Message}";
                    return list;
                }
                if (r is null) break;
                using (r)
                {
                    read++;
                    string instance = Prop(r, 0) ?? "", version = Prop(r, 4) ?? "";
                    if (instance.Length == 0 || version.Length == 0 || r.TimeCreated is not { } at) continue;
                    var date = r.Properties.Count > 3 && r.Properties[3].Value is DateTime d ? d.Date : DriverRecord.ParseLoggedDate(Prop(r, 3));
                    bool inbox = Prop(r, 6) is { } box && box.Equals("True", StringComparison.OrdinalIgnoreCase);
                    string cls = Prop(r, 2) ?? "";
                    list.Add(new PnpConfigured(at, instance, version.Trim(), Prop(r, 5) ?? "", date, inbox, cls.StartsWith('{') ? cls.ToLowerInvariant() : "{" + cls.ToLowerInvariant() + "}"));
                }
            }
        }
        catch (Exception ex)
        {
            // An empty or missing log (turned off, or cleared) is not an error worth more than a line.
            said = $"couldn't read Windows' log of driver installs: {ex.Message}";
            return list;
        }
        said = $"{read} entries in Windows' log of driver installs{(list.Count > 0 ? $", the first on {list.Min(e => e.Time):d MMM yyyy}" : "")}";
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    private static string? Prop(EventRecord r, int index) =>
        index < r.Properties.Count ? r.Properties[index].Value?.ToString() : null;

    // ── Windows ───────────────────────────────────────────────────────────

    private static string? Text(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, null, 0, out int size);
        if (size <= 0 || size > 4096) return null;
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, size, out _)) return null;
        string text = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return text.Length > 0 ? text : null;
    }

    private static string? InstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceIdW(set, ref info, buffer, buffer.Length, out int needed) && needed > 1 ? new string(buffer, 0, needed - 1) : null;
    }

    /// <summary>A string device property (DEVPROP_TYPE_STRING), or null when the device has none.</summary>
    private static string? Prop(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, null, 0, out int size, 0);
        if (size <= 2 || size > 4096 || type != DEVPROP_TYPE_STRING) return null;
        var buffer = new byte[size];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out _, buffer, size, out _, 0)) return null;
        string text = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>The driver's date as its maker set it (DEVPROP_TYPE_FILETIME), or null.</summary>
    private static DateTime? Date(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var key = DriverDate;
        var buffer = new byte[8];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, buffer, 8, out _, 0) || type != DEVPROP_TYPE_FILETIME) return null;
        long ft = BitConverter.ToInt64(buffer, 0);
        try { return ft > 0 ? DateTime.FromFileTimeUtc(ft).Date : null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private const uint DIGCF_PRESENT = 0x02, DIGCF_ALLCLASSES = 0x04;
    private const uint SPDRP_DEVICEDESC = 0x00, SPDRP_DRIVER = 0x09, SPDRP_FRIENDLYNAME = 0x0C;
    private const uint DEVPROP_TYPE_STRING = 0x12, DEVPROP_TYPE_FILETIME = 0x10;

    // DEVPKEY_Device_DriverDate (2), DriverVersion (3), DriverInfPath (5), DriverProvider (9).
    private static readonly Guid DriverProps = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly DEVPROPKEY DriverDate = new(DriverProps, 2), DriverVersion = new(DriverProps, 3), DriverInfPath = new(DriverProps, 5), DriverProvider = new(DriverProps, 9);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY(Guid fmtid, uint pid) { public Guid Fmtid = fmtid; public uint Pid = pid; }

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint type, byte[]? buffer, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA info, char[] buffer, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out uint type, byte[]? buffer, int size, out int required, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}

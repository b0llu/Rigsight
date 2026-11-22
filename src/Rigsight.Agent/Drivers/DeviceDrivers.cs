using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rigsight.Core;

namespace Rigsight.Agent.Drivers;

/// <summary>
/// Which devices use each driver file, as Device Manager knows them: every present device's service (its driver), class
/// ("Net", "Display", "MEDIA"…) and name. Core's DriverParts turns that into the part people know. No admin rights
/// needed. Devices change rarely (something plugged in or removed), so this is read at the start and when asked.
/// </summary>
internal static class DeviceDrivers
{
    /// <summary>Each driver file, lower case ("e1r68x64.sys"), with the present devices that use it: (class, name).</summary>
    public static Dictionary<string, List<(string Class, string Name)>> Read()
    {
        var byFile = new Dictionary<string, List<(string Class, string Name)>>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); // service → driver file
        var set = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (set == new IntPtr(-1))
        {
            Log.Write("drivers", $"Expected the list of devices from Windows, found error {Marshal.GetLastPInvokeError()}");
            return byFile;
        }
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (Text(set, ref info, SPDRP_SERVICE) is not { } service) continue;
                if (!files.TryGetValue(service, out string? file)) files[service] = file = FileOf(services, service);
                if (file is null) continue;
                string cls = Text(set, ref info, SPDRP_CLASS) ?? "";
                string name = Text(set, ref info, SPDRP_FRIENDLYNAME) ?? Text(set, ref info, SPDRP_DEVICEDESC) ?? "";
                if (!byFile.TryGetValue(file, out var list)) byFile[file] = list = [];
                list.Add((cls, Log.Printable(name)));
            }
        }
        catch (Exception ex)
        {
            Log.Error("drivers", ex);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return byFile;
    }

    /// <summary>
    /// A service's driver file, lower case, from its ImagePath ("\SystemRoot\System32\drivers\e1r68x64.sys", or a driver
    /// store path); a kernel driver with no ImagePath runs from System32\drivers\&lt;service&gt;.sys, as Windows does.
    /// </summary>
    internal static string? FileOf(RegistryKey? services, string service)
    {
        using var key = services?.OpenSubKey(service);
        if (key is null) return null;
        return FileOfImage(key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, service);
    }

    /// <summary>The file name out of an ImagePath (null: none written, the default <paramref name="service"/>.sys).</summary>
    internal static string FileOfImage(string? image, string service)
    {
        if (string.IsNullOrWhiteSpace(image)) return (service + ".sys").ToLowerInvariant();
        string path = image.Trim().Trim('"');
        int cut = path.LastIndexOfAny(['\\', '/']);
        return (cut >= 0 ? path[(cut + 1)..] : path).ToLowerInvariant();
    }

    private static string? Text(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, null, 0, out int size);
        if (size <= 0 || size > 4096) return null;
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, size, out _)) return null;
        string text = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return text.Length > 0 ? text : null;
    }

    // ── Windows ───────────────────────────────────────────────────────────
    private const uint DIGCF_PRESENT = 0x02, DIGCF_ALLCLASSES = 0x04;
    private const uint SPDRP_DEVICEDESC = 0x00, SPDRP_SERVICE = 0x04, SPDRP_CLASS = 0x07, SPDRP_FRIENDLYNAME = 0x0C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint type, byte[]? buffer, int size, out int required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}

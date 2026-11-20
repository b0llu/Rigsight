using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors.Devices;

/// <summary>
/// The batteries Windows itself knows: a Bluetooth device's level (the one Settings shows under Bluetooth &amp;
/// devices), and an Xbox controller's level from XInput (Full, Medium, Low or Empty, no percent). One thread only.
/// </summary>
internal static class WindowsDevices
{
    // ── Bluetooth ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every present device with a Bluetooth battery level, one per device (the level sits on several of a device's
    /// nodes; they share a container). Windows doesn't say here whether the device is connected, nor whether it's
    /// charging: the level is its last known one.
    /// </summary>
    public static List<DeviceBattery> Bluetooth()
    {
        var list = new List<DeviceBattery>();
        var seen = new HashSet<Guid>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (set == new IntPtr(-1)) return list;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (Property(set, ref info, BatteryLevel) is not { Length: >= 1 } level || level[0] > 100) continue;
                var container = Property(set, ref info, ContainerId) is { Length: 16 } c ? new Guid(c) : Guid.Empty;
                if (container != Guid.Empty && !seen.Add(container)) continue;
                string name = Text(Property(set, ref info, FriendlyName)) ?? Text(Property(set, ref info, DeviceDesc)) ?? "Bluetooth device";
                uint cod = Property(set, ref info, ClassOfDevice) is { Length: >= 4 } k ? BitConverter.ToUInt32(k) : 0;
                string key = container != Guid.Empty ? $"bluetooth/{container:N}" : $"bluetooth/{name}";
                list.Add(new DeviceBattery(key, Log.Printable(name), KindOf(cod, name), level[0], null, null, true, "Bluetooth"));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    /// <summary>
    /// What a Bluetooth device is, from its class (classic Bluetooth: the major class, and for a peripheral whether it's
    /// a keyboard or a pointer), else from words in its name (Bluetooth LE devices carry no class).
    /// </summary>
    internal static DeviceKind KindOf(uint classOfDevice, string name)
    {
        uint major = (classOfDevice >> 8) & 0x1F, minor = (classOfDevice >> 2) & 0x3F;
        if (major == 0x05)
        {
            // The minor class's top two bits: 01 a keyboard, 10 a pointing device (11 both, a combo: said by its name below).
            if ((minor & 0x30) == 0x10) return DeviceKind.Keyboard;
            if ((minor & 0x30) == 0x20) return DeviceKind.Mouse;
            if ((minor & 0x0F) is 1 or 2) return DeviceKind.Controller;
        }
        if (major == 0x04) return DeviceKind.Headset;
        string n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains(" mx ") || n.StartsWith("mx ")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("keys")) return DeviceKind.Keyboard;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("joy-con")) return DeviceKind.Controller;
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("buds") || n.Contains("airpods") || n.Contains("earbuds") || n.StartsWith("wh-") || n.StartsWith("wf-"))
            return DeviceKind.Headset;
        return DeviceKind.Other;
    }

    private static string? Text(byte[]? value) =>
        value is { Length: >= 2 } ? System.Text.Encoding.Unicode.GetString(value).TrimEnd('\0').Trim() is { Length: > 0 } t ? t : null : null;

    private static byte[]? Property(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(set, ref info, ref key, out _, null, 0, out int size, 0);
        if (size <= 0 || size > 4096) return null;
        var buffer = new byte[size];
        return SetupDiGetDevicePropertyW(set, ref info, ref key, out _, buffer, size, out _, 0) ? buffer : null;
    }

    // ── Xbox controllers ─────────────────────────────────────────────────

    /// <summary>
    /// Each Xbox controller on a battery (a wired one has none to show), by its XInput place. XInput gives a level,
    /// not a percent, and nothing about charging.
    /// </summary>
    public static List<DeviceBattery> Xbox()
    {
        var list = new List<DeviceBattery>();
        var found = new List<(uint User, BatteryLevel Level)>();
        try
        {
            for (uint user = 0; user < 4; user++)
            {
                if (XInputGetBatteryInformation(user, BATTERY_DEVTYPE_GAMEPAD, out var b) != 0) continue;
                // 0 disconnected, 1 wired (no battery), 0xFF a battery of unknown kind (a controller that isn't an Xbox one, through a mapper).
                if (b.BatteryType is not (2 or 3)) continue;
                found.Add((user, (BatteryLevel)Math.Min((int)b.BatteryLevel, 3)));
            }
        }
        catch (DllNotFoundException) { return list; }
        catch (EntryPointNotFoundException) { return list; }
        foreach (var (user, level) in found)
            list.Add(new DeviceBattery($"xinput/{user}", found.Count > 1 ? $"Xbox controller {user + 1}" : "Xbox controller",
                DeviceKind.Controller, null, level, null, true, "Xbox Wireless"));
        return list;
    }

    // ── Windows ───────────────────────────────────────────────────────────
    private const uint DIGCF_PRESENT = 0x02, DIGCF_ALLCLASSES = 0x04;
    private const byte BATTERY_DEVTYPE_GAMEPAD = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY(Guid fmtid, uint pid) { public Guid fmtid = fmtid; public uint pid = pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION { public byte BatteryType; public byte BatteryLevel; }

    // The Bluetooth battery level Settings shows (a byte, 0 to 100).
    private static readonly DEVPROPKEY BatteryLevel = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DEVPROPKEY ContainerId = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly DEVPROPKEY FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly DEVPROPKEY DeviceDesc = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
    private static readonly DEVPROPKEY ClassOfDevice = new(new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 10);

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out uint type, byte[]? buffer, int size, out int required, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("xinput1_4.dll")] private static extern int XInputGetBatteryInformation(uint user, byte devType, out XINPUT_BATTERY_INFORMATION info);
}

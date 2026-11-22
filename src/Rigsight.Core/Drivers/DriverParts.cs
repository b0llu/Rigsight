using System.Text.RegularExpressions;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Drivers;

/// <summary>
/// Which part of the PC a driver file is, from the devices Windows says use it (approved design, issue #15). A part is
/// named only when the answer is plain: one kind of network adapter, the graphics card, or one device with its own
/// name. Anything shared (Windows' own layers, a driver many devices use, a USB hub) is <see cref="PartKind.None"/>,
/// and then nothing is said: naming the wrong part would send someone after a problem they don't have.
/// </summary>
public static partial class DriverParts
{
    /// <summary>
    /// Windows' own files and the shared layers every device of a kind goes through: their time is the device's below
    /// them, which can't be told from here. Storage controllers too (v1 says nothing about storage).
    /// </summary>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntoskrnl.exe", "ntkrnlmp.exe", "hal.dll",
        "ndis.sys", "tcpip.sys", "netio.sys", "afd.sys", "netbt.sys", "wfplwfs.sys", "ndisuio.sys",
        "wdf01000.sys", "wdfldr.sys", "wpprecorder.sys", "ucx01000.sys",
        "acpi.sys", "pci.sys", "intelppm.sys", "amdppm.sys", "processr.sys",
        "storport.sys", "storahci.sys", "stornvme.sys", "ataport.sys", "classpnp.sys", "disk.sys",
        "usbport.sys", "usbxhci.sys", "usbhub3.sys", "usbhub.sys", "usbccgp.sys", "usbehci.sys", "usbuhci.sys", "usbohci.sys",
        "hdaudbus.sys", "portcls.sys", "ks.sys", "drmk.sys",
        "dxgkrnl.sys", "dxgmms1.sys", "dxgmms2.sys", "watchdog.sys", "basicdisplay.sys", "basicrender.sys",
        "hidclass.sys", "hidparse.sys", "hidusb.sys", "kbdclass.sys", "mouclass.sys", "i8042prt.sys",
        "bthport.sys", "bthusb.sys", "ksecdd.sys", "cng.sys",
    };

    /// <summary>The device classes (Windows' own names) whose single device can be named by its own name.</summary>
    private static readonly HashSet<string> DeviceClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "MEDIA", "AudioEndpoint", "USB", "HIDClass", "Bluetooth", "Camera", "Image",
    };

    public static DriverPart Name(string driverFile, IReadOnlyList<(string Class, string Name)> devices)
    {
        string file = (driverFile ?? "").Trim().ToLowerInvariant();
        var none = new DriverPart(file, PartKind.None, null, null);
        if (file.Length == 0 || Shared.Contains(file) || DriverFiles.IsWindowsCore(file) || devices is null || devices.Count == 0) return none;

        // Every device the driver serves must be of one kind: a driver for a network card and a webcam both isn't "the
        // network card" (seen with some vendors' all-in-one drivers).
        bool net = true, display = true, device = true;
        foreach (var (cls, _) in devices)
        {
            net &= Is(cls, "Net");
            display &= Is(cls, "Display");
            device &= DeviceClasses.Contains(cls ?? "");
        }

        if (display)
        {
            var names = Distinct(devices, d => true);
            return new DriverPart(file, PartKind.Gpu, null, names.Count == 1 ? names[0] : null);
        }

        if (net)
        {
            // Virtual adapters (VPN tunnels, Hyper-V switches, Windows' WAN miniports) carry no hardware of their own:
            // left out, and a driver with nothing else is never named.
            var real = new List<string>();
            foreach (var (_, n) in devices)
                if (!string.IsNullOrWhiteSpace(n) && !VirtualAdapter().IsMatch(n)) real.Add(Clean(n));
            if (real.Count == 0) return none;
            bool wifi = WiFi().IsMatch(real[0]);
            foreach (var n in real)
                if (WiFi().IsMatch(n) != wifi) return none; // Wi-Fi and Ethernet both: which one it was can't be said
            return new DriverPart(file, wifi ? PartKind.WiFi : PartKind.Ethernet, null, real.Count == 1 ? real[0] : null);
        }

        if (device)
        {
            // Hubs, host controllers and Windows' generic names are the plumbing, not a thing anyone plugged in.
            var named = Distinct(devices, n => !Generic().IsMatch(n));
            int count = 0;
            foreach (var (_, n) in devices)
                if (!string.IsNullOrWhiteSpace(n) && !Generic().IsMatch(n)) count++;
            if (count != 1 || named.Count != 1) return none;
            return new DriverPart(file, PartKind.Device, named[0], named[0]);
        }

        return none;
    }

    /// <summary>A device's name without trademark marks ("Intel(R) I211" → "Intel I211").</summary>
    public static string Clean(string name)
    {
        foreach (var mark in Marks) name = name.Replace(mark, "", StringComparison.OrdinalIgnoreCase);
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly string[] Marks = ["(r)", "(tm)", "®", "™"];

    private static bool Is(string? cls, string want) => string.Equals(cls, want, StringComparison.OrdinalIgnoreCase);

    private static List<string> Distinct(IReadOnlyList<(string Class, string Name)> devices, Func<string, bool> keep)
    {
        var list = new List<string>();
        foreach (var (_, n) in devices)
        {
            if (string.IsNullOrWhiteSpace(n) || !keep(n)) continue;
            string c = Clean(n);
            if (!list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
        }
        return list;
    }

    [GeneratedRegex(@"wi-?fi|wireless|wlan|802\.11", RegexOptions.IgnoreCase)]
    private static partial Regex WiFi();

    [GeneratedRegex(@"hyper-v|wan miniport|\bvpn\b|\btap\b|\btun\b|loopback|bluetooth|virtual|vmware|virtualbox|wireguard|tunnel|kernel debug", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualAdapter();

    [GeneratedRegex(@"\bhub\b|host controller|^usb composite device$|^usb input device$|^generic usb", RegexOptions.IgnoreCase)]
    private static partial Regex Generic();
}

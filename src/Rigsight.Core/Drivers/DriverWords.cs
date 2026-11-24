namespace Rigsight.Core.Drivers;

/// <summary>What a driver is about, for what can be tried (see <see cref="DriverWords.Tips"/>).</summary>
public enum DriverArea { Other, Storage, Network, Graphics, Usb, Sound, Power, Framework, Input, Bluetooth, Windows, Device }

/// <summary>
/// Any driver said in plain words (issue #15 follow-up, approved mock-up 2026-11-24): a part RigSight can name is "Your GPU"
/// (see <see cref="DriverPart.Say"/>); Windows' own shared drivers say what they are ("Windows' storage driver"), since
/// which device below them it was can't be told; anything else is "The driver x.sys". The file is always said too, small
/// and grey, for anyone who wants to search it. Technical words are kept out of the sentences, never out of the app.
/// </summary>
public static class DriverWords
{
    private static readonly Dictionary<string, DriverArea> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        ["storport.sys"] = DriverArea.Storage, ["stornvme.sys"] = DriverArea.Storage, ["storahci.sys"] = DriverArea.Storage,
        ["ataport.sys"] = DriverArea.Storage, ["classpnp.sys"] = DriverArea.Storage, ["disk.sys"] = DriverArea.Storage,
        ["ndis.sys"] = DriverArea.Network, ["tcpip.sys"] = DriverArea.Network, ["netio.sys"] = DriverArea.Network, ["afd.sys"] = DriverArea.Network,
        ["netbt.sys"] = DriverArea.Network, ["wfplwfs.sys"] = DriverArea.Network, ["ndisuio.sys"] = DriverArea.Network,
        ["dxgkrnl.sys"] = DriverArea.Graphics, ["dxgmms1.sys"] = DriverArea.Graphics, ["dxgmms2.sys"] = DriverArea.Graphics, ["watchdog.sys"] = DriverArea.Graphics,
        ["basicdisplay.sys"] = DriverArea.Graphics, ["basicrender.sys"] = DriverArea.Graphics,
        ["usbport.sys"] = DriverArea.Usb, ["usbxhci.sys"] = DriverArea.Usb, ["usbhub3.sys"] = DriverArea.Usb, ["usbhub.sys"] = DriverArea.Usb,
        ["usbccgp.sys"] = DriverArea.Usb, ["ucx01000.sys"] = DriverArea.Usb, ["usbehci.sys"] = DriverArea.Usb, ["usbuhci.sys"] = DriverArea.Usb, ["usbohci.sys"] = DriverArea.Usb,
        ["hdaudbus.sys"] = DriverArea.Sound, ["portcls.sys"] = DriverArea.Sound, ["ks.sys"] = DriverArea.Sound, ["drmk.sys"] = DriverArea.Sound,
        ["acpi.sys"] = DriverArea.Power, ["pci.sys"] = DriverArea.Power, ["intelppm.sys"] = DriverArea.Power, ["amdppm.sys"] = DriverArea.Power, ["processr.sys"] = DriverArea.Power,
        ["wdf01000.sys"] = DriverArea.Framework, ["wdfldr.sys"] = DriverArea.Framework, ["wpprecorder.sys"] = DriverArea.Framework,
        ["hidclass.sys"] = DriverArea.Input, ["hidparse.sys"] = DriverArea.Input, ["hidusb.sys"] = DriverArea.Input,
        ["kbdclass.sys"] = DriverArea.Input, ["mouclass.sys"] = DriverArea.Input, ["i8042prt.sys"] = DriverArea.Input,
        ["bthport.sys"] = DriverArea.Bluetooth, ["bthusb.sys"] = DriverArea.Bluetooth,
        ["ntoskrnl.exe"] = DriverArea.Windows, ["ntkrnlmp.exe"] = DriverArea.Windows, ["hal.dll"] = DriverArea.Windows,
        ["ksecdd.sys"] = DriverArea.Windows, ["cng.sys"] = DriverArea.Windows,
    };

    /// <summary>What a driver is about: its part's kind when it can be named, else Windows' shared layer it belongs to.</summary>
    public static DriverArea AreaOf(string file, DriverPart? part) => part?.Kind switch
    {
        PartKind.Gpu => DriverArea.Graphics,
        PartKind.Ethernet or PartKind.WiFi => DriverArea.Network,
        PartKind.Device => DriverArea.Device,
        _ => Shared.TryGetValue(file, out var area) ? area : DriverArea.Other,
    };

    /// <summary>A part RigSight can name (it has a usual of its own and nothing else shares it).</summary>
    private static string? Named(DriverPart? part) => part?.Say;

    /// <summary>Who it was, at the start of a sentence: "Your GPU", "Windows' storage driver", "The driver e1d68x64.sys".</summary>
    public static string Who(string file, DriverPart? part) => Named(part) is { } say ? Lang.T($"Your {say}") : AreaOf(file, part) switch
    {
        DriverArea.Storage => Lang.T("Windows' storage driver"),
        DriverArea.Network => Lang.T("Windows' network driver"),
        DriverArea.Graphics => Lang.T("Windows' graphics system"),
        DriverArea.Usb => Lang.T("Windows' USB driver"),
        DriverArea.Sound => Lang.T("Windows' sound driver"),
        DriverArea.Power => Lang.T("Windows' power management driver"),
        DriverArea.Framework => Lang.T("Windows' driver framework"),
        DriverArea.Input => Lang.T("Windows' keyboard and mouse driver"),
        DriverArea.Bluetooth => Lang.T("Windows' Bluetooth driver"),
        DriverArea.Windows => Lang.T("Windows itself"),
        _ => Lang.T($"The driver {file}"),
    };

    /// <summary>A few grey words after the file on a shared driver ("used by every drive"); null when there's nothing to add.</summary>
    public static string? SharedBy(string file, DriverPart? part) => Named(part) is not null ? null : AreaOf(file, part) switch
    {
        DriverArea.Storage => Lang.T("used by every drive"),
        DriverArea.Network => Lang.T("used by every network adapter"),
        DriverArea.Graphics => Lang.T("between apps and the GPU's own driver"),
        DriverArea.Usb => Lang.T("used by every USB device"),
        DriverArea.Sound => Lang.T("used by every sound device"),
        DriverArea.Framework => Lang.T("used by many devices' drivers"),
        DriverArea.Input => Lang.T("used by every keyboard and mouse"),
        DriverArea.Bluetooth => Lang.T("used by every Bluetooth device"),
        _ => null,
    };

    /// <summary>What the driver is, in a sentence or two, for the opened mark.</summary>
    public static string What(string file, DriverPart? part)
    {
        if (Named(part) is { } say)
            return part!.Model is { Length: > 0 } model ? Lang.T($"{file} is the driver of your {say}, {model}.") : Lang.T($"{file} is the driver of your {say}.");
        return AreaOf(file, part) switch
        {
            DriverArea.Storage => Lang.T($"{file} is Windows' own storage driver. Every SSD and hard drive goes through it, so RigSight can't tell which drive it was."),
            DriverArea.Network => Lang.T($"{file} is part of Windows' own network drivers. Every network adapter goes through it, so RigSight can't tell which one it was."),
            DriverArea.Graphics => Lang.T($"{file} is Windows' own graphics system, between apps and your GPU's driver. A delay here often comes from the GPU's driver or from an app drawing on screen."),
            DriverArea.Usb => Lang.T($"{file} is Windows' own USB driver. Every USB device goes through it, so RigSight can't tell which one it was."),
            DriverArea.Sound => Lang.T($"{file} is part of Windows' own sound drivers. Every sound device goes through it, so RigSight can't tell which one it was."),
            DriverArea.Power => Lang.T($"{file} is Windows' own driver for power management and the processor's power states."),
            DriverArea.Framework => Lang.T($"{file} is Windows' driver framework. Many devices' drivers run through it, so RigSight can't tell which one it was."),
            DriverArea.Input => Lang.T($"{file} is Windows' own driver for keyboards, mice and controllers."),
            DriverArea.Bluetooth => Lang.T($"{file} is Windows' own Bluetooth driver. Every Bluetooth device goes through it."),
            DriverArea.Windows => Lang.T($"{file} is part of Windows itself."),
            _ => Lang.T($"{file} is a driver RigSight couldn't match to a device. Searching its name usually tells you what it belongs to."),
        };
    }

    /// <summary>The line every driver sentence shares: a driver holding a core this long can be felt.</summary>
    public static string WhyItMatters => Lang.T("A driver that keeps a CPU core busy for more than about 1 ms at a time can cause short hitches in sound, the pointer or games.");

    /// <summary>
    /// Careful steps anyone can undo, for the kind of driver; none where there's nothing safe to suggest. Each a title and,
    /// where it helps, a quieter line under it.
    /// </summary>
    public static List<(string Step, string? More)> Tips(DriverArea area) => area switch
    {
        DriverArea.Storage =>
        [
            (Lang.T("Check your SSD maker's app for a firmware update."), Lang.T("Samsung Magician, WD Dashboard, Crucial Storage Executive and the like.")),
            (Lang.T("If it keeps happening at the same time of day, see what runs then: a backup, a cloud sync or a game update."), Lang.T("Storage on this PC shows what reads and writes the most.")),
        ],
        DriverArea.Network =>
        [
            (Lang.T("Get the network adapter's driver from your PC's or board's maker, or the adapter's maker."), null),
            (Lang.T("On Wi-Fi, try a cable for a day to see whether the delays follow the Wi-Fi."), null),
        ],
        DriverArea.Graphics =>
        [
            (Lang.T("Try the next graphics driver, or the one before, with a clean install."), Lang.T("The Timeline shows each driver you had, and what changed with it.")),
            (Lang.T("If it happens with no game running, close overlays and apps that draw on screen one at a time to see which it follows."), null),
        ],
        DriverArea.Usb =>
        [
            (Lang.T("Unplug USB devices you don't need, one at a time, to see which one the delays follow."), null),
            (Lang.T("Try a different USB port, ideally one on the back of the PC."), null),
        ],
        DriverArea.Sound =>
        [
            (Lang.T("Get the sound driver from your PC's or board's maker."), null),
            (Lang.T("Turn off sound enhancements for your speakers or headset in Windows' sound settings."), null),
        ],
        DriverArea.Power =>
        [
            (Lang.T("Look for a newer BIOS from your board's or PC's maker."), null),
            (Lang.T("If you use a power plan other than Balanced, try Balanced for a day, or the other way round."), null),
        ],
        DriverArea.Bluetooth =>
        [
            (Lang.T("Turn Bluetooth off for a while to see whether the delays stop."), null),
        ],
        DriverArea.Device =>
        [
            (Lang.T("Unplug it for a while to see whether the delays stop."), null),
            (Lang.T("Look for a newer driver or firmware from its maker."), null),
        ],
        _ => [],
    };

    /// <summary>A burst's length as said: "4.4 ms", "0.3 ms"; under 0.1 ms "0.1 ms" (the ring keeps nothing shorter that matters).</summary>
    public static string Ms(double us) => Lang.T($"{Math.Max(0.1, us / 1000.0):0.0} ms");

    /// <summary>"usually under 0.5 ms": the driver's usual longest each hour.</summary>
    public static string Usually(int usualUs) => Lang.T($"usually under {Ms(usualUs)}");

    /// <summary>
    /// The grey line: the file, its longest, its usual, and what a shared driver is used by.
    /// "storport.sys · up to 4.4 ms, usually under 0.5 ms · used by every drive".
    /// </summary>
    public static string FileLine(string file, DriverPart? part, int longestUs, int? usualUs)
    {
        string line = usualUs is int u ? $"{file} · " + Lang.T($"up to {Ms(longestUs)}, {Usually(u)}") : $"{file} · " + Lang.T($"up to {Ms(longestUs)}");
        return SharedBy(file, part) is { } by ? $"{line} · {by}" : line;
    }
}

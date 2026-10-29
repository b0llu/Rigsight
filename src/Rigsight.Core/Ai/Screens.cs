using System.Runtime.InteropServices;

namespace Rigsight.Core.Ai;

/// <summary>The screens Windows shows on now, with the refresh rate each runs at and the highest it offers at that size,
/// so an answer about frame limits and VSync compares with the real screen, never an assumed one.</summary>
public static class Screens
{
    public sealed record Screen(string Name, bool Main, int Width, int Height, int Hz, int MaxHz);

    public static List<Screen> Read()
    {
        var list = new List<Screen>();
        var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (int i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++, adapter.Size = Marshal.SizeOf<DisplayDevice>())
        {
            if ((adapter.Flags & AttachedToDesktop) == 0) continue;
            var now = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(adapter.Name, CurrentSettings, ref now)) continue;
            int max = now.Frequency;
            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            for (int m = 0; EnumDisplaySettings(adapter.Name, m, ref mode); m++)
                if (mode.Width == now.Width && mode.Height == now.Height) max = Math.Max(max, mode.Frequency);
            var monitor = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            string name = EnumDisplayDevices(adapter.Name, 0, ref monitor, 0) && !monitor.String.Contains("Generic", StringComparison.OrdinalIgnoreCase)
                ? monitor.String.Trim() : "";
            list.Add(new(name, (adapter.Flags & Primary) != 0, now.Width, now.Height, now.Frequency, max));
        }
        return list;
    }

    /// <summary>One line per screen for get_pc_info ("Screen (main): ASUS VG27AQ, 2560x1440 at 144 Hz (it offers up to 165 Hz)").</summary>
    public static IEnumerable<string> Lines()
    {
        List<Screen> screens;
        try { screens = Read(); }
        catch (Exception ex) { Log.Error("ai", ex); screens = []; }
        if (screens.Count == 0) { yield return "Screen: couldn't be read (its refresh rate isn't known)."; yield break; }
        foreach (var s in screens.OrderByDescending(s => s.Main))
            yield return $"Screen{(s.Main ? " (main)" : "")}: {(s.Name.Length > 0 ? s.Name + ", " : "")}{s.Width}x{s.Height} at {s.Hz} Hz"
                + (s.MaxHz > s.Hz ? $" (it offers up to {s.MaxHz} Hz at this size)" : "");
    }

    private const int CurrentSettings = -1, AttachedToDesktop = 0x1, Primary = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string String;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    // DEVMODEW with the display half of its union; only the size and refresh fields are read.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, Width, Height, DisplayFlags, Frequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, int index, ref DisplayDevice info, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode info);
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// Temperatures Windows itself gives, without admin rights or a driver of RigSight's: an NVMe drive's (the storage
/// driver answers a "temperature" query on a drive opened with no read or write access) and a graphics card's (its
/// driver's own figure, the one Task Manager shows). Used only where LibreHardwareMonitor gives none (see
/// <see cref="WindowsTempSensors"/>): a standard account's agent opens no drive, and a card the library doesn't know
/// yet has no temperature. Each call is a few microseconds.
/// </summary>
internal static class WindowsTemps
{
    /// <summary>A physical drive as Windows describes it: its number, how it's connected (17 is NVMe) and its model.</summary>
    internal sealed record Drive(int Number, int Bus, string Model, SafeFileHandle Handle);

    /// <summary>The highest drive number looked at: Windows numbers drives from 0 with no gaps but for ones unplugged.</summary>
    private const int MaxDrives = 32;

    /// <summary>NVMe drives (bus 17) that answer the temperature query, kept open; the caller disposes the handles it doesn't keep.</summary>
    public static List<Drive> NvmeDrives()
    {
        var list = new List<Drive>();
        for (int n = 0; n < MaxDrives; n++)
        {
            var handle = CreateFile($@"\\.\PhysicalDrive{n}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); continue; }
            var buffer = new byte[1024];
            int got = Query(handle, StorageDeviceProperty, buffer);
            int bus = got >= 32 ? BitConverter.ToInt32(buffer, 28) : -1;
            string model = got >= 20 ? Text(buffer, got, BitConverter.ToInt32(buffer, 16)) : "";
            if (bus != BusNvme || DriveTemperature(handle) is null)
            {
                handle.Dispose();
                continue;
            }
            list.Add(new Drive(n, bus, model.Length > 0 ? model : $"Drive {n}", handle));
        }
        return list;
    }

    /// <summary>The drive's own temperature (its first figure: an NVMe drive's "composite"), °C; null when it gives none.</summary>
    public static double? DriveTemperature(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed) return null;
        var buffer = new byte[512];
        int got = Query(handle, StorageDeviceTemperatureProperty, buffer);
        return ParseDriveTemperature(buffer.AsSpan(0, got));
    }

    /// <summary>The limits the drive reports for itself, °C: where it warns, and where it's in danger; null where it gives none.</summary>
    public static (double? Warning, double? Critical) DriveLimits(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed) return (null, null);
        var buffer = new byte[512];
        int got = Query(handle, StorageDeviceTemperatureProperty, buffer);
        return ParseDriveLimits(buffer.AsSpan(0, got));
    }

    /// <summary>The descriptor's WarningTemperature (at 10) and CriticalTemperature (at 8); a figure outside 40-120 °C is none.</summary>
    internal static (double? Warning, double? Critical) ParseDriveLimits(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return (null, null);
        static double? Limit(short t) => t is >= 40 and <= 120 ? t : null;
        return (Limit(BitConverter.ToInt16(data[10..])), Limit(BitConverter.ToInt16(data[8..])));
    }

    /// <summary>
    /// STORAGE_TEMPERATURE_DATA_DESCRIPTOR: Version, Size, CriticalTemperature, WarningTemperature, InfoCount at 12, then
    /// from 24 one STORAGE_TEMPERATURE_INFO of 16 bytes each (Index, then Temperature as a signed short at 2). The first
    /// is the drive's own; a figure outside 1-149 °C is no reading.
    /// </summary>
    internal static double? ParseDriveTemperature(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 + 16) return null;
        int count = BitConverter.ToUInt16(data[12..]);
        if (count < 1) return null;
        short t = BitConverter.ToInt16(data[26..]);
        return t is > 0 and < 150 ? t : null;
    }

    /// <summary>A graphics adapter as its driver names it, with the handle its readings are asked through.</summary>
    internal sealed record Adapter(uint Handle, string Name, uint Flags);

    /// <summary>Every graphics adapter Windows has (each opened: <see cref="Close"/> those not kept).</summary>
    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        var e = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.Count == 0 || e.Count > 64) return list;
        e.Adapters = Marshal.AllocHGlobal((int)e.Count * AdapterInfoSize);
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return list;
            for (int i = 0; i < e.Count; i++)
            {
                uint handle = (uint)Marshal.ReadInt32(e.Adapters + i * AdapterInfoSize);
                list.Add(new Adapter(handle, AdapterName(handle), AdapterFlags(handle)));
            }
        }
        finally { Marshal.FreeHGlobal(e.Adapters); }
        return list;
    }

    /// <summary>Lets go of an adapter's handle.</summary>
    public static void Close(uint handle)
    {
        var close = new CloseAdapter { Handle = handle };
        D3DKMTCloseAdapter(ref close);
    }

    /// <summary>The temperature the card's driver reports, °C (D3DKMT_ADAPTER_PERFDATA, in tenths); null when it gives none.</summary>
    public static double? GpuTemperature(uint adapter)
    {
        IntPtr data = Marshal.AllocHGlobal(PerfDataSize);
        try
        {
            for (int i = 0; i < PerfDataSize; i++) Marshal.WriteByte(data, i, 0);
            var q = new QueryAdapterInfo { Handle = adapter, Type = AdapterPerfData, Data = data, Size = PerfDataSize };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
            return ParseGpuTemperature((uint)Marshal.ReadInt32(data, 56));
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    /// <summary>Tenths of a degree to °C; 0 (no figure) and anything outside 1-149 °C is no reading.</summary>
    internal static double? ParseGpuTemperature(uint tenths) => tenths is > 10 and < 1500 ? tenths / 10.0 : null;

    /// <summary>
    /// The adapter for a card LibreHardwareMonitor names <paramref name="name"/>: the one whose driver gives the same
    /// name (ignoring case, spaces and ®/™ marks), that renders and isn't Windows' own software one. Null when none does.
    /// </summary>
    internal static Adapter? Match(string name, IEnumerable<Adapter> adapters)
    {
        string want = Plain(name);
        return want.Length == 0 ? null
            : adapters.FirstOrDefault(a => (a.Flags & RenderSupported) != 0 && (a.Flags & SoftwareDevice) == 0 && Plain(a.Name) == want);
    }

    /// <summary>A name with the marks and spacing that differ between the library and the driver taken out.</summary>
    internal static string Plain(string name) =>
        new([.. name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static string AdapterName(uint handle)
    {
        // D3DKMT_ADAPTERREGISTRYINFO: four strings of MAX_PATH characters, the adapter's name first.
        const int size = 4 * 260 * 2;
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(data, i, 0);
            var q = new QueryAdapterInfo { Handle = handle, Type = AdapterRegistryInfo, Data = data, Size = size };
            return D3DKMTQueryAdapterInfo(ref q) == 0 ? (Marshal.PtrToStringUni(data, 259).TrimEnd('\0').Trim()) : "";
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static uint AdapterFlags(uint handle)
    {
        IntPtr data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, 0);
            var q = new QueryAdapterInfo { Handle = handle, Type = AdapterType, Data = data, Size = 4 };
            return D3DKMTQueryAdapterInfo(ref q) == 0 ? (uint)Marshal.ReadInt32(data) : 0;
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    /// <summary>A NUL-ended string at <paramref name="offset"/> in a STORAGE_DEVICE_DESCRIPTOR ("" when there's none).</summary>
    private static string Text(byte[] buffer, int length, int offset)
    {
        if (offset <= 0 || offset >= length) return "";
        int end = Array.IndexOf(buffer, (byte)0, offset, length - offset);
        return Log.Printable(System.Text.Encoding.ASCII.GetString(buffer, offset, (end < 0 ? length : end) - offset)).Trim();
    }

    /// <summary>One STORAGE_PROPERTY_QUERY ("standard" query of <paramref name="property"/>); the bytes returned, 0 when it failed.</summary>
    private static int Query(SafeFileHandle handle, int property, byte[] buffer)
    {
        var query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0, 4), property);
        return DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, buffer, buffer.Length, out int got, IntPtr.Zero) ? got : 0;
    }

    private const int StorageDeviceProperty = 0, StorageDeviceTemperatureProperty = 52, BusNvme = 17;
    private const uint IoctlStorageQueryProperty = 0x2D1400, FileShareReadWrite = 3, OpenExisting = 3;
    private const int AdapterRegistryInfo = 8, AdapterType = 15, AdapterPerfData = 62, PerfDataSize = 64, AdapterInfoSize = 20;
    private const uint RenderSupported = 1, SoftwareDevice = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2 { public uint Count; public IntPtr Adapters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo { public uint Handle; public int Type; public IntPtr Data; public uint Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter { public uint Handle; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 e);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo q);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter c);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

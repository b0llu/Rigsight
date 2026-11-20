using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rigsight.Agent.Sensors.Devices;

/// <summary>
/// The HID devices Windows lists (their path, maker and product numbers, and what kind of reports they take), and a
/// way to talk to one: a report out, the reports that come back, each read with a time limit. Plain Windows calls
/// (SetupAPI and hid.dll), nothing installed.
/// </summary>
internal static class Hid
{
    /// <summary>One HID interface: a top-level collection of a device (a receiver lists several).</summary>
    internal sealed record Interface(string Path, ushort Vendor, ushort Product, ushort UsagePage, ushort Usage, int InputLength, int OutputLength);

    /// <summary>
    /// Every HID interface present, with its numbers and report sizes. An interface that can't even be opened for its
    /// attributes (a keyboard or mouse Windows keeps for itself) still gives its maker and product from its path.
    /// </summary>
    public static List<Interface> List(Func<ushort, bool>? vendor = null)
    {
        var list = new List<Interface>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return list;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                if (PathOf(set, ref data) is not { } path) continue;
                var (v, p) = NumbersFromPath(path);
                if (vendor is not null && !vendor(v)) continue;
                using var handle = Open(path, access: 0);
                if (handle.IsInvalid) continue;
                var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (HidD_GetAttributes(handle, ref attributes)) (v, p) = (attributes.VendorID, attributes.ProductID);
                if (vendor is not null && !vendor(v)) continue;
                ushort page = 0, usage = 0;
                int input = 0, output = 0;
                if (HidD_GetPreparsedData(handle, out var parsed))
                {
                    try
                    {
                        if (HidP_GetCaps(parsed, out var caps) == HIDP_STATUS_SUCCESS)
                            (page, usage, input, output) = (caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength);
                    }
                    finally { HidD_FreePreparsedData(parsed); }
                }
                list.Add(new Interface(path, v, p, page, usage, input, output));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    /// <summary>The maker and product numbers in an interface's path ("...vid_046d&amp;pid_c547..."), 0 where it has none.</summary>
    internal static (ushort Vendor, ushort Product) NumbersFromPath(string path)
    {
        static ushort After(string path, string mark)
        {
            int at = path.IndexOf(mark, StringComparison.OrdinalIgnoreCase);
            return at >= 0 && at + mark.Length + 4 <= path.Length
                && ushort.TryParse(path.AsSpan(at + mark.Length, 4), System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : (ushort)0;
        }
        return (After(path, "vid_"), After(path, "pid_"));
    }

    private static string? PathOf(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: its cbSize is the size of the fixed part, 8 in a 64-bit process.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle Open(string path, uint access) =>
        CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);

    /// <summary>
    /// An interface opened to send reports and read what comes back. Other programs (the maker's own app) keep their
    /// own copy of every report, so reading here takes nothing from them.
    /// </summary>
    internal sealed class Channel : IDisposable
    {
        private readonly FileStream _stream;
        private readonly int _inputLength;
        private readonly int _outputLength;

        private Channel(FileStream stream, int input, int output) => (_stream, _inputLength, _outputLength) = (stream, input, output);

        /// <summary>Null when the interface won't open for reading and writing.</summary>
        public static Channel? Open(Interface i)
        {
            var handle = Hid.Open(i.Path, GENERIC_READ | GENERIC_WRITE);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            try { return new Channel(new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true), i.InputLength, i.OutputLength); }
            catch { handle.Dispose(); return null; }
        }

        /// <summary>Sends one output report, padded to the interface's length. False if Windows refused it.</summary>
        public bool Send(ReadOnlySpan<byte> report)
        {
            var buffer = new byte[Math.Max(_outputLength, report.Length)];
            report.CopyTo(buffer);
            try
            {
                using var cts = new CancellationTokenSource(1000);
                _stream.WriteAsync(buffer, cts.Token).AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch { return false; }
        }

        /// <summary>The next input report, or null when none comes within <paramref name="timeoutMs"/>.</summary>
        public byte[]? Receive(int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            return ReceiveAsync(cts.Token).GetAwaiter().GetResult();
        }

        /// <summary>The next input report, or null once <paramref name="cancel"/> fires (or the device went).</summary>
        public async Task<byte[]?> ReceiveAsync(CancellationToken cancel)
        {
            var buffer = new byte[Math.Max(_inputLength, 64)];
            try
            {
                int n = await _stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
                return n > 0 ? buffer[..n] : null;
            }
            catch { return null; }
        }

        public void Dispose() => _stream.Dispose();
    }

    // ── Windows ───────────────────────────────────────────────────────────
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public fixed ushort Counts[10];
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}

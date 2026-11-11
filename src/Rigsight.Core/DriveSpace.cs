using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Rigsight.Core;

/// <summary>
/// A fixed drive as the Storage page lists it: its root ("C:\"), its size and free space in bytes, and the physical
/// drive it's on (Windows' number, the one LibreHardwareMonitor puts in its sensors' ids), for its temperature.
/// </summary>
public sealed record VolumeSpace(string Root, long Total, long Free, int? Disk)
{
    /// <summary>"C", as "the C drive" says it.</summary>
    public string Letter => Root.TrimEnd('\\').TrimEnd(':');

    /// <summary>How full it is, 0 to 1.</summary>
    public double UsedShare => Total > 0 ? Math.Clamp((double)(Total - Free) / Total, 0, 1) : 0;
}

/// <summary>The PC's fixed drives and how full they are, read as the Storage page reads them (Windows' own figures).</summary>
public static partial class DriveSpace
{
    /// <summary>The physical drive each root is on, asked of Windows once a root (a few microseconds; needs no admin rights).</summary>
    private static readonly ConcurrentDictionary<string, int?> Disks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every fixed drive that's ready, in the order of their letters; one that goes between "ready" and its size is left out.</summary>
    public static List<VolumeSpace> Fixed()
    {
        var list = new List<VolumeSpace>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return list; }
        foreach (var d in drives)
        {
            if (d.DriveType != DriveType.Fixed) continue;
            long total, free;
            try
            {
                if (!d.IsReady) continue;
                (total, free) = (d.TotalSize, d.TotalFreeSpace);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (total <= 0) continue;
            list.Add(new VolumeSpace(d.Name, total, free, Disks.GetOrAdd(d.Name, DiskOf)));
        }
        return [.. list.OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The physical drive a sensor's hardware is ("/nvme/2/temperature/0" is drive 2); null when its id doesn't say.</summary>
    public static int? DiskOfSensor(string id) =>
        SensorDisk().Match(id) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int disk) ? disk : null;

    [GeneratedRegex(@"^/[a-z]+/(\d+)/", RegexOptions.CultureInvariant)]
    private static partial Regex SensorDisk();

    /// <summary>The physical drive a volume's first part is on (VOLUME_DISK_EXTENTS); null when Windows wouldn't say.</summary>
    private static int? DiskOf(string root)
    {
        try
        {
            using var handle = CreateFile($@"\\.\{root.TrimEnd('\\')}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var buffer = new byte[256];
            // The count, then each extent with its disk's number first.
            return DeviceIoControl(handle, 0x560000, null, 0, buffer, buffer.Length, out int got, IntPtr.Zero) && got >= 12 && BitConverter.ToInt32(buffer, 0) >= 1
                ? BitConverter.ToInt32(buffer, 8) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

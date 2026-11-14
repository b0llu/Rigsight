using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rigsight.Agent.Storage;

/// <summary>
/// Windows' own count of the bytes each physical drive has read and written since it started (IOCTL_DISK_PERFORMANCE,
/// what Task Manager's disk graph reads), which doesn't depend on the disk trace: the sign that the drives were busy
/// while the trace said nothing (see <see cref="Network.TraceWatch"/>). The drive is opened with no read or write
/// access, which needs no admin rights and doesn't wake a sleeping hard drive.
/// </summary>
internal static class DiskCounters
{
    /// <summary>Bytes read and written by these drives together since Windows started; null when none could be read.</summary>
    public static long? TotalBytes(IEnumerable<int> disks)
    {
        long total = 0;
        bool any = false;
        var buffer = new byte[88]; // DISK_PERFORMANCE
        foreach (int disk in disks)
        {
            try
            {
                using var handle = CreateFile($@"\\.\PhysicalDrive{disk}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle.IsInvalid || !DeviceIoControl(handle, IoctlDiskPerformance, IntPtr.Zero, 0, buffer, buffer.Length, out int got, IntPtr.Zero) || got < 16)
                    continue;
                total += BitConverter.ToInt64(buffer, 0) + BitConverter.ToInt64(buffer, 8); // BytesRead, BytesWritten
                any = true;
            }
            catch
            {
                // A drive that was just unplugged: the others still count.
            }
        }
        return any ? total : null;
    }

    private const uint IoctlDiskPerformance = 0x70020, FileShareReadWrite = 3, OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

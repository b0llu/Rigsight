using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rigsight.Core;
using Rigsight.Models;

namespace Rigsight.Services;

/// <summary>
/// What Windows says of a physical drive, asked once when the sensor list arrives: whether it is an SSD or spins
/// (Windows' own "seek penalty"), how it is connected, its size, and whether Windows is on it. The drive is opened
/// with no read or write access, which needs no admin rights and doesn't wake a sleeping hard drive; each answer is a
/// few microseconds. The drives' numbers are the ones LibreHardwareMonitor puts in their sensors' ids ("/nvme/2/…"
/// is \\.\PhysicalDrive2).
/// </summary>
public static class DriveProbe
{
    /// <summary>The drive numbered <paramref name="disk"/>; <see cref="DriveFacts.None"/> where Windows wouldn't say.</summary>
    public static DriveFacts Read(int disk)
    {
        try
        {
            using var handle = CreateFile($@"\\.\PhysicalDrive{disk}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                Log.Write("drives", $"Drive {disk}: expected to open \\\\.\\PhysicalDrive{disk} for its type and size, found error {Marshal.GetLastWin32Error()}");
                return DriveFacts.None;
            }

            var buffer = new byte[1024];
            string? bus = null;
            if (Query(handle, StorageDeviceProperty, buffer) >= 32)
                bus = BitConverter.ToInt32(buffer, 28) switch { 17 => "NVMe", 11 or 3 => "SATA", 7 => "USB", _ => null };
            var kind = Query(handle, StorageDeviceSeekPenaltyProperty, buffer) >= 9 ? buffer[8] != 0 ? DriveKind.HardDrive : DriveKind.Ssd : DriveKind.Unknown;
            long? bytes = DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, buffer, buffer.Length, out int got, IntPtr.Zero) && got >= 32
                ? BitConverter.ToInt64(buffer, 24) : null;
            if (kind == DriveKind.Unknown || bytes is null)
                Log.Write("drives", $"Drive {disk}: expected its type and size from Windows, found type {kind}, bus {bus ?? "unknown"}, size {bytes?.ToString() ?? "unknown"}");
            return new DriveFacts(kind, bus, bytes, disk == WindowsDisk.Value);
        }
        catch (Exception ex)
        {
            Log.Error("drives", ex);
            return DriveFacts.None;
        }
    }

    /// <summary>The letter of the drive Windows is on ('C' nearly always).</summary>
    public static char WindowsLetter => Path.GetPathRoot(Environment.SystemDirectory) is [var letter, ':', ..] ? char.ToUpperInvariant(letter) : 'C';

    /// <summary>The number of the physical drive Windows' own volume is on; -1 when Windows wouldn't say.</summary>
    private static readonly Lazy<int> WindowsDisk = new(() =>
    {
        try
        {
            using var handle = CreateFile($@"\\.\{WindowsLetter}:", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            var buffer = new byte[256];
            // VOLUME_DISK_EXTENTS: the count, then each extent with its disk's number first.
            if (!handle.IsInvalid && DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, null, 0, buffer, buffer.Length, out int got, IntPtr.Zero)
                && got >= 12 && BitConverter.ToInt32(buffer, 0) >= 1)
                return BitConverter.ToInt32(buffer, 8);
            Log.Write("drives", $"Expected the drive the {WindowsLetter} drive is on, found error {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Error("drives", ex);
        }
        return -1;
    });

    /// <summary>One STORAGE_PROPERTY_QUERY ("standard" query of <paramref name="property"/>); the bytes returned, 0 when it failed.</summary>
    private static int Query(SafeFileHandle handle, int property, byte[] buffer)
    {
        var query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0, 4), property);
        return DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, buffer, buffer.Length, out int got, IntPtr.Zero) ? got : 0;
    }

    private const int StorageDeviceProperty = 0, StorageDeviceSeekPenaltyProperty = 7;
    private const uint IoctlStorageQueryProperty = 0x2D1400, IoctlDiskGetDriveGeometryEx = 0x700A0, IoctlVolumeGetVolumeDiskExtents = 0x560000;
    private const uint FileShareReadWrite = 3, OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

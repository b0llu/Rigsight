using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// Where winget.exe really is. The way everyone reaches it, an App Installer alias in the user's own WindowsApps folder
/// (or PATH), is somewhere any program of the user can write, and whatever the agent starts runs with admin rights. So
/// the alias is only read for where it points, and what's started is App Installer's own file under
/// Program Files\WindowsApps, which only Windows can change.
/// </summary>
internal static class Winget
{
    /// <summary>winget.exe in App Installer's own folder, or null (no App Installer, or an alias that points elsewhere).</summary>
    public static string? Find()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        return AliasTarget(alias) is { } target && IsAppInstallers(target, Environment.GetFolderPath) && File.Exists(target)
            ? Path.GetFullPath(target) : null;
    }

    /// <summary>Whether <paramref name="exe"/> is winget.exe in App Installer's package folder under Program Files\WindowsApps.</summary>
    internal static bool IsAppInstallers(string exe, Func<Environment.SpecialFolder, string> folder)
    {
        string full;
        try { full = Path.GetFullPath(exe); }
        catch { return false; }
        var root = folder(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(root)) return false;
        var packages = Path.Combine(Path.TrimEndingDirectorySeparator(root), "WindowsApps") + Path.DirectorySeparatorChar;
        return full.StartsWith(packages + "Microsoft.DesktopAppInstaller_", StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(full)) + Path.DirectorySeparatorChar, packages, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(full), "winget.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The program an app alias (a zero-length file that Windows redirects) stands for, or null when it isn't one.</summary>
    internal static string? AliasTarget(string alias)
    {
        try
        {
            using var file = CreateFile(alias, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, OpenReparsePoint, IntPtr.Zero);
            if (file.IsInvalid) return null;
            var buffer = new byte[16 * 1024];
            return DeviceIoControl(file, GetReparsePoint, IntPtr.Zero, 0, buffer, buffer.Length, out int read, IntPtr.Zero)
                ? TargetIn(buffer.AsSpan(0, read)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The program named in an alias's data: a tag, a length, a version, then text separated by zeros (the package, the
    /// app in it, the program, its kind).
    /// </summary>
    internal static string? TargetIn(ReadOnlySpan<byte> data)
    {
        const int Header = 8, Version = 4;
        if (data.Length < Header + Version || BitConverter.ToUInt32(data) != AppExecLink) return null;
        var parts = Encoding.Unicode.GetString(data[(Header + Version)..]).Split('\0');
        return parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
    }

    private const uint AppExecLink = 0x8000001B;      // IO_REPARSE_TAG_APPEXECLINK
    private const uint GetReparsePoint = 0x000900A8;  // FSCTL_GET_REPARSE_POINT
    private const int OpenReparsePoint = 0x00200000;  // FILE_FLAG_OPEN_REPARSE_POINT: the alias itself, not what it stands for

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, int access, FileShare share, IntPtr security, FileMode mode, int flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

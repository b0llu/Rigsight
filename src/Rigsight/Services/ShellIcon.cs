using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rigsight.Services;

/// <summary>
/// An app's own icon, as the Start menu shows it, for an "Open NVIDIA App" button (the approved artifact of 2026-10-24:
/// the line carries the app's icon). Read through the Shell from its Apps folder entry; null when Windows won't give one,
/// and the line keeps its plain sign.
/// </summary>
public static class ShellIcon
{
    private static readonly Dictionary<string, ImageSource?> Kept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon for "shell:AppsFolder\&lt;id&gt;" (or any Shell path), <paramref name="size"/> pixels square.</summary>
    public static ImageSource? Of(string shellPath, int size = 32)
    {
        lock (Kept) if (Kept.TryGetValue(shellPath, out var kept)) return kept;
        ImageSource? icon = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(shellPath, IntPtr.Zero, ref iid, out var factory) == 0 && factory is not null)
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4 /* SIIGBF_ICONONLY */, out var bitmap) == 0 && bitmap != IntPtr.Zero)
                {
                    try
                    {
                        var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        icon = source;
                    }
                    finally { DeleteObject(bitmap); }
                }
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            Core.Log.Write("ask", $"No icon for {shellPath}: {ex.Message}");
        }
        lock (Kept) Kept[shellPath] = icon;
        return icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

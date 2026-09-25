using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rigsight.Services;

/// <summary>Extracts and caches app icons from executables.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows' plain program icon, for processes without an icon of their own.</summary>
    public static ImageSource? Program => _program ??= StockIcon(SiidApplication);
    private static ImageSource? _program;

    private const uint SiidApplication = 2, ShgsiIcon = 0x100, ShgsiLargeIcon = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
    }

    [DllImport("shell32.dll")] private static extern int SHGetStockIconInfo(uint siid, uint flags, ref SHSTOCKICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    private static ImageSource? StockIcon(uint siid)
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(siid, ShgsiIcon | ShgsiLargeIcon, ref info) != 0 || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (Cache.TryGetValue(exePath, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            if (File.Exists(exePath))
            {
                using var icon = System.Drawing.Icon.ExtractIcon(exePath, 0, 64) ?? System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
            }
        }
        catch
        {
            // Some protected executables can't be read.
        }
        Cache[exePath] = image;
        return image;
    }
}

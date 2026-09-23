using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rigsight.Services;

/// <summary>Extracts and caches app icons from executables.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

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

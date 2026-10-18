using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rigsight.Services;

/// <summary>
/// The Games page's pictures: the covers, banners and logos the launchers keep on the PC, read at the size they're
/// shown, and a game's own icon, large and centred, for a game no launcher keeps a picture of.
/// </summary>
public static class GamePictures
{
    private static readonly ConcurrentDictionary<(string Path, int Width), ImageSource?> Pictures = new();
    private static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A picture file, decoded at <paramref name="width"/> pixels across (a cover needn't be read at 600 to show at 150).</summary>
    public static ImageSource? Picture(string? path, int width) =>
        string.IsNullOrEmpty(path) ? null : Pictures.GetOrAdd((path, width), key =>
        {
            try
            {
                if (!File.Exists(key.Path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = key.Width;
                image.UriSource = new Uri(key.Path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) // any: a half-written cover throws WIC's own errors too, and nothing may stop the library loading
            {
                Core.Log.Write("games", $"picture {key.Path} couldn't be read ({ex.GetType().Name}: {ex.Message})");
                return null;
            }
        });

    /// <summary>
    /// The game's own icon from its program, as large as the program holds it, while the program is there; else the one
    /// RigSight kept when it last ran (see <see cref="IconCache"/>). Cropped to what's visible and centred on a square,
    /// so every icon sits in the middle of its tile whatever empty edges its file has.
    /// </summary>
    public static ImageSource? Icon(string? exePath) =>
        string.IsNullOrEmpty(exePath) ? null : Icons.GetOrAdd(exePath, path =>
        {
            BitmapSource? source = null;
            try
            {
                if (File.Exists(path))
                {
                    using var icon = System.Drawing.Icon.ExtractIcon(path, 0, 256);
                    if (icon is not null)
                        source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch (Exception ex) // any: a damaged icon resource throws more than IO errors
            {
                Core.Log.Write("games", $"icon of {path} couldn't be read ({ex.GetType().Name}: {ex.Message})");
            }
            try
            {
                source ??= IconCache.Get(path) as BitmapSource;
                return source is null ? null : Centred(source);
            }
            catch (Exception ex)
            {
                Core.Log.Write("games", $"icon of {path} couldn't be centred ({ex.GetType().Name}: {ex.Message})");
                return null;
            }
        });

    private static readonly ConcurrentDictionary<string, string?> Programs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The game's own program in its folder, for a game RigSight never saw run (so it has no path for it): the largest
    /// program at the top of the folder or one level down, leaving out installers, crash reporters and launchers' helpers.
    /// </summary>
    public static string? ProgramIn(string? dir) =>
        string.IsNullOrEmpty(dir) ? null : Programs.GetOrAdd(dir, d =>
        {
            try
            {
                if (!Directory.Exists(d)) return null;
                var files = Directory.EnumerateFiles(d, "*.exe", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateDirectories(d).SelectMany(sub => Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly)));
                return files.Where(f => !NotTheGame.Any(n => Path.GetFileName(f).Contains(n, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            }
            catch (Exception) // a folder that went or can't be read: no program to show
            {
                return null;
            }
        });

    private static readonly string[] NotTheGame = ["unins", "setup", "crash", "report", "redist", "vc_", "dxsetup", "anticheat", "easyanticheat", "helper", "launcher"];

    /// <summary>The visible part of an icon (alpha above a trace), centred on a square of its larger side.</summary>
    internal static BitmapSource Centred(BitmapSource icon)
    {
        var bgra = icon.Format == PixelFormats.Bgra32 ? icon : new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        int l = w, t = h, r = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * stride + x * 4 + 3] > 24) { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y); }
        if (r < 0) { icon.Freeze(); return icon; }
        int cw = r - l + 1, ch = b - t + 1, side = Math.Max(cw, ch);
        var outPx = new byte[side * side * 4];
        int ox = (side - cw) / 2, oy = (side - ch) / 2;
        for (int y = 0; y < ch; y++)
            Buffer.BlockCopy(px, (t + y) * stride + l * 4, outPx, ((oy + y) * side + ox) * 4, cw * 4);
        var result = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, outPx, side * 4);
        result.Freeze();
        return result;
    }
}

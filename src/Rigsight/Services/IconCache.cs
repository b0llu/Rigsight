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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the icons a page is about to show, where its data is read (not on the window's thread): reading a program's
    /// icon from its file takes a few milliseconds each, and a page of thirty apps held the window up for as long.
    /// </summary>
    public static void Warm(IEnumerable<string?> exePaths)
    {
        foreach (var path in exePaths) Get(path);
    }

    /// <summary>Whether an icon has been read already (or found to have none).</summary>
    public static bool Has(string? exePath) => string.IsNullOrEmpty(exePath) || Cache.ContainsKey(exePath);

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
            string kept = KeptFile(exePath);
            if (!ReadKept(kept, out image))
            {
                image = FromProgram(exePath);
                Keep(kept, image as BitmapSource);
            }
        }
        catch
        {
            // Some protected executables can't be read.
        }
        Cache[exePath] = image;
        return image;
    }

    private static BitmapSource? FromProgram(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        using var icon = System.Drawing.Icon.ExtractIcon(exePath, 0, 64) ?? System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        if (icon is null) return null;
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    // ── Icons kept between runs ──
    // Reading an icon out of a program is quick once Windows has the file in memory and slow the first time after a
    // start (a game's exe took 100 to 460 ms each on a fast drive), and a page waits for the icons of every app in its
    // report: the first Home after sign-in, and the first look at a week, sat for seconds on them. Each icon read is
    // kept as a small picture in the data folder and read from there the next time.

    /// <summary>Where the kept icons are (the data folder's; a test points it elsewhere).</summary>
    internal static string KeptFolder { get; set; } = Core.RigsightPaths.IconCache;

    /// <summary>A kept icon is read from its program again after this long (an update can change it), a little later for some so they don't all fall due at once.</summary>
    internal static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    /// <summary>A program without an icon, or one that's gone, is looked at again sooner.</summary>
    internal static readonly TimeSpan NoneKeptFor = TimeSpan.FromDays(1);

    internal static string KeptFile(string exePath) => Path.Combine(KeptFolder,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(exePath.ToLowerInvariant())), 0, 16) + ".png");

    /// <summary>The icon kept for a program, if there is one and it isn't due to be read again (an empty file: the program has none).</summary>
    private static bool ReadKept(string file, out ImageSource? image)
    {
        image = null;
        var info = new FileInfo(file);
        if (!info.Exists) return false;
        var age = DateTime.UtcNow - info.LastWriteTimeUtc;
        if (info.Length == 0) return age < NoneKeptFor;
        if (age > KeptFor + TimeSpan.FromDays(Convert.ToByte(info.Name[..2], 16) % 30)) return false;
        try
        {
            using var stream = info.OpenRead();
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            // The same picture as the one read from the program: its pixels at 96 to the inch (a PNG's own figure is rounded).
            var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var raw = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
            pixels.CopyPixels(raw, pixels.PixelWidth * 4, 0);
            var source = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, 96, 96, PixelFormats.Bgra32, null, raw, pixels.PixelWidth * 4);
            source.Freeze();
            image = source;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return false; // not a picture any more: read from the program and kept anew
        }
    }

    /// <summary>For tests: the icon is read again (from the kept picture, else from the program) the next time it's asked for.</summary>
    internal static void Forget(string exePath) => Cache.TryRemove(exePath, out _);

    private static void Keep(string file, BitmapSource? icon)
    {
        try
        {
            Directory.CreateDirectory(KeptFolder);
            // Written beside it and moved into place, so a reader never meets half a picture.
            string part = $"{file}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.part";
            using (var stream = File.Create(part))
            {
                if (icon is not null)
                {
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(icon));
                    png.Save(stream);
                }
            }
            File.Move(part, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept this time: read from the program again next run.
        }
    }
}

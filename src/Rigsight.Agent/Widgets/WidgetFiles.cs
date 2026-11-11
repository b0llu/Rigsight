using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using Rigsight.Core.Settings;

#if RIGSIGHT_APP
// Linked into the app, which draws widgets with the same code for its Widgets page (see Rigsight.csproj).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

/// <summary>
/// The files a few tiles draw (widgets part three): a photos tile's pictures, a shortcuts tile's app icons. Only the
/// last few pictures and icons are kept, and a folder's list of pictures is read again at most once a minute.
/// </summary>
internal static class WidgetFiles
{
    private static readonly RecentIcons Icons = new(32);
    private static readonly RecentIcons Pictures = new(4);
    private static readonly Dictionary<string, (DateTime Read, string[] Files)> Folders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock FolderLock = new();
    private static readonly string[] PictureTypes = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];

    /// <summary>The longest side a picture is kept at: enough for a big tile at 3XL on a 4K screen.</summary>
    private const int MaxSide = 1200;

    /// <summary>The pictures in a folder (not its sub-folders), by name; none when it can't be read.</summary>
    public static string[] PicturesIn(string folder)
    {
        lock (FolderLock)
        {
            if (Folders.TryGetValue(folder, out var known) && DateTime.UtcNow - known.Read < TimeSpan.FromMinutes(1)) return known.Files;
            string[] files;
            try
            {
                files = [.. Directory.EnumerateFiles(folder).Where(f => PictureTypes.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                files = [];
            }
            Folders[folder] = (DateTime.UtcNow, files);
            return files;
        }
    }

    /// <summary>
    /// The picture a photos tile shows now: they take turns in name order, each for the tile's time, counted from the
    /// clock (so every window and every restart agrees, with nothing to remember).
    /// </summary>
    public static string? PictureNow(WidgetTile t, DateTime now)
    {
        if (t.Folder is not { } folder) return null;
        var files = PicturesIn(folder);
        if (files.Length == 0) return null;
        long step = new DateTimeOffset(now).ToUnixTimeSeconds() / Math.Max(1, t.Every);
        return files[(int)(step % files.Length)];
    }

    /// <summary>A picture, turned the way the camera held it and made no bigger than it needs to be; null when it can't be read.</summary>
    public static Bitmap? Picture(string path) => Pictures.Get(path, LoadPicture);

    private static Bitmap? LoadPicture(string path)
    {
        try
        {
            using var file = File.OpenRead(path); // read and let go: the file isn't held open
            using var image = Image.FromStream(file);
            if (image.PropertyIdList.Contains(0x0112) && image.GetPropertyItem(0x0112)?.Value is { Length: > 0 } turn)
            {
                image.RotateFlip(turn[0] switch
                {
                    3 => RotateFlipType.Rotate180FlipNone,
                    6 => RotateFlipType.Rotate90FlipNone,
                    8 => RotateFlipType.Rotate270FlipNone,
                    _ => RotateFlipType.RotateNoneFlipNone,
                });
            }
            float scale = Math.Min(1f, MaxSide / (float)Math.Max(image.Width, image.Height));
            var bmp = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, bmp.Width, bmp.Height);
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            // The kind of error, not its message: that carries the picture's path, with the person's own folder and file names.
            Rigsight.Core.Log.Write("widgets", $"A picture couldn't be read ({Path.GetExtension(path)}): {ex.GetType().Name}");
            return null;
        }
    }

    private static readonly Dictionary<string, (DateTime Read, bool File, bool Folder)> Seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a shortcut's path is a file or a folder now, looked at once a minute at most. A shortcuts tile is drawn
    /// at every reading when its widget shows one, on the agent's one window thread: a shortcut on a network share
    /// that's off or out of reach held that thread for seconds at each of those (the audit, 2026-11-11).
    /// </summary>
    public static (bool File, bool Folder) Look(string path)
    {
        lock (FolderLock)
        {
            if (Seen.TryGetValue(path, out var known) && DateTime.UtcNow - known.Read < TimeSpan.FromMinutes(1)) return (known.File, known.Folder);
            bool folder = Directory.Exists(path), file = !folder && File.Exists(path);
            Seen[path] = (DateTime.UtcNow, file, folder);
            return (file, folder);
        }
    }

    /// <summary>A shortcut to a folder (see <see cref="Look"/>).</summary>
    public static bool IsFolder(TileLink link) => !link.IsWeb && Look(link.Path).Folder;

    /// <summary>An app's or a file's own icon, for a shortcut; null for a folder, a web address, or one that can't be read.</summary>
    public static Bitmap? IconOf(TileLink link)
    {
        if (link.IsWeb || Look(link.Path) is not { File: true }) return null;
        return Icons.Get(link.Path, p =>
        {
            try { return Icon.ExtractAssociatedIcon(p)?.ToBitmap(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException) { return null; }
        });
    }

}

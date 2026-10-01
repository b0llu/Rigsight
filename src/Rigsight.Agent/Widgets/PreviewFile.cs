using System.Drawing;
using System.Drawing.Imaging;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// Writes the pictures of widgets and the overlay that the app shows. The app may be reading one at that very moment,
/// so the picture is drawn into memory first, then written with a couple of quick retries; if the file stays busy,
/// that one is skipped (the app keeps the picture it has, and the next render replaces it).
/// </summary>
internal static class PreviewFile
{
    private const int Tries = 4;

    public static bool Save(Image image, string path)
    {
        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        for (int i = 1; ; i++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                png.WriteTo(file);
                return true;
            }
            catch (IOException) when (i < Tries)
            {
                Thread.Sleep(40);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Rigsight.Agent.Ui;

/// <summary>
/// A test copy's own icon: a violet square with a white "T", so its tray icon is never taken for the installed RigSight's
/// logo (the user, 2026-11-14: "only the installed copy on my machine will show the original logo, all test copies should
/// show something else"). The app draws the same shape for its window (Rigsight's TestLogo).
/// </summary>
internal static class TestLogo
{
    public static readonly Color Violet = Color.FromArgb(124, 58, 237);

    /// <summary>The icon at <paramref name="size"/> pixels, square.</summary>
    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        float r = size * 0.22f;
        using (var path = new GraphicsPath())
        {
            path.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
            path.AddArc(size - 2 * r, 0, 2 * r, 2 * r, 270, 90);
            path.AddArc(size - 2 * r, size - 2 * r, 2 * r, 2 * r, 0, 90);
            path.AddArc(0, size - 2 * r, 2 * r, 2 * r, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Violet);
            g.FillPath(fill, path);
        }
        using var font = new Font("Segoe UI", size * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("T", font, white, new RectangleF(0, size * 0.02f, size, size), centre);
        return bmp;
    }

    /// <summary>The icon as an Icon (the caller disposes it and destroys its handle).</summary>
    public static Icon Icon(int size)
    {
        using var bmp = Draw(size);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}

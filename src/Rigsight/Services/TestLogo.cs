using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rigsight.Services;

/// <summary>
/// A test copy's own window and taskbar icon: a violet square with a white "T", the agent's tray icon's shape (the
/// agent's TestLogo), so it's never taken for the installed RigSight (the user, 2026-11-14: "only the installed copy on my
/// machine will show the original logo, all test copies should show something else").
/// </summary>
public static class TestLogo
{
    public static ImageSource Image(int size = 64)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double r = size * 0.22;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(124, 58, 237)), null, new Rect(0, 0, size, size), r, r);
            var text = new FormattedText("T", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size * 0.62, Brushes.White, 1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}

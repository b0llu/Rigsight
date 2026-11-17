using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The glyph inside an icon-only button sits in the button's middle, measured on the drawn pixels (the user,
/// 2026-11-16, of the red ✕: "these icons are not centered"). The size check in IconCentreTests can't see this: a
/// glyph's line box is taller than its shape, so even at matching sizes the font's spacing can put it off the middle.
/// </summary>
[Collection("UI")]
public sealed class IconInkCentreTests
{
    // The glyphs the app puts in its icon buttons most: ✕, bin, +, pencil, ⋯, check, chevrons, refresh, search, info.
    private static readonly string[] Glyphs = ["", "", "", "", "", "", "", "", "", "", ""];

    public static TheoryData<string, int, int, double> Cases()
    {
        var data = new TheoryData<string, int, int, double>();
        foreach (var style in new[] { "IconButton", "DangerIconButton" })
            foreach (var (box, glyph) in new[] { (28, 14), (26, 14), (30, 14), (22, 12), (40, 14), (28, 11) })
                foreach (var zoom in new[] { 1.0, 1.1, 1.25 })
                    data.Add(style, box, glyph, zoom);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_glyph_sits_in_the_middle_of_the_button(string style, int box, int glyphSize, double zoom)
    {
        var off = Ui.Run(() =>
        {
            var found = new List<string>();
            foreach (var g in Glyphs)
            {
                var (dx, dy) = Offset(style, box, glyphSize, g, zoom);
                // Half a pixel either way is as close as whole pixels allow (a shape of odd width, an edge's anti-aliasing).
                if (Math.Abs(dx) > 0.6 || Math.Abs(dy) > 0.6) found.Add($"U+{(int)g[0]:X4} off by ({dx:+0.0;-0.0;0}, {dy:+0.0;-0.0;0}) px");
            }
            return found;
        });
        Assert.True(off.Count == 0, $"{style} {box}x{box}, glyph {glyphSize}, zoom {zoom:P0}: " + string.Join("; ", off));
    }

    /// <summary>The glyph's drawn shape's middle, less the button's middle, in pixels at 100%.</summary>
    internal static (double Dx, double Dy) Offset(string style, int box, int glyphSize, string glyph, double zoom = 1)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource(style),
            Width = box, Height = box,
            Foreground = Brushes.White, BorderBrush = Brushes.Transparent, Background = Brushes.Transparent,
            Content = new TextBlock { Text = glyph, FontSize = glyphSize },
        };
        var inner = new Border { Child = button, Width = box, Height = box, LayoutTransform = new ScaleTransform(zoom, zoom) };
        int size = (int)Math.Ceiling(box * zoom);
        var host = new Border { Background = Brushes.Black, Child = inner, Width = size, Height = size, UseLayoutRounding = true };
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var px = new byte[size * size * 4];
        bmp.CopyPixels(px, size * 4, 0);
        double mid = box * zoom / 2;
        // The box round what the eye sees as the shape: pixels at least a quarter lit (faint anti-aliasing left out).
        int minX = size, minY = size, maxX = -1, maxY = -1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (px[(y * size + x) * 4 + 1] < 64) continue;
                (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
            }
        if (maxX < 0) return (double.NaN, double.NaN);
        return ((minX + maxX + 1) / 2.0 - mid, (minY + maxY + 1) / 2.0 - mid);
    }
}

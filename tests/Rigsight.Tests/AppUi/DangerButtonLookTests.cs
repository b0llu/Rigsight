using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// Every action that removes something is a red outlined button that fills red under the pointer (the user's rule,
/// 2026-11-10): readable in every theme, at rest and filled, and drawn so it can be looked at.
/// </summary>
[Collection("UI")]
public sealed class DangerButtonLookTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    [Fact]
    public void Remove_buttons_read_in_every_theme_at_rest_and_filled()
    {
        _ = host; // the real app, with its styles
        Directory.CreateDirectory(Shots);
        var problems = new List<string>();
        try
        {
            foreach (var theme in ThemeCatalog.All)
            {
                Ui.Run(() =>
                {
                    ThemeManager.Apply(theme.Key);
                    var res = Application.Current.Resources;
                    var hot = (Color)res["HotColor"];
                    var surface = (Color)res["SurfaceColor"];
                    var onHot = ((SolidColorBrush)res["OnHotBrush"]).Color;
                    double rest = Accents.Contrast(hot, surface), filled = Accents.Contrast(onHot, hot);
                    if (rest < 4.5) problems.Add($"{theme.Key}: red words on the card {rest:0.00}:1");
                    if (filled < 4.5) problems.Add($"{theme.Key}: words on the red fill {filled:0.00}:1");

                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
                    row.Children.Add(Make("DangerButton", "Remove", hover: false));
                    row.Children.Add(Make("DangerButton", "Remove", hover: true));
                    row.Children.Add(Make("DangerButton", WithBin("Clear all history…"), hover: false));
                    row.Children.Add(Make("DangerButton", WithBin("Clear all history…"), hover: true));
                    row.Children.Add(Make("DangerIconButton", "\uE74D", hover: false));
                    row.Children.Add(Make("DangerIconButton", "\uE74D", hover: true));
                    row.Children.Add(Make("DangerIconButton", "\uE711", hover: false));
                    var card = new Border { Child = row };
                    card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                    var window = new Window { Content = card, SizeToContent = SizeToContent.WidthAndHeight, Left = -3000, Top = -3000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                    window.Show();
                    window.UpdateLayout();
                    Ui.SavePng(card, Path.Combine(Shots, $"remove-buttons-{theme.Key}.png"));
                    window.Close();
                });
            }
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // The user, 2026-11-10, on a screen at 150%: a dark line showed just inside the filled edge, and the bin sat a pixel
    // right of the middle. Laid out and drawn at each screen scale, from a spot between pixels as in a real page: the
    // fill reaches the edge with nothing dark between, and the glyph's ink is centred within half a screen pixel.
    [Theory]
    [InlineData("\uE74D", 1.0)]
    [InlineData("\uE711", 1.0)]
    [InlineData("\uE74D", 2.0)]
    public void A_filled_icon_button_has_no_dark_ring_and_its_glyph_is_centred(string glyph, double scale)
    {
        _ = host;
        Directory.CreateDirectory(Shots);
        var (report, offX, offY, ring) = Ui.Run(() =>
        {
            ThemeManager.Apply(ThemeManager.Dark);
            var b = Make("DangerIconButton", glyph, hover: true);
            b.Margin = new Thickness(0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.VerticalAlignment = VerticalAlignment.Top;
            var card = new Border { Padding = new Thickness(6.4, 6.6, 6, 6), UseLayoutRounding = true, Width = 44, Height = 44 };
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            VisualTreeHelper.SetRootDpi(card, new DpiScale(scale, scale));
            card.Child = b;
            card.Measure(new Size(44, 44));
            card.Arrange(new Rect(0, 0, 44, 44));
            card.UpdateLayout();
            int w = (int)Math.Ceiling(44 * scale), h = w;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(card);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var f = File.Create(Path.Combine(Shots, $"remove-icon-{(int)glyph[0]:X}-at-{scale * 100:0}.png"))) enc.Save(f);
            var px = new byte[w * h * 4];
            bmp.CopyPixels(px, w * 4, 0);
            var hot = (Color)Application.Current.Resources["HotColor"];
            bool IsHot(int x, int y) { int i = (y * w + x) * 4; return Math.Abs(px[i + 2] - hot.R) < 20 && Math.Abs(px[i + 1] - hot.G) < 20 && Math.Abs(px[i] - hot.B) < 20; }
            int Lum(int x, int y) { int i = (y * w + x) * 4; return (px[i] + px[i + 1] + px[i + 2]) / 3; }
            // The button: the box of its fully red pixels.
            int l = w, t = h, r = -1, bo = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (IsHot(x, y)) (l, t, r, bo) = (Math.Min(l, x), Math.Min(t, y), Math.Max(r, x), Math.Max(bo, y));
            // Inside the edge, along the middle row and column, up to 3 pixels in: anything clearly darker than the red is a seam.
            int my = (t + bo) / 2, mx = (l + r) / 2;
            var seams = new List<string>();
            int hotLum = (hot.R + hot.G + hot.B) / 3;
            for (int k = 0; k <= 3; k++)
                foreach (var (x, y) in new[] { (l + k, my), (r - k, my), (mx, t + k), (mx, bo - k) })
                    if (Lum(x, y) < hotLum - 40) seams.Add($"{x},{y}={Lum(x, y)}");
            // The glyph: dark pixels well inside the button.
            int inset = (int)Math.Ceiling(4 * scale), gl = w, gt = h, gr = -1, gb = -1;
            for (int y = t + inset; y <= bo - inset; y++)
                for (int x = l + inset; x <= r - inset; x++)
                    if (Lum(x, y) < hotLum / 2) (gl, gt, gr, gb) = (Math.Min(gl, x), Math.Min(gt, y), Math.Max(gr, x), Math.Max(gb, y));
            double ox = (gl + gr) / 2.0 - (l + r) / 2.0, oy = (gt + gb) / 2.0 - (t + bo) / 2.0;
            return ($"at {scale * 100:0}%: button {l},{t}-{r},{bo}, glyph {gl},{gt}-{gr},{gb}; seams {string.Join(" ", seams)}", ox, oy, seams.Count > 0);
        });
        Assert.False(ring, "A dark line inside the filled edge. " + report);
        Assert.True(Math.Abs(offX) <= 0.5 && Math.Abs(offY) <= 0.5, $"The glyph is {offX:0.0}, {offY:0.0} screen px off the middle. " + report);
    }

    private static T? FindDown<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T t) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) if (FindDown<T>(VisualTreeHelper.GetChild(d, i)) is { } f) return f;
        return null;
    }

    private static object WithBin(string words)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = "\uE74D", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.StyleProperty, "Icon");
        icon.FontSize = 12;
        p.Children.Add(icon);
        p.Children.Add(new TextBlock { Text = words });
        return p;
    }

    /// <summary>A remove button; "hover" draws it as it is under the pointer (the fill and words its trigger sets).</summary>
    private static Button Make(string style, object content, bool hover)
    {
        var b = new Button { Content = content, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        b.Style = (Style)Application.Current.FindResource(style);
        if (hover)
        {
            b.SetResourceReference(Control.BackgroundProperty, "HotBrush");
            b.SetResourceReference(Control.ForegroundProperty, "OnHotBrush");
        }
        return b;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// Every icon in the middle of a button or a box can be in the middle (the user, 2026-11-11: "it feels the icon is not at
/// center ... check for all others in app"). An icon-font glyph's shape fills a square of its size, so drawn at an odd
/// size in an even box (or the other way round) it lands half a pixel off, whatever is done: its size and the box's
/// are both even or both odd. (A few glyphs lean inside their own square, the warning triangle say: no whole-pixel move
/// helps those, so they aren't checked.)
/// </summary>
[Collection("UI")]
public sealed class IconCentreTests(AppHost host) : IClassFixture<AppHost>
{
    public static TheoryData<string> Pages => [.. AppHost.Pages];

    [Theory]
    [MemberData(nameof(Pages))]
    public void Icons_in_buttons_and_boxes_are_centred(string page)
    {
        host.Show(page, 900);
        host.ScrollThrough();
        var off = Ui.Run(() => Offenders(host.Window));
        Assert.True(off.Count == 0, $"{page}: {off.Count} icons off the middle\n" + string.Join("\n", off));
    }

    internal static List<string> Offenders(Window window)
    {
        var icon = (FontFamily)Application.Current.FindResource("IconFont");
        var found = new List<string>();
        var seen = new HashSet<string>();
        foreach (var tb in Visuals.Descendants<TextBlock>(window))
        {
            if (!tb.IsVisible || string.IsNullOrEmpty(tb.Text) || tb.Text.Length > 2 || !Equals(tb.FontFamily, icon)) continue;
            // The box it's centred in: the nearest one with a set size, a few steps up, where the icon is centred.
            if (BoxOf(tb) is not { } box) continue;
            // The minimize glyph is a one-pixel line: it can't be in the middle of any box with an even height.
            if (tb.Text == "\uE921" || tb.FontSize != Math.Round(tb.FontSize)) continue;
            int size = (int)tb.FontSize, w = (int)Math.Round(box.ActualWidth), h = (int)Math.Round(box.ActualHeight);
            if (size % 2 == w % 2 && size % 2 == h % 2) continue;
            string where = Where(box);
            string line = $"U+{(int)tb.Text[0]:X4} size {tb.FontSize} in {box.GetType().Name} {w}x{h} ({where})";
            if (seen.Add(line)) found.Add(line);
        }
        return found;
    }

    private static FrameworkElement? BoxOf(TextBlock tb)
    {
        if (tb.HorizontalAlignment != HorizontalAlignment.Center && tb.VerticalAlignment != VerticalAlignment.Center
            && VisualTreeHelper.GetParent(tb) is not ContentPresenter) return null;
        DependencyObject d = tb;
        for (int i = 0; i < 4 && (d = VisualTreeHelper.GetParent(d)) is not null; i++)
        {
            if (d is FrameworkElement fe && (!double.IsNaN(fe.Width) || fe is Button or System.Windows.Controls.Primitives.ToggleButton)
                && !double.IsNaN(fe.Height) | fe is Button or System.Windows.Controls.Primitives.ToggleButton
                && fe.ActualWidth > 0 && fe.ActualWidth <= 48 && fe.ActualHeight <= 48)
                return fe;
        }
        return null;
    }

    private static string Where(FrameworkElement e)
    {
        for (DependencyObject? d = e; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement f && System.Windows.Automation.AutomationProperties.GetName(f) is { Length: > 0 } n) return n;
            if (d is FrameworkElement g && g.ToolTip is string t) return t;
        }
        return "?";
    }
}

using System.Windows;
using System.Windows.Controls;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// The alignment rule (WORDING.md), measured: beside an icon box, one line of text sits on the box's middle, and
/// several lines start level with its top while the box stays at the top.
/// </summary>
[Collection("UI")]
public sealed class IconRowTests
{
    private static (Rect Box, Rect Text) Laid(string text) => Ui.Run(() =>
    {
        var box = new Border { Style = (Style)Application.Current.FindResource("IconBox") };
        var said = new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
        var row = new HeaderedContentControl { Style = (Style)Application.Current.FindResource("IconRow"), Header = box, Content = said, Width = 240 };
        row.Measure(new Size(240, double.PositiveInfinity));
        row.Arrange(new Rect(row.DesiredSize));
        row.UpdateLayout();
        return (box.TransformToAncestor(row).TransformBounds(new Rect(box.RenderSize)), said.TransformToAncestor(row).TransformBounds(new Rect(said.RenderSize)));
    });

    [Fact]
    public void One_line_sits_on_the_middle_of_the_box()
    {
        var (box, text) = Laid("Fans as usual");
        Assert.Equal((30.0, 30.0), (box.Width, box.Height));
        Assert.Equal(0, box.Top);
        Assert.Equal(box.Top + box.Height / 2, text.Top + text.Height / 2, 0.5);
        Assert.Equal(box.Right + 13, text.Left, 0.5);
    }

    [Fact]
    public void The_pages_built_on_it_still_load()
    {
        Ui.Run(() =>
        {
            _ = new Rigsight.Views.HomeView();
            _ = new Rigsight.Views.ReportsView();
            _ = new Rigsight.Views.CrashesView();
        });
    }

    [Fact]
    public void Several_lines_start_level_with_the_top_of_the_box_and_the_box_stays_at_the_top()
    {
        var (box, text) = Laid("You gamed for 1h 48m (4 games). Longest session: Apex Legends, 47m starting 4:10 PM. A second line wraps under the first.");
        Assert.True(text.Height > box.Height, $"expected several lines taller than the box; the text was {text.Height} high");
        Assert.Equal(0, box.Top);
        Assert.Equal(box.Top, text.Top, 0.5);
    }
}

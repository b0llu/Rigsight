using System.Drawing;
using System.Drawing.Imaging;
using Rigsight.Agent.Ui;
using Rigsight.Core.Notices;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The notifications round's cards (2026-11-07): each new kind in its group's colour (problems red, worth knowing amber,
/// recaps blue) with its label, through Settings' preview words; drawn together into one picture to compare with the
/// approved mock-up LWycKU9ZC8jzeVH8bVjPWm.
/// </summary>
[Collection("Drawing")]
public sealed class NoticeRoundCardTests
{
    private static readonly string[] Previews =
        ["fan-stopped", "drive-bad", "hotter", "slowed", "fan-slower", "drive-sectors", "problems", "drive-full", "weekly", "memory", "startup"];

    [Fact]
    public void Each_new_kind_has_its_groups_colour_and_label()
    {
        Rigsight.Core.Units.Fahrenheit = false;
        var red = ToastRenderer.Accent(NoticeKind.Alert);
        var amber = ToastRenderer.Accent(NoticeKind.Crash);
        var blue = ToastRenderer.Accent(NoticeKind.Recap);
        foreach (var key in Previews)
        {
            var (kind, _, _) = NoticePreviews.Of(key)!.Value;
            var expected = NoticeLevels.Of(kind) switch { NoticeLevel.Problem => red, NoticeLevel.Worth => amber, _ => blue };
            Assert.Equal(expected, ToastRenderer.Accent(kind));
            Assert.NotEqual("UPDATE", ToastRenderer.KindLabel(kind));
        }
        Assert.Equal(NoticeLevel.Problem, NoticeLevels.Of(NoticeKind.FanStopped));
        Assert.Equal(NoticeLevel.Worth, NoticeLevels.Of(NoticeKind.Space));
        Assert.Equal(NoticeLevel.Recap, NoticeLevels.Of(NoticeKind.Memory));
        Assert.Equal(NoticeLevel.App, NoticeLevels.Of(NoticeKind.Update));
    }

    [Fact]
    public void Every_new_card_drawn_together()
    {
        Rigsight.Core.Units.Fahrenheit = false;
        var cards = Previews.Select(k => NoticePreviews.Of(k)!.Value)
            .Select(p => ToastRenderer.Render(new Notice(p.Kind, p.Text.Title, p.Text.Body), 1, false, light: false, out _)).ToList();
        try
        {
            int w = 404 * 2 + 60, h = 0, col = 0;
            var heights = new int[2];
            foreach (var c in cards) { heights[col] += c.Height + 20; col = heights[0] <= heights[1] ? 0 : 1; }
            h = heights.Max() + 20;
            using var all = new Bitmap(w, h);
            using (var g = Graphics.FromImage(all))
            {
                g.Clear(Color.FromArgb(11, 14, 20));
                var y = new[] { 20, 20 };
                col = 0;
                foreach (var c in cards)
                {
                    g.DrawImage(c, 20 + col * (404 + 20), y[col]);
                    y[col] += c.Height + 20;
                    col = y[0] <= y[1] ? 0 : 1;
                }
            }
            string dir = Path.Combine(TestEnvironment.DataDir, "screens");
            Directory.CreateDirectory(dir);
            all.Save(Path.Combine(dir, "notice-cards.png"), ImageFormat.Png);
            Assert.All(cards, c => Assert.Equal(404, c.Width));
        }
        finally
        {
            foreach (var c in cards) c.Dispose();
        }
    }
}

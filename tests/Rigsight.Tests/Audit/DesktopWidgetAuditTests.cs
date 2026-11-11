using System.Drawing;
using Rigsight.Agent.Widgets;

namespace Rigsight.Tests.Audit;

/// <summary>
/// The desktop widgets' audit (2026-11-11): each test holds a bug found in the agent's widget windows, as it was, so it
/// stays fixed.
/// </summary>
[Collection("Drawing")]
public class DesktopWidgetAuditTests
{
    private static MediaNow Playing(string title, Bitmap cover) => new(title, "Artist", true, cover, true, true, 0);

    private static bool Usable(Bitmap b)
    {
        try { return b.Width > 0; }
        catch (ArgumentException) { return false; } // what a disposed bitmap says
    }

    [Fact]
    public void A_cover_the_widgets_were_given_is_still_drawable_after_two_quick_track_changes()
    {
        // What's playing is read on Windows' own threads, and a track change often comes as two or three reads in a row
        // (the session, then its properties, then its picture). The widgets keep what they were last given until the
        // next tick and draw it again meanwhile (the pointer over them, a scroll, a setting).
        using var watcher = new MediaWatcher();
        var a = new Bitmap(8, 8);
        watcher.Set(Playing("A", a));
        var given = watcher.Take();
        Assert.Same(a, given!.Cover);

        var b = new Bitmap(8, 8);
        watcher.Set(Playing("B", b));
        watcher.Set(Playing("C", new Bitmap(8, 8)));

        Assert.True(Usable(a), "the cover in the widgets' data was let go while they could still draw it");
        // The next tick hands out C, and the widgets have it by the one after: A and B go then, not kept for ever.
        Assert.Equal("C", watcher.Take()!.Title);
        Assert.True(Usable(a));
        watcher.Take();
        Assert.False(Usable(a));
        Assert.False(Usable(b));
        Assert.True(Usable(watcher.Now!.Cover!));
    }
}

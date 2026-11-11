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

    [Fact]
    public void A_click_on_the_note_being_typed_in_keeps_its_box()
    {
        // Typed into a note, then clicked on it again (to place the caret, say) within 400 ms: the box was opened again
        // with the note as last saved, and the next save wrote that over what had been typed.
        var typing = new WidgetRenderer.TypingNow("w", 1, "Buy milk and eg", 15, Adding: false);
        var note = new WidgetRenderer.TileHit(1, WidgetRenderer.HitNote, 0, RectangleF.Empty);
        Assert.True(WidgetManager.StillTyping(typing, "w", note));
        // Another note, another widget, or the to-do line of the same tile: a box of its own.
        Assert.False(WidgetManager.StillTyping(typing, "w", note with { Tile = 2 }));
        Assert.False(WidgetManager.StillTyping(typing, "x", note));
        Assert.False(WidgetManager.StillTyping(typing, "w", note with { Action = WidgetRenderer.HitTodoAdd }));
        Assert.False(WidgetManager.StillTyping(null, "w", note));
        // The to-do line being typed in, clicked again: kept, with what's typed and not added yet.
        var adding = typing with { Adding = true, Text = "Call the" };
        Assert.True(WidgetManager.StillTyping(adding, "w", note with { Action = WidgetRenderer.HitTodoAdd }));
        // A to-do ticked while typing is a click of its own.
        Assert.False(WidgetManager.StillTyping(adding, "w", note with { Action = WidgetRenderer.HitTodo }));
    }

    [Fact]
    public void Clicks_go_through_the_typing_box_to_the_widget()
    {
        using var box = new TileTypingBox(new Rectangle(0, 0, 200, 40), "Note", multiline: true, (_, _) => { }, _ => { }, null);
        var cp = (System.Windows.Forms.CreateParams)typeof(System.Windows.Forms.Control).GetProperty("CreateParams", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(box)!;
        const int transparent = 0x20, layered = 0x80000;
        Assert.Equal(transparent | layered, cp.ExStyle & (transparent | layered));
    }
}

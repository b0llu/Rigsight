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

    [Fact]
    public void A_drag_cancelled_with_Esc_leaves_the_widget_where_it_was()
    {
        // Picked up at 100,100, dragged to 300,50, Esc: Windows puts it back at 100,100 just after it says the move has
        // ended (Wine's move loop, as Windows'). The widget had saved 300,50 by then, and jumped there at the next change
        // of a setting. The drop is now taken after Windows' last step: back where it began, it stays.
        Assert.Null(WidgetForm.PutBack(new Point(100, 100), new Point(300, 50), new Point(100, 100)));
        // Dropped against the screen's top (the badge's room above it), Windows brings it down: put back, as before.
        Assert.Equal(new Point(300, -11), WidgetForm.PutBack(new Point(100, 100), new Point(300, -11), new Point(300, 0)));
        // Dropped where Windows left it, or never moved: nothing to do.
        Assert.Null(WidgetForm.PutBack(new Point(100, 100), new Point(300, 50), new Point(300, 50)));
        Assert.Null(WidgetForm.PutBack(new Point(100, 100), null, new Point(100, 100)));
    }

    [Fact]
    public void A_shortcut_on_a_network_drive_opens_from_an_agent_run_as_administrator()
    {
        // Windows keeps the person's mapped network drives apart from programs run as administrator: to the agent, a
        // shortcut to Z:\Projects looked gone, and wasn't opened, though Explorer (which opens it) has Z:.
        char? free = null;
        for (char c = 'Z'; c >= 'G'; c--)
            if (new DriveInfo(c.ToString()).DriveType == DriveType.NoRootDirectory) { free = c; break; }
        Assert.NotNull(free);
        var onNetworkDrive = new Rigsight.Core.Settings.TileLink { Path = $@"{free}:\Projects\plan.xlsx", Name = "Plan" };
        Assert.True(WidgetManager.Opens(onNetworkDrive, elevated: true));
        // Without administrator rights the agent has the same drives as Explorer: one it can't see isn't there.
        Assert.False(WidgetManager.Opens(onNetworkDrive, elevated: false));
        // A file gone from a drive the agent does have: still not opened.
        var gone = new Rigsight.Core.Settings.TileLink { Path = Path.Combine(Path.GetTempPath(), "rigsight-gone-" + Guid.NewGuid().ToString("N"), "a.txt"), Name = "Gone" };
        Assert.False(WidgetManager.Opens(gone, elevated: true));
        Assert.True(WidgetManager.Opens(new Rigsight.Core.Settings.TileLink { Path = "https://rigsight.app/?a=1&b=2", Name = "Site" }, elevated: true));
    }

    [Fact]
    public void Widgets_brought_in_front_stay_there_while_a_note_on_them_is_typed_in()
    {
        // The shortcut brings the widgets in front of the windows until another window comes to the front. The box that
        // takes the keys for a note is a window of its own: it wasn't counted as one of the widgets', so the note went
        // back behind the windows a quarter of a second after it was clicked.
        IntPtr browser = 100, widget = 200, typingBox = 300, other = 400;
        IntPtr[] ours = [widget, typingBox];
        Assert.True(WidgetManager.StaysInFront(typingBox, browser, ours));
        Assert.True(WidgetManager.StaysInFront(widget, browser, ours));
        Assert.True(WidgetManager.StaysInFront(browser, browser, ours));
        Assert.False(WidgetManager.StaysInFront(other, browser, ours));
        Assert.False(WidgetManager.StaysInFront(IntPtr.Zero, browser, ours));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [Fact]
    public void A_shortcuts_icon_leaves_no_window_handle_behind()
    {
        // Each icon read for a shortcut made a Windows icon handle that was copied to a bitmap and left for the garbage
        // collector: one more of the agent's 10,000 for each icon read, until a collection came by.
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "icons-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var links = Enumerable.Range(0, 60).Select(i =>
            {
                var file = Path.Combine(dir, $"f{i}.txt");
                File.WriteAllText(file, "x");
                return new Rigsight.Core.Settings.TileLink { Path = file, Name = $"f{i}" };
            }).ToList();
            WidgetFiles.IconOf(links[0]); // the shell's own first-time handles, before counting
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var me = System.Diagnostics.Process.GetCurrentProcess().Handle;
            const uint user = 1;
            uint before = GetGuiResources(me, user);
            bool wasOn = GC.TryStartNoGCRegion(16 * 1024 * 1024);
            try
            {
                foreach (var l in links.Skip(1)) WidgetFiles.IconOf(l);
                Assert.True(GetGuiResources(me, user) - before < 20, $"{GetGuiResources(me, user) - before} icon handles left behind");
            }
            finally
            {
                if (wasOn && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_shortcuts_tile_doesnt_ask_the_disk_at_every_drawing()
    {
        // Drawn at every reading on the agent's one window thread, it asked whether each shortcut's file or folder was
        // there each time: a shortcut on a network share out of reach held the thread for seconds at every reading.
        // Asked once a minute now: a file gone a moment ago still has its icon from the last time it was looked at.
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "shortcuts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "notes.txt");
            File.WriteAllText(file, "x");
            var link = new Rigsight.Core.Settings.TileLink { Path = file, Name = "Notes" };
            Assert.NotNull(WidgetFiles.IconOf(link));
            var folder = new Rigsight.Core.Settings.TileLink { Path = dir, Name = "Folder" };
            Assert.True(WidgetFiles.IsFolder(folder));
            Assert.Null(WidgetFiles.IconOf(folder));

            File.Delete(file);
            Assert.NotNull(WidgetFiles.IconOf(link));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

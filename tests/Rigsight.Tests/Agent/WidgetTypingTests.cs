using System.Drawing;
using System.Drawing.Imaging;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Typing into a note or a to-do list on the desktop, and scrolling them (the user, 2026-11-11): the widget draws what's
/// typed itself, with a caret, in its own look; an item added with Enter shows above the line typed in, never under it;
/// a list or a note longer than its tile scrolls, and the wheel never runs past its ends; a to-do's text sits in the
/// middle of its row by its ink.
/// </summary>
[Collection("Drawing")]
public class WidgetTypingTests : IDisposable
{
    public void Dispose()
    {
        WidgetRenderer.Typing = null;
        WidgetRenderer.Scrolls.Clear();
    }

    private static WidgetConfig Box(params WidgetTile[] tiles) => new() { Style = WidgetStyle.Custom, Id = "custom-typing", Tiles = [.. tiles], BackgroundOpacity = 1 };

    private static WidgetTile Todo(int n) => new()
    {
        Kind = WidgetTiles.Todo, W = 6, H = 4,
        Todos = [.. Enumerable.Range(1, n).Select(i => new TodoItem { Text = $"Item {i}", Done = i == 1 })],
    };

    private static void Keep(Bitmap bmp, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "boxes");
        Directory.CreateDirectory(dir);
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
    }

    private static List<WidgetRenderer.TileHit> Render(WidgetConfig cfg, string? name = null)
    {
        using var bmp = WidgetRenderer.Render(cfg, new WidgetData(), 1, hover: false, out _);
        if (name is not null) Keep(bmp, name);
        return [.. WidgetRenderer.Hits!];
    }

    [Fact]
    public void A_long_to_do_list_scrolls_and_stops_at_its_ends()
    {
        var cfg = Box(Todo(12));
        var first = Render(cfg).Where(h => h.Action == WidgetRenderer.HitTodo).Select(h => h.Index).ToList();
        Assert.Equal(0, first[0]);
        Assert.DoesNotContain(Render(cfg), h => h.Action == WidgetRenderer.HitTodoAdd); // the line to add one is under the end
        Assert.Single(WidgetRenderer.ScrollAreas!);

        WidgetRenderer.Scrolls[(cfg.Id, 0)] = 3;
        Assert.Equal(3, Render(cfg).Where(h => h.Action == WidgetRenderer.HitTodo).Min(h => h.Index));
        // Far past the end: the end, with the line to add one.
        WidgetRenderer.Scrolls[(cfg.Id, 0)] = 99;
        var end = Render(cfg, "todo-scrolled");
        Assert.Contains(end, h => h.Action == WidgetRenderer.HitTodoAdd);
        Assert.Equal(11, end.Where(h => h.Action == WidgetRenderer.HitTodo).Max(h => h.Index));
        Assert.True(WidgetRenderer.Scrolls[(cfg.Id, 0)] < 99, "kept at the end, not past it");
    }

    [Fact]
    public void Typing_an_item_keeps_the_line_in_sight_below_the_items()
    {
        var cfg = Box(Todo(12));
        WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, "Back up the E drive", 19, Adding: true);
        var hits = Render(cfg, "todo-typing");
        var add = Assert.Single(hits, h => h.Action == WidgetRenderer.HitTodoAdd);
        Assert.All(hits.Where(h => h.Action == WidgetRenderer.HitTodo), h => Assert.True(h.Rect.Bottom <= add.Rect.Top + 0.5f));
        // The item added with Enter: it's above the line typed in.
        cfg.Tiles![0].Todos!.Add(new TodoItem { Text = "Back up the E drive" });
        WidgetRenderer.Typing = WidgetRenderer.Typing with { Text = "", Caret = 0 };
        hits = Render(cfg);
        var added = Assert.Single(hits, h => h.Action == WidgetRenderer.HitTodo && h.Index == 12);
        Assert.True(added.Rect.Bottom <= Assert.Single(hits, h => h.Action == WidgetRenderer.HitTodoAdd).Rect.Top + 0.5f);
    }

    [Fact]
    public void A_note_is_drawn_with_its_caret_while_it_is_typed_and_scrolls()
    {
        string text = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        var note = new WidgetTile { Kind = WidgetTiles.Notes, W = 6, H = 4, Text = text };
        var cfg = Box(note);
        using var plain = WidgetRenderer.Render(cfg, new WidgetData(), 1, hover: false, out _);
        // Typing at the end: the note scrolls to the caret, so its end shows.
        WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, text, text.Length, Adding: false);
        using var typing = WidgetRenderer.Render(cfg, new WidgetData(), 1, hover: false, out _);
        Keep(typing, "note-typing");
        Assert.True(WidgetRenderer.Scrolls[(cfg.Id, 0)] > 0);
        Assert.NotEqual(Bytes(plain), Bytes(typing));
        // An empty note being typed in: a caret, not "Click here to write."
        WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, "", 0, Adding: false);
        note.Text = null;
        using var empty = WidgetRenderer.Render(cfg, new WidgetData(), 1, hover: false, out _);
        Keep(empty, "note-empty-typing");
    }

    private static byte[] Bytes(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // The ink of a to-do's text, against its box: their middles within 1.5 px, measured on the picture.
    [Fact]
    public void A_to_dos_text_sits_in_the_middle_of_its_row()
    {
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Todo, W = 8, H = 3, Todos = [new() { Text = "Hxxx" }] });
        cfg.Theme = WidgetTheme.Dark;
        using var bmp = WidgetRenderer.Render(cfg, new WidgetData(), 3, hover: false, out _);
        var row = Assert.Single(WidgetRenderer.Hits!, h => h.Action == WidgetRenderer.HitTodo).Rect;
        // Columns of the box and of the text, at 3x: the box is the first ink from the left, the text after a gap.
        int top = (int)(row.Top * 3), bottom = (int)(row.Bottom * 3), left = (int)(row.Left * 3), right = (int)(row.Right * 3);
        bool Ink(int x, int y) => bmp.GetPixel(x, y).GetBrightness() > 0.45f;
        (int Top, int Bottom) Rows(int x0, int x1)
        {
            int t = -1, b = -1;
            for (int y = top; y < bottom; y++)
                for (int x = x0; x < x1; x++)
                    if (Ink(x, y)) { if (t < 0) t = y; b = y; break; }
            return (t, b);
        }
        int boxEnd = left;
        while (boxEnd < right && Enumerable.Range(top, bottom - top).Any(y => Ink(boxEnd, y))) boxEnd++;
        var box = Rows(left, boxEnd);
        int textStart = boxEnd + 3;
        while (textStart < right && !Enumerable.Range(top, bottom - top).Any(y => Ink(textStart, y))) textStart++;
        // The capital H only: its middle against the box's.
        var text = Rows(textStart, textStart + 3 * 9);
        float boxMid = (box.Top + box.Bottom) / 2f, textMid = (text.Top + text.Bottom) / 2f;
        Assert.True(Math.Abs(boxMid - textMid) <= 1.5f * 3, $"box middle {boxMid / 3:0.0}, text middle {textMid / 3:0.0} (px at size M)");
    }
}

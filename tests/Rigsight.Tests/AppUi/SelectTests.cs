using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.Controls;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// Text can be selected and copied (the person's call, 2026-10-24) through WPF's own text editor, switched on for the
/// text pressed. Fails the day a .NET update moves that editor: text would then stay unselectable (never a crash).
/// </summary>
[Collection("UI")]
public sealed class SelectTests
{
    [Fact]
    public void WPFs_text_editor_is_still_where_selection_needs_it() => Assert.True(Ui.Run(() => Select.Works));

    [Fact]
    public void Plain_text_selects_and_copies_and_lets_go_when_focus_leaves()
    {
        Ui.Run(() =>
        {
            var text = new TextBlock { Text = "WHEA_UNCORRECTABLE_ERROR 0x124" };
            var box = new TextBox();
            var window = Shown(new StackPanel { Children = { text, box } });
            // The person's own clipboard is put back after (the gate runs this on their PC).
            DataObject? before = null;
            if (Clipboard.GetDataObject() is { } data)
            {
                before = new DataObject();
                foreach (var f in data.GetFormats(false)) if (SafeGet(data, f) is { } v) before.SetData(f, v);
            }
            try
            {
                Assert.True(Select.Selectable(text));
                Assert.True(Select.Attach(text));
                Select.SelectAll(text);
                Assert.Equal("WHEA_UNCORRECTABLE_ERROR 0x124", Select.Selected(text));
                text.Focus();
                ApplicationCommands.Copy.Execute(null, text);
                Assert.Equal("WHEA_UNCORRECTABLE_ERROR 0x124", Clipboard.GetText());
                // The text changing under a selection (a live reading) is fine.
                text.Text = "62 °C";
                // Focus elsewhere: the selection goes and the text is plain again.
                box.Focus();
                Assert.Equal("", Select.Selected(text));
                Assert.False(text.Focusable);
            }
            finally
            {
                window.Close();
                try { if (before is not null) Clipboard.SetDataObject(before, true); else Clipboard.Clear(); }
                catch (System.Runtime.InteropServices.COMException) { }
            }
        });
    }

    [Fact]
    public void Text_pressed_for_something_else_stays_plain()
    {
        Ui.Run(() =>
        {
            var inButton = new TextBlock { Text = "Scan" };
            var inRow = new TextBlock { Text = "Discord" };
            var inDrag = new TextBlock { Text = "RigSight AI" };
            var takenOut = new TextBlock { Text = "Tile" };
            var outPanel = new Border { Child = takenOut };
            Select.SetText(outPanel, false);
            var window = Shown(new StackPanel
            {
                Children =
                {
                    new Button { Content = inButton },
                    new Border { Cursor = Cursors.Hand, Child = inRow },
                    new Border { Cursor = Cursors.SizeAll, Child = inDrag },
                    outPanel,
                },
            });
            try
            {
                Assert.All(new[] { inButton, inRow, inDrag, takenOut }, t => Assert.False(Select.Selectable(t), t.Text));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Nothing_is_selectable_outside_a_part_marked_for_it()
    {
        Ui.Run(() =>
        {
            var text = new TextBlock { Text = "CPU" };
            var window = new Window { Content = text, Width = 200, Height = 100, Left = -3000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try { Assert.False(Select.Selectable(text)); }
            finally { window.Close(); }
        });
    }

    private static object? SafeGet(IDataObject data, string format)
    {
        try { return data.GetData(format); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException or InvalidOperationException) { return null; }
    }

    private static Window Shown(UIElement content)
    {
        var window = new Window { Content = content, Width = 300, Height = 200, Left = -3000, ShowActivated = false, ShowInTaskbar = false };
        Select.SetText(window, true);
        window.Show();
        return window;
    }
}

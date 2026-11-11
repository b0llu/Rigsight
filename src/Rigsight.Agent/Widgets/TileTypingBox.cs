using System.Drawing;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// Takes the keyboard for a note or a to-do list's last line once it's clicked on the desktop (widgets part two,
/// approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U). It isn't seen: the widget draws what's typed itself, in its own font and
/// colours, with a caret (the user, 2026-11-11: a box over the widget didn't follow its look, and an item added with
/// Enter went behind it). A widget never takes the keyboard by itself, only this does, and only once it's clicked; Esc,
/// or a click anywhere else, gives the keyboard back. A note is saved as it's typed; a to-do list takes an item each
/// time Enter is pressed, and stays ready for the next one.
/// </summary>
internal sealed class TileTypingBox : Form
{
    private readonly TextBox _box;
    private readonly Action<string, int> _draft;
    private readonly Action<string>? _changed;
    private readonly Action<string>? _added;
    private readonly System.Windows.Forms.Timer _save = new() { Interval = 400 };

    /// <param name="at">The tile's place on the screen (where Windows puts a typing helper, for languages that use one).</param>
    /// <param name="draft">What's typed and where the caret is, as it changes: the widget draws it.</param>
    public TileTypingBox(Rectangle at, string text, bool multiline, Action<string, int> draft, Action<string>? changed, Action<string>? added)
    {
        _draft = draft;
        _changed = changed;
        _added = added;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "RigSight typing";
        KeyPreview = true;
        // Not seen, only the widget is: almost wholly see-through, and as small as the line typed in.
        Opacity = 0.01;
        _box = new TextBox
        {
            Multiline = multiline,
            AcceptsReturn = multiline,
            AcceptsTab = false,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            WordWrap = multiline,
            MaxLength = Rigsight.Core.Settings.WidgetTiles.MaxNoteLength,
        };
        Controls.Add(_box);
        Bounds = new Rectangle(at.X, at.Y, Math.Max(8, at.Width), Math.Max(8, Math.Min(at.Height, 40)));
        _save.Tick += (_, _) => { _save.Stop(); _changed?.Invoke(Current); };
        _box.TextChanged += (_, _) =>
        {
            Tell();
            if (_changed is not null) { _save.Stop(); _save.Start(); }
        };
        // The caret moved (arrows, Home, End, a click can't reach it): the widget draws it where it is now.
        _box.KeyUp += (_, _) => Tell();
    }

    /// <summary>
    /// Clicks go through it to the widget under it. It lies over the tile's first lines, and a click there went to the
    /// box nobody sees, which put its caret where its own font has that spot, not where the widget draws the text: the
    /// next keys went in somewhere else (the audit, 2026-11-11). It still takes the keys.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Rigsight.Agent.Native.Win32.WS_EX_LAYERED | Rigsight.Agent.Native.Win32.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    private string Current =>_box.Text.Replace("\r\n", "\n");

    /// <summary>The caret's place in the text as the widget has it (one character a line break, not two).</summary>
    private int Caret => _box.Text[..Math.Min(_box.SelectionStart, _box.Text.Length)].Replace("\r\n", "\n").Length;

    private void Tell() => _draft(Current, Caret);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _box.Focus();
        _box.SelectionStart = _box.TextLength;
        Tell();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            Close();
            return;
        }
        if (e.KeyCode == Keys.Enter && _added is not null)
        {
            e.SuppressKeyPress = true;
            string item = _box.Text.Trim();
            if (item.Length > 0) _added(item);
            _box.Clear();
            Tell();
            return;
        }
        base.OnKeyDown(e);
    }

    // A click elsewhere: done.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!IsDisposed) BeginInvoke(Close);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _save.Stop();
        _changed?.Invoke(Current);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _save.Dispose();
        base.Dispose(disposing);
    }
}

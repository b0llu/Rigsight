using System.Drawing;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// A box to type in, opened over a note or a to-do list's last line when it's clicked on the desktop (widgets part two,
/// approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U): a widget never takes the keyboard by itself, only this box does, and only
/// once it's clicked. Esc, or a click anywhere else, gives the keyboard back. A note is saved as it's typed; a to-do
/// list takes an item each time Enter is pressed, and the box stays for the next one.
/// </summary>
internal sealed class TileTypingBox : Form
{
    private readonly TextBox _box;
    private readonly Action<string>? _changed;
    private readonly Action<string>? _added;
    private readonly System.Windows.Forms.Timer _save = new() { Interval = 400 };

    public TileTypingBox(Rectangle at, string text, bool multiline, float px, Color back, Color fore, Action<string>? changed, Action<string>? added)
    {
        _changed = changed;
        _added = added;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = back;
        Text = "RigSight note";
        KeyPreview = true;
        Padding = new Padding(2);
        _box = new TextBox
        {
            Multiline = multiline,
            AcceptsReturn = multiline,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", Math.Max(8f, px), GraphicsUnit.Pixel),
            ForeColor = fore,
            BackColor = back,
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            WordWrap = multiline,
        };
        Controls.Add(_box);
        // A line to add is one line high, at least; a note fills the place it was clicked.
        Bounds = multiline ? at : new Rectangle(at.X, at.Y, at.Width, Math.Max(at.Height, (int)Math.Ceiling(px * 1.8f)));
        _save.Tick += (_, _) => { _save.Stop(); _changed?.Invoke(Current); };
        _box.TextChanged += (_, _) => { if (_changed is not null) { _save.Stop(); _save.Start(); } };
    }

    private string Current => _box.Text.Replace("\r\n", "\n");

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _box.Focus();
        _box.SelectionStart = _box.TextLength;
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
        if (_save.Enabled)
        {
            _save.Stop();
            _changed?.Invoke(Current);
        }
        else if (_changed is not null) _changed(Current);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _save.Dispose();
        base.Dispose(disposing);
    }
}

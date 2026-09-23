using System.Drawing;
using Rigsight.Agent.Native;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// A borderless, per-pixel-alpha window that shows one widget. It never takes focus (so it won't
/// steal input from a game), can be dragged anywhere, and becomes click-through when locked.
/// </summary>
internal sealed class WidgetForm : Form
{
    private const int WM_NCHITTEST = 0x84, WM_MOUSEMOVE = 0x200, WM_NCMOUSEMOVE = 0xA0, WM_LBUTTONUP = 0x202;
    private const int WM_RBUTTONUP = 0x205, WM_NCRBUTTONUP = 0xA5, WM_EXITSIZEMOVE = 0x232, WM_DPICHANGED = 0x2E0;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTTRANSPARENT = -1;

    private readonly WidgetManager _manager;
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 250 };
    private WidgetData? _data;
    private bool _hover;
    private RectangleF _closeRect;

    public WidgetForm(WidgetConfig config, WidgetManager manager)
    {
        Config = config;
        _manager = manager;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Rigsight widget";
        _hoverTimer.Tick += (_, _) =>
        {
            if (!Bounds.Contains(Cursor.Position))
            {
                _hover = false;
                _hoverTimer.Stop();
                Redraw();
            }
        };
    }

    public WidgetConfig Config { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            // Called from the base constructor, before Config is assigned.
            if (Config is { Locked: true }) cp.ExStyle |= Win32.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void Apply(WidgetConfig config)
    {
        Config = config;
        if (IsHandleCreated)
        {
            long ex = (long)Win32.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE);
            ex = config.Locked ? ex | Win32.WS_EX_TRANSPARENT : ex & ~Win32.WS_EX_TRANSPARENT;
            Win32.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, (IntPtr)ex);
        }
        Redraw();
    }

    public void UpdateData(WidgetData data)
    {
        _data = data;
        if (Visible) Redraw();
    }

    public void Redraw()
    {
        if (!IsHandleCreated) return;
        float scale = DeviceDpi / 96f * (float)Config.Scale;
        using var bmp = WidgetRenderer.Render(Config, _data, scale, _hover, out _closeRect);
        if (Size != bmp.Size) Size = bmp.Size;
        Win32.SetLayeredBitmap(Handle, bmp, Location, (byte)Math.Round(Config.Opacity * 255));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Redraw();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                if (Config.Locked) { m.Result = HTTRANSPARENT; return; }
                var pt = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                m.Result = _hover && _closeRect.Contains(pt) ? HTCLIENT : HTCAPTION;
                return;

            case WM_MOUSEMOVE:
            case WM_NCMOUSEMOVE:
                if (!_hover)
                {
                    _hover = true;
                    Redraw();
                }
                _hoverTimer.Start();
                break;

            case WM_LBUTTONUP:
                if (_closeRect.Contains(PointToClient(Cursor.Position)))
                {
                    _manager.Mutate(Config.Style, c => c.Enabled = false);
                    return;
                }
                break;

            case WM_RBUTTONUP:
            case WM_NCRBUTTONUP:
                _manager.ShowMenu(this, Cursor.Position);
                return;

            case WM_EXITSIZEMOVE:
                _manager.Mutate(Config.Style, c => { c.X = Left; c.Y = Top; });
                break;

            case WM_DPICHANGED:
                base.WndProc(ref m);
                Redraw();
                return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hoverTimer.Dispose();
        base.Dispose(disposing);
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Rigsight.Agent.Native;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// A borderless, per-pixel-alpha window that shows one widget. It never takes focus (so it won't
/// steal input from a game), can be dragged anywhere, and becomes click-through when locked.
/// </summary>
/// <remarks>
/// The window is a little bigger than the widget, above and to the right of it: room for the badge with the cross that
/// hides it, half outside the panel's corner, shown while the pointer is over the widget (approved mock-up
/// 8Bfo1Ce7KFJs4g1wxHQP1U; the user, 2026-11-10: "move it out like hover we show notification badge"). That room is
/// clear pixels, which pass every click through, so nothing moves when the badge appears. The saved spot is the panel's
/// own top left, as before.
/// </remarks>
internal sealed class WidgetForm : Form
{
    private const int WM_NCHITTEST = 0x84, WM_MOUSEMOVE = 0x200, WM_NCMOUSEMOVE = 0xA0, WM_LBUTTONUP = 0x202, WM_MOUSEWHEEL = 0x20A;
    private const int WM_RBUTTONUP = 0x205, WM_NCRBUTTONUP = 0xA5, WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232, WM_DPICHANGED = 0x2E0;
    private const int WM_MOVING = 0x216, WM_WINDOWPOSCHANGING = 0x46, WM_SETCURSOR = 0x20, WM_LBUTTONDOWN = 0x201, WM_NCLBUTTONDOWN = 0xA1;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Where a press on a clickable place began, while it may still be a click (null once it became a drag).</summary>
    private Point? _press;
    private const uint GW_HWNDPREV = 3;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTTRANSPARENT = -1;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    private readonly WidgetManager _manager;
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 250 };
    private WidgetData? _data;
    private bool _hover, _dragging, _drawnOnce;
    private List<WidgetRenderer.TileHit> _hits = [];
    private List<(int Tile, RectangleF Rect)> _scrollAreas = [];

    /// <summary>The note or to-do list under a point of the window that scrolls with the wheel, if any.</summary>
    private int? ScrollTileAt(Point client)
    {
        if (_hitScale <= 0) return null;
        var at = new PointF(client.X / _hitScale, (client.Y - Room) / _hitScale);
        foreach (var (tile, rect) in _scrollAreas) if (rect.Contains(at)) return tile;
        return null;
    }
    private float _hitScale = 1;

    /// <summary>The clickable place of a tile under a point of the window, if any.</summary>
    internal WidgetRenderer.TileHit? HitAt(Point client)
    {
        if (_hits.Count == 0 || _hitScale <= 0) return null;
        var at = new PointF(client.X / _hitScale, (client.Y - Room) / _hitScale);
        foreach (var h in _hits) if (h.Rect.Contains(at)) return h;
        return null;
    }

    /// <summary>A tile's clickable place on the screen.</summary>
    internal Rectangle ScreenRect(WidgetRenderer.TileHit hit) => Rectangle.Round(new RectangleF(
        Left + hit.Rect.X * _hitScale, Top + Room + hit.Rect.Y * _hitScale, hit.Rect.Width * _hitScale, hit.Rect.Height * _hitScale));

    internal float HitScale => _hitScale;
    private Point _dragFrom;

    /// <summary>Where the window was last dragged to (its top left), while a drag is on.</summary>
    private Point? _droppedAt;
    private RectangleF _closeRect;

    public WidgetForm(WidgetConfig config, WidgetManager manager)
    {
        Config = config;
        _manager = manager;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Rigsight widget";
        Room = RoomFor(DeviceDpi); // before the window exists, so a spot set now is the panel's
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

    /// <summary>The room above and to the right of the panel for the badge, in screen pixels.</summary>
    public int Room { get; private set; }

    private int RoomFor(int dpi) => (int)Math.Ceiling(11 * dpi / 96.0);

    /// <summary>The panel's top left on the screen (what's saved), whatever room the window keeps for the badge.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Point PanelLocation
    {
        get => new(Left, Top + Room);
        set => Location = new Point(value.X, value.Y - Room);
    }

    /// <summary>The panel on the screen, without the badge's room.</summary>
    public Rectangle PanelBounds => new(Left, Top + Room, Width - Room, Height - Room);

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
        // A spot set in the app (the widget dragged on the Widgets page's map, or placed from its editor): there on the
        // desktop too (the user, 2026-11-10: it went back where it was). Not while it's being dragged here, nor while it
        // waits in the corner for its screen to come back.
        if (IsHandleCreated && !_dragging && config.X is int x && config.Y is int y && new Point(x, y) != PanelLocation && !_manager.WaitsForScreen(config.Id))
        {
            Rigsight.Core.Log.Write("widgets", $"{WidgetCatalog.Title(config)} moved from {PanelLocation.X},{PanelLocation.Y} to {x},{y}, its spot as saved in the app");
            PanelLocation = new Point(x, y);
        }
        if (IsHandleCreated)
        {
            long ex = (long)Win32.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE);
            ex = config.Locked ? ex | Win32.WS_EX_TRANSPARENT : ex & ~Win32.WS_EX_TRANSPARENT;
            Win32.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, (IntPtr)ex);
        }
        Redraw();
        NoteTaskbar();
    }

    /// <summary>
    /// The widget is (partly) on a taskbar. The taskbar is itself an always-on-top window, so a widget on the desktop,
    /// behind every window, went under it (the user, 2026-11-11: "they no longer come over taskbar"): one placed on the
    /// taskbar sits over windows while it's there, and goes back behind them when moved off.
    /// </summary>
    public bool OnTaskbar { get; private set; }

    /// <summary>
    /// In front of windows: only while on a taskbar. Every widget sits on the desktop, behind windows (the user,
    /// 2026-11-11: Over windows removed); the shortcut brings them all in front for a moment.
    /// </summary>
    public bool SitsOver => OnTaskbar;

    /// <summary>Whether the widget is on its screen's taskbar now: restacked, and logged, when that changes.</summary>
    private void NoteTaskbar()
    {
        if (!IsHandleCreated) return;
        var screen = Screen.FromRectangle(PanelBounds);
        bool on = IsOnTaskbar(PanelBounds, screen.Bounds, screen.WorkingArea);
        if (on == OnTaskbar) return;
        OnTaskbar = on;
        Rigsight.Core.Log.Write("widgets", $"{WidgetCatalog.Title(Config)} " + (on ? "is on the taskbar: in front of it, over windows there" : "is off the taskbar: behind windows again"));
        Restack();
    }

    /// <summary>The panel overlaps a taskbar: the part of the screen its work area leaves out (none with a taskbar that hides itself).</summary>
    internal static bool IsOnTaskbar(Rectangle panel, Rectangle b, Rectangle w)
    {
        Rectangle[] bars =
        [
            Rectangle.FromLTRB(b.Left, w.Bottom, b.Right, b.Bottom), Rectangle.FromLTRB(b.Left, b.Top, b.Right, w.Top),
            Rectangle.FromLTRB(b.Left, b.Top, w.Left, b.Bottom), Rectangle.FromLTRB(w.Right, b.Top, b.Right, b.Bottom),
        ];
        return bars.Any(r => r.Width > 0 && r.Height > 0 && r.IntersectsWith(panel));
    }

    /// <summary>On a taskbar (or brought in front): in front of the always-on-top ones. Otherwise behind every window.</summary>
    public void Restack()
    {
        if (!IsHandleCreated) return;
        bool over = SitsOver || _manager.InFront;
        if (TopMost != over) TopMost = over;
        Win32.SetWindowPos(Handle, over ? Win32.HWND_TOPMOST : HWND_BOTTOM, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    // What was last drawn, for the widgets that are only their text (see WidgetRenderer.ChangeKey).
    private string? _drawn;

    public void UpdateData(WidgetData data)
    {
        _data = data;
        if (!Visible)
        {
            _drawn = null; // shown again, it's drawn whatever it says
            return;
        }
        if (SitsOver && UnderTaskbar()) KeepOnTop();
        // New readings that come to the same picture: nothing to draw. (A changed setting, the pointer over it and a
        // changed screen scale all go through Redraw itself.)
        if (WidgetRenderer.ChangeKey(Config, data) is { } key && key == _drawn) return;
        Redraw();
    }

    /// <summary>How solid the widget is: faded while the pointer is over a locked one, with that setting on.</summary>
    private byte _opacity = 255;

    private byte _fadeTo = 255, _fadeFrom = 255;
    private long _fadeStart;
    private System.Windows.Forms.Timer? _fadeTimer;

    /// <summary>The fade's length: quick, and still seen as a fade (the user, 2026-11-10: it lagged, then jumped).</summary>
    internal const int FadeMs = 120;

    /// <summary>
    /// Fades the widget out while the pointer is over it, and back when it leaves: over <see cref="FadeMs"/>, eased,
    /// only the window's opacity changing (the picture isn't drawn again).
    /// </summary>
    public void SetFaded(bool faded)
    {
        byte target = !faded ? (byte)255 : Config.Locked ? (byte)30 : (byte)128;
        if (target == _fadeTo) return;
        (_fadeFrom, _fadeTo, _fadeStart) = (_opacity, target, Environment.TickCount64);
        if (!IsHandleCreated || !Visible)
        {
            _opacity = target;
            return;
        }
        if (_fadeTimer is null)
        {
            _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _fadeTimer.Tick += (_, _) => FadeStep();
        }
        _fadeTimer.Start();
        FadeStep();
    }

    private void FadeStep()
    {
        double t = Math.Clamp((Environment.TickCount64 - _fadeStart) / (double)FadeMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3); // quick at first, settling at the end
        _opacity = (byte)Math.Round(_fadeFrom + (_fadeTo - _fadeFrom) * eased);
        if (IsHandleCreated) Win32.SetLayeredAlpha(Handle, _opacity);
        if (t >= 1) _fadeTimer?.Stop();
    }

    public void Redraw()
    {
        if (!IsHandleCreated) return;
        _drawn = WidgetRenderer.ChangeKey(Config, _data);
        float scale = DeviceDpi / 96f * (float)Config.Scale;
        var panelAt = PanelLocation;
        // Its size as last drawn; none before the first drawing (the window's size until then is Windows' own, not the
        // widget's: taking it for the widget's made a widget left partly off an edge move in at every start, 2026-11-11).
        var before = _drawnOnce ? PanelBounds : Rectangle.Empty;
        _drawnOnce = true;
        Room = RoomFor(DeviceDpi);
        using var panel = WidgetRenderer.Render(Config, _data, scale, hover: false, out _);
        // Where its tiles can be clicked (a to-do's box, a timer's Start), in the widget's own pixels.
        _hits = WidgetRenderer.Hits?.ToList() ?? [];
        _scrollAreas = WidgetRenderer.ScrollAreas?.ToList() ?? [];
        _hitScale = scale;
        using var bmp = new Bitmap(panel.Width + Room, panel.Height + Room, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.DrawImageUnscaled(panel, 0, Room);
            _closeRect = RectangleF.Empty;
            if (_hover && !Config.Locked)
            {
                _closeRect = BadgeRect(panel.Size, scale);
                DrawBadge(g, _closeRect, DeviceDpi / 96f);
            }
        }
        var kept = StaysOnScreen(before, new Rectangle(panelAt, panel.Size), Screen.FromRectangle(before.IsEmpty ? new Rectangle(panelAt, panel.Size) : before).WorkingArea);
        if (Size != bmp.Size) Size = bmp.Size;
        Location = new Point(kept.X, kept.Y - Room);
        Win32.SetLayeredBitmap(Handle, bmp, Location, _opacity); // opacity is in the bitmap (background and content apart)
        if (kept != panelAt)
        {
            Rigsight.Core.Log.Write("widgets", $"{WidgetCatalog.Title(Config)} grew from {before.Width}x{before.Height} to {panel.Width}x{panel.Height} at {panelAt.X},{panelAt.Y}: moved to {kept.X},{kept.Y} to stay on its screen");
            // After this pass over the widgets, as saving goes round to the settings again.
            BeginInvoke(() => _manager.Mutate(Config.Id, c => (c.X, c.Y) = (kept.X, kept.Y)));
        }
    }

    /// <summary>
    /// A widget that was wholly on its screen and grew past its edge (tiles added, a bigger size, a screen's scale picked
    /// up after it was measured: widgets were placed partly off the screen, 2026-11-10) is moved back in, as little as it
    /// takes. One the person left partly off an edge stays where it is.
    /// </summary>
    internal static Point StaysOnScreen(Rectangle before, Rectangle now, Rectangle area)
    {
        if (before.IsEmpty || before.Size == now.Size || !area.Contains(before) || area.Contains(now)) return now.Location;
        int x = Math.Max(area.Left, Math.Min(now.X, area.Right - now.Width));
        int y = Math.Max(area.Top, Math.Min(now.Y, area.Bottom - now.Height));
        return new Point(x, y);
    }

    /// <summary>
    /// Where the close badge goes, in the window: over the panel's top right, as a count sits on a bell (the user,
    /// 2026-11-10: it floated outside a slim bar). Its middle is on the panel's own curve there, not on the empty corner
    /// of a rounded panel's box, and a little inside it, so most of the badge lies over the widget.
    /// </summary>
    private RectangleF BadgeRect(Size panel, float scale) => BadgeAt(Config, panel, scale, DeviceDpi, Room);

    internal static RectangleF BadgeAt(WidgetConfig cfg, Size panel, float scale, int dpi, int room)
    {
        float d = 20 * dpi / 96f, inset = 3 * dpi / 96f;
        float radius = Math.Min(WidgetRenderer.CornerRadius(cfg, panel.Height / scale) * scale, Math.Min(panel.Width, panel.Height) / 2f);
        float onCurve = radius * (1 - MathF.Sqrt(0.5f)); // the curve's point at 45°, from the box's corner
        float cx = panel.Width - onCurve - inset, cy = room + onCurve + inset;
        return new RectangleF(cx - d / 2, cy - d / 2, d, d);
    }

    internal static void DrawBadge(Graphics g, RectangleF r, float dpi)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            g.FillEllipse(shadow, r.X, r.Y + 1.5f * dpi, r.Width, r.Height);
        using (var fill = new SolidBrush(Color.FromArgb(255, 43, 43, 43))) g.FillEllipse(fill, r);
        using (var ring = new Pen(Color.FromArgb(255, 90, 90, 90), 1 * dpi)) g.DrawEllipse(ring, RectangleF.Inflate(r, -0.5f * dpi, -0.5f * dpi));
        using var pen = new Pen(Color.FromArgb(240, 240, 240), 1.4f * dpi) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float inset = r.Width * 0.35f;
        g.DrawLine(pen, r.Left + inset, r.Top + inset, r.Right - inset, r.Bottom - inset);
        g.DrawLine(pen, r.Right - inset, r.Top + inset, r.Left + inset, r.Bottom - inset);
    }

    /// <summary>
    /// To the front of the always-on-top windows. The taskbar is one of them too, and Windows puts whichever was raised
    /// last in front: the taskbar comes back above a widget left on it when a fullscreen game closes or it's clicked.
    /// </summary>
    public void KeepOnTop()
    {
        if (SitsOver || _manager.InFront)
            Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// A taskbar is above this widget and over it. Only the taskbar: a menu or a flyout opened over the widget stays in front.
    /// </summary>
    private bool UnderTaskbar()
    {
        var bounds = Bounds;
        int steps = 0;
        for (var h = Win32.GetWindow(Handle, GW_HWNDPREV); h != IntPtr.Zero && steps++ < 256; h = Win32.GetWindow(h, GW_HWNDPREV))
        {
            if (!Win32.IsWindowVisible(h) || Win32.ClassName(h) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) continue;
            if (Win32.GetWindowRect(h, out var r) && bounds.IntersectsWith(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom))) return true;
        }
        return false;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && IsHandleCreated) Restack(); // back after stepping aside for a game: in its place again
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Redraw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    private const uint SWP_NOZORDER = 0x4;

    /// <summary>The pointer has gone further than Windows' own drag distance from where it was pressed.</summary>
    internal static bool Moved(Point from, Point to)
    {
        var d = SystemInformation.DragSize;
        return Math.Abs(to.X - from.X) > d.Width / 2 || Math.Abs(to.Y - from.Y) > d.Height / 2;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                if (Config.Locked) { m.Result = HTTRANSPARENT; return; }
                var pt = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                // The close badge and a tile's clickable places are clicked; anywhere else a press drags the widget.
                m.Result = _closeRect.Contains(pt) || HitAt(pt) is not null ? HTCLIENT : HTCAPTION;
                return;

            case WM_SETCURSOR when !Config.Locked && HitAt(PointToClient(Cursor.Position)) is not null:
                Cursor.Current = Cursors.Hand;
                m.Result = 1;
                return;

            case WM_MOUSEWHEEL when !Config.Locked:
                // Over a note or a to-do list that holds more than it shows: scrolled (a notch down shows what's under).
                long lp = (long)m.LParam;
                var wheelAt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (ScrollTileAt(wheelAt) is int scrolled)
                {
                    int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                    _manager.Scrolled(this, scrolled, -Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 120));
                    m.Result = IntPtr.Zero;
                    return;
                }
                break;

            case WM_LBUTTONDOWN:
                // A press on a clickable place (a note's text, a to-do, Start): a click if it ends there, a drag of the
                // whole widget once it moves (the user, 2026-11-11: a note couldn't be dragged by its text).
                _press = HitAt(PointToClient(Cursor.Position)) is not null ? Cursor.Position : null;
                break;

            case WM_MOUSEMOVE:
            case WM_NCMOUSEMOVE:
                if (!_hover)
                {
                    _hover = true;
                    Redraw();
                }
                _hoverTimer.Start();
                if (m.Msg == WM_MOUSEMOVE && _press is { } from && (MouseButtons & MouseButtons.Left) != 0 && Moved(from, Cursor.Position))
                {
                    _press = null;
                    // Handed to Windows' own move, as a press on the widget anywhere else is (snapping and all).
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
                    return;
                }
                break;

            case WM_LBUTTONUP:
                var up = PointToClient(Cursor.Position);
                if (_closeRect.Contains(up))
                {
                    _manager.Mutate(Config.Id, c => c.Enabled = false);
                    return;
                }
                bool clicked = _press is not null;
                _press = null;
                if (clicked && HitAt(up) is { } hit)
                {
                    _manager.TileClicked(this, hit);
                    return;
                }
                break;

            case WM_RBUTTONUP:
            case WM_NCRBUTTONUP:
                _manager.ShowMenu(this, Cursor.Position);
                return;

            case WM_ENTERSIZEMOVE:
                _dragging = true;
                _dragFrom = PanelLocation;
                _droppedAt = null;
                KeepOnTop(); // picked up: over everything while it's dragged, the taskbar included
                break;

            case WM_MOVING:
                // Pulled onto screen edges and other widgets as it's dragged (Alt: placed freely).
                var r = Marshal.PtrToStructure<Win32.RECT>(m.LParam);
                if (_manager.Snap(this, ref r)) Marshal.StructureToPtr(r, m.LParam, false);
                _droppedAt = new Point(r.Left, r.Top);
                break;

            case WM_WINDOWPOSCHANGING when !SitsOver && !_manager.InFront:
                // On the desktop: whatever happens, it stays behind every window.
                var pos = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                if ((pos.flags & SWP_NOZORDER) == 0)
                {
                    pos.hwndInsertAfter = HWND_BOTTOM;
                    Marshal.StructureToPtr(pos, m.LParam, false);
                }
                break;

            case WM_EXITSIZEMOVE:
                _dragging = false;
                // Windows keeps a dragged window's top on the screen when it's let go, and the window has room above the
                // panel for the close badge: a widget dropped against the top edge came down by that room, and one in
                // the corner moved too (the user, 2026-11-11). It stays exactly where it was let go.
                if (_droppedAt is { } dropped && dropped != Location)
                {
                    Rigsight.Core.Log.Write("widgets", $"{WidgetCatalog.Title(Config)} was moved by Windows from {dropped.X},{dropped.Y + Room} to {Left},{Top + Room} as it was let go: put back");
                    Win32.SetWindowPos(Handle, IntPtr.Zero, dropped.X, dropped.Y, 0, 0, Win32.SWP_NOSIZE | SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                }
                _droppedAt = null;
                KeepOnTop();
                _manager.Moved(Config.Id);
                var at = PanelLocation;
                if (at != _dragFrom) Rigsight.Core.Log.Write("widgets", $"{WidgetCatalog.Title(Config)} dragged from {_dragFrom.X},{_dragFrom.Y} to {at.X},{at.Y}");
                _manager.Mutate(Config.Id, c => { c.X = at.X; c.Y = at.Y; });
                NoteTaskbar();
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
        if (disposing)
        {
            _hoverTimer.Dispose();
            _fadeTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}

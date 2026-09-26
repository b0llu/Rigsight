using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Rigsight.Agent.Native;
using Rigsight.Agent.Widgets;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Ui;

internal enum NoticeKind { Alert, Recap, Session, Crash, Overlay, Update }

/// <summary>Something worth telling the user. <see cref="Urgent"/> notices are shown even during fullscreen games.</summary>
internal sealed record Notice(NoticeKind Kind, string Title, string Body, string? IconPath = null,
    string? Page = null, string? Arg = null, bool Urgent = false);

/// <summary>
/// Decides when and how notices appear: Rigsight cards or Windows notifications, held back while
/// a fullscreen app is in front (so nothing pops up mid-game), stacked in the bottom-right corner.
/// UI thread only.
/// </summary>
internal sealed class NotificationCenter(Func<RigsightSettings> settings, TrayController tray, Action<string?, string?> openApp,
    Func<Notice, InGame> showInGame)
{
    private readonly Queue<Notice> _held = new();
    private readonly List<ToastForm> _open = [];
    private bool _fullscreen;

    public void Show(Notice notice, bool bypassQuiet = false)
    {
        var s = settings().Alerts;
        if (!bypassQuiet && _fullscreen)
        {
            if (!notice.Urgent && s.QuietDuringFullscreen)
            {
                _held.Enqueue(notice);
                return;
            }
            // A game in exclusive fullscreen hides every window, cards and Windows notifications alike: the notice
            // goes into the game through RivaTuner, or, where RivaTuner isn't drawing, waits until the game is left
            // instead of vanishing unseen. Borderless and windowed games get the card as usual.
            switch (showInGame(notice))
            {
                case InGame.Shown:
                    return;
                case InGame.Unreachable:
                    _held.Enqueue(notice);
                    return;
            }
        }
        Display(notice);
    }

    /// <summary>Called on every update; releases held notices once the fullscreen app is gone.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        if (!fullscreen)
            while (_held.TryDequeue(out var n)) Display(n);
    }

    private void Display(Notice n)
    {
        var s = settings().Alerts;
        if (s.Style == NotificationStyle.Windows)
        {
            tray.ShowNotification(n.Title, n.Body, n.Kind is NoticeKind.Alert, () => openApp(n.Page, n.Arg));
            return;
        }

        // Keep at most three cards; the oldest makes room.
        if (_open.Count >= 3) _open[0].Dismiss();

        ToastForm? toast = null;
        toast = new ToastForm(n, s.CardSeconds, () => openApp(n.Page, n.Arg), () =>
        {
            _open.Remove(toast!);
            Relayout();
        });
        _open.Add(toast);
        toast.ShowToast();
        Relayout();
    }

    private void Relayout()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        int margin = (int)(16 * (_open.FirstOrDefault()?.DeviceDpi ?? 96) / 96.0);
        int y = area.Bottom - margin;
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            var t = _open[i];
            y -= t.Height;
            t.MoveTo(area.Right - t.Width - margin, y);
            y -= margin / 2;
        }
    }

    public void CloseAll()
    {
        foreach (var t in _open.ToList()) t.Dismiss();
        _held.Clear();
    }
}

/// <summary>A notification card: per-pixel-alpha window that fades in, never takes focus, pauses while hovered.</summary>
internal sealed class ToastForm : Form
{
    private const int WM_NCHITTEST = 0x84, WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
    private const int HTCLIENT = 1;

    private readonly Notice _notice;
    private readonly Action _onClick;
    private readonly Action _onClosed;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly long _lifetimeMs;
    private long _shownAt, _hoverStartedAt, _pausedMs;
    private bool _hover, _closing, _closed;
    private double _alpha;
    private RectangleF _closeRect;

    public ToastForm(Notice notice, int seconds, Action onClick, Action onClosed)
    {
        _notice = notice;
        _onClick = onClick;
        _onClosed = onClosed;
        _lifetimeMs = Math.Clamp(seconds, 3, 60) * 1000L;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-10000, -10000);
        _timer.Tick += (_, _) => Animate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowToast()
    {
        Show();
        Redraw();
        _shownAt = Environment.TickCount64;
        _timer.Start();
    }

    public void MoveTo(int x, int y)
    {
        Location = new Point(x, y);
        Redraw();
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Start();
    }

    private void Animate()
    {
        long now = Environment.TickCount64;
        if (_closing)
        {
            _alpha -= 0.12;
            if (_alpha <= 0)
            {
                Finish();
                return;
            }
        }
        else
        {
            if (_alpha < 1) _alpha = Math.Min(1, _alpha + 0.12);
            long visible = now - _shownAt - _pausedMs - (_hover ? now - _hoverStartedAt : 0);
            if (visible >= _lifetimeMs) _closing = true;
            if (_hover && !Bounds.Contains(Cursor.Position))
            {
                _pausedMs += now - _hoverStartedAt;
                _hover = false;
                Redraw();
            }
            if (_alpha >= 1 && !_hover && visible < _lifetimeMs - 50)
                _timer.Interval = 100; // idle: tick slowly until it's time to fade out
        }
        if (_closing) _timer.Interval = 16;
        Redraw();
    }

    private void Finish()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        Close();
        _onClosed();
    }

    private void Redraw()
    {
        if (!IsHandleCreated || _closed) return;
        float scale = DeviceDpi / 96f;
        using var bmp = ToastRenderer.Render(_notice, scale, _hover, out _closeRect);
        if (Size != bmp.Size) Size = bmp.Size;
        LayeredWindow.Update(this, bmp, (byte)Math.Round(Math.Clamp(_alpha, 0, 1) * 255));
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                m.Result = HTCLIENT;
                return;
            case WM_MOUSEMOVE:
                if (!_hover)
                {
                    _hover = true;
                    _hoverStartedAt = Environment.TickCount64;
                    _timer.Interval = 16;
                    Redraw();
                }
                break;
            case WM_LBUTTONUP:
                if (!_closeRect.Contains(PointToClient(Cursor.Position))) _onClick();
                Dismiss();
                return;
            case WM_RBUTTONUP:
                Dismiss();
                return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Draws a notification card.</summary>
internal static class ToastRenderer
{
    private const float Width = 404, Pad = 17, IconSize = 42;

    // Also used for the same notice drawn inside a game by RivaTuner (WidgetRenderer.RtssNoticeText).
    internal static readonly Color Bg = Color.FromArgb(250, 22, 27, 40);
    private static readonly Color Border = Color.FromArgb(45, 54, 76);
    internal static readonly Color Text = Color.FromArgb(232, 236, 244);
    internal static readonly Color Muted = Color.FromArgb(160, 168, 186);
    private static Bitmap? _logo;
    internal static readonly Widgets.RecentIcons Icons = new();

    public static Color Accent(NoticeKind kind) => kind switch
    {
        NoticeKind.Alert => Color.FromArgb(248, 113, 113),
        NoticeKind.Recap => Color.FromArgb(91, 140, 255),
        NoticeKind.Session => Color.FromArgb(61, 220, 151),
        NoticeKind.Crash => Color.FromArgb(251, 191, 36),
        _ => Color.FromArgb(177, 140, 255),
    };

    public static string KindLabel(NoticeKind kind) => kind switch
    {
        NoticeKind.Alert => "TEMPERATURE ALERT",
        NoticeKind.Recap => "DAILY RECAP",
        NoticeKind.Session => "SESSION SUMMARY",
        NoticeKind.Crash => "WHAT HAPPENED",
        NoticeKind.Overlay => "OVERLAY",
        _ => "UPDATE",
    };

    public static Bitmap Render(Notice n, float scale, bool hover, out RectangleF closeRect)
    {
        using var titleFont = new Font("Segoe UI Semibold", 16f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var capFont = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        float textX = Pad + 4 + IconSize + 12, textW = Width - textX - Pad - 18;

        SizeF bodySize;
        using (var tmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(tmp))
            bodySize = mg.MeasureString(n.Body, bodyFont, (int)textW, StringFormat.GenericTypographic);
        float height = Math.Max(Pad + 41 + bodySize.Height + Pad, Pad * 2 + IconSize + 6);

        var bmp = new Bitmap((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(height * scale), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);
        g.ScaleTransform(scale, scale);

        var accent = Accent(n.Kind);
        var card = new RectangleF(0.5f, 0.5f, Width - 1, height - 1);
        using (var path = RoundRect(card, 14))
        {
            using var bg = new SolidBrush(Bg);
            using var border = new Pen(Border, 1);
            g.FillPath(bg, path);
            g.SetClip(path);
            using (var bar = new SolidBrush(accent)) g.FillRectangle(bar, 0, 0, 4, height);
            g.ResetClip();
            g.DrawPath(border, path);
        }

        var icon = IconFor(n.IconPath);
        var iconRect = new RectangleF(Pad + 4, Pad + 2, IconSize, IconSize);
        if (icon is not null) g.DrawImage(icon, iconRect);

        using var capBrush = new SolidBrush(accent);
        using var titleBrush = new SolidBrush(Text);
        using var bodyBrush = new SolidBrush(Muted);
        g.DrawString(KindLabel(n.Kind), capFont, capBrush, textX, Pad - 2, StringFormat.GenericTypographic);

        using var trim = (StringFormat)StringFormat.GenericTypographic.Clone();
        trim.Trimming = StringTrimming.EllipsisCharacter;
        trim.FormatFlags |= StringFormatFlags.NoWrap;
        g.DrawString(n.Title, titleFont, titleBrush, new RectangleF(textX, Pad + 14, textW, 24), trim);
        g.DrawString(n.Body, bodyFont, bodyBrush, new RectangleF(textX, Pad + 40, textW, bodySize.Height + 4), StringFormat.GenericTypographic);

        var close = new RectangleF(Width - 30, 10, 20, 20);
        if (hover)
        {
            using var cb = new SolidBrush(Color.FromArgb(40, 48, 68));
            g.FillEllipse(cb, close);
            using var cp = new Pen(Text, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float i = 6.5f;
            g.DrawLine(cp, close.Left + i, close.Top + i, close.Right - i, close.Bottom - i);
            g.DrawLine(cp, close.Right - i, close.Top + i, close.Left + i, close.Bottom - i);
        }
        closeRect = new RectangleF(close.X * scale, close.Y * scale, close.Width * scale, close.Height * scale);
        return bmp;
    }

    private static Bitmap? IconFor(string? path)
    {
        if (path is not null)
        {
            var cached = Icons.Get(path, p =>
            {
                try
                {
                    using var icon = Icon.ExtractIcon(p, 0, 64);
                    return icon?.ToBitmap();
                }
                catch { return null; }
            });
            if (cached is not null) return cached;
        }
        if (_logo is null)
        {
            try
            {
                using var icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Rigsight.ico"), 64, 64);
                _logo = icon.ToBitmap();
            }
            catch { }
        }
        return _logo;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Pushes a bitmap with per-pixel alpha to a layered window.</summary>
internal static class LayeredWindow
{
    public static void Update(Form form, Bitmap bitmap, byte opacity)
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Win32.SelectObject(memDc, hBitmap);
        try
        {
            var size = new Win32.SIZE(bitmap.Width, bitmap.Height);
            var source = new Win32.POINT(0, 0);
            var topLeft = new Win32.POINT(form.Left, form.Top);
            var blend = new Win32.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
            Win32.UpdateLayeredWindow(form.Handle, screenDc, ref topLeft, ref size, memDc, ref source, 0, ref blend, Win32.ULW_ALPHA);
        }
        finally
        {
            Win32.SelectObject(memDc, old);
            Win32.DeleteObject(hBitmap);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}

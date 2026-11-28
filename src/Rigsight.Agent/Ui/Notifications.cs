using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.RegularExpressions;
using Rigsight.Agent.Native;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Ui;

internal enum NoticeKind
{
    Alert, Recap, Session, Crash, Overlay, Update, Rewind, Internet, InternetBack,
    // The notifications round (2026-11-07): problems in red, worth knowing in amber, recaps in blue.
    FanStopped, DriveFailing, Fans, Drive, Usual, Slowed, Change, Space, Memory, Startup, Weekly,
    // A widget's timer that has run out (widgets part two, 2026-11-11).
    Timer,
    // RivaTuner off, so no FPS is recorded (2026-11-14).
    Fps,
    // A wireless device's battery running low (2026-11-20).
    DeviceBattery,
    // What the tray's "Something just went wrong" found in the 2 minutes before (issue #15, 2026-11-22). RigSight's own
    // group, in the default violet: the person asked for it, so no limit or quiet holds it back.
    LookBack,
}

/// <summary>Something worth telling the user. <see cref="Urgent"/> notices are shown even during fullscreen games.</summary>
/// <summary>
/// One notification for an app that keeps crashing, not one each time: the first crash is always said; another of the
/// same app within <see cref="Quiet"/> of the one last said is not (a game that dies at every start would otherwise
/// fill the screen). A crash that isn't said doesn't lengthen the quiet (the user's ruling: two minutes, no restart),
/// so an app that goes down again later the same evening is said again. Every crash is still stored and on the
/// Crashes page.
/// </summary>
internal sealed class CrashNoticeGate
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    /// <summary>Per app, the crash last said.</summary>
    private readonly Dictionary<string, DateTime> _said = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Whether a crash of <paramref name="exe"/> at <paramref name="at"/> is to be said.</summary>
    public bool Allow(string exe, DateTime at)
    {
        lock (_gate)
        {
            // Within the quiet of the one last said, the same crash seen again included (two sessions of one app
            // ending around it both find it in Windows' log).
            if (_said.TryGetValue(exe, out var said) && at >= said && at - said < Quiet) return false;
            _said[exe] = at;
            return true;
        }
    }
}

/// <summary>What the notification for an app that crashed or froze says.</summary>
internal static class CrashNotices
{
    /// <summary>How long after the first look the crash is looked at again, when the first found too little to say.</summary>
    public static readonly TimeSpan SecondLook = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The example Settings shows: a made-up crash put through the real rules and the real wording, so the preview is
    /// always what a notification of that crash would say ("Inside the NVIDIA driver, 48 minutes into the game.", under
    /// the title "Dota 2 crashed").
    /// </summary>
    public static string Sample
    {
        get
        {
            var crash = new Rigsight.Core.Stability.CrashEvent { Kind = Rigsight.Core.Stability.CrashKind.AppCrash, AppExe = "dota2.exe", Module = "nvwgf2umx.dll", Code = "c0000005" };
            var input = new Rigsight.Core.Stability.CrashInput(crash, Rigsight.Core.Stability.CrashExplainer.Explain(crash, "Dota 2"), new Rigsight.Core.Settings.AlertSettings()) { AppName = "Dota 2", IsGame = true };
            return Body(Rigsight.Core.Stability.CrashFindings.Judge(input), game: true, activeSec: 48 * 60);
        }
    }

    /// <summary>
    /// Under the title, which says what happened ("Dota 2 crashed"): its cause as its own case says it, with how sure
    /// that is ("Likely as it was starting"), then how long it had been in use. Without a finding (the crash couldn't
    /// be stored), or with nothing on record to say why, no cause: a notification never states one nothing was read for.
    /// </summary>
    public static string Body(Rigsight.Core.Stability.CrashFinding? finding, bool game, double activeSec) =>
        Rigsight.Core.NoticeWords.AppDown(finding, game, activeSec);
}

internal sealed record Notice(NoticeKind Kind, string Title, string Body, string? IconPath = null,
    string? Page = null, string? Arg = null, bool Urgent = false)
{
    /// <summary>What it's about, so it isn't said twice (see Core's NoticeChecks); null for the ones that can't repeat.</summary>
    public string? Key { get; init; }
    /// <summary>Kept with the key (a drive's bad sector count).</summary>
    public string? Value { get; init; }
    /// <summary>Other keys this one says too (a game's summary that took in "hotter than usual").</summary>
    public IReadOnlyList<string> AlsoSays { get; init; } = [];

    /// <summary>The one figure the card draws against its usual (1.23.0), from Core with the words; null for most.</summary>
    public Core.Notices.NoticeFigure? Figure { get; init; }

    /// <summary>When it may interrupt (see <see cref="NoticeLevels.Of"/>).</summary>
    public Core.Notices.NoticeLevel Level => NoticeLevels.Of(Kind);
}

/// <summary>Each kind's group: problems at once, worth knowing after a game, recaps, and RigSight's own.</summary>
internal static class NoticeLevels
{
    public static Core.Notices.NoticeLevel Of(NoticeKind kind) => kind switch
    {
        NoticeKind.Alert or NoticeKind.Crash or NoticeKind.Internet or NoticeKind.InternetBack or NoticeKind.FanStopped or NoticeKind.DriveFailing or NoticeKind.DeviceBattery => Core.Notices.NoticeLevel.Problem,
        NoticeKind.Fans or NoticeKind.Drive or NoticeKind.Usual or NoticeKind.Slowed or NoticeKind.Change or NoticeKind.Space or NoticeKind.Fps => Core.Notices.NoticeLevel.Worth,
        NoticeKind.Recap or NoticeKind.Session or NoticeKind.Rewind or NoticeKind.Memory or NoticeKind.Startup or NoticeKind.Weekly => Core.Notices.NoticeLevel.Recap,
        _ => Core.Notices.NoticeLevel.App,
    };

    /// <summary>A check's candidate as a notice: its kind from what it's about and its group.</summary>
    public static NoticeKind KindOf(Core.Notices.NoticeCandidate c) => (c.Kind, c.Level) switch
    {
        ("fan", Core.Notices.NoticeLevel.Problem) => NoticeKind.FanStopped,
        ("fan", _) => NoticeKind.Fans,
        ("drive", Core.Notices.NoticeLevel.Problem) => NoticeKind.DriveFailing,
        ("drive", _) => NoticeKind.Drive,
        ("hotter" or "problems" or "wear", _) => NoticeKind.Usual,
        ("slowed", _) => NoticeKind.Slowed,
        ("change", _) => NoticeKind.Change,
        ("storage", _) => NoticeKind.Space,
        ("memory", _) => NoticeKind.Memory,
        ("startup", _) => NoticeKind.Startup,
        ("device", _) => NoticeKind.DeviceBattery,
        _ => NoticeKind.Weekly,
    };
}

/// <summary>
/// Decides when and how notices appear: Rigsight cards or Windows notifications, held back while
/// a fullscreen app is in front (so nothing pops up mid-game), stacked in the bottom-right corner.
/// UI thread only.
/// </summary>
internal sealed class NotificationCenter(Func<RigsightSettings> settings, TrayController tray, Action<string?, string?> openApp,
    Func<Notice, InGame> showInGame)
{
    /// <summary>At most this many cards on screen; the oldest makes room.</summary>
    public const int MaxCards = 3;

    private readonly Queue<Notice> _held = new();
    /// <summary>The cards on screen, oldest first; one that is fading out has already left this list.</summary>
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

        // Keep at most three cards; the oldest makes room (it leaves the list as it starts to fade).
        while (_open.Count >= MaxCards)
        {
            var oldest = _open[0];
            oldest.Dismiss();
            _open.Remove(oldest);
        }

        ToastForm? toast = null;
        toast = new ToastForm(n, s.CardSeconds, () => openApp(n.Page, n.Arg),
            onLeaving: () => _open.Remove(toast!),
            // Once it has faded, the cards above it glide down into its place.
            onClosed: Relayout);
        toast.Prepare();
        _open.Add(toast);
        Log.Write("notice", $"Card {n.Kind}{(n.Figure is null ? "" : " with its figure")}: {(toast.Moves ? "slides in" : "appears at once, Windows' animations are off")}, {_open.Count} on screen");
        Relayout();
    }

    /// <summary>
    /// Stacks the cards from the bottom-right corner up, newest at the bottom: a new one slides in from the edge, the
    /// others glide to their new places (both at once with Windows' animations off).
    /// </summary>
    private void Relayout()
    {
        if (_open.Count == 0) return;
        var area = Screen.PrimaryScreen!.WorkingArea;
        float dpi = _open[0].DeviceDpi / 96f;
        int margin = (int)Math.Round(16 * dpi), gap = (int)Math.Round(10 * dpi);
        int y = area.Bottom - margin;
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            var t = _open[i];
            y -= t.CardSize.Height;
            t.Place(area.Right - margin - t.CardSize.Width, y, area.Right);
            y -= gap;
        }
    }

    public void CloseAll()
    {
        foreach (var t in _open.ToList()) t.Dismiss();
        _held.Clear();
    }
}

/// <summary>
/// The cards' motion (1.23.0, mock-up section 9): in from the screen's right edge and to a new place in the stack in 250 ms,
/// easing out as CSS's cubic-bezier(0, 0, 0, 1) does; out with a 150 ms fade. Windows' animations off: none of it.
/// </summary>
internal static class ToastMotion
{
    public const double SlideMs = 250, FadeOutMs = 150;

    /// <summary>
    /// cubic-bezier(0, 0, 0, 1) at <paramref name="t"/> (0 to 1): with both handles on the left, its x is s³ and its y
    /// 3s² − 2s³, so s is the cube root of t. Quick at first, settling gently.
    /// </summary>
    public static double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double s = Math.Cbrt(t);
        return s * s * (3 - 2 * s);
    }
}

/// <summary>
/// A notification card: a per-pixel-alpha window that slides in, never takes focus, and pauses while hovered. Its picture
/// is drawn once (again only when the hover changes) and kept in a memory DC, so a slide or a glide only moves it.
/// </summary>
internal sealed class ToastForm : Form
{
    private const int WM_NCHITTEST = 0x84, WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
    private const int HTCLIENT = 1, HTTRANSPARENT = -1;

    private readonly Notice _notice;
    private readonly Action _onClick;
    private readonly Action _onLeaving;
    private readonly Action _onClosed;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly LayeredSurface _surface = new();
    private readonly long _lifetimeMs;
    private long _shownAt, _hoverStartedAt, _pausedMs, _enterAt, _leaveAt, _moveAt;
    private bool _hover, _closing, _closed, _placed, _moving;
    private double _alpha, _leaveFrom;
    // In the picture: the card (the rest is its shadow) and the close button, in pixels.
    private Rectangle _card;
    private RectangleF _closeRect;
    // The card's top-left on screen: where it is, and where a slide or glide started and ends.
    private PointF _at, _from, _to;
    // The working area's right edge: nothing is drawn past it, so the card comes out from behind it.
    private int _edge = int.MaxValue;

    /// <summary>Whether it moves: Windows' animation setting, read when it's made.</summary>
    public bool Moves { get; } = Win32.ClientAreaAnimation;

    /// <summary>The card's size in pixels, without its shadow.</summary>
    public Size CardSize => _card.Size;

    public ToastForm(Notice notice, int seconds, Action onClick, Action onLeaving, Action onClosed)
    {
        _notice = notice;
        _onClick = onClick;
        _onLeaving = onLeaving;
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

    /// <summary>Draws the card, so its size is known before it's placed. Not shown yet.</summary>
    public void Prepare()
    {
        _ = Handle;
        Render();
    }

    /// <summary>
    /// Puts the card's top-left at (<paramref name="x"/>, <paramref name="y"/>) on screen. The first time it slides in from
    /// <paramref name="edge"/> (the working area's right edge); after that it glides to its new place.
    /// </summary>
    public void Place(int x, int y, int edge)
    {
        if (_closed) return;
        _edge = edge;
        var target = new PointF(x, y);
        long now = Environment.TickCount64;
        if (!_placed)
        {
            _placed = true;
            _shownAt = _enterAt = now;
            _to = target;
            if (Moves)
            {
                // Its left edge on the screen's edge: hidden, then out from behind it.
                _from = _at = new PointF(edge, y);
                _moving = true;
                _moveAt = now;
                _alpha = 0;
            }
            else
            {
                _at = target;
                _alpha = 1;
            }
            Show();
            Present();
            _timer.Interval = 16;
            _timer.Start();
            return;
        }
        if (target == _to) return;
        _to = target;
        if (Moves)
        {
            _from = _at;
            _moveAt = now;
            _moving = true;
            _timer.Interval = 16;
        }
        else
        {
            _at = target;
            Present();
        }
    }

    /// <summary>Starts it fading out (at once with Windows' animations off); it leaves the stack now and closes after.</summary>
    public void Dismiss()
    {
        if (_closing || _closed) return;
        _closing = true;
        _onLeaving();
        if (!_placed)
        {
            Finish();
            return;
        }
        _leaveAt = Environment.TickCount64;
        _leaveFrom = _alpha;
        // Finished on the next tick rather than here: a click is still being handled in this window's own WndProc.
        _timer.Interval = 16;
        _timer.Start();
    }

    private void Animate()
    {
        if (_closed) return;
        long now = Environment.TickCount64;
        bool changed = false, busy = false;
        if (_moving)
        {
            double t = (now - _moveAt) / ToastMotion.SlideMs;
            if (t >= 1)
            {
                _at = _to;
                _moving = false;
            }
            else
            {
                double e = ToastMotion.Ease(t);
                _at = new PointF((float)(_from.X + (_to.X - _from.X) * e), (float)(_from.Y + (_to.Y - _from.Y) * e));
                busy = true;
            }
            changed = true;
        }
        if (_closing)
        {
            double t = Moves ? (now - _leaveAt) / ToastMotion.FadeOutMs : 1;
            if (t >= 1)
            {
                Finish();
                return;
            }
            _alpha = _leaveFrom * (1 - t);
            changed = busy = true;
        }
        else
        {
            if (_alpha < 1)
            {
                _alpha = ToastMotion.Ease((now - _enterAt) / ToastMotion.SlideMs);
                changed = true;
                busy |= _alpha < 1;
            }
            if (_hover && !ScreenCard.Contains(Cursor.Position))
            {
                _pausedMs += now - _hoverStartedAt;
                _hover = false;
                Render();
                changed = true;
            }
            long visible = now - _shownAt - _pausedMs - (_hover ? now - _hoverStartedAt : 0);
            if (visible >= _lifetimeMs)
            {
                Dismiss();
                return;
            }
        }
        if (changed) Present();
        // Every frame while it moves; slowly while it only waits (quicker while hovered, to see the pointer leave).
        _timer.Interval = busy ? 16 : _hover ? 33 : 100;
    }

    /// <summary>The card on screen, where it is now (its shadow left out).</summary>
    private Rectangle ScreenCard => new((int)Math.Round(_at.X), (int)Math.Round(_at.Y), _card.Width, _card.Height);

    private void Finish()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        _surface.Dispose();
        Close();
        _onClosed();
    }

    private void Render()
    {
        if (!IsHandleCreated || _closed) return;
        float scale = DeviceDpi / 96f;
        using var bmp = ToastRenderer.Render(_notice, scale, _hover, DarkMenuRenderer.Light, DarkMenuRenderer.Grey, out _closeRect, out _card);
        _surface.Set(bmp);
    }

    /// <summary>Puts the picture where the card is now, cut at the screen's edge, as solid as it is now.</summary>
    private void Present()
    {
        if (!IsHandleCreated || _closed) return;
        int left = (int)Math.Round(_at.X) - _card.X, top = (int)Math.Round(_at.Y) - _card.Y;
        int width = Math.Min(_surface.Size.Width, _edge - left);
        byte alpha = (byte)Math.Round(Math.Clamp(_alpha, 0, 1) * 255);
        _surface.Present(Handle, new Point(left, top), width, width > 0 ? alpha : (byte)0);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
            {
                // The shadow isn't the card: the pointer there goes to the card below it, if any.
                long lp = m.LParam.ToInt64();
                var p = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                m.Result = ScreenCard.Contains(p) ? HTCLIENT : HTTRANSPARENT;
                return;
            }
            case WM_MOUSEMOVE:
                if (!_hover && !_closing)
                {
                    _hover = true;
                    _hoverStartedAt = Environment.TickCount64;
                    _timer.Interval = 33;
                    Render();
                    Present();
                }
                break;
            case WM_LBUTTONUP:
            {
                var at = PointToClient(Cursor.Position);
                if (!_card.Contains(at)) return;
                if (!_closeRect.Contains(at)) _onClick();
                Dismiss();
                return;
            }
            case WM_RBUTTONUP:
                if (_card.Contains(PointToClient(Cursor.Position))) Dismiss();
                return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _surface.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Draws a notification card (1.23.0, the "New" card of mock-up 1.23.0 section 9): a header line with RigSight's logo, its
/// name and, on hover, the ×; the game's or app's own icon with a round badge in the kind's colour carrying the kind's
/// glyph; the title and the sentence under it; and for a notice that carries one, its figure against its usual. A dark
/// translucent card with a thin light edge and a soft shadow, in the app's Theme (dark, grey or light).
/// </summary>
internal static class ToastRenderer
{
    /// <summary>The card, in pixels at 100%: its width, its corners, its padding and the icon.</summary>
    internal const float CardWidth = 392, Radius = 12, PadX = 16, PadY = 14, IconSize = 44;

    /// <summary>The room round the card for its shadow, at 100%.</summary>
    internal const float Inset = 12;

    // The notice drawn inside a game by RivaTuner (WidgetRenderer.RtssNoticeText) keeps the old card's colours, always dark.
    internal static readonly Color Bg = Color.FromArgb(250, 22, 22, 22);
    internal static readonly Color Text = Color.FromArgb(255, 255, 255);
    internal static readonly Color Muted = Color.FromArgb(163, 163, 163);

    /// <summary>A theme's colours for the card.</summary>
    private sealed record Look(Color Bg, Color Solid, Color Edge, Color Text, Color Body, Color Faint, Color Close, Color CloseHover,
        Color Tile, Color TileGlyph, Color FigureBg, Color Track, Color Band, Color DotRing, Color BadgeGlyph, int Shadow, bool Light);

    // Dark: the mock-up's rgba(32,32,32,.94) with a 1 px edge of white at 8%; text #EDEDED, the sentence #BDBDBD.
    private static readonly Look DarkLook = new(Color.FromArgb(240, 32, 32, 32), Color.FromArgb(32, 32, 32), Color.FromArgb(20, 255, 255, 255),
        Color.FromArgb(237, 237, 237), Color.FromArgb(189, 189, 189), Color.FromArgb(140, 140, 140), Color.FromArgb(163, 163, 163), Color.FromArgb(24, 255, 255, 255),
        Color.FromArgb(20, 255, 255, 255), Color.FromArgb(163, 163, 163), Color.FromArgb(13, 255, 255, 255), Color.FromArgb(90, 90, 90),
        Color.FromArgb(77, 237, 237, 237), Color.FromArgb(42, 42, 42), Color.FromArgb(0, 0, 0), 120, false);
    // As the app's Grey theme: a lighter card, the same build.
    private static readonly Look GreyLook = new(Color.FromArgb(240, 48, 48, 48), Color.FromArgb(48, 48, 48), Color.FromArgb(26, 255, 255, 255),
        Color.FromArgb(237, 237, 237), Color.FromArgb(200, 200, 200), Color.FromArgb(150, 150, 150), Color.FromArgb(173, 173, 173), Color.FromArgb(28, 255, 255, 255),
        Color.FromArgb(24, 255, 255, 255), Color.FromArgb(173, 173, 173), Color.FromArgb(15, 255, 255, 255), Color.FromArgb(110, 110, 110),
        Color.FromArgb(77, 237, 237, 237), Color.FromArgb(58, 58, 58), Color.FromArgb(0, 0, 0), 120, false);
    // Light, as the app's Light theme: near-white with a thin dark edge, deeper kind colours and white glyphs on them.
    private static readonly Look LightLook = new(Color.FromArgb(245, 252, 252, 252), Color.FromArgb(252, 252, 252), Color.FromArgb(24, 0, 0, 0),
        Color.FromArgb(0, 0, 0), Color.FromArgb(85, 85, 85), Color.FromArgb(110, 110, 110), Color.FromArgb(85, 85, 85), Color.FromArgb(16, 0, 0, 0),
        Color.FromArgb(14, 0, 0, 0), Color.FromArgb(85, 85, 85), Color.FromArgb(10, 0, 0, 0), Color.FromArgb(185, 185, 185),
        Color.FromArgb(56, 0, 0, 0), Color.FromArgb(242, 242, 242), Color.FromArgb(255, 255, 255), 50, true);

    private static readonly Dictionary<int, Bitmap?> Logos = [];
    private static readonly Dictionary<float, Font?> GlyphFonts = [];
    internal static readonly Widgets.RecentIcons Icons = new();

    /// <summary>Each kind's colour as the old card drew it (its bar and label), still used inside a game by RivaTuner.</summary>
    public static Color Accent(NoticeKind kind, bool light = false) => (kind, light) switch
    {
        (NoticeKind.Alert or NoticeKind.FanStopped or NoticeKind.DriveFailing or NoticeKind.DeviceBattery, false) => Color.FromArgb(248, 113, 113),
        (NoticeKind.Recap or NoticeKind.Rewind or NoticeKind.Memory or NoticeKind.Startup or NoticeKind.Weekly, false) => Color.FromArgb(91, 140, 255),
        (NoticeKind.Fans or NoticeKind.Drive or NoticeKind.Usual or NoticeKind.Slowed or NoticeKind.Change or NoticeKind.Space or NoticeKind.Fps, false) => Color.FromArgb(251, 191, 36),
        (NoticeKind.Session, false) => Color.FromArgb(61, 220, 151),
        (NoticeKind.Crash or NoticeKind.Internet, false) => Color.FromArgb(251, 191, 36),
        (NoticeKind.InternetBack, false) => Color.FromArgb(61, 220, 151),
        (_, false) => Color.FromArgb(177, 140, 255),
        (NoticeKind.Alert or NoticeKind.FanStopped or NoticeKind.DriveFailing or NoticeKind.DeviceBattery, true) => Color.FromArgb(185, 28, 28),
        (NoticeKind.Recap or NoticeKind.Rewind or NoticeKind.Memory or NoticeKind.Startup or NoticeKind.Weekly, true) => Color.FromArgb(37, 99, 235),
        (NoticeKind.Fans or NoticeKind.Drive or NoticeKind.Usual or NoticeKind.Slowed or NoticeKind.Change or NoticeKind.Space or NoticeKind.Fps, true) => Color.FromArgb(180, 83, 9),
        (NoticeKind.Session, true) => Color.FromArgb(4, 120, 87),
        (NoticeKind.Crash or NoticeKind.Internet, true) => Color.FromArgb(180, 83, 9),
        (NoticeKind.InternetBack, true) => Color.FromArgb(4, 120, 87),
        (_, true) => Color.FromArgb(124, 58, 237),
    };

    /// <summary>Each kind's label as the old card drew it, still used inside a game by RivaTuner.</summary>
    public static string KindLabel(NoticeKind kind) => kind switch
    {
        NoticeKind.Alert => Lang.T("TEMPERATURE ALERT"),
        NoticeKind.Recap => Lang.T("DAILY RECAP"),
        NoticeKind.Session => Lang.T("SESSION SUMMARY"),
        NoticeKind.Crash => Lang.T("WHAT HAPPENED"),
        NoticeKind.Overlay => Lang.T("OVERLAY"),
        NoticeKind.Rewind => Lang.T("REWIND"),
        NoticeKind.Internet => Lang.T("INTERNET DROP"),
        NoticeKind.InternetBack => Lang.T("INTERNET BACK"),
        NoticeKind.FanStopped or NoticeKind.Fans => Lang.T("FANS"),
        NoticeKind.DriveFailing or NoticeKind.Drive => Lang.T("DRIVE HEALTH"),
        NoticeKind.Usual => Lang.T("AGAINST YOUR USUAL"),
        NoticeKind.Slowed => Lang.T("TEMPERATURES"),
        NoticeKind.Change => Lang.T("WHAT CHANGED"),
        NoticeKind.Space => Lang.T("STORAGE"),
        NoticeKind.Memory => Lang.T("MEMORY"),
        NoticeKind.Startup => Lang.T("STARTS WITH WINDOWS"),
        NoticeKind.Weekly => Lang.T("WEEKLY RECAP"),
        NoticeKind.Timer => Lang.T("TIMER"),
        NoticeKind.Fps => Lang.T("GAMES"),
        NoticeKind.DeviceBattery => Lang.T("BATTERY"),
        NoticeKind.LookBack => Lang.T("THE LAST 2 MINUTES"),
        _ => Lang.T("UPDATE"),
    };

    /// <summary>
    /// The badge's colour: red for a problem, amber for what needs a look, green for the internet back, blue for recaps and
    /// a device's battery (as drawn), violet for RigSight's own. Bright on dark, deeper on light (4.5:1 on white).
    /// </summary>
    public static Color BadgeColor(NoticeKind kind, bool light = false)
    {
        var (bright, deep) = kind switch
        {
            NoticeKind.Alert or NoticeKind.Crash or NoticeKind.Internet or NoticeKind.FanStopped or NoticeKind.DriveFailing
                => (Color.FromArgb(248, 113, 113), Color.FromArgb(185, 28, 28)),
            NoticeKind.Fans or NoticeKind.Drive or NoticeKind.Usual or NoticeKind.Slowed or NoticeKind.Change or NoticeKind.Space or NoticeKind.Fps
                => (Color.FromArgb(251, 191, 36), Color.FromArgb(180, 83, 9)),
            NoticeKind.InternetBack => (Color.FromArgb(61, 220, 151), Color.FromArgb(4, 120, 87)),
            NoticeKind.Recap or NoticeKind.Session or NoticeKind.Rewind or NoticeKind.Memory or NoticeKind.Startup or NoticeKind.Weekly or NoticeKind.DeviceBattery
                => (Color.FromArgb(91, 140, 255), Color.FromArgb(37, 99, 235)),
            _ => (Color.FromArgb(177, 140, 255), Color.FromArgb(124, 58, 237)),
        };
        return light ? deep : bright;
    }

    /// <summary>The amber of the figure's stretch above the usual: it needs a look.</summary>
    private static Color Over(bool light) => light ? Color.FromArgb(180, 83, 9) : Color.FromArgb(251, 191, 36);

    /// <summary>The kind's glyph on the badge (Windows' icon font; the sidebar's page icons where there is one).</summary>
    public static string BadgeGlyph(Notice n) => n.Kind switch
    {
        NoticeKind.Alert or NoticeKind.Slowed => "",
        // "More PC problems than usual" is a crash count, not heat.
        NoticeKind.Usual => n.Page == "crashes" ? "" : "",
        NoticeKind.Crash => "",
        NoticeKind.Internet => "",
        NoticeKind.InternetBack => "",
        NoticeKind.DeviceBattery => "",
        NoticeKind.FanStopped or NoticeKind.Fans => "",
        NoticeKind.DriveFailing or NoticeKind.Drive or NoticeKind.Space => "",
        NoticeKind.Change or NoticeKind.LookBack => "",
        NoticeKind.Memory => "",
        NoticeKind.Startup => "",
        NoticeKind.Recap or NoticeKind.Weekly => "",
        NoticeKind.Session or NoticeKind.Fps or NoticeKind.Overlay => "",
        NoticeKind.Rewind => "",
        NoticeKind.Timer => "",
        _ => "",
    };

    /// <summary>
    /// What stands in for an app's icon when the notice has none: what it's about (the PC, the internet, a device, a
    /// drive…); null for RigSight's own update, which shows RigSight's logo.
    /// </summary>
    public static string? SubjectGlyph(NoticeKind kind) => kind switch
    {
        NoticeKind.Internet or NoticeKind.InternetBack => "",
        NoticeKind.DeviceBattery => "",
        NoticeKind.DriveFailing or NoticeKind.Drive or NoticeKind.Space => "",
        NoticeKind.FanStopped or NoticeKind.Fans => "",
        NoticeKind.Memory => "",
        NoticeKind.Startup => "",
        NoticeKind.Recap or NoticeKind.Weekly => "",
        NoticeKind.Session or NoticeKind.Fps or NoticeKind.Overlay => "",
        NoticeKind.Rewind => "",
        NoticeKind.Timer => "",
        NoticeKind.Change or NoticeKind.LookBack => "",
        NoticeKind.Update => null,
        _ => "",
    };

    public static Bitmap Render(Notice n, float scale, bool hover, out RectangleF closeRect) =>
        Render(n, scale, hover, DarkMenuRenderer.Light, DarkMenuRenderer.Grey, out closeRect, out _);

    internal static Bitmap Render(Notice n, float scale, bool hover, bool light, out RectangleF closeRect, bool grey = false) =>
        Render(n, scale, hover, light, grey, out closeRect, out _);

    /// <param name="closeRect">The × in the picture's pixels (hover only).</param>
    /// <param name="card">The card in the picture's pixels; the rest is its shadow.</param>
    internal static Bitmap Render(Notice n, float scale, bool hover, bool light, bool grey, out RectangleF closeRect, out Rectangle card)
    {
        var look = light ? LightLook : grey ? GreyLook : DarkLook;
        using var headFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleFont = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Segoe UI", 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        float textX = PadX + IconSize + 13, textW = CardWidth - textX - PadX;
        float headY = PadY, midY = headY + 14 + 10;

        // The title wraps onto a second line rather than cut off its end: "You played Phantom Blade Zero for 2 hours…" must
        // still say how long. Past two lines it ends in "…" at a word. A number stays on the line of its unit ("2 hours").
        string title = Regex.Replace(n.Title, @"(?<=\d) (?=\p{L})", " ");
        float titleLine, titleH, bodyH;
        using (var tmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(tmp))
        {
            titleLine = titleFont.GetHeight(mg);
            float measured = mg.MeasureString(title, titleFont, (int)textW, StringFormat.GenericTypographic).Height;
            titleH = Math.Min(measured, titleLine * 2.2f) > titleLine * 1.5f ? titleLine * 2 : titleLine;
            bodyH = n.Body.Length == 0 ? 0 : mg.MeasureString(n.Body, bodyFont, (int)textW, StringFormat.GenericTypographic).Height;
        }
        float bodyY = midY + titleH + 3;
        float textBottom = bodyY + bodyH;
        float figureY = textBottom + 10, figureH = 42;
        if (n.Figure is not null) textBottom = figureY + figureH;
        float height = Math.Max(midY + IconSize, textBottom) + PadY;

        int w = (int)Math.Ceiling((CardWidth + Inset * 2) * scale), h = (int)Math.Ceiling((height + Inset * 2) * scale);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.Transparent);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(Inset, Inset);

        var cardRect = new RectangleF(0, 0, CardWidth, height);
        using (var path = RoundRect(cardRect, Radius))
        {
            Shadow(g, cardRect, path, look);
            using var bg = new SolidBrush(look.Bg);
            g.FillPath(bg, path);
            using var edge = new Pen(look.Edge, 1);
            using var inner = RoundRect(new RectangleF(0.5f, 0.5f, CardWidth - 1, height - 1), Radius - 0.5f);
            g.DrawPath(edge, inner);
        }

        // The header: RigSight's logo and name, and the × on hover.
        if (Logo((int)Math.Round(14 * scale)) is { } logo) g.DrawImage(logo, new RectangleF(PadX, headY, 14, 14));
        using (var faint = new SolidBrush(look.Faint))
            g.DrawString("RigSight", headFont, faint, PadX + 14 + 8, headY + 7 - headFont.GetHeight(g) / 2, StringFormat.GenericTypographic);
        var close = new RectangleF(CardWidth - PadX - 5 - 11, headY + 7 - 11, 22, 22);
        if (hover)
        {
            using var hb = new SolidBrush(look.CloseHover);
            g.FillEllipse(hb, close);
            using var xp = new Pen(look.Close, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float c = 4.2f, cx = close.X + 11, cy = close.Y + 11;
            g.DrawLine(xp, cx - c, cy - c, cx + c, cy + c);
            g.DrawLine(xp, cx + c, cy - c, cx - c, cy + c);
        }

        // The game's or app's icon, with the kind's badge on its corner.
        var av = new RectangleF(PadX, midY, IconSize, IconSize);
        Avatar(g, n, av, look, scale);
        var ring = new RectangleF(av.Right + 5 - 24, av.Bottom + 5 - 24, 24, 24);
        using (var rb = new SolidBrush(look.Solid)) g.FillEllipse(rb, ring);
        var badge = RectangleF.Inflate(ring, -2, -2);
        using (var kb = new SolidBrush(BadgeColor(n.Kind, look.Light))) g.FillEllipse(kb, badge);
        Glyph(g, BadgeGlyph(n), 10, look.BadgeGlyph, badge);

        // The title and the sentence under it.
        using var trim = (StringFormat)StringFormat.GenericTypographic.Clone();
        trim.Trimming = StringTrimming.EllipsisWord;
        trim.FormatFlags |= StringFormatFlags.LineLimit;
        using (var tb = new SolidBrush(look.Text))
            g.DrawString(title, titleFont, tb, new RectangleF(textX, midY, textW, titleH + 2), trim);
        if (bodyH > 0)
        {
            using var bb = new SolidBrush(look.Body);
            g.DrawString(n.Body, bodyFont, bb, new RectangleF(textX, bodyY, textW, bodyH + 4), StringFormat.GenericTypographic);
        }

        if (n.Figure is { } f) Figure(g, f, new RectangleF(textX, figureY, textW, figureH), look);

        closeRect = new RectangleF((close.X + Inset) * scale, (close.Y + Inset) * scale, close.Width * scale, close.Height * scale);
        card = new Rectangle((int)Math.Round(Inset * scale), (int)Math.Round(Inset * scale), (int)Math.Round(CardWidth * scale), (int)Math.Round(height * scale));
        return bmp;
    }

    /// <summary>A soft shadow round the card, a little lower than it, fading out over the inset (none under the card itself).</summary>
    private static void Shadow(Graphics g, RectangleF card, GraphicsPath cardPath, Look look)
    {
        var clip = g.Clip;
        g.SetClip(cardPath, CombineMode.Exclude);
        for (int d = (int)Inset; d >= 1; d--)
        {
            double fall = (Inset + 1 - d) / (Inset + 1);
            int a = (int)Math.Round(look.Shadow * fall * fall * fall);
            if (a <= 0) continue;
            using var pen = new Pen(Color.FromArgb(a, 0, 0, 0), 1.4f);
            var r = RectangleF.Inflate(card, d - 0.5f, d - 0.5f);
            r.Offset(0, Math.Min(3, d * 0.5f));
            using var p = RoundRect(r, Radius + d);
            g.DrawPath(pen, p);
        }
        g.Clip = clip;
        clip.Dispose();
    }

    /// <summary>The app's own icon, rounded; without one, what the notice is about on a quiet tile; for an update, RigSight's logo.</summary>
    private static void Avatar(Graphics g, Notice n, RectangleF r, Look look, float scale)
    {
        using var shape = RoundRect(r, 10);
        if (IconFor(n.IconPath) is { } icon)
        {
            using var tb = new TextureBrush(icon, WrapMode.Clamp);
            tb.TranslateTransform(r.X, r.Y);
            tb.ScaleTransform(r.Width / icon.Width, r.Height / icon.Height);
            g.FillPath(tb, shape);
            return;
        }
        if (SubjectGlyph(n.Kind) is not { } glyph)
        {
            if (Logo((int)Math.Round(IconSize * scale)) is { } logo) g.DrawImage(logo, r);
            return;
        }
        using (var tile = new SolidBrush(look.Tile)) g.FillPath(tile, shape);
        Glyph(g, glyph, 18, look.TileGlyph, r);
    }

    /// <summary>
    /// The figure line: the reading in big, a scale with the usual as a grey band labelled under it and an amber stretch from
    /// its top to an amber dot at the reading, and how far above it that is on the right.
    /// </summary>
    private static void Figure(Graphics g, Core.Notices.NoticeFigure f, RectangleF r, Look look)
    {
        using (var panel = RoundRect(r, 8))
        using (var pb = new SolidBrush(look.FigureBg))
            g.FillPath(pb, panel);
        using var bigFont = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var sideFont = new Font("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        string big = f.ValueText, side = f.AboveText, label = f.UsualText;
        var fmt = StringFormat.GenericTypographic;
        float bigW = g.MeasureString(big, bigFont, 400, fmt).Width, sideW = g.MeasureString(side, sideFont, 400, fmt).Width;
        float mid = r.Y + r.Height / 2;
        using (var tb = new SolidBrush(look.Text))
            g.DrawString(big, bigFont, tb, r.X + 10, mid - bigFont.GetHeight(g) / 2, fmt);
        using (var mb = new SolidBrush(look.Close))
            g.DrawString(side, sideFont, mb, r.Right - 10 - sideW, mid - sideFont.GetHeight(g) / 2, fmt);

        float sx = r.X + 10 + bigW + 10, sw = r.Right - 10 - sideW - 10 - sx, top = r.Y + 7;
        if (sw < 40) return;
        float X(double c) => sx + (float)(f.At(c) * sw);
        using (var track = new SolidBrush(look.Track)) g.FillRectangle(track, sx, top + 8, sw, 2);
        float lo = X(f.Low), hi = X(f.High);
        if (hi - lo < 10) (lo, hi) = ((lo + hi) / 2 - 5, (lo + hi) / 2 + 5);
        using (var band = new SolidBrush(look.Band))
        using (var bandPath = RoundRect(new RectangleF(lo, top + 4, hi - lo, 10), 5))
            g.FillPath(band, bandPath);
        float v = X(f.Value), from = X(f.High);
        var amber = Over(look.Light);
        using (var ov = new SolidBrush(amber)) g.FillRectangle(ov, from, top + 8, Math.Max(0, v - from), 2);
        using (var ringB = new SolidBrush(look.DotRing)) g.FillEllipse(ringB, v - 7, top + 2, 14, 14);
        using (var dot = new SolidBrush(amber)) g.FillEllipse(dot, v - 5, top + 4, 10, 10);
        float labelW = g.MeasureString(label, labelFont, 400, fmt).Width;
        float lx = Math.Clamp((X(f.Low) + X(f.High)) / 2 - labelW / 2, sx, Math.Max(sx, sx + sw - labelW));
        using (var fb = new SolidBrush(look.Faint)) g.DrawString(label, labelFont, fb, lx, top + 16, fmt);
    }

    /// <summary>One of Windows' icon glyphs, centred in <paramref name="r"/>.</summary>
    private static void Glyph(Graphics g, string glyph, float px, Color color, RectangleF r)
    {
        if (GlyphFont(px) is not { } font) return;
        using var brush = new SolidBrush(color);
        using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, r, centre);
    }

    /// <summary>Windows' icon font at <paramref name="px"/> pixels (Segoe Fluent Icons, else Segoe MDL2 Assets on Windows 10).</summary>
    private static Font? GlyphFont(float px)
    {
        lock (GlyphFonts)
        {
            if (GlyphFonts.TryGetValue(px, out var cached)) return cached;
            var family = FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons") ?? FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets");
            var font = family is null ? null : new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel);
            GlyphFonts[px] = font;
            return font;
        }
    }

    /// <summary>RigSight's logo at <paramref name="size"/> pixels (a test copy's violet "T"), kept once drawn.</summary>
    private static Bitmap? Logo(int size)
    {
        size = Math.Max(8, size);
        lock (Logos)
        {
            if (Logos.TryGetValue(size, out var cached)) return cached;
            Bitmap? logo = null;
            try
            {
                if (RigsightPaths.IsTestInstance) logo = TestLogo.Draw(size);
                else
                {
                    using var icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Rigsight.ico"), size, size);
                    logo = icon.ToBitmap();
                }
            }
            catch { }
            Logos[size] = logo;
            return logo;
        }
    }

    /// <summary>The app's own icon from its file, kept for the next card (null: none found).</summary>
    private static Bitmap? IconFor(string? path)
    {
        if (path is null) return null;
        return Icons.Get(path, p =>
        {
            try
            {
                using var icon = Icon.ExtractIcon(p, 0, 64);
                return icon?.ToBitmap();
            }
            catch { return null; }
        });
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// A card's picture kept in a memory DC, so it can be moved and faded without drawing it again: each step of a slide is one
/// UpdateLayeredWindow call. Cut on its right at a given width (the screen's edge, during the slide in).
/// </summary>
internal sealed class LayeredSurface : IDisposable
{
    private IntPtr _memDc, _bitmap, _old;

    public Size Size { get; private set; }

    /// <summary>Takes <paramref name="picture"/> (copied; the caller keeps and disposes it).</summary>
    public void Set(Bitmap picture)
    {
        Release();
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        try
        {
            _memDc = Win32.CreateCompatibleDC(screenDc);
            _bitmap = picture.GetHbitmap(Color.FromArgb(0));
            _old = Win32.SelectObject(_memDc, _bitmap);
            Size = picture.Size;
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Shows the picture's left <paramref name="width"/> pixels at <paramref name="at"/>, as solid as <paramref name="opacity"/>.</summary>
    public void Present(IntPtr hwnd, Point at, int width, byte opacity)
    {
        if (_memDc == IntPtr.Zero || Size.Height <= 0) return;
        var size = new Win32.SIZE(Math.Clamp(width, 1, Size.Width), Size.Height);
        var source = new Win32.POINT(0, 0);
        var topLeft = new Win32.POINT(at.X, at.Y);
        var blend = new Win32.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
        Win32.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref topLeft, ref size, _memDc, ref source, 0, ref blend, Win32.ULW_ALPHA);
    }

    private void Release()
    {
        if (_memDc != IntPtr.Zero)
        {
            Win32.SelectObject(_memDc, _old);
            Win32.DeleteDC(_memDc);
        }
        if (_bitmap != IntPtr.Zero) Win32.DeleteObject(_bitmap);
        _memDc = _bitmap = _old = IntPtr.Zero;
        Size = Size.Empty;
    }

    public void Dispose() => Release();
}

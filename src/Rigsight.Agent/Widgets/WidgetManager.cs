using System.Drawing;
using Rigsight.Agent.Ui;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>Creates, updates and positions widget windows according to settings. UI thread only.</summary>
internal sealed class WidgetManager(Func<RigsightSettings> getSettings, Action<Action<RigsightSettings>> mutateSettings, Action<string?> openWidgetSettings)
{
    private readonly Dictionary<string, WidgetForm> _forms = [];
    private WidgetData? _data;
    private readonly WidgetsOverGames _overGames = new();
    private IntPtr _gameMonitor;

    // The two widget shortcuts (Desktop widgets off and on; the widgets on the desktop in front), and Esc while they're
    // in front. Made on first use, on the UI thread.
    private GlobalHotkey? _shownKey, _frontKey, _escKey;
    private string? _shownKeyText, _frontKeyText;
    private System.Windows.Forms.Timer? _fadeTimer, _frontTimer;
    private IntPtr _frontOver;

    /// <summary>The widgets on the desktop are in front of the windows for now (their shortcut), until Esc or a click elsewhere.</summary>
    public bool InFront { get; private set; }

    public static string DisplayName(WidgetStyle style) => WidgetCatalog.Title(style);

    public void Apply(RigsightSettings settings)
    {
        // "Like RigSight" widgets take the app's theme and accent.
        WidgetRenderer.UseAppLook(settings.Theme, settings.DarkTheme, settings.LightTheme, settings.Accent);
        // Gone from settings (a widget of the user's own deleted) or turned off, or every widget switched off
        // (WidgetsShown, each keeping its own Enabled): its window goes.
        // A box with no tiles yet (a widget just made, still in the editor) has nothing to show: no window, and no spot
        // saved for its few pixels (it then grew off the screen's edge, 2026-11-10).
        var on = settings.WidgetsShown ? settings.Widgets.Where(w => w.Enabled && w.Tiles is not { Count: 0 }).ToList() : [];
        var shown = on.Select(w => w.Id).ToHashSet();
        foreach (var id in _forms.Keys.Where(id => !shown.Contains(id)).ToList())
        {
            _forms[id].Close();
            _forms[id].Dispose();
            _forms.Remove(id);
        }
        // Typing into a widget that's gone (hidden, removed, every widget switched off with the shortcut): the box nobody
        // sees kept the keyboard, and what was typed next went into a note no longer on the screen (the audit, 2026-11-11).
        if (_typing is { IsDisposed: false } typing && WidgetRenderer.Typing is { } ty && !shown.Contains(ty.WidgetId))
        {
            Log.Write("widgets", "Typing on the desktop ended: its widget is no longer shown");
            typing.Close();
        }
        foreach (var cfg in on)
        {
            _forms.TryGetValue(cfg.Id, out var form);
            if (form is null)
            {
                form = new WidgetForm(cfg, this);
                _forms[cfg.Id] = form;
                if (_data is not null) form.UpdateData(_data);
                PlaceInitially(form, cfg);
            }
            form.Apply(cfg);
        }
        UpdateVisibility();
        ApplyShortcuts(settings);
        ApplyFade(settings);
    }

    // ── The shortcuts ─────────────────────────────────────────────────────

    private void ApplyShortcuts(RigsightSettings settings)
    {
        if (settings.WidgetsHotkey != _shownKeyText)
        {
            _shownKeyText = settings.WidgetsHotkey;
            if (_shownKey is null)
            {
                _shownKey = new GlobalHotkey();
                _shownKey.Pressed += () =>
                {
                    Log.Write("widgets", "Desktop widgets turned " + (getSettings().WidgetsShown ? "off" : "on") + " with the shortcut");
                    mutateSettings(s => s.WidgetsShown = !s.WidgetsShown);
                };
            }
            bool ok = _shownKey.Register(Hotkey.TryParse(_shownKeyText, out var k) ? k : null);
            if (!ok) Log.Write("widgets", $"The shortcut {_shownKeyText} for Desktop widgets is taken by another program");
        }
        if (settings.WidgetsFrontHotkey != _frontKeyText)
        {
            _frontKeyText = settings.WidgetsFrontHotkey;
            if (_frontKey is null)
            {
                _frontKey = new GlobalHotkey();
                _frontKey.Pressed += () => BringInFront(!InFront);
            }
            bool ok = _frontKey.Register(Hotkey.TryParse(_frontKeyText, out var k) ? k : null);
            if (!ok) Log.Write("widgets", $"The shortcut {_frontKeyText} to bring widgets in front is taken by another program");
        }
    }

    /// <summary>
    /// The widgets on the desktop in front of the windows (or back behind them). In front, Esc sends them back, and so does
    /// any other window coming to the front (a click elsewhere): checked four times a second while they're in front.
    /// </summary>
    public void BringInFront(bool front)
    {
        if (front == InFront) return;
        InFront = front;
        Log.Write("widgets", front ? "Widgets on the desktop brought in front" : "Widgets on the desktop back behind the windows");
        foreach (var form in _forms.Values.Where(f => !f.SitsOver)) form.Restack();
        if (front)
        {
            _escKey ??= new GlobalHotkey();
            _escKey.Pressed -= SendBack;
            _escKey.Pressed += SendBack;
            _escKey.Register(new Hotkey(HotkeyModifiers.None, 0x1B));
            _frontOver = Native.Win32.GetForegroundWindow();
            _frontTimer ??= new System.Windows.Forms.Timer { Interval = 250 };
            _frontTimer.Tick -= CheckFront;
            _frontTimer.Tick += CheckFront;
            _frontTimer.Start();
        }
        else
        {
            _escKey?.Register(null);
            _frontTimer?.Stop();
        }
    }

    private void SendBack() => BringInFront(false);

    private void CheckFront(object? sender, EventArgs e)
    {
        var ours = _forms.Values.Where(f => f.IsHandleCreated).Select(f => f.Handle).ToList();
        if (_typing is { IsDisposed: false, IsHandleCreated: true } typing) ours.Add(typing.Handle);
        if (!StaysInFront(Native.Win32.GetForegroundWindow(), _frontOver, ours)) BringInFront(false);
    }

    /// <summary>
    /// The widgets stay in front while the window in front is the one that was when they came, or one of theirs: a
    /// widget, or the box that takes the keys for a note or a to-do clicked on. That box wasn't counted, so a note
    /// clicked on to type in went back behind the windows a quarter of a second later (the audit, 2026-11-11).
    /// </summary>
    internal static bool StaysInFront(IntPtr now, IntPtr frontOver, IEnumerable<IntPtr> ours) =>
        now == frontOver || now != IntPtr.Zero && ours.Contains(now);

    // ── Fading under the pointer ──────────────────────────────────────────

    /// <summary>Widgets fade while the pointer is over them: where the pointer is, 25 times a second, only with the setting on.</summary>
    private void ApplyFade(RigsightSettings settings)
    {
        bool wanted = settings.WidgetFade && _forms.Count > 0;
        if (!wanted)
        {
            _fadeTimer?.Stop();
            foreach (var f in _forms.Values) f.SetFaded(false);
            return;
        }
        if (_fadeTimer is null)
        {
            // 25 times a second, so the fade starts as the pointer arrives: a click-through widget gets no mouse
            // messages, so where the pointer is gets looked at (a quick check, with nothing drawn unless it changed).
            _fadeTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _fadeTimer.Tick += (_, _) =>
            {
                var at = Cursor.Position;
                // Every widget the pointer is over (the user, 2026-11-10: "widgets are not fading"): a click-through one
                // almost away, so what's behind can be read and clicked; another to half, so it can still be grabbed.
                foreach (var f in _forms.Values) f.SetFaded(f.Visible && f.PanelBounds.Contains(at));
            };
        }
        _fadeTimer.Start();
    }

    // ── Snapping while dragged ────────────────────────────────────────────

    /// <summary>
    /// A widget being dragged (its window's rectangle <paramref name="r"/>, room for the badge included) pulled onto its
    /// screen's edges and middle and onto other widgets' edges, 12 pixels apart, when within 10 pixels. Alt, or the
    /// setting off: placed freely. Returns whether it moved it.
    /// </summary>
    public bool Snap(WidgetForm form, ref Native.Win32.RECT r)
    {
        if (!getSettings().WidgetSnap || (Control.ModifierKeys & Keys.Alt) != 0) return false;
        int room = form.Room;
        var panel = Rectangle.FromLTRB(r.Left, r.Top + room, r.Right - room, r.Bottom);
        var area = Screen.FromRectangle(panel).WorkingArea;
        float dpi = form.DeviceDpi / 96f;
        int gap = (int)Math.Round(12 * dpi), near = (int)Math.Round(10 * dpi);
        var others = _forms.Values.Where(f => f != form && f.Visible && area.IntersectsWith(f.PanelBounds)).Select(f => f.PanelBounds).ToList();
        var (x, y) = SnapPanel(panel, area, others, gap, near);
        if (x == panel.X && y == panel.Y) return false;
        int dx = x - panel.X, dy = y - panel.Y;
        r.Left += dx; r.Right += dx; r.Top += dy; r.Bottom += dy;
        return true;
    }

    /// <summary>Where a panel snaps to: its screen's edges (a gap in) and middle, other panels' edges (lined up, or a gap apart).</summary>
    internal static (int X, int Y) SnapPanel(Rectangle panel, Rectangle area, IReadOnlyList<Rectangle> others, int gap, int near)
    {
        int w = panel.Width, h = panel.Height;
        var xs = new List<int> { area.Left + gap, area.Right - gap - w, area.Left + (area.Width - w) / 2 };
        var ys = new List<int> { area.Top + gap, area.Bottom - gap - h, area.Top + (area.Height - h) / 2 };
        foreach (var o in others)
        {
            xs.AddRange([o.Left, o.Right - w, o.Right + gap, o.Left - gap - w, o.Left + (o.Width - w) / 2]);
            ys.AddRange([o.Top, o.Bottom - h, o.Bottom + gap, o.Top - gap - h]);
        }
        static int Nearest(List<int> cands, int v, int near) =>
            cands.Select(c => (c, d: Math.Abs(c - v))).Where(p => p.d <= near).OrderBy(p => p.d).Select(p => (int?)p.c).FirstOrDefault() ?? v;
        return (Nearest(xs, panel.X, near), Nearest(ys, panel.Y, near));
    }

    /// <summary>
    /// Widgets whose saved spot is on no screen right now (a screen not there yet as the agent starts after an update or a
    /// sign-in, say): shown in the corner for now, their spot kept, and put back on it once a screen has it.
    /// </summary>
    private readonly HashSet<string> _waitingForScreen = [];
    private bool _watchingScreens;

    /// <summary>
    /// Enough of a widget at that spot shows on some screen, by the screen's whole area (a widget may sit over the taskbar
    /// or partly off an edge, as the user dragged it: 2026-11-06, a report of widgets back in the corner after every
    /// update, when the check was a point 20 px in, inside the area the taskbar leaves free).
    /// </summary>
    internal static bool OnAScreen(Rectangle widget, IEnumerable<Rectangle> screens) =>
        screens.Any(s => Rectangle.Intersect(s, widget) is { Width: >= 24, Height: >= 16 });

    private static IEnumerable<Rectangle> ScreenAreas => Screen.AllScreens.Select(s => s.Bounds);

    private void PlaceInitially(WidgetForm form, WidgetConfig cfg)
    {
        // No screen at all (a remote session as it starts, say): a usual size, not an error that stops the agent starting.
        var screen = (Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault())?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        if (cfg.X is int cx && cfg.Y is int cy)
        {
            form.PanelLocation = new Point(cx, cy);
            form.Show();
            form.Redraw(); // now at its real size
            if (OnAScreen(form.PanelBounds, ScreenAreas)) return;
            // Kept, not forgotten: the corner below is not saved, and the spot comes back with its screen.
            _waitingForScreen.Add(cfg.Id);
            WatchScreens();
            Log.Write("widgets", $"{WidgetCatalog.Title(cfg)} saved at {cx},{cy} ({form.Width}x{form.Height}) is on no screen now " +
                                 $"(screens: {string.Join(", ", Screen.AllScreens.Select(s => $"{s.Bounds.X},{s.Bounds.Y} {s.Bounds.Width}x{s.Bounds.Height}"))}): " +
                                 "shown in the corner for now, its spot kept for when that screen is back");
        }

        // No saved spot: render off-screen first to learn the real size, then tuck it into the
        // top-right corner, below any other widgets already placed there.
        form.PanelLocation = new Point(screen.Right + 4000, screen.Top);
        form.Show();
        form.Redraw();
        int margin = (int)(12 * form.DeviceDpi / 96.0);
        // Start just below where maximized windows draw their title bar and close button.
        int y = screen.Top + (int)(44 * form.DeviceDpi / 96.0);
        foreach (var other in _forms.Values.Where(f => f != form && f.Visible && f.PanelBounds.Right >= screen.Right - margin * 3 && f.PanelBounds.Top < screen.Top + screen.Height / 2))
            y = Math.Max(y, other.PanelBounds.Bottom + margin);
        form.PanelLocation = new Point(screen.Right - form.PanelBounds.Width - margin, y);
        form.Redraw();
        // Saved as its spot, so the Widgets page's map of the screens shows it there (after this pass over the widgets,
        // as saving goes round to the settings again). A spot set meanwhile is kept.
        var spot = form.PanelLocation;
        form.BeginInvoke(() => Mutate(cfg.Id, c => { if (c.X is null || c.Y is null) (c.X, c.Y) = (spot.X, spot.Y); }));
    }

    /// <summary>Screens added, removed or resized: a widget waiting for its spot goes back to it once a screen has it.</summary>
    private void WatchScreens()
    {
        if (_watchingScreens) return;
        _watchingScreens = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            // Raised on the events' own thread: the forms are moved on theirs.
            if (_forms.Values.FirstOrDefault() is { IsHandleCreated: true } any) any.BeginInvoke(ReturnToSavedSpots);
        };
    }

    private void ReturnToSavedSpots()
    {
        if (_waitingForScreen.Count == 0) return;
        var widgets = getSettings().Widgets;
        foreach (var id in _waitingForScreen.ToList())
        {
            if (!_forms.TryGetValue(id, out var form) || widgets.FirstOrDefault(w => w.Id == id) is not { X: int x, Y: int y } cfg)
            {
                _waitingForScreen.Remove(id);
                continue;
            }
            if (!OnAScreen(new Rectangle(x, y, form.Width, form.Height), ScreenAreas)) continue;
            form.PanelLocation = new Point(x, y);
            form.Redraw();
            _waitingForScreen.Remove(id);
            Log.Write("widgets", $"{WidgetCatalog.Title(cfg)} back at its saved spot {x},{y}: its screen is there now");
        }
    }

    /// <summary>A widget dragged somewhere new no longer waits for its old spot.</summary>
    public void Moved(string id) => _waitingForScreen.Remove(id);

    /// <summary>Says a widget's timer is up (title, body): the agent's notifications.</summary>
    public Action<string, string>? TimerUp { get; set; }

    private TileTypingBox? _typing;
    private MediaWatcher? _media;

    /// <summary>
    /// Opens a shortcut's app, file, folder or web address with normal rights: through Explorer when the agent runs
    /// as administrator, so nothing opened from a widget gets the agent's rights (see <see cref="LinkStart"/>).
    /// </summary>
    private static void OpenLink(TileLink link)
    {
        string kind = link.IsWeb ? "a web address" : Directory.Exists(link.Path) ? "a folder" : $"a {Path.GetExtension(link.Path)} file";
        try
        {
            bool admin = Environment.IsPrivilegedProcess, shell = Native.Win32.GetShellWindow() != IntPtr.Zero;
            if (LinkStart(link, admin, shell, File.Exists, Directory.Exists) is not { } start)
            {
                Log.Write("widgets", admin && !shell
                    ? $"A shortcut to {kind} wasn't opened: Windows' desktop (Explorer) isn't running, and without it it would open with admin rights"
                    : $"A shortcut to {kind} wasn't opened: it isn't a web address, or a full path to a file or folder that's there");
                return;
            }
            System.Diagnostics.Process.Start(start)?.Dispose();
            Log.Write("widgets", $"A shortcut opened {kind}");
        }
        catch (Exception ex)
        {
            Log.Write("widgets", $"A shortcut to {kind} couldn't be opened: {ex.Message}");
        }
    }

    /// <summary>A path on a drive letter this program doesn't have (a network drive mapped for the person, seen from an elevated agent).</summary>
    internal static bool DriveHidden(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: 3 } root && char.IsAsciiLetter(root[0]) && root[1] == ':'
                && new DriveInfo(root).DriveType == DriveType.NoRootDirectory;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a shortcut is opened (for the tests): see <see cref="LinkStart"/>, with Explorer running.
    /// </summary>
    internal static bool Opens(TileLink link, bool elevated) => LinkStart(link, elevated, shellRunning: true, File.Exists, Directory.Exists) is not null;

    /// <summary>
    /// How a shortcut is started, or null when it may not be. The settings file is the person's own, writable without
    /// admin rights, and the agent may run as administrator, so what's in it is never handed to a shell as it is:
    /// <list type="bullet">
    /// <item>a web address must read as an http or https address, and goes on escaped (no quote or space left in it);</item>
    /// <item>anything else must be a full path (not relative to the agent's working folder, not a \\?\ or \\.\ device
    /// path) to a file or folder that's there;</item>
    /// <item>as administrator it goes to explorer.exe, which hands it to the desktop's own Explorer, running with normal
    /// rights; with no desktop Explorer running, explorer.exe would become it, as administrator, so nothing opens.</item>
    /// </list>
    /// </summary>
    internal static System.Diagnostics.ProcessStartInfo? LinkStart(TileLink link, bool admin, bool shellRunning, Func<string, bool> fileExists, Func<string, bool> folderExists)
    {
        string target;
        if (link.IsWeb)
        {
            if (!Uri.TryCreate(link.Path.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
            target = uri.AbsoluteUri;
            if (target.IndexOfAny(['"', ' ']) >= 0) return null; // AbsoluteUri escapes both; never trusted blindly
        }
        else
        {
            string path = link.Path;
            if (path.Length == 0 || path.IndexOfAny(['"', '\0']) >= 0 || !Path.IsPathFullyQualified(path)
                || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
            // From an agent run as administrator, a path on a drive letter it doesn't have is let through to Explorer:
            // Windows keeps the person's mapped network drives apart from programs run as administrator, so a shortcut
            // to Z:\ looked gone (the audit, 2026-11-11), while Explorer, which opens it with the person's rights, has Z:.
            if (!fileExists(path) && !folderExists(path) && !(admin && DriveHidden(path))) return null;
            target = Path.TrimEndingDirectorySeparator(path);
        }
        if (!admin) return new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true };
        if (!shellRunning) return null;
        // In quotes, as one argument. A path still ending in \ is a root ("D:\", "\\nas\share\"): quoted it would end
        // in \", so it goes without quotes, or without its last \ when it has a space.
        string argument = !target.EndsWith('\\') ? $"\"{target}\"" : target.Contains(' ') ? $"\"{target.TrimEnd('\\')}\"" : target;
        return new System.Diagnostics.ProcessStartInfo(RigsightPaths.Explorer, argument) { UseShellExecute = false };
    }

    /// <summary>
    /// A click on a tile's clickable place on the desktop: a to-do ticked, a timer started, paused or reset, or a box to
    /// type in opened over a note or a to-do list's last line (the keyboard is taken only then; Esc gives it back).
    /// </summary>
    public void TileClicked(WidgetForm form, WidgetRenderer.TileHit hit)
    {
        var cfg = form.Config;
        if (cfg.Tiles is not { } tiles || hit.Tile >= tiles.Count) return;
        // The note or the line being typed in, clicked again: it keeps the keyboard as it is.
        if (_typing is { IsDisposed: false } open && StillTyping(WidgetRenderer.Typing, cfg.Id, hit))
        {
            open.Activate();
            return;
        }
        string kind = tiles[hit.Tile].Kind, id = cfg.Id;
        int index = hit.Tile;
        void OnTile(Action<WidgetTile> change) => Mutate(id, c =>
        {
            if (c.Tiles is { } ts && index < ts.Count && ts[index].Kind == kind) change(ts[index]);
        });
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        switch (hit.Action)
        {
            case WidgetRenderer.HitTodo:
                OnTile(t => { if (t.Todos is { } l && hit.Index < l.Count) l[hit.Index].Done = !l[hit.Index].Done; });
                break;
            case WidgetRenderer.HitGo:
                OnTile(t => WidgetTimer.StartOrPause(t, now));
                Log.Write("widgets", $"{WidgetCatalog.Title(cfg)}: timer started or paused on the desktop");
                break;
            case WidgetRenderer.HitReset:
                OnTile(WidgetTimer.Reset);
                break;
            case WidgetRenderer.HitPlay or WidgetRenderer.HitNext or WidgetRenderer.HitPrevious:
                _media?.Press(hit.Action);
                Log.Write("widgets", $"{WidgetCatalog.Title(cfg)}: media {hit.Action} on the desktop");
                break;
            case WidgetRenderer.HitOpen:
                if (tiles[hit.Tile].Links is { } links && hit.Index < links.Count) OpenLink(links[hit.Index]);
                break;
            case WidgetRenderer.HitNote:
                OpenTyping(form, hit, multiline: true, tiles[hit.Tile].Text ?? "", text => OnTile(t => t.Text = text.Length == 0 ? null : text), null);
                break;
            case WidgetRenderer.HitTodoAdd:
                OpenTyping(form, hit, multiline: false, "", null, text => OnTile(t => (t.Todos ??= []).Add(new TodoItem { Text = text })));
                break;
        }
    }

    /// <summary>
    /// A click on the note or the to-do line that is being typed in. Opened again, its box started from the text as last
    /// saved (every 400 ms, and taken by the widget a moment later), so what was typed just before the click was lost
    /// with the next save (the audit, 2026-11-11).
    /// </summary>
    internal static bool StillTyping(WidgetRenderer.TypingNow? typing, string widgetId, WidgetRenderer.TileHit hit) =>
        hit.Action is WidgetRenderer.HitNote or WidgetRenderer.HitTodoAdd && typing is { } t && t.WidgetId == widgetId && t.Tile == hit.Tile
        && t.Adding == (hit.Action == WidgetRenderer.HitTodoAdd);

    private void OpenTyping(WidgetForm form, WidgetRenderer.TileHit hit, bool multiline, string text, Action<string>? changed, Action<string>? added)
    {
        _typing?.Close();
        string id = form.Config.Id;
        int tile = hit.Tile;
        bool adding = !multiline;
        // The widget draws what's typed, with its caret; the box that takes the keys isn't seen.
        void Draft(string now, int caret)
        {
            WidgetRenderer.Typing = new WidgetRenderer.TypingNow(id, tile, now, caret, adding);
            if (_forms.TryGetValue(id, out var f)) f.Redraw();
        }
        var box = new TileTypingBox(form.ScreenRect(hit), text, multiline, Draft, changed, added);
        _typing = box;
        box.FormClosed += (_, _) =>
        {
            if (_typing == box) _typing = null;
            if (WidgetRenderer.Typing is { } ty && ty.WidgetId == id && ty.Tile == tile)
            {
                WidgetRenderer.Typing = null;
                if (_forms.TryGetValue(id, out var f)) f.Redraw();
            }
        };
        box.Show();
    }

    /// <summary>The wheel over a note or a to-do list: scrolled by a row (a list) or three lines (a note) a notch.</summary>
    public void Scrolled(WidgetForm form, int tile, int notches)
    {
        if (form.Config.Tiles is not { } tiles || tile >= tiles.Count) return;
        int step = tiles[tile].Kind == WidgetTiles.Notes ? 3 : 1;
        var key = (form.Config.Id, tile);
        WidgetRenderer.Scrolls[key] = Math.Max(0, WidgetRenderer.Scrolls.GetValueOrDefault(key) + notches * step);
        form.Redraw();
    }

    /// <summary>Every tick: a timer that has run its length stops at it, and says so once.</summary>
    private void CheckTimers()
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        // Every widget's timers, shown or not: one started and then hidden (Desktop widgets off, the widget switched off)
        // still runs, and still says when it's up (the audit, 2026-11-11: it said nothing until the widget came back).
        foreach (var cfg in getSettings().Widgets.ToList())
        {
            if (cfg.Tiles is not { } tiles) continue;
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (t.Kind != WidgetTiles.Timer || !WidgetTimer.Running(t) || !WidgetTimer.IsUp(t, now)) continue;
                int index = i, minutes = t.Minutes;
                string id = cfg.Id, name = WidgetCatalog.Title(cfg);
                Mutate(id, c => { if (c.Tiles is { } ts && index < ts.Count && ts[index].Kind == WidgetTiles.Timer) WidgetTimer.Stop(ts[index]); });
                Log.Write("widgets", $"{name}: a {minutes}-minute timer is up");
                var (title, body) = NoticeWords.TimerUp(minutes, name);
                TimerUp?.Invoke(title, body);
            }
        }
    }

    /// <summary>The widget is in the corner for now, its saved spot on a screen that isn't there.</summary>
    public bool WaitsForScreen(string id) => _waitingForScreen.Contains(id);

    /// <param name="gameInFront">A game (not a video or another app) is in front.</param>
    /// <param name="fillsScreen">The window in front covers its whole screen (borderless or exclusive).</param>
    /// <param name="exclusive">A Direct3D game is in exclusive fullscreen.</param>
    /// <param name="overlayPlanes">The game's screen has overlay planes (see <see cref="OverlayPlanes"/>).</param>
    /// <param name="gameMonitor">The screen of the window in front, or zero with no game.</param>
    public void Update(WidgetData data, bool gameInFront, bool fillsScreen, bool exclusive, bool overlayPlanes, IntPtr gameMonitor)
    {
        if (_forms.Values.Any(f => ShowsFrameRate(f.Config)))
        {
            // Its own copy: the overlay sets the frame rate on the shared data only when it shows it.
            data = data.Copy();
            data.Frame = Rtss.ReadFrameStats(ForegroundPid());
            data.RtssRunning = data.Frame is not null || Rtss.IsLive();
        }
        // What's playing, for Media tiles: read only while a widget that is on has one.
        bool media = _forms.Values.Any(f => f.Config.Tiles?.Any(t => t.Kind == WidgetTiles.Media) == true);
        if (media && _media is null) _media = MediaWatcher.Start();
        else if (!media && _media is not null)
        {
            _media.Dispose();
            _media = null;
            Rigsight.Core.Log.Write("widgets", "Media: no Media tile on, stopped reading what's playing");
        }
        if (_media is not null)
        {
            data = data.Copy();
            data.Media = _media.Take();
        }
        // The weather for Weather tiles, from the internet, at most every half hour a place.
        WeatherFeed.RefreshInBackground(_forms.Values.SelectMany(f => f.Config.Tiles ?? []));
        _data = data;
        CheckTimers();
        // Decided on every activity tick, so a game switching between windowed and borderless (Alt+Enter) is caught.
        _gameMonitor = _overGames.Update(gameInFront, fillsScreen, exclusive, overlayPlanes, gameMonitor, data.Activity.Name, out var log);
        if (log is not null && _forms.Count > 0) Rigsight.Core.Log.Write("widgets", log);
        UpdateVisibility();
        foreach (var form in _forms.Values)
            form.UpdateData(data);
    }

    /// <summary>Whether a widget shows anything from RivaTuner (frame rate, frame time, 1% low).</summary>
    public static bool ShowsFrameRate(WidgetConfig cfg) =>
        WidgetCatalog.ShownItems(cfg).Any(i => WidgetCatalog.Metric(i.Id) is OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow);

    /// <summary>The process of the window in front (0: none).</summary>
    internal static int ForegroundPid()
    {
        Native.Win32.GetWindowThreadProcessId(Native.Win32.GetForegroundWindow(), out int pid);
        return pid;
    }

    /// <summary>
    /// Widgets step aside for a game in exclusive fullscreen, and for a borderless one on a screen without overlay planes
    /// (see <see cref="WidgetsOverGames"/>), on that screen only, and the overlay takes over: a window over such a game
    /// makes Windows compose its frames (later on screen, G-Sync/FreeSync off). Over a windowed game, a borderless one on
    /// a screen with overlay planes, fullscreen videos and apps they stay, as do widgets on other screens.
    /// </summary>
    private void UpdateVisibility()
    {
        foreach (var form in _forms.Values)
        {
            // A widget on the desktop is under the game anyway: it never steps aside.
            bool show = !form.SitsOver || _gameMonitor == IntPtr.Zero ||
                Native.Win32.MonitorFromWindow(form.Handle, 2 /* MONITOR_DEFAULTTONEAREST */) != _gameMonitor;
            if (show && !form.Visible)
            {
                form.Show();
                form.Redraw();
            }
            else if (!show && form.Visible)
            {
                form.Hide();
            }
        }
    }

    public void Mutate(string id, Action<WidgetConfig> change) =>
        mutateSettings(s =>
        {
            if (s.Widgets.FirstOrDefault(w => w.Id == id) is { } cfg) change(cfg);
        });

    public void ShowMenu(WidgetForm form, Point at)
    {
        var cfg = form.Config;
        var menu = DarkMenu();

        string title = WidgetCatalog.Title(cfg);
        menu.Items.Add(Header(cfg.Style == WidgetStyle.Custom ? title : Lang.T($"{title} widget")));
        menu.Items.Add(new ToolStripSeparator());

        var theme = new ToolStripMenuItem(Lang.T("Theme"));
        foreach (var t in new[] { WidgetTheme.App, WidgetTheme.Dark, WidgetTheme.Grey, WidgetTheme.Light, WidgetTheme.System })
            theme.DropDownItems.Add(Check(ThemeName(t), cfg.Theme == t, () => Mutate(cfg.Id, c => c.Theme = t)));
        menu.Items.Add(theme);

        var background = new ToolStripMenuItem(Lang.T("Background opacity"));
        foreach (var o in new[] { 1.0, 0.9, 0.75, 0.5, 0.25, 0.0 })
            background.DropDownItems.Add(Check(o == 0 ? Lang.C("opacity", "None") : $"{o:P0}", Math.Abs(cfg.BackgroundOpacity - o) < 0.01, () => Mutate(cfg.Id, c => c.BackgroundOpacity = o)));
        menu.Items.Add(background);

        var contentOpacity = new ToolStripMenuItem(Lang.T("Content opacity"));
        foreach (var o in new[] { 1.0, 0.9, 0.75, 0.6, 0.45, 0.3, 0.2 })
            contentOpacity.DropDownItems.Add(Check($"{o:P0}", Math.Abs(cfg.ContentOpacity - o) < 0.01, () => Mutate(cfg.Id, c => c.ContentOpacity = o)));
        menu.Items.Add(contentOpacity);

        var size = new ToolStripMenuItem(Lang.T("Size"));
        foreach (var (label, s) in new[] { (Lang.Key("Small"), 0.8), (Lang.Key("Medium"), 1.0), (Lang.Key("Large"), 1.25), (Lang.Key("Extra large"), 1.5), ("2XL", 2.0), ("3XL", 2.5) })
            size.DropDownItems.Add(Check(Lang.T(label), Math.Abs(cfg.Scale - s) < 0.01, () => Mutate(cfg.Id, c => c.Scale = s)));
        menu.Items.Add(size);

        menu.Items.Add(Check(Lang.T("Grayscale"), cfg.Grayscale, () => Mutate(cfg.Id, c => c.Grayscale = !c.Grayscale)));
        menu.Items.Add(Check(Lang.T("Lock in place (click-through)"), cfg.Locked, () => Mutate(cfg.Id, c => c.Locked = !c.Locked)));
        menu.Items.Add(new ToolStripSeparator());

        // Another screen, at the same corner: only with two screens or more.
        var screens = Screen.AllScreens;
        if (screens.Length > 1)
        {
            var move = new ToolStripMenuItem(Lang.T("Move to"));
            var here = Screen.FromRectangle(form.PanelBounds);
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                string name = screen.Primary ? Lang.T("Main screen") : Lang.T($"Screen {i + 1}");
                move.DropDownItems.Add(Check(name, screen.DeviceName == here.DeviceName, () => MoveTo(form, screen)));
            }
            menu.Items.Add(move);
        }
        menu.Items.Add(Item(Lang.T("Duplicate"), () => Duplicate(cfg)));
        menu.Items.Add(Item(Lang.T("Edit widget…"), () => openWidgetSettings(cfg.Id)));
        menu.Items.Add(Item(Lang.T("Hide this widget"), () => Mutate(cfg.Id, c => c.Enabled = false)));
        menu.Items.Add(Item(Lang.T("Remove"), () => mutateSettings(s => s.Widgets.RemoveAll(w => w.Id == cfg.Id))));

        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        menu.Show(at);
    }

    /// <summary>The widget to the same corner of another screen, a little in from its edges.</summary>
    private void MoveTo(WidgetForm form, Screen screen)
    {
        var from = Screen.FromRectangle(form.PanelBounds).WorkingArea;
        var to = screen.WorkingArea;
        var p = form.PanelBounds;
        double fx = from.Width > p.Width ? (double)(p.Left - from.Left) / (from.Width - p.Width) : 1;
        double fy = from.Height > p.Height ? (double)(p.Top - from.Top) / (from.Height - p.Height) : 0;
        int x = to.Left + (int)Math.Round(Math.Clamp(fx, 0, 1) * Math.Max(0, to.Width - p.Width));
        int y = to.Top + (int)Math.Round(Math.Clamp(fy, 0, 1) * Math.Max(0, to.Height - p.Height));
        form.PanelLocation = new Point(x, y);
        form.Redraw();
        Moved(form.Config.Id);
        Mutate(form.Config.Id, c => { c.X = x; c.Y = y; });
    }

    /// <summary>A copy of a widget, a widget of the person's own, beside it: a built-in one is copied as tiles.</summary>
    private void Duplicate(WidgetConfig cfg) => mutateSettings(s =>
    {
        if (s.Widgets.Count(w => w.Style == WidgetStyle.Custom) >= WidgetCatalog.MaxCustom) return;
        s.Widgets.Add(new WidgetConfig
        {
            Style = WidgetStyle.Custom, Id = WidgetCatalog.NewId(), Name = WidgetCatalog.Title(cfg), Enabled = true,
            Tiles = cfg.Tiles?.Select(t => t.Copy()).ToList() ?? WidgetTiles.FromLayout(cfg),
            X = cfg.X + 24, Y = cfg.Y + 24, Theme = cfg.Theme, Grayscale = cfg.Grayscale, BackgroundOpacity = cfg.BackgroundOpacity,
            ContentOpacity = cfg.ContentOpacity, Scale = cfg.Scale, Accent = cfg.Accent, Font = cfg.Font, Corners = cfg.Corners,
            TileBoxes = cfg.TileBoxes, Gap = cfg.Gap, TileLines = cfg.TileLines,
        });
    });

    private static string ThemeName(WidgetTheme theme) => theme switch
    {
        WidgetTheme.App => Lang.T("Like RigSight"),
        WidgetTheme.Dark => Lang.T("Dark"),
        WidgetTheme.Grey => Lang.T("Gray"),
        WidgetTheme.Light => Lang.T("Light"),
        WidgetTheme.System => Lang.T("System"),
        _ => theme.ToString(),
    };

    /// <summary>Tray submenu listing every widget style.</summary>
    public ToolStripMenuItem BuildTrayMenu()
    {
        var root = new ToolStripMenuItem(Lang.T("Widgets"));
        // Its name in the language in use each time the tray menu opens (the language can change while it runs).
        root.OwnerChanged += (_, _) => { if (root.Owner is ToolStripDropDown owner) owner.Opening += (_, _) => root.Text = Lang.T("Widgets"); };
        root.DropDownOpening += (_, _) =>
        {
            root.DropDownItems.Clear();
            var settings = getSettings();
            // The built-in ones (FPS first, as on the Widgets page), then the user's own.
            var builtIn = settings.Widgets.Where(w => w.Style != WidgetStyle.Custom).OrderBy(w => w.Style != WidgetStyle.Fps).ToList();
            var custom = settings.Widgets.Where(w => w.Style == WidgetStyle.Custom).ToList();
            foreach (var cfg in builtIn) root.DropDownItems.Add(Toggle(cfg));
            if (custom.Count > 0) root.DropDownItems.Add(new ToolStripSeparator());
            foreach (var cfg in custom) root.DropDownItems.Add(Toggle(cfg));
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(Item(Lang.T("Unlock all widgets"), () => mutateSettings(s => s.Widgets.ForEach(w => w.Locked = false))));
            root.DropDownItems.Add(Item(Lang.T("Widget settings…"), () => openWidgetSettings(null)));
        };
        root.DropDownItems.Add("…"); // placeholder so the arrow shows

        ToolStripMenuItem Toggle(WidgetConfig cfg)
        {
            string id = cfg.Id;
            return Check(WidgetCatalog.Title(cfg), cfg.Enabled, () => Mutate(id, c => c.Enabled = !c.Enabled));
        }
        if (root.DropDown is ToolStripDropDownMenu dd) dd.Renderer = new DarkMenuRenderer();
        return root;
    }

    /// <summary>Saves a PNG of every widget style (with current data) for the app's Widgets page.</summary>
    public void RenderPreviews(RigsightSettings settings)
    {
        try
        {
            var dir = Path.Combine(Rigsight.Core.RigsightPaths.DataDir, "previews");
            Directory.CreateDirectory(dir);
            foreach (var cfg in settings.Widgets)
            {
                // Shown over a backdrop in the app, so both opacities are visible there as on the desktop.
                var preview = new WidgetConfig
                {
                    Style = cfg.Style, Id = cfg.Id, Name = cfg.Name, Layout = cfg.Layout, Items = cfg.Items, Theme = cfg.Theme,
                    Grayscale = cfg.Grayscale, BackgroundOpacity = cfg.BackgroundOpacity, ContentOpacity = cfg.ContentOpacity, Scale = 1,
                    Tiles = cfg.Tiles, Accent = cfg.Accent, Font = cfg.Font, Corners = cfg.Corners, TileBoxes = cfg.TileBoxes, Gap = cfg.Gap, TileLines = cfg.TileLines,
                };
                var data = _data;
                // A frame rate only exists with a game in front (never, while the app is): show one as it would look.
                if (ShowsFrameRate(cfg) && data?.Frame is null)
                {
                    data = data?.Copy() ?? new WidgetData();
                    data.Frame = new FrameStats(144, 6.9, 118);
                }
                using var bmp = WidgetRenderer.Render(preview, data, 2f, hover: false, out _);
                PreviewFile.Save(bmp, Path.Combine(dir, $"{cfg.Id}.png"));
            }
        }
        catch (Exception ex)
        {
            Rigsight.Core.Log.Error("widgets", ex);
        }
    }

    public void CloseAll()
    {
        foreach (var f in _forms.Values)
        {
            f.Close();
            f.Dispose();
        }
        _forms.Clear();
    }

    private static ContextMenuStrip DarkMenu() => Ui.DarkMenuRenderer.Create();

    private static ToolStripMenuItem Header(string text) => new(text) { Enabled = false };

    private static ToolStripMenuItem Item(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private static ToolStripMenuItem Check(string text, bool isChecked, Action action)
    {
        var item = Item(text, action);
        item.Checked = isChecked;
        return item;
    }
}

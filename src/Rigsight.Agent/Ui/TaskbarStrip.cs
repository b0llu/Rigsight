using System.Drawing;
using System.Runtime.InteropServices;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Ui;

/// <summary>
/// The taskbar readings inside Windows 11's taskbar, in any of the four styles: as a strip (each part's readings over
/// its name, like the clock's time over its date, grouped as <see cref="TrayParts"/> groups them), in two rows (boxes
/// of two as the person pairs them, see <see cref="TrayParts.Boxes"/>), or as icons, one each or one per part, drawn as
/// the notification area would draw them. It's a see-through window made a child of the
/// taskbar (as TrafficMonitor does), so it moves, hides and shows with it, full-screen games included. Checked on every
/// update: a restarted Explorer (a new taskbar) gets a new window, a tray that grew or shrank moves it.
/// By the clock (<see cref="TraySide.Right"/>) it sits just left of the tray, and never over the apps' buttons: with too
/// little room the strip's graphs get narrower (down to their words' width), then the last readings wait in a "+N" place
/// at the end, which names them when hovered. On the left (<see cref="TraySide.Left"/>) it sits at the taskbar's left
/// end, after Widgets and clear of Start, wherever the centred icons have moved Start to; with no room there (icons
/// aligned left, or too many) it goes next to the clock, and the log says why. Each reading has its look
/// (<see cref="TrayLook"/>) and, with tiles, a tile of its own. A click opens the clicked readings' card (ReadingCard: a
/// reading on the strip, a Two rows box or an icon per part whole, an icon; "+N": the Taskbar page);
/// right-click removes a part's readings, picks a reading's look, moves the readings to the other end, switches the
/// style, or opens the Taskbar page; hovering names the hardware and each reading. When it can't be placed (Windows 10,
/// whose taskbar lays out its buttons itself, or no taskbar) <see cref="Update"/> says so and the readings show as
/// Windows' own tray icons. UI thread only. Since 1.3 each reading shows in its own style and size (see
/// <see cref="StripComposer"/>, which lays them out the same way for the Taskbar page's preview); right-click also shows
/// the reading under the pointer in another style, or every reading in one.
/// </summary>
internal sealed class TaskbarStrip(Action open, Action<IReadOnlyList<string>> remove, Action<TrayStyle> setStyle, Action openPage,
    Action<TraySide> setSide, Action<string, TrayLook> setLook, Action<IReadOnlyList<string>, Rectangle, float> openCard, Func<bool> cardShowing,
    Action<IReadOnlyList<string>, TrayStyle> setStyleOf) : IDisposable
{
    /// <summary>Each reading's right edge within its part, as last drawn (null for a cell not on the strip): a click opens the one under it.</summary>
    private float[]?[] _valueEdges = [];

    /// <summary>Windows 11 (build 22000) and later: the taskbar's buttons leave room beside the tray.</summary>
    public static bool Supported => Environment.OSVersion.Version.Build >= 22000;

    private StripWindow? _window;
    private IntPtr _taskbar;
    private string _drawn = "";
    private Rectangle _placed;
    private Size _size; // the strip as last drawn
    private List<List<TrayReading>> _cells = [];
    private List<TrayStyle> _cellStyles = [];
    private float[] _edges = [];
    private TipWindow? _tip;
    private readonly System.Windows.Forms.Timer _tipDelay = new() { Interval = 400 };
    private string _tipText = "";
    private int _tipX;

    /// <summary>
    /// The tray grows and shrinks as icons come and go (Windows' bell, as any app opens or closes): told the moment
    /// it moves (<see cref="_trayMoved"/>), the strip moves with it, in the same instant, as the clock does. A check
    /// every second as well, in case a move went untold.
    /// </summary>
    private readonly System.Windows.Forms.Timer _follow = new() { Interval = 1000 };
    private IntPtr _hook, _trayWindow;
    private WinEventProc? _trayMoved;
    private float _scale = 1;

    private bool _wired;

    private TraySide _side;
    /// <summary>The style every reading without one of its own has (what "Show all as" leaves ticked).</summary>
    private TrayStyle _style;
    private string _graphsNote = "";
    private TaskbarButtons? _buttons;
    private SynchronizationContext? _ui;
    private IntPtr _taskList;

    /// <summary>Lays the readings out and keeps their graphs and widest shapes (shared with the Taskbar page's preview).</summary>
    private readonly StripComposer _composer = new();

    /// <summary>The readings waiting for room in "+N" (none when all fit), and the index of "+N" among <see cref="_cells"/> (-1: none).</summary>
    private List<TrayReading> _waiting = [];
    private int _moreCell = -1;

    /// <summary>What the room by the clock last made of the readings ("" when all fit as they are): logged when it changes.</summary>
    private string _fitNote = "";

    /// <summary>Why the readings aren't on the left as asked ("" when they are, or weren't asked): logged when it changes.</summary>
    private string? _sideNote;

    /// <summary>The readings waiting for room in "+N" right now (by sensor id; none when all fit), for the Taskbar page.</summary>
    public IReadOnlyList<string> Waiting => [.. _waiting.Select(r => r.Id)];

    /// <summary>
    /// Shows <paramref name="readings"/> in the taskbar, each in its own style and size (<paramref name="style"/>: the one
    /// every reading without its own has), in grayscale when <paramref name="gray"/>, on tiles when <paramref name="tiles"/>,
    /// the strip as roomy as <paramref name="width"/>, two rows' names as one letter when <paramref name="letters"/>, speeds
    /// in bits when <paramref name="speedBits"/>; false when it can't (nothing is shown then).
    /// </summary>
    public bool Update(IReadOnlyList<TrayReading> readings, bool gray = false, TraySide side = TraySide.Right, TrayStyle style = TrayStyle.Strip,
        bool tiles = true, TrayWidth width = TrayWidth.Standard, bool letters = false, bool speedBits = false)
    {
        _side = side;
        _style = style;
        long now = Environment.TickCount64;
        _composer.Remember(readings, now);
        string graphsNote = string.Join(", ", _composer.Graphs.Order(StringComparer.Ordinal));
        if (graphsNote != _graphsNote)
            Log.Write("taskbar", $"Graphs kept for the strip: {_composer.Graphs.Count} ({graphsNote}), of {readings.Count} readings ({string.Join(", ", readings.Select(r => $"{r.Id}={r.Style}/{r.Size}"))})");
        _graphsNote = graphsNote;
        if (readings.Count == 0 || !Supported) { Hide(); return false; }
        if (!_wired)
        {
            _wired = true;
            // Told on its own thread when Start, Widgets or the apps' buttons moved: the strip follows on its own.
            _ui = SynchronizationContext.Current;
            _buttons = new TaskbarButtons(() => _ui?.Post(_ => Follow(), null));
            _follow.Tick += (_, _) => Follow();
            _tipDelay.Tick += (_, _) => ShowTip();
        }
        try
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            IntPtr tray = taskbar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero || !GetWindowRect(taskbar, out var bar) || !GetWindowRect(tray, out var notify)) { Hide(); return false; }
            int barHeight = bar.B - bar.T;
            if (barHeight <= 0 || bar.R - bar.L < barHeight * 4) { Hide(); return false; } // on its side: no room for text

            // A new taskbar (Explorer restarted): a new strip on it.
            if (_window is null || _taskbar != taskbar || !IsWindow(_window.Handle))
            {
                Hide();
                _window = new StripWindow(this);
                _window.CreateControl();
                MakeChild(_window.Handle, taskbar);
                _taskbar = taskbar;
                _follow.Start();
                _taskList = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
                Watch(tray);
            }
            // Where the apps' buttons are, on both sides: the left keeps clear of Start, the right of the last app.
            _buttons!.Watch(taskbar);

            bool light = WidgetRenderer.TaskbarIsLight();
            float scale = GetDpiForWindow(taskbar) / 96f;
            if (scale <= 0) scale = 1;
            _scale = scale;
            var options = new StripComposer.Options(light, gray, tiles, width, letters, speedBits);
            int barWidth = bar.R - bar.L;
            float graphWide = WidgetRenderer.StripGraphWidthFor(barWidth, scale, width);
            var places = _buttons.Latest;

            // Left, as it is, when it fits before Start; else by the clock, fitted to the room the apps leave there.
            int gap = (int)Math.Round(6 * scale);
            int full = _composer.Build(readings, options, graphWide, 0, barHeight, scale, now).Width;
            bool onLeft = side == TraySide.Left && places is not null && LeftRoom(bar, places, gap) is { } left && left.From + full <= left.Until;
            int room = onLeft || places is not { AppsRight: > 0 } ? int.MaxValue : notify.L - gap - (places.AppsRight + gap * 2);
            var (fitWide, wait) = _composer.Fit(readings, options, graphWide, barHeight, scale, room, now);
            var layout = _composer.Build(readings, options, fitWide, wait, barHeight, scale, now);
            _cells = layout.Cells;
            _cellStyles = layout.CellStyles;
            _waiting = layout.Waiting;
            _moreCell = layout.MoreCell;
            NoteFit(full, room, graphWide, fitWide, wait, readings);
            // Drawn again only when something shown changed; placed again whenever the tray moved.
            // A graph moves with every reading, so a strip with one is drawn each time.
            string key = $"{barHeight}|{barWidth}|{scale}|{light}|{gray}|{tiles}|{width}|{letters}|{speedBits}|{fitWide}|{wait}|" + layout.Key;
            Size size = _size;
            if (key != _drawn)
            {
                using var bmp = layout.Render(out _edges, out _valueEdges);
                _size = size = bmp.Size;
                Place(bar, notify, size, scale);
                Push(_window.Handle, bmp);
                _drawn = key;
            }
            else Place(bar, notify, size, scale);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("strip", ex);
            Hide();
            return false;
        }
    }

    /// <summary>An icon's size on a taskbar this tall (see <see cref="StripComposer.IconSize"/>).</summary>
    internal static int IconSize(int height, float scale) => StripComposer.IconSize(height, scale);

    /// <summary>The room each icon takes (see <see cref="StripComposer.IconSlot"/>).</summary>
    internal static int IconSlot(int height, float scale) => StripComposer.IconSlot(height, scale);

    /// <summary>The room a reading needs, whatever its digits (see <see cref="StripComposer.Shape"/>).</summary>
    internal static string Shape(string text) => StripComposer.Shape(text);

    /// <summary>Logs what the room by the clock made of the readings, when that changes (the room, what fitted, who waits).</summary>
    private void NoteFit(int full, int room, float graphWide, float fitWide, int wait, IReadOnlyList<TrayReading> readings)
    {
        string note = fitWide < graphWide || wait > 0
            ? $"{full} px wide, {room} px of room between the apps and the clock: graphs {fitWide:0} px (of {graphWide:0}), {wait} waiting in +N"
              + (wait > 0 ? $" ({string.Join(", ", _waiting.Select(r => r.Id))})" : "")
            : "";
        if (note == _fitNote) return;
        Log.Write("taskbar", note.Length > 0 ? $"Not every reading fits as it is: {note}" : $"Every reading fits as it is ({readings.Count}, {string.Join(", ", readings.Select(r => r.Style).Distinct())})");
        _fitNote = note;
    }

    /// <summary>Asks Windows to say when the taskbar's windows move (on this thread, from its message loop).</summary>
    private void Watch(IntPtr tray)
    {
        Unwatch();
        _trayWindow = tray;
        // Called from Windows' own code: an error here would unwind through it, so none gets out.
        _trayMoved ??= (_, _, hwnd, idObject, _, _, _) =>
        {
            // The tray as it grows; the apps' buttons as they come and go (they move Start, and the room by the clock).
            try
            {
                if (idObject != OBJID_WINDOW) return;
                if (hwnd == _trayWindow) Follow();
                else if (hwnd == _taskList) _buttons?.Check();
            }
            catch (Exception ex) { Rigsight.Core.Log.Write("taskbar", $"Following the tray failed: {ex.Message}"); }
        };
        // Only the thread the tray's window lives on: all of Explorer would send every move of every File Explorer
        // window here, to be thrown away.
        uint thread = GetWindowThreadProcessId(tray, out int explorer);
        _hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _trayMoved, explorer, (int)thread, WINEVENT_OUTOFCONTEXT);
    }

    private void Unwatch()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Between readings: the strip kept beside the tray as it changes size (see <see cref="_follow"/>).</summary>
    private void Follow()
    {
        if (_window is null || _size.IsEmpty) return;
        IntPtr tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(_taskbar, out var bar) || !GetWindowRect(tray, out var notify)) return; // the next reading sorts it out
        Place(bar, notify, _size, _scale);
    }

    /// <summary>
    /// The room on the left, in the bar's own pixels: from after Widgets (the bar's left end without it) to just before
    /// Start; null when Start wasn't found.
    /// </summary>
    private static (int From, int Until)? LeftRoom(RECT bar, TaskbarButtons.Places places, int gap)
    {
        var (start, widgets) = (places.Start, places.Widgets);
        if (start.IsEmpty) return null;
        // Widgets is at the left end only with the icons centred; aligned left, it comes after them.
        int from = (widgets.IsEmpty || widgets.Left > start.Left ? 0 : widgets.Right - bar.L) + gap * 2;
        return (from, start.Left - bar.L - gap * 2);
    }

    /// <summary>
    /// Just left of the tray, a little apart from it, the full height of the bar; on the left, at the bar's left end
    /// (after Widgets) when it fits before Start, else beside the tray after all.
    /// </summary>
    private void Place(RECT bar, RECT notify, Size size, float scale)
    {
        if (_window is null) return;
        int gap = (int)Math.Round(6 * scale);
        int x = notify.L - bar.L - size.Width - gap;
        string note = "";
        if (_side == TraySide.Left)
        {
            // Not shown until it's known where the buttons are: it would show by the clock, then jump.
            if (_buttons?.Latest is not { } places) return;
            if (LeftRoom(bar, places, gap) is not { } left)
                note = "Start wasn't found on the taskbar";
            else if (left.From + size.Width <= left.Until) x = left.From;
            else note = $"no room before Start (needed {size.Width} px, found {Math.Max(0, left.Until - left.From)} px from {left.From} to {left.Until}"
                        + (places.Start.Left - bar.L < size.Width ? ": the taskbar's icons look aligned left)" : ")");
        }
        if (note != _sideNote)
        {
            if (note.Length > 0) Log.Write("taskbar", $"The readings were asked for the left side but stay next to the clock: {note}");
            else if (_sideNote is not null) Log.Write("taskbar", _side == TraySide.Left ? "The readings are on the left side" : "The readings are next to the clock");
            _sideNote = note;
        }
        var at = new Rectangle(x, 0, size.Width, size.Height);
        if (at == _placed) return;
        SetWindowPos(_window.Handle, IntPtr.Zero, at.X, at.Y, at.Width, at.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _placed = at;
    }

    public void Hide()
    {
        _buttons?.Stop();
        _waiting = [];
        _moreCell = -1;
        if (_window is null) return;
        _follow.Stop();
        Unwatch();
        HideTip();
        _window.Dispose();
        _window = null;
        _drawn = "";
        _placed = Rectangle.Empty;
        _size = Size.Empty;
    }

    /// <summary>The part (or icon, or "+N") under the pointer, by the drawn edges (-1 past the last).</summary>
    private int CellIndexAt(int x)
    {
        for (int i = 0; i < _edges.Length && i < _cells.Count; i++)
            if (x < _edges[i]) return i;
        return -1;
    }

    private List<TrayReading>? CellAt(int x) => CellIndexAt(x) is >= 0 and var i ? _cells[i] : null;

    /// <summary>The style the cell at <paramref name="index"/> is drawn in.</summary>
    private TrayStyle StyleAt(int index) => index >= 0 && index < _cellStyles.Count ? _cellStyles[index] : _style;

    private void OnMouse(MouseEventArgs e)
    {
        if (_window is null) return;
        HideTip();
        int index = CellIndexAt(e.X);
        bool more = index >= 0 && index == _moreCell;
        if (e.Button == MouseButtons.Left)
        {
            if (more) openPage();
            else if (index < 0 || !GetWindowRect(_window.Handle, out var strip)) open();
            else
            {
                // What was clicked: a reading on the strip; a Two rows box or an icon per part (all of its readings); an icon.
                var clicked = _cells[index];
                IReadOnlyList<TrayReading> targets = clicked;
                float from = index == 0 ? 0 : _edges[index - 1], to = _edges[index];
                if (StyleAt(index) == TrayStyle.Strip && index < _valueEdges.Length && _valueEdges[index] is { } edges)
                {
                    int i = Array.FindIndex(edges, edge => e.X < edge);
                    if (i < 0) i = edges.Length - 1;
                    if (i >= 0 && i < clicked.Count)
                    {
                        targets = [clicked[i]];
                        if (i > 0) from = edges[i - 1];
                        to = Math.Min(to, edges[i]);
                    }
                }
                openCard([.. targets.Select(t => t.Id)], new Rectangle(strip.L + (int)from, strip.T, Math.Max(1, (int)(to - from)), strip.B - strip.T), _scale);
            }
            return;
        }
        if (e.Button != MouseButtons.Right) return;
        var cell = more ? null : CellAt(e.X);
        var cellStyle = StyleAt(index);
        var menu = DarkMenuRenderer.Create();
        // A box of two rows can hold two parts' readings: each removed on its own.
        IEnumerable<IReadOnlyList<TrayReading>> removals = cell is null ? [] : cellStyle == TrayStyle.TwoRows ? cell.Select(r => (IReadOnlyList<TrayReading>)[r]) : [cell];
        foreach (var gone in removals)
        {
            var removeItem = new ToolStripMenuItem(TrayReadings.RemoveText(gone));
            removeItem.Click += (_, _) => remove([.. gone.Select(r => r.Id)]);
            menu.Items.Add(removeItem);
        }
        // Each reading's look, the one it has ticked: on the strip all of them, on an icon its own (number, fill, ring).
        if (cell is not null && cellStyle is TrayStyle.Strip or TrayStyle.Icons or TrayStyle.Grouped)
            foreach (var r in cellStyle == TrayStyle.Strip ? cell : cell.Take(1))
            {
                string name = r.Name.Length is > 0 and <= 40 ? r.Name : TrayParts.Label(r.Part);
                var item = new ToolStripMenuItem(Lang.T($"Look of {name}"));
                bool strip = cellStyle == TrayStyle.Strip;
                var looks = strip
                    ? TrayParts.LooksFor(r.Kind, TrayParts.IsCpuTotal(r.Kind, r.Part, r.Name) || r.Cores is { Length: > 0 }, r.BothSpeeds, r.LookId is null ? null : r.LookSourceKind)
                    : TrayParts.IconLooksFor(r.Kind, r.LookId is null ? null : r.LookSourceKind);
                foreach (var look in looks)
                {
                    var choice = new ToolStripMenuItem(strip ? TrayParts.LookName(look) : TrayParts.IconLookName(look)) { Checked = (strip ? r.StripLook : r.IconLook) == look };
                    choice.Click += (_, _) => setLook(r.Id, look);
                    item.DropDownItems.Add(choice);
                }
                menu.Items.Add(item);
            }
        // The readings under the pointer in another style.
        if (cell is not null)
        {
            var showThis = new ToolStripMenuItem(cell.Count > 1 ? Lang.T("Show these as") : Lang.T("Show this as"));
            foreach (var style in new[] { TrayStyle.Strip, TrayStyle.TwoRows, TrayStyle.Icons, TrayStyle.Grouped })
            {
                var choice = new ToolStripMenuItem(TrayParts.StyleName(style)) { Checked = style == cellStyle };
                choice.Click += (_, _) => setStyleOf([.. cell.Select(r => r.Id)], style);
                showThis.DropDownItems.Add(choice);
            }
            menu.Items.Add(showThis);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        var sideItem = new ToolStripMenuItem(_side == TraySide.Left ? Lang.T("Move next to the clock") : Lang.T("Move to the left side"));
        sideItem.Click += (_, _) => setSide(_side == TraySide.Left ? TraySide.Right : TraySide.Left);
        menu.Items.Add(sideItem);
        // Every reading in one style (each one's own style goes).
        var showAll = new ToolStripMenuItem(Lang.T("Show all as"));
        bool mixed = _cellStyles.Distinct().Count() > 1;
        foreach (var style in new[] { TrayStyle.Strip, TrayStyle.TwoRows, TrayStyle.Icons, TrayStyle.Grouped })
        {
            var choice = new ToolStripMenuItem(TrayParts.StyleName(style)) { Checked = !mixed && style == _style };
            choice.Click += (_, _) => setStyle(style);
            showAll.DropDownItems.Add(choice);
        }
        menu.Items.Add(showAll);
        var page = new ToolStripMenuItem(Lang.T("Taskbar settings…"));
        page.Click += (_, _) => openPage();
        menu.Items.AddRange([new ToolStripSeparator(), page]);
        menu.Closed += (_, _) => BeginDispose(menu);
        menu.Show(Cursor.Position);
    }

    private static void BeginDispose(ContextMenuStrip menu) =>
        SynchronizationContext.Current?.Post(_ => menu.Dispose(), null);

    /// <summary>The pointer moved: a part's tooltip after a moment's rest on it (another part, another tooltip).</summary>
    private void OnHover(int x)
    {
        if (_window is null) return;
        int index = CellIndexAt(x);
        if (index >= 0 && index == CellIndexAt(_tipX) && _tipText.Length > 0) return;
        HideTip();
        _tipX = x;
        if (index >= 0) _tipDelay.Start();
    }

    /// <summary>
    /// What hovering a part says: its hardware, then each reading in full (as the icons' tooltips); "+N" names the
    /// readings waiting for room, each with its value. Shown above the taskbar, as Windows' own tray tooltips are: over
    /// it, the taskbar (which stays on top) would draw over it.
    /// </summary>
    private void ShowTip()
    {
        _tipDelay.Stop();
        // The card says it all, and the tip would sit over it.
        if (_window is null || cardShowing() || CellIndexAt(_tipX) is not (>= 0 and var index)) return;
        var cell = _cells[index];
        _tipText = index == _moreCell
            ? Lang.T("Waiting for room on the taskbar:") + "\n" + string.Join('\n', cell.Select(r => $"{r.Name}: {Units.Format(r.Kind, r.Value)}"))
            : TrayReadings.Tooltip(cell);
        if (!GetWindowRect(_taskbar, out var bar) || !GetWindowRect(_window.Handle, out var strip)) return;
        _tip ??= new TipWindow();
        _tip.ShowAbove(_tipText, new Point(strip.L + _tipX, bar.T), _scale);
    }

    private void HideTip()
    {
        _tipDelay.Stop();
        _tipText = "";
        _tip?.Hide();
    }

    public void Dispose()
    {
        Hide();
        _buttons?.Dispose();
        _follow.Dispose();
        _tipDelay.Dispose();
        _tip?.Dispose();
    }

    /// <summary>
    /// The strip's tooltip: its own small window, dark like Rigsight's menus, just above the taskbar over the part
    /// pointed at. WinForms' tooltips only show for a program in use, which a taskbar strip never is.
    /// </summary>
    private sealed class TipWindow : Form
    {
        private string _text = "";
        private float _scale = 1;

        public TipWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        /// <summary>Centred over <paramref name="anchor"/> (the taskbar's top edge), a gap above it, kept on the screen.</summary>
        public void ShowAbove(string text, Point anchor, float scale)
        {
            _text = text;
            _scale = scale;
            using var font = new Font("Segoe UI", 9f, GraphicsUnit.Point);
            var textSize = TextRenderer.MeasureText(text, font);
            int padX = (int)Math.Round(9 * scale), padY = (int)Math.Round(6 * scale), gap = (int)Math.Round(8 * scale);
            var size = new Size(textSize.Width + padX * 2, textSize.Height + padY * 2);
            var screen = Screen.FromPoint(anchor).Bounds;
            int x = Math.Clamp(anchor.X - size.Width / 2, screen.Left + gap, screen.Right - gap - size.Width);
            SetWindowPos(Handle, HWND_TOPMOST, x, anchor.Y - gap - size.Height, size.Width, size.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(DarkMenuRenderer.Bg);
            using (var pen = new Pen(DarkMenuRenderer.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using var font = new Font("Segoe UI", 9f, GraphicsUnit.Point);
            int padX = (int)Math.Round(9 * _scale), padY = (int)Math.Round(6 * _scale);
            TextRenderer.DrawText(e.Graphics, _text, font, new Point(padX, padY), DarkMenuRenderer.TextColor);
        }
    }

    /// <summary>A see-through window for the strip: no frame, never activated, never in the taskbar or Alt+Tab.</summary>
    private sealed class StripWindow : Form
    {
        private readonly TaskbarStrip _owner;

        public StripWindow(TaskbarStrip owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MouseUp += (_, e) => _owner.OnMouse(e);
            MouseMove += (_, e) => _owner.OnHover(e.X);
            MouseLeave += (_, _) => _owner.HideTip();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;
    }

    /// <summary>Makes the window a child of the taskbar: it lives inside it from now on.</summary>
    private static void MakeChild(IntPtr window, IntPtr taskbar)
    {
        long style = (long)GetWindowLongPtr(window, GWL_STYLE);
        style = (style & ~(WS_POPUP | WS_CAPTION)) | WS_CHILD;
        SetWindowLongPtr(window, GWL_STYLE, (IntPtr)style);
        SetParent(window, taskbar);
    }

    /// <summary>The drawn strip onto its window, keeping where it is (a child's place is in its parent's terms).</summary>
    private static void Push(IntPtr window, Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var source = new POINT();
            var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(window, screen, IntPtr.Zero, ref size, mem, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const int GWL_STYLE = -16, ULW_ALPHA = 2;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, int process, int thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int process);
    private const long WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000L, WS_CHILD = 0x40000000L;
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}

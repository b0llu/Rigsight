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
/// (<see cref="TrayLook"/>) and, with tiles, a tile of its own. A click opens Rigsight ("+N": the Taskbar page);
/// right-click removes a part's readings, picks a reading's look, moves the readings to the other end, switches the
/// style, or opens the Taskbar page; hovering names the hardware and each reading. When it can't be placed (Windows 10,
/// whose taskbar lays out its buttons itself, or no taskbar) <see cref="Update"/> says so and the readings show as
/// Windows' own tray icons. UI thread only.
/// </summary>
internal sealed class TaskbarStrip(Action open, Action<IReadOnlyList<string>> remove, Action<TrayStyle> setStyle, Action openPage,
    Action<TraySide> setSide, Action<string, TrayLook> setLook) : IDisposable
{
    /// <summary>Windows 11 (build 22000) and later: the taskbar's buttons leave room beside the tray.</summary>
    public static bool Supported => Environment.OSVersion.Version.Build >= 22000;

    private StripWindow? _window;
    private IntPtr _taskbar;
    private string _drawn = "";
    private Rectangle _placed;
    private Size _size; // the strip as last drawn
    private List<List<TrayReading>> _cells = [];
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
    private TrayStyle _style;
    private bool _letters;
    private HashSet<string> _graphIds = [];
    private TaskbarButtons? _buttons;
    private SynchronizationContext? _ui;
    private IntPtr _taskList;

    /// <summary>The readings waiting for room in "+N" (none when all fit), and the index of "+N" among <see cref="_cells"/> (-1: none).</summary>
    private List<TrayReading> _waiting = [];
    private int _moreCell = -1;

    /// <summary>What the room by the clock last made of the readings ("" when all fit as they are): logged when it changes.</summary>
    private string _fitNote = "";

    /// <summary>Why the readings aren't on the left as asked ("" when they are, or weren't asked): logged when it changes.</summary>
    private string? _sideNote;

    /// <summary>Each graph's readings with their times (ms), the last <see cref="TrayParts.GraphSeconds"/> kept.</summary>
    private readonly Dictionary<string, Queue<(long At, float Value)>> _graphs = [];

    /// <summary>Each reading's widest shape so far ("88°", "888 W"): the place kept for it (see <see cref="Shape"/>).</summary>
    private readonly Dictionary<string, string> _widest = [];

    /// <summary>The readings waiting for room in "+N" right now (by sensor id; none when all fit), for the Taskbar page.</summary>
    public IReadOnlyList<string> Waiting => [.. _waiting.Select(r => r.Id)];

    /// <summary>
    /// Shows <paramref name="readings"/> in the taskbar as <paramref name="style"/>, in grayscale when <paramref name="gray"/>,
    /// on tiles when <paramref name="tiles"/>, the strip as roomy as <paramref name="width"/>, two rows' names as one letter
    /// when <paramref name="letters"/>; false when it can't (nothing is shown then).
    /// </summary>
    public bool Update(IReadOnlyList<TrayReading> readings, bool gray = false, TraySide side = TraySide.Right, TrayStyle style = TrayStyle.Strip,
        bool tiles = true, TrayWidth width = TrayWidth.Standard, bool letters = false)
    {
        _side = side;
        _style = style;
        _letters = letters;
        // Only the strip draws graphs (the icons show numbers alone); every look but the number keeps the last minutes,
        // as a short taskbar draws the fill-up looks as a graph.
        HashSet<string> graphIds = style == TrayStyle.Strip ? [.. readings.Where(r => r.StripLook != TrayLook.Number).Select(r => r.Id)] : [];
        if (!graphIds.SetEquals(_graphIds))
            Log.Write("taskbar", $"Graphs on the strip: {graphIds.Count} ({string.Join(", ", graphIds)}), of {readings.Count} readings ({string.Join(", ", readings.Select(r => r.Id))})");
        _graphIds = graphIds;
        Remember(readings);
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
            var look = new StripLook(tiles, width, light);
            int barWidth = bar.R - bar.L;
            float graphWide = WidgetRenderer.StripGraphWidthFor(barWidth, scale, width);
            var places = _buttons.Latest;

            // Left, as it is, when it fits before Start; else by the clock, fitted to the room the apps leave there.
            int gap = (int)Math.Round(6 * scale);
            int full = Build(readings, light, gray, graphWide, look, 0).Width(barHeight, scale);
            bool onLeft = side == TraySide.Left && places is not null && LeftRoom(bar, places, gap) is { } left && left.From + full <= left.Until;
            int room = onLeft || places is not { AppsRight: > 0 } ? int.MaxValue : notify.L - gap - (places.AppsRight + gap * 2);
            var (fitWide, wait) = Fit(readings, light, gray, barHeight, scale, graphWide, look, room);
            var drawing = Build(readings, light, gray, fitWide, look, wait);
            NoteFit(full, room, graphWide, fitWide, wait, readings);
            // Drawn again only when something shown changed; placed again whenever the tray moved.
            // A graph moves with every reading, so a strip with one is drawn each time.
            string key = $"{style}|{barHeight}|{barWidth}|{scale}|{light}|{gray}|{tiles}|{width}|{letters}|{fitWide}|{wait}|" + drawing.Key;
            Size size = _size;
            if (key != _drawn)
            {
                using var bmp = drawing.Render(barHeight, scale, out _edges);
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

    /// <summary>
    /// What will be drawn: the cells (parts on the strip, icons otherwise, "+N" last when readings wait), what tells a
    /// change, how wide it is and how to draw it, at a height and scale.
    /// </summary>
    private sealed record Drawing(string Key, Func<int, float, int> Width, Func<int, float, (Bitmap Bitmap, float[] Edges)> Draw)
    {
        public Bitmap Render(int height, float scale, out float[] edges)
        {
            var (bmp, e) = Draw(height, scale);
            edges = e;
            return bmp;
        }
    }

    /// <summary>
    /// Lays the readings out as the style draws them, the last <paramref name="wait"/> of them (in the order they are
    /// shown) left for "+N"; sets <see cref="_cells"/>, <see cref="_waiting"/> and <see cref="_moreCell"/>.
    /// </summary>
    private Drawing Build(IReadOnlyList<TrayReading> readings, bool light, bool gray, float graphWide, StripLook look, int wait)
    {
        var groups = _style switch
        {
            TrayStyle.Strip => TrayParts.Group(readings, r => (r.Group, r.Kind), int.MaxValue),
            TrayStyle.TwoRows => TrayParts.Boxes(readings, r => r.Alone),
            _ => TrayReadings.Icons(readings, _style == TrayStyle.Grouped),
        };
        // The order they're shown in, so the last ones are the ones to wait.
        var shown = groups.SelectMany(g => g).ToList();
        _waiting = wait > 0 ? shown.Skip(Math.Max(0, shown.Count - wait)).ToList() : [];
        var waiting = _waiting.Select(r => r.Id).ToHashSet();
        _cells = [.. groups.Select(g => g.Where(r => !waiting.Contains(r.Id)).ToList()).Where(g => g.Count > 0)];
        _moreCell = _waiting.Count > 0 ? _cells.Count : -1;
        if (_moreCell >= 0) _cells.Add(_waiting);
        var cells = _cells;
        int moreCell = _moreCell, waitCount = _waiting.Count;
        var ink = light ? Color.Black : Color.White;
        var neutral = Color.FromArgb(TrayColors.NeutralTileAlpha(light), ink);

        if (_style == TrayStyle.Strip)
        {
            var names = TrayParts.Names([.. cells.Take(moreCell < 0 ? cells.Count : moreCell).Select(c => c[0].Part)]);
            var strip = cells.Select((c, i) =>
            {
                if (i == moreCell)
                    return new StripCell("", ink, [new StripValue($"+{waitCount}", ink, Lang.T("more"), "+88", LabelColor: Color.FromArgb(160, ink),
                        Tile: look.Tiles ? neutral : null, More: true)]);
                var labels = TrayParts.StripLabels([.. c.Select(r => r.Short)], names[i]);
                return new StripCell(names[i], c[0].NameOn(light, gray), [.. c.Select((r, j) => Value(r, light, gray, labels[j], look.Tiles))]);
            }).ToList();
            string key = string.Join('|', strip.Select(c => c.Name + ":" + string.Join(',', c.Values.Select(v => v.Text + v.Label + v.Color.ToArgb() + "/" + v.LabelColor?.ToArgb()
                + v.Look + v.Fill.ToString("0.###") + v.Tile?.ToArgb() + (v.Graph is { } graph ? string.Join(';', graph.Points.Select(pt => $"{pt.X:0.###},{pt.Y:0.###}")) : "")))));
            return new Drawing(key, (height, scale) => WidgetRenderer.MeasureStrip(strip, height, scale, graphWide, look),
                (height, scale) => (WidgetRenderer.RenderStrip(strip, height, scale, out var edges, graphWide, look), edges));
        }

        if (_style == TrayStyle.TwoRows)
        {
            var columns = TwoRowColumns(cells, moreCell, waitCount, readings, light, gray, look.Tiles);
            string key = string.Join('|', columns.Select(c => c.Tile?.ToArgb() + ":" + string.Join(',', c.Lines.Select(l => $"{l.Label}{l.LabelColor.ToArgb()}{l.Text}{l.Color.ToArgb()}{l.Reserve}"))));
            return new Drawing(key, (height, scale) => WidgetRenderer.MeasureTwoRows(columns, height, scale, look),
                (height, scale) => (WidgetRenderer.RenderTwoRows(columns, height, scale, out var edges, look), edges));
        }

        // Icons, as the notification area would draw them, side by side about as far apart as Windows' own.
        var icons = cells.Select((c, i) => i == moreCell
            ? ((IReadOnlyList<(string Text, Color Color)>)[($"+{waitCount}", ink)], look.Tiles ? neutral : (Color?)null)
            : ((IReadOnlyList<(string Text, Color Color)>)TrayReadings.Lines(c, light, gray), TrayReadings.Tile(c, light, gray, look.Tiles))).ToList();
        string iconKey = string.Join('|', icons.Select(ic => string.Join(',', ic.Item1.Select(l => l.Text + l.Color.ToArgb())) + "/" + ic.Item2?.ToArgb()));
        return new Drawing(iconKey, (height, scale) => IconSlot(height, scale) * icons.Count, (height, scale) =>
        {
            int size = IconSize(height, scale), slot = IconSlot(height, scale);
            return (WidgetRenderer.RenderIconRow(icons, height, size, slot, out var edges), edges);
        });
    }

    /// <summary>
    /// Two rows' boxes as drawn: each reading's word (see <see cref="TrayParts.BoxLabel"/>; a part's name numbered when it
    /// comes twice, "GPU 2") in its name's colour and its number in its own, on the box's tile (the first one picked in
    /// it, else the top's); "+N" last, over "more".
    /// </summary>
    private List<TwoRowColumn> TwoRowColumns(List<List<TrayReading>> cells, int moreCell, int waitCount, IReadOnlyList<TrayReading> readings,
        bool light, bool gray, bool tiles)
    {
        var groups = readings.Select(r => r.Group).Distinct().ToList();
        var partNames = TrayParts.Names([.. groups.Select(g => readings.First(r => r.Group == g).Part)]);
        var nameOf = groups.Select((g, i) => (g, partNames[i])).ToDictionary(x => x.g, x => x.Item2);
        var ink = light ? Color.Black : Color.White;
        return [.. cells.Select((box, i) =>
        {
            if (i == moreCell)
                return new TwoRowColumn([new TwoRowLine("", ink, $"+{waitCount}", ink, "+88"), new TwoRowLine(Lang.T("more"), Color.FromArgb(160, ink), "", ink)],
                    tiles ? Color.FromArgb(TrayColors.NeutralTileAlpha(light), ink) : null);
            var lines = box.Select((r, j) =>
            {
                string text = Units.StripText(r.Kind, r.Value), shape = Shape(text);
                if (!_widest.TryGetValue(r.Id, out var widest) || shape.Length > widest.Length) _widest[r.Id] = widest = shape;
                string label = r.Hardware.Length == 0 ? "" // not found yet: the hover names it
                    : TrayParts.BoxLabel(nameOf[r.Group], r.Group, r.Kind, r.Short, r.OwnShort, j == 0 ? null : box[0].Group, _letters);
                return new TwoRowLine(label, r.NameOn(light, gray), text, r.NumberOn(light, gray), widest);
            }).ToList();
            return new TwoRowColumn(lines, tiles ? box.FirstOrDefault(r => r.TileColor is not null, box[0]).TileOn(light, gray, icon: false) : null);
        })];
    }

    /// <summary>
    /// An icon's size (see <see cref="TrayParts.IconSize"/>: 24 at 100%, 36 at 150%), kept clear of a short taskbar's top
    /// and bottom.
    /// </summary>
    internal static int IconSize(int height, float scale) =>
        Math.Max(1, Math.Min((int)Math.Round(TrayParts.IconSize * scale), height - (int)Math.Round(12 * scale)));

    /// <summary>The room each icon takes: itself and the gap to the next.</summary>
    internal static int IconSlot(int height, float scale) => IconSize(height, scale) + (int)Math.Round(TrayParts.IconGap * scale);

    /// <summary>
    /// The readings fitted to <paramref name="room"/> pixels: the strip's graphs narrower first, as wide as still fits down
    /// to their words' width, then the fewest last readings waiting in "+N" (at least "+N" itself shows). Each found by
    /// halving, a handful of measurements, not one a pixel: this runs with every reading.
    /// </summary>
    private (float GraphWide, int Wait) Fit(IReadOnlyList<TrayReading> readings, bool light, bool gray, int height, float scale, float graphWide, StripLook look, int room)
    {
        if (room == int.MaxValue) return (graphWide, 0);
        int Width(float wide, int wait) => Build(readings, light, gray, wide, look, wait).Width(height, scale);
        float graph = graphWide;
        if (_style == TrayStyle.Strip && graph > 0 && Width(graph, 0) > room)
        {
            float lo = 0, hi = graph;
            if (Width(0, 0) > room) hi = 0;
            else while (hi - lo > 1) { float mid = (lo + hi) / 2; if (Width(mid, 0) <= room) lo = mid; else hi = mid; }
            graph = hi == 0 ? 0 : MathF.Floor(lo);
        }
        if (Width(graph, 0) <= room) return (graph, 0);
        int fewest = 1, most = readings.Count;
        while (fewest < most) { int mid = (fewest + most) / 2; if (Width(graph, mid) <= room) most = mid; else fewest = mid + 1; }
        return (graph, fewest);
    }

    /// <summary>Logs what the room by the clock made of the readings, when that changes (the room, what fitted, who waits).</summary>
    private void NoteFit(int full, int room, float graphWide, float fitWide, int wait, IReadOnlyList<TrayReading> readings)
    {
        string note = fitWide < graphWide || wait > 0
            ? $"{full} px wide, {room} px of room between the apps and the clock: graphs {fitWide:0} px (of {graphWide:0}), {wait} waiting in +N"
              + (wait > 0 ? $" ({string.Join(", ", _waiting.Select(r => r.Id))})" : "")
            : "";
        if (note == _fitNote) return;
        Log.Write("taskbar", note.Length > 0 ? $"Not every reading fits as it is: {note}" : $"Every reading fits as it is ({readings.Count}, {_style})");
        _fitNote = note;
    }

    private StripValue Value(TrayReading r, bool light, bool gray, string label, bool tiles)
    {
        string text = Units.StripText(r.Kind, r.Value);
        string shape = Shape(text);
        if (!_widest.TryGetValue(r.Id, out var widest) || shape.Length > widest.Length) _widest[r.Id] = widest = shape;
        // A graph behind the number in its name's colour (its part's, or the one picked; grayscale: the taskbar's text colour).
        var name = r.NameOn(light, gray);
        StripGraph? graph = _graphIds.Contains(r.Id)
            ? new StripGraph(GraphPoints(r), gray ? WidgetRenderer.TrayColor(SensorKind.Load, 0, light, gray: true) : name)
            : null;
        return new StripValue(text, r.NumberOn(light, gray), label, widest, graph, name, r.StripLook, TrayParts.Fill(r.Value),
            tiles ? r.TileOn(light, gray, icon: false) : null);
    }

    /// <summary>Keeps each graph's reading (and forgets the readings no longer drawn as graphs).</summary>
    private void Remember(IReadOnlyList<TrayReading> readings)
    {
        long now = Environment.TickCount64;
        foreach (var gone in _graphs.Keys.Where(id => !_graphIds.Contains(id) || readings.All(r => r.Id != id)).ToList()) _graphs.Remove(gone);
        foreach (var r in readings)
        {
            if (!_graphIds.Contains(r.Id)) continue;
            if (!_graphs.TryGetValue(r.Id, out var points)) _graphs[r.Id] = points = new();
            points.Enqueue((now, r.Value is double v && double.IsFinite(v) ? (float)v : float.NaN));
            while (points.Count > 0 && now - points.Peek().At > TrayParts.GraphSeconds * 1000L) points.Dequeue();
        }
    }

    /// <summary>A graph's points, across its two minutes (now at the right) and up its range (see <see cref="TrayParts.GraphRange"/>).</summary>
    private List<PointF> GraphPoints(TrayReading r)
    {
        if (!_graphs.TryGetValue(r.Id, out var points) || points.Count == 0) return [];
        long now = Environment.TickCount64, window = TrayParts.GraphSeconds * 1000L;
        var (lo, hi) = TrayParts.GraphRange(r.Kind, [.. points.Select(p => p.Value)]);
        return [.. points.Select(p => new PointF(1 - (now - p.At) / (float)window,
            float.IsFinite(p.Value) && hi > lo ? (float)((p.Value - lo) / (hi - lo)) : float.NaN))];
    }

    /// <summary>
    /// The room a reading needs, whatever its digits: each one an 8, the widest ("56°" → "88°", "4.5 GHz" → "8.8 GHz"),
    /// and a whole number at least two ("9%" → "88%"), so the everyday change from one digit to two moves nothing.
    /// </summary>
    internal static string Shape(string text)
    {
        var shape = new string([.. text.Select(ch => char.IsDigit(ch) ? '8' : ch)]);
        int digits = shape.TakeWhile(ch => ch == '8').Count();
        bool whole = digits == shape.Length || shape[digits] != '.'; // "4.5 GHz" is as wide as it gets
        return digits == 1 && whole ? "8" + shape : shape;
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

    private void OnMouse(MouseEventArgs e)
    {
        if (_window is null) return;
        HideTip();
        int index = CellIndexAt(e.X);
        bool more = index >= 0 && index == _moreCell;
        if (e.Button == MouseButtons.Left) { if (more) openPage(); else open(); return; }
        if (e.Button != MouseButtons.Right) return;
        var cell = more ? null : CellAt(e.X);
        var menu = DarkMenuRenderer.Create();
        // A box of two rows can hold two parts' readings: each removed on its own.
        IEnumerable<IReadOnlyList<TrayReading>> removals = cell is null ? [] : _style == TrayStyle.TwoRows ? cell.Select(r => (IReadOnlyList<TrayReading>)[r]) : [cell];
        foreach (var gone in removals)
        {
            var removeItem = new ToolStripMenuItem(TrayReadings.RemoveText(gone));
            removeItem.Click += (_, _) => remove([.. gone.Select(r => r.Id)]);
            menu.Items.Add(removeItem);
        }
        // On the strip, each reading's look, the one it has ticked.
        if (cell is not null && _style == TrayStyle.Strip)
            foreach (var r in cell)
            {
                string name = r.Name.Length is > 0 and <= 40 ? r.Name : TrayParts.Label(r.Part);
                var item = new ToolStripMenuItem(Lang.T($"Look of {name}"));
                foreach (var look in TrayParts.LooksFor(r.Kind))
                {
                    var choice = new ToolStripMenuItem(TrayParts.LookName(look)) { Checked = r.StripLook == look };
                    choice.Click += (_, _) => setLook(r.Id, look);
                    item.DropDownItems.Add(choice);
                }
                menu.Items.Add(item);
            }
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        var sideItem = new ToolStripMenuItem(_side == TraySide.Left ? Lang.T("Move next to the clock") : Lang.T("Move to the left side"));
        sideItem.Click += (_, _) => setSide(_side == TraySide.Left ? TraySide.Right : TraySide.Left);
        menu.Items.Add(sideItem);
        foreach (var (style, words) in new[] { (TrayStyle.Strip, Lang.T("Show as a strip")), (TrayStyle.TwoRows, Lang.T("Show in two rows")),
                     (TrayStyle.Icons, Lang.T("Show an icon each")), (TrayStyle.Grouped, Lang.T("Show an icon per part")) })
        {
            if (style == _style) continue;
            var item = new ToolStripMenuItem(words);
            item.Click += (_, _) => setStyle(style);
            menu.Items.Add(item);
        }
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
        if (_window is null || CellIndexAt(_tipX) is not (>= 0 and var index)) return;
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

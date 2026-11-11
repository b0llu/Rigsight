using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// A tile on the editor's canvas and in its list of tiles: its name, what it shows in a few words, its icon, and its
/// box on the canvas at the zoom in use.
/// </summary>
public sealed partial class TileBox(int index, string name, string detail = "", string glyph = "") : ObservableObject
{
    public int Index { get; } = index;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public string Glyph { get; } = glyph;
    [ObservableProperty] private double _left;
    [ObservableProperty] private double _top;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A kind of tile in the editor's list, ready to add.</summary>
public sealed record TileChoice(string Kind, string Name, string Line, string Group);

/// <summary>A reading to pick for a tile: its identifier (an overlay reading's name or "sensor:…") and its name.</summary>
public sealed record ReadingChoice(string Id, string Name);

/// <summary>One of a few choices shown by name, its value as stored.</summary>
public sealed record Choice(string Value, string Name);

/// <summary>A reading on a line of readings or a graph, in the editor.</summary>
public sealed record TileReadingRow(int Index, string Name);

/// <summary>A line of a to-do list, in the editor.</summary>
public sealed record TodoRow(int Index, string Text, bool Done);

/// <summary>One of a shortcuts tile's apps, files, folders or links, as the editor lists it.</summary>
public sealed record LinkRow(int Index, string Name, string Where, string Glyph);

/// <summary>
/// The widget editor, a page of its own (approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U, after the user's "do web search on the
/// create widget/edit side bar, i feel we can show it much better way"): the widget on top at its real size or fitted,
/// the tiles to add beside it, and every setting under it, for the picked tile, the look, the shape and the desktop.
/// Every change is saved at once, as the rest of the app does, and can be undone while the editor is open.
/// </summary>
public sealed partial class WidgetEditorViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly LiveData? _live;
    private readonly Action _close;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private WidgetConfig _w;
    /// <summary>Each change made here: the widget just before it and just after it (see <see cref="Rebase"/>).</summary>
    private readonly Stack<(string Before, string After)> _undo = new(), _redo = new();

    private readonly Action<string, int, int>? _moved;
    private readonly Action? _remove;

    /// <param name="moved">A spot picked here (Place), sent on to the desktop: settings from the app never move a widget there.</param>
    /// <param name="remove">Remove, done by the Widgets page (which says what was removed, with Undo); none: removed here.</param>
    public WidgetEditorViewModel(string id, SettingsModel settings, LiveData? live, Action close, Action<string, int, int>? moved = null, Action? remove = null)
    {
        Id = id;
        _moved = moved;
        _remove = remove;
        _settings = settings;
        _live = live;
        _close = close;
        _w = Clone(settings.Current.Widgets.First(w => w.Id == id));
        _opened = Clone(_w);
        // A widget drawn as before the box came is shown as the tiles it becomes (saved as tiles at its first change).
        if (_w.Tiles is null)
        {
            _w.Tiles = WidgetTiles.FromLayout(_w);
            _w.Layout = null;
        }
        Readings = MakeReadings();
        TileChoices = [.. WidgetTiles.Kinds.Select(k => new TileChoice(k.Id, WidgetTiles.NameOf(k), WidgetTiles.LineOf(k), k.Group))];
        TileChips = [.. new[] { WidgetsViewModel.AllGroup }.Concat(WidgetTiles.Kinds.Select(k => k.Group).Distinct())
            .Select(g => new WidgetGroupChip(g, g == WidgetsViewModel.AllGroup ? TileChoices.Count : TileChoices.Count(c => c.Group == g)))];
        TileChips[0].IsSelected = true;
        FilterTiles();
        // Nothing picked at first: the panel shows the whole widget, and the list of tiles says what there is to pick.
        Selected = -1;
        Rebuild();
        // A clock on the widget moves on: the picture is drawn again each second while the editor is open.
        _tick.Tick += (_, _) => Draw();
        _tick.Start();
    }

    public string Id { get; }

    public void Close() => _tick.Stop();

    internal static WidgetConfig Clone(WidgetConfig w) =>
        JsonSerializer.Deserialize<WidgetConfig>(JsonSerializer.Serialize(w, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    private string Snapshot() => JsonSerializer.Serialize(_w, SettingsStore.JsonOptions);

    /// <summary>
    /// A change to the widget: kept for Undo, saved at once, drawn again. A widget drawn as before the box came becomes
    /// tiles at its first change (it looks the same).
    /// </summary>
    private void Change(Action<WidgetConfig> change)
    {
        TakeSaved();
        string before = Snapshot();
        _redo.Clear();
        if (_w.Tiles is null)
        {
            _w.Tiles = WidgetTiles.FromLayout(_w);
            _w.Layout = null;
        }
        change(_w);
        WidgetTiles.Clean(_w);
        _undo.Push((before, Snapshot()));
        Commit();
    }

    /// <summary>
    /// The widget as it's saved now, before a change here: while the editor is open it can be dragged on the desktop or
    /// on the map, or changed from its menu on the desktop, and a change here doesn't put any of that back (the user,
    /// 2026-11-10: a calendar dragged away kept going back where it was when the editor opened).
    /// </summary>
    private void TakeSaved()
    {
        if (_settings.Current.Widgets.FirstOrDefault(w => w.Id == Id) is not { } now) return;
        var tiles = _w.Tiles;
        _w = Clone(now);
        // Drawn as before the box came: the tiles it became here, until its first change saves them.
        if (_w.Tiles is null)
        {
            _w.Tiles = tiles ?? WidgetTiles.FromLayout(_w);
            _w.Layout = null;
        }
    }

    /// <summary>Undo and Redo bring back the widget's look and tiles, not where it is nor its switches, which aren't undone here.</summary>
    private void KeepSavedPlace()
    {
        if (_settings.Current.Widgets.FirstOrDefault(w => w.Id == Id) is not { } now) return;
        (_w.X, _w.Y, _w.Enabled, _w.Locked) = (now.X, now.Y, now.Enabled, now.Locked);
    }

    private void Commit()
    {
        WidgetTiles.Clean(_w);
        var saved = Clone(_w);
        _settings.Update(s =>
        {
            int i = s.Widgets.FindIndex(w => w.Id == Id);
            if (i >= 0) s.Widgets[i] = saved;
        });
        if (Selected >= (_w.Tiles?.Count ?? 0)) Selected = (_w.Tiles?.Count ?? 0) - 1;
        Rebuild();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Undo takes back what the change itself did, on the widget as it's saved now: what was done to it meanwhile on the
    /// desktop (a note typed, a to-do ticked, a timer started or run out) stays (audit 2026-11-11: it all went back to
    /// how it was before the change, and a timer that had run out said so again).
    /// </summary>
    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        TakeSaved();
        var step = _undo.Pop();
        _redo.Push(step);
        _w = Rebase(step.After, step.Before, Snapshot());
        KeepSavedPlace();
        Commit();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        TakeSaved();
        var step = _redo.Pop();
        _undo.Push(step);
        _w = Rebase(step.Before, step.After, Snapshot());
        KeepSavedPlace();
        Commit();
    }

    /// <summary><paramref name="now"/> with the change from <paramref name="from"/> to <paramref name="to"/> made on it (see <see cref="SettingsMerge"/>).</summary>
    private static WidgetConfig Rebase(string from, string to, string now) =>
        (SettingsMerge.Merge(JsonNode.Parse(from), JsonNode.Parse(to), JsonNode.Parse(now)) ?? JsonNode.Parse(to))!.Deserialize<WidgetConfig>(SettingsStore.JsonOptions)!;

    /// <summary>Everything the page shows from the widget, after it changed.</summary>
    private void Rebuild()
    {
        var tiles = _w.Tiles ?? [];
        TileBoxes.Clear();
        for (int i = 0; i < tiles.Count; i++)
            TileBoxes.Add(new TileBox(i, NameOf(tiles[i]), DetailOf(tiles[i]), GlyphOf(tiles[i].Kind)) { IsSelected = i == Selected });
        Draw();
        ShowAll();
    }

    /// <summary>
    /// Everything shown follows the widget and the picked tile. The ready sizes and a reading's looks are lists made for
    /// the tile picked, so their pick is said again once the new lists are in: told at once with the rest, a list could
    /// take its pick before its new items, find nothing to light, and show none (a tile at a ready size showed none lit).
    /// </summary>
    private void ShowAll()
    {
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(TilePreset));
        OnPropertyChanged(nameof(Look));
    }

    // ── The widget's name, and the bar at the top ───────────────────────

    public string Title
    {
        get => WidgetCatalog.Title(_w);
        set
        {
            string name = (value ?? "").Trim();
            if (name.Length == 0 || name == Title) return;
            Change(w => w.Name = name.Length > WidgetCatalog.MaxNameLength ? name[..WidgetCatalog.MaxNameLength] : name);
        }
    }

    public bool Enabled
    {
        get => _w.Enabled;
        set
        {
            if (value == _w.Enabled) return;
            TakeSaved();
            _w.Enabled = value;
            Commit();
        }
    }

    [RelayCommand]
    private void Back() => _close();

    /// <summary>The widget as it was when the editor opened, and whether it had no tiles then (a new one).</summary>
    internal WidgetConfig Opened => _opened;
    private readonly WidgetConfig _opened;

    public bool StartedEmpty => Opened.Tiles is { Count: 0 };

    /// <summary>A copy can be made while there's room for another widget of the person's own (200 at most).</summary>
    public bool CanDuplicate => _settings.Current.Widgets.Count(w => w.Style == WidgetStyle.Custom) < WidgetCatalog.MaxCustom;

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void Duplicate()
    {
        // As it is now: moved, typed in or ticked on the desktop since the editor last saved it.
        TakeSaved();
        var copy = Clone(_w);
        copy.Id = WidgetCatalog.NewId();
        copy.Style = WidgetStyle.Custom;
        copy.Name = Title;
        copy.Tiles ??= WidgetTiles.FromLayout(_w);
        copy.Layout = null;
        (copy.X, copy.Y) = (_w.X + 24, _w.Y + 24);
        _settings.Update(s => s.Widgets.Add(copy));
        _close();
    }

    /// <summary>Takes the widget away for good (a built-in one too: the list to add from has it again).</summary>
    [RelayCommand]
    private void Remove()
    {
        if (_remove is not null)
        {
            _remove();
            return;
        }
        _settings.Update(s => s.Widgets.RemoveAll(w => w.Id == Id));
        _close();
    }

    // ── The canvas ──────────────────────────────────────────────────────

    public ObservableCollection<TileBox> TileBoxes { get; } = [];

    public bool IsEmpty => _w.Tiles is { Count: 0 };

    [ObservableProperty] private ImageSource? _picture;

    /// <summary>The screen's scale (the view sets it), so the picture is drawn pixel for pixel.</summary>
    public double ScreenScale { get; set; } = 1;

    /// <summary>The canvas' width, as the view lays it out.</summary>
    public double CanvasWidth
    {
        get => _canvasWidth;
        set
        {
            if (Math.Abs(value - _canvasWidth) < 0.5) return;
            _canvasWidth = value;
            Draw();
        }
    }
    private double _canvasWidth = 700;

    public const double CanvasHeight = 520;

    /// <summary>
    /// Fit: the whole widget filling the view, made smaller or (up to twice) bigger. 100%: its real size, scrolling where
    /// it's bigger. (Fit only ever made a widget smaller, so for one that fits the two looked the same: the user,
    /// 2026-11-11, "this no longer works".)
    /// </summary>
    public bool Fit
    {
        get => _fit;
        set
        {
            if (SetProperty(ref _fit, value)) Draw();
        }
    }
    private bool _fit = true;

    /// <summary>The stand-in for the wallpaper behind the widget: dark or light.</summary>
    [ObservableProperty] private bool _onLight;

    public double Zoom { get; private set; } = 1;
    public double PictureLeft { get; private set; }
    public double PictureTop { get; private set; }
    public double PictureWidth { get; private set; }
    public double PictureHeight { get; private set; }
    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }

    /// <summary>"568 × 200 pixels at size M", with whether it's bigger than the main screen and the zoom in use.</summary>
    public string SizeLine
    {
        get
        {
            if (IsEmpty) return Lang.T("No tiles yet");
            var (w, h) = WidgetPictures.SizeOf(_w);
            var main = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            bool wide = w > main.Width, tall = h > main.Height;
            string size = Lang.T($"{w} × {h} pixels at size M");
            if (wide && tall) size = Lang.T($"{w} × {h} pixels at size M, bigger than your main screen");
            else if (wide) size = Lang.T($"{w} × {h} pixels at size M, wider than your main screen");
            else if (tall) size = Lang.T($"{w} × {h} pixels at size M, taller than your main screen");
            return Math.Abs(Zoom - 1) > 0.005 ? Lang.T($"{size}, shown at {Math.Round(Zoom * 100):0}%") : size;
        }
    }

    private void Draw()
    {
        Layout();
        // Drawn at the size it's shown, so a widget shown bigger is as sharp as one shown at its size.
        Picture = IsEmpty ? null : WidgetPictures.Draw(_w, _live, _settings.Current, (float)(ScreenScale * Math.Max(1, Zoom)));
        MarkPlace();
        // A weather tile shows its weather once it has come (asked for at most every half hour a place).
        WeatherFeed.RefreshInBackground(_w.Tiles ?? [], () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Draw));
    }

    /// <summary>Where the widget and each tile's box sit on the canvas, at the zoom in use.</summary>
    private void Layout()
    {
        var (w, h) = IsEmpty ? (0, 0) : WidgetPictures.SizeOf(_w);
        double cw = Math.Max(200, CanvasWidth), ch = CanvasHeight;
        const double roomX = WidgetTiles.CellWidth, roomY = WidgetTiles.CellHeight;
        Zoom = Fit && w > 0 ? Math.Min(MaxFit, Math.Min(cw / (w + roomX * 2), ch / (h + roomY * 2))) : 1;
        PictureWidth = w * Zoom;
        PictureHeight = h * Zoom;
        bool fitsX = PictureWidth + roomX * 2 * Zoom <= cw + 0.5, fitsY = PictureHeight + roomY * 2 * Zoom <= ch + 0.5;
        PictureLeft = fitsX ? Math.Round((cw - PictureWidth) / 2) : roomX;
        PictureTop = fitsY ? Math.Round((ch - PictureHeight) / 2) : roomY;
        ContentWidth = fitsX ? cw : PictureWidth + roomX * 6;
        ContentHeight = fitsY ? ch : PictureHeight + roomY * 5;
        var tiles = _w.Tiles ?? [];
        foreach (var b in TileBoxes)
        {
            if (b.Index >= tiles.Count) continue;
            var t = tiles[b.Index];
            b.Left = PictureLeft + (WidgetTiles.Edge + t.X * WidgetTiles.CellWidth) * Zoom;
            b.Top = PictureTop + (WidgetTiles.Edge + t.Y * WidgetTiles.CellHeight) * Zoom;
            b.Width = t.W * WidgetTiles.CellWidth * Zoom;
            b.Height = t.H * WidgetTiles.CellHeight * Zoom;
        }
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PictureLeft));
        OnPropertyChanged(nameof(PictureTop));
        OnPropertyChanged(nameof(PictureWidth));
        OnPropertyChanged(nameof(PictureHeight));
        OnPropertyChanged(nameof(ContentWidth));
        OnPropertyChanged(nameof(ContentHeight));
        OnPropertyChanged(nameof(SizeLine));
    }

    /// <summary>A tile dragged on the canvas, by whole cells. Dropped on another, the two swap places where they fit.</summary>
    public void MoveTile(int index, int dx, int dy)
    {
        if (_w.Tiles is not { } tiles || index >= tiles.Count || (dx == 0 && dy == 0)) { SelectTile(index); return; }
        Change(w =>
        {
            var t = w.Tiles![index];
            var from = (t.X, t.Y);
            (t.X, t.Y) = (t.X + dx, t.Y + dy);
            WidgetTiles.Settle(w.Tiles, t, from);
        });
        SelectTile(index);
    }

    /// <summary>A tile's edge or corner dragged, by whole cells, never smaller than its kind allows.</summary>
    public void ResizeTile(int index, int dw, int dh)
    {
        if (_w.Tiles is not { } tiles || index >= tiles.Count || (dw == 0 && dh == 0)) return;
        Change(w =>
        {
            var t = w.Tiles![index];
            var k = WidgetTiles.Kind(t.Kind)!;
            var from = (t.X, t.Y);
            (t.W, t.H) = (Math.Max(k.MinW, t.W + dw), Math.Max(k.MinH, t.H + dh));
            WidgetTiles.Settle(w.Tiles, t, from);
        });
        SelectTile(index);
    }

    /// <summary>Arrow keys: the picked tile one cell over.</summary>
    public void Nudge(int dx, int dy) { if (Selected >= 0) MoveTile(Selected, dx, dy); }

    [RelayCommand]
    private void RemoveTile(TileBox box) => RemoveTileAt(box.Index);

    public void RemoveTileAt(int index)
    {
        if (_w.Tiles is not { } tiles || index < 0 || index >= tiles.Count) return;
        Selected = -1;
        Change(w => { w.Tiles!.RemoveAt(index); WidgetTiles.Settle(w.Tiles); });
    }

    // ── Starting from something ─────────────────────────────────────────

    /// <summary>Three widgets to start from, on an empty one.</summary>
    public IReadOnlyList<Choice> Starters { get; } = [new("desk", Lang.T("Desk")), new("glance", Lang.T("PC at a glance")), new("game", Lang.T("Game night"))];

    [RelayCommand]
    private void Start(Choice starter)
    {
        WidgetTile T(string kind, int x, int y, int w, int h, Action<WidgetTile>? set = null)
        {
            var t = WidgetTiles.New(WidgetTiles.Kind(kind)!);
            (t.X, t.Y, t.W, t.H) = (x, y, w, h);
            set?.Invoke(t);
            return t;
        }
        List<WidgetTile> tiles = starter.Value switch
        {
            "desk" => [T(WidgetTiles.Clock, 0, 0, 6, 2), T(WidgetTiles.Calendar, 0, 2, 6, 6), T(WidgetTiles.Date, 6, 0, 3, 2)],
            "game" => [T(WidgetTiles.Fps, 0, 0, 5, 3), T(WidgetTiles.Reading, 5, 0, 3, 3), T(WidgetTiles.Reading, 8, 0, 3, 3, t => t.Item = nameof(OverlayMetric.GpuTemp)), T(WidgetTiles.NowPlaying, 0, 3, 11, 3)],
            _ => [T(WidgetTiles.Reading, 0, 0, 3, 4), T(WidgetTiles.Reading, 3, 0, 3, 4, t => t.Item = nameof(OverlayMetric.GpuTemp)), T(WidgetTiles.Graph, 6, 0, 8, 4),
                  T(WidgetTiles.Line, 0, 4, 14, 1, t => t.Items = WidgetCatalog.Items(OverlayMetric.CpuLoad, OverlayMetric.GpuLoad, OverlayMetric.Ram))],
        };
        Selected = 0;
        Change(w => w.Tiles = tiles);
    }

    // ── The list of tiles to add ────────────────────────────────────────

    public IReadOnlyList<TileChoice> TileChoices { get; }
    public List<WidgetGroupChip> TileChips { get; }
    public ObservableCollection<TileChoice> TileChoicesShown { get; } = [];

    [ObservableProperty] private string _tileQuery = "";

    partial void OnTileQueryChanged(string value) => FilterTiles();

    public WidgetGroupChip? PickedTileChip
    {
        get => TileChips.FirstOrDefault(c => c.IsSelected);
        set { if (value is not null && value != PickedTileChip) PickTileChip(value); }
    }

    [RelayCommand]
    private void PickTileChip(WidgetGroupChip chip)
    {
        foreach (var c in TileChips) c.IsSelected = c == chip;
        OnPropertyChanged(nameof(PickedTileChip));
        FilterTiles();
    }

    public bool NoTiles => TileChoicesShown.Count == 0;
    public string NoTilesText => Lang.T($"Nothing matches “{TileQuery.Trim()}”.");

    private void FilterTiles()
    {
        string group = TileChips?.FirstOrDefault(c => c.IsSelected)?.Key ?? WidgetsViewModel.AllGroup, q = TileQuery.Trim();
        TileChoicesShown.Clear();
        foreach (var c in TileChoices.Where(c => (group == WidgetsViewModel.AllGroup || c.Group == group)
                     && (q.Length == 0 || c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Line.Contains(q, StringComparison.CurrentCultureIgnoreCase))))
            TileChoicesShown.Add(c);
        OnPropertyChanged(nameof(NoTiles));
        OnPropertyChanged(nameof(NoTilesText));
    }

    /// <summary>The list of tiles to add is open (from Add tile, over the panel).</summary>
    [ObservableProperty] private bool _addListOpen;

    [RelayCommand]
    private void OpenAddList() => AddListOpen = true;

    /// <summary>Adds a tile to the right of the widget, or under it once the widget is much wider than tall, and picks it.</summary>
    [RelayCommand]
    private void AddTile(TileChoice choice)
    {
        AddListOpen = false;
        var kind = WidgetTiles.Kind(choice.Kind)!;
        Change(w =>
        {
            var t = WidgetTiles.New(kind);
            WidgetTiles.Place(w.Tiles!, t);
            Selected = w.Tiles!.IndexOf(t);
        });
    }

    // ── The picked tile ─────────────────────────────────────────────────

    [ObservableProperty] private int _selected = -1;

    private WidgetTile? Tile => _w.Tiles is { } tiles && Selected >= 0 && Selected < tiles.Count ? tiles[Selected] : null;

    public void SelectTile(int index)
    {
        Selected = _w.Tiles is { } tiles && index >= 0 && index < tiles.Count ? index : -1;
        foreach (var b in TileBoxes) b.IsSelected = b.Index == Selected;
        ShowAll();
    }

    /// <summary>A tile picked in the list of tiles.</summary>
    [RelayCommand]
    private void PickTile(TileBox? box)
    {
        if (box is not null) SelectTile(box.Index);
    }

    /// <summary>Back to the whole widget's settings: no tile picked.</summary>
    [RelayCommand]
    private void WholeWidget() => SelectTile(-1);

    /// <summary>Which of the whole widget's settings show with no tile picked: "look", "shape" or "desktop".</summary>
    [ObservableProperty] private string _panelTab = "look";

    /// <summary>A tile's few words in the list of tiles: its style, its reading's look, its readings, its first line…</summary>
    private string DetailOf(WidgetTile t) => t.Kind switch
    {
        WidgetTiles.Clock => Lang.T(t.Style ?? WidgetTiles.ClockStyles[0]),
        WidgetTiles.Reading => Lang.T(t.Look ?? WidgetTiles.Number),
        WidgetTiles.Line or WidgetTiles.Graph => string.Join(", ", (t.Items ?? []).Select(i => ReadingName(i.Id))),
        WidgetTiles.Notes => (t.Text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "",
        WidgetTiles.Todo when t.Todos is { Count: > 0 } todos => Lang.T($"{todos.Count(i => i.Done)} of {todos.Count} done"),
        WidgetTiles.Timer when WidgetTimer.IsCountdown(t) => t.Until is { } until ? until.ToString("d MMMM", System.Globalization.CultureInfo.CurrentCulture) : "",
        WidgetTiles.Timer when WidgetTimer.IsStopwatch(t) => Lang.T("Stopwatch"),
        WidgetTiles.Timer => Lang.T($"{t.Minutes} minutes"),
        WidgetTiles.Weather => t.Place ?? "",
        WidgetTiles.Shortcuts when t.Links is { Count: > 0 } links => links.Count == 1 ? links[0].Name : Lang.T($"{links.Count} shortcuts"),
        WidgetTiles.Photos when t.Folder is { } folder => System.IO.Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder,
        _ => "",
    };

    /// <summary>A tile kind's icon, from Windows' icon font.</summary>
    internal static string GlyphOf(string kind) => kind switch
    {
        WidgetTiles.Clock => "\uE823",
        WidgetTiles.Date => "\uE8BF",
        WidgetTiles.Calendar => "\uE787",
        WidgetTiles.Reading => "\uE9D9",
        WidgetTiles.Line => "\uE8FD",
        WidgetTiles.Graph => "\uE9D2",
        WidgetTiles.Fps => "\uE7FC",
        WidgetTiles.Today => "\uE81C",
        WidgetTiles.NowPlaying => "\uE768",
        WidgetTiles.Notes => "\uE70B",
        WidgetTiles.Media => "\uE8D6",
        WidgetTiles.Weather => "\uE753",
        WidgetTiles.Shortcuts => "\uE71D",
        WidgetTiles.Photos => "\uE91B",
        WidgetTiles.Todo => "\uE9D5",
        WidgetTiles.Timer => "\uE916",
        _ => "\uE80A",
    };

    public string TileGlyph => Tile is { } t ? GlyphOf(t.Kind) : "";

    // ── A note, a to-do list, a timer ─────────────────────────────────

    public bool IsNotes => Tile?.Kind == WidgetTiles.Notes;
    public bool IsTodo => Tile?.Kind == WidgetTiles.Todo;
    public bool IsTimerTile => Tile?.Kind == WidgetTiles.Timer;
    public bool HasTextSize => IsNotes || IsTodo;

    public IReadOnlyList<Choice> TextSizes { get; } = [.. WidgetTiles.TextSizes.Select(z => new Choice(z.Name, Lang.T(z.Name)))];

    public string TextSize
    {
        get => Tile?.TextSize ?? WidgetTiles.TextSizes[1].Name;
        set { if (value != TextSize) ChangeTile(t => t.TextSize = value == WidgetTiles.TextSizes[1].Name ? null : value); }
    }

    /// <summary>A note's text (saved when the box loses focus; on the desktop it's typed straight onto the note).</summary>
    public string NoteText
    {
        get => Tile?.Text ?? "";
        set
        {
            string text = value.Length > WidgetTiles.MaxNoteLength ? value[..WidgetTiles.MaxNoteLength] : value;
            if (text != NoteText) ChangeTile(t => t.Text = text.Length == 0 ? null : text);
        }
    }

    /// <summary>A note drawn as a sticky note: yellow paper and handwriting.</summary>
    public bool NoteSticky
    {
        get => Tile?.Look == WidgetTiles.Sticky;
        set { if (value != NoteSticky) ChangeTile(t => t.Look = value ? WidgetTiles.Sticky : null); }
    }

    public IReadOnlyList<TodoRow> TodoRows => [.. (Tile?.Todos ?? []).Select((i, n) => new TodoRow(n, i.Text, i.Done))];

    [ObservableProperty] private string _newTodo = "";

    [RelayCommand]
    private void AddTodo()
    {
        string text = NewTodo.Trim();
        if (text.Length == 0 || !IsTodo) return;
        ChangeTile(t => (t.Todos ??= []).Add(new TodoItem { Text = text }));
        NewTodo = "";
    }

    [RelayCommand]
    private void ToggleTodo(TodoRow? row) => ChangeTile(t => { if (row is not null && t.Todos is { } l && row.Index < l.Count) l[row.Index].Done = !l[row.Index].Done; });

    [RelayCommand]
    private void RemoveTodo(TodoRow? row) => ChangeTile(t => { if (row is not null && t.Todos is { } l && row.Index < l.Count) l.RemoveAt(row.Index); });

    public IReadOnlyList<Choice> TimerKinds { get; } = [.. WidgetTiles.TimerKinds.Select(k => new Choice(k, Lang.T(k)))];

    public string TimerKind
    {
        get => Tile is { } t ? WidgetTimer.KindOf(t) : WidgetTiles.TimerKinds[0];
        set
        {
            if (value == TimerKind || !WidgetTiles.TimerKinds.Contains(value)) return;
            ChangeTile(t =>
            {
                t.TimerKind = value == WidgetTiles.TimerKinds[0] ? null : value;
                WidgetTimer.Reset(t);
                if (value == WidgetTiles.TimerKinds[2] && t.Until is null) t.Until = DateTime.Today.AddDays(7);
            });
        }
    }

    public bool IsCountdownTile => IsTimerTile && Tile is { } t && WidgetTimer.IsCountdown(t);
    public bool IsTimerOnly => IsTimerTile && Tile is { } t && WidgetTimer.IsTimer(t);

    public int TimerMinutes => Tile?.Minutes ?? 25;

    /// <summary>A timer's length a step longer or shorter: a minute at a time below ten, then five.</summary>
    [RelayCommand]
    private void StepMinutes(string? how) => ChangeTile(t =>
    {
        int step = how == "+" ? (t.Minutes < 10 ? 1 : 5) : (t.Minutes <= 10 ? -1 : -5);
        t.Minutes = Math.Clamp(t.Minutes + step, 1, 24 * 60);
        WidgetTimer.Reset(t);
    });

    public DateTime? CountdownDay
    {
        get => Tile?.Until;
        set { if (value != CountdownDay) ChangeTile(t => t.Until = value?.Date); }
    }

    // ── Weather, shortcuts, photos (part three) ───────────────────────────

    public bool IsWeather => Tile?.Kind == WidgetTiles.Weather;
    public bool IsShortcuts => Tile?.Kind == WidgetTiles.Shortcuts;
    public bool IsPhotos => Tile?.Kind == WidgetTiles.Photos;

    /// <summary>The weather tile's place, as picked.</summary>
    public string PlaceName => Tile?.Place ?? "";

    [ObservableProperty] private string _placeQuery = "";
    [ObservableProperty] private IReadOnlyList<WeatherPlace> _placesFound = [];

    /// <summary>Under the place search: that it's searching, or that nothing was found.</summary>
    [ObservableProperty] private string _placeNote = "";

    /// <summary>Looks the typed name up (OpenStreetMap), and lists what it found to pick from.</summary>
    [RelayCommand]
    private async Task FindPlace()
    {
        string q = PlaceQuery.Trim();
        if (q.Length < 2) return;
        PlaceNote = Lang.T("Searching…");
        PlacesFound = [];
        var found = await WeatherFeed.FindAsync(q);
        PlacesFound = found;
        PlaceNote = found.Count == 0 ? Lang.T("No place found by that name. Try the nearest town or city.") : "";
    }

    [RelayCommand]
    private void PickPlace(WeatherPlace? place)
    {
        if (place is null || !IsWeather) return;
        ChangeTile(t => (t.Place, t.Lat, t.Lon) = (place.Name, Math.Round(place.Lat, 4), Math.Round(place.Lon, 4)));
        PlacesFound = [];
        PlaceQuery = "";
        PlaceNote = "";
    }

    public IReadOnlyList<LinkRow> LinkRows => [.. (Tile?.Links ?? []).Select((l, n) => new LinkRow(n, l.Name, l.Path, l.IsWeb ? "\uE774" : System.IO.Directory.Exists(l.Path) ? "\uE8B7" : "\uE8A5"))];

    public bool CanAddLink => IsShortcuts && (Tile?.Links?.Count ?? 0) < WidgetTiles.MaxLinks;

    [ObservableProperty] private string _newLink = "";

    /// <summary>Under the shortcuts: why a web address wasn't added, or that the tile is full.</summary>
    [ObservableProperty] private string _linkNote = "";

    [RelayCommand]
    private void AddApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Lang.T("Apps and shortcuts") + "|*.exe;*.lnk;*.url;*.appref-ms|" + Lang.T("All files") + "|*.*",
            DereferenceLinks = false,
        };
        if (dlg.ShowDialog() != true) return;
        string path = dlg.FileName, name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName is { Length: > 0 and <= 40 } product) name = product.Trim();
            }
            catch (System.IO.IOException) { }
        }
        AddLink(path, name);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() != true) return;
        AddLink(dlg.FolderName, System.IO.Path.GetFileName(dlg.FolderName.TrimEnd('\\')) is { Length: > 0 } n ? n : dlg.FolderName);
    }

    [RelayCommand]
    private void AddWebLink()
    {
        string text = NewLink.Trim();
        if (text.Length == 0) return;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.Contains('.'))
        {
            LinkNote = Lang.T("That isn't a web address. Try one like rigsight.app.");
            return;
        }
        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        AddLink(uri.ToString(), host);
        NewLink = "";
    }

    private void AddLink(string path, string name)
    {
        LinkNote = "";
        if (!IsShortcuts) return;
        if ((Tile?.Links?.Count ?? 0) >= WidgetTiles.MaxLinks)
        {
            LinkNote = Lang.T($"A shortcuts tile holds {WidgetTiles.MaxLinks}. Add another tile for more.");
            return;
        }
        ChangeTile(t =>
        {
            t.Links ??= [];
            if (t.Links.All(l => !string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase))) t.Links.Add(new TileLink { Path = path, Name = name });
        });
    }

    [RelayCommand]
    private void RemoveLink(LinkRow? row) => ChangeTile(t => { if (row is not null && t.Links is { } l && row.Index < l.Count) l.RemoveAt(row.Index); });

    /// <summary>The photos tile's folder, and how many pictures it holds.</summary>
    public string PhotoFolder => Tile?.Folder ?? "";
    public bool HasPhotoFolder => Tile?.Folder is not null;

    public string PhotoCount
    {
        get
        {
            if (Tile?.Folder is not { } folder) return "";
            int n = Rigsight.TaskbarDrawing.WidgetFiles.PicturesIn(folder).Length;
            return n switch { 0 => Lang.T("No pictures in this folder."), 1 => Lang.T("1 picture"), _ => Lang.T($"{n} pictures") };
        }
    }

    [RelayCommand]
    private void PickPhotoFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (Tile?.Folder is { } now && System.IO.Directory.Exists(now)) dlg.InitialDirectory = now;
        if (dlg.ShowDialog() == true) ChangeTile(t => t.Folder = dlg.FolderName);
    }

    public IReadOnlyList<Choice> PhotoSteps { get; } = [.. WidgetTiles.PhotoSteps.Select(s => new Choice(s.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), Lang.T(s.Name)))];

    public string PhotoEvery
    {
        get => (Tile?.Every ?? 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (value != PhotoEvery && int.TryParse(value, out int s)) ChangeTile(t => t.Every = s); }
    }

    public bool HasTile => Tile is not null;
    public bool NoTilePicked => Tile is null && !IsEmpty;
    public string TileName => Tile is { } t ? NameOf(t) : "";
    public string TileLine => Tile is { } t && WidgetTiles.Kind(t.Kind) is { } k ? WidgetTiles.LineOf(k) : "";

    private string NameOf(WidgetTile t) =>
        t.Kind == WidgetTiles.Reading && t.Item is { } item ? ReadingName(item) : WidgetTiles.Kind(t.Kind) is { } k ? WidgetTiles.NameOf(k) : t.Kind;

    private void ChangeTile(Action<WidgetTile> change)
    {
        int i = Selected;
        if (i < 0) return;
        Change(w => change(w.Tiles![i]));
    }

    public bool IsClock => Tile?.Kind == WidgetTiles.Clock;
    public bool IsReading => Tile?.Kind == WidgetTiles.Reading;
    public bool HasReadings => Tile?.Kind is WidgetTiles.Line or WidgetTiles.Graph;
    public bool CanAlign => Tile?.Kind is WidgetTiles.Clock or WidgetTiles.Date or WidgetTiles.Reading;

    public IReadOnlyList<Choice> ClockStyles { get; } = [.. WidgetTiles.ClockStyles.Select(s => new Choice(s, Lang.T(s)))];

    public string ClockStyle
    {
        get => Tile?.Style ?? WidgetTiles.ClockStyles[0];
        set { if (value != ClockStyle) ChangeTile(t => t.Style = value == WidgetTiles.ClockStyles[0] ? null : value); }
    }

    public bool Hours24
    {
        get => Tile?.Hours24 ?? System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        set { if (value != Hours24) ChangeTile(t => t.Hours24 = value); }
    }

    public bool Seconds
    {
        get => Tile?.Seconds ?? false;
        set { if (value != Seconds) ChangeTile(t => t.Seconds = value); }
    }

    public bool ShowDate
    {
        get => Tile?.ShowDate ?? true;
        set { if (value != ShowDate) ChangeTile(t => t.ShowDate = value); }
    }

    public string Align
    {
        get => (Tile?.Align ?? (Tile is { } t ? WidgetTiles.Kind(t.Kind)?.Align : null) ?? TileAlign.Left).ToString();
        set { if (value != Align && Enum.TryParse<TileAlign>(value, out var a)) ChangeTile(t => t.Align = a); }
    }

    /// <summary>
    /// Every reading a tile can show: the usual ones, then each sensor of this PC by the name it shows everywhere else
    /// (the one given on All sensors, else its own in the language in use), as the list it's picked from names it.
    /// </summary>
    public IReadOnlyList<ReadingChoice> Readings { get; }

    private List<ReadingChoice> MakeReadings()
    {
        var list = WidgetCatalog.Offered(WidgetLayout.Tiles).Select(m => new ReadingChoice(m.ToString(), WidgetCatalog.MetricName(m))).ToList();
        if (_live is not null)
            list.AddRange(_live.AllSensors.Select(s => new ReadingChoice(WidgetCatalog.SensorPrefix + s.Id, $"{s.DisplayName} ({s.HardwareShown})")));
        return list;
    }

    private string ReadingName(string id) =>
        Readings.FirstOrDefault(r => r.Id == id)?.Name ?? (WidgetCatalog.Sensor(id) is { } sensor ? WidgetCard_SensorName(sensor) : id);

    /// <summary>A sensor not read yet, named from its identifier ("/gpu-nvidia/0/fan/1" is "GPU fan 1").</summary>
    private static string WidgetCard_SensorName(string id)
    {
        var parts = id.Trim('/').Split('/');
        string part = parts.Length > 0 ? parts[0].Split('-')[0].ToUpperInvariant() : "";
        string kind = parts.Length > 2 ? parts[2] : "";
        string n = parts.Length > 3 ? parts[3] : "";
        return $"{part} {kind} {n}".Trim();
    }

    public string Reading
    {
        get => Tile?.Item ?? nameof(OverlayMetric.CpuTemp);
        set
        {
            if (value == Reading || value is null) return;
            ChangeTile(t =>
            {
                t.Item = value;
                var kind = _live?.AllSensors.FirstOrDefault(s => WidgetCatalog.SensorPrefix + s.Id == value)?.Kind;
                if (!WidgetTiles.Looks(value, kind).Contains(t.Look ?? WidgetTiles.Number)) t.Look = WidgetTiles.Number;
            });
        }
    }

    public IReadOnlyList<Choice> Looks
    {
        get
        {
            var kind = Tile?.Item is { } id ? _live?.AllSensors.FirstOrDefault(s => WidgetCatalog.SensorPrefix + s.Id == id)?.Kind : null;
            return [.. WidgetTiles.Looks(Tile?.Item, kind).Select(l => new Choice(l, Lang.T(l)))];
        }
    }

    public string Look
    {
        get => Tile?.Look ?? WidgetTiles.Number;
        set { if (value is not null && value != Look) ChangeTile(t => t.Look = value); }
    }

    public string ShortName
    {
        get => Tile?.Label ?? "";
        set
        {
            string label = (value ?? "").Trim();
            if (label == ShortName) return;
            ChangeTile(t => t.Label = label.Length == 0 ? null : label.Length > OverlaySettings.MaxLabelLength ? label[..OverlaySettings.MaxLabelLength] : label);
        }
    }

    /// <summary>A line's or graph's readings, in order.</summary>
    public IReadOnlyList<TileReadingRow> TileReadings =>
        [.. (Tile?.Items ?? []).Select((r, i) => new TileReadingRow(i, ReadingName(r.Id)))];

    /// <summary>A reading picked from the list: added to the line or graph (a graph only takes readings it keeps five minutes of).</summary>
    public ReadingChoice? ReadingToAdd
    {
        get => null;
        set
        {
            if (value is null || Tile is not { } tile) return;
            if (tile.Kind == WidgetTiles.Graph && (WidgetCatalog.Metric(value.Id) is not { } m || !WidgetCatalog.GraphMetrics.Contains(m))) return;
            if (tile.Items?.Any(i => i.Id == value.Id) == true) return;
            ChangeTile(t => (t.Items ??= []).Add(new WidgetItem { Id = value.Id }));
        }
    }

    // ── Picking a reading: a search and every reading under its part, as on the Taskbar page ──
    // (the user, 2026-11-11: "this reading box also needs to have a searchable list, something like ... taskbar").

    /// <summary>The list a tile's reading is picked from is open.</summary>
    [ObservableProperty] private bool _readingPickerOpen;

    /// <summary>The list a reading is added to a line or graph from is open.</summary>
    [ObservableProperty] private bool _addPickerOpen;

    [ObservableProperty] private string _readingQuery = "";

    private bool _pickingToAdd;

    partial void OnReadingQueryChanged(string value)
    {
        OnPropertyChanged(nameof(ReadingRows));
        OnPropertyChanged(nameof(NoReadingRows));
    }

    /// <summary>The tile's reading by name, on the button that opens the list.</summary>
    public string ReadingShown => ReadingName(Reading);

    [RelayCommand]
    private void OpenReadingPicker(string? forWhat)
    {
        _pickingToAdd = forWhat == "add";
        ReadingQuery = "";
        OnPropertyChanged(nameof(ReadingRows));
        OnPropertyChanged(nameof(NoReadingRows));
        if (_pickingToAdd) AddPickerOpen = true;
        else ReadingPickerOpen = true;
    }

    [RelayCommand]
    private void PickRow(TaskbarPartnerItem? row) => row?.Pick?.Invoke();

    public bool NoReadingRows => ReadingRows.Count == 0;

    /// <summary>
    /// What's typed matched against each reading: the usual ones first, then this PC's sensors under their part, with
    /// their hardware and value now. The one in use is ticked; on a line or graph, those already on it are ticked and
    /// can't be added twice.
    /// </summary>
    public IReadOnlyList<TaskbarPartnerItem> ReadingRows
    {
        get
        {
            string q = ReadingQuery.Trim();
            var source = (_pickingToAdd ? ReadingsToAdd : Readings).Select(r => r.Id).ToHashSet();
            var taken = _pickingToAdd ? (Tile?.Items ?? []).Select(i => i.Id).ToHashSet() : [Reading];
            bool Has(string text) => q.Length == 0 || text.Contains(q, StringComparison.CurrentCultureIgnoreCase);
            var rows = new List<TaskbarPartnerItem>();
            var usual = WidgetCatalog.Offered(WidgetLayout.Tiles).Where(m => source.Contains(m.ToString()) && Has(WidgetCatalog.MetricName(m))).ToList();
            if (usual.Count > 0) rows.Add(new(Lang.T("USUAL READINGS"), "", "", "", false, false, null));
            foreach (var m in usual)
            {
                string id = m.ToString(), name = WidgetCatalog.MetricName(m);
                bool on = taken.Contains(id);
                rows.Add(new(null, name, "", MetricValue(m), !_pickingToAdd || !on, on, !_pickingToAdd || !on ? () => Pick(id, name) : null, id));
            }
            var sensors = _live?.AllSensors.Where(s => source.Contains(WidgetCatalog.SensorPrefix + s.Id)).ToList() ?? [];
            foreach (var part in TaskbarViewModel.PartOrder)
            {
                var these = sensors.Where(s => TrayParts.PartOf(s.HardwareType) == part && (TaskbarViewModel.Matches(s, q) || Has(TrayParts.Label(part)))).ToList();
                if (these.Count == 0) continue;
                rows.Add(new(TrayParts.Label(part), "", "", "", false, false, null));
                foreach (var s in these)
                {
                    string id = WidgetCatalog.SensorPrefix + s.Id, name = $"{s.DisplayName} ({s.HardwareShown})";
                    bool on = taken.Contains(id);
                    rows.Add(new(null, s.DisplayName, s.HardwareShown, s.FormattedValue ?? "", !_pickingToAdd || !on, on, !_pickingToAdd || !on ? () => Pick(id, name) : null, id));
                }
            }
            return rows;
        }
    }

    private void Pick(string id, string name)
    {
        if (_pickingToAdd)
        {
            AddPickerOpen = false;
            ReadingToAdd = new ReadingChoice(id, name);
        }
        else
        {
            ReadingPickerOpen = false;
            Reading = id;
        }
    }

    /// <summary>A usual reading's value now, from the app's own readings (none for what the agent alone knows, the frame rate say).</summary>
    private string MetricValue(OverlayMetric m) => (m switch
    {
        OverlayMetric.CpuTemp => _live?.CpuTemp, OverlayMetric.CpuLoad => _live?.CpuLoad, OverlayMetric.CpuPower => _live?.CpuPower,
        OverlayMetric.CpuClock => _live?.CpuClock, OverlayMetric.GpuTemp => _live?.GpuTemp, OverlayMetric.GpuHotSpot => _live?.GpuHotSpot,
        OverlayMetric.GpuLoad => _live?.GpuLoad, OverlayMetric.GpuPower => _live?.GpuPower, OverlayMetric.GpuClock => _live?.GpuClock,
        OverlayMetric.GpuMemory => _live?.GpuVramUsed, OverlayMetric.Ram => _live?.RamLoad,
        _ => null,
    })?.FormattedValue ?? "";

    /// <summary>What a graph can add: the readings the agent keeps five minutes of.</summary>
    public IReadOnlyList<ReadingChoice> ReadingsToAdd => Tile?.Kind == WidgetTiles.Graph
        ? [.. WidgetCatalog.GraphMetrics.Select(m => new ReadingChoice(m.ToString(), WidgetCatalog.MetricName(m)))]
        : Readings;

    [RelayCommand]
    private void RemoveReading(TileReadingRow row) => ChangeTile(t => { if (t.Items is { } items && row.Index < items.Count) items.RemoveAt(row.Index); });

    /// <summary>The picked tile's size in cells, with buttons for one more or one less.</summary>
    public int TileW => Tile?.W ?? 0;
    public int TileH => Tile?.H ?? 0;

    /// <summary>The picked tile's ready sizes (small, medium, large), as "W×H" with their names.</summary>
    public IReadOnlyList<Choice> SizePresets => Tile is { } t
        ? [.. WidgetTiles.Presets(t.Kind).Select(p => new Choice($"{p.W}x{p.H}", Lang.T(p.Name)))]
        : [];

    public bool HasSizePresets => SizePresets.Count > 0;

    /// <summary>The ready size the picked tile is at ("8x3"), or null between them; picked, the tile takes it.</summary>
    public string? TilePreset
    {
        get => Tile is { } t && WidgetTiles.Presets(t.Kind).Any(p => p.W == t.W && p.H == t.H) ? $"{t.W}x{t.H}" : null;
        set
        {
            int i = Selected;
            if (value is null || value == TilePreset || i < 0 || value.Split('x') is not [var a, var b] || !int.TryParse(a, out int pw) || !int.TryParse(b, out int ph)) return;
            Change(w =>
            {
                var t = w.Tiles![i];
                var from = (t.X, t.Y);
                (t.W, t.H) = (pw, ph);
                WidgetTiles.Settle(w.Tiles, t, from);
            });
            SelectTile(i);
        }
    }

    [RelayCommand]
    private void Grow(string how)
    {
        int i = Selected;
        if (i < 0) return;
        (int dw, int dh) = how switch { "w+" => (1, 0), "w-" => (-1, 0), "h+" => (0, 1), _ => (0, -1) };
        ResizeTile(i, dw, dh);
    }

    [RelayCommand]
    private void DuplicateTile()
    {
        if (Tile is not { } t) return;
        Change(w =>
        {
            var copy = t.Copy();
            WidgetTiles.Place(w.Tiles!, copy);
            Selected = w.Tiles!.IndexOf(copy);
        });
    }

    [RelayCommand]
    private void RemovePickedTile() => RemoveTileAt(Selected);

    // ── Look ────────────────────────────────────────────────────────────

    /// <summary>A widget's themes, here and in the Widgets page's quick settings.</summary>
    public static IReadOnlyList<Choice> ThemeList() =>
    [
        new(nameof(WidgetTheme.App), Lang.T("Like RigSight")), new(nameof(WidgetTheme.Dark), Lang.T("Dark")),
        new(nameof(WidgetTheme.Grey), Lang.T("Gray")), new(nameof(WidgetTheme.Light), Lang.T("Light")), new(nameof(WidgetTheme.System), Lang.T("System")),
    ];

    public IReadOnlyList<Choice> Themes { get; } = ThemeList();

    public string Theme
    {
        get => _w.Theme.ToString();
        set { if (value is not null && value != Theme && Enum.TryParse<WidgetTheme>(value, out var t)) Change(w => w.Theme = t); }
    }

    /// <summary>The accents: the theme's own, then a few colours that aren't a status colour (never green, amber or red).</summary>
    public IReadOnlyList<Choice> Accents { get; } =
    [
        new("", Lang.T("The theme's own")), new("#6E99FF", Lang.T("Blue")), new("#22D3EE", Lang.T("Cyan")),
        new("#B18CFF", Lang.T("Violet")), new("#F472B6", Lang.T("Pink")), new("#FACC15", Lang.T("Yellow")),
    ];

    public string Accent
    {
        get => _w.Accent ?? "";
        set { if (value is not null && value != Accent) Change(w => w.Accent = value.Length == 0 ? null : value); }
    }

    public IReadOnlyList<Choice> Fonts { get; } =
        [new("", "Segoe UI"), new("Bahnschrift", "Bahnschrift"), new("Cascadia Mono", "Cascadia Mono"), new("Georgia", "Georgia")];

    public string Font
    {
        get => _w.Font ?? "";
        set { if (value is not null && value != Font) Change(w => w.Font = value.Length == 0 ? null : value); }
    }

    public bool Grayscale
    {
        get => _w.Grayscale;
        set { if (value != _w.Grayscale) Change(w => w.Grayscale = value); }
    }

    /// <summary>A widget's sizes, here and in the Widgets page's quick settings.</summary>
    public static readonly IReadOnlyList<Choice> SizeList =
        [new("0.8", "S"), new("1", "M"), new("1.25", "L"), new("1.5", "XL"), new("2", "2XL"), new("2.5", "3XL")];

    public IReadOnlyList<Choice> Sizes => SizeList;

    public string Size
    {
        get => _w.Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (value is null || value == Size || !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s)) return;
            Change(w => w.Scale = s);
        }
    }

    public double BackgroundPercent
    {
        get => Math.Round(_w.BackgroundOpacity * 100);
        set { if (Math.Abs(value - BackgroundPercent) >= 1) Change(w => w.BackgroundOpacity = Math.Clamp(value / 100, 0, 1)); }
    }

    public double ContentPercent
    {
        get => Math.Round(_w.ContentOpacity * 100);
        set { if (Math.Abs(value - ContentPercent) >= 1) Change(w => w.ContentOpacity = Math.Clamp(value / 100, 0.2, 1)); }
    }

    // ── Shape ───────────────────────────────────────────────────────────

    public string Corners
    {
        get => _w.Corners.ToString();
        set { if (value is not null && value != Corners && Enum.TryParse<WidgetCorners>(value, out var c)) Change(w => w.Corners = c); }
    }

    public bool TileBoxesOn
    {
        get => _w.TileBoxes;
        set
        {
            if (value == _w.TileBoxes) return;
            // A box for each tile starts with a little room between them.
            Change(w => { w.TileBoxes = value; if (value && w.Gap == WidgetGap.None) w.Gap = WidgetGap.Small; if (value) w.TileLines = false; });
        }
    }

    public IReadOnlyList<Choice> Gaps { get; } =
        [new(nameof(WidgetGap.None), Lang.T("None")), new(nameof(WidgetGap.Small), Lang.T("Small")), new(nameof(WidgetGap.Normal), Lang.T("Normal")), new(nameof(WidgetGap.Large), Lang.T("Large"))];

    public string Gap
    {
        get => _w.Gap.ToString();
        set { if (value is not null && value != Gap && Enum.TryParse<WidgetGap>(value, out var g)) Change(w => w.Gap = g); }
    }

    public bool TileLines
    {
        get => _w.TileLines;
        set { if (value != _w.TileLines) Change(w => w.TileLines = value); }
    }

    // ── On the desktop ──────────────────────────────────────────────────

    public bool Locked
    {
        get => _w.Locked;
        set
        {
            if (value == _w.Locked) return;
            TakeSaved();
            _w.Locked = value;
            Commit();
        }
    }

    /// <summary>How much bigger Fit shows a small widget, at most.</summary>
    public const double MaxFit = 2;

    /// <summary>The nine places, top left to bottom right; the one the widget is at is lit.</summary>
    public IReadOnlyList<PlaceSpot> PlaceSpots { get; } =
    [
        new(0, Lang.T("Top left")), new(1, Lang.T("Top")), new(2, Lang.T("Top right")),
        new(3, Lang.T("Left")), new(4, Lang.T("Middle")), new(5, Lang.T("Right")),
        new(6, Lang.T("Bottom left")), new(7, Lang.T("Bottom")), new(8, Lang.T("Bottom right")),
    ];

    /// <summary>
    /// Where a place puts the widget on the screen it's on (its panel's top left): a corner, an edge's middle or the
    /// middle, 12 pixels in, at the size the agent draws it there: that screen's scale, not the one the app's window is
    /// on (with a laptop at 150% beside a monitor at 100%, a widget sent to a corner hung off the edge or stopped short).
    /// </summary>
    private (int X, int Y)? SpotFor(int anchor, int? atX, int? atY)
    {
        var at = new System.Drawing.Point(atX ?? 0, atY ?? 0);
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(at)) ?? System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return null;
        var area = screen.WorkingArea;
        var (pw, ph) = WidgetPictures.SizeOf(_w);
        double own = ScreenChoice.ScaleOf(screen.Bounds), scale = own * _w.Scale;
        int w = (int)Math.Round(pw * scale), h = (int)Math.Round(ph * scale), gap = (int)Math.Round(12 * own);
        int col = anchor % 3, row = anchor / 3;
        int x = col == 0 ? area.Left + gap : col == 2 ? area.Right - gap - w : area.Left + (area.Width - w) / 2;
        int y = row == 0 ? area.Top + gap : row == 2 ? area.Bottom - gap - h : area.Top + (area.Height - h) / 2;
        return (x, y);
    }

    /// <summary>
    /// Lights the place the widget is at, by where it's saved now (it may have been dragged on the desktop meanwhile):
    /// none when it's somewhere else (the user, 2026-11-11: the grid didn't show which place was in use).
    /// </summary>
    private void MarkPlace()
    {
        var saved = _settings.Current.Widgets.FirstOrDefault(w => w.Id == Id);
        int? x = saved?.X ?? _w.X, y = saved?.Y ?? _w.Y;
        foreach (var p in PlaceSpots)
            p.IsActive = !IsEmpty && x is int sx && y is int sy && SpotFor(p.Index, x, y) is { } spot && Math.Abs(spot.X - sx) <= 2 && Math.Abs(spot.Y - sy) <= 2;
    }

    /// <summary>Sends the widget to a corner, an edge's middle or the middle of its screen (0 top left … 8 bottom right), 12 pixels in.</summary>
    [RelayCommand]
    private void Place(string spot)
    {
        if (!int.TryParse(spot, out int anchor)) return;
        TakeSaved();
        if (SpotFor(anchor, _w.X, _w.Y) is not { } at) return;
        (_w.X, _w.Y) = (at.X, at.Y);
        Commit();
        _moved?.Invoke(Id, at.X, at.Y);
    }
}

/// <summary>One of the nine places a widget can be sent to from its editor.</summary>
public sealed partial class PlaceSpot(int index, string name) : ObservableObject
{
    public int Index { get; } = index;
    public string Name { get; } = name;
    public string Arg { get; } = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    [ObservableProperty] private bool _isActive;
}

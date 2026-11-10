using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A tile on the editor's canvas: its name, its place in cells, and its box on the canvas at the zoom in use.</summary>
public sealed partial class TileBox(int index, string name) : ObservableObject
{
    public int Index { get; } = index;
    public string Name { get; } = name;
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
    private readonly Stack<string> _undo = new(), _redo = new();

    public WidgetEditorViewModel(string id, SettingsModel settings, LiveData? live, Action close)
    {
        Id = id;
        _settings = settings;
        _live = live;
        _close = close;
        _w = Clone(settings.Current.Widgets.First(w => w.Id == id));
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
        Selected = _w.Tiles is { Count: > 0 } ? 0 : -1;
        Rebuild();
        // A clock on the widget moves on: the picture is drawn again each second while the editor is open.
        _tick.Tick += (_, _) => Draw();
        _tick.Start();
    }

    public string Id { get; }

    public void Close() => _tick.Stop();

    private static WidgetConfig Clone(WidgetConfig w) =>
        JsonSerializer.Deserialize<WidgetConfig>(JsonSerializer.Serialize(w, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    private string Snapshot() => JsonSerializer.Serialize(_w, SettingsStore.JsonOptions);

    /// <summary>
    /// A change to the widget: kept for Undo, saved at once, drawn again. A widget drawn as before the box came becomes
    /// tiles at its first change (it looks the same).
    /// </summary>
    private void Change(Action<WidgetConfig> change)
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        if (_w.Tiles is null)
        {
            _w.Tiles = WidgetTiles.FromLayout(_w);
            _w.Layout = null;
        }
        change(_w);
        Commit();
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

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        _w = JsonSerializer.Deserialize<WidgetConfig>(_undo.Pop(), SettingsStore.JsonOptions)!;
        Commit();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        _w = JsonSerializer.Deserialize<WidgetConfig>(_redo.Pop(), SettingsStore.JsonOptions)!;
        Commit();
    }

    /// <summary>Everything the page shows from the widget, after it changed.</summary>
    private void Rebuild()
    {
        var tiles = _w.Tiles ?? [];
        TileBoxes.Clear();
        for (int i = 0; i < tiles.Count; i++)
            TileBoxes.Add(new TileBox(i, NameOf(tiles[i])) { IsSelected = i == Selected });
        Draw();
        OnPropertyChanged(string.Empty);
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
            _w.Enabled = value;
            Commit();
        }
    }

    [RelayCommand]
    private void Back() => _close();

    [RelayCommand]
    private void Duplicate()
    {
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
            Layout();
        }
    }
    private double _canvasWidth = 700;

    public const double CanvasHeight = 340;

    /// <summary>Fit: the whole widget in view, made smaller where needed. 100%: its real size, scrolling where it's bigger.</summary>
    public bool Fit
    {
        get => _fit;
        set
        {
            if (SetProperty(ref _fit, value)) Layout();
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
            return Zoom < 0.995 ? Lang.T($"{size}, shown at {Math.Round(Zoom * 100):0}%") : size;
        }
    }

    private void Draw()
    {
        Picture = IsEmpty ? null : WidgetPictures.Draw(_w, _live, _settings.Current, (float)ScreenScale);
        Layout();
    }

    /// <summary>Where the widget and each tile's box sit on the canvas, at the zoom in use.</summary>
    private void Layout()
    {
        var (w, h) = IsEmpty ? (0, 0) : WidgetPictures.SizeOf(_w);
        double cw = Math.Max(200, CanvasWidth), ch = CanvasHeight;
        const double roomX = WidgetTiles.CellWidth, roomY = WidgetTiles.CellHeight;
        Zoom = Fit && w > 0 ? Math.Min(1, Math.Min(cw / (w + roomX * 2), ch / (h + roomY * 2))) : 1;
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

    /// <summary>Adds a tile to the right of the widget, or under it once the widget is much wider than tall, and picks it.</summary>
    [RelayCommand]
    private void AddTile(TileChoice choice)
    {
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
        OnPropertyChanged(string.Empty);
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

    /// <summary>Every reading a tile can show: the usual ones, then each sensor of this PC.</summary>
    public IReadOnlyList<ReadingChoice> Readings { get; }

    private List<ReadingChoice> MakeReadings()
    {
        var list = WidgetCatalog.Offered(WidgetLayout.Tiles).Select(m => new ReadingChoice(m.ToString(), WidgetCatalog.MetricName(m))).ToList();
        if (_live is not null)
            list.AddRange(_live.AllSensors.Select(s => new ReadingChoice(WidgetCatalog.SensorPrefix + s.Id, $"{s.Name} ({s.HardwareShown})")));
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

    /// <summary>What a graph can add: the readings the agent keeps five minutes of.</summary>
    public IReadOnlyList<ReadingChoice> ReadingsToAdd => Tile?.Kind == WidgetTiles.Graph
        ? [.. WidgetCatalog.GraphMetrics.Select(m => new ReadingChoice(m.ToString(), WidgetCatalog.MetricName(m)))]
        : Readings;

    [RelayCommand]
    private void RemoveReading(TileReadingRow row) => ChangeTile(t => { if (t.Items is { } items && row.Index < items.Count) items.RemoveAt(row.Index); });

    /// <summary>The picked tile's size in cells, with buttons for one more or one less.</summary>
    public int TileW => Tile?.W ?? 0;
    public int TileH => Tile?.H ?? 0;

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

    public IReadOnlyList<Choice> Themes { get; } =
    [
        new(nameof(WidgetTheme.App), Lang.T("Like RigSight")), new(nameof(WidgetTheme.Dark), Lang.T("Dark")),
        new(nameof(WidgetTheme.Grey), Lang.T("Gray")), new(nameof(WidgetTheme.Light), Lang.T("Light")), new(nameof(WidgetTheme.System), Lang.T("System")),
    ];

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

    public IReadOnlyList<Choice> Sizes { get; } =
        [new("0.8", "S"), new("1", "M"), new("1.25", "L"), new("1.5", "XL"), new("2", "2XL"), new("2.5", "3XL")];

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

    public IReadOnlyList<Choice> SitsChoices { get; } =
        [new(nameof(WidgetSits.OnDesktop), Lang.T("On the desktop")), new(nameof(WidgetSits.OverWindows), Lang.T("Over windows"))];

    public string Sits
    {
        get => _w.Sits.ToString();
        set
        {
            if (value is null || value == Sits || !Enum.TryParse<WidgetSits>(value, out var s)) return;
            _w.Sits = s;
            Commit();
        }
    }

    public bool Locked
    {
        get => _w.Locked;
        set
        {
            if (value == _w.Locked) return;
            _w.Locked = value;
            Commit();
        }
    }

    /// <summary>Sends the widget to a corner, an edge's middle or the middle of its screen (0 top left … 8 bottom right), 12 pixels in.</summary>
    [RelayCommand]
    private void Place(string spot)
    {
        if (!int.TryParse(spot, out int anchor)) return;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var at = new System.Drawing.Point(_w.X ?? 0, _w.Y ?? 0);
        var screen = screens.FirstOrDefault(s => s.Bounds.Contains(at)) ?? System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var (pw, ph) = WidgetPictures.SizeOf(_w);
        double scale = ScreenScale * _w.Scale;
        int w = (int)Math.Round(pw * scale), h = (int)Math.Round(ph * scale), gap = (int)Math.Round(12 * ScreenScale);
        int col = anchor % 3, row = anchor / 3;
        int x = col == 0 ? area.Left + gap : col == 2 ? area.Right - gap - w : area.Left + (area.Width - w) / 2;
        int y = row == 0 ? area.Top + gap : row == 2 ? area.Bottom - gap - h : area.Top + (area.Height - h) / 2;
        (_w.X, _w.Y) = (x, y);
        Commit();
    }
}

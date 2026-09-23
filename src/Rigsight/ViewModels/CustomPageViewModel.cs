using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A dashboard the user builds from tiles: add, remove, resize and drag tiles into place.</summary>
public sealed partial class CustomPageViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly Action<CustomPageViewModel> _open;
    private readonly Action<CustomPageViewModel> _delete;

    private TileViewModel? _dragged;
    private TileViewModel? _placeholder;
    private Dictionary<TileViewModel, (int X, int Y)>? _dragHome;

    public CustomPageViewModel(CustomPageConfig config, SettingsModel settings, LiveData live, HomeViewModel home,
        CrashesViewModel crashes, Action<CustomPageViewModel> open, Action<CustomPageViewModel> delete)
    {
        _settings = settings;
        _open = open;
        _delete = delete;
        Id = config.Id;
        _name = config.Name;
        Live = live;
        Home = home;
        Crashes = crashes;
        foreach (var t in config.Tiles) Tiles.Add(new TileViewModel(t, this));
        Live.SensorsRebuilt += () => { foreach (var t in Tiles) t.ResolveSensor(); };
        settings.Changed += () => OnPropertyChanged(nameof(IsStartPage));
    }

    /// <summary>Whether the app opens on this page.</summary>
    public bool IsStartPage => _settings.Current.StartPage == NavKey;

    [RelayCommand]
    private void ToggleStartPage()
    {
        bool make = !IsStartPage;
        _settings.Update(s => s.StartPage = make ? NavKey : "home");
    }

    public string Id { get; }
    public string NavKey => "custom:" + Id;
    public LiveData Live { get; }
    public HomeViewModel Home { get; }
    public CrashesViewModel Crashes { get; }

    public ObservableCollection<TileViewModel> Tiles { get; } = [];
    public bool IsEmpty => !Tiles.Any(t => !t.IsPlaceholder);

    public IReadOnlyList<TileGroup> Catalog => TileCatalog.Groups;
    public IReadOnlyList<SensorItem> AllSensors => Live.AllSensors;
    [ObservableProperty] private SensorItem? _sensorToAdd;

    /// <summary>Raised when tiles moved or changed size, so the grid can re-arrange (and animate).</summary>
    public event Action? LayoutChanged;

    [ObservableProperty] private string _name;
    partial void OnNameChanged(string value) => Save();

    [ObservableProperty] private bool _isEditing;

    /// <summary>Bound to this page's sidebar button.</summary>
    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _open(this);
    }

    [RelayCommand]
    private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand]
    private void DeletePage() => _delete(this);

    /// <summary>Loads the history the page's tiles need (only what's on it).</summary>
    public async Task RefreshAsync()
    {
        var kinds = Tiles.Select(t => t.Kind).ToHashSet();
        if (kinds.Overlaps(["most-used", "insights", "yesterday"])) await Home.RefreshAsync();
        if (kinds.Contains("crashes")) await Crashes.LoadAsync();
    }

    // ── Adding, removing, resizing ────────────────────────────────────────

    [RelayCommand]
    private void AddTile(TileKind kind) => Add(kind.Kind, kind.Sensor, kind.W, kind.H);

    [RelayCommand]
    private void AddSensorTile()
    {
        if (SensorToAdd is null) return;
        var single = TileCatalog.Find("sensor");
        Add("sensor", SensorToAdd.Id, single.W, single.H);
        SensorToAdd = null;
    }

    public void AddStarterTiles()
    {
        foreach (var kind in TileCatalog.Starter()) Add(kind.Kind, kind.Sensor, kind.W, kind.H, save: false);
        Save();
    }

    private void Add(string kind, string? sensor, int w, int h, bool save = true)
    {
        var (x, y) = TileLayout.FindSpot(Tiles, w, h);
        var tile = new TileViewModel(new TileConfig { Kind = kind, Sensor = sensor, X = x, Y = y, W = w, H = h }, this);
        Tiles.Add(tile);
        OnPropertyChanged(nameof(IsEmpty));
        LayoutChanged?.Invoke();
        if (save) Save();
        _ = RefreshAsync();
    }

    public void Remove(TileViewModel tile)
    {
        Tiles.Remove(tile);
        TileLayout.Flow(Tiles, null);
        OnPropertyChanged(nameof(IsEmpty));
        LayoutChanged?.Invoke();
        Save();
    }

    // ── Resizing (by dragging a tile's edge or corner) ────────────────────

    private TileViewModel? _resized;

    public void BeginResize(TileViewModel tile)
    {
        _resized = tile;
        _dragHome = Tiles.ToDictionary(t => t, t => (t.X, t.Y));
    }

    /// <summary>The edge is over this many cells: resize (within the tile's limits) and let the others move.</summary>
    public void ResizeTo(int w, int h)
    {
        if (_resized is not { } tile) return;
        w = Math.Clamp(w, tile.MinW, TileConfig.Columns - tile.X);
        h = Math.Clamp(h, tile.MinH, TileConfig.MaxHeight);
        if (w == tile.W && h == tile.H) return;
        tile.W = w;
        tile.H = h;
        TileLayout.Flow(Tiles, tile, _dragHome);
        LayoutChanged?.Invoke();
    }

    public void EndResize()
    {
        if (_resized is null) return;
        _resized = null;
        _dragHome = null;
        Save();
    }

    // ── Dragging ──────────────────────────────────────────────────────────

    public void BeginDrag(TileViewModel tile)
    {
        _dragged = tile;
        _dragHome = Tiles.ToDictionary(t => t, t => (t.X, t.Y));
        tile.IsDragging = true;
        _placeholder = TileViewModel.PlaceholderFor(tile);
        Tiles.Insert(0, _placeholder);
    }

    /// <summary>The dragged tile is over this cell: move the others out of the way.</summary>
    public void DragTo(int col, int row)
    {
        if (_dragged is null || _placeholder is null) return;
        col = Math.Clamp(col, 0, TileConfig.Columns - _dragged.W);
        row = Math.Max(0, row);
        if (_placeholder.X == col && _placeholder.Y == row) return;

        _dragged.X = col;
        _dragged.Y = row;
        TileLayout.Flow(Tiles, _dragged, _dragHome);
        _placeholder.CopyPosition(_dragged);
        LayoutChanged?.Invoke();
    }

    public void EndDrag()
    {
        if (_dragged is null) return;
        _dragged.IsDragging = false;
        if (_placeholder is not null) Tiles.Remove(_placeholder);
        _dragged = _placeholder = null;
        _dragHome = null;
        LayoutChanged?.Invoke();
        Save();
    }

    // ── Saving ────────────────────────────────────────────────────────────

    private void Save()
    {
        var tiles = Tiles.Where(t => !t.IsPlaceholder).Select(t => t.ToConfig()).ToList();
        _settings.Update(s =>
        {
            var page = s.CustomPages.FirstOrDefault(p => p.Id == Id);
            if (page is null)
            {
                page = new CustomPageConfig { Id = Id, Grid = CustomPageConfig.CurrentGrid };
                s.CustomPages.Add(page);
            }
            page.Name = string.IsNullOrWhiteSpace(Name) ? "Dashboard" : Name.Trim();
            page.Tiles = tiles;
            page.Grid = CustomPageConfig.CurrentGrid; // the tiles above are measured in the current grid
        });
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Settings;
using Rigsight.Models;

namespace Rigsight.ViewModels;

/// <summary>One tile on a custom page: what it shows, and where it sits on the grid.</summary>
public sealed partial class TileViewModel : ObservableObject
{
    public TileViewModel(TileConfig config, CustomPageViewModel page)
    {
        Id = config.Id;
        Kind = config.Kind;
        SensorRef = config.Sensor;
        Page = page;
        _x = config.X;
        _y = config.Y;
        _w = config.W;
        _h = config.H;
        Definition = TileCatalog.ForTile(Kind, SensorRef);
        ResolveSensor();
    }

    /// <summary>The dashed outline showing where a dragged tile will land.</summary>
    private TileViewModel(TileViewModel dragged)
    {
        Id = "placeholder";
        Kind = "placeholder";
        Page = dragged.Page;
        IsPlaceholder = true;
        CopyPosition(dragged);
    }

    public static TileViewModel PlaceholderFor(TileViewModel dragged) => new(dragged);

    public string Id { get; }
    public string Kind { get; }
    public string? SensorRef { get; }
    public TileKind? Definition { get; }
    public CustomPageViewModel Page { get; }
    public bool IsPlaceholder { get; }

    // Data the tile templates bind to.
    public LiveData Live => Page.Live;
    public HomeViewModel Home => Page.Home;
    public CrashesViewModel Crashes => Page.Crashes;

    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;
    [ObservableProperty] private int _w;
    [ObservableProperty] private int _h;

    [ObservableProperty] private bool _isDragging;

    /// <summary>Where the dragged tile is drawn (pixels, relative to the grid) while it follows the mouse.</summary>
    public Point DragPosition { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Accent))]
    private SensorItem? _sensor;

    public void ResolveSensor()
    {
        if (Kind == "sensor") Sensor = Live.Resolve(SensorRef);
    }

    // A sensor renamed on All sensors renames its tile. Listened to weakly: the sensor outlives a removed tile.
    partial void OnSensorChanged(SensorItem? oldValue, SensorItem? newValue)
    {
        if (oldValue is not null) PropertyChangedEventManager.RemoveHandler(oldValue, OnSensorRenamed, nameof(SensorItem.DisplayName));
        if (newValue is not null) PropertyChangedEventManager.AddHandler(newValue, OnSensorRenamed, nameof(SensorItem.DisplayName));
    }

    private void OnSensorRenamed(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Title));

    public string Title => Kind == "sensor"
        ? Definition?.Sensor == SensorRef ? Definition!.Title : Sensor?.DisplayName ?? "Sensor"
        : Definition?.Title ?? Kind;

    /// <summary>CPU readings in the CPU color, GPU readings in the GPU color.</summary>
    public Brush Accent => (Sensor?.HardwareType, SensorRef) switch
    {
        ("Cpu", _) => Res("CpuBrush"),
        ("GpuNvidia" or "GpuAmd" or "GpuIntel", _) => Res("GpuBrush"),
        ("Memory", _) => Res("PurpleBrush"),
        _ => Res("AccentBrush"),
    };

    private static Brush Res(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.SteelBlue;

    public int MinW => Definition?.MinW ?? 1;
    public int MinH => Definition?.MinH ?? 1;

    [RelayCommand]
    private void Remove() => Page.Remove(this);

    public void CopyPosition(TileViewModel other)
    {
        X = other.X;
        Y = other.Y;
        W = other.W;
        H = other.H;
    }

    public TileConfig ToConfig() => new() { Id = Id, Kind = Kind, Sensor = SensorRef, X = X, Y = Y, W = W, H = H };
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Models;

namespace Rigsight.ViewModels;

/// <summary>A preset as the picker shows it: what it will hold on this PC.</summary>
public sealed record PresetCard(DashboardPreset Preset, string Name, string Description, string Glyph, string Shows);

/// <summary>
/// "New dashboard": a blank page or a preset, offered only when this PC has the readings the preset is about, and made
/// without the tiles it can't fill.
/// </summary>
public sealed partial class PresetPickerViewModel(LiveData live, Action<DashboardPreset> create) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private List<PresetCard> _cards = [];

    [RelayCommand]
    private void Open()
    {
        var cards = DashboardPresets.All
            .Where(p => DashboardPresets.Offered(p, Available))
            .Select(p => new PresetCard(p, p.Name, p.Description, p.Glyph,
                string.Join(", ", DashboardPresets.TilesFor(p, Available).Select(DashboardPresets.Title).Distinct())))
            .ToList();
        var blank = DashboardPresets.Blank;
        cards.Add(new PresetCard(blank, blank.Name, blank.Description, blank.Glyph, ""));
        Cards = cards;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void Pick(PresetCard card)
    {
        IsOpen = false;
        create(card.Preset);
    }

    /// <summary>
    /// Whether this PC can fill the tile. Until the agent has sent its sensors nothing is known, so everything counts
    /// (a tile without a reading shows a dash until one arrives).
    /// </summary>
    public bool Available(PresetTile tile)
    {
        if (!live.HasHardware) return true;
        return tile.Kind switch
        {
            "cpu-gauge" => live.CpuTemp is not null,
            "gpu-gauge" => live.GpuTemp is not null,
            "temp-chart" => live.CpuTemp is not null || live.GpuTemp is not null,
            "fans" => live.Fans.Count > 0,
            "drives" => live.Drives.Count > 0,
            "sensor" => live.Resolve(tile.Sensor) is not null,
            _ => true, // history tiles: every PC has them
        };
    }
}

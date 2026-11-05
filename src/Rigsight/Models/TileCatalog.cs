using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Models;

/// <summary>A tile you can add to a custom page.</summary>
/// <param name="Kind">Which template renders it ("cpu-gauge", "sensor", "most-used"…).</param>
/// <param name="Sensor">For "sensor" tiles: "key:&lt;name&gt;" for a well-known sensor, or a sensor id.</param>
/// <param name="MinW">Smallest width (in grid cells) the tile can be resized to; likewise <paramref name="MinH"/>.</param>
public sealed record TileKind(string Kind, string Title, string Description, string Glyph, int W, int H,
    int MinW = 1, int MinH = 1, string? Sensor = null);

public sealed record TileGroup(string Name, IReadOnlyList<TileKind> Tiles);

public static class TileCatalog
{
    /// <summary>The Add box's groups, from the one list in Core (DashboardTiles), in the person's language.</summary>
    public static readonly IReadOnlyList<TileGroup> Groups =
    [
        .. DashboardTiles.All.GroupBy(t => t.Group).Select(g => new TileGroup(Lang.T(g.Key),
            [.. g.Select(t => new TileKind(t.Kind, Lang.T(t.Title), Lang.T(t.Description), t.Glyph, t.W, t.H, t.MinW, t.MinH, t.Sensor))])),
    ];

    /// <summary>What a new page starts with, so it isn't blank.</summary>
    public static IEnumerable<TileKind> Starter() =>
    [
        Find("cpu-gauge"), Find("gpu-gauge"), Find("sensor", "key:" + KeySensors.CpuLoad), Find("sensor", "key:" + KeySensors.GpuLoad),
        Find("sensor", "key:" + KeySensors.RamLoad), Find("sensor", "key:" + KeySensors.GpuPower), Find("today"),
    ];

    public static TileKind Find(string kind, string? sensor = null) =>
        Groups.SelectMany(g => g.Tiles).FirstOrDefault(t => t.Kind == kind && (sensor is null || t.Sensor == sensor))
        ?? Groups.SelectMany(g => g.Tiles).First(t => t.Kind == kind);

    /// <summary>The kind for a tile on a page (any sensor falls back to the generic single-reading tile).</summary>
    public static TileKind? ForTile(string kind, string? sensor) =>
        Groups.SelectMany(g => g.Tiles).FirstOrDefault(t => t.Kind == kind && t.Sensor == sensor)
        ?? Groups.SelectMany(g => g.Tiles).FirstOrDefault(t => t.Kind == kind);
}

using Rigsight.Core;

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
    public static readonly IReadOnlyList<TileGroup> Groups =
    [
        new("LIVE", [
            new("cpu-gauge", "CPU temperature", "A big gauge, colored by how hot it is.", "\uE9CA", 3, 4, MinW: 2, MinH: 3),
            new("gpu-gauge", "GPU temperature", "A big gauge, colored by how hot it is.", "\uE9CA", 3, 4, MinW: 2, MinH: 3),
            new("temp-chart", "Temperature chart", "CPU and GPU temperatures over the last few minutes.", "\uE9D9", 12, 4, MinW: 4, MinH: 3),
            new("fans", "Fans", "Every spinning fan and its speed.", "\uE9CA", 3, 4, MinW: 3, MinH: 3),
            new("drives", "Drives", "How full each drive is, and its temperature.", "\uEDA2", 6, 4, MinW: 3, MinH: 3),
            new("top-memory", "Top memory users", "The apps using the most memory right now.", "\uE964", 6, 4, MinW: 4, MinH: 3),
        ]),
        new("SINGLE READINGS", [
            new("sensor", "CPU load", "How busy the CPU is.", "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuLoad),
            new("sensor", "CPU power", "How much power the CPU draws.", "\uE945", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuPower),
            new("sensor", "CPU clock", "Average CPU clock speed.", "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuClock),
            new("sensor", "GPU load", "How busy the graphics card is.", "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuLoad),
            new("sensor", "GPU power", "How much power the graphics card draws.", "\uE945", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuPower),
            new("sensor", "GPU hot spot", "The hottest point on the graphics chip.", "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuHotSpot),
            new("sensor", "GPU memory temperature", "Video memory temperature (memory junction).", "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuMemJunction),
            new("sensor", "GPU fan", "Graphics card fan speed.", "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuFan),
            new("sensor", "Video memory", "How much of the GPU's memory is in use.", "\uE964", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuVramLoad),
            new("sensor", "RAM", "How much of your memory is in use.", "\uE964", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.RamLoad),
        ]),
        new("YOUR DAY", [
            new("today", "Today so far", "Active time, PC on and away.", "\uE823", 6, 2, MinW: 4, MinH: 2),
            new("most-used", "Most used today", "Your top apps today.", "\uECA5", 6, 4, MinW: 4, MinH: 3),
            new("insights", "What stands out", "Today's highlights, in plain words.", "\uE734", 6, 4, MinW: 4, MinH: 3),
            new("peaks", "Today's peaks", "Hottest CPU and GPU today, and the app responsible.", "\uE9CA", 6, 2, MinW: 3, MinH: 2),
            new("yesterday", "Yesterday", "Yesterday's active time and top apps.", "\uE708", 6, 4, MinW: 3, MinH: 3),
            new("crashes", "Crashes", "Crashes in the last 30 days, and the latest one.", "\uE7BA", 6, 4, MinW: 4, MinH: 3),
        ]),
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

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
        new(Lang.T("LIVE"), [
            new("cpu-gauge", Lang.T("CPU temperature"), Lang.T("A big gauge, colored by how hot it is."), "\uE9CA", 3, 4, MinW: 2, MinH: 3),
            new("gpu-gauge", Lang.T("GPU temperature"), Lang.T("A big gauge, colored by how hot it is."), "\uE9CA", 3, 4, MinW: 2, MinH: 3),
            new("temp-chart", Lang.T("Temperature chart"), Lang.T("CPU and GPU temperatures over the last few minutes."), "\uE9D9", 12, 4, MinW: 4, MinH: 3),
            new("fans", Lang.T("Fans"), Lang.T("Every spinning fan and its speed."), "\uEDA8", 3, 4, MinW: 3, MinH: 3),
            new("drives", Lang.T("Drives"), Lang.T("How full each drive is, and its temperature."), "\uEDA2", 6, 4, MinW: 3, MinH: 3),
            new("top-memory", Lang.T("Top memory users"), Lang.T("The apps using the most memory right now."), "\uE964", 6, 4, MinW: 4, MinH: 3),
        ]),
        new(Lang.T("SINGLE READINGS"), [
            new("sensor", Lang.T("CPU load"), Lang.T("How busy the CPU is."), "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuLoad),
            new("sensor", Lang.T("CPU power"), Lang.T("How much power the CPU draws."), "\uE945", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuPower),
            new("sensor", Lang.T("CPU clock"), Lang.T("Average CPU clock speed."), "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuClock),
            new("sensor", Lang.T("GPU load"), Lang.T("How busy the GPU is."), "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuLoad),
            new("sensor", Lang.T("GPU power"), Lang.T("How much power the GPU draws."), "\uE945", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuPower),
            new("sensor", Lang.T("GPU hot spot"), Lang.T("The hottest point on the GPU."), "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuHotSpot),
            new("sensor", Lang.T("GPU memory temperature"), Lang.T("Video memory temperature (memory junction)."), "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuMemJunction),
            new("sensor", Lang.T("GPU fan"), Lang.T("GPU fan speed."), "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuFan),
            new("sensor", Lang.T("Video memory"), Lang.T("How much of the GPU's memory is in use."), "\uE964", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuVramLoad),
            new("sensor", Lang.T("RAM"), Lang.T("How much of your memory is in use."), "\uE964", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.RamLoad),
        ]),
        new(Lang.T("INTERNET"), [
            new("net-speed", Lang.T("Internet speed"), Lang.T("Download and upload right now, over the last minute."), "\uE839", 6, 4, MinW: 4, MinH: 3),
            new("net-now", Lang.T("Using the internet now"), Lang.T("The apps moving the most right now."), "\uE839", 6, 4, MinW: 4, MinH: 3),
            new("net-today", Lang.T("Internet today"), Lang.T("Everything used today, in total, down, up and in the background."), "\uE839", 6, 2, MinW: 4, MinH: 2),
            new("net-apps", Lang.T("Top internet users today"), Lang.T("The apps that used the most internet today."), "\uE839", 6, 4, MinW: 4, MinH: 3),
            new("net-chart", Lang.T("Internet use by hour"), Lang.T("Each hour of today, split by app."), "\uE9D9", 12, 4, MinW: 6, MinH: 3),
            new("net-drops", Lang.T("Connection today"), Lang.T("Whether the internet dropped today."), "\uE839", 3, 2, MinW: 3, MinH: 2),
        ]),
        new(Lang.T("YOUR DAY"), [
            new("today", Lang.T("Today so far"), Lang.T("Active time, PC on and away."), "\uE823", 6, 2, MinW: 4, MinH: 2),
            new("most-used", Lang.T("Most used today"), Lang.T("Your top apps today."), "\uECA5", 6, 4, MinW: 4, MinH: 3),
            new("insights", Lang.T("What stands out"), Lang.T("Today's highlights, in plain words."), "\uE734", 6, 4, MinW: 4, MinH: 3),
            new("peaks", Lang.T("Today's peaks"), Lang.T("CPU peak and GPU peak today, and the app responsible."), "\uE9CA", 6, 2, MinW: 3, MinH: 2),
            new("yesterday", Lang.T("Yesterday"), Lang.T("Yesterday's active time and top apps."), "\uE708", 6, 4, MinW: 3, MinH: 3),
            new("crashes", Lang.T("Crashes"), Lang.T("Crashes in the last 30 days, and the latest one."), "\uE7BA", 6, 4, MinW: 4, MinH: 3),
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

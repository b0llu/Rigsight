using Rigsight.Core;

namespace Rigsight.Models;

/// <summary>A tile in a preset: a kind from <see cref="TileCatalog"/>, at its usual size unless given.</summary>
public sealed record PresetTile(string Kind, string? Sensor = null, int? W = null, int? H = null)
{
    public static PresetTile Reading(string key) => new("sensor", "key:" + key);
}

/// <summary>
/// A ready-made dashboard. Tiles this PC has no readings for are left out when it's made, and a preset missing any of
/// its <see cref="Needs"/> isn't offered at all (a cooling dashboard on a PC whose fans can't be read would be empty).
/// </summary>
/// <param name="Needs">Tiles the preset is about; without every one of them it isn't offered.</param>
public sealed record DashboardPreset(string Name, string Description, string Glyph, IReadOnlyList<PresetTile> Tiles, IReadOnlyList<PresetTile> Needs);

public static class DashboardPresets
{
    private static readonly PresetTile CpuGauge = new("cpu-gauge"), GpuGauge = new("gpu-gauge"), Fans = new("fans"), Chart = new("temp-chart");

    /// <summary>An empty page (offered last in the picker).</summary>
    public static readonly DashboardPreset Blank = new("Blank", "An empty page to fill with the tiles you want.", "\uE710", [], []);

    public static readonly IReadOnlyList<DashboardPreset> All =
    [
        new("Essentials", "Temperatures, load and power at a glance, and your day so far.", "\uE80F",
            [.. TileCatalog.Starter().Select(t => new PresetTile(t.Kind, t.Sensor))], []),
        new("Gaming", "Graphics card heat, load and power, with the CPU beside it.", "\uE7FC",
            [GpuGauge, CpuGauge, PresetTile.Reading(KeySensors.GpuLoad), PresetTile.Reading(KeySensors.GpuPower),
             PresetTile.Reading(KeySensors.GpuHotSpot), PresetTile.Reading(KeySensors.GpuVramLoad), Chart,
             PresetTile.Reading(KeySensors.GpuMemJunction), PresetTile.Reading(KeySensors.GpuFan), PresetTile.Reading(KeySensors.CpuLoad),
             PresetTile.Reading(KeySensors.RamLoad)],
            [GpuGauge]),
        new("Temperatures", "Every temperature worth watching, how it moved, and today's highs.", "\uE9CA",
            [CpuGauge, GpuGauge, PresetTile.Reading(KeySensors.GpuHotSpot), PresetTile.Reading(KeySensors.GpuMemJunction),
             PresetTile.Reading(KeySensors.CpuPower), PresetTile.Reading(KeySensors.GpuPower), Chart, new("peaks"), new("drives")],
            [CpuGauge]),
        new("Cooling", "Fan speeds next to the temperatures they answer to.", "\uEDA8",
            [new("fans", W: 6), CpuGauge, GpuGauge, Chart, PresetTile.Reading(KeySensors.GpuFan), PresetTile.Reading(KeySensors.CpuPower),
             PresetTile.Reading(KeySensors.GpuPower)],
            [Fans]),
        new("Workload", "How busy the CPU, graphics card and memory are, and what's using them.", "\uE9D9",
            [PresetTile.Reading(KeySensors.CpuLoad), PresetTile.Reading(KeySensors.CpuClock), PresetTile.Reading(KeySensors.CpuPower),
             PresetTile.Reading(KeySensors.RamLoad), PresetTile.Reading(KeySensors.GpuLoad), PresetTile.Reading(KeySensors.GpuVramLoad),
             PresetTile.Reading(KeySensors.GpuPower), new("top-memory"), new("most-used")],
            []),
        new("Network", "Your internet right now and today: speeds, who is using it, and each hour.", "\uE839",
            // Three full rows, nothing left over: today's figures and the connection; each hour; then who used it, who is
            // using it, and the speed.
            [new("net-today", W: 8), new("net-drops", W: 4), new("net-chart"), new("net-apps", W: 4), new("net-now", W: 4), new("net-speed", W: 4)],
            []),
        new("My day", "Time on the PC, the apps you used, what stood out and any crashes.", "\uE823",
            [new("today"), new("peaks"), new("most-used"), new("insights"), new("yesterday"), new("crashes")],
            []),
    ];

    /// <summary>The preset's tiles this PC can show (all of them while the readings haven't arrived yet).</summary>
    public static List<PresetTile> TilesFor(DashboardPreset preset, Func<PresetTile, bool> available) =>
        [.. preset.Tiles.Where(available)];

    /// <summary>Whether to offer the preset here: everything it's about can be read, and it would hold at least three tiles.</summary>
    public static bool Offered(DashboardPreset preset, Func<PresetTile, bool> available) =>
        preset.Needs.All(available) && preset.Tiles.Count(available) >= 3;

    /// <summary>A tile's name as the add-tile list shows it ("GPU hot spot", "Fans").</summary>
    public static string Title(PresetTile tile) => TileCatalog.ForTile(tile.Kind, tile.Sensor)?.Title ?? tile.Kind;
}

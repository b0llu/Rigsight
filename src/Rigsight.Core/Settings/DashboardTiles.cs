namespace Rigsight.Core.Settings;

/// <summary>
/// A tile a dashboard can have: its group in the Add box, its kind (which template draws it), its title and what it
/// shows (English, marked with Lang.Key and shown through Lang.T), its icon and sizes, and for a single reading its
/// sensor ("key:&lt;name&gt;").
/// </summary>
public sealed record DashboardTile(string Group, string Kind, string Title, string Description, string Glyph, int W, int H,
    int MinW = 1, int MinH = 1, string? Sensor = null);

/// <summary>
/// Every tile a dashboard can have, in the Add box's order: built once here for the app's Add box (Models.TileCatalog)
/// and the AI's dashboards look-up, which used to see only the tiles already placed and told the person it couldn't
/// see what could be added (the user, 2026-11-05).
/// </summary>
public static class DashboardTiles
{
    public static readonly IReadOnlyList<DashboardTile> All =
    [
        new(Lang.Key("LIVE"), "cpu-gauge", Lang.Key("CPU temperature"), Lang.Key("A big gauge, colored by how hot it is."), "\uE9CA", 3, 4, MinW: 2, MinH: 3),
        new(Lang.Key("LIVE"), "gpu-gauge", Lang.Key("GPU temperature"), Lang.Key("A big gauge, colored by how hot it is."), "\uE9CA", 3, 4, MinW: 2, MinH: 3),
        new(Lang.Key("LIVE"), "temp-chart", Lang.Key("Temperature chart"), Lang.Key("CPU and GPU temperatures over the last few minutes."), "\uE9D9", 12, 4, MinW: 4, MinH: 3),
        new(Lang.Key("LIVE"), "fans", Lang.Key("Fans"), Lang.Key("Every spinning fan and its speed."), "\uEDA8", 3, 4, MinW: 3, MinH: 3),
        new(Lang.Key("LIVE"), "drives", Lang.Key("Drives"), Lang.Key("How full each drive is, and its temperature."), "\uEDA2", 6, 4, MinW: 3, MinH: 3),
        new(Lang.Key("LIVE"), "top-memory", Lang.Key("Top memory users"), Lang.Key("The apps using the most memory right now."), "\uE964", 6, 4, MinW: 4, MinH: 3),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("CPU load"), Lang.Key("How busy the CPU is."), "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuLoad),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("CPU power"), Lang.Key("How much power the CPU draws."), "\uE945", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuPower),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("CPU clock"), Lang.Key("Average CPU clock speed."), "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.CpuClock),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("GPU load"), Lang.Key("How busy the GPU is."), "\uE9D9", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuLoad),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("GPU power"), Lang.Key("How much power the GPU draws."), "\uE945", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuPower),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("GPU hot spot"), Lang.Key("The hottest point on the GPU."), "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuHotSpot),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("GPU memory temperature"), Lang.Key("Video memory temperature (memory junction)."), "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuMemJunction),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("GPU fan"), Lang.Key("GPU fan speed."), "\uE9CA", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuFan),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("Video memory"), Lang.Key("How much of the GPU's memory is in use."), "\uE964", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.GpuVramLoad),
        new(Lang.Key("SINGLE READINGS"), "sensor", Lang.Key("RAM"), Lang.Key("How much of your memory is in use."), "\uE964", 3, 2, MinW: 2, MinH: 2, Sensor: "key:" + KeySensors.RamLoad),
        new(Lang.Key("INTERNET"), "net-speed", Lang.Key("Internet speed"), Lang.Key("Download and upload right now, over the last minute."), "\uE839", 6, 4, MinW: 4, MinH: 3),
        new(Lang.Key("INTERNET"), "net-now", Lang.Key("Using the internet now"), Lang.Key("The apps moving the most right now."), "\uE839", 6, 4, MinW: 4, MinH: 3),
        new(Lang.Key("INTERNET"), "net-today", Lang.Key("Internet today"), Lang.Key("Everything used today, in total, down, up and in the background."), "\uE839", 6, 2, MinW: 4, MinH: 2),
        new(Lang.Key("INTERNET"), "net-apps", Lang.Key("Top internet users today"), Lang.Key("The apps that used the most internet today."), "\uE839", 6, 4, MinW: 4, MinH: 3),
        new(Lang.Key("INTERNET"), "net-chart", Lang.Key("Internet use by hour"), Lang.Key("Each hour of today, split by app."), "\uE9D9", 12, 4, MinW: 6, MinH: 3),
        new(Lang.Key("INTERNET"), "net-drops", Lang.Key("Connection today"), Lang.Key("Whether the internet dropped today."), "\uE839", 3, 2, MinW: 3, MinH: 2),
        new(Lang.Key("YOUR DAY"), "today", Lang.Key("Today so far"), Lang.Key("Active time, PC on and away."), "\uE823", 6, 2, MinW: 4, MinH: 2),
        new(Lang.Key("YOUR DAY"), "most-used", Lang.Key("Most used today"), Lang.Key("Your top apps today."), "\uE8A9", 6, 4, MinW: 4, MinH: 3),
        new(Lang.Key("YOUR DAY"), "insights", Lang.Key("What stands out"), Lang.Key("Today's highlights, in plain words."), "\uE734", 6, 4, MinW: 4, MinH: 3),
        new(Lang.Key("YOUR DAY"), "peaks", Lang.Key("Today's peaks"), Lang.Key("CPU peak and GPU peak today, and the app responsible."), "\uE9CA", 6, 2, MinW: 3, MinH: 2),
        new(Lang.Key("YOUR DAY"), "yesterday", Lang.Key("Yesterday"), Lang.Key("Yesterday's active time and top apps."), "\uE708", 6, 4, MinW: 3, MinH: 3),
        new(Lang.Key("YOUR DAY"), "crashes", Lang.Key("Crashes"), Lang.Key("Crashes in the last 30 days, and the latest one."), "\uE7BA", 6, 4, MinW: 4, MinH: 3),
    ];

    /// <summary>The tile for a tile on a page: its own sensor's, or the first of its kind (any other sensor is a single reading).</summary>
    public static DashboardTile? For(string kind, string? sensor) =>
        All.FirstOrDefault(t => t.Kind == kind && t.Sensor == sensor) ?? All.FirstOrDefault(t => t.Kind == kind);
}

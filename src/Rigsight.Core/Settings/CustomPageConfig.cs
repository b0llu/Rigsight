namespace Rigsight.Core.Settings;

/// <summary>A page the user builds from tiles ("My pages" in the sidebar).</summary>
public sealed class CustomPageConfig
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "My page";
    public List<TileConfig> Tiles { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];
}

/// <summary>
/// One tile on a custom page. Position and size are in grid cells: the page is
/// <see cref="Columns"/> cells wide and grows downwards as needed.
/// </summary>
public sealed class TileConfig
{
    public const int Columns = 4;
    public const int MaxHeight = 4;

    public string Id { get; set; } = CustomPageConfig.NewId();

    /// <summary>What the tile shows, e.g. "cpu-gauge", "sensor", "most-used".</summary>
    public string Kind { get; set; } = "";

    /// <summary>For "sensor" tiles: a sensor identifier, or "key:&lt;name&gt;" for one of the well-known sensors.</summary>
    public string? Sensor { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
}

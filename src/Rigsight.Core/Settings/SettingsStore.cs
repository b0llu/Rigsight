using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rigsight.Core.Settings;

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RigsightSettings Load()
    {
        try
        {
            if (File.Exists(RigsightPaths.SettingsFile))
                return Normalize(Deserialize(File.ReadAllText(RigsightPaths.SettingsFile)));
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
        return Normalize(new RigsightSettings());
    }

    public static void Save(RigsightSettings settings)
    {
        try
        {
            Directory.CreateDirectory(RigsightPaths.DataDir);
            var tmp = RigsightPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, Serialize(settings));
            File.Move(tmp, RigsightPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
    }

    public static string Serialize(RigsightSettings s) => JsonSerializer.Serialize(s, JsonOptions);

    public static RigsightSettings Deserialize(string json) =>
        Normalize(JsonSerializer.Deserialize<RigsightSettings>(json, JsonOptions) ?? new RigsightSettings());

    private const int CurrentVersion = 2;

    /// <summary>Repairs settings from older versions or hand edits (missing widgets, out-of-range numbers…).</summary>
    private static RigsightSettings Normalize(RigsightSettings s)
    {
        if (s.SettingsVersion < 2)
        {
            // v2: the slim bar is the default widget.
            s.Widgets = WidgetConfig.Defaults();
        }
        s.SettingsVersion = CurrentVersion;

        foreach (var style in Enum.GetValues<WidgetStyle>())
            if (s.Widgets.All(w => w.Style != style))
                s.Widgets.Add(new WidgetConfig { Style = style });
        s.Widgets = [.. s.Widgets.GroupBy(w => w.Style).Select(g => g.First()).OrderBy(w => w.Style)];

        foreach (var w in s.Widgets)
        {
            w.Opacity = Math.Clamp(w.Opacity, 0.3, 1.0);
            w.Scale = Math.Clamp(w.Scale, 0.6, 2.0);
        }

        var t = s.Tracking;
        t.SensorIntervalMs = Math.Clamp(t.SensorIntervalMs, 500, 30_000);
        t.ProcessIntervalSeconds = Math.Clamp(t.ProcessIntervalSeconds, 2, 60);
        t.IdleMinutes = Math.Clamp(t.IdleMinutes, 1, 120);
        t.KeepDetailedDays = Math.Clamp(t.KeepDetailedDays, 7, 3650);
        t.KeepHistoryDays = Math.Clamp(t.KeepHistoryDays, 30, 3650);
        s.LiveRefreshMs = Math.Clamp(s.LiveRefreshMs, 250, 10_000);

        foreach (var page in s.CustomPages)
        {
            if (string.IsNullOrWhiteSpace(page.Id)) page.Id = CustomPageConfig.NewId();
            if (string.IsNullOrWhiteSpace(page.Name)) page.Name = "My page";
            page.Tiles = [.. page.Tiles.Where(tile => !string.IsNullOrEmpty(tile.Kind)).DistinctBy(tile => tile.Id)];
            foreach (var tile in page.Tiles)
            {
                tile.W = Math.Clamp(tile.W, 1, TileConfig.Columns);
                tile.H = Math.Clamp(tile.H, 1, TileConfig.MaxHeight);
                tile.X = Math.Clamp(tile.X, 0, TileConfig.Columns - tile.W);
                tile.Y = Math.Max(0, tile.Y);
            }
        }
        s.CustomPages = [.. s.CustomPages.DistinctBy(p => p.Id)];

        // JSON loses the case-insensitive comparers.
        s.AppNames = new Dictionary<string, string>(s.AppNames, StringComparer.OrdinalIgnoreCase);
        s.AppCategories = new Dictionary<string, AppCategory>(s.AppCategories, StringComparer.OrdinalIgnoreCase);
        return s;
    }
}

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

    public static RigsightSettings Load() => Load(RigsightPaths.SettingsFile);

    /// <summary>Reads the settings file at <paramref name="path"/>: <see cref="RigsightPaths.SettingsFile"/>, or a test's.</summary>
    public static RigsightSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return Normalize(Deserialize(File.ReadAllText(path)));
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
        return Normalize(new RigsightSettings());
    }

    public static void Save(RigsightSettings settings) => Save(settings, RigsightPaths.SettingsFile);

    public static void Save(RigsightSettings settings, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(settings));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
    }

    public static string Serialize(RigsightSettings s) => JsonSerializer.Serialize(s, JsonOptions);

    public static RigsightSettings Deserialize(string json) =>
        Normalize(JsonSerializer.Deserialize<RigsightSettings>(json, JsonOptions) ?? new RigsightSettings());

    private const int CurrentVersion = 6;

    /// <summary>Repairs settings from older versions or hand edits (missing widgets, out-of-range numbers…).</summary>
    private static RigsightSettings Normalize(RigsightSettings s)
    {
        // A hand edit can leave null where a section or list belongs: that part starts from its defaults, the rest is kept.
        s.Widgets = s.Widgets is null ? WidgetConfig.Defaults() : [.. s.Widgets.Where(w => w is not null)];
        s.Tracking ??= new TrackingSettings();
        s.Tracking.ExcludedApps ??= [];
        s.Alerts ??= new AlertSettings();
        s.CustomPages = [.. (s.CustomPages ?? []).Where(p => p is not null)];
        s.SensorLabels ??= [];
        s.HiddenSensors ??= [];
        s.CollapsedHardware ??= [];
        s.HardwareOrder ??= [];
        s.StartPage ??= "home";

        if (s.SettingsVersion < 2)
        {
            // v2: the slim bar is the default widget.
            s.Widgets = WidgetConfig.Defaults();
        }
        if (s.SettingsVersion < 3)
        {
            // v3: the overlay replaced "only show this widget over fullscreen apps".
            foreach (var w in s.Widgets.Where(w => w.Visibility == WidgetVisibility.OnlyInFullscreen))
            {
                w.Visibility = WidgetVisibility.Always;
                w.Enabled = false;
            }
        }
        if (s.SettingsVersion < 4 && s.Overlay is { } overlay)
        {
            // v4: the overlay can show frame rate; turn it on once for existing setups.
            overlay.Metrics ??= [];
            if (!overlay.Metrics.Contains(OverlayMetric.Fps)) overlay.Metrics.Add(OverlayMetric.Fps);
            if (!overlay.Metrics.Contains(OverlayMetric.OnePercentLow)) overlay.Metrics.Add(OverlayMetric.OnePercentLow);
        }
        if (s.SettingsVersion < 5)
        {
            // v5: one retention for all history. "Forever" used to be stored as ten years; it now means never delete.
            if (s.Tracking.KeepHistoryDays >= 3650) s.Tracking.KeepHistoryDays = 0;
        }
        // v6: separate background and content opacity. The old single value faded both, so both start there
        // (the widgets and overlay look exactly as before).
        foreach (var w in s.Widgets)
            if (w.Opacity is double old) (w.BackgroundOpacity, w.ContentOpacity, w.Opacity) = (old, old, null);
        if (s.Overlay?.Opacity is double oldOverlay)
            (s.Overlay.BackgroundOpacity, s.Overlay.ContentOpacity, s.Overlay.Opacity) = (oldOverlay, oldOverlay, null);
        s.SettingsVersion = CurrentVersion;

        if (s.Theme is not ("dark" or "light" or "system")) s.Theme = "dark";

        foreach (var style in Enum.GetValues<WidgetStyle>())
            if (s.Widgets.All(w => w.Style != style))
                s.Widgets.Add(new WidgetConfig { Style = style });
        s.Widgets = [.. s.Widgets.GroupBy(w => w.Style).Select(g => g.First()).OrderBy(w => w.Style)];

        foreach (var w in s.Widgets)
        {
            if (w.Theme == WidgetTheme.Black) w.Theme = WidgetTheme.Dark;
            w.BackgroundOpacity = Math.Clamp(w.BackgroundOpacity, 0, 1.0);
            w.ContentOpacity = Math.Clamp(w.ContentOpacity, 0.2, 1.0);
            w.Scale = Math.Clamp(w.Scale, 0.6, 2.0);
        }

        var o = s.Overlay ??= new OverlaySettings();
        o.BackgroundOpacity = Math.Clamp(o.BackgroundOpacity, 0, 1.0);
        o.ContentOpacity = Math.Clamp(o.ContentOpacity, 0.2, 1.0);
        o.Scale = Math.Clamp(o.Scale, 0.6, 2.0);
        if (!Hotkey.TryParse(o.Hotkey, out _)) o.Hotkey = OverlaySettings.DefaultHotkey;
        o.Metrics = [.. (o.Metrics ?? []).Where(m => Enum.IsDefined(m)).Distinct().Order()];
        o.Sensors = [.. (o.Sensors ?? []).Where(x => !string.IsNullOrWhiteSpace(x?.Id)).DistinctBy(x => x.Id).Take(OverlaySettings.MaxSensors)];
        foreach (var x in o.Sensors)
        {
            x.Label = string.IsNullOrWhiteSpace(x.Label) ? null : x.Label.Trim();
            if (x.Label is { Length: > OverlaySettings.MaxLabelLength }) x.Label = x.Label[..OverlaySettings.MaxLabelLength];
        }

        s.MutedCrashApps = [.. (s.MutedCrashApps ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase)];

        var t = s.Tracking;
        t.SensorIntervalMs = Math.Clamp(t.SensorIntervalMs, 500, 30_000);
        t.ProcessIntervalSeconds = Math.Clamp(t.ProcessIntervalSeconds, 2, 60);
        t.IdleMinutes = Math.Clamp(t.IdleMinutes, 1, 120);
        // Snap to the choices Settings offers: 3 months, 1 year, 2 years, forever (0).
        t.KeepHistoryDays = t.KeepHistoryDays switch
        {
            <= 0 => 0,
            <= 180 => 90,
            <= 547 => 365,
            <= 1500 => 730,
            _ => 0,
        };
        s.LiveRefreshMs = Math.Clamp(s.LiveRefreshMs, 250, 10_000);
        // The temperature chart offers 5 minutes, 1 hour, 6 hours, 24 hours and today (0: since midnight).
        if (s.ChartWindowSeconds is not (0 or 300 or 3600 or 21600 or 86400))
            s.ChartWindowSeconds = s.ChartWindowSeconds <= 300 ? 300 : 3600;

        foreach (var page in s.CustomPages)
        {
            if (string.IsNullOrWhiteSpace(page.Id)) page.Id = CustomPageConfig.NewId();
            if (string.IsNullOrWhiteSpace(page.Name)) page.Name = "Dashboard";
            page.Tiles = [.. (page.Tiles ?? []).Where(tile => !string.IsNullOrEmpty(tile?.Kind)).DistinctBy(tile => tile.Id)];
            if (page.Grid < CustomPageConfig.CurrentGrid)
            {
                // 4 columns → 12, and each row → two half-height rows: same look, finer resizing.
                foreach (var tile in page.Tiles)
                {
                    tile.X *= 3;
                    tile.W *= 3;
                    tile.Y *= 2;
                    tile.H *= 2;
                }
                page.Grid = CustomPageConfig.CurrentGrid;
            }
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
        s.AppNames = CaseInsensitive(s.AppNames);
        s.AppCategories = CaseInsensitive(s.AppCategories);
        return s;
    }

    /// <summary>A copy that finds keys in any case. Of keys differing only in case (from a copy that lost its comparer), the last wins.</summary>
    private static Dictionary<string, T> CaseInsensitive<T>(Dictionary<string, T>? source)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source ?? []) result[key] = value;
        return result;
    }
}

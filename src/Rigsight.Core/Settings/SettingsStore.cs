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
    /// <remarks>
    /// A file that's there but can't be read (cut short by a power loss, a hand edit gone wrong) must not turn into
    /// defaults: the next save would write those over everything the user had set up. The copy from the save before is
    /// used instead, and the unreadable file is kept beside it (".bad"). Only with neither does it start from defaults.
    /// </remarks>
    public static RigsightSettings Load(string path)
    {
        if (!File.Exists(path)) return Normalize(new RigsightSettings());
        if (Read(path) is { } settings) return settings;
        try { File.Copy(path, path + BadSuffix, overwrite: true); }
        catch (Exception ex) { Log.Error("settings", ex); }
        if (File.Exists(path + BackupSuffix) && Read(path + BackupSuffix) is { } previous)
        {
            Log.Write("settings", "The settings file couldn't be read: using the copy from the save before (the unreadable one is kept as settings.json.bad)");
            return previous;
        }
        Log.Write("settings", "The settings file couldn't be read and there's no earlier copy: starting from defaults (the unreadable one is kept as settings.json.bad)");
        return Normalize(new RigsightSettings());
    }

    internal const string BackupSuffix = ".bak";
    internal const string BadSuffix = ".bad";

    /// <summary>The settings in <paramref name="path"/>, or null when it can't be read. A file in use for a moment (being saved, scanned) is tried again.</summary>
    private static RigsightSettings? Read(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return Normalize(Deserialize(File.ReadAllText(path)));
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                Log.Error("settings", ex);
                return null;
            }
        }
    }

    public static void Save(RigsightSettings settings) => Save(settings, RigsightPaths.SettingsFile);

    /// <summary>Written whole to a file beside it, then swapped in; the file it replaces stays as the ".bak" copy (see <see cref="Load(string)"/>).</summary>
    public static void Save(RigsightSettings settings, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(settings));
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, path + BackupSuffix);
                    return;
                }
                catch (IOException)
                {
                    // The copy can't be kept right now (in use): saving matters more.
                }
            }
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

    private const int CurrentVersion = 9;

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
        // v7: stepping aside for RGB and fan apps. Settings from an earlier version belong to a PC where Rigsight already
        // works, so the first-start check is considered done and the switch stays off. (A new install has no file:
        // SettingsVersion 0, and its agent does the check before the first hardware scan.)
        if (s.SettingsVersion is > 0 and < 7) s.HardwareAppsChecked = true;
        // v8: "What's new" after an update. Settings from an earlier version mean an update from 0.6.3 or before: it
        // shows what came after that (a new install has no file, and sees nothing).
        if (s.SettingsVersion is > 0 and < 8) s.WhatsNewSeen ??= "0.6.3";
        // v9: grayscale taskbar readings. A new install starts in grayscale; an earlier setup keeps its colours.
        if (s.SettingsVersion is > 0 and < 9) s.TrayGrayscale = false;
        // The "record" switch became pausing until resumed (one way to stop tracking, and one way to start it again).
        if (s.Tracking.Enabled == false) s.Tracking.PausedUntil = -1;
        s.Tracking.Enabled = null;
        s.SettingsVersion = CurrentVersion;

        if (s.Theme is not ("dark" or "grey" or "light" or "system")) s.Theme = "dark";
        if (string.IsNullOrWhiteSpace(s.Accent)) s.Accent = "mono";
        s.Sidebar ??= new();
        s.Sidebar.Order ??= [];
        s.Sidebar.Hidden ??= [];
        s.Sidebar.Collapsed ??= [];
        s.Sidebar.Hidden.Remove("home");

        // One of each built-in widget, in order, then the user's own (each with its own identifier, name and layout).
        var widgets = (s.Widgets ?? []).Where(w => w is not null && Enum.IsDefined(w.Style)).ToList();
        foreach (var style in WidgetCatalog.BuiltIn)
            if (widgets.All(w => w.Style != style))
                widgets.Add(new WidgetConfig { Style = style, Grayscale = true });
        var builtIn = widgets.Where(w => w.Style != WidgetStyle.Custom).GroupBy(w => w.Style).Select(g => g.First()).OrderBy(w => w.Style);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var custom = new List<WidgetConfig>();
        foreach (var w in widgets.Where(w => w.Style == WidgetStyle.Custom).Take(WidgetCatalog.MaxCustom))
        {
            if (string.IsNullOrWhiteSpace(w.Id) || !w.Id.StartsWith("custom-", StringComparison.Ordinal) || !ids.Add(w.Id))
                ids.Add(w.Id = WidgetCatalog.NewId());
            string name = (w.Name ?? "").Trim();
            w.Name = name.Length == 0 ? WidgetCatalog.Title(WidgetStyle.Custom) : name.Length > WidgetCatalog.MaxNameLength ? name[..WidgetCatalog.MaxNameLength] : name;
            if (w.Layout is not { } layout || !WidgetCatalog.CustomLayouts.Contains(layout)) w.Layout = WidgetLayout.Bar;
            custom.Add(w);
        }
        s.Widgets = [.. builtIn, .. custom];

        foreach (var w in s.Widgets)
        {
            if (w.Style != WidgetStyle.Custom) (w.Id, w.Name, w.Layout) = (w.Style.ToString(), null, null);
            WidgetCatalog.Clean(w);
            if (w.Theme == WidgetTheme.Black) w.Theme = WidgetTheme.Dark;
            w.BackgroundOpacity = Math.Clamp(w.BackgroundOpacity, 0, 1.0);
            w.ContentOpacity = Math.Clamp(w.ContentOpacity, 0.2, 1.0);
            w.Scale = Math.Clamp(w.Scale, 0.6, 2.0);
        }

        var o = s.Overlay ??= new OverlaySettings { Grayscale = true };
        o.BackgroundOpacity = Math.Clamp(o.BackgroundOpacity, 0, 1.0);
        o.ContentOpacity = Math.Clamp(o.ContentOpacity, 0.2, 1.0);
        o.Scale = Math.Clamp(o.Scale, 0.6, 2.0);
        // Before 0.8 the overlay went in one of four corners: no anchor means that corner's (see OverlayPlacement.AnchorOf).
        if (o.Anchor is int anchor) o.Anchor = Math.Clamp(anchor, 0, 8);
        o.OffsetX = double.IsFinite(o.OffsetX) ? Math.Clamp(o.OffsetX, -1, 1) : 0;
        o.OffsetY = double.IsFinite(o.OffsetY) ? Math.Clamp(o.OffsetY, -1, 1) : 0;
        if (!Hotkey.TryParse(o.Hotkey, out _)) o.Hotkey = OverlaySettings.DefaultHotkey;
        o.Metrics = [.. (o.Metrics ?? []).Where(m => Enum.IsDefined(m)).Distinct().Order()];
        o.Sensors = [.. (o.Sensors ?? []).Where(x => !string.IsNullOrWhiteSpace(x?.Id)).DistinctBy(x => x.Id).Take(OverlaySettings.MaxSensors)];
        foreach (var x in o.Sensors)
        {
            x.Label = string.IsNullOrWhiteSpace(x.Label) ? null : x.Label.Trim();
            if (x.Label is { Length: > OverlaySettings.MaxLabelLength }) x.Label = x.Label[..OverlaySettings.MaxLabelLength];
        }

        s.TraySensors = [.. (s.TraySensors ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct()];
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
        // The temperature chart offers 5 minutes, 1 hour, 6 hours, 24 hours, a day (0: today since midnight, or an
        // earlier one), a week, a month and a year (604800, 2592000 and 31536000 stand for those: one on the calendar).
        if (s.ChartWindowSeconds is not (0 or 300 or 3600 or 21600 or 86400 or 604800 or 2592000 or 31536000))
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

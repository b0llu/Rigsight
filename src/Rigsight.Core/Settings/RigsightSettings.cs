namespace Rigsight.Core.Settings;

/// <summary>
/// All user settings. The agent is the only process that writes settings.json; the app sends
/// changes to the agent, which saves them and broadcasts the result back.
/// </summary>
public sealed class RigsightSettings
{
    /// <summary>Bumped when defaults change, so older settings files get migrated once.</summary>
    public int SettingsVersion { get; set; }

    public bool UseFahrenheit { get; set; }

    /// <summary>How often the app's live views refresh while it is open.</summary>
    public int LiveRefreshMs { get; set; } = 1000;

    public int ChartWindowSeconds { get; set; } = 300;

    public TrackingSettings Tracking { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public List<WidgetConfig> Widgets { get; set; } = WidgetConfig.Defaults();

    /// <summary>User-chosen display names for apps, keyed by exe name (e.g. "eldenring.exe").</summary>
    public Dictionary<string, string> AppNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User overrides for app categories, keyed by exe name.</summary>
    public Dictionary<string, AppCategory> AppCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-defined sensor names, keyed by sensor identifier.</summary>
    public Dictionary<string, string> SensorLabels { get; set; } = [];

    /// <summary>Identifiers of sensors hidden on the Sensors page.</summary>
    public HashSet<string> HiddenSensors { get; set; } = [];

    /// <summary>Hardware groups collapsed on the All sensors page (by name).</summary>
    public HashSet<string> CollapsedHardware { get; set; } = [];

    /// <summary>Pages the user built from tiles.</summary>
    public List<CustomPageConfig> CustomPages { get; set; } = [];

    /// <summary>Set once the agent has registered itself to start with Windows (so turning it off sticks).</summary>
    public bool StartupConfigured { get; set; }

    /// <summary>Last day (yyyy-MM-dd) the agent showed the daily recap notification.</summary>
    public string? LastRecapDay { get; set; }

    public RigsightSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this));
}

public sealed class TrackingSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Unix seconds until which tracking is paused; 0 = not paused, -1 = until resumed.</summary>
    public long PausedUntil { get; set; }

    /// <summary>Sensor sampling interval while the app is closed.</summary>
    public int SensorIntervalMs { get; set; } = 2000;

    /// <summary>How often per-app CPU/memory and window states are sampled.</summary>
    public int ProcessIntervalSeconds { get; set; } = 5;

    /// <summary>No keyboard/mouse input for this long counts as "away".</summary>
    public int IdleMinutes { get; set; } = 5;

    /// <summary>Count a fullscreen app (games, movies) as in use even without keyboard/mouse input.</summary>
    public bool FullscreenCountsAsActive { get; set; } = true;

    /// <summary>How long minute-by-minute data (timelines, temperature curves) is kept.</summary>
    public int KeepDetailedDays { get; set; } = 90;

    /// <summary>How long hourly per-app data (reports) is kept.</summary>
    public int KeepHistoryDays { get; set; } = 730;

    /// <summary>Apps (exe names) that are never recorded.</summary>
    public List<string> ExcludedApps { get; set; } = [];

    public bool IsPaused(long nowUnix) => !Enabled || PausedUntil == -1 || PausedUntil > nowUnix;
}

public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;
    public double CpuLimit { get; set; } = 85;
    public double GpuLimit { get; set; } = 83;
    public double GpuHotSpotLimit { get; set; } = 100;

    /// <summary>A limit must be exceeded for this long before alerting (ignores brief spikes).</summary>
    public int SustainSeconds { get; set; } = 10;

    public int CooldownMinutes { get; set; } = 10;

    /// <summary>Notify with a summary when a game session ends.</summary>
    public bool SessionSummaries { get; set; } = true;

    /// <summary>Notify with yesterday's recap the first time the PC is used each day.</summary>
    public bool DailyRecap { get; set; } = true;

    /// <summary>Game sessions shorter than this don't get a summary.</summary>
    public int SessionSummaryMinMinutes { get; set; } = 15;

    /// <summary>Tell me what happened when an app or game crashes (off by default: nobody wants a popup right after a crash).</summary>
    public bool CrashNotifications { get; set; }

    /// <summary>Hold recaps and summaries while a fullscreen app is in front; show them afterwards.</summary>
    public bool QuietDuringFullscreen { get; set; } = true;

    public NotificationStyle Style { get; set; } = NotificationStyle.Card;

    /// <summary>Seconds a notification card stays on screen.</summary>
    public int CardSeconds { get; set; } = 8;
}

public enum NotificationStyle
{
    /// <summary>Rigsight's own small card in the bottom-right corner.</summary>
    Card,
    /// <summary>Standard Windows notifications (appear in the notification center).</summary>
    Windows,
}

public enum AppCategory { Other, Game, Browser, Development, Communication, Media, Launcher, Productivity, System }

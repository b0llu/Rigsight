using System.Text.Json.Serialization;
using Rigsight.Core;

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

    /// <summary>How times of day are written: as Windows' region does, or always 12-hour or 24-hour (see <see cref="TimeText"/>).</summary>
    public ClockStyle ClockStyle { get; set; }

    /// <summary>The language the app and the agent show: "en" (built in) or a downloaded pack's code ("es").</summary>
    public string Language { get; set; } = LanguagePack.English;

    /// <summary>The app's colors: a theme's key from <see cref="ThemeCatalog"/> ("dark", "paper", "plum"...) or "system" (follow Windows).</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>
    /// The dark (or colorful) theme picked last: the one Same as Windows shows while Windows is dark, and the one the Dark
    /// look returns to. <see cref="LightTheme"/> is the same for the light ones (2026-11-05).
    /// </summary>
    public string DarkTheme { get; set; } = ThemeCatalog.DefaultDark;

    public string LightTheme { get; set; } = ThemeCatalog.DefaultLight;

    /// <summary>The app's accent: "mono" (the theme's own), a colour's name, or "windows" (follow Windows).</summary>
    public string Accent { get; set; } = "mono";

    /// <summary>
    /// With an accent picked, every part's colour becomes a shade of it, and so do the colours for how a reading is
    /// doing (temperatures, drive health, fullness). On, those stay green, amber and red.
    /// </summary>
    public bool KeepStatusColors { get; set; }

    /// <summary>How often the app's live views refresh while it is open.</summary>
    public int LiveRefreshMs { get; set; } = 1000;

    public int ChartWindowSeconds { get; set; } = 300;

    /// <summary>What the temperature graph's line is from an hour up: each minute's average ("Avg"), highest ("High") or lowest ("Low").</summary>
    public string ChartPlot { get; set; } = "Avg";

    public TrackingSettings Tracking { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public RewindSettings Rewind { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public List<WidgetConfig> Widgets { get; set; } = WidgetConfig.Defaults();
    /// <summary>A new install's overlay (like its widgets and taskbar readings) starts in grayscale.</summary>
    public OverlaySettings Overlay { get; set; } = new() { Grayscale = true };

    /// <summary>User-chosen display names for apps, keyed by exe name (e.g. "eldenring.exe").</summary>
    /// <remarks>Read into this case-insensitive dictionary (not a new one), so copies from the pipe keep matching any case.</remarks>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> AppNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User overrides for app categories, keyed by exe name.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, AppCategory> AppCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-defined sensor names, keyed by sensor identifier.</summary>
    public Dictionary<string, string> SensorLabels { get; set; } = [];

    /// <summary>Identifiers of sensors hidden on the Sensors page.</summary>
    public HashSet<string> HiddenSensors { get; set; } = [];

    /// <summary>Hardware groups collapsed on the All sensors page (by name).</summary>
    public HashSet<string> CollapsedHardware { get; set; } = [];

    /// <summary>The order the user dragged the All sensors groups into (by name); groups not listed follow in their usual order.</summary>
    public List<string> HardwareOrder { get; set; } = [];

    /// <summary>
    /// Which colour each app had on the Network page's chart, newest first, as "slot:exe" ("2:obs64.exe"), so an app
    /// keeps its colour from one period to the next. Remembered once there is a week of history. Lists from before
    /// 1.5.0 held just the exes, one per colour in order; they're read as that slot.
    /// </summary>
    public List<string> NetColorApps { get; set; } = [];
    public const int MaxNetColorApps = 24;

    /// <summary>One remembered colour: its slot and the app's exe (null: not one).</summary>
    public static (int Slot, string Exe)? ParseNetColor(string entry)
    {
        int colon = entry.IndexOf(':');
        return colon > 0 && int.TryParse(entry.AsSpan(0, colon), out int slot) && slot >= 0 && colon < entry.Length - 1
            ? (slot, entry[(colon + 1)..]) : null;
    }

    /// <summary>Sensors shown as numbers in the taskbar's notification area, in this order (by sensor id).</summary>
    public List<string> TraySensors { get; set; } = [];

    /// <summary>
    /// How <see cref="TraySensors"/> show: a strip in the taskbar (Windows 11), or notification-area icons. Since 1.3 the
    /// style of every reading not in <see cref="TrayStyles"/>, and what "Set all to" sets.
    /// </summary>
    public TrayStyle TrayStyle { get; set; }

    /// <summary>
    /// Readings with a style of their own (by sensor id), since 1.3: the taskbar draws each reading in its style, side by
    /// side in the list's order. A reading not listed shows as <see cref="TrayStyle"/>.
    /// </summary>
    public Dictionary<string, TrayStyle> TrayStyles { get; set; } = [];

    /// <summary>Readings with a size of their own (by sensor id, see <see cref="TraySize"/>); a reading not listed fits the taskbar.</summary>
    public Dictionary<string, TraySize> TraySizes { get; set; } = [];

    /// <summary>
    /// Readings whose look is drawn from another reading (by sensor id → the other's id): the number is its own, the
    /// ring, blocks or graph the other's ("Together": CPU temperature as the number, CPU load as its ring).
    /// </summary>
    public Dictionary<string, string> TrayLookFrom { get; set; } = [];

    /// <summary>Speeds on the taskbar in bits a second ("99 Mbps", as internet plans say them), not bytes ("12 MB/s").</summary>
    public bool TraySpeedBits { get; set; }

    /// <summary>
    /// The taskbar readings in the taskbar's text colour and grey, without the parts' colours (on a new install; settings
    /// from before 0.11 keep the colours they had).
    /// </summary>
    public bool TrayGrayscale { get; set; } = true;

    /// <summary>Where the strip sits: next to the clock, or at the taskbar's left end (with its icons centred).</summary>
    public TraySide TraySide { get; set; }

    /// <summary>
    /// Before 1.2: taskbar readings drawn on the strip with a graph behind the number (by sensor id). Only read from older
    /// settings: each becomes a <see cref="TrayLook.Area"/> in <see cref="TrayLooks"/>, so the strip looks as it did.
    /// </summary>
    public List<string> TrayGraphs { get; set; } = [];

    /// <summary>How each taskbar reading draws on the strip (by sensor id); a reading not listed is its number alone.</summary>
    public Dictionary<string, TrayLook> TrayLooks { get; set; } = [];

    /// <summary>
    /// Each taskbar reading on a tile of its own (the strip, and the icons in the part's colour); on for everyone from
    /// 1.2, readings or not (someone without any gets none added).
    /// </summary>
    public bool TrayTiles { get; set; } = true;

    /// <summary>How much room the strip's readings take (see <see cref="TrayWidth"/>).</summary>
    public TrayWidth TrayWidth { get; set; }

    /// <summary>The room between the readings (see <see cref="TrayGap"/>).</summary>
    public TrayGap TrayGap { get; set; }

    /// <summary>In two rows, each reading's name as one letter ("C", "L") instead of in full ("CPU", "Load").</summary>
    public bool TrayLetterNames { get; set; }

    /// <summary>
    /// In two rows, the readings with a box of their own (by sensor id); the others share a box with the next in
    /// <see cref="TraySensors"/>'s order, so the list's order is how they pair (see <see cref="TrayParts.Boxes"/>).
    /// </summary>
    public List<string> TrayAlone { get; set; } = [];

    /// <summary>
    /// 1.3: the readings that share their spot with the next in <see cref="TraySensors"/> (by sensor id): side by side on
    /// one tile, one over the other in two rows, or stacked in one icon. Null until the Taskbar page first writes it: till
    /// then the spots are as before 1.3 (the strip and icon per part by part, two rows by the list's order and
    /// <see cref="TrayAlone"/>; see <see cref="TrayParts.Spots"/>).
    /// </summary>
    public List<string>? TrayPairs { get; set; }

    /// <summary>1.3: readings in colour or grayscale of their own (true: grayscale); one not listed is like the rest (<see cref="TrayGrayscale"/>).</summary>
    public Dictionary<string, bool> TrayGrayOf { get; set; } = [];

    /// <summary>1.3: readings with a background of their own (true: on a tile); one not listed is like the rest (<see cref="TrayTiles"/>).</summary>
    public Dictionary<string, bool> TrayTilesOf { get; set; } = [];

    /// <summary>
    /// 1.3: readings whose name shows as one letter in two rows, set for each spot (both of a pair listed). Before 1.3
    /// one switch for all (<see cref="TrayLetterNames"/>), which still counts while <see cref="TrayPairs"/> is null.
    /// </summary>
    public List<string> TrayLetters { get; set; } = [];

    /// <summary>
    /// 1.3: readings whose graph is drawn behind their name, under their number (as 1.2 drew every graph), not beside them;
    /// each reading's own choice (the user: "per reading ... keep compact roomy standard completely different").
    /// </summary>
    public List<string> TrayGraphsBehind { get; set; } = [];

    /// <summary>Taskbar readings given colours of their own (by sensor id); a reading not listed looks as RigSight colours it.</summary>
    public Dictionary<string, TrayColor> TrayColors { get; set; } = [];

    /// <summary>
    /// The colours made with the picker's "+" for numbers, and for names and marks, each in the order made (the newest
    /// last, just before "+"), offered after the quick picks for every reading.
    /// </summary>
    public List<string> TrayNumberColors { get; set; } = [];
    public List<string> TrayNameColors { get; set; } = [];
    public List<string> TrayTileColors { get; set; } = [];
    public const int MaxTrayCustomColors = 30;

    /// <summary>Apps (exe names) whose crashes aren't counted, listed or notified about.</summary>
    public List<string> MutedCrashApps { get; set; } = [];

    public bool IsCrashMuted(string? exe) =>
        !string.IsNullOrEmpty(exe) && MutedCrashApps.Contains(exe, StringComparer.OrdinalIgnoreCase);

    /// <summary>Pages the user built from tiles.</summary>
    public List<CustomPageConfig> CustomPages { get; set; } = [];

    /// <summary>The sidebar's page order, hidden pages and folded sections.</summary>
    public SidebarSettings Sidebar { get; set; } = new();

    /// <summary>The page the app opens on: "home", another page's key, or "custom:&lt;id&gt;".</summary>
    public string StartPage { get; set; } = "home";

    /// <summary>
    /// New versions download in the background and install the next time Windows starts. Off: Rigsight only
    /// says when there's a new version.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// While an RGB or fan-control app (iCUE, Gigabyte Control Center, Armoury Crate…) is running, don't read the
    /// hardware it controls (motherboard sensor chip, fan hubs, power supply), so the two never talk to it at once.
    /// Off unless a fresh install finds one of those apps running when it first starts (see <see cref="HardwareAppsChecked"/>):
    /// a PC where Rigsight already works keeps reading everything.
    /// </summary>
    public bool YieldToHardwareApps { get; set; }

    /// <summary>
    /// The first start has looked for RGB and fan-control apps (turning <see cref="YieldToHardwareApps"/> on if it found
    /// one). Set for settings from before 0.6.0, so an update never turns it on by itself.
    /// </summary>
    public bool HardwareAppsChecked { get; set; }

    /// <summary>The sensor notice (what's left out and why) last seen in Settings, so the sidebar only points at a new one.</summary>
    public string? SensorNoticeSeen { get; set; }

    /// <summary>
    /// On a standard account the sidebar says once that some readings are off; set when that note was opened or the
    /// window it showed in was closed. Never shown again after, not after an update either: the person knows.
    /// </summary>
    public bool StandardNoteSeen { get; set; }

    /// <summary>
    /// On a PC where Rigsight starts for everyone who signs in: whether it starts for this person. "Start with Windows"
    /// in Settings, for an account that has no start-up task of its own to switch off.
    /// </summary>
    public bool StartAtSignIn { get; set; } = true;

    /// <summary>
    /// The newest version whose "What's new" has been shown ("0.6.4"). Empty on a new install, where nothing is new: the
    /// app then sets it without showing anything.
    /// </summary>
    public string? WhatsNewSeen { get; set; }

    /// <summary>A tip the first time each page opens: on for a new install only (2026-10-30), never after an update.</summary>
    public bool FirstVisitTips { get; set; }

    /// <summary>The pages whose first-visit tip was closed.</summary>
    public List<string> TipsSeen { get; set; } = [];

    /// <summary>
    /// The day (yyyy-MM-dd) a big card last showed: the welcome, What's new, or a rewind full screen. The support card
    /// never asks that day (one big thing at a time, 2026-10-30).
    /// </summary>
    public string? LastBigDay { get; set; }

    /// <summary>
    /// What happens after an update (Settings › General › Updates): the card opened by itself (the default), a What's new
    /// button in the title bar, or nothing. What's new stays in the ⋯ menu whichever is picked.
    /// </summary>
    public WhatsNewAfter WhatsNewAfter { get; set; } = WhatsNewAfter.Open;

    /// <summary>The switch this replaced: read once from settings from before (off becomes <see cref="WhatsNewAfter.Off"/>), then gone.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowWhatsNew { get; set; }

    /// <summary>
    /// The page an update's headline feature is on, dotted in the sidebar after its What's new was closed until it's
    /// opened ("storage"), with what its hover says ("New in 0.34.0: Storage, rebuilt"). Null when there's none.
    /// </summary>
    public string? NewFeaturePage { get; set; }
    public string? NewFeatureText { get; set; }

    /// <summary>The app window's zoom, Ctrl + wheel (1 = 100%; see <see cref="AppZoom"/>).</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>
    /// Where the app window was and how big, when it was last closed (its size before maximizing, in screen units), and
    /// whether it was maximized: it opens the same again (2026-10-27). Empty: the first opening, centred at its usual size.
    /// </summary>
    public double[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>The "Enjoying RigSight?" card's record (see <see cref="SupportAsk"/>).</summary>
    public SupportAskState Support { get; set; } = new();

    /// <summary>Last version the agent sent an "update ready" notification for (one each).</summary>
    public string? LastUpdateNotice { get; set; }

    /// <summary>Set once the agent has registered itself to start with Windows (so turning it off sticks).</summary>
    public bool StartupConfigured { get; set; }

    /// <summary>Last day (yyyy-MM-dd) the agent showed the daily recap notification.</summary>
    public string? LastRecapDay { get; set; }

    /// <summary>The last day (yyyy-MM-dd) whose recap was shown: each day's comes once, the next time the PC is turned on.</summary>
    public string? RecappedDay { get; set; }

    /// <summary>The folder exports were last saved in (each export makes its own folder there); null: Documents.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>Exports are tables for a spreadsheet (CSV) and pages for a browser, not laid-out text files (the format picked last).</summary>
    public bool ExportAsCsv { get; set; }

    /// <summary>The history file's last check and repair, as the agent found them (it alone writes this).</summary>
    public HistoryFileState HistoryFile { get; set; } = new();

    public RigsightSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this));
}

/// <summary>What the sidebar card and Settings → History say about the history file (see <see cref="Data.HistoryRepair"/>).</summary>
public sealed class HistoryFileState
{
    /// <summary>When the file was last checked, and why ("after the PC lost power", "when you asked").</summary>
    public DateTime? CheckedAt { get; set; }
    public string? CheckedWhy { get; set; }
    /// <summary>Being checked right now (Settings → History → Check now).</summary>
    public bool Checking { get; set; }
    /// <summary>Damage the check confirmed in a file still in use: old readings can't be read, new ones save.</summary>
    public bool Damaged { get; set; }
    /// <summary>"repaired", "whole" (repaired, nothing lost), "nothing", "waiting", "lists", or null.</summary>
    public string? Outcome { get; set; }
    public DateTime? RepairedAt { get; set; }
    /// <summary>What was kept and what wasn't, as the card says it.</summary>
    public string? Summary { get; set; }
    public string? DamagedCopy { get; set; }
    public string? WaitingReason { get; set; }
    /// <summary>The card was put away (it comes back with the next damage or repair).</summary>
    public bool Dismissed { get; set; }
}

public sealed class TrackingSettings
{
    /// <summary>Before 0.5.18: a separate "record" switch. Off is read once as paused until resumed, never written.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }

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

    /// <summary>
    /// The line check (1.3.0): a ping to this PC's router and to the internet provider's first router every few
    /// seconds, for the Network page's ping. Off, nothing is pinged and what was checked before stays.
    /// </summary>
    public bool PingChecks { get; set; } = true;

    /// <summary>
    /// How long history is kept, in days: minute-by-minute readings, hourly app time, sessions, crashes and drive
    /// fill all expire together, so every page covers the same dates. 0 keeps everything.
    /// </summary>
    public int KeepHistoryDays { get; set; } = 730;

    /// <summary>Apps (exe names) that are never recorded.</summary>
    public List<string> ExcludedApps { get; set; } = [];

    public bool IsPaused(long nowUnix) => PausedUntil == -1 || PausedUntil > nowUnix;
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

    /// <summary>
    /// Tell me what happened when an app or game crashes, or the PC goes down. On by default since the notification
    /// says what most likely happened and how sure that is (it was off before, and people took crashes "not coming up"
    /// for Rigsight missing them). An app that crashes again within two minutes is said once, not each time (see the agent's CrashNoticeGate).
    /// </summary>
    public bool CrashNotifications { get; set; } = true;

    /// <summary>
    /// Tell me when the internet drops and when it's back (1.4.0, the user's ask 2026-11-05): after 15 s down, then
    /// with how long it lasted; one "keeps dropping" instead of a stream (see the agent's DropNotices). A click opens the
    /// Network page on that day.
    /// </summary>
    public bool InternetDrops { get; set; } = true;

    /// <summary>Hold recaps and summaries while a fullscreen app is in front; show them afterwards.</summary>
    public bool QuietDuringFullscreen { get; set; } = true;

    public NotificationStyle Style { get; set; } = NotificationStyle.Card;

    /// <summary>Seconds a notification card stays on screen.</summary>
    public int CardSeconds { get; set; } = 8;
}

/// <summary>
/// How the taskbar readings show. <see cref="Strip"/>: a strip inside the taskbar, each part's readings over its name
/// (Windows 11; elsewhere the icons). <see cref="Icons"/>: an icon each. <see cref="Grouped"/>: an icon per part, up to two
/// readings stacked (see <see cref="TrayParts"/>). <see cref="TwoRows"/>: the readings in boxes of two, paired as the person
/// lists them (see <see cref="TrayParts.Boxes"/>), the name beside each number, no wider than the strip's numbers alone
/// (Windows 11; elsewhere an icon each). On Windows 11 RigSight draws every
/// style itself, at either end of the taskbar; on Windows 10 the icons are Windows' own.
/// </summary>
public enum TrayStyle { Strip, Icons, Grouped, TwoRows }

/// <summary>
/// Where the taskbar readings sit. <see cref="Left"/>: at the taskbar's left end, before Start, which needs the taskbar's
/// icons centred (Windows 11's own setting); left-aligned icons fill that end, so they stay next to the clock. Icons on
/// the left are drawn by RigSight itself (Windows puts its own tray icons by the clock only).
/// </summary>
public enum TraySide { Right, Left }

/// <summary>
/// How a reading draws on the strip, always under its number and name: <see cref="Number"/> alone; the last two minutes
/// as a <see cref="Area"/> (filled), <see cref="Line"/> or <see cref="Bars"/> graph behind its name; or where it is now
/// out of 100 as a <see cref="Level"/> behind its name, ten <see cref="Blocks"/> under it, or a <see cref="Ring"/>
/// beside the number. The last three only for readings with a top (see <see cref="TrayParts.HasTop"/>). Since 1.3 also a
/// <see cref="Half"/> ring (a gauge), a <see cref="Pie"/>, <see cref="Cores"/> (a thin bar per CPU core), a <see cref="Big"/>
/// number alone, a <see cref="Spark"/> graph beside the number, and for the network <see cref="DownUp"/> (both speeds in
/// one place) and <see cref="Mirror"/> (download above the middle, upload under it). Saved by name: new ones go at the end.
/// On an icon, <see cref="Level"/> fills the icon from the bottom and <see cref="Ring"/> rings its edge.
/// </summary>
public enum TrayLook { Number, Area, Line, Bars, Level, Blocks, Ring, Half, Pie, Cores, Big, Spark, DownUp, Mirror }

/// <summary>
/// How tall a reading is drawn in the taskbar. <see cref="Auto"/>: as tall as the taskbar has room for; <see cref="Small"/>:
/// one line, the name beside the number (needs 30 px at 100%); <see cref="Medium"/>: the number over its name (40 px);
/// <see cref="Large"/>: the number over its name with the most room for its look (48 px, Windows' usual taskbar). A size
/// the taskbar is too short for draws at the taskbar's height until there is room (see <see cref="TrayParts.SizeHeight"/>).
/// </summary>
public enum TraySize { Auto, Small, Medium, Large }

/// <summary>
/// How much room the strip's readings take. <see cref="Standard"/>: graphs as wide as before (wider on a wide screen);
/// <see cref="Compact"/>: graphs as narrow as their words, on tighter tiles; <see cref="Roomy"/>: graphs half as wide again.
/// </summary>
public enum TrayWidth { Standard, Compact, Roomy }

/// <summary>
/// The room between the taskbar's readings (1.3, the user: "if users want it very close they can do that"): as the
/// width sets it (Normal), very close (None), closer (Small), or further apart (Large). A pair's two keep theirs.
/// </summary>
public enum TrayGap { Normal, None, Small, Large }

/// <summary>
/// A taskbar reading's own colours: its number's (null: as RigSight colours it; "heat": by how warm, temperatures
/// only; else a code like "#5B8CFF"), its name's, which also colours its graph and its icon's tile (null: its part's),
/// and its tile's (null: a faint neutral tile on the strip, the name's colour on an icon).
/// </summary>
public sealed class TrayColor
{
    public string? Number { get; set; }
    public string? Name { get; set; }
    public string? Tile { get; set; }
}

/// <summary>The clock times are written in: Windows' own (its region's), 12-hour ("6:41 PM") or 24-hour ("18:41").</summary>
public enum ClockStyle { Windows, H12, H24 }

public enum NotificationStyle
{
    /// <summary>Rigsight's own small card in the bottom-right corner.</summary>
    Card,
    /// <summary>Standard Windows notifications (appear in the notification center).</summary>
    Windows,
}

public enum AppCategory { Other, Game, Browser, Development, Communication, Media, Launcher, Productivity, System }

/// <summary>What happens after an update: see <see cref="RigsightSettings.WhatsNewAfter"/>.</summary>
public enum WhatsNewAfter
{
    /// <summary>A What's new button beside the window's buttons, until it's read.</summary>
    Button,
    /// <summary>The card opens by itself the first time the app opens.</summary>
    Open,
    /// <summary>Neither.</summary>
    Off,
}

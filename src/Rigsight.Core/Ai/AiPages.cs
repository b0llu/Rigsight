namespace Rigsight.Core.Ai;

/// <summary>A RigSight page as a source for answers: what it holds, the plan it starts in, and the look-ups that read it.</summary>
/// <param name="Plan">The plan it first comes with (0 Free, 1 Essentials, 2 Complete), for the words shown before the
/// relay has said which pages each plan opens.</param>
public sealed record AiPage(string Id, string Name, string What, int Plan, string[] Tools)
{
    public string Label => Lang.T(Name);
    public string Description => Lang.T(What);
}

/// <summary>
/// The pages answers can use (the plans mock-up, 2026-10-23): each look-up belongs to one page, and Settings › AI switches
/// pages, not kinds of record. Which pages a plan opens comes from the relay (it decides, and cuts the look-ups to them);
/// this list is the same table the relay has (rigsight-relay src/pages.ts), so keep the two together.
/// </summary>
public static class AiPages
{
    public const string Home = "home", Parts = "parts", Temperatures = "temperatures", Crashes = "crashes", Memory = "memory",
        Storage = "storage", Processes = "processes", Reports = "reports", Games = "games", Timeline = "timeline", Apps = "apps",
        Network = "network", Fans = "fans", Sensors = "sensors", Rewind = "rewind", Dashboards = "dashboards",
        Widgets = "widgets", Overlay = "overlay", Taskbar = "taskbar";

    // The plan each page comes with (0 Free, 1 Essentials, 2 Complete), as the relay's plans table says since migration 0011
    // (the owner's choice, 2026-10-26): Processes with Free, Games with Essentials.
    public static readonly IReadOnlyList<AiPage> All =
    [
        new(Home, Lang.Key("Home"), Lang.Key("How your PC is doing today, with heat, problems and drive health"), 0, ["get_today"]),
        new(Parts, Lang.Key("Your PC's parts"), Lang.Key("Your processor, graphics card, memory, drives and drivers"), 0, ["get_pc_info"]),
        new(Temperatures, Lang.Key("Temperatures"), Lang.Key("Heat now and over time, and when parts slowed down to cool off"), 0, ["get_temperatures"]),
        new(Crashes, Lang.Key("Crashes"), Lang.Key("Every crash, sudden shut-off and driver reset, and what was running"), 1, ["get_crashes"]),
        new(Memory, Lang.Key("Memory"), Lang.Key("Memory use now and over time, and apps that keep growing"), 1, ["get_memory"]),
        new(Storage, Lang.Key("Storage"), Lang.Key("Space on each drive, drive health, and what you could free"), 1, ["get_storage", "get_drive_scan"]),
        new(Processes, Lang.Key("Processes"), Lang.Key("What's running right now and what it's using"), 0, ["get_running_now"]),
        new(Reports, Lang.Key("Reports"), Lang.Key("Summaries of any day, week or month, energy used included"), 1, ["get_report", "get_on_times", "get_moment"]),
        new(Games, Lang.Key("Games"), Lang.Key("Your games, every play, frame rates and what held them back"), 1, ["list_games", "get_game"]),
        new(Timeline, Lang.Key("Timeline"), Lang.Key("Windows updates, driver and setting changes, and what changed after"), 2, ["get_changes"]),
        new(Apps, Lang.Key("Apps"), Lang.Key("How much you use each app and how heavy it is"), 2, ["get_apps"]),
        new(Network, Lang.Key("Network"), Lang.Key("Internet use for each app, drops, and whether it's the PC or the line"), 2, ["get_network"]),
        new(Fans, Lang.Key("Fans"), Lang.Key("Fan speeds, how they respond to heat, and fans wearing out"), 2, ["get_fans"]),
        new(Sensors, Lang.Key("All sensors"), Lang.Key("Every reading your PC gives, like voltages and power draw"), 2, ["get_sensors_now"]),
        new(Rewind, Lang.Key("Rewind"), Lang.Key("Your month or year with this PC"), 2, ["get_rewind"]),
        new(Dashboards, Lang.Key("Your dashboards"), Lang.Key("The dashboards you made"), 2, ["get_dashboard"]),
        // What RigSight shows outside its window, for those pages' AI buttons (2026-10-26: every page a look-up). Free: they
        // help people set RigSight up, and say nothing a person's records do.
        new(Widgets, Lang.Key("Widgets"), Lang.Key("Your desktop widgets and the readings they can show"), 0, ["get_widgets"]),
        new(Overlay, Lang.Key("Game overlay"), Lang.Key("Your in-game overlay and the readings it can show"), 0, ["get_overlay"]),
        new(Taskbar, Lang.Key("Taskbar"), Lang.Key("Your taskbar readings and the ones you could add"), 0, ["get_taskbar"]),
    ];

    public static AiPage? Get(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The page a look-up reads, or null for one that isn't a page's (web search).</summary>
    public static AiPage? Of(string tool) => All.FirstOrDefault(p => p.Tools.Contains(tool));

    /// <summary>The pages a plan opens before the relay has said (its own list wins once it has).</summary>
    public static IReadOnlyList<string> DefaultFor(int plan) => [.. All.Where(p => p.Plan <= plan).Select(p => p.Id)];

    /// <summary>
    /// The old switches by kind of record (before 2026-10-23), as pages: a kind turned off turns off every page showing it,
    /// so nobody's choice is undone by the move to pages.
    /// </summary>
    internal static IEnumerable<string> FromOldSwitches(Settings.AiSettings a)
    {
        if (!a.ShareCrashes) yield return Crashes;
        if (!a.ShareHeat) { yield return Temperatures; yield return Fans; yield return Sensors; }
        if (!a.ShareApps) { yield return Apps; yield return Games; yield return Processes; }
        if (!a.ShareChanges) yield return Timeline;
        if (!a.ShareMemory) { yield return Memory; yield return Storage; }
        if (!a.ShareNetwork) yield return Network;
        if (!a.ShareParts) yield return Parts;
        if (!a.ShareUsage) yield return Reports;
    }
}

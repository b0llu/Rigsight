using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Games;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ai;

/// <summary>
/// The look-ups the AI helper may ask for, answered from RigSight's history the way its pages and exports build them
/// (the report, crashes with their context, the timeline, games). Only the kinds of records Settings › Ask RigSight lets it
/// send are offered. Each answer is kept for the question, so asking twice reads the history once.
/// </summary>
public sealed partial class AiTools(RigsightSettings settings, string? dbPath = null)
{
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private List<InstalledGame>? _installed;
    private readonly object _gamesLock = new();

    /// <summary>
    /// The pages the person's RigSight AI plan opens, as the relay said (null: every page, for their own key and Ollama,
    /// which no plan limits). The relay cuts the look-ups to them too; this is so the app never offers or runs the others.
    /// </summary>
    public IReadOnlyCollection<string>? PlanPages { get; set; }

    /// <summary>Where games keep their settings: this PC's folders, or a test's (where none are read from the real PC).</summary>
    public GameSettings.Places SettingsPlaces { get; set; } = GameSettings.Places.ThisPc();

    private bool InPlan(string name) => PlanPages is null || AiPages.Of(name) is not { } page || PlanPages.Contains(page.Id);

    /// <summary>The settings the look-ups follow (and the person's answer preferences for the prompt).</summary>
    public RigsightSettings Settings => settings;

    /// <summary>A look-up answers when its page is switched on in Settings › AI; otherwise it says it isn't shared.</summary>
    private bool Allowed(string name) => AiPages.Of(name) is { } page && settings.Ai.Sends(page.Id);

    public JsonArray Definitions()
    {
        var all = new JsonArray
        {
            Def("get_pc_info", "The PC's parts: CPU, graphics card with driver version, motherboard, RAM, Windows version, and each screen with its resolution and the refresh rate it runs at now.", new JsonObject()),
            Def("get_report", "RigSight's report for a period: time on, peaks for CPU and GPU temperature, what stood out, this period against the PC's own usual (temperatures, fans, problems), apps used with their peaks, crashes, and the electricity the CPU and graphics card used (kWh, average and highest watts). Dates are the user's local dates.", Dates()),
            Def("get_crashes", "Every crash, hang, graphics driver reset, blue screen and sudden shut-off in a period, each with what was going on (app in front, loads, temperatures, power), RigSight's finding with how sure it is, and changes in the 7 days before.", Dates()),
            Def("get_changes", "What changed on the PC in a period: drivers, Windows updates, hardware, BIOS/firmware, settings, fan settings, apps installed or updated.", Dates()),
            Def("list_games", "The games played on this PC with hours played, sessions, launcher and how hard each works the graphics card. With from and to: only the games played in that period, with the time played in it. Without them: all of RigSight's history, not any one day.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["from"] = new JsonObject { ["type"] = "string", ["description"] = "First day, yyyy-MM-dd (leave out for the whole history)." },
                    ["to"] = new JsonObject { ["type"] = "string", ["description"] = "Last day included, yyyy-MM-dd." },
                },
            }),
            Def("get_game", "One game's history and how it's set: each play with its length, CPU/GPU load and temperature, power, frame rate when recorded, how it ended; its usual; crashes; changes between plays; the game's own saved graphics settings (resolution, display mode, VSync, frame limit, quality, upscaling) and Windows' settings for it. Look it up for how to make a game run better, smoother or cooler.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The game's name as list_games gives it." } },
                ["required"] = new JsonArray("name"),
            }),
        };
        foreach (var def in MoreDefinitions()) all.Add(def!.DeepClone());
        foreach (var def in PageDefinitions()) all.Add(def);
        if (OpenableAppsLookUp) all.Add(Def(AppsLookUp, "Which apps from RigSight's list of PC tools (graphics driver apps, game launchers, fan, lighting and drive tools) are on this PC, so a step done in one can come with a button that opens it. Look it up when the next step happens in such an app (check for a driver update, change lighting, verify a game in its launcher).", new JsonObject()));
        // Every look-up the plan opens is offered, whatever Settings › AI sends: one switched off answers that it isn't
        // shared, so the model can say so and where to switch it on, instead of never knowing the records exist.
        return [.. all.Where(d => InPlan((string?)d?["function"]?["name"] ?? "")).Select(d => d!.DeepClone())];
    }

    private static JsonObject Dates() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["from"] = new JsonObject { ["type"] = "string", ["description"] = "First day, yyyy-MM-dd." },
            ["to"] = new JsonObject { ["type"] = "string", ["description"] = "Last day included, yyyy-MM-dd." },
        },
        ["required"] = new JsonArray("from", "to"),
    };

    private static JsonObject Def(string name, string description, JsonObject parameters) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = parameters.Count == 0 ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } : parameters,
        },
    };

    /// <summary>Answers one look-up; a mistake in what the model asked for comes back as text it can correct.</summary>
    public string Run(string name, string argsJson)
    {
        JsonObject args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson) as JsonObject ?? []; }
        catch (JsonException) { return "Error: the arguments weren't valid JSON."; }
        if (name == AppsLookUp && OpenableAppsLookUp) return AppsText();
        if (!Known(name)) return $"Error: there is no tool called {name}.";
        if (!InPlan(name)) return NotInPlan(name);
        if (!Allowed(name)) return NotShared(name);
        string key = name + " " + args.ToJsonString();
        // What is running and the readings now are a moment: looked at again every time, never answered from an earlier look.
        // A drive's scan too: "not scanned yet" was answered again after the scan its own button had just run (2026-10-24).
        bool moment = name is "get_running_now" or "get_sensors_now" or "get_drive_scan";
        if (!moment && _cache.TryGetValue(key, out var hit)) return hit;
        string result;
        try
        {
            result = name switch
            {
                "get_pc_info" => PcInfo(),
                "get_report" => WithDb(db => Report(db, Day(args, "from"), Day(args, "to"))),
                "get_crashes" => WithDb(db => Crashes(db, Day(args, "from"), Day(args, "to"))),
                "get_changes" => WithDb(db => Changes(db, Day(args, "from"), Day(args, "to"))),
                "list_games" when args["from"] is not null || args["to"] is not null =>
                    WithDb(db => GamesIn(db, Day(args, args["from"] is null ? "to" : "from"), Day(args, args["to"] is null ? "from" : "to"))),
                "list_games" => WithDb(ListGames),
                "get_game" => WithDb(db => Game(db, (string?)args["name"] ?? "")),
                _ => RunMore(name, args) ?? RunPages(name, args) ?? $"Error: there is no tool called {name}.",
            };
        }
        catch (FormatException ex) { result = "Error: " + ex.Message; }
        if (!moment) _cache[key] = result;
        return result;
    }

    private T WithDb<T>(Func<RigsightDb, T> read)
    {
        using var db = (dbPath is null ? RigsightDb.OpenReader() : RigsightDb.OpenReader(dbPath)) ?? throw new FormatException("RigSight's history couldn't be opened.");
        return read(db);
    }

    private static DateTime Day(JsonObject args, string name) =>
        DateTime.TryParseExact((string?)args[name], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new FormatException($"'{name}' must be a date like 2026-10-19.");

    private Report BuildReport(RigsightDb db, DateTime from, DateTime to) =>
        from == to ? ReportBuilder.Build(db, ReportRange.Day, from.AddHours(12), settings) : ReportBuilder.BuildCustom(db, from, to.AddDays(1), settings);

    private string Report(RigsightDb db, DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        var report = BuildReport(db, from, to);
        Trim(report);
        string title = from == to ? from.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture) : $"{from:d MMM yyyy} to {to:d MMM yyyy}";
        return WithoutMaker(ReadableExport.ReportText(report, title, "RigSight", times: settings.Ai.Sends(AiPages.Reports), tight: true)) + Energy(report, from, to)
            + LeftOut(Switch.Crashes, Switch.Heat, Switch.Apps, Switch.Memory, Switch.Network, Switch.Usage);
    }

    /// <summary>
    /// What the CPU and the graphics card used over the period, as the Reports page shows it: the report always had it,
    /// but its text for the model didn't, so "how much electricity did my PC use?" was answered "no record" (2026-10-23).
    /// Said with what it leaves out, so the model doesn't call it the whole PC's use or a bill.
    /// </summary>
    internal static string Energy(Report report, DateTime from, DateTime to)
    {
        if (report.EnergyWh is not > 0 || report.PoweredMinutes <= 0) return "";
        static string Wh(double wh) => wh >= 1000 ? $"{wh / 1000:0.00} kWh" : $"{wh:0} Wh";
        double wh = report.EnergyWh.Value, hours = report.PoweredMinutes / 60.0;
        int days = Math.Max(1, (to.Date - from.Date).Days + 1);
        var sb = new StringBuilder("\nENERGY (CPU and graphics card only)\n");
        sb.AppendLine($"  Used: {Wh(wh)} over {hours:0.#} hours on" + (days > 1 ? $", about {Wh(wh / days)} a day" : "")
            + $"; on average {wh / hours:0} W while on.");
        if (report.CpuEnergyWh is double cpu && report.GpuEnergyWh is double gpu)
            sb.AppendLine($"  CPU {Wh(cpu)}, graphics card {Wh(gpu)}.");
        if (report.CpuPowerPeak is { } cp) sb.AppendLine($"  CPU's highest: {cp.Value:0} W at {cp.Time:d MMM h:mm tt}" + (cp.App is { } a ? $" ({a})" : "") + ".");
        if (report.GpuPowerPeak is { } gp) sb.AppendLine($"  Graphics card's highest: {gp.Value:0} W at {gp.Time:d MMM h:mm tt}" + (gp.App is { } b ? $" ({b})" : "") + ".");
        sb.AppendLine("  From the two parts' own power readings each minute: the screen, drives, fans, the motherboard and the power supply's own loss aren't in it, so the PC at the wall uses more. No price is known: for a cost, multiply the kWh by the price on the person's electricity bill.");
        return sb.ToString();
    }

    private static readonly ChangeKind[] Telling = [ChangeKind.Driver, ChangeKind.WindowsUpdate, ChangeKind.Windows, ChangeKind.Hardware, ChangeKind.Firmware, ChangeKind.Setting, ChangeKind.Fans];

    /// <summary>Crashes told in full in one look-up; the rest are in its counts.</summary>
    internal const int MaxCrashDetails = 30;

    // As ExportViewModel.Stories builds them for "Crashes with their context".
    private string Crashes(RigsightDb db, DateTime from, DateTime to)
    {
        var stories = Stories(db, from, to);
        if (stories.Count == 0) return $"No crashes, hangs, driver resets or sudden shut-offs from {from:d MMM yyyy} to {to:d MMM yyyy}.";
        // Every one of them counted first, by kind and by RigSight's finding: the details below stop at the newest 15,
        // and a model left to count from those guesses (it once called 46 shut-offs, nearly all likely power cuts, a
        // failing power supply from the one it saw under load).
        string summary = Summary(stories);
        // The newest 30 at most, laid out without padding (2026-10-26: was 15 with each field padded to a column, and a
        // model asking again for the older ones pushed answers over their budget; about the same size now holds twice as many).
        int all = stories.Count;
        stories = [.. stories.OrderByDescending(x => x.Crash.Ts).Take(MaxCrashDetails).Select(Shared)];
        string text = WithoutMaker(ReadableExport.CrashesText(stories, $"{from:d MMM yyyy} to {to:d MMM yyyy}", DateTime.Now, "RigSight", tight: true));
        return summary + (all > MaxCrashDetails ? $"Details of the newest {MaxCrashDetails} of {all} follow; ask for a shorter period for older ones. Use the counts above for totals.\n\n" : "\n") + text
            + LeftOut(Switch.Heat, Switch.Apps, Switch.Memory, Switch.Changes);
    }

    /// <summary>
    /// A crash with only what the switches share around it: the readings just before (temperatures and fans), the app in
    /// front and how long it ran (apps), memory, the changes before it. The crash itself and RigSight's finding stay.
    /// </summary>
    private CrashStory Shared(CrashStory s)
    {
        var a = settings.Ai;
        var c = s.Context;
        if (c is not null)
        {
            if (!a.Sends(AiPages.Temperatures)) c = c with { CpuBefore = null, GpuBefore = null, CpuLoad = null, GpuLoad = null, GpuPower = null };
            if (!a.Sends(AiPages.Apps)) c = c with { FrontApp = null, SessionSec = null };
            if (!a.Sends(AiPages.Memory)) c = c with { RamUsed = null };
        }
        // RigSight's own finding keeps its sentence; the lines under it that rest on a reading not shared go.
        var finding = s.Finding is { } f ? f with { Points = Keep(f.Points), RuledOut = Keep(f.RuledOut), NotChecked = Keep(f.NotChecked) } : null;
        return s with
        {
            Finding = finding,
            Context = c,
            AppInFront = a.Sends(AiPages.Apps) ? s.AppInFront : null,
            ChangesBefore = a.Sends(AiPages.Timeline) ? s.ChangesBefore : [],
            // The close-up's lines are readings and apps every two seconds.
            InLastMinutes = a.Sends(AiPages.Temperatures) && a.Sends(AiPages.Apps) ? s.InLastMinutes : [],
        };
    }

    /// <summary>
    /// An export's text without the lines naming the app that made it ("RigSight report", "Made by RigSight … on …"):
    /// the model copied them into "RigSight found…" where it should speak as RigSight.
    /// </summary>
    private static string WithoutMaker(string text)
    {
        // The maker line is one text with the version and time in it: matched by its start, in English or the pack's words.
        string made = Lang.T($"Made by RigSight {"VERSION"} on {"WHEN"}. Everything here was recorded on this PC.").Split("VERSION")[0];
        return string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim() != Lang.T("RigSight report") && !l.StartsWith(made, StringComparison.Ordinal)));
    }

    /// <summary>Every problem in the period with what was going on and RigSight's finding (muted apps left out).</summary>
    private List<CrashStory> Stories(RigsightDb db, DateTime from, DateTime to)
    {
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to.AddDays(1));
        var apps = db.LoadApps();
        var byId = apps.ToDictionary(a => a.Id);
        var byExe = apps.GroupBy(a => a.Exe, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string Name(AppRow a) => settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name;
        var context = db.GetCrashContext(f, t);
        var changes = db.GetChanges(f - 7 * 86400, t).Where(c => Telling.Contains(c.Kind)).ToList();
        var crashes = db.GetCrashes(f, t).Where(e => !settings.IsCrashMuted(e.AppExe)).ToList();
        var findings = CrashCases.Of(db, crashes, settings);
        Dictionary<DateTime, ChangeResult> results = changes.Any(ChangeEffects.IsMajor) ? ChangeEffects.Read(db, settings) : [];
        return [.. crashes.Select(e =>
        {
            var at = TimeUtil.FromUnix(e.Ts);
            context.TryGetValue(e.Id, out var ctx);
            return new CrashStory(e,
                e.AppExe.Length == 0 ? null : byExe.TryGetValue(e.AppExe, out var row) ? Name(row) : null,
                ctx,
                ctx?.FrontApp is long front && byId.TryGetValue(front, out var app) ? Name(app) : null,
                [.. changes.Where(c => c.Time <= at && c.Time >= at.AddDays(-7)).TakeLast(6)])
            {
                Finding = findings.GetValueOrDefault(e.Id),
                Results = results,
                InLastMinutes = e.Kind is CrashKind.AppCrash or CrashKind.AppHang or CrashKind.GpuDriverReset && db.GetCloseUp(e.Ts, e.AppExe) is { } close
                    ? [.. CloseUpWords.Find(close.Points, e.Ts, id => byId.TryGetValue(id, out var a) ? Name(a) : "An app",
                        e.AppExe.Length > 0 && byExe.TryGetValue(e.AppExe, out var crashed) ? crashed.Id : null).Select(x => x.Text)]
                    : [],
            };
        })];
    }

    // A finding's lines by the reading each rests on (titles as CrashCases words them, in the language shown).
    private static readonly string[] HeatLines = ["It wasn't overheating", "It was running hot", "Temperatures weren't recorded", "The CPU wasn't overheating",
        "The GPU wasn't overheating", "It was working hard", "It wasn't doing much"];
    private static readonly string[] MemoryLines = ["Memory wasn't full", "Memory was nearly full", "Windows was running out of memory"];

    private List<Evidence> Keep(List<Evidence> lines)
    {
        var a = settings.Ai;
        return [.. lines.Where(e =>
            !(!a.Sends(AiPages.Temperatures) && HeatLines.Any(t => Lang.T(t) == e.Title))
            && !(!a.Sends(AiPages.Memory) && MemoryLines.Any(t => Lang.T(t) == e.Title))
            && !(!a.Sends(AiPages.Reports) && Lang.T("Nobody was using it") == e.Title))];
    }

    private static string Kind(CrashKind kind) => kind switch
    {
        CrashKind.AppCrash => "App crashes",
        CrashKind.AppHang => "App freezes",
        CrashKind.GpuDriverReset => "Graphics driver resets",
        CrashKind.SystemCrash => "Blue screens",
        _ => "Sudden shut-offs",
    };

    /// <summary>All the problems in the period: how many of each kind, split by RigSight's finding, and which apps.</summary>
    private static string Summary(List<CrashStory> stories)
    {
        // One line per kind and one per finding under it, each with its own number, so no count can be read as another's.
        var kinds = stories.GroupBy(s => s.Crash.Kind).OrderByDescending(g => g.Count()).ToList();
        var sb = new StringBuilder($"TOTALS for this period ({stories.Count} problems in all: {string.Join(", ", kinds.Select(k => $"{k.Count()} {Kind(k.Key).ToLowerInvariant()}"))}):\n");
        foreach (var kind in kinds)
        {
            sb.AppendLine($"{Kind(kind.Key)}: {kind.Count()} in all, first {TimeUtil.FromUnix(kind.Min(s => s.Crash.Ts)):d MMM}, last {TimeUtil.FromUnix(kind.Max(s => s.Crash.Ts)):d MMM}. What we found for them:");
            foreach (var found in kind.GroupBy(s => s.Finding is { } f ? $"{f.Head.TrimEnd('.')} ({char.ToLowerInvariant(f.HowSure[0])}{f.HowSure[1..]})" : "no finding").OrderByDescending(g => g.Count()))
                sb.AppendLine($"  {found.Count()} of the {kind.Count()}: {found.Key}");
            var apps = kind.Where(s => s.App is not null).GroupBy(s => s.App!).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} {g.Count()}").ToList();
            if (apps.Count > 0) sb.AppendLine($"  Apps: {string.Join(", ", apps)}");
            var fronts = kind.Key == CrashKind.UnexpectedShutdown
                ? kind.Where(s => s.AppInFront is not null).GroupBy(s => s.AppInFront!).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} {g.Count()}").ToList() : [];
            if (fronts.Count > 0) sb.AppendLine($"  In front when it happened: {string.Join(", ", fronts)}");
        }
        return sb.ToString();
    }

    private static string Changes(RigsightDb db, DateTime from, DateTime to)
    {
        var list = db.GetChanges(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to.AddDays(1)));
        if (list.Count == 0) return "No changes recorded in that time.";
        var sb = new StringBuilder();
        if (list.Count > 120) { sb.AppendLine($"(The newest 120 of {list.Count}.)"); list = list[^120..]; }
        foreach (var c in list) sb.AppendLine($"{c.Time:ddd d MMM yyyy h:mm tt}  [{c.Kind}]  {c.Shown}");
        return sb.ToString();
    }

    /// <summary>The games installed on this PC (scanned once): what a "Verify files" or "Open the game" button is checked against.</summary>
    public IReadOnlyList<InstalledGame> Installed()
    {
        lock (_gamesLock) return _installed ??= GameLibrary.Scan(GameSources.ThisPc());
    }

    private List<GameEntry> Library(RigsightDb db)
    {
        lock (_gamesLock) _installed ??= GameLibrary.Scan(GameSources.ThisPc());
        return GameHistory.Library(db, _installed, settings);
    }

    private string ListGames(RigsightDb db)
    {
        var games = Played(db);
        if (games.Count == 0) return "No games recorded.";
        var sb = new StringBuilder();
        // Said first: a model asked about one day once took the busiest games of all time for that day's.
        sb.AppendLine($"Every game in the whole history ({games.Min(g => g.FirstPlayed):d MMM yyyy} to {games.Max(g => g.LastPlayed):d MMM yyyy}), not any one day or week. For a period, ask with from and to.");
        foreach (var g in games)
            sb.AppendLine($"{g.Name}: {Units.Duration(g.ActiveSec)} over {g.Sessions} sessions, first {g.FirstPlayed:d MMM yyyy}, last {g.LastPlayed:d MMM yyyy}"
                + (g.Launcher is { } l ? $", {l}" : "") + (g.GpuLoad is double gpu ? $", graphics card {gpu:0}% busy on average" : ""));
        return sb.ToString();
    }

    /// <summary>Only the games played in the period, with the time played in it (from each game's plays).</summary>
    private string GamesIn(RigsightDb db, DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        DateTime end = to.AddDays(1);
        string period = from == to ? from.ToString("dddd d MMM yyyy", CultureInfo.InvariantCulture) : $"{from:d MMM yyyy} to {to:d MMM yyyy}";
        var sb = new StringBuilder();
        foreach (var (g, sec, plays) in PlayedIn(db, from, to))
            sb.AppendLine($"{g.Name}: {Units.Duration(sec)} in {plays} {(plays == 1 ? "play" : "plays")}"
                + (g.Launcher is { } l ? $", {l}" : "") + (g.GpuLoad is double gpu ? $", graphics card {gpu:0}% busy on average (over all its plays)" : ""));
        return sb.Length == 0 ? $"No games played on {period}." : $"Games played on {period} (only these were played then):\n" + sb;
    }

    /// <summary>The games played from one day to another (both included), with the time in front and the plays then.</summary>
    private List<(GameEntry Game, double Sec, int Plays)> PlayedIn(RigsightDb db, DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        DateTime end = to.AddDays(1);
        var list = new List<(GameEntry, double, int)>();
        foreach (var g in Played(db).Where(g => g.FirstPlayed is not { } first || g.LastPlayed is not { } last || (first < end && last >= from)))
        {
            var plays = GameHistory.Story(db, g, settings, DateTime.Now).Plays.Where(p => p.StartTime < end && p.EndTime >= from).ToList();
            if (plays.Count > 0) list.Add((g, plays.Sum(p => p.ActiveSec), plays.Count));
        }
        return [.. list.OrderByDescending(x => x.Item2)];
    }

    private List<GameEntry> Played(RigsightDb db) => [.. Library(db).Where(g => g.HasHistory).OrderByDescending(g => g.ActiveSec)];

    private string Game(RigsightDb db, string name)
    {
        var games = Library(db).Where(g => g.HasHistory).ToList();
        var entry = games.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? games.FirstOrDefault(g => g.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            // An app asked about as a game ("What is Freedom to Stream?" was looked up here and answered "no such game",
            // real PC 2026-10-27): say it's an app and where its records are.
            var app = db.LoadApps().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || a.Exe.Equals(name, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(a.Exe).Equals(name, StringComparison.OrdinalIgnoreCase));
            return app is not null
                ? $"'{name}' isn't a game: it's an app on this PC ({app.Exe}, kind: {AppCatalog.Label(app.Category)}). get_apps has its time in use, load and memory; describe it only from its name, kind and records, or a web search."
                : $"No game called '{name}' in the history. Known: {string.Join(", ", games.Select(g => g.Name))}.";
        }
        var story = GameHistory.Story(db, entry, settings, DateTime.Now);
        var sb = new StringBuilder();
        sb.AppendLine($"{entry.Name}: {Units.Duration(entry.ActiveSec)} in {story.Plays.Count} plays."
            + (settings.Ai.Sends(AiPages.Crashes) ? $" Crashes {story.Crashes}, hangs {story.Hangs}, driver resets {story.DriverResets}, shut-offs {story.ShutOffs}, power cuts {story.PowerCuts.Count}." : ""));
        if (settings.Ai.Sends(AiPages.Temperatures) && story.Usual is { } u) sb.AppendLine($"Its usual (from {u.Plays} plays at like graphics card power): GPU {u.Gpu:0} °C, CPU {u.Cpu:0} °C, power {u.Power:0} W.");
        if (story.SinceUpdate is { } since) sb.AppendLine($"Frame rate since its last update ({story.LastUpdate:d MMM}): {since}.");
        // How it's set: the game's own settings file and Windows' settings for it (the person's ask, 2026-10-25).
        sb.Append(GameSettings.Text(GameSettings.Read(entry, SettingsPlaces), entry.ExePath ?? GameSettings.ProgramIn(entry.Installed?.InstallDir)));
        sb.AppendLine("All plays together: " + Stats(story.All));
        if (NoFrameRate(story) is { } noFps) sb.AppendLine(noFps);
        if (story.All.FrameMinutes > 0)
            sb.AppendLine("A stutter is one frame taking at least 2.5 times as long as the frames just before it, and at least 20 ms: a hitch you feel. Lows and stutters come from every frame RivaTuner drew; more stutters an hour than the game's own other plays means it ran less smoothly.");
        if (settings.Ai.Sends(AiPages.Memory)) sb.AppendLine("\"Graphics card memory in use\" is the whole card, every app together. \"The game's own video memory\" is the game alone, recorded only for plays from 20 Oct 2026 on: for older plays, check what else was using the card before saying a game uses more.");
        sb.AppendLine("Plays, newest first (at most 15):");
        // Video memory is the whole card's (every app together), so each play says what else had the card at the time.
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var own = entry.AppIds.ToHashSet();
        foreach (var p in story.Plays.OrderByDescending(p => p.Start).Take(15))
        {
            // A play of a few seconds has no minute of readings: no empty ". ." before what ran with it.
            string stats = Stats(p.Stats, cardNote: false);
            string alongside = Alongside(db, p, own, apps);
            sb.AppendLine($"  {p.StartTime:ddd d MMM h:mm tt} to {p.EndTime:h:mm tt}, {Units.Duration(p.ActiveSec)} in front{(settings.Ai.Sends(AiPages.Crashes) ? $", ended: {p.Ending}" : "")}"
                + (stats.Length > 0 ? $". {stats}" + alongside : alongside));
        }
        // When the newest play with frames stuttered, and what else was going on then (as its page says it).
        if (story.Plays.LastOrDefault(p => p.Stats.Stutters is > 0) is { } rough && entry.AppIds.Count > 0)
        {
            var stutters = db.GetStutters(entry.AppIds[0], rough.Start * 1000, (rough.End + 60) * 1000);
            if (StutterCauses.Explain(db, entry.AppIds[0], stutters, rough.Start, rough.End + 60, settings) is { } why)
                sb.AppendLine($"Stutters in the {rough.StartTime:ddd d MMM} play: {why}");
        }
        if (settings.Ai.Sends(AiPages.Timeline))
            foreach (var c in story.Changes.TakeLast(10)) sb.AppendLine($"Changed between plays: {c.Change.Time:d MMM} {c.Change.Shown}");
        return sb.ToString() + LeftOut(Switch.Heat, Switch.Memory, Switch.Changes, Switch.Crashes);
    }

    /// <summary>
    /// Why plays have no frame rate (the person's ask, 2026-10-25: people without RivaTuner must know why FPS isn't
    /// shown): RigSight reads it from RivaTuner, and says which of its states kept it out. Null when every play has one.
    /// </summary>
    private string? NoFrameRate(GameStory story)
    {
        int without = story.Plays.Count(p => p.Stats.Fps is null);
        if (without == 0) return null;
        string some = without == story.Plays.Count ? "No play of this game has a frame rate (FPS, lows or stutters)" : $"{without} of its {story.Plays.Count} plays have no frame rate";
        // Running already: the next step is only to play (the person's ask, 2026-10-27: "rivatuner is already on and fps
        // tracking works, the next step should just be play it"). Otherwise the Overlay page, never RivaTuner itself.
        string state = RivaTuner?.Invoke() switch
        {
            "missing" or "install-failed" => "it isn't installed on this PC. Next step: install it from RigSight's Overlay page ([do: open overlay]), then play the game",
            "installing" => "it's being installed right now. Next step: play the game once it's done",
            "stopped" => "it's installed but isn't running now. Next step: start it from RigSight's Overlay page ([do: open overlay]), then play the game",
            "running" => "it's already running, so frame rates are being recorded; these plays were simply before it ran. Next step: just play the game once more, then ask again. Don't tell them to start, install or open RivaTuner",
            _ => "it wasn't running for them. Next step: play the game again, and if a play still has no frame rate, the Overlay page ([do: open overlay]) shows whether RivaTuner runs",
        };
        return $"WHY NO FRAME RATE: {some}. RigSight reads frame rates from RivaTuner Statistics Server, a free tool anti-cheat accepts; {state}. Say this when the frame rate matters to the question, use that next step as it is, and never guess a frame rate.";
    }

    /// <summary>
    /// The other apps working the graphics card during a play (its average over the play, the busiest three), and how
    /// the card's memory went from the start of the play to its highest.
    /// </summary>
    private string Alongside(RigsightDb db, GamePlay p, HashSet<long> own, Dictionary<long, AppRow> apps)
    {
        var parts = new List<string>();
        var minutes = db.GetMinutes(p.Start, p.End);
        if (settings.Ai.Sends(AiPages.Memory) && minutes.Where(m => m.VramUsed is not null).ToList() is { Count: > 0 } vram)
            parts.Add($"graphics card memory in use (all apps) {vram[0].VramUsed / 1024:0.0} GB at the start, highest {vram.Max(m => m.VramUsed) / 1024:0.0} GB");
        int n = Math.Max(1, (int)((p.End - p.Start) / 60));
        var others = db.GetAppMinutes(p.Start, p.End).Where(a => !own.Contains(a.App)).GroupBy(a => a.App)
            .Select(g => (App: g.Key, Avg: g.Sum(a => a.Gpu) / n, Max: g.Max(a => a.Gpu)))
            .Where(x => x.Avg >= 3 || x.Max >= 20).OrderByDescending(x => x.Avg).Take(3).ToList();
        string Name(long id) => apps.TryGetValue(id, out var a)
            ? settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name : "an app";
        parts.Add(others.Count == 0 ? "no other app used the graphics card much"
            : "also using the graphics card: " + string.Join(", ", others.Select(o => $"{Name(o.App)} {o.Avg:0}% on average (up to {o.Max:0}%)")));
        return ". With it: " + string.Join("; ", parts) + ".";
    }

    /// <param name="cardNote">Whether to say again what "graphics card memory in use" covers: said once, for all plays
    /// together, and not on every play under it (15 times the same words, 2026-10-26).</param>
    private string Stats(PlayStats s, bool cardNote = true)
    {
        var parts = new List<string>();
        bool heat = settings.Ai.Sends(AiPages.Temperatures), memory = settings.Ai.Sends(AiPages.Memory);
        void Add(string label, double? v, string unit) { if (heat && v is double x) parts.Add($"{label} {x:0}{unit}"); }
        Add("GPU temp average", s.Gpu, " °C"); Add("GPU temp max", s.GpuMax, " °C"); Add("hot spot max", s.HotMax, " °C");
        Add("CPU temp average", s.Cpu, " °C"); Add("CPU temp max", s.CpuMax, " °C"); Add("GPU load", s.GpuLoad, "%");
        Add("GPU power", s.GpuPower, " W"); Add("CPU+GPU power", s.Power, " W");
        if (s.Fps is double fps) parts.Add($"FPS average {fps:0}");
        // From every frame where it was recorded (since 25 Oct 2026); before, RivaTuner's own 1% low averaged over minutes.
        if (s.FpsLow is double low) parts.Add(s.FrameMinutes > 0 ? $"1% low {low:0}" : $"1% low about {low:0} (RivaTuner's rolling figure, smoother than the true one)");
        if (s.FpsLow01 is double low01) parts.Add($"0.1% low {low01:0}");
        if (s.Stutters is int st && s.StuttersAnHour is double rate)
            parts.Add($"{st} stutters ({rate:0.#} an hour of play)" + (s.Worst is { } w ? $", the worst a {w.Ms:0} ms frame at {DateTimeOffset.FromUnixTimeMilliseconds(w.AtMs).LocalDateTime:h:mm:ss tt} against {w.UsualMs:0.#} ms usual" : ""));
        if (memory && s.VramMax is double v && s.VramTotal is double tot) parts.Add($"graphics card memory in use max {v / 1024:0.0} of {tot / 1024:0.0} GB" + (cardNote ? " (the whole card, every app together, not this game alone)" : ""));
        if (memory && s.VramOwnMax is double own) parts.Add($"the game's own video memory max {own / 1024:0.0} GB");
        if (heat && s.Minutes > 0) parts.Add($"GPU at 95%+ for {s.GpuFullMinutes} of {s.Minutes} min");
        return string.Join(", ", parts);
    }

    private static string PcInfo()
    {
        var lines = new List<string>();
        using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            if (cpu?.GetValue("ProcessorNameString") is string n) lines.Add($"CPU: {n.Trim()}");
        using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
            foreach (var sub in cls?.GetSubKeyNames().Where(x => x.All(char.IsDigit)) ?? [])
            {
                using var key = cls!.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc || desc.Contains("Basic") || desc.Contains("Virtual") || desc.Contains("Remote") || desc.Contains("Indirect")) continue;
                string version = key.GetValue("DriverVersion") as string ?? "?";
                // NVIDIA's Windows version "32.0.16.1742" is the driver people know as 617.42.
                if (desc.Contains("NVIDIA") && version.Split('.') is [_, _, var third, var fourth] && fourth.Length == 4)
                {
                    var digits = third[^1] + fourth;
                    version = $"{digits[..3]}.{digits[3..]}";
                }
                lines.Add($"GPU: {desc} (driver {version}, dated {key.GetValue("DriverDate")})");
            }
        using (var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
            if (bios is not null) lines.Add($"Motherboard: {bios.GetValue("BaseBoardManufacturer")} {bios.GetValue("BaseBoardProduct")} (BIOS {bios.GetValue("BIOSVersion")})");
        lines.Add($"RAM: {Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0):0} GB");
        using (var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            lines.Add($"Windows: build {nt?.GetValue("CurrentBuild")} {nt?.GetValue("DisplayVersion")}");
        lines.AddRange(Screens.Lines());
        lines.Add("Cooling: not known (fan and pump speeds are recorded, not the cooler's model).");
        return string.Join("\n", lines);
    }
}

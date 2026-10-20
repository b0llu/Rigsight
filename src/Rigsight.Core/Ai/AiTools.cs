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

    private bool Allowed(string name) => name switch
    {
        "get_pc_info" => settings.Ai.ShareParts,
        "get_report" => settings.Ai.ShareHeat || settings.Ai.ShareApps,
        "get_crashes" => settings.Ai.ShareCrashes,
        "get_changes" => settings.Ai.ShareChanges,
        "list_games" or "get_game" => settings.Ai.ShareApps,
        _ => false,
    };

    public JsonArray Definitions()
    {
        var list = new JsonArray();
        var all = new JsonArray
        {
            Def("get_pc_info", "The PC's parts: CPU, graphics card with driver version, motherboard, RAM, Windows version.", new JsonObject()),
            Def("get_report", "RigSight's report for a period: time on, peaks for CPU and GPU temperature, what stood out, this period against the PC's own usual (temperatures, fans, problems), apps used with their peaks, crashes. Dates are the user's local dates.", Dates()),
            Def("get_crashes", "Every crash, hang, graphics driver reset, blue screen and sudden shut-off in a period, each with what was going on (app in front, loads, temperatures, power), RigSight's finding with how sure it is, and changes in the week before.", Dates()),
            Def("get_changes", "What changed on the PC in a period: drivers, Windows updates, hardware, BIOS/firmware, settings, fan settings, apps installed or updated.", Dates()),
            Def("list_games", "The games RigSight knows on this PC: hours played, sessions, first and last played, launcher, how hard each works the graphics card.", new JsonObject()),
            Def("get_game", "One game's history: each play with its length, CPU/GPU load and temperature, power, frame rate when recorded, how it ended; its usual; crashes; changes between plays.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The game's name as list_games gives it." } },
                ["required"] = new JsonArray("name"),
            }),
        };
        // Only the records Settings › Ask RigSight lets it send.
        foreach (var def in all.ToList())
        {
            all.Remove(def);
            if (Allowed((string)def!["function"]!["name"]!)) list.Add(def);
        }
        return list;
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
        if (!Allowed(name)) return $"Error: there is no tool called {name}.";
        string key = name + " " + args.ToJsonString();
        if (_cache.TryGetValue(key, out var hit)) return hit;
        string result;
        try
        {
            result = name switch
            {
                "get_pc_info" => PcInfo(),
                "get_report" => WithDb(db => Report(db, Day(args, "from"), Day(args, "to"))),
                "get_crashes" => WithDb(db => Crashes(db, Day(args, "from"), Day(args, "to"))),
                "get_changes" => WithDb(db => Changes(db, Day(args, "from"), Day(args, "to"))),
                "list_games" => WithDb(ListGames),
                "get_game" => WithDb(db => Game(db, (string?)args["name"] ?? "")),
                _ => $"Error: there is no tool called {name}.",
            };
        }
        catch (FormatException ex) { result = "Error: " + ex.Message; }
        _cache[key] = result;
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
        string title = from == to ? from.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture) : $"{from:d MMM yyyy} to {to:d MMM yyyy}";
        return ReadableExport.ReportText(report, title, "RigSight");
    }

    private static readonly ChangeKind[] Telling = [ChangeKind.Driver, ChangeKind.WindowsUpdate, ChangeKind.Windows, ChangeKind.Hardware, ChangeKind.Firmware, ChangeKind.Setting, ChangeKind.Fans];

    // As ExportViewModel.Stories builds them for "Crashes with their context".
    private string Crashes(RigsightDb db, DateTime from, DateTime to)
    {
        var stories = Stories(db, from, to);
        if (stories.Count == 0) return $"No crashes, hangs, driver resets or sudden shut-offs from {from:d MMM yyyy} to {to:d MMM yyyy}.";
        // Every one of them counted first, by kind and by RigSight's finding: the details below stop at the newest 15,
        // and a model left to count from those guesses (it once called 46 shut-offs, nearly all likely power cuts, a
        // failing power supply from the one it saw under load).
        string summary = Summary(stories);
        // The newest 15 at most, so a long period stays readable.
        int all = stories.Count;
        stories = [.. stories.OrderByDescending(x => x.Crash.Ts).Take(15)];
        string text = ReadableExport.CrashesText(stories, $"{from:d MMM yyyy} to {to:d MMM yyyy}", DateTime.Now, "RigSight");
        return summary + (all > 15 ? $"Details of the newest 15 of {all} follow; ask for a shorter period for older ones. Use the counts above for totals.\n\n" : "\n") + text;
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
            sb.AppendLine($"{Kind(kind.Key)}: {kind.Count()} in all, first {TimeUtil.FromUnix(kind.Min(s => s.Crash.Ts)):d MMM}, last {TimeUtil.FromUnix(kind.Max(s => s.Crash.Ts)):d MMM}. RigSight's finding for them:");
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
        foreach (var g in games)
            sb.AppendLine($"{g.Name}: {Units.Duration(g.ActiveSec)} over {g.Sessions} sessions, first {g.FirstPlayed:d MMM yyyy}, last {g.LastPlayed:d MMM yyyy}"
                + (g.Launcher is { } l ? $", {l}" : "") + (g.GpuLoad is double gpu ? $", graphics card {gpu:0}% busy on average" : ""));
        return sb.ToString();
    }

    private List<GameEntry> Played(RigsightDb db) => [.. Library(db).Where(g => g.HasHistory).OrderByDescending(g => g.ActiveSec)];

    private string Game(RigsightDb db, string name)
    {
        var games = Library(db).Where(g => g.HasHistory).ToList();
        var entry = games.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? games.FirstOrDefault(g => g.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return $"No game called '{name}' in RigSight's history. Known: {string.Join(", ", games.Select(g => g.Name))}.";
        var story = GameHistory.Story(db, entry, settings, DateTime.Now);
        var sb = new StringBuilder();
        sb.AppendLine($"{entry.Name}: {Units.Duration(entry.ActiveSec)} in {story.Plays.Count} plays. Crashes {story.Crashes}, hangs {story.Hangs}, driver resets {story.DriverResets}, shut-offs {story.ShutOffs}, power cuts {story.PowerCuts.Count}.");
        if (story.Usual is { } u) sb.AppendLine($"Its usual (from {u.Plays} plays at like graphics card power): GPU {u.Gpu:0} °C, CPU {u.Cpu:0} °C, power {u.Power:0} W.");
        if (story.SinceUpdate is { } since) sb.AppendLine($"Frame rate since its last update ({story.LastUpdate:d MMM}): {since}.");
        sb.AppendLine("All plays together: " + Stats(story.All));
        sb.AppendLine("Graphics card memory is recorded for the whole card, not per app: before saying a game uses more, check what else was using the card in that play.");
        sb.AppendLine("Plays, newest first (at most 15):");
        // Video memory is the whole card's (every app together), so each play says what else had the card at the time.
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var own = entry.AppIds.ToHashSet();
        foreach (var p in story.Plays.OrderByDescending(p => p.Start).Take(15))
            sb.AppendLine($"  {p.StartTime:ddd d MMM h:mm tt} to {p.EndTime:h:mm tt}, {Units.Duration(p.ActiveSec)} in front, ended: {p.Ending}. {Stats(p.Stats)}"
                + Alongside(db, p, own, apps));
        foreach (var c in story.Changes.TakeLast(10)) sb.AppendLine($"Changed between plays: {c.Change.Time:d MMM} {c.Change.Shown}");
        return sb.ToString();
    }

    /// <summary>
    /// The other apps working the graphics card during a play (its average over the play, the busiest three), and how
    /// the card's memory went from the start of the play to its highest.
    /// </summary>
    private string Alongside(RigsightDb db, GamePlay p, HashSet<long> own, Dictionary<long, AppRow> apps)
    {
        var parts = new List<string>();
        var minutes = db.GetMinutes(p.Start, p.End);
        if (minutes.Where(m => m.VramUsed is not null).ToList() is { Count: > 0 } vram)
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

    private static string Stats(PlayStats s)
    {
        var parts = new List<string>();
        void Add(string label, double? v, string unit) { if (v is double x) parts.Add($"{label} {x:0}{unit}"); }
        Add("GPU temp avg", s.Gpu, " °C"); Add("GPU temp max", s.GpuMax, " °C"); Add("hot spot max", s.HotMax, " °C");
        Add("CPU temp avg", s.Cpu, " °C"); Add("CPU temp max", s.CpuMax, " °C"); Add("GPU load", s.GpuLoad, "%");
        Add("GPU power", s.GpuPower, " W"); Add("CPU+GPU power", s.Power, " W"); Add("FPS avg", s.Fps, "");
        if (s.VramMax is double v && s.VramTotal is double tot) parts.Add($"graphics card memory in use max {v / 1024:0.0} of {tot / 1024:0.0} GB (the whole card, every app together, not this game alone)");
        if (s.Minutes > 0) parts.Add($"GPU at 95%+ for {s.GpuFullMinutes} of {s.Minutes} min");
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
        lines.Add("Cooling: not known to RigSight (it reads fan and pump speeds, not the cooler's model).");
        return string.Join("\n", lines);
    }
}

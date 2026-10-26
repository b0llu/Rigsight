using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Rewind;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ai;

/// <summary>
/// The look-ups for the pages that had none: Home (today), All sensors (every reading now), Rewind and the person's own
/// dashboards. Each says what its page says, built from the same Core sources the page uses, and leaves out what
/// Settings › Ask RigSight says not to send. Readings the app holds live (sensors, drive health) come through hooks the
/// app sets; without them the last recorded minute stands in, with how old it is.
/// </summary>
public sealed partial class AiTools
{
    /// <summary>Every reading right now, as the app's All sensors page has them (AiLiveReadings in the app). Null: tests, or no app.</summary>
    public Func<string>? SensorsNow { get; set; }

    /// <summary>Each drive's health right now (SMART status, an SSD's life left), from the app. Null: not known here.</summary>
    public Func<string>? DrivesNow { get; set; }

    /// <summary>
    /// RivaTuner Statistics Server as the agent last said ("missing", "install-failed", "installing", "stopped", "running"),
    /// from the app: why a game has no frame rate. Null: not known here.
    /// </summary>
    public Func<string?>? RivaTuner { get; set; }

    /// <summary>
    /// What the last scan of a drive found (its biggest folders, its biggest files by kind, and the space that could be
    /// freed on it), from the app's Storage scanner, for a drive letter; null when it hasn't been scanned.
    /// </summary>
    public Func<string, string?>? DriveScan { get; set; }

    /// <summary>A sensor's name as the app shows it, from its identifier (null: not known here, the identifier stands in).</summary>
    public Func<string, string?>? SensorName { get; set; }

    private static IEnumerable<JsonObject> PageDefinitions() =>
    [
        Def("get_today", "What RigSight's Home page shows for today so far: PC health against this PC's usual (temperatures, fans, problems in the last 7 days, drives), and today's report (time on, peaks, what stood out, apps used).", new JsonObject()),
        Def("get_sensors_now", "Every sensor reading right now, grouped by part: temperatures, loads, clocks, power, voltages, fan speeds, memory, drives. A moment, looked at again each time.", new JsonObject()),
        Def("get_rewind", "RigSight's Rewind for a month or a year: time on and in use, busiest day, top apps and games, longest play, hottest moments, new driver, apps installed, internet used, crashes and shut-offs.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["period"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("month", "year"), ["description"] = "month or year." },
                ["when"] = new JsonObject { ["type"] = "string", ["description"] = "yyyy-MM for a month, yyyy for a year. Leave out for last month, or this year so far." },
            },
            ["required"] = new JsonArray("period"),
        }),
        Def("get_drive_scan", "What is using the space on one drive, from RigSight's scan of it: the biggest folders, the biggest files by kind and folder, and the space that could be freed there (temporary files, caches, old downloads, the Recycle Bin). Only after a scan: when there is none, it says so, and the answer offers [do: scan <letter>].", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["drive"] = new JsonObject { ["type"] = "string", ["description"] = "The drive's letter, like \"C\" or \"E\"." } },
            ["required"] = new JsonArray("drive"),
        }),
        Def("get_widgets", "The Widgets page: the desktop widgets that are on, with their readings, the ones that are off, the built-in kinds, and every reading a widget can show. For suggesting widgets: suggest only from these.", new JsonObject()),
        Def("get_overlay", "The Overlay page: the in-game overlay, on or off, its shortcut and the readings it shows, and every reading it can show. For suggesting overlay readings: suggest only from these.", new JsonObject()),
        Def("get_taskbar", "The Taskbar page: the readings shown beside the clock, and every reading that can be added there. For suggesting taskbar readings: suggest only from these.", new JsonObject()),
        Def("get_dashboard", "The dashboards the person made from tiles. Without a name: their names and what tiles each has. With a name: what each of its tiles shows right now.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The dashboard's name as the list gives it." } },
        }),
    ];

    private string? RunPages(string name, JsonObject args) => name switch
    {
        "get_today" => WithDb(Today),
        "get_sensors_now" => SensorsText(),
        "get_rewind" => WithDb(db => RewindText(db, (string?)args["period"] ?? "month", (string?)args["when"])),
        "get_dashboard" => (string?)args["name"] is { Length: > 0 } dash ? DashboardText(dash) : DashboardList(),
        "get_drive_scan" => DriveScanText((string?)args["drive"]),
        "get_widgets" => WidgetsText(),
        "get_overlay" => OverlayText(),
        "get_taskbar" => TaskbarText(),
        _ => null,
    };

    // ── Widgets, the overlay, the taskbar ────────────────────────────────

    /// <summary>
    /// A reading by its name: one of RigSight's own, or a sensor ("sensor:&lt;id&gt;" on a widget, the bare id on the
    /// taskbar) by the name the app shows it under.
    /// </summary>
    private string ReadingName(string id) =>
        Enum.TryParse<OverlayMetric>(id, out var m) && Enum.IsDefined(m) ? WidgetCatalog.MetricName(m)
        : id.StartsWith(WidgetCatalog.SensorPrefix, StringComparison.Ordinal) ? SensorName?.Invoke(id[WidgetCatalog.SensorPrefix.Length..]) ?? id[WidgetCatalog.SensorPrefix.Length..]
        : SensorName?.Invoke(id) ?? id;

    // What each place can show (2026-10-26: the taskbar question was offered "1% low", which the taskbar can't show): the
    // overlay its own readings, not the widgets-only ones after Clock; the taskbar the readings it shows as a sensor.
    private static readonly OverlayMetric[] OverlayReadings = [.. Enum.GetValues<OverlayMetric>().Where(m => m <= OverlayMetric.Clock)];
    private static readonly OverlayMetric[] TaskbarReadings =
    [
        OverlayMetric.CpuTemp, OverlayMetric.CpuLoad, OverlayMetric.CpuClock, OverlayMetric.CpuPower, OverlayMetric.GpuTemp, OverlayMetric.GpuHotSpot,
        OverlayMetric.GpuLoad, OverlayMetric.GpuClock, OverlayMetric.GpuPower, OverlayMetric.GpuMemory, OverlayMetric.Ram,
    ];

    /// <summary>The readings a place can show, and whether RivaTuner (which the frame readings need) is running.</summary>
    private string Pickable(IEnumerable<OverlayMetric> readings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("READINGS THAT CAN BE PICKED HERE (no others): " + string.Join(", ", readings.Select(m =>
            WidgetCatalog.MetricName(m) + (m is OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow ? " (needs RivaTuner)" : ""))) + ", and any sensor by its name (get_sensors_now has every one).");
        if (readings.Any(m => m is OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow) && RivaTuner?.Invoke() is { } rtss)
            sb.AppendLine($"RivaTuner, which the frame readings need: {rtss}.");
        return sb.ToString();
    }

    // The Widgets, Overlay and Taskbar pages, said as the pages set them (2026-10-26: those pages' AI buttons had no
    // look-up, so a suggestion could name a reading RigSight doesn't have, or one already on). Suggest only from these.
    private string WidgetsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("The desktop widgets, as the Widgets page sets them (each turned on there, moved anywhere, its readings changed). Suggest only from these, by these names, and say when one is already on.");
        var on = settings.Widgets.Where(w => w.Enabled).ToList();
        sb.AppendLine(on.Count == 0 ? "On: none." : "On: " + string.Join("; ", on.Select(w => $"{WidgetCatalog.Title(w)} ({string.Join(", ", WidgetCatalog.ItemsOf(w).Select(i => ReadingName(i.Id)))})")) + ".");
        var off = settings.Widgets.Where(w => !w.Enabled).Select(WidgetCatalog.Title).ToList();
        if (off.Count > 0) sb.AppendLine("Off: " + string.Join(", ", off) + ".");
        sb.AppendLine("Built in: " + string.Join("; ", WidgetCatalog.Listed.Select(st => $"{WidgetCatalog.Title(st)}, {WidgetCatalog.Description(st).TrimEnd('.')}")) + ".");
        sb.AppendLine($"One of their own (Create widget): a {string.Join(", ", WidgetCatalog.CustomLayouts.Select(WidgetCatalog.LayoutName))} with any readings below.");
        sb.Append(Pickable(Enum.GetValues<OverlayMetric>()));
        return sb.ToString();
    }

    private string OverlayText()
    {
        var o = settings.Overlay;
        var sb = new StringBuilder();
        sb.AppendLine("The in-game overlay, as the Overlay page sets it. Suggest only from these, by these names, and say when one is already shown.");
        sb.AppendLine(o.Enabled ? $"On: shown and hidden in a game with {o.Hotkey}." : "Off.");
        sb.AppendLine("Shows: " + string.Join(", ", o.Metrics.Select(WidgetCatalog.MetricName))
            + (o.Sensors.Count > 0 ? "; and the sensors " + string.Join(", ", o.Sensors.Select(x => x.Label ?? ReadingName(x.Id))) : "") + ".");
        sb.Append(Pickable(OverlayReadings));
        return sb.ToString();
    }

    private string TaskbarText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("The readings beside the clock, as the Taskbar page sets them. Suggest only from these, by these names, and say when one is already there.");
        sb.AppendLine(settings.TraySensors.Count == 0 ? "Shown: none." : "Shown: " + string.Join(", ", settings.TraySensors.Select(ReadingName)) + ".");
        sb.Append(Pickable(TaskbarReadings));
        return sb.ToString();
    }

    /// <summary>The last scan of a drive, or how to get one (the button the answer may offer).</summary>
    private string DriveScanText(string? drive)
    {
        if (drive is null || Regex.Match(drive, @"^\s*([A-Za-z])") is not { Success: true } m) return "Name a drive by its letter, like \"C\".";
        string letter = m.Groups[1].Value.ToUpperInvariant();
        if (DriveScan?.Invoke(letter) is { Length: > 0 } scan) return scan;
        return $"{letter}: hasn't been scanned, so what fills it isn't known yet. Offer the button [do: scan {letter}]: pressed, RigSight scans the drive (a minute or two) and asks again by itself with what it found.";
    }

    // ── Home: today ──────────────────────────────────────────────────────
    private string Today(RigsightDb db)
    {
        var now = DateTime.Now;
        var day = now.Date;
        var sb = new StringBuilder();
        sb.AppendLine($"Today so far, {now.ToString("dddd d MMMM yyyy, h:mm tt", CultureInfo.InvariantCulture)}, as RigSight's Home page shows it.");

        var report = BuildReport(db, day, day);
        Trim(report);
        // As Home reads them: the PC's own problems in the last 7 days, shut-offs that look like the power apart.
        var problems = new List<CrashKind>();
        int mains = 0, mainsLikely = 0;
        if (On(Switch.Crashes))
        {
            var crashes = db.GetCrashes(TimeUtil.ToUnix(day.AddDays(-6)), TimeUtil.ToUnix(now.AddDays(1))).Where(e => !settings.IsCrashMuted(e.AppExe)).ToList();
            var found = CrashCases.Of(db, crashes, settings);
            problems = [.. crashes.Where(e => PcUsual.CountsAsPcProblem(e, found.GetValueOrDefault(e.Id))).Select(e => e.Kind)];
            mains = crashes.Count(e => PcUsual.LooksLikePower(e, found.GetValueOrDefault(e.Id)));
            mainsLikely = crashes.Count(e => PcUsual.ShutOffOf(e, found.GetValueOrDefault(e.Id)) == ShutOff.PowerLikely);
        }
        var health = UsualLines.Health(report, problems, mains, mainsLikely)
            .Where(i => On(Switch.Heat) || !i.Key.Contains("heat") && !i.Key.Contains("temps") && !i.Key.StartsWith("fan"))
            .Where(i => On(Switch.Crashes) || i.Key is not ("no-problems" or "pc-problems" or "usual-problems" or "usual-power" or "power-cuts"))
            .ToList();
        if (health.Count > 0)
        {
            sb.AppendLine("PC health (against this PC's usual):");
            foreach (var i in health) sb.AppendLine($"  {i.Text}");
        }
        if (On(Switch.Memory))
        {
            string? drives = DrivesNow?.Invoke();
            if (!string.IsNullOrWhiteSpace(drives)) sb.AppendLine("Drives right now:" + Environment.NewLine + drives.TrimEnd());
            else if (Fullest(db) is { } full) sb.AppendLine($"Fullest drive (daily record): {full}. Drive health isn't known here; get_storage has the drives.");
        }
        sb.AppendLine();
        sb.Append(Report(db, day, day));
        return sb.ToString();
    }

    /// <summary>"C: 81% full", the fullest drive by the last daily record, or null when none is recorded.</summary>
    private static string? Fullest(RigsightDb db)
    {
        var last = db.GetDriveDays(TimeUtil.ToUnix(DateTime.Today.AddDays(-7))).GroupBy(d => d.Drive).Select(g => g.OrderBy(d => d.Day).Last())
            .Where(d => d.TotalGb > 0).OrderByDescending(d => d.UsedGb / d.TotalGb).FirstOrDefault();
        return last is null ? null : $"{last.Drive.TrimEnd('\\')} {last.UsedGb / last.TotalGb * 100:0}% full ({Units.DataGb(last.UsedGb)} of {Units.DataGb(last.TotalGb)})";
    }

    // ── All sensors: now ─────────────────────────────────────────────────
    private string SensorsText()
    {
        if (SensorsNow?.Invoke() is { Length: > 0 } live) return live + LeftOut(Switch.Memory);
        return WithDb(LastMinute) + LeftOut(Switch.Memory);
    }

    /// <summary>Without the app's live readings: the agent's last recorded minute, saying how old it is.</summary>
    private string LastMinute(RigsightDb db)
    {
        long nowTs = TimeUtil.ToUnix(DateTime.Now);
        if (db.LastMinuteBefore(nowTs + 60) is not long ts) return "No readings: nothing has been recorded yet.";
        var m = db.GetMinutes(ts, ts + 60).FirstOrDefault();
        if (m is null) return "No readings: nothing has been recorded yet.";
        var at = TimeUtil.FromUnix(ts);
        double ago = (DateTime.Now - at).TotalMinutes;
        var sb = new StringBuilder();
        sb.AppendLine($"Live readings aren't available here, so this is the last minute recorded: {at.ToString("ddd d MMM h:mm tt", CultureInfo.InvariantCulture)} ({(ago < 2 ? "about a minute ago" : $"{ago:0} minutes ago")}).");
        sb.AppendLine($"  CPU: {T(m.CpuTemp)} (up to {T(m.CpuTempMax)}), {Pct(m.CpuLoad)} busy" + (m.CpuClock is double cc ? $", {cc:0} MHz" : "") + (m.CpuPower is double cp ? $", {cp:0} W" : ""));
        sb.AppendLine($"  GPU: {T(m.GpuTemp)} (up to {T(m.GpuTempMax)}), {Pct(m.GpuLoad)} busy" + (m.GpuClock is double gc ? $", {gc:0} MHz" : "") + (m.GpuPower is double gp ? $", {gp:0} W" : "")
            + (m.GpuHotMax is double h ? $", hot spot up to {h:0} °C" : "") + (m.GpuMemMax is double mj ? $", memory up to {mj:0} °C" : ""));
        if (On(Switch.Memory) && m.RamUsed is double ram) sb.AppendLine($"  Memory: {ram:0.0} GB in use");
        var fans = db.GetFans().ToDictionary(f => f.Id);
        var speeds = db.GetFanMinutes(ts, ts + 60).Select(f => fans.TryGetValue(f.Fan, out var fan) ? $"{fan.Name} {f.RpmAvg} rpm" : null).OfType<string>().ToList();
        if (speeds.Count > 0) sb.AppendLine("  Fans: " + string.Join(", ", speeds));
        return sb.ToString();
    }

    // ── Rewind ───────────────────────────────────────────────────────────
    private string RewindText(RigsightDb db, string period, string? when)
    {
        bool year = period.Equals("year", StringComparison.OrdinalIgnoreCase);
        if (!year && !period.Equals("month", StringComparison.OrdinalIgnoreCase)) throw new FormatException("'period' must be month or year.");
        var today = DateTime.Today;
        DateTime from;
        if (string.IsNullOrWhiteSpace(when)) from = year ? new DateTime(today.Year, 1, 1) : new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        else if (year && DateTime.TryParseExact(when, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var y)) from = y;
        else if (!year && DateTime.TryParseExact(when, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var mo)) from = mo;
        else throw new FormatException(year ? "'when' must be a year like 2026." : "'when' must be a month like 2026-09.");
        if (from > today) return $"{Said(from, year)} hasn't started yet.";
        var to = year ? from.AddYears(1) : from.AddMonths(1);
        if (to > today.AddDays(1)) to = today.AddDays(1);
        var f = RewindBuilder.Build(db, year ? RewindKind.Year : RewindKind.Month, from, to, settings, DateTime.Now);
        if (f.OnSec <= 0 && f.DaysOn == 0) return $"Nothing was recorded in {Said(from, year)}.";

        var sb = new StringBuilder();
        bool soFar = to <= (year ? from.AddYears(1) : from.AddMonths(1)).AddDays(-1);
        sb.AppendLine($"Rewind of {Said(from, year)}{(soFar ? " so far" : "")} ({f.Days} days):");
        if (f.Time && On(Switch.Usage))
        {
            sb.AppendLine($"  On {Units.Duration(f.OnSec)}, in use {Units.Duration(f.ActiveSec)}, on {f.DaysOn} of {f.Days} days.");
            if (f.BusiestDay is { } busy) sb.AppendLine($"  Busiest day: {busy.ToString("ddd d MMM", CultureInfo.InvariantCulture)}, {Units.Duration(f.BusiestDaySec)} in use.");
        }
        if (f.Time && On(Switch.Apps) && f.TopApps.Count > 0)
            sb.AppendLine("  Top apps: " + string.Join(", ", f.TopApps.Take(5).Select(a => $"{a.Name} {Units.Duration(a.Value)}")) + ".");
        if (f.Games && On(Switch.Apps) && f.GameCount > 0)
        {
            sb.AppendLine($"  Games: {Units.Duration(f.GamingSec)} over {f.Plays} plays of {f.GameCount} games; most played: " + string.Join(", ", f.TopGames.Take(5).Select(g => $"{g.Name} {Units.Duration(g.Value)}")) + ".");
            if (f.LongestGame is { } longest && f.LongestStart is { } ls)
                sb.AppendLine($"  Longest play: {longest.Name}, {Units.Duration(longest.Value)}, from {ls.ToString("ddd d MMM h:mm tt", CultureInfo.InvariantCulture)}.");
            if (f.NightShare > 0) sb.AppendLine($"  {f.NightShare * 100:0}% of play was at night.");
        }
        if (f.Heat && On(Switch.Heat))
        {
            void Hot(string part, RewindHeat? h)
            {
                if (h is null) return;
                string app = h.App is { Length: > 0 } a && On(Switch.Apps) ? $", with {a} {(h.InGame ? "being played" : "working it hardest")}" : "";
                sb.AppendLine($"  Hottest {part}: {h.Value:0} °C on {h.At.ToString("ddd d MMM h:mm tt", CultureInfo.InvariantCulture)}{app}.");
            }
            Hot("CPU", f.CpuHot);
            Hot("GPU", f.GpuHot);
            Hot("GPU hot spot", f.HotSpot);
        }
        if (f.Changes && On(Switch.Changes))
        {
            if (f.NewDriver is { Length: > 0 } driver) sb.AppendLine($"  New graphics driver: {driver}" + (f.NewDriverAt is { } d ? $" on {d.ToString("d MMM", CultureInfo.InvariantCulture)}." : "."));
            if (f.Installed + f.Updated + f.Removed > 0) sb.AppendLine($"  Apps: {f.Installed} installed, {f.Updated} updated, {f.Removed} removed.");
        }
        if (f.Internet && On(Switch.Network) && f.Down + f.Up > 0)
            sb.AppendLine($"  Internet: {Units.Data(f.Down)} down, {Units.Data(f.Up)} up" + (On(Switch.Apps) && f.TopInternet.Count > 0 ? "; most: " + string.Join(", ", f.TopInternet.Take(3).Select(a => $"{a.Name} {Units.Data((long)a.Value)}")) : "") + ".");
        if (f.Problems && On(Switch.Crashes))
            sb.AppendLine($"  App crashes: {f.AppCrashes}. Sudden shut-offs: {f.ShutOffs}.");
        return sb.ToString() + LeftOut(Switch.Usage, Switch.Apps, Switch.Heat, Switch.Changes, Switch.Network, Switch.Crashes);
    }

    private static string Said(DateTime from, bool year) => from.ToString(year ? "yyyy" : "MMMM yyyy", CultureInfo.InvariantCulture);

    // ── Your dashboards ──────────────────────────────────────────────────
    private string DashboardList()
    {
        var pages = settings.CustomPages;
        if (pages.Count == 0) return "The person hasn't made any dashboards.";
        var sb = new StringBuilder();
        sb.AppendLine($"The person's dashboards ({pages.Count}):");
        foreach (var p in pages)
            sb.AppendLine($"  {Clean(p.Name)}: " + (p.Tiles.Count == 0 ? "no tiles" : string.Join(", ", p.Tiles.Select(TileName))));
        return sb.ToString();
    }

    private string DashboardText(string name)
    {
        var page = settings.CustomPages.FirstOrDefault(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (page is null)
            return settings.CustomPages.Count == 0 ? "The person hasn't made any dashboards."
                : $"No dashboard is called \"{Clean(name)}\". The dashboards are: {string.Join(", ", settings.CustomPages.Select(p => Clean(p.Name)))}.";
        if (page.Tiles.Count == 0) return $"The dashboard \"{Clean(page.Name)}\" has no tiles.";
        var kinds = page.Tiles.Select(t => t.Kind).ToHashSet();
        var sb = new StringBuilder();
        sb.AppendLine($"The dashboard \"{Clean(page.Name)}\", its tiles top to bottom: {string.Join(", ", page.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X).Select(TileName))}.");
        sb.AppendLine("What they show right now:");
        // Each kind of data once, however many tiles show it, from the look-up that page's tiles read.
        if (kinds.Overlaps(LiveKinds)) sb.AppendLine("Live readings:" + Environment.NewLine + SensorsText().TrimEnd());
        if (kinds.Overlaps(DayKinds) && (On(Switch.Heat) || On(Switch.Apps)))
            sb.AppendLine("Today:" + Environment.NewLine + WithDb(db => Report(db, DateTime.Today, DateTime.Today)).TrimEnd());
        if (kinds.Contains("yesterday") && (On(Switch.Heat) || On(Switch.Apps)))
            sb.AppendLine("Yesterday:" + Environment.NewLine + WithDb(db => Report(db, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-1))).TrimEnd());
        if (kinds.Contains("crashes")) sb.AppendLine(On(Switch.Crashes) ? WithDb(RecentCrashes) : "Crashes: left out, the user turned off sharing \"Crashes and shut-offs\".");
        if (kinds.Overlaps(NetKinds))
            sb.AppendLine(On(Switch.Network) ? "Internet today:" + Environment.NewLine + WithDb(db => Network(db, DateTime.Today, DateTime.Today)).TrimEnd()
                : "Internet: left out, the user turned off sharing \"Internet use\".");
        return sb.ToString();
    }

    private static readonly HashSet<string> LiveKinds = ["cpu-gauge", "gpu-gauge", "temp-chart", "fans", "drives", "top-memory", "sensor"];
    private static readonly HashSet<string> DayKinds = ["today", "most-used", "insights", "peaks"];
    private static readonly HashSet<string> NetKinds = ["net-speed", "net-now", "net-today", "net-apps", "net-chart", "net-drops"];

    /// <summary>The dashboard tile's title in English (the app's catalog is in the person's language).</summary>
    private static string TileName(TileConfig t) => t.Kind switch
    {
        "cpu-gauge" => "CPU temperature",
        "gpu-gauge" => "GPU temperature",
        "temp-chart" => "Temperature chart",
        "fans" => "Fans",
        "drives" => "Drives",
        "top-memory" => "Top memory users",
        "sensor" => t.Sensor switch
        {
            "key:" + KeySensors.CpuLoad => "CPU load",
            "key:" + KeySensors.CpuPower => "CPU power",
            "key:" + KeySensors.CpuClock => "CPU clock",
            "key:" + KeySensors.GpuLoad => "GPU load",
            "key:" + KeySensors.GpuPower => "GPU power",
            "key:" + KeySensors.GpuHotSpot => "GPU hot spot",
            "key:" + KeySensors.GpuMemJunction => "GPU memory temperature",
            "key:" + KeySensors.GpuFan => "GPU fan",
            "key:" + KeySensors.GpuVramLoad => "Video memory",
            "key:" + KeySensors.RamLoad => "RAM",
            _ => "A sensor reading",
        },
        "net-speed" => "Internet speed",
        "net-now" => "Using the internet now",
        "net-today" => "Internet today",
        "net-apps" => "Top internet users today",
        "net-chart" => "Internet use by hour",
        "net-drops" => "Connection today",
        "today" => "Today so far",
        "most-used" => "Most used today",
        "insights" => "What stands out",
        "peaks" => "Today's peaks",
        "yesterday" => "Yesterday",
        "crashes" => "Crashes",
        _ => "A tile",
    };

    /// <summary>The Crashes tile: the last 30 days' count and the latest one.</summary>
    private string RecentCrashes(RigsightDb db)
    {
        var list = db.GetCrashes(TimeUtil.ToUnix(DateTime.Today.AddDays(-29)), TimeUtil.ToUnix(DateTime.Now.AddDays(1))).Where(e => !settings.IsCrashMuted(e.AppExe)).ToList();
        if (list.Count == 0) return "Crashes: none in the last 30 days.";
        var latest = list.MaxBy(e => e.Ts)!;
        return $"Crashes: {list.Count} in the last 30 days; the latest {TimeUtil.FromUnix(latest.Ts).ToString("ddd d MMM h:mm tt", CultureInfo.InvariantCulture)}, {CrashExplainer.Explain(latest).Title}.";
    }

    /// <summary>A name the person typed, on one line and not too long.</summary>
    private static string Clean(string text)
    {
        string one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= 60 ? one : one[..60] + "…";
    }
}

using System.Text.RegularExpressions;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ai;

/// <summary>What a button under an answer does.</summary>
public enum AiActionKind { CloseApp, CheckAgain, OpenPage, OpenWindows, VerifyGame, OpenSharing, StartAgent, CopyReport, ScanDrive, OpenApp, OpenWeb, CreateWidget, AddOverlay, AddTaskbar }

/// <summary>
/// A reading an answer can put on a widget, the overlay or the taskbar: one of the overlay's own (<see cref="Metric"/>) or one
/// of the PC's sensors (<see cref="SensorId"/>), with the name it shows. <see cref="Part"/>: a sensor's part, so "GPU Core"
/// can be told from "CPU Core" when an answer names both.
/// </summary>
public sealed record AiReading(string Name, OverlayMetric? Metric = null, string? SensorId = null, string? Part = null)
{
    /// <summary>As a widget stores it: the metric's name, or "sensor:" and the sensor's id.</summary>
    public string Key => Metric is { } m ? m.ToString() : WidgetCatalog.SensorPrefix + SensorId;
    /// <summary>A frame reading, which only RivaTuner measures.</summary>
    public bool IsFrame => Metric is OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow;
}

/// <summary>
/// A button under an answer, worked out by RigSight from the model's "[do: …]" line and checked against what is really
/// there: <see cref="Target"/> is the app's exe, the page, the Windows screen's address, or the game's verify link.
/// </summary>
/// <param name="Windowless">A Close's app has no window open (what the card says about it, measured, not the model's words).</param>
public sealed record AiAction(AiActionKind Kind, string Label, string Glyph, string Target = "", string? Focus = null, int Minutes = 0,
    CheckWhat What = CheckWhat.Fans, double MemMb = 0, bool Windowless = false, IReadOnlyList<AiReading>? Readings = null)
{
    /// <summary>A card that changes RigSight's own settings (a widget, the overlay, the taskbar), with Undo.</summary>
    public bool IsSetup => Kind is AiActionKind.CreateWidget or AiActionKind.AddOverlay or AiActionKind.AddTaskbar;
}

/// <summary>
/// What the buttons are checked against: this answer's look-ups, what was running when it looked, the games installed,
/// which pages the answers may read now (the plan and Settings › AI; null: all), and the known apps on this PC.
/// <paramref name="Sensors"/>: the PC's sensors by the names they show, read on the window's thread when the answer came
/// (null: none known yet); <paramref name="TrayOf"/>: the sensor the taskbar shows for an overlay reading ("CPU temperature").
/// </summary>
public sealed record AiActionContext(IReadOnlyList<AiLookUp> LookUps, RunningSnapshot? Running, Func<IReadOnlyList<InstalledGame>> Installed, bool AgentRunning,
    Func<string, bool>? CanRead = null, Func<IReadOnlyDictionary<string, StartApp>>? Apps = null, Func<IReadOnlyList<StartApp>>? StartMenu = null,
    IReadOnlyList<AiReading>? Sensors = null, IReadOnlyDictionary<OverlayMetric, AiReading>? TrayOf = null)
{
    public bool Reads(string page) => CanRead?.Invoke(page) ?? true;
}

/// <summary>
/// The buttons an answer may carry (the 2026-10-22 audit, rigsight-ai-actions-audit): the model asks for one with a
/// "[do: …]" line from a fixed list; RigSight decides what it does, from its own records, and drops any it can't stand
/// behind. Nothing in the model's words runs by itself: records can carry made-up names, so an app is only ever one this
/// answer saw running, a game one installed, a page or Windows screen one on the list.
/// </summary>
public static class AiActions
{
    public const int Most = 3;

    private static readonly Regex Line = new(@"^\[\s*do\s*:\s*(?<what>[^\]]+?)\s*\]$", RegexOptions.IgnoreCase);
    public static readonly Regex Inline = new(@"[ \t]*\[\s*do\s*:\s*(?<what>[^\]\n]+?)\s*\][ \t]*", RegexOptions.IgnoreCase);

    /// <summary>The words of a "[do: …]" line ("close python"), or null for any other line.</summary>
    public static string? Marker(string line) => Line.Match(line.Trim()) is { Success: true } m ? m.Groups["what"].Value.Trim() : null;

    /// <summary>The pages a button can open, with the name it shows.</summary>
    private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crashes"] = Lang.Key("Crashes"), ["timeline"] = Lang.Key("Timeline"), ["games"] = Lang.Key("Games"), ["reports"] = Lang.Key("Reports"), ["temperatures"] = Lang.Key("Temperatures"),
        ["fans"] = Lang.Key("Fans"), ["memory"] = Lang.Key("Memory"), ["storage"] = Lang.Key("Storage"), ["network"] = Lang.Key("Network"), ["processes"] = Lang.Key("Processes"),
        ["overlay"] = Lang.Key("Overlay"),
    };

    /// <summary>The Windows screens a button can open: only screens, never a change.</summary>
    private static readonly Dictionary<string, (string Uri, string Label, string Glyph)> Windows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["storage-sense"] = ("ms-settings:storagesense", Lang.Key("Open Storage Sense"), ""),
        ["disk-cleanup"] = ("cleanmgr.exe", Lang.Key("Open Disk Cleanup"), ""),
        ["startup-apps"] = ("ms-settings:startupapps", Lang.Key("Open Startup apps"), ""),
        ["update-history"] = ("ms-settings:windowsupdate-history", Lang.Key("Open Windows Update history"), ""),
        ["installed-apps"] = ("ms-settings:appsfeatures", Lang.Key("Open Installed apps"), ""),
        ["memory-diagnostic"] = ("mdsched.exe", Lang.Key("Open Windows Memory Diagnostic"), ""),
        ["network-status"] = ("ms-settings:network-status", Lang.Key("Open Network status"), ""),
        ["windows-update"] = ("ms-settings:windowsupdate", Lang.Key("Open Windows Update"), ""),
        ["optional-updates"] = ("ms-settings:windowsupdate-optionalupdates", Lang.Key("Open Optional updates"), ""),
        ["device-manager"] = ("devmgmt.msc", Lang.Key("Open Device Manager"), ""),
        ["graphics-settings"] = ("ms-settings:display-advancedgraphics", Lang.Key("Open Graphics settings"), ""),
        ["power-mode"] = ("ms-settings:powersleep", Lang.Key("Open Power settings"), ""),
        ["game-mode"] = ("ms-settings:gaming-gamemode", Lang.Key("Open Game Mode"), ""),
        ["windows-security"] = ("windowsdefender:", Lang.Key("Open Windows Security"), ""),
        ["task-manager"] = ("taskmgr.exe", Lang.Key("Open Task Manager"), ""),
    };

    /// <summary>The buttons for these "[do: …]" lines, at most <see cref="Most"/>, each checked; why one was dropped goes to the log.</summary>
    public static IReadOnlyList<AiAction> Resolve(IEnumerable<string> markers, AiActionContext context)
    {
        var actions = new List<AiAction>();
        foreach (var marker in markers)
        {
            if (actions.Count >= Most) { Log.Write("ask", $"[do: {marker}] dropped: already {Most} buttons"); continue; }
            var (action, why) = One(marker, context);
            if (action is null) { Log.Write("ask", $"[do: {marker}] dropped: {why}"); continue; }
            if (actions.Any(a => a.Kind == action.Kind && a.Target.Equals(action.Target, StringComparison.OrdinalIgnoreCase) && a.Focus == action.Focus)) continue;
            actions.Add(action);
        }
        return actions;
    }

    private static readonly Regex RivaTunerName = new(@"\b(riva\s*tuner|rtss)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (AiAction? Action, string Why) One(string marker, AiActionContext c)
    {
        var words = marker.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "", rest = words.Length > 1 ? words[1].Trim().Trim('"', '\'') : "";
        switch (verb)
        {
            case "close":
            {
                if (c.Running is null || !c.LookUps.Any(l => l.Tool == "get_running_now")) return (null, "this answer didn't look at what is running now");
                if (c.Running.Find(rest) is not { } app) return (null, $"'{rest}' wasn't running when it looked");
                if (!app.CanClose) return (null, $"{app.Exe} isn't one an answer may close ({app.Risk}, {app.Category}{(app.InFront ? ", in front" : "")})");
                return (new AiAction(AiActionKind.CloseApp, Lang.T($"Close {app.Name}"), "", app.Exe, app.Name, MemMb: app.MemMb, Windowless: !app.HasWindow), "");
            }
            case "check":
            {
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int minutes = parts.Length > 0 && int.TryParse(parts[0], out int m) ? m : 10;
                minutes = Math.Clamp((int)Math.Round(minutes / 5.0) * 5, 5, 60);
                if (ActionCheck.Parse(parts.Length > 1 ? parts[1] : "fans") is not { } what) return (null, $"nothing to check called '{(parts.Length > 1 ? parts[1] : "")}'");
                return (new AiAction(AiActionKind.CheckAgain, Lang.T($"Check again in {minutes} min"), "", what.ToString(), Minutes: minutes, What: what), "");
            }
            case "open":
            {
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string page = parts.Length > 0 ? parts[0].ToLowerInvariant() : "", focus = parts.Length > 1 ? parts[1] : "";
                if (!Pages.TryGetValue(page, out var shown)) return (null, $"no page called '{page}'");
                string name = Lang.T(shown);
                if (page == "storage" && Regex.Match(focus, @"^([A-Za-z])(:|\b)") is { Success: true } drive)
                    return (new AiAction(AiActionKind.OpenPage, Lang.T($"Open Storage on the {drive.Groups[1].Value.ToUpperInvariant()} drive"), "", page, drive.Groups[1].Value.ToUpperInvariant()), "");
                if (page == "games" && focus.Length > 0)
                    return Installed(c, focus) is { } g
                        ? (new AiAction(AiActionKind.OpenPage, Lang.T($"Open {g.Name}"), "", page, g.Key), "")
                        : (new AiAction(AiActionKind.OpenPage, Lang.T($"Open {name}"), "", page), "");
                if (page == "processes" && focus.Length > 0)
                    return (new AiAction(AiActionKind.OpenPage, Lang.T("Open Processes by memory"), "", page, focus.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? "cpu" : "memory"), "");
                return (new AiAction(AiActionKind.OpenPage, Lang.T($"Open {name}"), "", page), "");
            }
            case "scan":
            {
                // A drive that is there and ready: the model can only name one; RigSight checks it exists.
                if (Regex.Match(rest, @"^([A-Za-z])(:|\b)") is not { Success: true } d) return (null, $"'{rest}' isn't a drive letter");
                string letter = d.Groups[1].Value.ToUpperInvariant();
                if (!DriveReady(letter)) return (null, $"there's no drive {letter}: ready on this PC");
                // A scan is for the AI to read afterwards: not when the plan or Settings › AI keeps Storage from it (the
                // person's I drive on Free, 2026-10-24: "Scan the I drive" under "Storage isn't in your plan").
                if (!c.Reads(AiPages.Storage)) return (null, "the answers can't read Storage (not in the plan, or switched off)");
                return (new AiAction(AiActionKind.ScanDrive, Lang.T($"Scan the {letter} drive"), "", letter), "");
            }
            case "windows":
                return Windows.TryGetValue(rest.Replace(' ', '-'), out var screen)
                    ? (new AiAction(AiActionKind.OpenWindows, Lang.T(screen.Label), screen.Glyph, screen.Uri), "")
                    : (null, $"no Windows screen called '{rest}'");
            case "app":
            {
                // "[do: app nvidia-app]", or "[do: app steam <game>]" for that game's page: only an app on the fixed list;
                // on this PC it opens, otherwise its maker's download page (when it has one).
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) return (null, "no app named");
                // RivaTuner starts hidden in the tray, so opening it showed nothing (a person kept pressing "Open RivaTuner
                // Statistics Server", 2026-10-27); the Overlay page starts or installs it and says whether it runs.
                if (RivaTunerName.IsMatch(rest)) return One("open overlay", c);
                if (OpenableApps.Get(parts[0]) is not { } app)
                {
                    // Any other tool, by its name on this PC's Start menu (the person's call, 2026-10-25); never downloaded.
                    return c.StartMenu?.Invoke() is { } start && OpenableApps.MatchByName(rest, start) is { } found
                        ? (new AiAction(AiActionKind.OpenApp, Lang.T($"Open {found.Name}"), "", @"shell:AppsFolder\" + found.AppId, found.Name), "")
                        : (null, $"'{rest}' isn't on RigSight's list, and no safe Start menu entry has that name");
                }
                var installed = c.Apps?.Invoke() ?? new Dictionary<string, StartApp>();
                if (installed.TryGetValue(app.Id, out var entry))
                {
                    if (app.Id == "steam" && parts.Length > 1 && Installed(c, parts[1]) is { Launcher: Launcher.Steam } game && game.Key.StartsWith("steam:", StringComparison.Ordinal))
                        return (new AiAction(AiActionKind.OpenApp, Lang.T($"Open {game.Name} in Steam"), "", "steam://nav/games/details/" + game.Key["steam:".Length..], app.Id), "");
                    return (new AiAction(AiActionKind.OpenApp, Lang.T($"Open {app.Name}"), "", @"shell:AppsFolder\" + entry.AppId, app.Id), "");
                }
                return app.Download is { } url
                    ? (new AiAction(AiActionKind.OpenWeb, Lang.T($"Get {app.Name}"), "", url, app.Id), "")
                    : (null, $"{app.Name} isn't on this PC and has no download page on the list");
            }
            case "verify":
            {
                if (Installed(c, rest) is not { } game) return (null, $"'{rest}' isn't an installed game");
                if (game.Launcher != Launcher.Steam || !game.Key.StartsWith("steam:", StringComparison.Ordinal)) return (null, $"{game.Name} isn't a Steam game (only Steam has a check-the-files link)");
                return (new AiAction(AiActionKind.VerifyGame, Lang.T($"Verify {game.Name}'s files"), "", "steam://validate/" + game.Key["steam:".Length..], game.Name), "");
            }
            case "settings" or "sharing" or "turn-on":
                return (new AiAction(AiActionKind.OpenSharing, Lang.T("Open What RigSight sends"), ""), "");
            case "start-agent" or "agent" when !c.AgentRunning:
                return (new AiAction(AiActionKind.StartAgent, Lang.T("Start the agent"), ""), "");
            case "start-agent" or "agent":
                return (null, "the agent is already running");
            case "copy-report" or "report":
                return (new AiAction(AiActionKind.CopyReport, Lang.T("Copy problem report"), ""), "");
            case "widget" or "widgets" or "overlay" or "taskbar" or "tray":
            {
                // Readings for a new widget, the overlay or the taskbar: only RigSight's own settings change, and only once
                // the card's button is pressed (board 7, 2026-10-26). Each name must be a reading RigSight knows.
                var kind = verb switch { "overlay" => AiActionKind.AddOverlay, "taskbar" or "tray" => AiActionKind.AddTaskbar, _ => AiActionKind.CreateWidget };
                var readings = Readings(rest, kind, c);
                if (readings.Count == 0) return (null, $"none of '{rest}' is a reading RigSight can put there");
                Log.Write("ask", $"[do: {marker}] readings matched: {string.Join(", ", readings.Select(r => $"{r.Name} ({r.Key})"))}");
                var (label, glyph) = kind switch
                {
                    AiActionKind.AddOverlay => (Lang.T("Add to your game overlay"), "\uE7FC"),
                    AiActionKind.AddTaskbar => (Lang.T("Add beside the clock"), "\uE75B"),
                    _ => (Lang.T("A widget with these readings"), "\uF246"),
                };
                return (new AiAction(kind, label, glyph, string.Join(",", readings.Select(r => r.Key)), Readings: readings), "");
            }
            default:
                return (null, $"no action called '{verb}'");
        }
    }

    /// <summary>The overlay's own readings: those before the widgets-only ones (Screen time and after).</summary>
    private static bool OnOverlay(OverlayMetric m) => m < OverlayMetric.ScreenTime;

    /// <summary>Other ways answers write a reading, besides its name in the editor (either language) and its code.</summary>
    private static readonly Dictionary<string, OverlayMetric> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fps"] = OverlayMetric.Fps, ["frame rate"] = OverlayMetric.Fps, ["1% lows"] = OverlayMetric.OnePercentLow, ["1% low fps"] = OverlayMetric.OnePercentLow,
        ["cpu temp"] = OverlayMetric.CpuTemp, ["gpu temp"] = OverlayMetric.GpuTemp, ["hot spot"] = OverlayMetric.GpuHotSpot, ["gpu hotspot"] = OverlayMetric.GpuHotSpot,
        ["ram"] = OverlayMetric.Ram, ["memory in use"] = OverlayMetric.Ram, ["vram"] = OverlayMetric.GpuMemory, ["gpu memory"] = OverlayMetric.GpuMemory,
    };

    private static readonly Regex NameSplit = new(@"\s*[,;|]\s*|\s+and\s+(?=[^,;|]+$)", RegexOptions.IgnoreCase);
    private static readonly Regex LeadingAnd = new(@"^and\s+", RegexOptions.IgnoreCase);

    /// <summary>
    /// The readings an answer named for <paramref name="kind"/>'s place, each once: overlay readings by name (as the widget
    /// editor names them, in English or the language in use), then the PC's sensors by the name they show. A name that is
    /// nothing RigSight knows, or that the place can't show, is dropped and logged; a widget keeps what its slim bar holds.
    /// </summary>
    internal static List<AiReading> Readings(string names, AiActionKind kind, AiActionContext c)
    {
        var found = new List<AiReading>();
        var parts = NameSplit.Split(names).Select(n => LeadingAnd.Replace(n.Trim().Trim('"', '\'', '“', '”', '.', '*').Trim(), "")).Where(n => n.Length > 0);
        int most = WidgetCatalog.MaxItems(WidgetLayout.Bar);
        foreach (var name in parts)
        {
            var reading = Metric(name) is { } m ? new AiReading(WidgetCatalog.MetricName(m), m) : Sensor(name, c);
            if (reading is null) { Log.Write("ask", $"Reading '{name}' dropped: no reading or sensor by that name"); continue; }
            if (kind == AiActionKind.AddOverlay && reading.Metric is { } om && !OnOverlay(om)) { Log.Write("ask", $"Reading '{name}' dropped: the overlay doesn't show {om}"); continue; }
            if (kind == AiActionKind.AddTaskbar && reading.Metric is { } tm)
            {
                // The taskbar shows sensors: an overlay reading goes as the sensor that measures it on this PC.
                // A sensor of the same name ("GPU Hot Spot") when there's none on the list for it.
                if (c.TrayOf?.GetValueOrDefault(tm) is { } sensor) reading = sensor with { Name = reading.Name };
                else if (Sensor(name, c) is { } same) reading = same;
                else { Log.Write("ask", $"Reading '{name}' dropped: no sensor for {tm} on this PC for the taskbar"); continue; }
            }
            if (found.Any(f => f.Key == reading.Key)) continue;
            if (kind == AiActionKind.CreateWidget && found.Count >= most) { Log.Write("ask", $"Reading '{name}' dropped: a widget's bar holds {most}"); continue; }
            found.Add(reading);
        }
        return found;
    }

    private static OverlayMetric? Metric(string name)
    {
        if (Aliases.TryGetValue(name, out var alias)) return alias;
        foreach (var m in Enum.GetValues<OverlayMetric>())
        {
            if (m.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) || WidgetCatalog.MetricName(m).Equals(name, StringComparison.OrdinalIgnoreCase)) return m;
            using (Lang.UseHere(null))
                if (WidgetCatalog.MetricName(m).Equals(name, StringComparison.OrdinalIgnoreCase)) return m;
        }
        return null;
    }

    private static AiReading? Sensor(string name, AiActionContext c) =>
        c.Sensors is not { } all ? null
        : all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
          ?? all.FirstOrDefault(s => s.Part is { } part && ($"{part} {s.Name}".Equals(name, StringComparison.OrdinalIgnoreCase) || $"{s.Name} ({part})".Equals(name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Whether a drive is there and readable (the tests give their own).</summary>
    internal static Func<string, bool> DriveReady { get; set; } = letter =>
    {
        try { return new System.IO.DriveInfo(letter).IsReady; }
        catch (ArgumentException) { return false; }
    };

    private static InstalledGame? Installed(AiActionContext c, string named)
    {
        if (named.Length == 0) return null;
        var games = c.Installed();
        return games.FirstOrDefault(g => g.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
            ?? games.FirstOrDefault(g => g.Name.Replace("®", "").Replace("™", "").Contains(named, StringComparison.OrdinalIgnoreCase));
    }
}

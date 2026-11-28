using Rigsight.Core.Ai;
using Rigsight.Core.Drivers;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Advice;

/// <summary>What a step needs or what follows it, said beside it: only where there is a consequence or a requirement.</summary>
public enum TryTag { None, Windows, Steam, Restart, Bios, PcOff }

/// <summary>What a step's button does. Only things RigSight already does elsewhere; nothing runs by itself.</summary>
public enum TryDo
{
    None,
    /// <summary>Opens a Windows screen from the AI's fixed list (<see cref="AiActions.Screen"/>): <see cref="TryStep.Target"/> is its key.</summary>
    Windows,
    /// <summary>Opens a RigSight page: <see cref="TryStep.Target"/> is its name ("fans", "timeline").</summary>
    Page,
    /// <summary>Steam checks the game's files: <see cref="TryStep.Target"/> is its Steam app id.</summary>
    VerifyGame,
    /// <summary>Asks an app's windows to close, after a question: <see cref="TryStep.Target"/> is its exe.</summary>
    CloseApp,
    /// <summary>Empties every drive's Recycle Bin, after a question naming how many and how much.</summary>
    EmptyBin,
    /// <summary>Windows Memory Diagnostic at the next start, now or later, after a question.</summary>
    MemoryTest,
}

/// <summary>One thing to try: what to do, a quieter line under it where it helps, and a button where RigSight can do or open it.</summary>
public sealed record TryStep(string Id, string Text)
{
    public string? Why { get; init; }
    public TryTag Tag { get; init; }
    public TryDo Do { get; init; }
    public string Target { get; init; } = "";
    /// <summary>The button's words (English, translated where shown); null where the step is text only.</summary>
    public string? Button { get; init; }
    /// <summary>Something done by hand that RigSight can't see (dust, a cable, the BIOS): once tried, the step says so.</summary>
    public bool CantSee { get; init; }
    /// <summary>The app's name, for a close.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// The steps under one finding, most likely and cheapest first: the first is marked "Try first". One finding's steps never
/// mix with another's. <see cref="Key"/> keeps what was tried; <see cref="HomeKey"/> is the PC health line it can hide.
/// </summary>
public sealed record TryBlock(string Key, IReadOnlyList<TryStep> Steps)
{
    public string? HomeKey { get; init; }
    /// <summary>The finding in words, sent with the steps by "Ask about this".</summary>
    public string Subject { get; init; } = "";
    public string AskPage { get; init; } = "";
}

/// <summary>
/// The one table of steps, per kind of finding, built from the advice the app already gives (the crash rules, Latency's
/// tips, the insight lines). Each page asks here, so the same finding gets the same steps everywhere.
/// </summary>
public static class WhatToTry
{
    /// <summary>Blue screen codes that point at memory (MEMORY_MANAGEMENT, PAGE_FAULT_IN_NONPAGED_AREA, PFN_LIST_CORRUPT).</summary>
    private static readonly HashSet<string> MemoryCodes = new(StringComparer.OrdinalIgnoreCase) { "0x1a", "0x50", "0x4e" };
    private static readonly HashSet<string> HardwareCodes = new(StringComparer.OrdinalIgnoreCase) { "0x124", "0x101" };
    private static readonly HashSet<string> GpuCodes = new(StringComparer.OrdinalIgnoreCase) { "0x116", "0x119" };
    private static readonly HashSet<string> DriveCodes = new(StringComparer.OrdinalIgnoreCase) { "0x7a", "0x154", "0xef" };

    private static TryStep Screen(string id, string text, string key, string? why = null)
    {
        var screen = AiActions.Screen(key);
        return new(id, text) { Why = why, Tag = TryTag.Windows, Do = screen is null ? TryDo.None : TryDo.Windows, Target = key, Button = screen?.Label };
    }

    private static TryStep Page(string id, string text, string page, string button, string? why = null) =>
        new(id, text) { Why = why, Do = TryDo.Page, Target = page, Button = button };

    private static TryStep MemoryTest(string id) => new(id, Lang.T("Test the memory with Windows Memory Diagnostic."))
    { Tag = TryTag.Restart, Do = TryDo.MemoryTest, Button = Lang.Key("Test the memory") };

    private static TryStep UndoTuning(string id) => new(id, Lang.T("Undo any undervolt or memory tuning (Curve Optimizer, XMP or EXPO) for a few days and see whether it stops."))
    { Tag = TryTag.Bios, CantSee = true };

    private static TryStep CleanGraphicsDriver(string id) => Page(id, Lang.T("Try the next graphics driver, or the one before, with a clean install."), "timeline",
        Lang.Key("Open Timeline"), Lang.T("The Timeline shows each driver you had, and what changed with it."));

    private static TryStep UndoGpuOverclock(string id) => new(id, Lang.T("Undo any GPU overclock or undervolt while you test.")) { CantSee = true };

    /// <summary>
    /// An opened crash's steps, by what kind it was and its cause; null where the one line it has says enough (an app's own
    /// fault, a driver install, a shutdown already under way).
    /// </summary>
    /// <param name="steamId">The crashed game's Steam app id, where it's a Steam game.</param>
    public static TryBlock? ForCrash(CrashEvent e, CrashFinding f, string? steamId = null, bool isGame = false)
    {
        string subject = f.Told();
        TryBlock Block(string key, params TryStep[] steps) => new(key, steps) { Subject = subject, AskPage = AiPages.Crashes, HomeKey = HomeKeyOf(e, f) };
        switch (e.Kind)
        {
            case CrashKind.UnexpectedShutdown when e.Moment != PowerMoment.Running:
                return null;
            case CrashKind.UnexpectedShutdown:
                switch (f.Cause)
                {
                    case "HEAT":
                        return Block("shutoff-heat",
                            Page("t1", Lang.T("Check the fans spin and their curves weren't changed."), "fans", Lang.Key("Open Fans"), Lang.T("The Fans page shows each fan's speed against its own usual.")),
                            new("t2", Lang.T("Clean the dust out of the coolers and the case's fans and filters.")) { Tag = TryTag.PcOff, CantSee = true },
                            Page("t3", Lang.T("Watch the temperatures the next time it's under the same load."), "temperatures", Lang.Key("Open Temperatures")));
                    case "A HARDWARE FAULT":
                        return Block("shutoff-hardware", UndoTuning("h1"),
                            new("h2", Lang.T("Look for a newer BIOS from your board's or PC's maker.")) { Tag = TryTag.Bios, CantSee = true },
                            Page("h3", Lang.T("Check the temperatures just before it happened."), "temperatures", Lang.Key("Open Temperatures")));
                    case "THE GPU HUNG":
                        return Block("shutoff-gpu", CleanGraphicsDriver("g1"), UndoGpuOverclock("g2"),
                            new("g3", Lang.T("Check the GPU's power cables are pushed fully in.")) { Tag = TryTag.PcOff, CantSee = true });
                    case "THE DRIVER INSTALL":
                        return null;
                    default:
                        // A power cut, a dip, the mains or the power supply, or not enough to say: the power first.
                        return Block("shutoff-power",
                            new TryStep("p1", Lang.T("If other things in the room lost power too, it was the mains, and a battery backup (UPS) stops these."))
                            { Why = Lang.T("Plug the PC straight into the wall, not through a power strip, and see whether it happens again."), CantSee = true },
                            new("p2", Lang.T("If nothing else lost power, check your power supply's wattage against what the PC draws.")) { Why = Lang.T("The wattage is on the power supply's label."), Tag = TryTag.PcOff, CantSee = true },
                            UndoTuning("p3"));
                }
            case CrashKind.SystemCrash:
                string code = (e.Code ?? "").Trim().ToLowerInvariant();
                if (f.Cause == "A HARDWARE FAULT" || HardwareCodes.Contains(code))
                    return Block("bsod-hardware", UndoTuning("h1"),
                        new("h2", Lang.T("Look for a newer BIOS from your board's or PC's maker.")) { Tag = TryTag.Bios, CantSee = true },
                        Page("h3", Lang.T("Check the temperatures just before it happened."), "temperatures", Lang.Key("Open Temperatures")));
                if (MemoryCodes.Contains(code))
                    return Block("bsod-memory", MemoryTest("m1"),
                        new("m2", Lang.T("Turn off XMP or EXPO for a few days and see whether it happens again.")) { Tag = TryTag.Bios, CantSee = true },
                        new("m3", Lang.T("If it keeps happening, test one memory stick at a time.")) { Tag = TryTag.PcOff, CantSee = true });
                if (GpuCodes.Contains(code))
                    return Block("bsod-gpu", CleanGraphicsDriver("g1"), UndoGpuOverclock("g2"));
                if (DriveCodes.Contains(code))
                    return Block("bsod-drive",
                        Page("d1", Lang.T("Check the drive's health."), "storage", Lang.Key("Open Storage")),
                        new("d2", Lang.T("Check the drive's cable, or that it's seated firmly.")) { Tag = TryTag.PcOff, CantSee = true },
                        new("d3", Lang.T("Look for a firmware update in your SSD maker's app.")) { CantSee = true });
                if (e.Module is null)
                    return Block("bsod-drivers",
                        Screen("u1", Lang.T("Install Windows' updates, optional driver updates included."), "optional-updates"),
                        MemoryTest("u2") with { Why = Lang.T("If it keeps happening, a memory test rules the memory in or out.") });
                return null;
            case CrashKind.GpuDriverReset:
                return Block("gpu-reset", isGame
                    ? [new TryStep("r0", Lang.T("Lower the game's graphics settings or limit its frame rate, and see whether it happens again.")) { CantSee = true }, CleanGraphicsDriver("r1"), UndoGpuOverclock("r2")]
                    : [CleanGraphicsDriver("r1"), UndoGpuOverclock("r2"), Page("r3", Lang.T("Check the GPU's temperatures just before it happened."), "temperatures", Lang.Key("Open Temperatures"))]);
            case CrashKind.AppCrash when isGame && steamId is not null && f.Cause is "AT START-UP" or "A FAULT IN THE APP" or null:
                return Block("game-crash|" + steamId, VerifyGame("v1", steamId),
                    new("v2", Lang.T("Update the game, and try it without mods or add-ons.")) { CantSee = true },
                    CleanGraphicsDriver("v3"));
            default:
                return null;
        }
    }

    /// <summary>The PC health line a crash's finding stands behind on Home, where there is one.</summary>
    private static string? HomeKeyOf(CrashEvent e, CrashFinding f) => e.Kind switch
    {
        CrashKind.UnexpectedShutdown when f.Outside || f.Cause is "A POWER CUT" or "MAINS OR POWER SUPPLY" or "A POWER DIP OR A RESET" => "usual-power",
        CrashKind.UnexpectedShutdown or CrashKind.SystemCrash or CrashKind.GpuDriverReset => "usual-problems",
        _ => null,
    };

    private static TryStep VerifyGame(string id, string steamId) => new(id, Lang.T("Verify the game's files."))
    { Why = Lang.T("Steam checks every file and replaces any that are damaged."), Tag = TryTag.Steam, Do = TryDo.VerifyGame, Target = steamId, Button = Lang.Key("Verify in Steam") };

    /// <summary>A game that crashes more than it usually does, on its own page.</summary>
    public static TryBlock GameCrashes(string gameKey, string? steamId, string subject)
    {
        var steps = new List<TryStep>();
        if (steamId is not null) steps.Add(VerifyGame("v1", steamId));
        else steps.Add(new("v1", Lang.T("Verify or repair the game's files in its launcher.")) { CantSee = true });
        steps.Add(new("v2", Lang.T("Update the game, and try it without mods or add-ons.")) { CantSee = true });
        steps.Add(CleanGraphicsDriver("v3"));
        steps.Add(UndoGpuOverclock("v4"));
        return new("game-crash|" + gameKey, steps) { Subject = subject, AskPage = AiPages.Games };
    }

    /// <summary>A part running hotter at the same load than it used to (Temperatures' "Over the months").</summary>
    public static TryBlock Heat(bool gpu, string subject) => new(gpu ? "heat-gpu" : "heat-cpu", gpu
        ?
        [
            new TryStep("h1", Lang.T("Clean the dust out of the GPU's cooler and the case's fans and filters.")) { Tag = TryTag.PcOff, CantSee = true },
            Page("h2", Lang.T("Check the GPU's fans spin and its fan curve wasn't changed."), "fans", Lang.Key("Open Fans"), Lang.T("The Fans page shows its speed against its own usual.")),
            new("h3", Lang.T("Have the GPU's thermal paste and pads replaced by a repair shop.")) { Tag = TryTag.PcOff, CantSee = true },
        ]
        :
        [
            new TryStep("h1", Lang.T("Clean the dust out of the CPU cooler and the case's fans and filters.")) { Tag = TryTag.PcOff, CantSee = true },
            Page("h2", Lang.T("Check the CPU fan spins and its fan curve wasn't changed."), "fans", Lang.Key("Open Fans"), Lang.T("The Fans page shows its speed against its own usual.")),
            new("h3", Lang.T("Replace the CPU's thermal paste, or have a repair shop do it.")) { Tag = TryTag.PcOff, CantSee = true },
        ]) { HomeKey = "wear-cooling", Subject = subject, AskPage = AiPages.Temperatures };

    /// <summary>A fan stopped or spinning slower than it did.</summary>
    public static TryBlock Fan(string fanKey, string homeKey, string subject) => new("fan|" + fanKey,
    [
        new TryStep("f1", Lang.T("Check nothing stops the fan turning, and clean the dust off it.")) { Tag = TryTag.PcOff, CantSee = true },
        new("f2", Lang.T("If you changed its fan curve in the BIOS or a fan app, check it.")) { CantSee = true },
        new("f3", Lang.T("Check its cable is pushed fully into the board or fan hub.")) { Tag = TryTag.PcOff, CantSee = true },
    ]) { HomeKey = homeKey, Subject = subject, AskPage = AiPages.Fans };

    /// <summary>A drive nearly full (90% or more).</summary>
    public static TryBlock DriveFull(string root, string? binSize, string subject)
    {
        var steps = new List<TryStep>();
        steps.Add(new("d1", Lang.T("Empty the Recycle Bin.")) { Why = binSize is null ? null : Lang.T($"It holds {binSize}."), Do = TryDo.EmptyBin, Button = Lang.Key("Empty it") });
        steps.Add(Screen("d2", Lang.T("Let Storage Sense clear temporary files."), "storage-sense"));
        steps.Add(Screen("d3", Lang.T("Uninstall apps and games you don't use."), "installed-apps"));
        return new("drive-full|" + root.TrimEnd('\\', ':').ToUpperInvariant(), steps) { Subject = subject, AskPage = AiPages.Storage };
    }

    /// <summary>Memory nearly full at its fullest, with the app holding the most where it's still running.</summary>
    public static TryBlock MemoryFull(string? app, string? exe, string? held, string subject)
    {
        var steps = new List<TryStep>();
        if (app is not null && exe is not null)
            steps.Add(new("r1", Lang.T($"Close {app} if you don't need it open.")) { Why = held is null ? null : Lang.T($"It holds {held} right now."), Do = TryDo.CloseApp, Target = exe, Name = app, Button = Lang.Key("Close") });
        steps.Add(Screen("r2", Lang.T("Stop apps you don't need from starting with Windows."), "startup-apps"));
        steps.Add(new("r3", Lang.T("If it fills up often, more memory would help.")) { CantSee = true });
        return new("memory-full", steps) { Subject = subject, AskPage = AiPages.Memory };
    }

    /// <summary>
    /// A start that took longer than its usual (the Start-up page, 1.22.0): the startup apps first, then an update for each
    /// app Windows itself named as slower to start (at most two). Null for any other start: nothing to try.
    /// </summary>
    public static TryBlock? ForStart(Core.Startup.StartView start, string subject)
    {
        if (!start.Longer) return null;
        var steps = new List<TryStep> { Screen("s1", Core.Startup.StartupWords.TryStartupApps, "startup-apps") };
        // The apps that took longer than their own usual, then those Windows itself named: two at most, each once.
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in start.LongerApps.Select(a => a.Name).Where(named.Add).Take(2))
            steps.Add(new("s" + (steps.Count + 1), Core.Startup.StartupWords.TryAppLonger(app)) { CantSee = true });
        foreach (var app in start.Record.Slow.Where(s => s.Event == 101 && s.Name.Length > 0).Select(s => s.Name).Where(named.Add).Take(3 - steps.Count))
            steps.Add(new("s" + (steps.Count + 1), Core.Startup.StartupWords.TryAppUpdate(app)) { CantSee = true });
        return new("startup|" + start.Time.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), steps) { Subject = subject, AskPage = AiPages.Home };
    }

    /// <summary>The internet dropping more than it usually does.</summary>
    public static TryBlock NetDrops(string subject) => new("net-drops",
    [
        new TryStep("n1", Lang.T("Restart your router and see whether the drops stop.")) { CantSee = true },
        new("n2", Lang.T("On Wi-Fi, try a cable for a day to see whether the drops follow the Wi-Fi.")) { CantSee = true },
        Screen("n3", Lang.T("See what Windows says about the connection."), "network-status"),
    ]) { Subject = subject, AskPage = AiPages.Network };

    /// <summary>
    /// A driver's delays on Latency (and the sound cut-outs they may cause): Latency's own tips, with a button where one of
    /// them is a screen RigSight can open. Null where the kind of driver has none.
    /// </summary>
    public static TryBlock? Latency(DriverArea area, string driverKey, string subject)
    {
        var tips = DriverWords.Tips(area);
        if (tips.Count == 0) return null;
        var steps = tips.Select((t, i) =>
        {
            var step = new TryStep("l" + (i + 1), t.Step) { Why = t.More, CantSee = true };
            return (area, i) switch
            {
                (DriverArea.Network or DriverArea.Sound or DriverArea.Device or DriverArea.Bluetooth, 0) when !t.Step.Contains("Bluetooth off", StringComparison.Ordinal) =>
                    step with { Tag = TryTag.Windows, Do = TryDo.Windows, Target = "device-manager", Button = AiActions.Screen("device-manager")?.Label, CantSee = false },
                (DriverArea.Graphics, 0) => step with { Do = TryDo.Page, Target = "timeline", Button = Lang.Key("Open Timeline"), CantSee = false },
                (DriverArea.Storage, 1) => step with { Do = TryDo.Page, Target = "storage", Button = Lang.Key("Open Storage"), CantSee = false },
                (DriverArea.Power, 1) => step with { Tag = TryTag.Windows, Do = TryDo.Windows, Target = "power-mode", Button = AiActions.Screen("power-mode")?.Label, CantSee = false },
                _ => step,
            };
        }).ToList();
        return new("latency|" + driverKey, steps) { Subject = subject, AskPage = AiPages.Home };
    }

    /// <summary>"Tried 26 Nov".</summary>
    public static string TriedText(DateTime day) => Lang.T($"Tried {day:d MMM}");

    /// <summary>What RigSight saw since a step was tried, from the finding's own times: "Not seen since you tried a step on 26 Nov", "Happened 2 more times since…".</summary>
    public static string Since(DateTime tried, int timesSince) => timesSince switch
    {
        0 => Lang.T($"Not seen since you tried a step on {tried:d MMM}"),
        1 => Lang.T($"Happened once more since you tried a step on {tried:d MMM}"),
        _ => Lang.T($"Happened {timesSince} more times since you tried a step on {tried:d MMM}"),
    };
}

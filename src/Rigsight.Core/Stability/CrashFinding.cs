using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Stability;

/// <summary>
/// How sure a finding is, by what it rests on: Windows itself recorded the cause; several separate things point the
/// same way and nothing points against; it fits, but so does something else; or there is too little to say.
/// </summary>
public enum Confidence { Named, Likely, Possible, NotEnough }

/// <summary>
/// One thing a finding rests on, or one thing that was looked at and found not to be the cause. <see cref="Kind"/>
/// picks the icon: "power", "pc", "clock", "pattern", "info", "warn", "change", "ok" (ruled out), "unknown" (couldn't be checked).
/// </summary>
public sealed record Evidence(string Kind, string Title, string Detail);

/// <summary>
/// What most likely happened in a crash, in one sentence with how sure that is, what it rests on, what was ruled out,
/// and what can't be told. Every page that shows a crash shows this, so they all say the same.
/// </summary>
public sealed record CrashFinding(Confidence Confidence, string Head, string Text, string Advice, string Line)
{
    /// <summary>After the confidence word on the chip, where a few words say the cause ("A POWER CUT").</summary>
    public string? Cause { get; init; }

    private readonly string? _short;

    /// <summary>
    /// What happened in a few words with no full stop, to carry a time after it ("PC lost power while idle"):
    /// <see cref="Line"/> where that is already one short sentence.
    /// </summary>
    public string Short { get => _short ?? Line.TrimEnd('.'); init => _short = value; }
    public List<Evidence> Points { get; init; } = [];
    /// <summary>What was looked at and found not to be the cause (each with the reading that says so).</summary>
    public List<Evidence> RuledOut { get; init; } = [];
    /// <summary>What would have been looked at but wasn't recorded: said, so that nothing unread passes for ruled out.</summary>
    public List<Evidence> NotChecked { get; init; } = [];
    public string? CantTell { get; init; }

    /// <summary>
    /// The cause was most likely outside the PC or outside the thing that crashed (the mains, a shutdown already under
    /// way, a driver being installed): not a fault to count against the PC, a driver or the app.
    /// </summary>
    public bool Outside { get; init; }

    /// <summary>"LIKELY A POWER CUT", "POSSIBLE: MAINS OR POWER SUPPLY", "CONFIRMED", "NOT ENOUGH TO SAY".</summary>
    public string Chip => Confidence switch
    {
        Confidence.Named => "CONFIRMED",
        Confidence.Likely => Cause is null ? "LIKELY" : $"LIKELY {Cause}",
        Confidence.Possible => Cause is null ? "POSSIBLE" : $"POSSIBLE: {Cause}",
        _ => "NOT ENOUGH TO SAY",
    };

    /// <summary>The confidence as a sentence, for a line of text with no chip: "Likely a power cut.", "Confirmed by Windows.", "Not enough to say why."</summary>
    public string Verdict => Confidence switch
    {
        Confidence.Named => "Confirmed by Windows.",
        Confidence.Likely => Cause is null ? "Likely." : $"Likely {Lowered(Cause)}.",
        Confidence.Possible => Cause is null ? "Possible, not certain." : $"Possible: {Lowered(Cause)}.",
        _ => "Not enough to say why.",
    };

    /// <summary>A chip's capitals as running text; "PC" stays "PC".</summary>
    private static string Lowered(string cause) => cause.ToLowerInvariant().Replace(" pc", " PC");

    /// <summary>What happened and how sure that is, in one line: for a recap, a notification, a row.</summary>
    public string Said => $"{Line} {Verdict}";
}

/// <summary>Everything a finding is worked out from. Only the crash itself is always there.</summary>
public sealed record CrashInput(CrashEvent Crash, CrashExplanation Explanation, AlertSettings Alerts)
{
    public string? AppName { get; init; }
    public bool IsGame { get; init; }
    public CrashContext? Context { get; init; }
    public string? FrontApp { get; init; }
    /// <summary>This PC's temperatures at rest over the two weeks before.</summary>
    public LoadTemps? Rest { get; init; }
    /// <summary>Installed memory in GB, where known.</summary>
    public double? RamTotal { get; init; }
    /// <summary>The same problem's earlier times, oldest first (see <see cref="CrashFindings.KeyOf"/>).</summary>
    public IReadOnlyList<CrashEvent> Earlier { get; init; } = [];
    /// <summary>What changed on the PC in the week before, oldest first; null when no record of changes reaches back that far.</summary>
    public IReadOnlyList<SystemChange>? Changes { get; init; }
    /// <summary>Trouble Windows logged from half an hour before to a day after.</summary>
    public IReadOnlyList<CrashSign> Signs { get; init; } = [];
    /// <summary>The graphics card's memory as the app crashed, against that app's usual (worked out for an opened crash only).</summary>
    public GpuMemoryAtCrash? GpuMemory { get; init; }
    /// <summary>A program's name as the app shows it, from its file name.</summary>
    public Func<string, string> NameOf { get; init; } = exe => Path.GetFileNameWithoutExtension(exe);
}

/// <summary>
/// The rules. Each is a fixed test on what was recorded, so the same crash always gets the same finding and each can
/// be tried on recorded cases. A rule that needs something that wasn't recorded doesn't fire: the finding says less.
/// </summary>
public static class CrashFindings
{
    /// <summary>The title of the line that says a part was over its alert limit (the Crashes page counts these).</summary>
    public const string HotTitle = "It was running hot";

    /// <summary>How many times, as a person says it: "once", "twice", "17 times".</summary>
    public static string Times(int n) => n switch { 1 => "once", 2 => "twice", _ => $"{n} times" };

    /// <summary>"4m" said the way a person says it: "4 minutes", "an hour and 5 minutes".</summary>
    public static string Words(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        static string Of(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
        if (t.TotalMinutes < 1) return Of((int)t.TotalSeconds, "second");
        if (t.TotalHours < 1) return Of((int)t.TotalMinutes, "minute");
        return t.Minutes == 0 ? Of((int)t.TotalHours, "hour") : $"{Of((int)t.TotalHours, "hour")} and {Of(t.Minutes, "minute")}";
    }

    /// <summary>"CPU 59°, GPU 65°" as the start of a sentence: "The CPU was at 59° and the GPU at 65°".</summary>
    private static string Sentence(string readings)
    {
        var parts = readings.Split(", ").Select(p => p.Split(' ', 2)).ToList();
        return parts.Count == 2 ? $"The {parts[0][0]} was at {parts[0][1]} and the {parts[1][0]} at {parts[1][1]}" : $"The {parts[0][0]} was at {parts[0][1]}";
    }

    /// <summary>Loads under this (percent, over the ten minutes before) are a PC doing little.</summary>
    public const double LightLoad = 30;
    /// <summary>A program that fails within this many seconds of being started failed as it was starting.</summary>
    internal const double StartUpSec = 60;
    private const double NearlyFull = 0.92;
    /// <summary>A PC running again within this many minutes of going off came straight back: a dip or a reset, not a cut.</summary>
    private const int QuickBack = 2;

    /// <summary>What makes two crashes "the same problem": the same app failing in the same file, the same blue screen code, the same kind of shut-off.</summary>
    public static string KeyOf(CrashEvent e) => e.Kind switch
    {
        CrashKind.SystemCrash => $"bsod|{e.Code}",
        CrashKind.UnexpectedShutdown => $"power|{e.Moment}",
        CrashKind.GpuDriverReset => "gpu",
        _ => $"{e.Kind}|{e.AppExe.ToLowerInvariant()}|{e.Module?.ToLowerInvariant()}",
    };

    public static CrashFinding Judge(CrashInput x) => Tidy(UpClose(x, x.Crash.Kind switch
    {
        CrashKind.UnexpectedShutdown => ShutOff(x),
        CrashKind.SystemCrash => BlueScreen(x),
        CrashKind.GpuDriverReset => DriverReset(x),
        CrashKind.AppHang => Hang(x),
        _ => AppCrash(x),
    }));

    /// <summary>
    /// What only an opened crash looks at adds to what the finding rests on, never to what it says: the graphics card's
    /// memory against the app's usual.
    /// The headline, the confidence and the advice are the same as on every list, which work without them.
    /// </summary>
    private static CrashFinding UpClose(CrashInput x, CrashFinding f)
    {
        var points = f.Points.ToList();
        var ruled = f.RuledOut.ToList();
        if (x.GpuMemory?.Point(x.AppName ?? x.NameOf(x.Crash.AppExe)) is { } memory) points.Add(memory);
        return points.Count == f.Points.Count && ruled.Count == f.RuledOut.Count ? f : f with { Points = points, RuledOut = ruled };
    }

    /// <summary>
    /// Every finding leaves here with its evidence in the right list, whatever rule made it: only what a reading
    /// cleared is "ruled out", what wasn't recorded is "not checked", and a warning never sits among the cleared.
    /// </summary>
    private static CrashFinding Tidy(CrashFinding f) => f with
    {
        Points = [.. f.Points, .. f.RuledOut.Where(e => e.Kind is not ("ok" or "unknown") && !f.Points.Contains(e))],
        RuledOut = [.. f.RuledOut.Where(e => e.Kind == "ok")],
        NotChecked = [.. f.NotChecked, .. f.RuledOut.Where(e => e.Kind == "unknown")],
    };

    // ── The PC went off ─────────────────────────────────────────────────

    private static CrashFinding ShutOff(CrashInput x)
    {
        var (e, f, c, ex) = (x.Crash, x.Crash.Facts, x.Context, x.Explanation);
        if (e.Moment == PowerMoment.ShuttingDown)
            return new(Confidence.Named, "Windows was already shutting down when the PC went off.", "It went off before Windows could record a clean finish. No crash report was saved.", ex.Advice,
                "Windows was already shutting down. Usually harmless.") { Short = "PC went off while it was shutting down", Outside = true, Points = [new("info", "Windows was already shutting down", "Its record from the next start says it was shutting down, restarting or hibernate was under way.")] };
        if (e.Moment == PowerMoment.Asleep)
            return new(Confidence.Named, "The PC lost power while it was asleep.", "It was asleep or waking up when it went down, and no crash report was saved.", ex.Advice,
                "The PC was asleep when the power went.") { Short = "PC lost power while asleep", Outside = true, Points = [new("info", "The PC was asleep", "Windows' record from the next start says so.")] };

        bool has = c is { Minutes: > 0 };
        var heat = Heat(x);
        // Doing little only where how busy it was is on record: a load that wasn't read is not a low one.
        bool light = has && (c!.GpuLoad is not null || c.CpuLoad is not null) && (c.GpuLoad ?? 0) < LightLoad && (c.CpuLoad ?? 0) < LightLoad;
        // How long nobody had been at it: to the minute where the last use is on record, else only "for some time"
        // (no use in the four hours looked at, of which fewer may have been recorded: no figure is made up).
        int? idle = has && c!.LastActive is long last ? (int)Math.Max(0, (e.Ts - last - 60) / 60) : has ? int.MaxValue : null;
        string idleFor = idle == int.MaxValue ? "some time" : Minutes(idle ?? 0);
        int? backOn = c?.BackOn is long on && on - e.Ts <= 3 * 3600 ? (int)Math.Max(1, (on - e.Ts) / 60) : null;
        string where = x.FrontApp is null ? "" : $" in {x.FrontApp}";
        string back = backOn is int b ? $" Back on {Minutes(b)} later." : "";
        // What Windows itself noted about the power button; null where it noted nothing either way.
        bool noPress = f?.PowerButton == false;
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        const string cant = "A power cut and a failing power supply look the same from inside the PC. A press of the reset button isn't recorded, and a processor or memory setting that is unstable can switch a PC off the same way.";

        // Trouble Windows logged just before, memory nearly full, a driver that this began with: any of them, and the
        // mains is no longer the likely answer.
        var signs = Signs(x).ToList();
        var change = Change(x, anyDriver: true);
        var memory = Memory(x);
        bool trouble = signs.Count > 0 || memory is { Kind: "warn" } || change is { Kind: "change" };
        // Only an error the hardware could not correct: corrected ones are logged all day on some healthy PCs.
        bool hardware = f?.HardwareErrors > 0 || x.Signs.Any(s => s.Kind == SignKind.Hardware && s.Detail.Contains("couldn't correct", StringComparison.Ordinal) && Near(s, e, f));
        var hang = f?.NextStart is long start ? x.Signs.FirstOrDefault(s => s.Kind == SignKind.GraphicsHang && s.Ts >= start - 60 && s.Ts <= start + 900) : null;
        // The moment it hung, where Windows' report could be read: just before the PC went down, no more than two minutes before
        // the last whole minute it was recorded running ended (the shut-off is given that time): a hang earlier than that it recovered from.
        // And the PC was starting again within ten minutes of it: Windows' own time for a shut-off can be hours stale.
        var hungAt = f?.NextStart is long restart
            ? x.Signs.Where(s => s.Kind == SignKind.GraphicsHangAt && s.Ts >= e.Ts - 180 && s.Ts <= restart && restart - s.Ts <= 600).MaxBy(s => s.Ts)
            : null;

        if (f?.PowerButton == true) points.Add(new("power", "Someone held the power button", "Windows noted it when the PC next started."));
        else if (noPress) points.Add(new("power", "Nobody held the power button", "Windows notes it when the PC next starts if someone does, and it noted nothing."));
        if (has)
        {
            string loads = Loads(c!);
            if (light && idle >= 5) points.Add(new("pc", "Nobody was using it", $"{(idle == int.MaxValue ? "No use on record in the hours before." : $"Idle since {TimeUtil.FromUnix(e.Ts - idle.Value * 60L):h:mm tt}.")}{loads}"));
            else if (light) points.Add(new("pc", "It wasn't doing much", $"{(x.FrontApp is null ? "" : x.FrontApp + " was in front.")}{loads}".Trim()));
            else if (loads.Length > 0) points.Add(new("pc", "It was working hard", $"{(x.FrontApp is null ? "" : x.FrontApp + " was in front.")}{loads}{(c!.GpuPower is double w ? $" The graphics card was drawing {w:0} W." : "")}".Trim()));
        }
        if (backOn is int n) points.Add(new("clock", $"It came back on at {TimeUtil.FromUnix(c!.BackOn!.Value):h:mm tt}", $"{Capital(Minutes(n))} later."));
        if (Before(x) is { } before) points.Add(before);
        points.AddRange(signs);
        if (heat.Hot) points.Add(new("warn", HotTitle, heat.Evidence.Detail)); else ruled.Add(heat.Evidence);
        if (f is { HardwareErrors: 0 } && !hardware) ruled.Add(new("ok", "It wasn't a blue screen or a hardware fault", "Windows saved no crash report and logged no hardware error it couldn't fix."));
        Sort(change, points, ruled);
        Sort(memory, points, ruled);

        if (f?.PowerButton == true)
            return new(Confidence.Named, "It was switched off by holding the power button.", "Windows recorded a long press of the power button at the start after. That is usually done when the PC has frozen.",
                "If it had frozen, look at what was running just before, below. One freeze is not a pattern; several with the same app in front are.",
                $"Switched off by holding the power button{(x.FrontApp is null ? "" : $", with {x.FrontApp} in front")}.")
            { Short = "PC was switched off by its power button", Points = points, RuledOut = ruled, CantTell = "Why it had frozen, if it had." };
        if (hardware)
            return new(Confidence.Likely, "The hardware reported an error.", "Windows logged a hardware error it couldn't correct around the time the PC went down. That points at the processor, the memory or the motherboard, or at tuning that is too tight for them.",
                "Undo any overclock or undervolt (Curve Optimizer, XMP or EXPO included), update the BIOS, and check the temperatures.",
                "Windows logged a hardware error around it.")
            { Short = "PC went down with a hardware error", Cause = "A HARDWARE FAULT", Points = [new("warn", "Windows logged a hardware error", f?.HardwareErrors > 0 ? $"{f.HardwareErrors} handed over by the firmware at the next start." : "In the minutes around it."), .. points], RuledOut = ruled, CantTell = "Which part reported it." };
        if (hungAt is not null)
        {
            var at = TimeUtil.FromUnix(hungAt.Ts);
            string starting = f?.NextStart is long next && next > hungAt.Ts ? $" The PC was starting again at {TimeUtil.FromUnix(next):h:mm:ss tt}." : "";
            return new(Confidence.Likely, "The graphics card hung, and the PC went down with it.", $"Windows recorded the graphics card hanging at {at:h:mm:ss tt}, while the PC was still running.{starting} A card that stops answering can take the whole PC down.",
                "Update or clean-reinstall the graphics driver and undo any overclock of the card. If it happens again, check the card's power cables.",
                $"The graphics card hung at {at:h:mm:ss tt}{where}, then the PC went down.{(backOn is int b2 ? $" Back on {Minutes(b2)} later." : "")}")
            {
                Short = "PC went down after the graphics card hung", Cause = "THE GRAPHICS CARD HUNG",
                Points = [new("warn", $"Windows recorded the graphics card hanging at {at:h:mm:ss tt}", "Its report gives the moment of the hang, before the PC went down."), .. points],
                RuledOut = ruled, CantTell = "Why the card hung: its driver, heat, power, or the card itself.",
            };
        }
        if (hang is not null)
            return new(Confidence.Possible, "The graphics card may have stopped answering.", $"Windows filed a graphics-hang report at the start after, {hang.Time:h:mm tt}. A screen that freezes or goes black, and a PC that then has to be switched off, fit that.",
                "Update or clean-reinstall the graphics driver and undo any overclock of the card.", "Windows filed a graphics-hang report at the next start.")
            { Short = "PC went down, and Windows filed a graphics-hang report", Cause = "THE GRAPHICS CARD HUNG", Points = [new("warn", "Windows reported that the graphics card hung", $"It filed the report at {hang.Time:h:mm tt}, when the PC started again."), .. points], RuledOut = ruled, CantTell = "When the card hung: the report carries the time it was filed, not the time of the hang." };
        if (f is null && !has)
            return new(Confidence.NotEnough, "Not enough on record to say why.", ex.Reason, ex.Advice, "No crash report was saved, and nothing was being recorded.") { Short = "PC shut off unexpectedly", Points = points, RuledOut = ruled, CantTell = cant };
        if (heat.Hot)
            return new(Confidence.Possible, "It was running hot when it went off.", $"{heat.Evidence.Detail} A PC that overheats switches itself off to protect its parts; a power cut at the same moment would look the same.",
                "Check the fans and airflow, and the temperatures on the Temperatures page for the minutes before.", $"Running hot{where} when it went off: {heat.Short}.{back}")
            { Short = "PC went off while running hot", Cause = "HEAT", Points = points, RuledOut = ruled, CantTell = cant };
        if (!has)
            return new(Confidence.Possible, "The power went, and nothing was being recorded.", $"{(noPress ? "Windows recorded no press of the power button and saved no crash" : "Windows saved no crash")}, so the PC lost power or was reset. What it was doing isn't on record.",
                ex.Advice, $"No crash saved. Nothing was being recorded.{back}")
            { Short = "PC lost power", Cause = "POWER LOST", Points = points, RuledOut = ruled, CantTell = cant };
        if (light && noPress && backOn is <= QuickBack)
            // Running again within a minute or two: the power dipped for a moment, or the PC reset itself.
            return new(Confidence.Possible, $"It went off and was running again {Minutes(backOn.Value)} later.",
                "Windows saved no crash and recorded no press of the power button. A PC that is back that fast either lost power for a moment, or reset itself, which a processor or memory setting that is unstable at idle can do.",
                "If the lights or other devices flickered too, it was the mains. If it repeats with nothing else affected, undo any undervolt or memory tuning (Curve Optimizer, XMP or EXPO) and see whether it stops.",
                $"{(idle >= 5 ? $"Idle for {idleFor}" : x.FrontApp is null ? "Doing little" : $"In {x.FrontApp}")}. Running again {Minutes(backOn.Value)} later.")
            { Short = "PC went off and came straight back", Cause = "A POWER DIP OR A RESET", Points = points, RuledOut = ruled, CantTell = cant };
        if (light && noPress && trouble)
            return new(Confidence.Possible, "The power went while the PC was doing little.", "Windows saved no crash and recorded no press of the power button, but there was trouble on record just before it (above), so this one isn't put down to the mains.",
                ex.Advice, $"{(idle >= 5 ? $"Idle for {idleFor}" : x.FrontApp is null ? "Doing little" : $"In {x.FrontApp}")}, with trouble logged just before.{back}")
            { Short = "PC lost power while doing little", Cause = "POWER LOST", Points = points, RuledOut = ruled, CantTell = cant };
        if (light && noPress)
        {
            string doing = idle >= 5 ? $"had been idle for {idleFor}" : $"was doing little{where}";
            // "Cool" only where both the processor's and the graphics card's temperature were read.
            string cool = heat.Full ? " it was cool," : "";
            return new(Confidence.Likely, "Most likely a power cut.", $"The PC {doing},{cool} Windows logged no error, and it recorded no press of the power button.{(backOn is int m ? $" It was back on {Minutes(m)} later." : "")}",
                "If other things in the room lost power too, it was the mains, and a battery backup (UPS) stops these. If nothing else did, look at the PC's power cable and power supply.",
                $"{(idle >= 5 ? $"Idle{(heat.Full ? " and cool" : "")} for {idleFor}" : $"{(x.FrontApp is null ? "Doing little" : $"In {x.FrontApp}")}{(c!.GpuLoad is double g ? $", graphics card at {g:0}%" : "")}")}.{back}")
            { Short = idle >= 5 ? "PC lost power while idle" : "PC lost power while doing little", Cause = "A POWER CUT", Outside = true, Points = points, RuledOut = ruled, CantTell = cant };
        }
        if (light)
            return new(Confidence.Possible, "The power went while the PC was doing little.", "No crash report was saved. Whether the power button was held isn't on record for this one, so a freeze that was switched off by hand can't be told from a power cut.",
                ex.Advice, $"{(x.FrontApp is null ? "Doing little" : $"In {x.FrontApp}")}, no crash saved.{back}")
            { Short = "PC lost power while doing little", Cause = "POWER LOST", Points = points, RuledOut = ruled, CantTell = cant };
        string watts = c!.GpuPower is double p ? $" with the graphics card drawing {p:0} W" : "";
        return new(Confidence.Possible, "The power went while the PC was working hard.", $"It went off{where}{watts}. {(noPress ? "Windows recorded no press of the power button and saved no crash" : "Windows saved no crash")}, which fits a power cut, and also a power supply that can't keep up.",
            "If it only ever happens under load, suspect the power supply. If it happens at idle too, suspect the mains.",
            $"{(x.FrontApp is null ? "Working hard" : $"In {x.FrontApp}")}{(c.GpuPower is double drawn ? $", graphics card drawing {drawn:0} W" : "")}.{back}")
        { Short = x.FrontApp is null ? "PC lost power while working hard" : $"PC lost power in {x.FrontApp}", Cause = "MAINS OR POWER SUPPLY", Points = points, RuledOut = ruled, CantTell = cant };
    }

    // ── Windows crashed ─────────────────────────────────────────────────

    private static CrashFinding BlueScreen(CrashInput x)
    {
        var (e, f, ex) = (x.Crash, x.Crash.Facts, x.Explanation);
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        var heat = Heat(x);
        if (heat.Hot) points.Add(new("warn", HotTitle, heat.Evidence.Detail)); else ruled.Add(heat.Evidence);
        if (f?.HardwareErrors > 0) points.Add(new("warn", "Windows logged a hardware error", $"{f.HardwareErrors} handed over by the firmware at the next start."));
        points.AddRange(Signs(x));
        if (Before(x) is { } before) points.Add(before);
        Sort(Memory(x), points, ruled);
        string code = ex.Cause is { } name && name != e.Code ? $"{name} ({e.Code})" : e.Code ?? "unknown";

        if (f?.Driver is { } file && !DriverFiles.IsWindowsCore(file))
        {
            var known = CrashExplainer.Module(file);
            // A known file by its plain name ("NVIDIA driver"); else what the file says it is.
            string what = known?.Culprit ?? f.DriverName ?? file;
            bool graphics = IsGraphicsFile(file);
            if (!graphics) ruled.Add(new("ok", "It wasn't the graphics driver", "The file Windows named isn't part of it."));
            else Sort(Change(x, anyDriver: false), points, ruled);
            return new(Confidence.Named, what == file ? $"Windows crashed inside {file}." : $"Windows crashed inside {file}, the {Plain(what)}.",
                "Windows' own record of the crash names the file it happened in.",
                known?.Advice ?? $"Update or reinstall {(what == file ? "the driver that file belongs to" : $"the {Plain(what)}")}. If the crashes began after new hardware or a new driver, that is the first thing to undo.",
                what == file ? $"Windows crashed inside {file}." : $"Windows crashed inside the {Plain(what)} ({file}).")
            {
                Short = what == file ? $"Windows crashed inside {file}" : $"Windows crashed inside the {Plain(what)}",
                Points = [new("info", $"Windows blames {file}", "It wrote this beside the crash when the PC next started."), new("info", "The blue screen's code", code), .. points],
                RuledOut = ruled,
                CantTell = x.Context is { Minutes: > 0 } ? "Whether the driver itself failed, or the hardware under it." : "What was running at the time: nothing was being recorded.",
            };
        }
        Sort(Change(x, anyDriver: true), points, ruled);
        bool hardware = e.Code is "0x124" or "0x101";
        return new(hardware ? Confidence.Likely : Confidence.Possible, ex.Reason, "Windows recorded the kind of crash but named no driver for it.", ex.Advice, ex.Reason)
        {
            Short = "Windows crashed (blue screen)", Cause = hardware ? "A HARDWARE FAULT" : null,
            Points = [new("info", "The blue screen's code", code), .. points], RuledOut = ruled,
            CantTell = "Which driver or part was at fault: Windows didn't name one.",
        };
    }

    private static CrashFinding DriverReset(CrashInput x)
    {
        var ex = x.Explanation;
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        var heat = Heat(x);
        if (heat.Hot) points.Add(new("warn", HotTitle, heat.Evidence.Detail)); else ruled.Add(heat.Evidence);
        points.AddRange(Signs(x));
        if (Before(x) is { } before) points.Add(before);
        Sort(Change(x, anyDriver: false), points, ruled);
        var f = x.Crash.Facts;
        // Read from Windows' hang report (most PCs), or from its older display entry (event 4101).
        var said = f?.HangKey is not null
            ? new Evidence("info", "Windows reported that the graphics card stopped answering", f.Driver is { } driver
                ? $"Its report names {driver}{(CrashExplainer.Module(driver)?.Culprit is { } known ? $", {Lower(known)}" : "")}, and Windows restarted it." : "Windows restarted the graphics driver and filed a report.")
            : new Evidence("info", "Windows restarted the graphics driver", "It recorded this at that moment.");
        string advice = x.FrontApp is null
            ? "Update or clean-reinstall the graphics driver and undo any overclock of the card. If it keeps happening, check the card's temperatures around these times."
            : $"If it keeps happening in {x.FrontApp}, lower its graphics settings or cap its frame rate first. If it happens in other games too, update or clean-reinstall the graphics driver and undo any overclock of the card.";
        return new(Confidence.Named, "The graphics card stopped answering and Windows reset its driver.", "The screen may have gone black for a moment. Windows records the reset, not why the card stopped.",
            advice, $"Windows reset the graphics driver{(x.FrontApp is null ? "" : $" while {x.FrontApp} was in front")}.")
        { Short = x.FrontApp is null ? "The graphics driver was reset" : $"The graphics driver was reset in {x.FrontApp}", Points = [said, .. points], RuledOut = ruled, CantTell = "Why the card stopped answering: its driver, heat, power, or the card itself." };
    }

    // ── One app ─────────────────────────────────────────────────────────

    private static CrashFinding Hang(CrashInput x)
    {
        var (e, f, ex) = (x.Crash, x.Crash.Facts, x.Explanation);
        string app = x.AppName ?? x.NameOf(e.AppExe);
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        if (f?.RanSec is double ran) points.Add(new("clock", ran < StartUpSec ? $"{Seconds(ran)} after it was started" : $"It had been running for {Units.Duration(ran)}", ran < StartUpSec ? "It froze as it was starting." : "So it started and ran normally first."));
        Shared(x, points, ruled);
        if (f?.WaitedOn is { } other)
        {
            string name = x.NameOf(other);
            return new(Confidence.Likely, $"{app} was stuck waiting on {name}.", "Windows' report of the freeze names the program it was waiting for.",
                $"Update {name}, or close it and see whether {app} still freezes.", $"{app} was stuck waiting on {name}.")
            {
                Points = [new("info", $"{app} was waiting on {other}", $"Windows recorded this in its report{(f.WaitedOnVersion is null ? "" : $" ({other} version {f.WaitedOnVersion})")}."), .. points],
                RuledOut = ruled, CantTell = $"Why {name} didn't answer.",
            };
        }
        var full = points.FirstOrDefault(p => p.Title.Contains("memory", StringComparison.OrdinalIgnoreCase));
        var drive = x.Signs.FirstOrDefault(s => s.Kind == SignKind.Drive && s.Ts <= e.Ts + 120 && s.Ts >= e.Ts - 600);
        if (full is not null)
            return new(Confidence.Possible, $"{app} froze with memory nearly full.", full.Detail, "Close what you aren't using, and see the Memory page for what was holding the most.", $"{app} froze with memory nearly full.")
            { Cause = "OUT OF MEMORY", Points = points, RuledOut = ruled, CantTell = "Whether low memory caused it, or only happened at the same time." };
        if (drive is not null)
            return new(Confidence.Possible, $"{app} froze while a drive was reporting errors.", $"{drive.Detail}, at {drive.Time:h:mm tt}. A program waiting on a drive that doesn't answer stops responding.",
                "Check the drive's health on the Storage page, and its cable.", $"{app} froze while a drive was reporting errors.")
            { Cause = "A DRIVE", Points = points, RuledOut = ruled, CantTell = "Whether the app was using that drive." };
        return new(Confidence.NotEnough, $"{app} stopped responding and was closed.", "Windows recorded the freeze, but not what the app was waiting for.", ex.Advice, $"{app} stopped responding. Windows didn't record what it was waiting for.")
        { Short = $"{app} stopped responding", Points = points, RuledOut = ruled, CantTell = "What it was waiting for." };
    }

    private static CrashFinding AppCrash(CrashInput x)
    {
        var (e, f, c, ex) = (x.Crash, x.Crash.Facts, x.Context, x.Explanation);
        string app = x.AppName ?? x.NameOf(e.AppExe);
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        string module = string.Equals(e.Module, "unknown", StringComparison.OrdinalIgnoreCase) ? "" : e.Module ?? "";
        bool ownCode = module.Length > 0 && module.Equals(e.AppExe, StringComparison.OrdinalIgnoreCase);
        var known = CrashExplainer.Module(module);
        // By Windows' own record of when the program was started: only that says "as it was starting" for sure.
        bool atStart = f?.RanSec is < StartUpSec;
        var twin = x.Earlier.LastOrDefault(p => e.Ts - p.Ts is >= 0 and <= 120);

        if (atStart) points.Add(new("clock", $"It crashed {Seconds(f!.RanSec!.Value)} after starting", twin is null ? "Windows' record of the crash says when the app was started." : e.Ts == twin.Ts ? "It had crashed in the same second the time before, too." : $"It had crashed {e.Ts - twin.Ts} seconds earlier too."));
        else if (f?.RanSec is double ran) points.Add(new("clock", $"It had been running fine for {Words(ran)}", "So it didn't fail while starting up."));
        else if (c?.SessionSec is double s && s >= 60) points.Add(new("clock", x.IsGame ? $"It crashed {Words(s)} into your game" : $"It crashed {Words(s)} after you started using it", "It was working fine until then."));
        if (f?.ReportType is { } type && type.StartsWith("BEX", StringComparison.OrdinalIgnoreCase))
            points.Add(new("info", "Windows stopped it for touching memory it shouldn't", $"Its report is of the kind {type}: the app ran or wrote where it isn't allowed to."));
        if (module.Length == 0) points.Add(new("unknown", "Windows couldn't tell which file it crashed in", "Its report lists it as unknown."));
        else if (f?.AppVersion is not null || f?.ModuleVersion is not null)
            points.Add(new("info", $"It crashed in {module}", string.Join(" ", new[]
            {
                f.AppVersion is null or "0.0.0.0" ? null : $"{app} was on version {f.AppVersion}.",
                ownCode || f.ModuleVersion is null or "0.0.0.0" ? null : $"{module} was on version {f.ModuleVersion}.",
            }.OfType<string>())));
        var same = f?.Offset is { } offset ? x.Earlier.Where(p => p.Facts?.Offset == offset && string.Equals(p.Module, e.Module, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        Shared(x, points, ruled);
        bool graphics = known is { } k && (k.Culprit.Contains("driver", StringComparison.OrdinalIgnoreCase) || k.Culprit is "DirectX" or "Vulkan");
        if (graphics || e.AppExe.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase)) Sort(Change(x, anyDriver: false), points, ruled);
        string what = module.Length == 0 ? "Which file it crashed in: Windows didn't name one." : $"Why it failed inside {module}.";

        if (f?.Error is { } error)
            return new(Confidence.Named, $"{app} stopped on an error it didn't handle: {error}.", f.ErrorAt is null ? "The .NET runtime recorded the error as it ended the program." : $"The .NET runtime recorded it as it ended the program. It was thrown in {f.ErrorAt}.",
                "This is a fault in the program. Update it; if it keeps happening, the error's name above is what its makers need.", $"{app} stopped on an unhandled error: {error}.")
            { Points = [new("info", "The app recorded its error", f.ErrorAt is null ? error : $"{error}, in {f.ErrorAt}."), .. points], RuledOut = ruled, CantTell = "What caused the error inside the app." };
        if (known is { } m && !string.IsNullOrEmpty(module))
            return new(Confidence.Named, First(ex.Reason), $"{Rest(ex.Reason)}Windows names the file it crashed in: {module}.", ex.Advice, $"{app} crashed inside {Lower(m.Culprit)}.")
            { Short = $"{app} crashed inside {Lower(m.Culprit)}", Points = points, RuledOut = ruled, CantTell = what };
        if (same.Count >= 2)
            return new(Confidence.Likely, $"{app} crashes at the same spot every time.", $"{same.Count + 1} crashes on record, all at the same place in {module}. A fault that repeats exactly is a fault in the program, not hardware that is unstable.",
                "Update the app. If it has add-ons or mods, try without them.", $"{app} crashed at the same spot as {same.Count} times before.")
            { Cause = "A FAULT IN THE APP", Points = points, RuledOut = ruled, CantTell = what };
        if (atStart)
            return new(Confidence.Likely, twin is null ? $"{app} stopped as it was starting." : $"{app} stopped as it was starting, twice {Apart(e.Ts - twin.Ts)}.",
                "A crash within seconds of starting points at the program's own start-up (its files, an add-on, or anti-cheat for a game), not at heat or load.",
                x.IsGame ? "Verify the game's files in its launcher, and repair its anti-cheat if it has one." : "Repair or reinstall it. For a game, verify its files in its launcher and repair its anti-cheat if it has one.",
                $"{app} stopped as it was starting{(twin is null ? "" : $", twice {Apart(e.Ts - twin.Ts)}")}.")
            { Cause = "AT START-UP", Points = points, RuledOut = ruled, CantTell = "Which part of its start-up failed." };
        if (ex.Culprit == "The app itself")
            return new(Confidence.Possible, First(ex.Reason), Rest(ex.Reason) + (ownCode ? "Windows names the program's own file as where it crashed." : "It crashed in a part of Windows every program uses, so the fault is most often the program's own."),
                ex.Advice, ex.Reason)
            { Short = ex.Title, Cause = "A FAULT IN THE APP", Points = points, RuledOut = ruled, CantTell = what };
        if (ex.Culprit is "Graphics driver" or "Windows Explorer" or "Windows service")
            return new(Confidence.Possible, ex.Title + ".", ex.Reason, ex.Advice, ex.Reason) { Short = ex.Title, Points = points, RuledOut = ruled, CantTell = what };
        return new(Confidence.NotEnough, First(ex.Reason), Rest(ex.Reason) + (module.Length == 0 ? "Windows recorded the crash but couldn't name the file it happened in." : $"Windows names {module} as where it crashed. That file alone doesn't say why."),
            ex.Advice, ex.Reason)
        { Short = ex.Title, Points = points, RuledOut = ruled, CantTell = what };
    }

    /// <summary>
    /// An app that went down while a graphics driver was being put in: that explains it by itself, whatever it crashed in.
    /// </summary>
    public static CrashFinding DriverSwap(CrashInput x, SystemChange driver)
    {
        string app = x.AppName ?? x.NameOf(x.Crash.AppExe);
        string name = driver.Title.EndsWith(" installed", StringComparison.Ordinal) ? driver.Title[..^" installed".Length] : driver.Title;
        return Tidy(new(Confidence.Likely, $"{name} was being installed at that moment.", "Apps that are open can crash while a graphics driver is replaced under them. It says nothing about the new driver.",
            "Nothing to do. If it keeps crashing with no driver being installed, that is a different problem.", $"{name} was being installed at that moment.")
        {
            Short = $"{app} crashed while a graphics driver was being installed", Cause = "THE DRIVER INSTALL", Outside = true,
            Points = [new("change", $"{name} was being installed", $"Windows logged it at {driver.Time:h:mm:ss tt}; {app} went down at {x.Crash.Time:h:mm:ss tt}.")],
            CantTell = null,
        });
    }

    /// <summary>
    /// Several things failing within minutes of each other, oldest first: one moment, one finding.
    /// </summary>
    /// <param name="driver">A graphics driver that was being installed at that moment, which explains all of them.</param>
    public static CrashFinding Together(IReadOnlyList<CrashInput> members, SystemChange? driver = null)
    {
        var first = members[0];
        int minutes = Math.Max(1, (int)Math.Ceiling((members[^1].Crash.Ts - first.Crash.Ts) / 60.0));
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        Shared(first, points, ruled);
        // The apps among them, each once; a reset of the graphics driver is not an app.
        var apps = members.Where(m => m.Crash.Kind != CrashKind.GpuDriverReset).Select(m => m.AppName ?? m.NameOf(m.Crash.AppExe)).Distinct().ToList();
        var names = members.Select(m => m.Crash.Kind == CrashKind.GpuDriverReset ? "the graphics driver" : m.AppName ?? m.NameOf(m.Crash.AppExe)).Distinct().ToList();
        string span = minutes <= 1 ? $"at {first.Crash.Time:h:mm tt}" : $"from {first.Crash.Time:h:mm} to {members[^1].Crash.Time:h:mm tt}";
        points.Add(new("clock", $"{names.Count} things failed {span}", string.Join(", ", names) + "."));
        string within = $"within {Minutes(minutes)}";
        string appCount = apps.Count == 1 ? "1 app" : $"{apps.Count} apps";
        const string advice = "When several apps fail together, the cause is usually the whole PC and not one app. Check the drive's health on the Storage page and the temperatures for those minutes.";

        if (driver is not null)
        {
            string name = driver.Title.EndsWith(" installed", StringComparison.Ordinal) ? driver.Title[..^" installed".Length] : driver.Title;
            return Tidy(new(Confidence.Likely, $"{name} was being installed at that moment.", $"{Capital(appCount)} went down {within} of it. Apps that are open can crash while a graphics driver is replaced under them. It says nothing about the new driver.",
                "Nothing to do. If they keep failing with no driver being installed, that is a different problem.", $"{name} was being installed; {appCount} went down with it.")
            {
                Short = $"{Capital(appCount)} went down while a graphics driver was being installed", Cause = "THE DRIVER INSTALL", Outside = true,
                Points = [new("change", $"{name} was being installed", $"Windows logged it at {driver.Time:h:mm:ss tt}."), .. points], RuledOut = ruled,
                CantTell = null,
            });
        }
        if (members.FirstOrDefault(m => m.Crash.Facts?.WaitedOn is not null) is { } stuck)
        {
            string app = stuck.AppName ?? stuck.NameOf(stuck.Crash.AppExe), other = stuck.NameOf(stuck.Crash.Facts!.WaitedOn!);
            string version = stuck.Crash.Facts.WaitedOnVersion is { } v ? $", version {v}" : "";
            int rest = names.Count - 1;
            return Tidy(new(Confidence.Likely, $"{app} was stuck waiting on {other}.", $"Windows' report of the freeze names the program {app} was waiting for. {rest} other thing{(rest == 1 ? "" : "s")} froze or failed {within}.",
                $"Update {other}, or close it and see whether the freezes stop.", $"{app} was stuck waiting on {other}; {rest} more froze {within}.")
            {
                Points = [new("info", $"{app} was waiting on {stuck.Crash.Facts.WaitedOn}", $"Windows recorded this in its report{version}."), .. points], RuledOut = ruled,
                CantTell = "Whether every one of them had the same cause.",
            });
        }
        if (members.Any(m => m.Crash.Kind == CrashKind.GpuDriverReset))
            // Windows records the reset. That the apps failed because of it is the likely reading, not a record.
            return Tidy(new(Confidence.Likely, "The graphics driver reset, and apps failed around it.", $"Windows reset the graphics driver, and {appCount} failed {within}. Apps that are drawing when the driver resets often go down with it.",
                "Update or clean-reinstall the graphics driver, and undo any overclock of the card.", $"The graphics driver reset; {appCount} failed {within}.")
            { Cause = "THE GRAPHICS DRIVER", Points = points, RuledOut = ruled, CantTell = "Why the card stopped answering, and whether every app failed because of it." });
        return Tidy(new(Confidence.Possible, $"{names.Count} things failed {within}.", "Nothing on record names one cause for all of them.", advice, $"{names.Count} things failed {within}.")
        { Cause = "SEVERAL THINGS AT ONCE", Points = points, RuledOut = ruled, CantTell = "What they had in common." });
    }

    // ── Evidence shared by the rules ────────────────────────────────────

    /// <summary>Heat, memory, logged trouble and earlier times: looked at for every app problem.</summary>
    private static void Shared(CrashInput x, List<Evidence> points, List<Evidence> ruled)
    {
        var heat = Heat(x);
        if (heat.Hot) points.Add(new("warn", HotTitle, heat.Evidence.Detail)); else ruled.Add(heat.Evidence);
        Sort(Memory(x), points, ruled);
        points.AddRange(Signs(x));
        if (Before(x) is { } before) points.Add(before);
    }

    private static void Sort(Evidence? e, List<Evidence> points, List<Evidence> ruled)
    {
        if (e is null) return;
        (e.Kind is "ok" or "unknown" ? ruled : points).Add(e);
    }

    /// <summary>
    /// The temperatures just before against the user's own alert limits (83° is where many graphics cards sit in every
    /// game, so no fixed figure), and for a PC doing little, against how it usually runs at rest.
    /// </summary>
    /// <returns>Known: at least one of the two was read. Full: both were, so "cool" can be said of the PC.</returns>
    private static (Evidence Evidence, bool Hot, bool Known, bool Full, string Short) Heat(CrashInput x)
    {
        var c = x.Context;
        if (c is null || (c.CpuBefore is null && c.GpuBefore is null))
            return (new("unknown", "Temperatures weren't recorded", c is null || c.NoReadings || c.Minutes == 0 ? "Nothing was being recorded in those minutes." : "No temperature was read in those minutes."), false, false, false, "");
        var parts = new List<string>();
        if (c.CpuBefore is double cpu) parts.Add($"CPU {Units.TempShort(cpu)}");
        if (c.GpuBefore is double gpu) parts.Add($"GPU {Units.TempShort(gpu)}");
        string now = string.Join(", ", parts);
        bool hotGpu = c.GpuBefore >= x.Alerts.GpuLimit, hotCpu = c.CpuBefore >= x.Alerts.CpuLimit;
        if (hotGpu || hotCpu)
            return (new("warn", HotTitle, $"{now}: above your alert limit of {Units.TempShort(hotGpu ? x.Alerts.GpuLimit : x.Alerts.CpuLimit)} for the {(hotGpu ? "GPU" : "CPU")}."), true, true, parts.Count == 2, now);
        bool light = (c.GpuLoad ?? 100) < LightLoad && (c.CpuLoad ?? 100) < LightLoad;
        var rest = new List<string>();
        if (light && x.Rest is { Minutes: >= 60 } r)
        {
            if (c.CpuBefore is not null && r.Cpu is double rc) rest.Add(Units.TempShort(rc));
            if (c.GpuBefore is not null && r.Gpu is double rg) rest.Add(Units.TempShort(rg));
        }
        string usual = rest.Count == parts.Count && rest.Count > 0 ? $", close to the {string.Join(" and ", rest)} this PC sits at when idle." : $", under your alert limit{(parts.Count == 2 ? "s" : "")}.";
        // One of the two wasn't read (a standard account has no CPU temperature): the other can't clear it.
        string missing = parts.Count == 2 ? "" : c.CpuBefore is null ? " The CPU's temperature wasn't recorded, so this only covers the graphics card." : " The GPU's temperature wasn't recorded, so this only covers the processor.";
        return (new("ok", parts.Count == 2 ? "It wasn't overheating" : c.CpuBefore is null ? "The graphics card wasn't overheating" : "The processor wasn't overheating",
            $"{Sentence(now)}{usual}{missing}"), false, true, parts.Count == 2, now);
    }

    private static Evidence? Memory(CrashInput x)
    {
        var e = x.Crash;
        if (x.Signs.FirstOrDefault(s => s.Kind == SignKind.Memory && s.Ts <= e.Ts + 120 && s.Ts >= e.Ts - 900) is { } sign)
            return new("warn", "Windows was running out of memory", $"It logged this at {sign.Time:h:mm tt}.");
        if (x.Context?.RamUsed is not double used || x.RamTotal is not double total || total <= 0 || used > total * 1.02) return null;
        return used / total >= NearlyFull
            ? new("warn", "Memory was nearly full", $"{used:0.0} of {total:0} GB was in use.")
            : new("ok", "Memory wasn't full", $"{used:0.0} of {total:0} GB was in use.");
    }

    /// <summary>Hardware, drive and graphics faults Windows logged in the half hour before.</summary>
    private static IEnumerable<Evidence> Signs(CrashInput x)
    {
        var e = x.Crash;
        foreach (var s in x.Signs.Where(s => s.Kind is SignKind.Hardware or SignKind.Drive or SignKind.Graphics && s.Ts <= e.Ts + 120 && s.Ts >= e.Ts - 1800))
        {
            if (s.Kind == SignKind.Hardware && e.Kind is CrashKind.UnexpectedShutdown && s.Detail.Contains("couldn't correct", StringComparison.Ordinal)) continue; // said by the finding itself
            int before = (int)Math.Max(0, (e.Ts - s.Ts) / 60);
            string what = e.Kind == CrashKind.GpuDriverReset ? "reset" : "crash";
            yield return new("warn", s.Detail, before <= 1 ? $"In the same minute as the {what}." : $"{Capital(Minutes(before))} before the {what}.");
        }
    }

    private static bool Near(CrashSign s, CrashEvent e, CrashFacts? f) =>
        (s.Ts <= e.Ts + 120 && s.Ts >= e.Ts - 1800) || (f?.NextStart is long start && s.Ts >= start - 60 && s.Ts <= start + 900);

    private static Evidence? Before(CrashInput x)
    {
        // The same thing again within two minutes is this time, not an earlier one (an app that fails twice as it starts).
        var earlier = x.Earlier.Where(p => x.Crash.Ts - p.Ts > 120).ToList();
        int n = earlier.Count;
        if (n == 0) return null;
        long month = x.Crash.Ts - 30L * 86400;
        int recent = earlier.Count(p => p.Ts >= month);
        string versions = x.Crash.Facts?.AppVersion is { } v and not "0.0.0.0" && earlier.All(p => p.Facts?.AppVersion == v) ? $" Every time on version {v}." : "";
        return new("pattern", n == 1 ? "It has happened once before" : $"It has happened {n} times before",
            $"First on {earlier[0].Time:d MMM yyyy}{(recent == n ? "" : $", {recent} of them in the 30 days before this one")}.{versions}");
    }

    /// <summary>
    /// The newest driver or Windows update of the week before: named as a lead only when this problem isn't on record
    /// from before it, and ruled out when it is.
    /// </summary>
    private static Evidence? Change(CrashInput x, bool anyDriver)
    {
        if (x.Changes is null) return null;
        var at = x.Crash.Time;
        var change = x.Changes.Where(c => c.Earliest < at && c.Time >= at.AddDays(-7) && (c.IsGraphicsDriver || c.Kind is ChangeKind.WindowsUpdate or ChangeKind.Windows or ChangeKind.Firmware || (anyDriver && c.IsSystemLevel)))
            .OrderByDescending(c => c.IsGraphicsDriver).ThenByDescending(c => c.Time).FirstOrDefault();
        if (change is null) return new("ok", "Nothing new was installed", "No new driver or Windows update in the week before.");
        string name = change.Title.EndsWith(" installed", StringComparison.Ordinal) ? change.Title[..^" installed".Length] : change.Title;
        int days = (int)(at.Date - change.Time.Date).TotalDays;
        string when = days switch { 0 => "the same day", 1 => "the day before", _ => $"{days} days before" };
        long ts = TimeUtil.ToUnix(change.Earliest);
        int before = x.Earlier.Count(p => p.Ts < ts && p.Ts >= ts - 30L * 86400), since = x.Earlier.Count(p => p.Ts >= ts) + 1;
        return before > 0
            ? new("ok", $"{name} didn't cause it", $"It was installed {when}. This had already happened {Times(before)} in the 30 days before it, and {Times(since)} since.")
            : new("change", $"{name} was installed {when}", "This never happened before it.");
    }

    private static string Loads(CrashContext c) =>
        (c.CpuLoad, c.GpuLoad) switch
        {
            (double cpu, double gpu) => $" Processor at {cpu:0}%, graphics card at {gpu:0}%.",
            (double cpu, null) => $" Processor at {cpu:0}%.",
            (null, double gpu) => $" Graphics card at {gpu:0}%.",
            _ => "",
        };

    private static string Apart(long sec) => sec <= 0 ? "in the same second" : sec == 1 ? "in a second" : $"in {sec} seconds";

    private static string Seconds(double sec) => sec < 1.5 ? "A second" : $"{sec:0} seconds";

    /// <summary>A graphics card's own kernel driver, or Windows' graphics core: a crash in one is a graphics crash.</summary>
    internal static bool IsGraphicsFile(string file)
    {
        string f = file.ToLowerInvariant();
        return f.StartsWith("nvlddmkm", StringComparison.Ordinal) || f.StartsWith("nvoglv", StringComparison.Ordinal) || f.StartsWith("amdkmdag", StringComparison.Ordinal)
               || f.StartsWith("atikm", StringComparison.Ordinal) || f.StartsWith("igdkm", StringComparison.Ordinal) || f.StartsWith("igfx", StringComparison.Ordinal)
               || f.StartsWith("dxgkrnl", StringComparison.Ordinal) || f.StartsWith("dxgmms", StringComparison.Ordinal);
    }

    internal static string Minutes(int n) => n switch
    {
        1 => "1 minute",
        < 120 => $"{n} minutes",
        _ => $"{n / 60} hours",
    };

    /// <summary>A driver's own description as it reads after "the": "Realtek USB Audio Driver" as "Realtek USB audio driver".</summary>
    private static string Plain(string name) =>
        string.Join(' ', name.Split(' ').Select((w, i) => i > 0 && w.Length > 1 && char.IsUpper(w[0]) && w.Skip(1).All(char.IsLower) ? w.ToLowerInvariant() : w));

    /// <summary>
    /// A reason's first sentence, for a headline: the rest ("Windows says it tried to use memory it wasn't allowed
    /// to (Access violation).") is detail, and a headline of two sentences is a paragraph.
    /// </summary>
    private static string First(string reason) => reason.IndexOf(". ", StringComparison.Ordinal) is > 0 and var i ? reason[..(i + 1)] : reason;

    /// <summary>What follows a reason's first sentence, ready to have more put after it ("" when it is one sentence).</summary>
    private static string Rest(string reason) => reason.IndexOf(". ", StringComparison.Ordinal) is > 0 and var i ? reason[(i + 2)..] + " " : "";

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    /// <summary>"NVIDIA driver" reads as "the NVIDIA driver" in a sentence; names keep their capitals.</summary>
    private static string Lower(string culprit) => culprit.StartsWith("The ", StringComparison.Ordinal) ? "the " + culprit[4..] : $"the {culprit}";
}

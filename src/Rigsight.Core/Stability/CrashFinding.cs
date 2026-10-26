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
    /// <summary>
    /// After the confidence word on the chip, where a few words say the cause ("A POWER CUT"). Kept in English, since
    /// it's also matched on: <see cref="Chip"/>, <see cref="HowSure"/> and <see cref="Why"/> translate it, and anywhere
    /// else it's shown must too (<c>Lang.T(Cause)</c>).
    /// </summary>
    public string? Cause { get; init; }

    /// <summary>
    /// What happened, as a fact with no time and no full stop ("Dota 2 crashed", "The PC shut off suddenly"): an event
    /// Windows recorded is never in doubt, only its cause is (see <see cref="Wording"/>).
    /// </summary>
    public string Event { get; init; } = "";

    private readonly string? _why;

    /// <summary>
    /// The cause in running words, to read after the event with no confidence word ("a power cut", "as it was
    /// starting", "inside the NVIDIA driver"): the chip's cause where that already reads so; null where there is none.
    /// </summary>
    public string? Why { get => _why ?? (Cause is null ? null : Wording.Running(Lang.T(Cause))); init => _why = value; }

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

    /// <summary>"LIKELY A POWER CUT", "POSSIBLY MAINS OR POWER SUPPLY", "CONFIRMED", "NOT ENOUGH TO SAY": the same in a list and opened.</summary>
    public string Chip => Wording.Chip(Confidence, Cause is null ? null : Lang.T(Cause));

    /// <summary>The cause with how sure it is, as a sentence of its own: "Likely a power cut.", "Inside the NVIDIA driver.", "Not enough on record to say why."</summary>
    public string Verdict => Wording.CauseSentence(Confidence, Why);

    /// <summary>How sure, for a cell: "Confirmed", "Likely a power cut", "Not enough to say".</summary>
    public string HowSure => Wording.HowSure(Confidence, Cause is null ? null : Lang.T(Cause));

    /// <summary>
    /// The event and its cause in one sentence, with the time where one is given: "Rocket League crashed at 11:07 PM,
    /// likely as it was starting.", "Dota 2 crashed at 9:14 PM with a .NET error (NullReferenceException).", "The PC shut
    /// off suddenly at 3:12 AM. Not enough on record to say why."
    /// </summary>
    public string Told(string? when = null)
    {
        string head = when is null ? Event : Lang.T($"{Event} at {when}");
        return $"{head}{Wording.CauseAfter(Confidence, Why)}.{(Confidence == Confidence.NotEnough ? " " + Wording.NotEnoughWhy : "")}";
    }

    /// <summary>The event and its cause with no time: for a recap, a row.</summary>
    public string Said => Told();
}

/// <summary>Everything a finding is worked out from. Only the crash itself is always there.</summary>
public sealed record CrashInput(CrashEvent Crash, CrashExplanation Explanation, AlertSettings Alerts)
{
    public string? AppName { get; init; }
    public bool IsGame { get; init; }
    public CrashContext? Context { get; init; }
    public string? FrontApp { get; init; }
    /// <summary>Whether the app in front was a game: only a game's graphics settings can be lowered (2026-10-26: a reset in Windows Terminal Host was told to "lower its graphics settings").</summary>
    public bool FrontIsGame { get; init; }
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
    public static string HotTitle => Lang.T("It was running hot");

    /// <summary>How many times, as a person says it: "once", "twice", "17 times".</summary>
    public static string Times(int n) => n switch { 1 => Lang.T("once"), 2 => Lang.T("twice"), _ => Lang.T($"{n} times") };

    /// <summary>"4m" said the way a person says it: "4 minutes", "an hour and 5 minutes".</summary>
    public static string Words(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        static string Secs(int n) => n == 1 ? Lang.T("1 second") : Lang.T($"{n} seconds");
        static string Mins(int n) => n == 1 ? Lang.T("1 minute") : Lang.T($"{n} minutes");
        static string Hours(int n) => n == 1 ? Lang.T("1 hour") : Lang.T($"{n} hours");
        if (t.TotalMinutes < 1) return Secs((int)t.TotalSeconds);
        if (t.TotalHours < 1) return Mins((int)t.TotalMinutes);
        if (t.Minutes == 0) return Hours((int)t.TotalHours);
        string hours = Hours((int)t.TotalHours), minutes = Mins(t.Minutes);
        return Lang.T($"{hours} and {minutes}");
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

    public static CrashFinding Judge(CrashInput x) => Tidy(UpClose(x, Told(x, x.Crash.Kind switch
    {
        CrashKind.UnexpectedShutdown => ShutOff(x),
        CrashKind.SystemCrash => BlueScreen(x),
        CrashKind.GpuDriverReset => DriverReset(x),
        CrashKind.AppHang => Hang(x),
        _ => AppCrash(x),
    })));

    /// <summary>
    /// The event as a fact, where the rule didn't word it itself: an app's by its explanation's title (Windows' own
    /// parts read as what the user saw: "Windows' display crashed"), the rest by its kind.
    /// </summary>
    private static CrashFinding Told(CrashInput x, CrashFinding f) => f.Event.Length > 0 ? f : f with
    {
        Event = x.Crash.Kind == CrashKind.AppCrash && CrashExplainer.IsWindowsPart(x.Crash.AppExe) ? x.Explanation.Title
            : Wording.Event(x.Crash.Kind, x.AppName ?? x.NameOf(x.Crash.AppExe)),
    };

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

    // â”€â”€ The PC went off â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static CrashFinding ShutOff(CrashInput x)
    {
        var (e, f, c, ex) = (x.Crash, x.Crash.Facts, x.Context, x.Explanation);
        if (e.Moment == PowerMoment.ShuttingDown)
            return new(Confidence.Named, Lang.T("Windows was already shutting down when the PC shut off."), Lang.T("It shut off before Windows could record a clean finish. No crash report was saved."), ex.Advice,
                Lang.T("Windows was already shutting down. Usually harmless.")) { Why = Lang.T("as Windows was already shutting down"), Outside = true, Points = [new("info", Lang.T("Windows was already shutting down"), Lang.T("Its record from the next start says it was shutting down, restarting or hibernate was under way."))] };
        if (e.Moment == PowerMoment.Asleep)
            return new(Confidence.Named, Lang.T("The PC shut off while it was asleep."), Lang.T("It was asleep or waking up when it shut off, and no crash report was saved."), ex.Advice,
                Lang.T("The PC was asleep when it shut off.")) { Why = Lang.T("while it was asleep"), Outside = true, Points = [new("info", Lang.T("The PC was asleep"), Lang.T("Windows' record from the next start says so."))] };

        bool has = c is { Minutes: > 0 };
        var heat = Heat(x);
        // Doing little only where how busy it was is on record: a load that wasn't read is not a low one.
        bool light = has && (c!.GpuLoad is not null || c.CpuLoad is not null) && (c.GpuLoad ?? 0) < LightLoad && (c.CpuLoad ?? 0) < LightLoad;
        // How long nobody had been at it: to the minute where the last use is on record, else only "for some time"
        // (no use in the four hours looked at, of which fewer may have been recorded: no figure is made up).
        int? idle = has && c!.LastActive is long last ? (int)Math.Max(0, (e.Ts - last - 60) / 60) : has ? int.MaxValue : null;
        string idleFor = idle == int.MaxValue ? Lang.T("some time") : Wording.Minutes(idle ?? 0);
        int? backOn = c?.BackOn is long on && on - e.Ts <= 3 * 3600 ? (int)Math.Max(1, (on - e.Ts) / 60) : null;
        string? front = x.FrontApp;
        string back = backOn is int b ? " " + Lang.T($"Back on {Wording.Minutes(b)} later.") : "";
        // What Windows itself noted about the power button; null where it noted nothing either way.
        bool noPress = f?.PowerButton == false;
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        string cant = Lang.T("A power cut and a failing power supply look the same from inside the PC. A press of the reset button isn't recorded, and a processor or memory setting that is unstable can switch a PC off the same way.");

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

        if (f?.PowerButton == true) points.Add(new("power", Lang.T("Someone held the power button"), Lang.T("Windows noted it when the PC next started.")));
        else if (noPress) points.Add(new("power", Lang.T("Nobody held the power button"), Lang.T("Windows notes it when the PC next starts if someone does, and it noted nothing.")));
        if (has)
        {
            string loads = Loads(c!);
            string inFront = front is null ? "" : Lang.T($"{front} was in front.");
            if (light && idle >= 5) points.Add(new("pc", Lang.T("Nobody was using it"), (idle == int.MaxValue ? Lang.T("No use on record in the hours before.") : Lang.T($"Idle since {TimeUtil.FromUnix(e.Ts - idle.Value * 60L):h:mm tt}.")) + loads));
            else if (light) points.Add(new("pc", Lang.T("It wasn't doing much"), (inFront + loads).Trim()));
            else if (loads.Length > 0) points.Add(new("pc", Lang.T("It was working hard"), (inFront + loads + (c!.GpuPower is double w ? " " + Lang.T($"The GPU was drawing {w:0} W.") : "")).Trim()));
        }
        if (backOn is int n) points.Add(new("clock", Lang.T($"It came back on at {TimeUtil.FromUnix(c!.BackOn!.Value):h:mm tt}"), Lang.T($"{Wording.Capital(Wording.Minutes(n))} later.")));
        if (Before(x) is { } before) points.Add(before);
        points.AddRange(signs);
        if (heat.Hot) points.Add(new("warn", HotTitle, heat.Evidence.Detail)); else ruled.Add(heat.Evidence);
        if (f is { HardwareErrors: 0 } && !hardware) ruled.Add(new("ok", Lang.T("It wasn't a blue screen or a hardware fault"), Lang.T("Windows saved no crash report and logged no hardware error it couldn't fix.")));
        Sort(change, points, ruled);
        Sort(memory, points, ruled);

        if (f?.PowerButton == true)
            return new(Confidence.Named, Lang.T("It was switched off by holding the power button."), Lang.T("Windows recorded a long press of the power button at the start after. That is usually done when the PC has frozen."),
                Lang.T("If it had frozen, look at what was running just before, below. One freeze is not a pattern; several with the same app in front are."),
                front is null ? Lang.T("Switched off by holding the power button.") : Lang.T($"Switched off by holding the power button, with {front} in front."))
            { Why = Lang.T("because someone held the power button"), Points = points, RuledOut = ruled, CantTell = Lang.T("Why it had frozen, if it had.") };
        if (hardware)
            return new(Confidence.Likely, Lang.T("The hardware reported an error."), Lang.T("Windows logged a hardware error it couldn't correct around the time the PC shut off. That points at the processor, the memory or the motherboard, or at tuning that is too tight for them."),
                Lang.T("Undo any overclock or undervolt (Curve Optimizer, XMP or EXPO included), update the BIOS, and check the temperatures."),
                Lang.T("Windows logged a hardware error around it."))
            { Cause = Lang.Key("A HARDWARE FAULT"), Points = [new("warn", Lang.T("Windows logged a hardware error"), f?.HardwareErrors > 0 ? Lang.T($"{f.HardwareErrors} handed over by the firmware at the next start.") : Lang.T("In the minutes around it.")), .. points], RuledOut = ruled, CantTell = Lang.T("Which part reported it.") };
        if (hungAt is not null)
        {
            var at = TimeUtil.FromUnix(hungAt.Ts);
            string starting = f?.NextStart is long next && next > hungAt.Ts ? " " + Lang.T($"The PC was starting again at {TimeUtil.FromUnix(next):h:mm:ss tt}.") : "";
            return new(Confidence.Likely, Lang.T("The GPU hung, and the PC shut off with it."), Lang.T($"Windows recorded the GPU hanging at {at:h:mm:ss tt}, while the PC was still running.") + starting + " " + Lang.T("A GPU that stops answering can take the whole PC with it."),
                Lang.T("Update or clean-reinstall the graphics driver and undo any overclock of the GPU. If it happens again, check the GPU's power cables."),
                (front is null ? Lang.T($"The GPU hung at {at:h:mm:ss tt}, then the PC shut off.") : Lang.T($"The GPU hung at {at:h:mm:ss tt} in {front}, then the PC shut off.")) + back)
            {
                Cause = Lang.Key("THE GPU HUNG"), Why = Lang.T("after the GPU hung"),
                Points = [new("warn", Lang.T($"Windows recorded the GPU hanging at {at:h:mm:ss tt}"), Lang.T("Its report gives the moment of the hang, before the PC shut off.")), .. points],
                RuledOut = ruled, CantTell = Lang.T("Why the GPU hung. It could be its driver, heat, power, or the GPU itself."),
            };
        }
        if (hang is not null)
            return new(Confidence.Possible, Lang.T("The GPU may have stopped answering."), Lang.T($"Windows filed a graphics-hang report at the start after, {hang.Time:h:mm tt}. A screen that freezes or goes black, and a PC that then has to be switched off, fit that."),
                Lang.T("Update or clean-reinstall the graphics driver and undo any overclock of the GPU."), Lang.T("Windows filed a graphics-hang report at the next start."))
            { Cause = Lang.Key("THE GPU HUNG"), Why = Lang.T("after the GPU hung"), Points = [new("warn", Lang.T("Windows reported that the GPU hung"), Lang.T($"It filed the report at {hang.Time:h:mm tt}, when the PC started again.")), .. points], RuledOut = ruled, CantTell = Lang.T("When the GPU hung. The report carries the time it was filed, not the time of the hang.") };
        if (f is null && !has)
            return new(Confidence.NotEnough, Lang.T("Not enough on record to say why."), ex.Reason, ex.Advice, Lang.T("No crash report was saved, and nothing was being recorded.")) { Points = points, RuledOut = ruled, CantTell = cant };
        if (heat.Hot)
            return new(Confidence.Possible, Lang.T("It was running hot when it shut off."), heat.Evidence.Detail + " " + Lang.T("A PC that overheats switches itself off to protect its parts; a power cut at the same moment would look the same."),
                Lang.T("Check the fans and airflow, and the temperatures on the Temperatures page for the minutes before."),
                (front is null ? Lang.T($"Running hot when it shut off ({heat.Short}).") : Lang.T($"Running hot in {front} when it shut off ({heat.Short}).")) + back)
            { Cause = Lang.Key("HEAT"), Points = points, RuledOut = ruled, CantTell = cant };
        if (!has)
            return new(Confidence.Possible, Lang.T("The PC shut off, and nothing was being recorded."),
                noPress ? Lang.T("Windows recorded no press of the power button and saved no crash, so the PC lost power or was reset. What it was doing isn't on record.")
                    : Lang.T("Windows saved no crash, so the PC lost power or was reset. What it was doing isn't on record."),
                ex.Advice, Lang.T("No crash saved. Nothing was being recorded.") + back)
            { Cause = Lang.Key("A POWER CUT OR A RESET"), Points = points, RuledOut = ruled, CantTell = cant };
        if (light && noPress && backOn is <= QuickBack)
        {
            // Running again within a minute or two: the power dipped for a moment, or the PC reset itself.
            string again = Wording.Minutes(backOn.Value);
            return new(Confidence.Possible, Lang.T($"It shut off and was running again {again} later."),
                Lang.T("Windows saved no crash and recorded no press of the power button. A PC that is back that fast either lost power for a moment, or reset itself, which a processor or memory setting that is unstable at idle can do."),
                Lang.T("If the lights or other devices flickered too, it was the mains. If it repeats with nothing else affected, undo any undervolt or memory tuning (Curve Optimizer, XMP or EXPO) and see whether it stops."),
                (idle >= 5 ? Lang.T($"Idle for {idleFor}.") : front is null ? Lang.T("Doing little.") : Lang.T($"In {front}.")) + " " + Lang.T($"Running again {again} later."))
            { Cause = Lang.Key("A POWER DIP OR A RESET"), Points = points, RuledOut = ruled, CantTell = cant };
        }
        if (light && noPress && trouble)
            return new(Confidence.Possible, Lang.T("The PC shut off while it was doing little."), Lang.T("Windows saved no crash and recorded no press of the power button, but there was trouble on record just before it (above), so this one isn't put down to the mains."),
                ex.Advice, (idle >= 5 ? Lang.T($"Idle for {idleFor}, with trouble logged just before.") : front is null ? Lang.T("Doing little, with trouble logged just before.") : Lang.T($"In {front}, with trouble logged just before.")) + back)
            { Cause = Lang.Key("A POWER CUT OR A RESET"), Points = points, RuledOut = ruled, CantTell = cant };
        if (light && noPress)
        {
            // "Cool" only where both the processor's and the graphics card's temperature were read.
            bool cool = heat.Full;
            string text = (idle >= 5, cool, front) switch
            {
                (true, true, _) => Lang.T($"The PC had been idle for {idleFor}, it was cool, Windows logged no error, and it recorded no press of the power button."),
                (true, false, _) => Lang.T($"The PC had been idle for {idleFor}, Windows logged no error, and it recorded no press of the power button."),
                (false, true, null) => Lang.T("The PC was doing little, it was cool, Windows logged no error, and it recorded no press of the power button."),
                (false, false, null) => Lang.T("The PC was doing little, Windows logged no error, and it recorded no press of the power button."),
                (false, true, _) => Lang.T($"The PC was doing little in {front}, it was cool, Windows logged no error, and it recorded no press of the power button."),
                (false, false, _) => Lang.T($"The PC was doing little in {front}, Windows logged no error, and it recorded no press of the power button."),
            };
            if (backOn is int m) text += " " + Lang.T($"It was back on {Wording.Minutes(m)} later.");
            string line = idle >= 5
                ? (cool ? Lang.T($"Idle and cool for {idleFor}.") : Lang.T($"Idle for {idleFor}."))
                : (front, c!.GpuLoad) switch
                {
                    (null, double g) => Lang.T($"Doing little, GPU at {g:0}%."),
                    (null, null) => Lang.T("Doing little."),
                    (_, double g) => Lang.T($"In {front}, GPU at {g:0}%."),
                    _ => Lang.T($"In {front}."),
                };
            return new(Confidence.Likely, Lang.T("Most likely a power cut."), text,
                Lang.T("If other things in the room lost power too, it was the mains, and a battery backup (UPS) stops these. If nothing else did, look at the PC's power cable and power supply."),
                line + back)
            { Cause = Lang.Key("A POWER CUT"), Outside = true, Points = points, RuledOut = ruled, CantTell = cant };
        }
        if (light)
            return new(Confidence.Possible, Lang.T("The PC shut off while it was doing little."), Lang.T("No crash report was saved. Whether the power button was held isn't on record for this one, so a freeze that was switched off by hand can't be told from a power cut."),
                ex.Advice, (front is null ? Lang.T("Doing little, no crash saved.") : Lang.T($"In {front}, no crash saved.")) + back)
            { Cause = Lang.Key("A POWER CUT OR A FREEZE"), Points = points, RuledOut = ruled, CantTell = cant };
        string shut = (front, c!.GpuPower) switch
        {
            (null, double p) => Lang.T($"It shut off with the GPU drawing {p:0} W."),
            (null, null) => Lang.T("It shut off."),
            (_, double p) => Lang.T($"It shut off in {front} with the GPU drawing {p:0} W."),
            _ => Lang.T($"It shut off in {front}."),
        };
        string fits = noPress
            ? Lang.T("Windows recorded no press of the power button and saved no crash, which fits a power cut, and also a power supply that can't keep up.")
            : Lang.T("Windows saved no crash, which fits a power cut, and also a power supply that can't keep up.");
        string working = (front, c.GpuPower) switch
        {
            (null, double drawn) => Lang.T($"Working hard, GPU drawing {drawn:0} W."),
            (null, null) => Lang.T("Working hard."),
            (_, double drawn) => Lang.T($"In {front}, GPU drawing {drawn:0} W."),
            _ => Lang.T($"In {front}."),
        };
        return new(Confidence.Possible, Lang.T("The PC shut off while it was working hard."), shut + " " + fits,
            Lang.T("If it only ever happens under load, suspect the power supply. If it happens at idle too, suspect the mains."),
            working + back)
        { Cause = Lang.Key("MAINS OR POWER SUPPLY"), Why = Lang.T("the mains or the power supply"), Points = points, RuledOut = ruled, CantTell = cant };
    }

    // â”€â”€ Windows crashed â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static CrashFinding BlueScreen(CrashInput x)
    {
        var (e, f, ex) = (x.Crash, x.Crash.Facts, x.Explanation);
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        var heat = Heat(x);
        if (heat.Hot) points.Add(new("warn", HotTitle, heat.Evidence.Detail)); else ruled.Add(heat.Evidence);
        if (f?.HardwareErrors > 0) points.Add(new("warn", Lang.T("Windows logged a hardware error"), Lang.T($"{f.HardwareErrors} handed over by the firmware at the next start.")));
        points.AddRange(Signs(x));
        if (Before(x) is { } before) points.Add(before);
        Sort(Memory(x), points, ruled);
        string code = ex.Cause is { } name && name != e.Code ? $"{name} ({e.Code})" : e.Code ?? Lang.T("unknown");

        if (f?.Driver is { } file && !DriverFiles.IsWindowsCore(file))
        {
            var known = CrashExplainer.Module(file);
            // A known file by its plain name ("NVIDIA driver"); else what the file says it is.
            string what = known is { } k ? Lang.T(k.Culprit) : f.DriverName ?? file;
            bool graphics = IsGraphicsFile(file);
            if (!graphics) ruled.Add(new("ok", Lang.T("It wasn't the graphics driver"), Lang.T("The file Windows named isn't part of it.")));
            else Sort(Change(x, anyDriver: false), points, ruled);
            string plain = Plain(what);
            // A known culprit is said whole with its article ("the NVIDIA driver", in another language its own article);
            // a name the file gives itself can't be, so those sentences have their own entries ("name") to word around it.
            string? the = known is { } kc ? Lang.T("the " + Plain(kc.Culprit)) : null;
            return new(Confidence.Named, what == file ? Lang.T($"Windows crashed inside {file}.") : the is not null ? Lang.T($"Windows crashed inside {file}, {the}.") : Lang.C("name", $"Windows crashed inside {file}, the {plain}."),
                Lang.T("Windows' own record of the crash names the file it happened in."),
                known?.Advice ?? (what == file
                    ? Lang.T("Update or reinstall the driver that file belongs to. If the crashes began after new hardware or a new driver, that is the first thing to undo.")
                    : Lang.C("name", $"Update or reinstall the {plain}. If the crashes began after new hardware or a new driver, that is the first thing to undo.")),
                what == file ? Lang.T($"Windows crashed inside {file}.") : the is not null ? Lang.T($"Windows crashed inside {the} ({file}).") : Lang.C("name", $"Windows crashed inside the {plain} ({file})."))
            {
                Why = what == file ? Lang.T($"inside {file}") : the is not null ? Lang.T($"inside {the}") : Lang.C("name", $"inside the {plain}"),
                Points = [new("info", Lang.T($"Windows blames {file}"), Lang.T("It wrote this beside the crash when the PC next started.")), new("info", Lang.T("The blue screen's code"), code), .. points],
                RuledOut = ruled,
                CantTell = x.Context is { Minutes: > 0 } ? Lang.T("Whether the driver itself failed, or the hardware under it.") : Lang.T("What was running at the time. Nothing was being recorded."),
            };
        }
        Sort(Change(x, anyDriver: true), points, ruled);
        bool hardware = e.Code is "0x124" or "0x101";
        return new(hardware ? Confidence.Likely : Confidence.Possible, ex.Reason, Lang.T("Windows recorded the kind of crash but named no driver for it."), ex.Advice, ex.Reason)
        {
            Cause = hardware ? Lang.Key("A HARDWARE FAULT") : null,
            Points = [new("info", Lang.T("The blue screen's code"), code), .. points], RuledOut = ruled,
            CantTell = Lang.T("Which driver or part was at fault. Windows didn't name one."),
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
            ? new Evidence("info", Lang.T("Windows reported that the GPU stopped answering"), f.Driver is { } driver
                ? (CrashExplainer.Module(driver)?.Culprit is { } known
                    ? Lang.T($"Its report names {driver}, {The(known)}, and Windows restarted it.")
                    : Lang.T($"Its report names {driver}, and Windows restarted it."))
                : Lang.T("Windows restarted the graphics driver and filed a report."))
            : new Evidence("info", Lang.T("Windows restarted the graphics driver"), Lang.T("It recorded this at that moment."));
        string advice = x.FrontApp is null || !x.FrontIsGame
            ? Lang.T("Update or clean-reinstall the graphics driver and undo any overclock of the GPU. If it keeps happening, check the GPU's temperatures around these times.")
            : Lang.T($"If it keeps happening in {x.FrontApp}, lower its graphics settings or cap its frame rate first. If it happens in other games too, update or clean-reinstall the graphics driver and undo any overclock of the GPU.");
        return new(Confidence.Named, Lang.T("The GPU stopped answering and Windows reset its driver."), Lang.T("The screen may have gone black for a moment. Windows records the reset, not why the GPU stopped."),
            advice, x.FrontApp is null ? Lang.T("Windows reset the graphics driver.") : Lang.T($"Windows reset the graphics driver while {x.FrontApp} was in front."))
        { Event = x.FrontApp is null ? Wording.Event(CrashKind.GpuDriverReset, "") : x.FrontIsGame ? Lang.T($"The graphics driver was reset in {x.FrontApp}") : Lang.T($"The graphics driver was reset while {x.FrontApp} was in front"), Points = [said, .. points], RuledOut = ruled, CantTell = Lang.T("Why the GPU stopped answering. It could be its driver, heat, power, or the GPU itself.") };
    }

    // â”€â”€ One app â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static CrashFinding Hang(CrashInput x)
    {
        var (e, f, ex) = (x.Crash, x.Crash.Facts, x.Explanation);
        string app = x.AppName ?? x.NameOf(e.AppExe);
        var points = new List<Evidence>();
        var ruled = new List<Evidence>();
        if (f?.RanSec is double ran)
            points.Add(new("clock",
                ran >= StartUpSec ? Lang.T($"It had been running for {Units.Duration(ran)}") : ran < 1.5 ? Lang.T("A second after it was started") : Lang.T($"{ran:0} seconds after it was started"),
                ran < StartUpSec ? Lang.T("It froze as it was starting.") : Lang.T("So it started and ran normally first.")));
        Shared(x, points, ruled);
        if (f?.WaitedOn is { } other)
        {
            string name = x.NameOf(other);
            return new(Confidence.Likely, Lang.T($"{app} was stuck waiting on {name}."), Lang.T("Windows' report of the freeze names the program it was waiting for."),
                Lang.T($"Update {name}, or close it and see whether {app} still freezes."), Lang.T($"{app} was stuck waiting on {name}."))
            {
                Why = Lang.T($"stuck waiting on {name}"),
                Points = [new("info", Lang.T($"{app} was waiting on {other}"), f.WaitedOnVersion is { } version
                    ? Lang.T($"Windows recorded this in its report ({other} version {version}).")
                    : Lang.T("Windows recorded this in its report.")), .. points],
                RuledOut = ruled, CantTell = Lang.T($"Why {name} didn't answer."),
            };
        }
        // Memory nearly full or running out (Shared put it among the points).
        var full = Memory(x) is { Kind: "warn" } low ? low : null;
        var drive = x.Signs.FirstOrDefault(s => s.Kind == SignKind.Drive && s.Ts <= e.Ts + 120 && s.Ts >= e.Ts - 600);
        if (full is not null)
            return new(Confidence.Possible, Lang.T($"{app} froze with memory nearly full."), full.Detail, Lang.T("Close what you aren't using, and see the Memory page for what was holding the most."), Lang.T($"{app} froze with memory nearly full."))
            { Cause = Lang.Key("OUT OF MEMORY"), Why = Lang.T("with memory nearly full"), Points = points, RuledOut = ruled, CantTell = Lang.T("Whether low memory caused it, or only happened at the same time.") };
        if (drive is not null)
            return new(Confidence.Possible, Lang.T($"{app} froze while a drive was reporting errors."), Lang.T($"{Lang.T(drive.Detail)}, at {drive.Time:h:mm tt}. A program waiting on a drive that doesn't answer freezes."),
                Lang.T("Check the drive's health on the Storage page, and its cable."), Lang.T($"{app} froze while a drive was reporting errors."))
            { Cause = Lang.Key("A DRIVE"), Why = Lang.T("a drive that wasn't answering"), Points = points, RuledOut = ruled, CantTell = Lang.T("Whether the app was using that drive.") };
        return new(Confidence.NotEnough, Lang.T($"{app} froze and was closed."), Lang.T("Windows recorded the freeze, but not what the app was waiting for."), ex.Advice, Lang.T($"{app} froze. Windows didn't record what it was waiting for."))
        { Points = points, RuledOut = ruled, CantTell = Lang.T("What it was waiting for.") };
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

        if (atStart)
        {
            double ranSec = f!.RanSec!.Value;
            string title = ranSec < 1.5 ? Lang.T("It crashed a second after starting") : Lang.T($"It crashed {ranSec:0} seconds after starting");
            points.Add(new("clock", title, twin is null ? Lang.T("Windows' record of the crash says when the app was started.") : e.Ts == twin.Ts ? Lang.T("It had crashed in the same second the time before, too.") : Lang.T($"It had crashed {e.Ts - twin.Ts} seconds earlier too.")));
        }
        else if (f?.RanSec is double ran) points.Add(new("clock", Lang.T($"It had been running fine for {Words(ran)}"), Lang.T("So it didn't fail while starting up.")));
        else if (c?.SessionSec is double s && s >= 60) points.Add(new("clock", x.IsGame ? Lang.T($"It crashed {Words(s)} into your game") : Lang.T($"It crashed {Words(s)} after you started using it"), Lang.T("It was working fine until then.")));
        if (f?.ReportType is { } type && type.StartsWith("BEX", StringComparison.OrdinalIgnoreCase))
            points.Add(new("info", Lang.T("Windows stopped it for touching memory it shouldn't"), Lang.T($"Its report is of the kind {type}, so the app ran or wrote where it isn't allowed to.")));
        if (module.Length == 0) points.Add(new("unknown", Lang.T("Windows couldn't tell which file it crashed in"), Lang.T("Its report lists it as unknown.")));
        else if (f?.AppVersion is not null || f?.ModuleVersion is not null)
            points.Add(new("info", Lang.T($"It crashed in {module}"), string.Join(" ", new[]
            {
                f.AppVersion is null or "0.0.0.0" ? null : Lang.T($"{app} was on version {f.AppVersion}."),
                ownCode || f.ModuleVersion is null or "0.0.0.0" ? null : Lang.T($"{module} was on version {f.ModuleVersion}."),
            }.OfType<string>())));
        var same = f?.Offset is { } offset ? x.Earlier.Where(p => p.Facts?.Offset == offset && string.Equals(p.Module, e.Module, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        Shared(x, points, ruled);
        bool graphics = known is { } k && (k.Culprit.Contains("driver", StringComparison.OrdinalIgnoreCase) || k.Culprit is "DirectX" or "Vulkan");
        if (graphics || e.AppExe.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase)) Sort(Change(x, anyDriver: false), points, ruled);
        string what = module.Length == 0 ? Lang.T("Which file it crashed in. Windows didn't name one.") : Lang.T($"Why it failed inside {module}.");

        if (f?.Error is { } error)
            return new(Confidence.Named, Lang.T($"{app} crashed on a .NET error ({error})."), f.ErrorAt is null ? Lang.T("The .NET runtime recorded the error as it ended the program.") : Lang.T($"The .NET runtime recorded it as it ended the program. It was thrown in {f.ErrorAt}."),
                Lang.T("This is a fault in the program. Update it; if it keeps happening, the error's name above is what its makers need."), Lang.T($"{app} crashed on a .NET error ({error})."))
            { Why = Lang.T($"with a .NET error ({ErrorName(error)})"), Points = [new("info", Lang.T("The app recorded its error"), f.ErrorAt is null ? error : Lang.T($"{error}, in {f.ErrorAt}.")), .. points], RuledOut = ruled, CantTell = Lang.T("What caused the error inside the app.") };
        if (known is { } m && !string.IsNullOrEmpty(module))
            return new(Confidence.Named, First(ex.Reason), Rest(ex.Reason) + Lang.T($"Windows names {module} as the file it crashed in."), ex.Advice, Lang.T($"{app} crashed inside {The(m.Culprit)}."))
            { Why = Lang.T($"inside {The(m.Culprit)}"), Points = points, RuledOut = ruled, CantTell = what };
        if (same.Count >= 2)
            return new(Confidence.Likely, Lang.T($"{app} crashes at the same spot every time."), Lang.T($"{same.Count + 1} crashes on record, all at the same place in {module}. A fault that repeats exactly is a fault in the program, not hardware that is unstable."),
                Lang.T("Update the app. If it has add-ons or mods, try without them."), Lang.T($"{app} crashed at the same spot as {same.Count} times before."))
            { Cause = Lang.Key("A FAULT IN THE APP"), Points = points, RuledOut = ruled, CantTell = what };
        if (atStart)
        {
            string? apart = twin is null ? null : Apart(e.Ts - twin.Ts);
            string started = apart is null ? Lang.T($"{app} crashed as it was starting.") : Lang.T($"{app} crashed as it was starting, twice {apart}.");
            return new(Confidence.Likely, started,
                Lang.T("A crash within seconds of starting points at the program's own start-up (its files, an add-on, or anti-cheat for a game), not at heat or load."),
                x.IsGame ? Lang.T("Verify the game's files in its launcher, and repair its anti-cheat if it has one.") : Lang.T("Repair or reinstall it. For a game, verify its files in its launcher and repair its anti-cheat if it has one."),
                started)
            { Cause = Lang.Key("AT START-UP"), Why = Lang.T("as it was starting"), Points = points, RuledOut = ruled, CantTell = Lang.T("Which part of its start-up failed.") };
        }
        if (ex.Culprit == "The app itself")
            return new(Confidence.Possible, First(ex.Reason), Rest(ex.Reason) + (ownCode ? Lang.T("Windows names the program's own file as where it crashed.") : Lang.T("It crashed in a part of Windows every program uses, so the fault is most often the program's own.")),
                ex.Advice, ex.Reason)
            { Cause = Lang.Key("A FAULT IN THE APP"), Points = points, RuledOut = ruled, CantTell = what };
        if (ex.Culprit is "Graphics driver" or "Windows Explorer" or "Windows service")
            return new(Confidence.Possible, ex.Title + ".", ex.Reason, ex.Advice, ex.Reason) { Points = points, RuledOut = ruled, CantTell = what };
        return new(Confidence.NotEnough, First(ex.Reason), Rest(ex.Reason) + (module.Length == 0 ? Lang.T("Windows recorded the crash but couldn't name the file it happened in.") : Lang.T($"Windows names {module} as where it crashed. That file alone doesn't say why.")),
            ex.Advice, ex.Reason)
        { Points = points, RuledOut = ruled, CantTell = what };
    }

    /// <summary>
    /// An app that crashed while a graphics driver was being put in: that explains it by itself, whatever it crashed in.
    /// </summary>
    public static CrashFinding DriverSwap(CrashInput x, SystemChange driver)
    {
        string app = x.AppName ?? x.NameOf(x.Crash.AppExe);
        string name = driver.Title.EndsWith(" installed", StringComparison.Ordinal) ? driver.Title[..^" installed".Length] : driver.Title;
        return Tidy(new(Confidence.Likely, Lang.T($"{name} was being installed at that moment."), Lang.T("Apps that are open can crash while a graphics driver is replaced under them. It says nothing about the new driver."),
            Lang.T("Nothing to do. If it keeps crashing with no driver being installed, that is a different problem."), Lang.T($"{name} was being installed at that moment."))
        {
            Event = Wording.Event(CrashKind.AppCrash, app), Cause = Lang.Key("THE DRIVER INSTALL"), Why = Lang.T("as a graphics driver was being installed"), Outside = true,
            Points = [new("change", Lang.T($"{name} was being installed"), Lang.T($"Windows logged it at {driver.Time:h:mm:ss tt}; {app} crashed at {x.Crash.Time:h:mm:ss tt}."))],
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
        var names = members.Select(m => m.Crash.Kind == CrashKind.GpuDriverReset ? Lang.T("the graphics driver") : m.AppName ?? m.NameOf(m.Crash.AppExe)).Distinct().ToList();
        int count = names.Count;
        string failed = minutes <= 1
            ? Lang.T($"{count} things failed at {first.Crash.Time:h:mm tt}")
            : Lang.T($"{count} things failed from {first.Crash.Time:h:mm} to {members[^1].Crash.Time:h:mm tt}");
        points.Add(new("clock", failed, string.Join(", ", names) + "."));
        string mins = Wording.Minutes(minutes);
        bool oneApp = apps.Count == 1;
        int appsCount = apps.Count;
        string advice = Lang.T("When several apps fail together, the cause is usually the whole PC and not one app. Check the drive's health on the Storage page and the temperatures for those minutes.");
        string thingsFailed = Lang.T($"{count} things failed within {mins}");

        if (driver is not null)
        {
            string name = driver.Title.EndsWith(" installed", StringComparison.Ordinal) ? driver.Title[..^" installed".Length] : driver.Title;
            return Tidy(new(Confidence.Likely, Lang.T($"{name} was being installed at that moment."),
                (oneApp ? Lang.T($"1 app failed within {mins} of it.") : Lang.T($"{appsCount} apps failed within {mins} of it.")) + " " + Lang.T("Apps that are open can crash while a graphics driver is replaced under them. It says nothing about the new driver."),
                Lang.T("Nothing to do. If they keep failing with no driver being installed, that is a different problem."),
                oneApp ? Lang.T($"{name} was being installed; 1 app failed with it.") : Lang.T($"{name} was being installed; {appsCount} apps failed with it."))
            {
                Event = oneApp ? Lang.T($"1 app failed within {mins}") : Lang.T($"{appsCount} apps failed within {mins}"), Cause = Lang.Key("THE DRIVER INSTALL"), Why = Lang.T("as a graphics driver was being installed"), Outside = true,
                Points = [new("change", Lang.T($"{name} was being installed"), Lang.T($"Windows logged it at {driver.Time:h:mm:ss tt}.")), .. points], RuledOut = ruled,
                CantTell = null,
            });
        }
        if (members.FirstOrDefault(m => m.Crash.Facts?.WaitedOn is not null) is { } stuck)
        {
            string app = stuck.AppName ?? stuck.NameOf(stuck.Crash.AppExe), other = stuck.NameOf(stuck.Crash.Facts!.WaitedOn!);
            int rest = count - 1;
            string others = rest == 1 ? Lang.T($"1 other thing froze or failed within {mins}.") : Lang.T($"{rest} other things froze or failed within {mins}.");
            return Tidy(new(Confidence.Likely, Lang.T($"{app} was stuck waiting on {other}."), Lang.T($"Windows' report of the freeze names the program {app} was waiting for.") + " " + others,
                Lang.T($"Update {other}, or close it and see whether the freezes stop."), Lang.T($"{app} was stuck waiting on {other}; {rest} more froze within {mins}."))
            {
                Event = Lang.T($"{count} things froze or failed within {mins}"), Why = Lang.T($"{app} was stuck waiting on {other}"),
                Points = [new("info", Lang.T($"{app} was waiting on {stuck.Crash.Facts.WaitedOn}"), stuck.Crash.Facts.WaitedOnVersion is { } v
                    ? Lang.T($"Windows recorded this in its report, version {v}.")
                    : Lang.T("Windows recorded this in its report.")), .. points], RuledOut = ruled,
                CantTell = Lang.T("Whether every one of them had the same cause."),
            });
        }
        if (members.Any(m => m.Crash.Kind == CrashKind.GpuDriverReset))
            // Windows records the reset. That the apps failed because of it is the likely reading, not a record.
            return Tidy(new(Confidence.Likely, Lang.T("The graphics driver reset, and apps failed around it."),
                (oneApp ? Lang.T($"Windows reset the graphics driver, and 1 app failed within {mins}.") : Lang.T($"Windows reset the graphics driver, and {appsCount} apps failed within {mins}.")) + " " + Lang.T("Apps that are drawing when the driver resets often go down with it."),
                Lang.T("Update or clean-reinstall the graphics driver, and undo any overclock of the GPU."),
                oneApp ? Lang.T($"The graphics driver reset; 1 app failed within {mins}.") : Lang.T($"The graphics driver reset; {appsCount} apps failed within {mins}."))
            { Event = thingsFailed, Cause = Lang.Key("THE GRAPHICS DRIVER"), Why = Lang.T("the graphics driver reset"), Points = points, RuledOut = ruled, CantTell = Lang.T("Why the GPU stopped answering, and whether every app failed because of it.") });
        return Tidy(new(Confidence.Possible, thingsFailed + ".", Lang.T("Nothing on record names one cause for all of them."), advice, thingsFailed + ".")
        { Event = thingsFailed, Cause = Lang.Key("SEVERAL THINGS AT ONCE"), Points = points, RuledOut = ruled, CantTell = Lang.T("What they had in common.") });
    }

    // â”€â”€ Evidence shared by the rules â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
    /// The temperatures just before against the user's own alert limits (83Â° is where many graphics cards sit in every
    /// game, so no fixed figure), and for a PC doing little, against how it usually runs at rest.
    /// </summary>
    /// <returns>Known: at least one of the two was read. Full: both were, so "cool" can be said of the PC.</returns>
    private static (Evidence Evidence, bool Hot, bool Known, bool Full, string Short) Heat(CrashInput x)
    {
        var c = x.Context;
        if (c is null || (c.CpuBefore is null && c.GpuBefore is null))
            return (new("unknown", Lang.T("Temperatures weren't recorded"), c is null || c.NoReadings || c.Minutes == 0 ? Lang.T("Nothing was being recorded in those minutes.") : Lang.T("No temperature was read in those minutes.")), false, false, false, "");
        string? cpuT = c.CpuBefore is double cpu ? Units.TempShort(cpu) : null, gpuT = c.GpuBefore is double gpu ? Units.TempShort(gpu) : null;
        var parts = new List<string>();
        if (cpuT is not null) parts.Add($"CPU {cpuT}");
        if (gpuT is not null) parts.Add($"GPU {gpuT}");
        string now = string.Join(", ", parts);
        bool both = parts.Count == 2;
        bool hotGpu = c.GpuBefore >= x.Alerts.GpuLimit, hotCpu = c.CpuBefore >= x.Alerts.CpuLimit;
        if (hotGpu || hotCpu)
            return (new("warn", HotTitle, hotGpu
                ? Lang.T($"{now}, above your alert limit of {Units.TempShort(x.Alerts.GpuLimit)} for the GPU.")
                : Lang.T($"{now}, above your alert limit of {Units.TempShort(x.Alerts.CpuLimit)} for the CPU.")), true, true, both, now);
        bool light = (c.GpuLoad ?? 100) < LightLoad && (c.CpuLoad ?? 100) < LightLoad;
        string? restCpu = null, restGpu = null;
        if (light && x.Rest is { Minutes: >= 60 } r)
        {
            if (c.CpuBefore is not null && r.Cpu is double rc) restCpu = Units.TempShort(rc);
            if (c.GpuBefore is not null && r.Gpu is double rg) restGpu = Units.TempShort(rg);
        }
        // Close to this PC's usual at rest only where that is known for every part that was read.
        bool close = (cpuT is null || restCpu is not null) && (gpuT is null || restGpu is not null);
        string said = (cpuT, gpuT, close) switch
        {
            ({ }, { }, true) => Lang.T($"The CPU was at {cpuT} and the GPU at {gpuT}, close to the {restCpu} and {restGpu} this PC sits at when idle."),
            ({ }, { }, false) => Lang.T($"The CPU was at {cpuT} and the GPU at {gpuT}, under your alert limits."),
            ({ }, null, true) => Lang.T($"The CPU was at {cpuT}, close to the {restCpu} this PC sits at when idle."),
            ({ }, null, false) => Lang.T($"The CPU was at {cpuT}, under your alert limit."),
            (null, _, true) => Lang.T($"The GPU was at {gpuT}, close to the {restGpu} this PC sits at when idle."),
            _ => Lang.T($"The GPU was at {gpuT}, under your alert limit."),
        };
        // One of the two wasn't read (a standard account has no CPU temperature): the other can't clear it.
        string missing = both ? "" : " " + (cpuT is null ? Lang.T("The CPU's temperature wasn't recorded, so this only covers the GPU.") : Lang.T("The GPU's temperature wasn't recorded, so this only covers the CPU."));
        return (new("ok", both ? Lang.T("It wasn't overheating") : cpuT is null ? Lang.T("The GPU wasn't overheating") : Lang.T("The CPU wasn't overheating"),
            said + missing), false, true, both, now);
    }

    private static Evidence? Memory(CrashInput x)
    {
        var e = x.Crash;
        if (x.Signs.FirstOrDefault(s => s.Kind == SignKind.Memory && s.Ts <= e.Ts + 120 && s.Ts >= e.Ts - 900) is { } sign)
            return new("warn", Lang.T("Windows was running out of memory"), Lang.T($"It logged this at {sign.Time:h:mm tt}."));
        if (x.Context?.RamUsed is not double used || x.RamTotal is not double total || total <= 0 || used > total * 1.02) return null;
        return used / total >= NearlyFull
            ? new("warn", Lang.T("Memory was nearly full"), Lang.T($"{used:0.0} of {total:0} GB was in use."))
            : new("ok", Lang.T("Memory wasn't full"), Lang.T($"{used:0.0} of {total:0} GB was in use."));
    }

    /// <summary>Hardware, drive and graphics faults Windows logged in the half hour before.</summary>
    private static IEnumerable<Evidence> Signs(CrashInput x)
    {
        var e = x.Crash;
        foreach (var s in x.Signs.Where(s => s.Kind is SignKind.Hardware or SignKind.Drive or SignKind.Graphics && s.Ts <= e.Ts + 120 && s.Ts >= e.Ts - 1800))
        {
            if (s.Kind == SignKind.Hardware && e.Kind is CrashKind.UnexpectedShutdown && s.Detail.Contains("couldn't correct", StringComparison.Ordinal)) continue; // said by the finding itself
            int before = (int)Math.Max(0, (e.Ts - s.Ts) / 60);
            // The detail is kept in English with the sign (see CrashLogReader.SignOf): translated here, where it's shown.
            yield return new("warn", Lang.T(s.Detail), SignWhen(e.Kind, before));
        }
    }

    /// <summary>When a sign was logged, against the problem: "In the same minute as the crash.", "12 minutes before the shut-off."</summary>
    private static string SignWhen(CrashKind kind, int before)
    {
        if (before <= 1)
            return kind switch
            {
                CrashKind.GpuDriverReset => Lang.T("In the same minute as the reset."),
                CrashKind.UnexpectedShutdown => Lang.T("In the same minute as the shut-off."),
                CrashKind.SystemCrash => Lang.T("In the same minute as the blue screen."),
                CrashKind.AppHang => Lang.T("In the same minute as the freeze."),
                _ => Lang.T("In the same minute as the crash."),
            };
        string mins = Wording.Capital(Wording.Minutes(before));
        return kind switch
        {
            CrashKind.GpuDriverReset => Lang.T($"{mins} before the reset."),
            CrashKind.UnexpectedShutdown => Lang.T($"{mins} before the shut-off."),
            CrashKind.SystemCrash => Lang.T($"{mins} before the blue screen."),
            CrashKind.AppHang => Lang.T($"{mins} before the freeze."),
            _ => Lang.T($"{mins} before the crash."),
        };
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
        string versions = x.Crash.Facts?.AppVersion is { } v and not "0.0.0.0" && earlier.All(p => p.Facts?.AppVersion == v) ? " " + Lang.T($"Every time on version {v}.") : "";
        var firstTime = earlier[0].Time;
        return new("pattern", n == 1 ? Lang.T("It has happened once before") : Lang.T($"It has happened {n} times before"),
            (n == 1 ? Lang.T($"On {firstTime:d MMM yyyy}.") : recent == n ? Lang.T($"First on {firstTime:d MMM yyyy}.") : recent == 0 ? Lang.T($"First on {firstTime:d MMM yyyy}, none in the 30 days before this one.")
                : Lang.T($"First on {firstTime:d MMM yyyy}, {recent} of them in the 30 days before this one.")) + versions);
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
        if (change is null) return new("ok", Lang.T("Nothing new was installed"), Lang.T("No new driver or Windows update in the 7 days before."));
        // The change's title is kept as Windows' record gave it (see ChangeLogReader).
        string name = SavedText.ChangeName(change);
        int days = (int)(at.Date - change.Time.Date).TotalDays;
        long ts = TimeUtil.ToUnix(change.Earliest);
        int before = x.Earlier.Count(p => p.Ts < ts && p.Ts >= ts - 30L * 86400), since = x.Earlier.Count(p => p.Ts >= ts) + 1;
        if (before > 0)
        {
            string installed = days switch
            {
                0 => Lang.T("It was installed the same day."),
                1 => Lang.T("It was installed the day before."),
                _ => Lang.T($"It was installed {days} days before."),
            };
            string beforeTimes = Times(before), sinceTimes = Times(since);
            return new("ok", Lang.T($"{name} didn't cause it"), installed + " " + Lang.T($"This had already happened {beforeTimes} in the 30 days before it, and {sinceTimes} since."));
        }
        string title = days switch
        {
            0 => Lang.T($"{name} was installed the same day"),
            1 => Lang.T($"{name} was installed the day before"),
            _ => Lang.T($"{name} was installed {days} days before"),
        };
        // Only the 30 days before it are counted: an older time is said, never "never" (2026-10-26: 5 resets since July were).
        return new("change", title, x.Earlier.LastOrDefault(p => p.Ts < ts) is { } last
            ? Lang.T($"It hadn't happened in the 30 days before it. The time before was on {last.Time:d MMM}.")
            : Lang.T("This never happened before it."));
    }

    /// <summary>How busy it was, as a sentence that follows another: " CPU at 12%, GPU at 4%."</summary>
    private static string Loads(CrashContext c) =>
        (c.CpuLoad, c.GpuLoad) switch
        {
            (double cpu, double gpu) => " " + Lang.T($"CPU at {cpu:0}%, GPU at {gpu:0}%."),
            (double cpu, null) => " " + Lang.T($"CPU at {cpu:0}%."),
            (null, double gpu) => " " + Lang.T($"GPU at {gpu:0}%."),
            _ => "",
        };

    /// <summary>A .NET error's own name without its namespace: "System.NullReferenceException" as "NullReferenceException".</summary>
    private static string ErrorName(string error) => error.LastIndexOf('.') is var i and >= 0 && i < error.Length - 1 ? error[(i + 1)..] : error;

    private static string Apart(long sec) => sec <= 0 ? Lang.T("in the same second") : sec == 1 ? Lang.T("in a second") : Lang.T($"in {sec} seconds");

    /// <summary>A graphics card's own kernel driver, or Windows' graphics core: a crash in one is a graphics crash.</summary>
    internal static bool IsGraphicsFile(string file)
    {
        string f = file.ToLowerInvariant();
        return f.StartsWith("nvlddmkm", StringComparison.Ordinal) || f.StartsWith("nvoglv", StringComparison.Ordinal) || f.StartsWith("amdkmdag", StringComparison.Ordinal)
               || f.StartsWith("atikm", StringComparison.Ordinal) || f.StartsWith("igdkm", StringComparison.Ordinal) || f.StartsWith("igfx", StringComparison.Ordinal)
               || f.StartsWith("dxgkrnl", StringComparison.Ordinal) || f.StartsWith("dxgmms", StringComparison.Ordinal);
    }

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

    /// <summary>"NVIDIA driver" reads as "the NVIDIA driver" in a sentence; names keep their capitals.</summary>
    private static string Lower(string culprit) => culprit.StartsWith("The ", StringComparison.Ordinal) ? "the " + culprit[4..] : $"the {culprit}";

    /// <summary>
    /// A known culprit as it reads in a sentence, in the language in use: "the NVIDIA driver". Each is translated whole,
    /// article and all (another language's article depends on the word), so each is listed in <see cref="TheCulprits"/>.
    /// </summary>
    private static string The(string culprit) => Lang.T(Lower(culprit));

    /// <summary>The known culprits (<see cref="CrashExplainer.Module"/>) as <see cref="The"/> says them, listed for the language packs.</summary>
    private static readonly string[] TheCulprits =
    [
        Lang.Key("the NVIDIA driver"), Lang.Key("the GeForce Experience overlay"), Lang.Key("the AMD driver"), Lang.Key("the Intel graphics driver"),
        Lang.Key("the DirectX"), Lang.Key("the Vulkan"), Lang.Key("the Steam overlay"), Lang.Key("the Discord overlay"), Lang.Key("the RivaTuner overlay"),
        Lang.Key("the Unity engine"), Lang.Key("the Game code"), Lang.Key("the Unreal Engine"), Lang.Key("the .NET runtime"),
        // As a blue screen's file says them ("the " and the plain name: see Plain).
        Lang.Key("the GeForce experience overlay"), Lang.Key("the Unreal engine"),
    ];
}

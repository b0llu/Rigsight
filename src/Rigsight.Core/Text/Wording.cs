using Rigsight.Core.Stability;

namespace Rigsight.Core;

/// <summary>
/// The app's words for the ideas it says on more than one page, kept in one place so every page, notification and
/// export says them the same way (the rule set is WORDING.md at the repo root; a test fails on the forms it retired).
/// </summary>
public static class Wording
{
    // ── How sure: about the cause, never the event ──────────────────────

    public const string Confirmed = "CONFIRMED";
    public const string Likely = "LIKELY";
    public const string Possibly = "POSSIBLY";
    public const string NotEnough = "NOT ENOUGH TO SAY";

    /// <summary>The sentence for a problem nothing on record explains.</summary>
    public const string NotEnoughWhy = "Not enough on record to say why.";

    /// <summary>
    /// The chip, the same in a list and on the opened crash: "CONFIRMED", "LIKELY A POWER CUT", "POSSIBLY HEAT",
    /// "NOT ENOUGH TO SAY". <paramref name="cause"/> is in capitals already.
    /// </summary>
    public static string Chip(Confidence sure, string? cause) => sure switch
    {
        Confidence.Named => Confirmed,
        Confidence.Likely => cause is null ? Likely : $"{Likely} {cause}",
        Confidence.Possible => cause is null ? Possibly : $"{Possibly} {cause}",
        _ => NotEnough,
    };

    /// <summary>
    /// The cause as it reads after the event, with how sure it is: a confirmed one is plain fact (": a .NET error"),
    /// the others take the chip's word (", likely a power cut"). Empty where there is no cause to say; a problem
    /// nothing explains gets its own sentence (" Not enough on record to say why.").
    /// </summary>
    public static string CauseAfter(Confidence sure, string? why) => sure switch
    {
        Confidence.NotEnough => "",
        _ when string.IsNullOrEmpty(why) => "",
        Confidence.Named => $": {why}",
        Confidence.Likely => $", likely {why}",
        _ => $", possibly {why}",
    };

    /// <summary>
    /// The cause as a sentence of its own: "Inside the NVIDIA driver.", "Likely a power cut.", "Possibly heat.",
    /// "Not enough on record to say why."; empty where a confirmed problem has no cause to add.
    /// </summary>
    public static string CauseSentence(Confidence sure, string? why) => sure switch
    {
        Confidence.NotEnough => NotEnoughWhy,
        _ when string.IsNullOrEmpty(why) => "",
        Confidence.Named => $"{Capital(why)}.",
        Confidence.Likely => $"Likely {why}.",
        _ => $"Possibly {why}.",
    };

    /// <summary>How sure, in a cell or after "How sure:": "Confirmed", "Likely a power cut", "Not enough to say".</summary>
    public static string HowSure(Confidence sure, string? cause) => sure switch
    {
        Confidence.Named => "Confirmed",
        Confidence.NotEnough => "Not enough to say",
        _ => Capital(Running(Chip(sure, cause))),
    };

    /// <summary>A chip's capitals as running text; "PC", "GPU" and "CPU" keep theirs.</summary>
    public static string Running(string chip) =>
        string.Join(' ', chip.ToLowerInvariant().Split(' ').Select(w => w is "pc" or "gpu" or "cpu" ? w.ToUpperInvariant() : w));

    // ── The kinds of problem ────────────────────────────────────────────

    /// <summary>All of them together: crashes, freezes, blue screens, sudden shut-offs and graphics driver resets.</summary>
    public const string Problems = "problems";
    public const string NoProblems = "No problems";

    /// <summary>What happened, as a fact with no time: "Dota 2 crashed", "Chrome froze", "The PC shut off suddenly".</summary>
    public static string Event(CrashKind kind, string app) => kind switch
    {
        CrashKind.AppCrash => $"{app} crashed",
        CrashKind.AppHang => $"{app} froze",
        CrashKind.GpuDriverReset => "The graphics driver was reset",
        CrashKind.SystemCrash => "Windows crashed with a blue screen",
        _ => "The PC shut off suddenly",
    };

    /// <summary>A notification's title: "Dota 2 crashed", "Chrome froze", "Blue screen", "Sudden shut-off".</summary>
    public static string Title(CrashKind kind, string app) => kind switch
    {
        CrashKind.AppCrash => $"{app} crashed",
        CrashKind.AppHang => $"{app} froze",
        CrashKind.GpuDriverReset => "Graphics driver reset",
        CrashKind.SystemCrash => "Blue screen",
        _ => "Sudden shut-off",
    };

    /// <summary>The kind's chip: "APP CRASH", "FROZE", "GRAPHICS DRIVER RESET", "BLUE SCREEN", "SUDDEN SHUT-OFF".</summary>
    public static string KindChip(CrashKind kind) => kind switch
    {
        CrashKind.AppCrash => "APP CRASH",
        CrashKind.AppHang => "FROZE",
        CrashKind.GpuDriverReset => "GRAPHICS DRIVER RESET",
        CrashKind.SystemCrash => "BLUE SCREEN",
        _ => "SUDDEN SHUT-OFF",
    };

    /// <summary>The kind as a noun, one and many: "app crash", "freeze", "graphics driver reset", "blue screen", "sudden shut-off".</summary>
    public static string Noun(CrashKind kind, bool many = false) => kind switch
    {
        CrashKind.AppCrash => many ? "app crashes" : "app crash",
        CrashKind.AppHang => many ? "freezes" : "freeze",
        CrashKind.GpuDriverReset => many ? "graphics driver resets" : "graphics driver reset",
        CrashKind.SystemCrash => many ? "blue screens" : "blue screen",
        _ => many ? "sudden shut-offs" : "sudden shut-off",
    };

    /// <summary>How many of a kind, in words: "1 app crash", "2 apps froze", "3 sudden shut-offs".</summary>
    public static string Count(CrashKind kind, int n) => kind switch
    {
        CrashKind.AppHang => n == 1 ? "1 app froze" : $"{n} apps froze",
        _ => $"{n} {Noun(kind, n != 1)}",
    };

    /// <summary>Shut-offs that were most likely the mains, said the same on every page: "1 likely power cut".</summary>
    public static string PowerCuts(int n) => n == 1 ? "1 likely power cut" : $"{n} likely power cuts";

    /// <summary>
    /// Shut-offs that look like the power, said the same on every page: "2 likely power cuts" when every one was likely,
    /// else "3 shut-offs, likely or possibly the power" (a power cut, a dip, the mains or the power supply).
    /// </summary>
    public static string PowerOffs(int likely, int all) =>
        all == likely ? PowerCuts(all) : all == 1 ? "1 shut-off, possibly the power" : likely == 0 ? $"{all} shut-offs, possibly the power" : $"{all} shut-offs, likely or possibly the power";

    // ── Notifications ───────────────────────────────────────────────────

    /// <summary>The last words of every notification.</summary>
    public const string FullPicture = "Click for the full picture.";

    // ── Readings ────────────────────────────────────────────────────────

    /// <summary>Rigsight wasn't recording then (the PC was off, the agent stopped).</summary>
    public const string NotRecorded = "Not recorded";
    /// <summary>It was recording, and the sensor gave nothing.</summary>
    public const string NoReading = "No reading";

    public const string Average = "Average";
    public const string Highest = "Highest";
    public const string Lowest = "Lowest";

    /// <summary>A part's peak, part first: "CPU peak", "GPU peak".</summary>
    public static string Peak(string part) => $"{part} peak";

    /// <summary>The first letter in capitals ("graphics driver resets" as "Graphics driver resets"): every page's one copy.</summary>
    public static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"A, B and C" ("A and B", "A"; nothing for none).</summary>
    public static string JoinAnd(IEnumerable<string> items)
    {
        var list = items as IReadOnlyList<string> ?? items.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    /// <summary>A number of minutes in words: "1 minute", "45 minutes", and from an hour on as every other duration ("1h 30m").</summary>
    public static string Minutes(int minutes) => minutes >= 60 ? Units.Duration(minutes * 60.0) : minutes == 1 ? "1 minute" : $"{minutes} minutes";
}

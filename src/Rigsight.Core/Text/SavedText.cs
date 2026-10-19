using System.Text.RegularExpressions;

namespace Rigsight.Core;

/// <summary>
/// Lines RigSight saves in its history and settings (a change's title, a crash's detail, a repair's summary) are kept in
/// one fixed form, the English they were written in, so its own checks can keep reading them and a change of language
/// never leaves two languages in the file. They are said in the language in use only when shown: each fixed English
/// pattern here is turned into its template (<c>"Copilot updated to 26.01"</c> → <c>"{0} updated to {1}"</c>) and
/// translated with its values as holes. Old lines are shown the same way. A line no pattern knows is shown as saved.
/// </summary>
public static partial class SavedText
{
    /// <summary>A saved form: its pattern, the template it's said with, and how each captured value is shown.</summary>
    private sealed record Pattern(Regex Match, string Template, Func<string, string>[]? Values = null);

    /// <summary>A value from a closed set of the app's own words (a setting's name, "on"), said in the language in use.</summary>
    private static string Word(string s) => Lang.T(s);

    /// <summary>A value that's a name or a number: shown as saved.</summary>
    private static string Raw(string s) => s;

    private static Pattern P(string regex, string template, params Func<string, string>[] values) =>
        new(new Regex(regex, RegexOptions.CultureInvariant), template, values.Length == 0 ? null : values);

    private static readonly Func<string, string> W = Word, R = Raw;

    /// <summary>A lost stretch of history inside a repair's sentence, said in the language in use.</summary>
    private static string Stretch(string s) => Match(Stretches, s) ?? s;

    // The order matters: the more particular forms come before the general ones ("Graphics card removed: X" before "X removed").
    private static readonly Pattern[] Patterns =
    [
        // ── Changes (Inventory, ChangeLogReader, RigsightDb.Changes, StorageChanges, TaskEnded, FanSettings) ──
        P(@"^NVIDIA graphics driver installed$", Lang.Key("NVIDIA graphics driver installed")),
        P(@"^\.NET update$", Lang.Key(".NET update")),
        P(@"^Windows update$", Lang.Key("Windows update")),
        P(@"^Fan speeds changed while the PC was off$", Lang.Key("Fan speeds changed while the PC was off")),
        P(@"^GPU disconnected for 1 minute: (.+)$", Lang.Key("GPU disconnected for 1 minute: {0}")),
        P(@"^GPU disconnected for (\d+) minutes: (.+)$", Lang.Key("GPU disconnected for {0} minutes: {1}")),
        P(@"^Graphics card added: (.+)$", Lang.Key("Graphics card added: {0}")),
        P(@"^Graphics card removed: (.+)$", Lang.Key("Graphics card removed: {0}")),
        P(@"^Drive added: (.+)$", Lang.Key("Drive added: {0}")),
        P(@"^Drive removed: (.+)$", Lang.Key("Drive removed: {0}")),
        P(@"^Processor changed to (.+)$", Lang.Key("Processor changed to {0}")),
        P(@"^Memory changed to (.+)$", Lang.Key("Memory changed to {0}")),
        P(@"^Motherboard changed to (.+)$", Lang.Key("Motherboard changed to {0}")),
        P(@"^BIOS updated to (.+)$", Lang.Key("BIOS updated to {0}")),
        P(@"^Power plan changed to (.+)$", Lang.Key("Power plan changed to {0}"), W),
        P(@"^(Fast Startup|Hardware-accelerated GPU scheduling|Game Mode|Memory integrity) turned on$", Lang.Key("{0} turned on"), W),
        P(@"^(Fast Startup|Hardware-accelerated GPU scheduling|Game Mode|Memory integrity) turned off$", Lang.Key("{0} turned off"), W),
        P(@"^(.+) left startup and came straight back$", Lang.Key("{0} left startup and came straight back")),
        P(@"^(.+) was added to startup and taken off straight away$", Lang.Key("{0} was added to startup and taken off straight away")),
        P(@"^(.+) was turned off at startup and straight back on$", Lang.Key("{0} was turned off at startup and straight back on")),
        P(@"^(.+) was turned on at startup and straight back off$", Lang.Key("{0} was turned on at startup and straight back off")),
        P(@"^(.+) turned on at startup$", Lang.Key("{0} turned on at startup")),
        P(@"^(.+) turned off at startup$", Lang.Key("{0} turned off at startup")),
        P(@"^(.+) now starts with Windows$", Lang.Key("{0} now starts with Windows")),
        P(@"^(.+) no longer starts with Windows$", Lang.Key("{0} no longer starts with Windows")),
        P(@"^PawnIO sensor driver (.+)$", Lang.Key("PawnIO sensor driver {0}")),
        P(@"^(.+) driver (\S+) \(from Windows Update\)$", Lang.Key("{0} driver {1} (from Windows Update)")),
        P(@"^\.NET update (\S+)$", Lang.Key(".NET update {0}")),
        P(@"^Windows update (\S+)$", Lang.Key("Windows update {0}")),
        P(@"^Graphics driver (\S+)$", Lang.Key("Graphics driver {0}")),
        P(@"^(.+) graphics driver (\S+)$", Lang.Key("{0} graphics driver {1}")),
        P(@"^(.+) audio driver (\S+)$", Lang.Key("{0} audio driver {1}")),
        P(@"^(.+) network driver (\S+)$", Lang.Key("{0} network driver {1}")),
        P(@"^(.+) system driver (\S+)$", Lang.Key("{0} system driver {1}")),
        P(@"^(.+) storage controller driver (\S+)$", Lang.Key("{0} storage controller driver {1}")),
        P(@"^(.+) storage driver (\S+)$", Lang.Key("{0} storage driver {1}")),
        P(@"^(.+) USB driver (\S+)$", Lang.Key("{0} USB driver {1}")),
        P(@"^(.+) input driver (\S+)$", Lang.Key("{0} input driver {1}")),
        P(@"^(.+) Bluetooth driver (\S+)$", Lang.Key("{0} Bluetooth driver {1}")),
        P(@"^(.+) device driver (\S+)$", Lang.Key("{0} device driver {1}")),
        P(@"^(.+) ended from Processes$", Lang.Key("{0} ended from Processes")),
        P(@"^([\d.,   ]+) GB added to the (\S+) drive$", Lang.Key("{0} GB added to the {1} drive")),
        P(@"^([\d.,   ]+) GB freed on the (\S+) drive$", Lang.Key("{0} GB freed on the {1} drive")),
        P(@"^(.+) updated to (.+)$", Lang.Key("{0} updated to {1}")),
        P(@"^(.+) updated$", Lang.Key("{0} updated")),
        P(@"^(.+) installed$", Lang.Key("{0} installed")),
        P(@"^(.+) removed$", Lang.Key("{0} removed")),

        // ── Crash details (CrashLogReader) ──
        P(@"^Display driver stopped responding and has recovered$", Lang.Key("Display driver stopped responding and has recovered")),
        P(@"^Noticed at startup (.+)$", Lang.Key("Noticed at startup {0}")),
        P(@"^Found at next startup \((.+)\)$", Lang.Key("Found at next startup ({0})")),
        P(@"^Windows reported that the graphics card stopped answering \(LiveKernelEvent (.+)\)$",
            Lang.Key("Windows reported that the graphics card stopped answering (LiveKernelEvent {0})")),

        // ── History repair (HistoryRepair: a summary is several sentences, each said by itself) ──
        P(@"^Kept (\d+) of (\d+) days in full\.$", Lang.Key("Kept {0} of {1} days in full.")),
        P(@"^(.+) and 1 more stretch couldn't be read\.$", Lang.Key("{0} and 1 more stretch couldn't be read."), Stretch),
        P(@"^(.+) and (\d+) more stretches couldn't be read\.$", Lang.Key("{0} and {1} more stretches couldn't be read."), Stretch, R),
        P(@"^(.+) couldn't be read\.$", Lang.Key("{0} couldn't be read."), Stretch),
        P(@"^Drive (\S+) needs (.+) free for the repair\. It has (.+)\.$", Lang.Key("Drive {0} needs {1} free for the repair. It has {2}.")),
        P(@"^The repair didn't finish: (.+)$", Lang.Key("The repair didn't finish: {0}")),
        P(@"^The history file stayed in use: (.+)$", Lang.Key("The history file stayed in use: {0}")),
        P(@"^Nothing was lost\.$", Lang.Key("Nothing was lost.")),
        P(@"^A few app readings couldn't be read\.$", Lang.Key("A few app readings couldn't be read.")),
        P(@"^The damaged file is kept\.$", Lang.Key("The damaged file is kept.")),
        P(@"^The file was too damaged to read\.$", Lang.Key("The file was too damaged to read.")),
        P(@"^RigSight started a new history today\.$", Lang.Key("RigSight started a new history today.")),
        P(@"^The old file is kept in case a tool can read it\.$", Lang.Key("The old file is kept in case a tool can read it.")),
    ];

    /// <summary>A lost stretch of history, inside a repair's sentences only ("2:10 PM to 3:40 PM on Fri 3 Oct").</summary>
    private static readonly Pattern[] Stretches =
    [
        P(@"^(.+) to (.+) on (.+)$", Lang.Key("{0} to {1} on {2}")),
        P(@"^Everything after (.+)$", Lang.Key("Everything after {0}")),
        P(@"^Everything before (.+)$", Lang.Key("Everything before {0}")),
        P(@"^Some readings$", Lang.Key("Some readings")),
        // Its own entry: inside a repair's sentence another language words it apart from a chart's "{0} to {1}".
        P(@"^(.+?) to (.+)$", Lang.Key("‹stretch›{0} to {1}")),
    ];

    /// <summary>
    /// A saved line in the language in use; exactly as saved with no pack (the English), or when no pattern knows it.
    /// </summary>
    public static string Show(string? saved)
    {
        if (string.IsNullOrEmpty(saved)) return saved ?? "";
        if (!Lang.Translating) return saved;
        return Whole(saved) ?? BySentence(saved) ?? saved;
    }

    /// <summary>A change by its name, as a crash's findings put it in a sentence: an install's name alone, else its title as shown.</summary>
    public static string ChangeName(Stability.SystemChange change) =>
        change.Title.EndsWith(" installed", StringComparison.Ordinal) ? change.Title[..^" installed".Length] : Show(change.Title);

    /// <summary>The whole line: a text saved as it's translated (marked with Lang.Key where it's written), or a pattern.</summary>
    private static string? Whole(string saved)
    {
        string exact = Lang.T(saved);
        if (exact != saved) return exact;
        return Match(Patterns, saved);
    }

    private static string? Match(Pattern[] patterns, string saved)
    {
        foreach (var p in patterns)
        {
            var m = p.Match.Match(saved);
            if (!m.Success) continue;
            var values = new object?[m.Groups.Count - 1];
            for (int i = 0; i < values.Length; i++)
            {
                var show = p.Values is { } v && i < v.Length ? v[i] : R;
                values[i] = show(m.Groups[i + 1].Value);
            }
            return Lang.FC(p.Template, values);
        }
        return null;
    }

    /// <summary>Several sentences in one line (a repair's summary): each said by itself, kept as saved when none is known.</summary>
    private static string? BySentence(string saved)
    {
        var parts = Sentence().Split(saved.Trim());
        if (parts.Length < 2) return null;
        bool any = false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (Whole(parts[i]) is { } said) { parts[i] = said; any = true; }
        }
        return any ? string.Join(" ", parts) : null;
    }

    [GeneratedRegex(@"(?<=\.)\s+(?=[A-Z])")]
    private static partial Regex Sentence();

    /// <summary>Whether a saved line (or each sentence of it) has a pattern here, for the test that every saved form is known.</summary>
    internal static bool IsKnown(string saved) =>
        Patterns.Any(p => p.Match.IsMatch(saved))
        || Sentence().Split(saved.Trim()) is { Length: > 1 } parts && parts.All(s => Patterns.Any(p => p.Match.IsMatch(s)));

    /// <summary>The templates, for the test that every saved form is known.</summary>
    internal static IEnumerable<(Regex Match, string Template)> All => Patterns.Concat(Stretches).Select(p => (p.Match, p.Template));
}

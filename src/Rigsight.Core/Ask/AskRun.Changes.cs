using System.Text.RegularExpressions;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    /// <summary>Days a question about changes covers when it names no time.</summary>
    private const int ChangeDays = 7;

    /// <summary>Words that name a kind of change, not the thing changed ("when did the NVIDIA driver update": "nvidia" is the subject).</summary>
    private static readonly HashSet<string> KindWords = new(StringComparer.Ordinal)
    {
        "driver", "drivers", "windows", "bios", "firmware", "hardware", "startup", "start", "starts", "launch", "launches", "setting", "settings",
        "app", "apps", "program", "programs", "software", "graphics", "gpu", "card", "version", "versions", "things", "stuff", "since", "before",
        "game", "games", "boot", "up", "run", "runs", "running", "part", "parts", "one", "ones", "kb", "also", "else", "so", "far", "lately",
        "exactly", "precisely", "date", "day", "happen", "happened", "only", "just", "about", "them", "those", "these", "cause", "caused", "undo", "revert",
    };

    private static string KindNoun(ChangeKind kind, int n) => kind switch
    {
        ChangeKind.Driver => Count(n, "driver", "drivers"),
        ChangeKind.WindowsUpdate => Count(n, "Windows update", "Windows updates"),
        ChangeKind.Windows => Count(n, "new Windows version", "new Windows versions"),
        ChangeKind.AppInstalled => Count(n, "app installed", "apps installed"),
        ChangeKind.AppRemoved => Count(n, "app removed", "apps removed"),
        ChangeKind.AppUpdated => Count(n, "app update", "app updates"),
        ChangeKind.Startup => Count(n, "startup change", "startup changes"),
        ChangeKind.Hardware => Count(n, "hardware change", "hardware changes"),
        ChangeKind.Firmware => Count(n, "BIOS update", "BIOS updates"),
        ChangeKind.Setting => Count(n, "setting changed", "settings changed"),
        _ => Count(n, "jump in drive space", "jumps in drive space"),
    };

    /// <summary>How much a kind of change matters when only a few fit: what the PC runs on before which apps came and went.</summary>
    private static int Weight(SystemChange c) => c.Kind switch
    {
        ChangeKind.Hardware or ChangeKind.Firmware or ChangeKind.Windows => 5,
        ChangeKind.Driver => c.IsGraphicsDriver ? 5 : 3,
        ChangeKind.WindowsUpdate => 4,
        ChangeKind.Setting or ChangeKind.Startup => 3,
        ChangeKind.AppInstalled or ChangeKind.AppRemoved or ChangeKind.Storage => 2,
        _ => 1,
    };

    /// <summary>The kinds of change the question's words ask for (null: any).</summary>
    private Func<SystemChange, bool>? ChangeKindAsked(out string named)
    {
        string t = _q.Text.ToLowerInvariant();
        named = "";
        if (GraphicsDriverWords().IsMatch(t)) { named = "graphics driver change"; return c => c.IsGraphicsDriver; }
        if (t.Contains("driver")) { named = "driver change"; return c => c.Kind == ChangeKind.Driver || (c.Kind == ChangeKind.WindowsUpdate && c.Title.Contains("driver", StringComparison.OrdinalIgnoreCase)); }
        if (BiosWords().IsMatch(t)) { named = "BIOS update"; return c => c.Kind == ChangeKind.Firmware; }
        if (WindowsUpdateWords().IsMatch(t)) { named = "Windows update"; return c => c.Kind is ChangeKind.WindowsUpdate or ChangeKind.Windows; }
        if (StartupWords().IsMatch(t)) { named = "startup change"; return c => c.Kind == ChangeKind.Startup; }
        if (HardwareWords().IsMatch(t)) { named = "hardware change"; return c => c.Kind == ChangeKind.Hardware; }
        if (SettingWords().IsMatch(t)) { named = "setting change"; return c => c.Kind == ChangeKind.Setting; }
        if (RemovedWords().IsMatch(t)) { named = "app removed"; return c => c.Kind == ChangeKind.AppRemoved; }
        if (_q.App is null && InstalledWords().IsMatch(t)) { named = "install"; return c => c.Kind is ChangeKind.AppInstalled or ChangeKind.Driver or ChangeKind.WindowsUpdate or ChangeKind.Windows; }
        return null;
    }

    private AskAnswer Changes()
    {
        var a = new AskAnswer();
        a.Links.Add(new("Open Timeline", "timeline"));
        if (!_db.HasChanges || _db.ChangesScanned is null)
        {
            a.Lead = "Changes on this PC haven't been read yet. It takes a minute after the first start; ask again shortly.";
            return a;
        }

        string lower = _q.Text.ToLowerInvariant();
        bool when = _q.Last || WhenWords().IsMatch(lower);
        var everything = _db.GetChanges(0, long.MaxValue / 2);
        everything.AddRange(StorageChanges.From(_db.GetDriveDays(0)));
        everything = [.. everything.OrderBy(c => c.Time)];

        // What it's about: a kind of change, an app by name, or whatever other words are left in the question.
        var kind = ChangeKindAsked(out string named);
        List<string> terms = _q.App is { } app
            ? [.. Words(app.ToLowerInvariant()).Where(w => w.Length >= 3)]
            : Words(_q.Rest).Where(w => w.Length >= 3 && !KindWords.Contains(w)).ToList();
        bool Subject(SystemChange c) => terms.All(t => c.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || c.Subject.Contains(t, StringComparison.OrdinalIgnoreCase));
        bool hasSubject = terms.Count > 0 && everything.Any(c => Subject(c) && (kind is null || kind(c)));
        if (terms.Count > 0 && !hasSubject && (when || _q.App is not null))
        {
            string what = _q.App ?? string.Join(" ", terms);
            a.Lead = $"No change to {what} is on record.";
            var installed = _db.GetInventory().FirstOrDefault(i => i.Kind == Inventory.App && terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase)));
            a.Paragraphs.Add(installed is not null
                ? $"{installed.Name} is installed{(installed.Value.Length > 0 ? $" (version {installed.Value})" : "")}, and hasn't been installed, updated or removed since apps started being followed."
                : $"Changes are on record back to {AskTime.DayText(everything.Count > 0 ? everything[0].Time : Now, Now)}; anything from before that isn't.");
            return a;
        }
        bool Match(SystemChange c) => (kind is null || kind(c)) && (!hasSubject || Subject(c));

        // "When did…": the latest one that fits, whenever it was. Otherwise a list for the period.
        bool single = when && (kind is not null || hasSubject);
        var period = _q.Period ?? (single ? AskTime.All(Now) : AskTime.LastDays(ChangeDays, Now));
        if (_q.Period is null && !single) Used = period;
        var found = everything.Where(c => c.Time >= period.From && c.Time < period.To && Match(c)).ToList();

        if (found.Count == 0)
        {
            // A time from before anything was recorded isn't a time when nothing changed.
            if (everything.Count > 0 && period.To <= everything[0].Time)
            {
                a.Lead = $"Nothing is on record {period.Label}: changes are recorded back to {AskTime.DayText(everything[0].Time, Now)}.";
                return a;
            }
            string what = named.Length > 0 ? $"No {named}" : hasSubject ? $"No change to {_q.App ?? string.Join(" ", terms)}" : "Nothing changed";
            a.Lead = $"{what} {(single ? "is on record" : period.Label)}{(named.Length > 0 || hasSubject || single ? "" : ", as far as the records show")}.";
            if (everything.LastOrDefault(c => c.Time < period.From && Match(c)) is { } earlier)
                a.Paragraphs.Add($"The last one before that: {earlier.Title}, {DayWord(earlier.Time)}.");
            return a;
        }

        var effects = Effects(everything);
        if (single)
        {
            var c = found[^1];
            a.Lead = WhenLine(c);
            if (c.Kind != ChangeKind.Storage && !string.IsNullOrEmpty(c.Was)) a.Paragraphs.Add($"Before that it was {c.Was}.");
            if (ChangeEffects.IsMajor(c) && effects.TryGetValue(c.Time.Date, out var effect)) a.Paragraphs.Add(string.Join(" ", effect.Lines));
            var earlier = found.Take(found.Count - 1).Reverse().Take(3).ToList();
            if (earlier.Count > 0)
            {
                a.PointsTitle = "Before that";
                foreach (var e in earlier) a.Points.Add(new($"**{AskTime.DayText(e.Time, Now)}**: {e.Title}"));
            }
            a.Links[0] = new("Open Timeline", "timeline", c.Time.Date);
            Used = AskTime.Day(c.Time, Now); // "any crashes since then?" counts from this day
            a.FollowUps.Add($"Any crashes since {AskTime.DayText(c.Time, Now)}?");
            a.FollowUps.Add($"What else changed {AskTime.Day(c.Time, Now).Label}?");
            return a;
        }

        // The list: a count by kind, then the changes that matter most (an app's routine update last).
        var groups = found.GroupBy(c => c.Kind).OrderByDescending(g => Weight(g.First())).ThenByDescending(g => g.Count()).ToList();
        a.Lead = found.Count == 1
            ? $"One thing changed {period.Label}: **{found[0].Title}**, {AtIn(found[0].Time, period)}."
            : $"**{Count(found.Count, "thing", "things")} {(named == "install" ? "were installed" : "changed")} {period.Label}**: {Join([.. groups.Select(g => KindNoun(g.Key, g.Count()))])}.";
        if (found.Count > 1)
        {
            const int shown = 7;
            var updates = found.Where(c => c.Kind == ChangeKind.AppUpdated).ToList();
            bool fold = updates.Count > 3 && found.Count > shown;
            var lines = found.Where(c => !fold || c.Kind != ChangeKind.AppUpdated)
                .OrderByDescending(Weight).ThenByDescending(c => c.Time).Take(fold ? shown - 1 : shown).OrderByDescending(c => c.Time).ToList();
            foreach (var c in lines)
                a.Points.Add(new($"**{(period.IsDay ? TimeText(c) : AskTime.DayText(c.Time, Now))}**: {c.Title}{(c.Kind != ChangeKind.Storage && !string.IsNullOrEmpty(c.Was) ? $" (was {c.Was})" : "")}",
                    ChangeEffects.IsMajor(c) ? AskTone.Warn : AskTone.Neutral));
            if (fold)
            {
                var names = updates.Select(c => c.Title.Split(" updated")[0]).Distinct().Take(4).ToList();
                a.Points.Add(new($"**{updates.Count} apps updated**: {string.Join(", ", names)}{(updates.Count > names.Count ? " and others" : "")}"));
            }
            int left = found.Count - lines.Count - (fold ? updates.Count : 0);
            if (left > 0) a.Note = $"{Count(left, "more is", "more are")} on the Timeline.";
        }

        // What a big change did, where there's enough on both sides of it to say.
        if (found.Where(ChangeEffects.IsMajor).OrderByDescending(c => c.Time).FirstOrDefault(c => effects.ContainsKey(c.Time.Date)) is { } major)
            a.Paragraphs.Add($"After {major.Title}: {string.Join(" ", effects[major.Time.Date].Lines).ToLowerFirstWord()}");

        if (period.IsDay) a.Links[0] = new("Open Timeline", "timeline", period.From);
        a.FollowUps.Add($"Any crashes {period.Label}?");
        if (found.FirstOrDefault(c => c.IsGraphicsDriver) is not null) a.FollowUps.Add("When did my graphics driver last change?");
        return a;
    }

    private static string TimeText(SystemChange c) => c.Kind == ChangeKind.Storage ? "That day" : (c.IsApproximate ? "~" : "") + Clock(c.Time);

    /// <summary>"NVIDIA graphics driver 616.92 was installed on Tue 30 Sep at 7:15 PM."</summary>
    private string WhenLine(SystemChange c)
    {
        string day = DayWord(c.Time);
        return c.Kind switch
        {
            ChangeKind.Driver or ChangeKind.WindowsUpdate => $"**{Thing(c)}** was installed {day} at {Clock(c.Time)}.",
            ChangeKind.Storage => $"**{c.Title}** {day}.",
            _ when c.IsApproximate => $"**{c.Title}** {day}, between {Clock(c.Earliest)} and {Clock(c.Time)}.",
            _ => $"**{c.Title}** {day} at {Clock(c.Time)}.",
        };
    }

    /// <summary>What was different after each big change (the Timeline's "since this change"), by day.</summary>
    private Dictionary<DateTime, ChangeEffect> Effects(List<SystemChange> changes)
    {
        try
        {
            var problems = _db.GetCrashes(0, long.MaxValue / 2).Where(IsPcLevel).Select(e => (e.Time, e.Kind)).ToList();
            return ChangeEffects.Of(changes, _db.GetHeatDays(0, long.MaxValue / 2), _db.GetSystemDays(0, long.MaxValue / 2) ?? [], problems, NameOf, Now);
        }
        catch (Exception ex)
        {
            Log.Error("ask", ex);
            return [];
        }
    }

    private static List<string> Words(string text) => [.. WordPattern().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"[a-z0-9][a-z0-9.+#-]*")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^when\b|\bwhen (did|was|were|has|have)\b|\bwhat (day|date|time) (did|was)\b|\bhow long ago\b")]
    private static partial Regex WhenWords();

    [GeneratedRegex(@"\b(graphics|gpu|nvidia|amd|radeon|geforce|display|video)( card)? drivers?\b")]
    private static partial Regex GraphicsDriverWords();

    [GeneratedRegex(@"\b(bios|firmware|uefi)\b")]
    private static partial Regex BiosWords();

    [GeneratedRegex(@"\bwindows( \d+)? (updat\w*|install\w*|version)|\b(updat\w*|patch\w*) (for|of|to|from) windows\b|\bwindows last updat\w*|\bkb\d+")]
    private static partial Regex WindowsUpdateWords();

    [GeneratedRegex(@"\b(startup|start(s|ing)? with windows|start(s)? (up )?automatically|launch(es)? at (startup|boot|start)|at boot|auto ?start)\b")]
    private static partial Regex StartupWords();

    [GeneratedRegex(@"\b(hardware|new part|parts|swapped|replaced)\b")]
    private static partial Regex HardwareWords();

    [GeneratedRegex(@"\bsettings?\b")]
    private static partial Regex SettingWords();

    [GeneratedRegex(@"\b(uninstall\w*|removed?|deleted|gone|missing)\b")]
    private static partial Regex RemovedWords();

    [GeneratedRegex(@"\b(install\w*|new (apps?|programs?|software|games?))\b")]
    private static partial Regex InstalledWords();
}

internal static partial class AskTextMore
{
    /// <summary>Lower-cases a sentence's first word when it's an ordinary one ("In Rematch, …" after "After the driver: ").</summary>
    public static string ToLowerFirstWord(this string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && char.IsLower(s[1]) && (s.StartsWith("In ", StringComparison.Ordinal) || s.StartsWith("No ", StringComparison.Ordinal) || s.StartsWith("The ", StringComparison.Ordinal))
            ? char.ToLowerInvariant(s[0]) + s[1..] : s;
}

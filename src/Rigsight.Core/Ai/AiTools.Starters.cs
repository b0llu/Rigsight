using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ai;

/// <summary>
/// A question to start a new chat with, the page whose records answer it (for its coloured dot), and its topic (the
/// question without its figures, see <see cref="AiStarterLog.TopicOf"/>).
/// </summary>
public sealed record AiStarter(string Text, string Page)
{
    public string Topic => AiStarterLog.TopicOf(Text);
}

/// <summary>Home's lines a new chat's starters are drawn from: today's, and the day before's.</summary>
public sealed record StarterLines(IReadOnlyList<Insight> Today, IReadOnlyList<Insight> Before);

public sealed partial class AiTools
{
    /// <summary>
    /// Questions to start a new chat with, from what this PC did (the person's call, 2026-10-24: "make these questions
    /// dynamic based on the data of PC"). Home's lines that raise a question (<see cref="Insight.Ask"/>) and the records'
    /// own (this week's shut-off or blue screen, driver resets, the game played most, a drive filling up), ranked by how
    /// much they matter, then how recent: today's over the day before's. A question already asked in a chat doesn't
    /// show, one just picked rests three days, one shown day after day and never picked sinks. Four at most, each from
    /// a different page; general ones in this PC's parts' names fill the rest. Only pages switched on for answers and
    /// open on the plan (<paramref name="open"/>).
    /// </summary>
    public IReadOnlyList<AiStarter> Starters(DateTime now, Func<string, bool>? open = null, StarterLines? lines = null,
        IReadOnlyCollection<string>? asked = null, AiStarterLog? log = null)
    {
        var found = new List<(AiStarter Starter, double Score)>();
        bool On(string page) => settings.Ai.Sends(page) && (open?.Invoke(page) ?? true);
        (string? Cpu, string? Gpu, string? Ram) parts = default;

        // Home's lines: how much each matters (its rank on Home, a worry above a fact), today's ahead of the day before's.
        void From(IReadOnlyList<Insight>? list, double recent)
        {
            foreach (var i in list ?? [])
                if (i.Ask is { Length: > 0 } ask && i.AskPage.Length > 0)
                    found.Add((new(ask, i.AskPage), i.Priority + recent + i.Tone switch { InsightTone.Hot => 15, InsightTone.Warn => 8, InsightTone.Good => -10, _ => 0 }));
        }
        From(lines?.Today, 10);
        From(lines?.Before, 0);

        try
        {
            using var db = (dbPath is null ? RigsightDb.OpenReader() : RigsightDb.OpenReader(dbPath));
            if (db is not null)
            {
                var week = db.GetCrashes(TimeUtil.ToUnix(now.Date.AddDays(-6)), TimeUtil.ToUnix(now) + 1);
                if (week.Where(c => c.Kind is CrashKind.UnexpectedShutdown or CrashKind.SystemCrash).MaxBy(c => c.Ts) is { } down)
                {
                    var at = TimeUtil.FromUnix(down.Ts);
                    string when = at.Date == now.Date ? Lang.T($"at {TimeText.Time(at)} today")
                        : at.Date == now.Date.AddDays(-1) ? Lang.T($"at {TimeText.Time(at)} yesterday")
                        : Lang.T($"on {TimeText.At(at, "dddd")}");
                    found.Add((new(down.Kind == CrashKind.SystemCrash ? Lang.T($"Why did my PC blue-screen {when}?") : Lang.T($"Why did my PC shut off {when}?"), AiPages.Crashes),
                        at.Date == now.Date ? 115 : at.Date == now.Date.AddDays(-1) ? 100 : 85));
                }
                int resets = week.Count(c => c.Kind == CrashKind.GpuDriverReset);
                if (resets > 0 && On(AiPages.Crashes))
                    found.Add((new(resets == 1 ? Lang.T("Did this week's graphics driver reset come from heat?") : Lang.T($"Did this week's {resets} graphics driver resets come from heat?"), AiPages.Temperatures), 80 + Math.Min(resets, 5) * 2));
                if (PlayedIn(db, now.Date.AddDays(-6), now.Date).FirstOrDefault() is { Game: { } game })
                    found.Add((new(Lang.T($"How hard does {game.Name} push my graphics card?"), AiPages.Games), 45));
                parts = Parts(db);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("ask", $"Starters from the records couldn't be read ({ex.Message}); the general ones show"); }
        // A drive filling up matters more the fuller it is: 70% is worth a question, 95% ranks with a warning.
        if (Fullest() is { } drive)
            found.Add((new(Lang.T($"What's filling my {drive.Letter} drive ({drive.Percent}% full)?"), AiPages.Storage), 40 + (drive.Percent - 70) * 2));

        // Already asked (in any chat, figures aside), or just picked: not again. Shown for days and never picked: lower.
        var askedTopics = (asked ?? []).Select(AiStarterLog.TopicOf).ToHashSet();
        double Score(AiStarter s, double score)
        {
            if (log?.Get(s.Topic) is not { } seen) return score;
            if (seen.Picked is { } picked && now - picked < TimeSpan.FromDays(3)) return double.NegativeInfinity;
            return score - Math.Min(40, Math.Max(0, seen.Days - 2) * 10);
        }
        var list = new List<AiStarter>();
        foreach (var (s, _) in found
            .Where(f => On(f.Starter.Page) && !askedTopics.Contains(f.Starter.Topic))
            .Select(f => (f.Starter, Score: Score(f.Starter, f.Score)))
            .Where(f => !double.IsNegativeInfinity(f.Score))
            .OrderByDescending(f => f.Score))
        {
            if (list.Count >= 4) break;
            if (list.Any(x => x.Page == s.Page || x.Topic == s.Topic)) continue;
            list.Add(s);
        }

        // The general ones, for what the records had nothing on, in this PC's parts' names where they're known.
        var (cpu, gpu, ram) = parts;
        foreach (var (text, page) in new[]
        {
            (Lang.T("Why did my PC turn off by itself?"), AiPages.Crashes),
            (cpu is null ? Lang.T("Are my temperatures normal for my parts?") : Lang.T($"Are my temperatures normal for a {cpu}?"), AiPages.Temperatures),
            (gpu is null ? Lang.T("Which game is hardest on my PC?") : Lang.T($"Which game is hardest on my {gpu}?"), AiPages.Games),
            (Lang.T("Did anything change before my crashes started?"), AiPages.Timeline),
            (Lang.T("What's taking up the most space on my drives?"), AiPages.Storage),
            (ram is null ? Lang.T("Is my PC's memory enough for what I run?") : Lang.T($"Is {ram} of memory enough for what I run?"), AiPages.Memory),
            (Lang.T("Are my fans keeping my PC cool enough?"), AiPages.Fans),
            (Lang.T("Is my internet connection stable?"), AiPages.Network),
        })
        {
            if (list.Count >= 4) break;
            var s = new AiStarter(text, page);
            if (list.Any(x => x.Page == page) || !On(page) || askedTopics.Contains(s.Topic)) continue;
            list.Add(s);
        }
        return list;
    }

    /// <summary>The processor, main graphics card and memory, as people say them; null for any not read yet.</summary>
    private static (string? Cpu, string? Gpu, string? Ram) Parts(RigsightDb db)
    {
        var items = db.GetInventory();
        string? cpu = items.FirstOrDefault(i => i.Kind == Inventory.Cpu)?.Value is { Length: > 0 } c ? ShortCpu(c) : null;
        string? gpu = OllamaPicks.MainCard(items.Where(i => i.Kind == Inventory.Gpu).Select(i => i.Name)) is { } g ? OllamaPicks.ShortCard(g) : null;
        string? ram = items.FirstOrDefault(i => i.Kind == Inventory.Ram)?.Value is { Length: > 0 } r ? r : null;
        return (cpu, gpu, ram);
    }

    /// <summary>
    /// The PC in a line, for the new chat's heading: "Ryzen 7 5700X3D · RTX 3080 Ti · 16 GB", as people say the parts
    /// (the maker's words and "8-Core Processor" dropped). Empty before the parts were first read.
    /// </summary>
    public string PcLine()
    {
        try
        {
            using var db = dbPath is null ? RigsightDb.OpenReader() : RigsightDb.OpenReader(dbPath);
            if (db is null) return "";
            var (cpu, gpu, ram) = Parts(db);
            return string.Join(" · ", new[] { cpu, gpu, ram }.OfType<string>());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("ask", $"The parts line couldn't be read: {ex.Message}"); return ""; }
    }

    /// <summary>"Ryzen 7 5700X3D" from "AMD Ryzen 7 5700X3D 8-Core Processor", "Core i7-12700K" from "12th Gen Intel(R) Core(TM) i7-12700K".</summary>
    internal static string ShortCpu(string name)
    {
        var n = System.Text.RegularExpressions.Regex.Replace(name, @"\((R|TM)\)|\b\d+(th|st|nd|rd) Gen\b|\bAMD\b|\bIntel\b|\b\d+-Core\b|\bProcessor\b|\bCPU\b|@.*$", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ").Trim();
        return n.Length > 0 ? n : name.Trim();
    }

    /// <summary>The fixed drive most full, when it's over 70%: its letter and how full.</summary>
    private static (string Letter, int Percent)? Fullest()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
                .Select(d => (Letter: d.Name.TrimEnd('\\'), Percent: (int)Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize)))
                .Where(d => d.Percent >= 70).OrderByDescending(d => d.Percent).Cast<(string, int)?>().FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

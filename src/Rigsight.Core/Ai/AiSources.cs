using System.Globalization;
using System.Text.RegularExpressions;

namespace Rigsight.Core.Ai;

/// <summary>One web result a search returned: its number in the answer ("w3"), title, address, and date when the page has one.</summary>
public sealed record WebSource(int N, string Title, string Url, string? Date);

/// <summary>
/// Where a paragraph's facts came from, as its pill lists them: one of RigSight's records (opening its page) or a web
/// page (opening the site).
/// </summary>
public sealed record AiSource(bool IsApp, string Site, string Title, string? Date, string? Page, string? Url, string? Focus = null)
{
    /// <summary>The letter on a web page's round mark ("t" for tomshardware.com).</summary>
    public string Mark => IsApp ? "" : Site.Length > 0 ? char.ToUpperInvariant(Site[0]).ToString() : "?";
}

/// <summary>
/// The sources under an answer's paragraphs: the relay numbers each web result ("[w1] Title (url), published …"), the
/// model ends a paragraph with "[src: w1 crashes]", and only what this answer really looked at or found is kept, so a
/// made-up source never shows.
/// </summary>
public static partial class AiSources
{
    public const int MostPerParagraph = 4;

    [GeneratedRegex(@"^\[w(?<n>\d+)\] (?<title>.+) \((?<url>https?://[^)\s]+)\)(?:, published (?<date>\d{4}-\d{2}-\d{2}))?\s*$", RegexOptions.Multiline)]
    private static partial Regex WebLine();

    /// <summary>A "[src: …]" anywhere in a line, and one still being typed at the very end of what has come so far.</summary>
    [GeneratedRegex(@"[ \t]*\[\s*src\s*:\s*(?<what>[^\]\n]*)\]", RegexOptions.IgnoreCase)]
    public static partial Regex Marker();

    [GeneratedRegex(@"[ \t]*\[(?:s(?:r(?:c(?:\s*:[^\]\n]*)?)?)?)?$", RegexOptions.IgnoreCase)]
    public static partial Regex Unfinished();

    /// <summary>The numbered results in a search's text, as the relay writes them.</summary>
    public static List<WebSource> ParseWeb(string text) =>
        [.. WebLine().Matches(text).Select(m => new WebSource(int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture), m.Groups["title"].Value.Trim(),
            m.Groups["url"].Value, m.Groups["date"].Success ? m.Groups["date"].Value : null))];

    /// <summary>The look-ups each word in a marker stands for.</summary>
    private static string[] Tools(string word) => word.ToLowerInvariant() switch
    {
        "crashes" or "crash" => ["get_crashes"],
        "temps" or "temperatures" or "heat" => ["get_temperatures"],
        "fans" => ["get_fans"],
        "apps" => ["get_apps"],
        "games" or "game" => ["list_games", "get_game"],
        "memory" => ["get_memory"],
        "storage" or "drives" or "scan" => ["get_storage", "get_drive_scan"],
        "internet" or "network" => ["get_network"],
        "changes" => ["get_changes"],
        "report" => ["get_report"],
        "moment" => ["get_moment"],
        "running" or "running-now" => ["get_running_now"],
        "parts" or "pc" => ["get_pc_info"],
        "on-times" or "on" => ["get_on_times"],
        "today" or "home" => ["get_today"],
        "sensors" => ["get_sensors_now"],
        "rewind" => ["get_rewind"],
        "dashboards" or "dashboard" => ["get_dashboard"],
        _ => [],
    };

    /// <summary>What a marker's words stand for in this answer; the rest (a record not looked at, a result not found) is left out.</summary>
    /// <param name="log">Say in the log what was left out (the finished answer only: one still arriving says it on every update).</param>
    public static List<AiSource> Resolve(string what, IReadOnlyList<AiLookUp> lookUps, bool log = true)
    {
        var web = lookUps.SelectMany(l => l.Web ?? []).GroupBy(w => w.N).ToDictionary(g => g.Key, g => g.First());
        var found = new List<AiSource>();
        foreach (var word in what.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AiSource? source = null;
            if (word.Length > 1 && (word[0] is 'w' or 'W') && int.TryParse(word.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                source = web.TryGetValue(n, out var w) ? Web(w) : null;
            else
            {
                var tools = Tools(word);
                source = lookUps.Where(l => tools.Contains(l.Tool)).Select(Record).OfType<AiSource>().FirstOrDefault();
            }
            if (source is null) { if (log) Log.Write("ask", $"[src: {word}] left out: not looked at or found in this answer"); continue; }
            if (!found.Contains(source)) found.Add(source);
            if (found.Count >= MostPerParagraph) break;
        }
        return found;
    }

    /// <summary>Everything this answer looked at and found: the pill on its last paragraph when the model named none.</summary>
    public static List<AiSource> All(IReadOnlyList<AiLookUp> lookUps)
    {
        var all = new List<AiSource>();
        foreach (var l in lookUps)
        {
            if (l.Web is { } results) all.AddRange(results.Take(3).Select(Web));
            else if (Record(l) is { } r && !all.Contains(r)) all.Add(r);
        }
        return all;
    }

    private static AiSource Web(WebSource w)
    {
        string site = Uri.TryCreate(w.Url, UriKind.Absolute, out var uri) ? uri.Host : w.Url;
        if (site.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) site = site[4..];
        string? date = DateTime.TryParseExact(w.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) : null;
        return new AiSource(false, site, w.Title, date, null, w.Url);
    }

    /// <summary>One of RigSight's records, in product words, with the page that shows it.</summary>
    public static AiSource? Record(AiLookUp l)
    {
        string range = Range(l);
        (string Page, string Name, string Title)? r = l.Tool switch
        {
            "get_report" => ("reports", Lang.T("Reports"), Lang.T($"Report, {range}")),
            "get_crashes" => ("crashes", Lang.T("Crashes"), Lang.T($"Crashes, {range}")),
            "get_changes" => ("timeline", Lang.T("Timeline"), Lang.T($"Changes, {range}")),
            "list_games" => ("games", Lang.T("Games"), Lang.T("Your games")),
            "get_game" => ("games", Lang.T("Games"), (string?)l.Args["name"] ?? Lang.T("A game")),
            "get_temperatures" => ("temperatures", Lang.T("Temperatures"), Lang.T($"Temperatures, {range}")),
            "get_fans" => ("fans", Lang.T("Fans"), Lang.T($"Fans, {range}")),
            "get_apps" => ("apps", Lang.T("Apps"), Lang.T($"Apps, {range}")),
            "get_memory" => ("memory", Lang.T("Memory"), Lang.T($"Memory, {range}")),
            "get_storage" => ("storage", Lang.T("Storage"), Lang.T("Your drives")),
            "get_drive_scan" => ("storage", Lang.T("Storage"), (string?)l.Args["drive"] is { Length: > 0 } d ? Lang.T($"Scan of {d.Trim().TrimEnd(':').ToUpperInvariant()}:") : Lang.T("A drive's scan")),
            "get_network" => ("network", Lang.T("Network"), Lang.T($"Internet, {range}")),
            "get_on_times" => ("reports", Lang.T("Reports"), Lang.T($"When it was on, {range}")),
            "get_running_now" => ("processes", Lang.T("Processes"), Lang.T("Running now")),
            "get_moment" => ("timeline", Lang.T("Timeline"), Moment(l)),
            "get_pc_info" => ("", Lang.T("Your PC's parts"), Lang.T("CPU, graphics card, motherboard, RAM")),
            "get_today" => ("home", Lang.T("Home"), Lang.T("Today so far")),
            "get_sensors_now" => ("sensors", Lang.T("All sensors"), Lang.T("Every reading now")),
            // Rewind and dashboards have no page of their own to open at (a rewind plays; a dashboard is found by its id).
            "get_rewind" => ("", Lang.T("Rewind"), Lang.T("Your rewind")),
            "get_dashboard" => ("", Lang.T("Your dashboards"), (string?)l.Args["name"] ?? Lang.T("Your dashboards")),
            _ => null,
        };
        // A look-up without dates ("Report, ") says just its name.
        // Where it opens, said in the list under what it was ("Open Temperatures").
        return r is { } x ? new AiSource(true, x.Name, x.Title.TrimEnd(' ', ','), x.Page.Length > 0 ? Lang.T($"Open {x.Name}") : null, x.Page.Length > 0 ? x.Page : null, null, Days(l)) : null;
    }

    /// <summary>"Around 8:57 AM, 23 Oct" for a look at one moment (the model writes it "2026-10-23 08:57").</summary>
    private static string Moment(AiLookUp l)
    {
        string? at = (string?)l.Args["time"];
        foreach (var f in new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm:ss" })
            if (DateTime.TryParseExact(at, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return Lang.T($"Around {t.ToString(TimeText.Fit("h:mm tt"), CultureInfo.CurrentCulture)}, {t:d MMM}");
        return Lang.T("Around one moment");
    }

    /// <summary>"15 Oct – 22 Oct", or one day.</summary>
    /// <summary>
    /// The days a look-up read, "2026-10-16..2026-10-22", for its page to open at them (Temperatures, Reports, Crashes);
    /// null when it read no days. <see cref="ParseDays"/> reads it back.
    /// </summary>
    public static string? Days(AiLookUp l)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!DateTime.TryParseExact((string?)l.Args["from"], "yyyy-MM-dd", inv, DateTimeStyles.None, out var from)
            || !DateTime.TryParseExact((string?)l.Args["to"], "yyyy-MM-dd", inv, DateTimeStyles.None, out var to)) return null;
        if (to < from) (from, to) = (to, from);
        return $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}";
    }

    public static (DateTime From, DateTime To)? ParseDays(string? days)
    {
        var inv = CultureInfo.InvariantCulture;
        if (days?.Split("..") is not [var a, var b]
            || !DateTime.TryParseExact(a, "yyyy-MM-dd", inv, DateTimeStyles.None, out var from)
            || !DateTime.TryParseExact(b, "yyyy-MM-dd", inv, DateTimeStyles.None, out var to)) return null;
        return (from, to);
    }

    public static string Range(AiLookUp l)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!DateTime.TryParseExact((string?)l.Args["from"], "yyyy-MM-dd", inv, DateTimeStyles.None, out var from)
            || !DateTime.TryParseExact((string?)l.Args["to"], "yyyy-MM-dd", inv, DateTimeStyles.None, out var to)) return "";
        if (to < from) (from, to) = (to, from);
        return from == to ? Lang.T($"{from:d MMM}") : Lang.T($"{from:d MMM} – {to:d MMM}");
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using Rigsight.Core.Data;

namespace Rigsight.Core.Games;

/// <summary>How a download ended in Steam's log.</summary>
public enum DownloadEnd
{
    /// <summary>"finished update": all of it came down and went in.</summary>
    Finished,
    /// <summary>Cancelled and never picked up again, or the game was uninstalled partway.</summary>
    Stopped,
    /// <summary>The log has no end for it: still going, or Steam's log moved on.</summary>
    Unknown,
}

/// <summary>
/// One download Steam made: which game, a new install or an update, how much it had to download, and when it started and
/// ended, as Steam's own download log says. Ended is when it finished, stopped, or (with no end) Steam's last line about it.
/// </summary>
public sealed record GameDownload(string AppId, string? Name, DateTime Started, DateTime Ended, long Bytes, bool NewInstall, DownloadEnd How = DownloadEnd.Unknown);

/// <summary>
/// What Steam downloaded, from its own record of every download (logs\content_log.txt, and content_log.previous.txt from
/// before it last started a new one: together months of history). The network records only see "Steam, 133 GB"; this
/// names the game for certain (the user, 2026-10-30: "i want the AI to be sure about what was downloaded"). Other
/// launchers keep nothing like it on this PC: Epic's log is gone after about three days, EA, Ubisoft and Battle.net have
/// no download record at all (checked on the user's PC the same day).
/// </summary>
public static partial class SteamDownloads
{
    /// <summary>
    /// Steam's downloads that ran during [from, to), oldest first: the ones the agent saved in the history (<paramref
    /// name="db"/>, kept after Steam's log has moved past them) together with what Steam's log says now. The log's word
    /// wins for a download in both (it may have ended since), and a name either one has is kept.
    /// </summary>
    public static IReadOnlyList<GameDownload> Read(RigsightDb? db, DateTime from, DateTime to)
    {
        var all = new Dictionary<(string, DateTime), GameDownload>();
        if (db is not null)
            try { foreach (var d in db.GetSteamDownloads(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to))) all[(d.AppId, d.Started)] = d; }
            catch (Exception ex) { Log.Write("games", $"saved Steam downloads couldn't be read ({ex.GetType().Name}: {ex.Message})"); }
        foreach (var d in ReadThisPc().Where(d => d.Started < to && d.Ended >= from))
            all[(d.AppId, d.Started)] = d.Name is null && all.TryGetValue((d.AppId, d.Started), out var saved) ? d with { Name = saved.Name } : d;
        return [.. all.Values.OrderBy(d => d.Started)];
    }

    /// <summary>Steam's downloads that ran during [from, to), from its log alone.</summary>
    public static IReadOnlyList<GameDownload> Read(DateTime from, DateTime to) => [.. ReadThisPc().Where(d => d.Started < to && d.Ended >= from)];

    /// <summary>Steam's log files on this PC, older first (none when Steam or its log isn't there).</summary>
    public static IReadOnlyList<string> LogFiles()
    {
        try
        {
            foreach (var root in GameLibrary.SteamRootsOnThisPc())
            {
                var logs = new[] { "content_log.previous.txt", "content_log.txt" }.Select(f => Path.Combine(root, "logs", f)).Where(File.Exists).ToList();
                if (logs.Count > 0) return logs;
            }
        }
        catch (Exception ex) { Log.Write("games", $"Steam's folder couldn't be read ({ex.GetType().Name}: {ex.Message})"); }
        return [];
    }

    /// <summary>Every download in Steam's log on this PC, each named by its game while Steam still has it installed.</summary>
    public static IReadOnlyList<GameDownload> ReadThisPc()
    {
        try
        {
            var logs = LogFiles();
            if (logs.Count == 0) return [];
            string root = Path.GetDirectoryName(Path.GetDirectoryName(logs[0])!)!;
            var names = GameLibrary.ReadSteam([root]).GroupBy(g => g.Key["steam:".Length..]).ToDictionary(g => g.Key, g => g.First().Name);
            return Parse(logs.SelectMany(ReadShared), names);
        }
        catch (Exception ex) { Log.Write("games", $"Steam's download log couldn't be read ({ex.GetType().Name}: {ex.Message})"); }
        return [];
    }

    /// <summary>A log Steam is writing to: read without locking it.</summary>
    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>
    /// The downloads in Steam's log lines. A download starts at an app's first "update started" with something to
    /// download ("download 0/148285013680") and goes on through the progress lines that repeat it. "update canceled"
    /// pauses it (Steam pauses downloads while a game is played): more "update started" lines later are the same
    /// download going on. It ends at "finished update", at "finished uninstall" (removed partway), or, cancelled and never
    /// picked up again, at its cancel. "reuse 0/0" means nothing of the game was there before: a new install. One with
    /// nothing to download (a check of the files) isn't a download. With no end, it ends at Steam's last line about it.
    /// </summary>
    internal static List<GameDownload> Parse(IEnumerable<string> lines, IReadOnlyDictionary<string, string> names)
    {
        var done = new List<GameDownload>();
        var open = new Dictionary<string, (GameDownload Download, DateTime? Cancelled)>();
        var seen = new Dictionary<string, DateTime>();
        // Which job Steam is running for each app: the game itself ("App update changed") or its shader cache ("Shader
        // update changed", a few KB of precompiled graphics, not the game: a 3 KB one on 18 Oct read as a Risk of Rain 2
        // install that never ended).
        var shader = new HashSet<string>();
        foreach (var line in lines)
        {
            var m = Entry().Match(line);
            if (!m.Success || !DateTime.TryParseExact(m.Groups["at"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
            string app = m.Groups["app"].Value, what = m.Groups["what"].Value;
            // Only download lines count as the download still going: Steam also writes about an app for months after
            // (update checks, "scheduler update"), which would stretch a download with no end over all of that.
            if (what.StartsWith("update started", StringComparison.Ordinal) || what.Contains("Downloading", StringComparison.Ordinal)
                || what.Contains("Staging", StringComparison.Ordinal) || what.Contains("Committing", StringComparison.Ordinal)) seen[app] = at;
            if (what.StartsWith("Shader update changed : Running Update", StringComparison.Ordinal)) shader.Add(app);
            else if (what.StartsWith("App update changed : Running Update", StringComparison.Ordinal)) shader.Remove(app);
            if (Started().Match(what) is { Success: true } s)
            {
                if (shader.Contains(app)) continue;
                long bytes = long.Parse(s.Groups["total"].Value, CultureInfo.InvariantCulture);
                if (bytes <= 0) continue;
                if (open.TryGetValue(app, out var going))
                {
                    // Picked up again after a pause, or more progress: the same download. A fresh one (nothing of it
                    // downloaded yet) after a cancel is a new try of the same thing: still the same download.
                    open[app] = (going.Download with { Bytes = Math.Max(bytes, going.Download.Bytes) }, null);
                    continue;
                }
                open[app] = (new GameDownload(app, names.GetValueOrDefault(app), at, at, bytes, s.Groups["reuse"].Value == "0"), null);
            }
            else if (!open.TryGetValue(app, out var going)) continue;
            else if (what.StartsWith("finished update", StringComparison.Ordinal))
            {
                open.Remove(app);
                done.Add(going.Download with { Ended = at, How = DownloadEnd.Finished });
            }
            else if (what.StartsWith("finished uninstall", StringComparison.Ordinal))
            {
                open.Remove(app);
                done.Add(going.Download with { Ended = going.Cancelled ?? at, How = DownloadEnd.Stopped });
            }
            // Cancelled, or the job stopped with nothing finished ("App update changed : None" with no "finished update"
            // before it): paused, and stopped for good unless more of it comes later.
            else if (what.StartsWith("update canceled", StringComparison.Ordinal)
                || (what.StartsWith("App update changed : None", StringComparison.Ordinal) && going.Cancelled is null)) open[app] = (going.Download, at);
        }
        // No end in the log: a cancel never picked up again ended it; otherwise Steam's last line about the app.
        foreach (var (app, (download, cancelled)) in open)
            done.Add(download with { Ended = cancelled ?? seen[app], How = cancelled is null ? DownloadEnd.Unknown : DownloadEnd.Stopped });
        return [.. done.OrderBy(d => d.Started)];
    }

    [GeneratedRegex(@"^\[(?<at>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] AppID (?<app>\d+) (?<what>.*)$")]
    private static partial Regex Entry();

    [GeneratedRegex(@"^update started : download \d+/(?<total>\d+), store \d+/\d+, reuse \d+/(?<reuse>\d+)")]
    private static partial Regex Started();
}

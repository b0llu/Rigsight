using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Games;

/// <summary>A game the person said isn't one, as the foot of the library lists it: its name, the keys their choice was written under, and a program for its icon.</summary>
public sealed record NotGameRow(string Key, string Name, IReadOnlyList<string> Keys, string? ExePath);

/// <summary>
/// "Not a game" (1.23.0, approved mock-up "2 + 1 · Games", board "Not a game"): the person's own word that something the
/// Games page lists isn't a game, kept for good in the same place as every app category they pick (settings'
/// AppCategories), so the rest of the app counts it as they said too. Its history stays where it was, as an app. A
/// launcher's listing is kept under its own key ("steam:570"), which has a colon no program's name has, so a game
/// installed but never played can leave too. "This is a game" on Apps is the other way round (Game in the same place).
/// </summary>
public static class NotGames
{
    /// <summary>A launcher's listing key ("steam:570", "epic:Sugar"), not a program's name.</summary>
    public static bool IsListingKey(string key) => key.Contains(':');

    /// <summary>The keys "Not a game" writes for a game: each of its programs, and its launcher's listing.</summary>
    public static List<string> KeysOf(GameEntry entry) => [.. entry.Exes, .. entry.Installed is { } g ? new[] { g.Key } : []];

    /// <summary>
    /// The category a game becomes: a launcher when it is one (a known launcher's program, or one named like a launcher:
    /// Rockstar Games Launcher, the mock-up's case), else Other. Never the folder's say, which is why it was a game.
    /// </summary>
    public static AppCategory CategoryFor(GameEntry entry)
    {
        foreach (var exe in entry.Exes)
            if (AppCatalog.Classify(exe, null) is var known && known is not (AppCategory.Other or AppCategory.Game or AppCategory.System)) return known;
        bool launcher = entry.Name.Contains("launcher", StringComparison.OrdinalIgnoreCase)
            || entry.Exes.Any(e => e.Contains("launcher", StringComparison.OrdinalIgnoreCase));
        return launcher ? AppCategory.Launcher : AppCategory.Other;
    }

    /// <summary>Marks a game not a game; returns what each key held before (null: nothing), for Undo.</summary>
    public static Dictionary<string, AppCategory?> Mark(RigsightSettings s, GameEntry entry)
    {
        var category = CategoryFor(entry);
        var before = new Dictionary<string, AppCategory?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in KeysOf(entry))
        {
            before[key] = s.AppCategories.TryGetValue(key, out var c) ? c : null;
            s.AppCategories[key] = category;
        }
        return before;
    }

    /// <summary>Puts each key back as it was before <see cref="Mark"/>.</summary>
    public static void Undo(RigsightSettings s, IReadOnlyDictionary<string, AppCategory?> before)
    {
        foreach (var (key, was) in before)
            if (was is { } c) s.AppCategories[key] = c;
            else s.AppCategories.Remove(key);
    }

    /// <summary>Puts a game back on the Games page: its keys go, and what RigSight worked out (a game) counts again.</summary>
    public static void PutBack(RigsightSettings s, NotGameRow row)
    {
        foreach (var key in row.Keys) s.AppCategories.Remove(key);
    }

    /// <summary>The launchers' listings the person marked not games.</summary>
    public static HashSet<string> MarkedInstalled(IReadOnlyList<InstalledGame> installed, RigsightSettings s) =>
        installed.Where(g => s.AppCategories.TryGetValue(g.Key, out var c) && c != AppCategory.Game).Select(g => g.Key).ToHashSet();

    /// <summary>
    /// The listings of installed games whose played program (a minute or more) the person said isn't a game, though
    /// RigSight had it as one: chosen on Apps, where only the program's category is written.
    /// </summary>
    public static IEnumerable<string> LeftByPrograms(IReadOnlyList<AppRow> apps, IReadOnlyList<InstalledGame> installed, RigsightSettings s, Func<long, double> activeSec)
    {
        foreach (var app in apps)
            if (app.Category == AppCategory.Game && s.AppCategories.TryGetValue(app.Exe, out var c) && c != AppCategory.Game && activeSec(app.Id) >= 60
                && GameLibrary.Find(installed, app.Path) is { } game)
                yield return game.Key;
    }

    /// <summary>
    /// Everything the person marked not a game that RigSight had as one, a row a game (a launcher's game with its
    /// programs), by name.
    /// </summary>
    public static List<NotGameRow> Marked(IReadOnlyList<AppRow> apps, IReadOnlyList<InstalledGame> installed, RigsightSettings s)
    {
        var byExe = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps) byExe.TryAdd(a.Exe, a);
        var rows = new Dictionary<string, (string Name, List<string> Keys, string? Path)>(StringComparer.OrdinalIgnoreCase);
        void Add(string row, string name, string key, string? path)
        {
            if (!rows.TryGetValue(row, out var r)) rows[row] = r = (name, [], path);
            r.Keys.Add(key);
            if (r.Path is null && path is not null) rows[row] = r with { Path = path };
        }
        foreach (var (key, category) in s.AppCategories)
        {
            if (category == AppCategory.Game) continue;
            if (IsListingKey(key))
            {
                if (installed.FirstOrDefault(g => g.Key == key) is { } listed) Add(listed.Key, listed.Name, key, null);
                continue;
            }
            // Only what RigSight had as a game: an app put in another category for its own sake isn't listed.
            if (!byExe.TryGetValue(key, out var app) || app.Category != AppCategory.Game) continue;
            var game = GameLibrary.Find(installed, app.Path);
            Add(game?.Key ?? "app:" + app.Exe, game?.Name ?? StutterStretches.NameOf(app, s), key, app.Path);
        }
        return [.. rows.Select(r => new NotGameRow(r.Key, r.Value.Name, r.Value.Keys, r.Value.Path)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}

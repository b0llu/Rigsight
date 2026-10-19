using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Rigsight.Core.Games;

/// <summary>Where a game was installed from.</summary>
public enum Launcher { Steam, Epic, Riot, Gog, Ubisoft, Ea, BattleNet, Other }

/// <summary>Pictures a launcher keeps for a game on this PC: the tall cover, the wide banner and the logo. Any may be missing.</summary>
public sealed record GameArt(string? Cover, string? Banner, string? Logo)
{
    public static readonly GameArt None = new(null, null, null);
}

/// <summary>
/// A game a launcher says is installed on this PC, read from the launcher's own files (nothing is downloaded).
/// <see cref="LaunchTarget"/> and <see cref="UninstallTarget"/> are what Windows is asked to open: a launcher link
/// (steam://…) or a program with its arguments; RigSight never removes files itself.
/// </summary>
public sealed record InstalledGame
{
    /// <summary>Stable across scans: "steam:570", "epic:Sugar", "uninstall:Riot Game valorant.live".</summary>
    public required string Key { get; init; }
    public required Launcher Launcher { get; init; }
    public required string Name { get; init; }
    public required string InstallDir { get; init; }
    public long? SizeBytes { get; init; }
    /// <summary>The launcher's version of the game (Steam's build number, Epic's version string), when it keeps one.</summary>
    public string? Version { get; init; }
    public DateTime? Updated { get; init; }
    /// <summary>When the launcher says it was last played (Steam only), which may be before RigSight's own history.</summary>
    public DateTime? LastPlayed { get; init; }
    public string? LaunchTarget { get; init; }
    public string? LaunchArgs { get; init; }
    public string? UninstallTarget { get; init; }
    public string? UninstallArgs { get; init; }
    public GameArt Art { get; init; } = GameArt.None;
    /// <summary>Another launcher that only holds a starter for this game (VALORANT added through Epic Games, run by the Riot Client).</summary>
    public Launcher? AddedThrough { get; init; }

    /// <summary>True when <paramref name="exePath"/> is inside this game's folder.</summary>
    public bool Contains(string? exePath) => exePath is not null && GameLibrary.IsUnder(exePath, InstallDir);
}

/// <summary>One entry of Windows' list of installed programs (the Uninstall keys).</summary>
public sealed record UninstallEntry(string KeyName, string? DisplayName, string? Publisher, string? InstallLocation,
    string? UninstallString, string? DisplayVersion, long? EstimatedSizeKb);

/// <summary>Where the launchers keep their files; the defaults are the real PC's, tests point them at folders of their own.</summary>
public sealed class GameSources
{
    public IReadOnlyList<string> SteamRoots { get; init; } = [];
    public string? EpicManifests { get; init; }
    public Func<IEnumerable<UninstallEntry>> UninstallEntries { get; init; } = () => [];
    /// <summary>Also find each game's pictures (the Games page); the agent's minute check doesn't need them.</summary>
    public bool WithArt { get; init; }

    public static GameSources ThisPc(bool withArt = false) =>
        // A test copy can be given launchers of its own (the site's showcase: its made-up games, never this PC's).
        From(withArt, RigsightPaths.IsTestInstance ? Environment.GetEnvironmentVariable("RIGSIGHT_GAMES_DIR") : null);

    /// <summary>The launchers in <paramref name="gamesDir"/> (its Steam and Epic folders, nothing from Windows' list), else this PC's.</summary>
    internal static GameSources From(bool withArt, string? gamesDir) => gamesDir is { Length: > 0 } games
            ? new() { WithArt = withArt, SteamRoots = [Path.Combine(games, "Steam")], EpicManifests = Path.Combine(games, "Epic") }
            : new()
    {
        WithArt = withArt,
        SteamRoots = GameLibrary.SteamRootsOnThisPc(),
        EpicManifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests"),
        UninstallEntries = GameLibrary.ReadUninstallEntries,
    };
}

/// <summary>
/// The games installed on this PC, from every launcher's own records: Steam's manifests (with its cached cover art),
/// Epic's manifests, and Windows' list of installed programs for Riot, GOG, Ubisoft, EA and Battle.net. One game
/// shows once, whichever launcher it was added through.
/// </summary>
public static class GameLibrary
{
    /// <summary>Reads every launcher. Never throws: a launcher that can't be read is left out and logged.</summary>
    public static List<InstalledGame> Scan(GameSources sources)
    {
        var found = new List<InstalledGame>();
        Try("Steam", () => found.AddRange(ReadSteam(sources.SteamRoots, sources.WithArt)));
        Try("Epic", () => found.AddRange(ReadEpic(sources.EpicManifests)));
        Try("installed programs", () =>
        {
            var entries = sources.UninstallEntries().ToList();
            found.AddRange(entries.Select(FromUninstall).OfType<InstalledGame>()
                .Where(g => !found.Any(f => f.Launcher == Launcher.Steam && SameDir(f.InstallDir, g.InstallDir))));
        });
        // One entry per key: the same appid in two Steam libraries (one left behind by a move), a stale Epic manifest, or
        // one program listed in both registry views would otherwise reach the change log twice under one key.
        return Fold(found.GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(x => x.SizeBytes ?? 0).First()).ToList());
    }

    private static void Try(string what, Action read)
    {
        try { read(); }
        catch (Exception ex) { Log.Write("games", $"{what}: couldn't be read ({ex.GetType().Name}: {ex.Message})"); }
    }

    /// <summary>
    /// One game, one entry: when two launchers list the same game, the one holding the game itself stays and the other
    /// (a small starter, like Epic's VALORANT that only opens the Riot Client) becomes <see cref="InstalledGame.AddedThrough"/>.
    /// </summary>
    internal static List<InstalledGame> Fold(List<InstalledGame> games)
    {
        var result = new List<InstalledGame>();
        foreach (var group in games.GroupBy(g => Normalize(g.Name)))
        {
            var list = group.ToList();
            if (list.Count == 1) { result.Add(list[0]); continue; }
            var main = list.OrderByDescending(g => g.SizeBytes ?? 0).First();
            var other = list.FirstOrDefault(g => g != main && g.Launcher != main.Launcher);
            result.Add(other is null ? main : main with { AddedThrough = other.Launcher });
            result.AddRange(list.Where(g => g != main && g != other && g.Launcher == main.Launcher)); // two installs from one launcher stay apart
        }
        return result.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // ---------------- Steam ----------------

    /// <summary>Steam's install folder from the registry, else its usual place.</summary>
    internal static List<string> SteamRootsOnThisPc()
    {
        var roots = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) roots.Add(Path.GetFullPath(p));
        }
        catch (Exception ex) { Log.Write("games", $"Steam's folder couldn't be read from the registry ({ex.Message})"); }
        var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (roots.Count == 0 && Directory.Exists(usual)) roots.Add(usual);
        return roots;
    }

    private static readonly Regex LibraryPath = new(@"""path""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex VdfPair = new(@"^\s*""([^""]+)""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    internal static IEnumerable<InstalledGame> ReadSteam(IEnumerable<string> roots, bool withArt = false)
    {
        foreach (var root in roots)
        {
            var libraries = new List<string> { Path.Combine(root, "steamapps") };
            var folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            // Each library's "path", wherever it sits on its line.
            if (File.Exists(folders))
                foreach (Match m in LibraryPath.Matches(File.ReadAllText(folders)))
                    libraries.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"));
            foreach (var steamapps in libraries.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
                foreach (var game in ReadLibrary(steamapps, root, withArt)) yield return game;
        }
    }

    // Steam's own tools that sit in the library like games.
    private static readonly HashSet<string> SteamNotGames = ["228980", "1070560", "1391110", "1628350", "1493710", "250820", "431960"];

    /// <summary>One Steam library's games. A library that can't be listed, or one manifest that can't be read, costs only itself.</summary>
    private static List<InstalledGame> ReadLibrary(string steamapps, string root, bool withArt)
    {
        var games = new List<InstalledGame>();
        IEnumerable<string> files;
        try { files = Directory.GetFiles(steamapps, "appmanifest_*.acf"); }
        catch (Exception ex) { Log.Write("games", $"Steam library {steamapps} couldn't be listed ({ex.GetType().Name}: {ex.Message})"); return games; }
        foreach (var file in files)
        {
            try { if (ReadAcf(file, steamapps, root, withArt) is { } game) games.Add(game); }
            catch (Exception ex) { Log.Write("games", $"{Path.GetFileName(file)} in {steamapps} couldn't be read ({ex.GetType().Name}: {ex.Message})"); }
        }
        return games;
    }

    private static InstalledGame? ReadAcf(string file, string steamapps, string root, bool withArt)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(file))
            if (VdfPair.Match(line) is { Success: true } m) v.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        if (!v.TryGetValue("appid", out var id) || !v.TryGetValue("name", out var name) || !v.TryGetValue("installdir", out var dir)) return null;
        if (SteamNotGames.Contains(id) || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)) return null;
        var installDir = Path.Combine(steamapps, "common", dir);
        if (!HoldsGame(installDir, null))
        {
            LogOnce($"Steam lists {name} ({id}) at {installDir}, but that folder {(Directory.Exists(installDir) ? "is empty" : "doesn't exist")}: not counted as installed");
            return null;
        }
        return new InstalledGame
        {
            Key = "steam:" + id,
            Launcher = Launcher.Steam,
            Name = name.Trim(),
            InstallDir = installDir,
            SizeBytes = long.TryParse(v.GetValueOrDefault("SizeOnDisk"), out var size) && size > 0 ? size : null,
            // Data, in one form whatever the language: the Games page words it ("Build 1234").
            Version = v.TryGetValue("buildid", out var build) && build != "0" ? $"build {build}" : null,
            Updated = UnixDate(v.GetValueOrDefault("LastUpdated")),
            LastPlayed = UnixDate(v.GetValueOrDefault("LastPlayed")),
            LaunchTarget = "steam://rungameid/" + id,
            UninstallTarget = "steam://uninstall/" + id,
            Art = withArt ? SteamArt(root, id) : GameArt.None,
        };
    }

    /// <summary>
    /// A launcher's list outlives the game when its folder is deleted by hand or an uninstall stops half way (a user's
    /// Fortnite: Epic's manifest said 75 GB, the folder was empty). Only a folder holding the game counts: its program
    /// where the launcher names one, else anything at all in it.
    /// </summary>
    internal static bool HoldsGame(string dir, string? exe)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            return exe is { Length: > 0 } ? File.Exists(Path.Combine(dir, exe)) : Directory.EnumerateFileSystemEntries(dir).Any();
        }
        // A folder RigSight may not look into: the launcher is believed.
        catch (UnauthorizedAccessException) { return true; }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return false; }
    }

    private static readonly HashSet<string> Logged = [];

    /// <summary>The library is read again every minute or so: a game left out is logged once a run, not each time.</summary>
    private static void LogOnce(string message)
    {
        lock (Logged) if (!Logged.Add(message)) return;
        Log.Write("games", message);
    }

    private static DateTime? UnixDate(string? s) => long.TryParse(s, out var t) && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : null;

    /// <summary>
    /// The cover, banner and logo Steam keeps for its library: appcache\librarycache\&lt;id&gt;\…\library_capsule.jpg (or
    /// library_600x900.jpg), library_hero.jpg and logo.png, in a hashed sub-folder in newer Steam or flat beside it.
    /// </summary>
    internal static GameArt SteamArt(string root, string id)
    {
        var cache = Path.Combine(root, "appcache", "librarycache");
        string? Find(params string[] names)
        {
            var dir = Path.Combine(cache, id);
            if (Directory.Exists(dir))
                foreach (var n in names)
                    if (Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).FirstOrDefault() is { } f) return f;
            foreach (var n in names)
                if (File.Exists(Path.Combine(cache, $"{id}_{n}"))) return Path.Combine(cache, $"{id}_{n}");
            return null;
        }
        try { return new GameArt(Find("library_capsule.jpg", "library_600x900.jpg"), Find("library_hero.jpg"), Find("logo.png")); }
        catch (Exception ex) { Log.Write("games", $"Steam's pictures for {id} couldn't be read ({ex.Message})"); return GameArt.None; }
    }

    // ---------------- Epic ----------------

    internal static IEnumerable<InstalledGame> ReadEpic(string? manifests)
    {
        if (manifests is null || !Directory.Exists(manifests)) yield break;
        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            InstalledGame? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var j = doc.RootElement;
                string? S(string p) => j.TryGetProperty(p, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (j.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                if (S("DisplayName") is not { Length: > 0 } name || S("InstallLocation") is not { Length: > 0 } dir) continue;
                if (S("MainGameAppName") is { Length: > 0 } main && main != S("AppName")) continue; // a DLC of another game
                if (!HoldsGame(dir, S("LaunchExecutable")))
                {
                    LogOnce($"Epic lists {name} at {dir} ({Path.GetFileName(file)}), but {(Directory.Exists(dir) ? $"{S("LaunchExecutable") ?? "no file"} isn't in it" : "that folder doesn't exist")}: not counted as installed");
                    continue;
                }
                string? ns = S("CatalogNamespace"), item = S("CatalogItemId"), app = S("AppName");
                game = new InstalledGame
                {
                    Key = "epic:" + (app ?? name),
                    Launcher = Launcher.Epic,
                    Name = name.Replace("®", "").Replace("™", "").Trim(),
                    InstallDir = Path.GetFullPath(dir),
                    SizeBytes = j.TryGetProperty("InstallSize", out var sz) && sz.TryGetInt64(out var size) && size > 0 ? size : null,
                    Version = S("AppVersionString"),
                    LaunchTarget = ns is not null && item is not null && app is not null
                        ? $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true" : null,
                    // Epic writes nothing to Windows' list of programs: its own library is where a game is removed.
                    UninstallTarget = "com.epicgames.launcher://store/library",
                };
            }
            catch (Exception ex) { Log.Write("games", $"Epic's {Path.GetFileName(file)} couldn't be read ({ex.Message})"); }
            if (game is not null) yield return game;
        }
    }

    // ---------------- Windows' list of installed programs ----------------

    /// <summary>Every Uninstall entry for this PC and this person (64- and 32-bit views).</summary>
    public static IEnumerable<UninstallEntry> ReadUninstallEntries()
    {
        var list = new List<UninstallEntry>();
        void Read(RegistryKey hive, string path)
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) return;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null) continue;
                    long? kb = k.GetValue("EstimatedSize") is int i && i > 0 ? i : null;
                    list.Add(new UninstallEntry(name, k.GetValue("DisplayName") as string, k.GetValue("Publisher") as string,
                        k.GetValue("InstallLocation") as string, k.GetValue("UninstallString") as string, k.GetValue("DisplayVersion") as string, kb));
                }
            }
            catch (Exception ex) { Log.Write("games", $"installed programs under {path} couldn't be read ({ex.Message})"); }
        }
        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        Read(Registry.LocalMachine, Uninstall);
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(Registry.CurrentUser, Uninstall);
        return list;
    }

    private static readonly Regex Ubisoft = new(@"^Uplay Install (\d+)$", RegexOptions.IgnoreCase);
    private static readonly Regex BattleNetUid = new(@"--uid=([^\s""]+)", RegexOptions.IgnoreCase);

    /// <summary>A game from an Uninstall entry that a game launcher wrote; null for every other program.</summary>
    internal static InstalledGame? FromUninstall(UninstallEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.DisplayName) || string.IsNullOrWhiteSpace(e.InstallLocation?.Trim().Trim('"'))) return null; // "" too: one bad entry costs only itself
        string publisher = e.Publisher ?? "", un = e.UninstallString ?? "", key = e.KeyName;
        Launcher launcher;
        string? launch = null, launchArgs = null;
        if (key.StartsWith("Riot Game ", StringComparison.OrdinalIgnoreCase) && un.Contains("--uninstall-product=", StringComparison.OrdinalIgnoreCase))
        {
            if (key.Contains("Riot_Client", StringComparison.OrdinalIgnoreCase)) return null; // the launcher itself
            launcher = Launcher.Riot;
            (launch, launchArgs) = SplitCommand(un.Replace("--uninstall-", "--launch-", StringComparison.OrdinalIgnoreCase));
        }
        else if (Ubisoft.Match(key) is { Success: true } ubi)
        {
            launcher = Launcher.Ubisoft;
            launch = $"uplay://launch/{ubi.Groups[1].Value}/0";
        }
        else if (key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) && publisher.Contains("GOG", StringComparison.OrdinalIgnoreCase))
            launcher = Launcher.Gog;
        else if (un.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase) && BattleNetUid.Match(un) is { Success: true } uid)
        {
            if (uid.Groups[1].Value.Equals("battle.net", StringComparison.OrdinalIgnoreCase)) return null; // the launcher itself
            launcher = Launcher.BattleNet;
        }
        else if (publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) && un.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase))
            launcher = Launcher.Ea;
        else return null;

        var (uninstall, uninstallArgs) = launcher == Launcher.Ubisoft
            ? ($"uplay://uninstall/{Ubisoft.Match(key).Groups[1].Value}", null)
            : SplitCommand(un);
        return new InstalledGame
        {
            Key = "uninstall:" + key,
            Launcher = launcher,
            Name = e.DisplayName!.Replace("®", "").Replace("™", "").Trim(),
            InstallDir = Path.GetFullPath(e.InstallLocation!.Trim().Trim('"')),
            SizeBytes = e.EstimatedSizeKb * 1024,
            Version = string.IsNullOrWhiteSpace(e.DisplayVersion) ? null : e.DisplayVersion.Trim(),
            LaunchTarget = launch,
            LaunchArgs = launchArgs,
            UninstallTarget = uninstall,
            UninstallArgs = uninstallArgs,
        };
    }

    /// <summary>"\"C:\x y\a.exe\" --b c" → ("C:\x y\a.exe", "--b c"); an unquoted command splits at ".exe".</summary>
    internal static (string? File, string? Args) SplitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return (null, null);
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            if (end > 1) return (c[1..end], c[(end + 1)..].Trim() is { Length: > 0 } a ? a : null);
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (c[..(exe + 4)], c[(exe + 4)..].Trim() is { Length: > 0 } a ? a : null);
        return (c, null);
    }

    // ---------------- folders ----------------

    /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or inside it (slashes and case don't matter).</summary>
    public static bool IsUnder(string path, string dir)
    {
        var p = Norm(path);
        var d = Norm(dir).TrimEnd('\\') + '\\';
        return p.StartsWith(d, StringComparison.OrdinalIgnoreCase) || p.Equals(d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameDir(string a, string b) => Norm(a).TrimEnd('\\').Equals(Norm(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string p) => p.Replace('/', '\\');

    /// <summary>The installed game whose folder holds <paramref name="exePath"/>, the deepest folder first.</summary>
    public static InstalledGame? Find(IEnumerable<InstalledGame> games, string? exePath) =>
        exePath is null ? null : games.Where(g => g.Contains(exePath)).OrderByDescending(g => g.InstallDir.Length).FirstOrDefault();
}

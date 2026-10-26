using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;

namespace Rigsight.Core.Games;

/// <summary>One saved setting as the game's own file names it ("ResolutionSizeX", "setting.mat_vsync").</summary>
public sealed record GameSetting(string Key, string Value);

/// <summary>A file (or registry key) a game keeps its graphics settings in, with the graphics settings read from it.</summary>
public sealed record GameSettingsFile(string Path, DateTime Saved, IReadOnlyList<GameSetting> Settings);

/// <summary>A setting said in plain words: "Display mode" · "Borderless window". The label is English, for Lang.T.</summary>
public sealed record GameSettingFact(string Label, string Value);

/// <summary>How a game is set: the files it was read from and the main settings in plain words.</summary>
public sealed record GameSettingsRead(IReadOnlyList<GameSettingsFile> Files, IReadOnlyList<GameSettingFact> Facts)
{
    public static readonly GameSettingsRead None = new([], []);
    public bool Found => Files.Count > 0;
    /// <summary>When the newest of its files was saved: the last time the game wrote its settings.</summary>
    public DateTime? Saved => Files.Count == 0 ? null : Files.Max(f => f.Saved);
}

/// <summary>
/// Reads a game's own saved graphics settings so the AI can say how to run it better and the Games page can show how
/// it's set (the person's ask, 2026-10-25). Every game keeps them its own way, so the places are found from the game
/// itself: Unreal games under %LOCALAPPDATA%\&lt;project&gt;\Saved\Config, Unity games in the registry under their maker
/// and name, and otherwise a settings-like file in a folder named after the game (Documents\My Games, Saved Games,
/// AppData) or in the install folder, or Steam's own copy. Only reads; never changes a file. Only graphics settings are
/// kept, and nothing that names the person (account ids, paths, server names).
/// </summary>
public static class GameSettings
{
    /// <summary>Where a PC keeps games' settings: this PC's folders, or a test's.</summary>
    public sealed class Places
    {
        public string LocalAppData { get; init; } = "";
        public string RoamingAppData { get; init; } = "";
        public string LocalLow { get; init; } = "";
        public string Documents { get; init; } = "";
        public string SavedGames { get; init; } = "";
        /// <summary>Steam's own folder (its userdata holds some games' settings).</summary>
        public string? Steam { get; init; }
        /// <summary>A Unity game's saved preferences by maker and name (the registry on a real PC).</summary>
        public Func<string, string, (string Where, DateTime Saved, IReadOnlyList<GameSetting> Values)?> UnityPrefs { get; init; } = (_, _) => null;

        public static Places ThisPc()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new()
            {
                LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                LocalLow = Path.Combine(profile, "AppData", "LocalLow"),
                Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                SavedGames = Path.Combine(profile, "Saved Games"),
                Steam = SteamFolder(),
                UnityPrefs = UnityRegistry,
            };
        }
    }

    /// <summary>The game's settings, from the first places that hold any. Never throws; nothing found is <see cref="GameSettingsRead.None"/>.</summary>
    public static GameSettingsRead Read(GameEntry game, Places places) =>
        Read(game.Name, game.ExePath ?? ProgramIn(game.Installed?.InstallDir), game.Installed?.InstallDir,
            game.Installed?.Key is { } key && key.StartsWith("steam:", StringComparison.Ordinal) ? key[6..] : null, places);

    public static GameSettingsRead Read(string name, string? exePath, string? installDir, string? steamAppId, Places places)
    {
        try
        {
            var files = new List<GameSettingsFile>();
            var names = NamesOf(name, exePath, installDir);

            // Unreal: the project's folder name is the one before Binaries, and its settings live under that name.
            if (exePath is not null && Regex.Match(exePath, @"[\\/]([^\\/]+)[\\/]Binaries[\\/]Win(64|GDK)[\\/]", RegexOptions.IgnoreCase) is { Success: true } ue)
                names.Insert(0, ue.Groups[1].Value);
            if (Newest(names.SelectMany(n => UnrealFiles(places, n))) is { } unreal && Parse(unreal) is { } uf) files.Add(uf);

            // Unity: the game's _Data\app.info names its maker and product, which key its saved preferences.
            if (files.Count == 0 && exePath is not null && UnityName(exePath) is var (company, product))
            {
                if (places.UnityPrefs(company, product) is { } prefs && Graphics(prefs.Values) is { Count: > 0 } kept)
                    files.Add(new(prefs.Where, prefs.Saved, kept));
                files.AddRange(Best(Candidates(Path.Combine(places.LocalLow, company, product), 3, names), 1, least: 3));
                // Some keep only the screen in the registry and the rest in a file of their own in _Data (Risk of Rain 2's
                // Config\config.cfg: 2 settings in the registry, every graphics one in the file; 2026-10-25).
                files.AddRange(Best(Candidates(Path.Combine(Path.GetDirectoryName(exePath)!, Path.GetFileNameWithoutExtension(exePath) + "_Data"), 2, names), 1));
            }

            if (files.Count == 0) files.AddRange(Best(KnownFiles(places, names), 1, least: 3));
            if (files.Count == 0)
            {
                // A folder named after the game where games keep settings, then Steam's copy, then the install folder (which
                // also holds the defaults the game shipped with, so only when nothing of the person's own was found).
                var folders = new List<string>();
                foreach (var root in new[] { Path.Combine(places.Documents, "My Games"), places.Documents, places.SavedGames, places.LocalAppData, places.RoamingAppData })
                    folders.AddRange(Named(root, names));
                foreach (var maker in SafeDirs(places.SavedGames).Concat(SafeDirs(places.LocalLow)).Concat(SafeDirs(places.LocalAppData)))
                    folders.AddRange(Named(maker, names));
                files.AddRange(Best(folders.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(f => Candidates(f, 4, names)), 2));
                if (files.Count == 0 && steamAppId is not null && places.Steam is { } steam)
                    files.AddRange(Best(SafeDirs(Path.Combine(steam, "userdata")).SelectMany(u => Candidates(Path.Combine(u, steamAppId), 3, names)), 2));
                if (files.Count == 0 && installDir is not null) files.AddRange(Best(Candidates(installDir, 4, names), 2));
            }
            return files.Count == 0 ? GameSettingsRead.None : new(files, Facts(files.SelectMany(f => f.Settings).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Log.Write("GameSettings", $"{name} couldn't be read ({ex.GetType().Name}: {ex.Message})");
            return GameSettingsRead.None;
        }
    }

    // ── Where ───────────────────────────────────────────────────────────

    /// <summary>
    /// The game's program in a folder when RigSight never saw it played: an Unreal "-Shipping" program (its folder names
    /// the project), else a "_Data" Unity program, else the biggest program at the top.
    /// </summary>
    public static string? ProgramIn(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var exes = Files(dir, 4, f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !NotTheGame.IsMatch(Path.GetFileName(f))).ToList();
        return exes.FirstOrDefault(f => Path.GetFileName(f).Contains("-Shipping", StringComparison.OrdinalIgnoreCase))
            ?? exes.FirstOrDefault(f => Directory.Exists(Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f) + "_Data")))
            ?? exes.Where(f => string.Equals(Path.GetDirectoryName(f), dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)).OrderByDescending(SafeLength).FirstOrDefault();
    }

    /// <summary>The names a game's folders may go by: its name, its install folder's, its program's (without Unreal's suffixes).</summary>
    private static List<string> NamesOf(string name, string? exePath, string? installDir)
    {
        var list = new List<string> { name };
        if (installDir is { Length: > 0 }) list.Add(Path.GetFileName(installDir.TrimEnd('\\', '/')));
        if (exePath is { Length: > 0 })
            list.Add(Regex.Replace(Path.GetFileNameWithoutExtension(exePath), @"(Client)?-Win64-Shipping$|-WinGDK-Shipping$|_x64$|64$", "", RegexOptions.IgnoreCase));
        return [.. list.Where(n => Norm(n).Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Letters and digits only, lower case: "Tom Clancy's The Division 2" and "TomClancysTheDivision2" match.</summary>
    internal static string Norm(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static bool SameName(string folder, IEnumerable<string> names)
    {
        string f = Norm(folder);
        // "Apex" for Apex Legends, "Rainbow Six - Siege" for Tom Clancy's Rainbow Six Siege.
        return f.Length >= 3 && names.Any(n => Norm(n) is var x && (x == f
            || (Math.Min(x.Length, f.Length) >= 4 && (x.StartsWith(f, StringComparison.Ordinal) || f.StartsWith(x, StringComparison.Ordinal)))
            || (Math.Min(x.Length, f.Length) >= 8 && (x.Contains(f, StringComparison.Ordinal) || f.Contains(x, StringComparison.Ordinal)))));
    }

    private static IEnumerable<string> Named(string root, List<string> names) => SafeDirs(root).Where(d => SameName(Path.GetFileName(d), names));

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> UnrealFiles(Places places, string project)
    {
        foreach (var root in new[] { Path.Combine(places.LocalAppData, project, "Saved", "Config"), Path.Combine(places.Documents, "My Games", project, "Saved", "Config") })
            foreach (var f in Files(root, 3, f => Path.GetFileName(f).Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase)))
                yield return f;
    }

    private static string? Newest(IEnumerable<string> files) => files.Select(f => (f, t: SafeTime(f))).OrderByDescending(x => x.t).Select(x => x.f).FirstOrDefault();

    private static (string Company, string Product)? UnityName(string exePath)
    {
        string info = Path.Combine(Path.GetDirectoryName(exePath) ?? "", Path.GetFileNameWithoutExtension(exePath) + "_Data", "app.info");
        try
        {
            if (!File.Exists(info)) return null;
            var lines = File.ReadAllLines(info).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            return lines.Length >= 2 ? (lines[0], lines[1]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly Regex NotTheGame = new(@"unins|setup|install|crash|report|redist|vc_|dxsetup|anticheat|helper|launcher|createdump|ubisoftconnect|bootstrap|prereq", RegexOptions.IgnoreCase);

    /// <summary>Games whose settings are where nothing about the game points (a maker's short name): the file itself.</summary>
    private static readonly (string Name, string Where)[] Known =
    [
        ("crimsondesert", @"LocalAppData\Pearl Abyss\CD\save\user_engine_option_save.xml"),
    ];

    private static IEnumerable<string> KnownFiles(Places places, List<string> names)
    {
        foreach (var (known, where) in Known)
            if (names.Any(n => Norm(n).StartsWith(known, StringComparison.Ordinal)))
                yield return where.Replace("LocalAppData", places.LocalAppData);
    }

    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase) { ".ini", ".cfg", ".txt", ".json", ".xml", ".mxml", ".vdf", ".save", ".sav", ".config", ".dat" };
    private static readonly Regex Settingsy = new(@"setting|config|option|video|graphic|display|pref|engine|system|render|quality", RegexOptions.IgnoreCase);
    private static readonly Regex NotSettings = new(@"log|crash|dump|backup|\.bak|sentry|tool|addon|template|default|example|netengine|network|achiev|keybind|binding|input|control|slot|progress|stats|license|eula|readme|credits|steam_autocloud|manifest|localization|lang", RegexOptions.IgnoreCase);

    /// <summary>
    /// Files under <paramref name="dir"/> that are named like settings (or after the game itself: STEEP.ini) and small
    /// enough to be them.
    /// </summary>
    private static IEnumerable<string> Candidates(string dir, int depth, List<string> names) => Files(dir, depth, f =>
    {
        string file = Path.GetFileName(f);
        return Kinds.Contains(Path.GetExtension(f)) && (Settingsy.IsMatch(file) || SameName(Path.GetFileNameWithoutExtension(f), names))
            && !NotSettings.IsMatch(file) && SafeLength(f) is > 0 and <= 256 * 1024;
    });

    /// <summary>The files under a folder that pass <paramref name="keep"/>, at most 400 (a game's asset folders hold thousands).</summary>
    private static IEnumerable<string> Files(string dir, int depth, Func<string, bool> keep)
    {
        if (!Directory.Exists(dir)) return [];
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(keep).Take(400).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The files with the most graphics settings in them, newest first among equals. A file found by its name alone needs
    /// 5 (a network or launcher file can have a "buffer" or two).
    /// </summary>
    private static IEnumerable<GameSettingsFile> Best(IEnumerable<string> paths, int take, int least = 5) =>
        paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(Parse).OfType<GameSettingsFile>()
            .Where(f => f.Settings.Count >= least).OrderByDescending(f => Math.Min(f.Settings.Count, 40)).ThenByDescending(f => f.Saved).Take(take);

    private static long SafeLength(string f) { try { return new FileInfo(f).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; } }
    private static DateTime SafeTime(string f) { try { return File.GetLastWriteTime(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; } }

    // ── Reading a file ──────────────────────────────────────────────────

    internal static GameSettingsFile? Parse(string path)
    {
        string text;
        try
        {
            if (SafeLength(path) is <= 0 or > 256 * 1024) return null;
            var bytes = File.ReadAllBytes(path);
            // UTF-16 with its mark; otherwise a zero byte inside means a binary file (saves, caches). Zeros at the very end
            // don't (Apex ends its videoconfig.txt with one).
            int end = bytes.Length;
            while (end > 0 && bytes[end - 1] == 0) end--;
            if (bytes is [0xFF, 0xFE, ..]) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes is [0xFE, 0xFF, ..]) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.AsSpan(0, Math.Min(end, 4096)).Contains((byte)0)) return null;
            else text = Encoding.UTF8.GetString(bytes, 0, end).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var all = ParseText(text);
        var kept = Graphics(all);
        return kept.Count == 0 ? null : new(path, SafeTime(path), kept);
    }

    private static readonly Regex ValveLine = new("^\\s*\"([^\"]+)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline);
    private static readonly Regex IniLine = new(@"^\s*([A-Za-z_][\w.\[\]:\- ]{0,80}?)\s*[=:]\s*(.*?)\s*$", RegexOptions.Multiline);
    private static readonly Regex CfgLine = new("^\\s*([A-Za-z_][\\w.]{1,60})\\s+\"?([^\"\\r\\n]{1,60})\"?\\s*$", RegexOptions.Multiline);

    /// <summary>Every name and value in a settings text, whatever its format: JSON, XML, Valve's quoted pairs, INI or cfg.</summary>
    internal static List<GameSetting> ParseText(string text)
    {
        string t = text.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var list = new List<GameSetting>();
                Flatten(doc.RootElement, "", list);
                return list;
            }
            catch (JsonException) { }
        }
        if (t.StartsWith('<')) { if (Xml(text) is { Count: > 0 } x) return x; }
        var valve = ValveLine.Matches(text).Select(m => new GameSetting(m.Groups[1].Value, m.Groups[2].Value)).ToList();
        if (valve.Count >= 3) return valve;
        var ini = IniLine.Matches(text).Select(m => new GameSetting(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim().Trim('"'))).ToList();
        if (ini.Count >= 3) return ini;
        // A console line may end with ";" (Risk of Rain 2: "fps_max 144;").
        return [.. CfgLine.Matches(text).Select(m => new GameSetting(m.Groups[1].Value, m.Groups[2].Value.Trim().TrimEnd(';').Trim()))];
    }

    /// <summary>
    /// JSON settings by their path ("video.vsync"). A list of {"name", "value"} objects is one setting each, named by
    /// its name under its group (Cyberpunk 2077: "graphics_basic.Resolution"), not by its place in the list.
    /// </summary>
    private static void Flatten(JsonElement e, string prefix, List<GameSetting> list, string group = "")
    {
        if (list.Count > 2000) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("group_name", out var g) && g.ValueKind == JsonValueKind.String) group = (g.GetString() ?? "").Trim('/').Replace('/', '_');
                if (e.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && e.TryGetProperty("value", out var value)
                    && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    string key = (group.Length > 0 ? group + "." : "") + name.GetString();
                    list.Add(new(key, value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()));
                    break;
                }
                foreach (var p in e.EnumerateObject()) Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name, list, group);
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in e.EnumerateArray()) { if (i >= 200) break; Flatten(item, $"{prefix}[{i++}]", list, group); }
                break;
            case JsonValueKind.String: list.Add(new(prefix, e.GetString() ?? "")); break;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False: list.Add(new(prefix, e.GetRawText())); break;
        }
    }

    /// <summary>
    /// XML settings: an element with a name and a value ("&lt;Property name="VsyncEx" value="Off"/&gt;", Crimson Desert's
    /// "&lt;OptionInt Name="_x" _value="0"/&gt;") is one setting, named with the named elements around it.
    /// </summary>
    private static List<GameSetting> Xml(string text)
    {
        var list = new List<GameSetting>();
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                doc.Load(reader);
            void Walk(XmlNode node, string prefix)
            {
                if (list.Count > 2000) return;
                foreach (XmlNode child in node.ChildNodes)
                {
                    if (child is not XmlElement el) continue;
                    string? name = Attr(el, "name", "Name", "id");
                    string? value = Attr(el, "value", "_value", "_select", "Value");
                    string here = name is null ? prefix : prefix.Length == 0 ? name : prefix + "." + name;
                    if (name is not null && value is not null && !el.HasChildNodes) list.Add(new(here, value));
                    else if (name is not null && value is not null && el.ChildNodes.OfType<XmlElement>().All(c => Attr(c, "name", "Name") is null)) list.Add(new(here, value));
                    // Top-level attributes that are settings themselves (_selectPreset="Ultra").
                    foreach (XmlAttribute a in el.Attributes)
                        if (a.Name.Contains("preset", StringComparison.OrdinalIgnoreCase)) list.Add(new(a.Name, a.Value));
                    Walk(el, here);
                }
            }
            Walk(doc, "");
        }
        catch (XmlException) { }
        return list;
    }

    private static string? Attr(XmlElement el, params string[] names)
    {
        foreach (var n in names) if (el.HasAttribute(n)) return el.GetAttribute(n);
        return null;
    }

    // ── Which settings ──────────────────────────────────────────────────

    private static readonly Regex GraphicsWord = new(
        @"res(olution)?|width|height|resx|resy|fullscreen|windowed|window_?mode|borderless|nowindowborder|display_?(type|mode)|vsync|v_sync|fps|frame|refresh|hz\b|quality|preset|shadow|texture|aniso|antialias|anti_alias|\baa\b|msaa|taa|fxaa|smaa|dlss|fsr|xess|upscal|upsampl|super_?res|sharpen|scale|ray|_rt_|\brt\b|reflection|lighting|illumination|ssao|ambient|occlusion|fog|volumetric|cloud|detail|lod|distance|draw|effect|particle|post_?process|bloom|blur|depth_of_field|dof|motion|hdr|gamma|render|foliage|grass|tree|water|tessellation|latency|reflex|boost|dynamic|streaming|stream_memory|picmip|mip|decal|ragdoll|gib|physics|crowd|graphics?|gpu|cpu_level|mem_level|shader|vram|budget|backbuffer|buffer|triple|level_?of|^r_|^pp_|_aa$|_ao$",
        RegexOptions.IgnoreCase);
    // "path" is a file's (never kept), but not path tracing, a graphics setting (2026-10-25: Cyberpunk's RayTracedPathTracing
    // was dropped, and the AI never heard path tracing was on). A value with a slash is dropped anyway (SafeValue).
    private static readonly Regex PrivateWord = new(
        @"user|name|account|email|mail|token|password|passwd|secret|session|guid|uuid|steam_?id|login|path(?!_?trac)|folder|directory|\bdir\b|ip\b|address|server|region|device|monitor|adapter|vendor|machine|serial|hwid|\bid$|_id$|identifier|benchmark|cookie|auth|key$|language|locale|volume|sound|audio|music|voice|mouse|sensitivity|keyboard|bind|crosshair|chat",
        RegexOptions.IgnoreCase);

    /// <summary>Groups of settings that are never graphics, whatever their names say (Cyberpunk's "controls_vehicle.VehicleTPPCameraHeight").</summary>
    private static readonly Regex NotGraphicsGroup = new(@"(^|[._\[])(controls?|audio|sound|accessibility|input|keybind\w*|hud|interface|subtitles?)[._\]]", RegexOptions.IgnoreCase);

    /// <summary>The graphics settings only, and nothing that could name the person or their PC; at most 150.</summary>
    internal static List<GameSetting> Graphics(IEnumerable<GameSetting> all) => [.. all
        .Where(s => s.Key.Length is > 0 and <= 90 && s.Value.Length <= 40 && GraphicsWord.IsMatch(s.Key) && !PrivateWord.IsMatch(Leaf(s.Key))
            && !NotGraphicsGroup.IsMatch(s.Key) && SafeValue(s.Value))
        .DistinctBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Take(150)];

    private static bool SafeValue(string v) =>
        !v.Contains('\\') && !v.Contains('/') && !v.Contains('@') && !Regex.IsMatch(v, @"[0-9a-f]{12,}|[0-9a-f]{8}-[0-9a-f]{4}", RegexOptions.IgnoreCase);

    /// <summary>The last part of a dotted name: "setting.mat_vsync" → "mat_vsync".</summary>
    private static string Leaf(string key) => key[(key.LastIndexOf('.') + 1)..];

    // ── In plain words ──────────────────────────────────────────────────

    // English, translated where shown (the language tool lists them from here).
    public static readonly string Resolution = Lang.Key("Resolution"), DisplayMode = Lang.Key("Display mode"), VSync = Lang.Key("VSync"),
        FrameLimit = Lang.Key("Frame limit"), Quality = Lang.Key("Quality"), Upscaling = Lang.Key("Upscaling"), FrameGeneration = Lang.Key("Frame generation"),
        RayTracing = Lang.Key("Ray tracing");
    public static readonly string Fullscreen = Lang.Key("Fullscreen"), Borderless = Lang.Key("Borderless window"), Windowed = Lang.Key("Windowed"),
        On = Lang.Key("On"), Off = Lang.Key("Off"), NoLimit = Lang.Key("No limit"), PathTracing = Lang.Key("Path tracing");
    private static readonly string[] UnrealLevels = [Lang.Key("Low"), Lang.Key("Medium"), Lang.Key("High"), Lang.Key("Epic"), Lang.Key("Cinematic")];

    /// <summary>The main settings in plain words, from whichever names the game used. Only what was found is said.</summary>
    internal static List<GameSettingFact> Facts(List<GameSetting> s)
    {
        var facts = new List<GameSettingFact>();
        string? Get(params string[] leaves) => s.FirstOrDefault(x => leaves.Any(l => Leaf(x.Key).Equals(l, StringComparison.OrdinalIgnoreCase) || x.Key.Equals(l, StringComparison.OrdinalIgnoreCase)))?.Value;
        static int? Int(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : null;
        static bool? Bool(string? v) => v?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "1" or "true" or "on" or "yes" or "enabled" or "enable" => true,
            "0" or "false" or "off" or "no" or "disabled" or "disable" or "none" => false,
            var x when int.TryParse(x, out int n) => n > 0,
            // A menu's own word for it: Cyberpunk saves "UI-Settings-Video-QualitySetting-Off".
            var x when x.EndsWith("-off") || x.EndsWith("_off") || x.EndsWith(".off") => false,
            var x when x.EndsWith("-on") || x.EndsWith("_on") || x.EndsWith(".on") => true,
            _ => null,
        };

        // Resolution, from the pair of names the game used.
        (string W, string H)[] pairs =
        [
            ("ResolutionSizeX", "ResolutionSizeY"), ("setting.defaultres", "setting.defaultresheight"), ("ResolutionWidth", "ResolutionHeight"),
            ("ResX", "ResY"), ("ScreenWidth", "ScreenHeight"), ("resolution_width", "resolution_height"), ("Width", "Height"),
        ];
        foreach (var (wk, hk) in pairs)
            if (Int(Get(wk)) is int w and > 300 && Int(Get(hk)) is int h and > 200) { facts.Add(new(Resolution, $"{w} × {h}")); break; }
        if (!facts.Any(f => f.Label == Resolution))
        {
            var unity = s.FirstOrDefault(x => x.Key.StartsWith("Screenmanager Resolution Width_", StringComparison.OrdinalIgnoreCase));
            var unityH = s.FirstOrDefault(x => x.Key.StartsWith("Screenmanager Resolution Height_", StringComparison.OrdinalIgnoreCase));
            if (Int(unity?.Value) is int uw and > 300 && Int(unityH?.Value) is int uh and > 200) facts.Add(new(Resolution, $"{uw} × {uh}"));
            else if (s.FirstOrDefault(x => Leaf(x.Key).Equals("resolution", StringComparison.OrdinalIgnoreCase) && Regex.Match(x.Value, @"^(\d{3,5})\s*[x×]\s*(\d{3,5})(\s*[x×@]\s*\d{2,3})?$").Success) is { } r)
                // "2560x1440x144" also has the refresh rate.
                facts.Add(new(Resolution, Regex.Replace(Regex.Replace(r.Value, @"^(\d+\s*[x×]\s*\d+).*$", "$1"), @"\s*[x×]\s*", " × ")));
        }

        // Display mode: Unreal 0/1/2, Valve's fullscreen + no border, Unity's four, or a word. A Unity game's own word wins
        // over Unity's code, as it's what its menu says (Risk of Rain 2's "Fullscreen" is Unity's borderless window).
        string? mode = null;
        if (Int(Get("FullscreenMode", "PreferredFullscreenMode", "LastConfirmedFullscreenMode")) is int ue)
            mode = ue switch { 0 => Fullscreen, 1 => Borderless, 2 => Windowed, _ => null };
        else if (Get("_displayType", "DisplayMode", "WindowMode", "displaymode", "window_mode") is null
                 && s.FirstOrDefault(x => x.Key.StartsWith("Screenmanager Fullscreen mode_", StringComparison.OrdinalIgnoreCase)) is { } um && Int(um.Value) is int u)
            mode = u switch { 0 => Fullscreen, 1 => Borderless, 2 or 3 => Windowed, _ => null };
        else if (Bool(Get("setting.fullscreen", "fullscreen", "FullScreen", "bFullscreen")) is bool full)
            mode = full ? Bool(Get("Borderless")) == true ? Borderless : Fullscreen : Bool(Get("setting.nowindowborder", "nowindowborder", "Borderless")) == true ? Borderless : Windowed;
        else if (Get("_displayType", "DisplayMode", "WindowMode", "displaymode", "window_mode") is { } word)
            mode = word.ToLowerInvariant() switch
            {
                var w when w.Contains("borderless") || w.Contains("windowedfullscreen") || w.Contains("windowed fullscreen") => Borderless,
                var w when w.Contains("window") => Windowed,
                var w when w.Contains("full") || w.Contains("exclusive") => Fullscreen,
                _ => null,
            };
        if (mode is not null) facts.Add(new(DisplayMode, mode));

        // VSync, by whichever name it has.
        if (s.FirstOrDefault(x => Leaf(x.Key).Contains("vsync", StringComparison.OrdinalIgnoreCase)) is { } vs && Bool(vs.Value) is bool v)
            facts.Add(new(VSync, v ? On : Off));

        // A frame limit: 0 or a huge number means none, and so does its own switch turned off (Cyberpunk keeps
        // MaximumFPS_Value=30 beside MaximumFPS_OnOff=false: 30 was only ever a number on a slider that's off).
        if (Bool(Get("MaximumFPS_OnOff", "FrameRateLimitEnabled", "bUseFrameRateLimit", "FPSLimitEnabled", "FrameLimitEnabled", "LimitFPS", "fps_limit_enabled", "EnableFrameRateLimit")) == false)
            facts.Add(new(FrameLimit, NoLimit));
        else if (Get("FrameRateLimit", "fps_max", "MaxFPS", "MaximumFPS_Value", "MaximumFPS", "FPSLimitValue", "max_fps", "FpsLimit", "fps_limit", "FrameLimit", "FrameRateCap", "framerate_limit", "_maxFps", "TargetFPS", "FrameCap") is { } limit)
        {
            if (Int(limit) is int n) facts.Add(new(FrameLimit, n <= 0 || n >= 999 ? NoLimit : $"{n} fps"));
            else if (Bool(limit) == false) facts.Add(new(FrameLimit, NoLimit));
        }

        // Quality: a named preset, else Unreal's scalability groups by their most common level.
        if (Get("_selectPreset", "QualityPreset", "GraphicsPreset", "QuickPresets", "Preset", "OverallQuality", "GraphicsQuality", "graphics_quality") is { } preset && preset.Length is > 0 and <= 20 && !Regex.IsMatch(preset, @"^\d+$"))
            facts.Add(new(Quality, preset));
        else if (s.Where(x => x.Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase) && x.Key.EndsWith("Quality", StringComparison.OrdinalIgnoreCase))
                     .Select(x => Int(x.Value)).OfType<int>().Where(n => n is >= 0 and <= 4).ToList() is { Count: >= 3 } levels)
        {
            var common = levels.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            facts.Add(new(Quality, common.Count() == levels.Count ? UnrealLevels[common.Key] : Lang.F(Lang.Key("Mostly {0}"), UnrealLevels[common.Key])));
        }

        // Upscaling, frame generation and ray tracing: said only when the file has them.
        // Not Unreal 3's "UpscaleScreenPercentage" (consoles' screen scaling), which Rocket League keeps on.
        var up = s.Where(x => Regex.IsMatch(Leaf(x.Key), "dlss(?!g)|fsr|xess|upsampl|super_?res|upscal(er|ing)", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(x.Key, "sharp|framegen|frame_gen|percentage|framerate|target", RegexOptions.IgnoreCase)).ToList();
        // The game's own pick of upscaler, with that upscaler's setting (Cyberpunk: ResolutionScaling=DLSS, DLSS=Balanced).
        if (s.FirstOrDefault(x => Regex.IsMatch(Leaf(x.Key), "^(ResolutionScaling|Upscaler|UpscalingMode|UpscaleMode|UpscalerType|SuperResolution)$", RegexOptions.IgnoreCase)) is { } picked
            && Tech(picked.Value) is { } pickedTech)
        {
            string? how = s.FirstOrDefault(x => Leaf(x.Key).Equals(picked.Value, StringComparison.OrdinalIgnoreCase))?.Value;
            facts.Add(new(Upscaling, how is { Length: > 0 } && Bool(how) is null ? $"{pickedTech} {how}" : pickedTech));
            up = [];
        }
        else if (s.FirstOrDefault(x => Regex.IsMatch(Leaf(x.Key), "^(ResolutionScaling|Upscaler|UpscalingMode)$", RegexOptions.IgnoreCase)) is { } none && Bool(none.Value) == false)
        {
            facts.Add(new(Upscaling, Off));
            up = [];
        }
        up = [.. up.Where(x => !Regex.IsMatch(x.Key, "preset|backend|unsupported", RegexOptions.IgnoreCase))];
        if (up.Count > 0)
        {
            var onOne = up.FirstOrDefault(x => Bool(x.Value) != false && x.Value.Length > 0);
            facts.Add(new(Upscaling, onOne is null ? Off : Tech(onOne.Key) is { } tech ? (Bool(onOne.Value) is null ? $"{tech} {onOne.Value}" : tech) : On));
        }
        if (s.FirstOrDefault(x => Regex.IsMatch(Leaf(x.Key), "framegen|frame_?generation|numFramesToGenerate|dlssg", RegexOptions.IgnoreCase)) is { } fg)
            facts.Add(new(FrameGeneration, Int(fg.Value) is int k ? k <= 0 ? Off : k == 1 ? On : $"{k}×" : Bool(fg.Value) is bool b ? b ? On : Off : fg.Value));
        // Path tracing said by name: with it on, a game's other ray tracing settings hardly matter (2026-10-25: the AI told
        // to lower Cyberpunk's Ray Traced Lighting while path tracing, by far the costliest, was on).
        if (s.Any(x => Regex.IsMatch(Leaf(x.Key), "path_?trac", RegexOptions.IgnoreCase) && !Regex.IsMatch(x.Key, "photo", RegexOptions.IgnoreCase) && Bool(x.Value) == true))
            facts.Add(new(RayTracing, PathTracing));
        else if (s.FirstOrDefault(x => Regex.IsMatch(Leaf(x.Key), "raytrac|ray_trac|^rt_|_rt$|rtx", RegexOptions.IgnoreCase)) is { } rt && Bool(rt.Value) is bool rton)
            facts.Add(new(RayTracing, rton ? On : Off));
        return facts;
    }

    private static string? Tech(string key) =>
        key.Contains("dlss", StringComparison.OrdinalIgnoreCase) ? "DLSS" : key.Contains("fsr", StringComparison.OrdinalIgnoreCase) ? "FSR"
        : key.Contains("xess", StringComparison.OrdinalIgnoreCase) ? "XeSS" : null;

    // ── This PC ─────────────────────────────────────────────────────────

    private static string? SteamFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string p && Directory.Exists(p) ? p.Replace('/', '\\') : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>A Unity game's PlayerPrefs (HKCU\Software\maker\name): the screen and quality ones, values read as numbers.</summary>
    private static (string, DateTime, IReadOnlyList<GameSetting>)? UnityRegistry(string company, string product)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\{company}\{product}");
            if (key is null) return null;
            var list = new List<GameSetting>();
            foreach (var name in key.GetValueNames())
            {
                // PlayerPrefs add "_h<hash>" to each name; strings are kept as UTF-8 bytes.
                object? v = key.GetValue(name);
                string? value = v switch
                {
                    int i => i.ToString(CultureInfo.InvariantCulture),
                    long l => l.ToString(CultureInfo.InvariantCulture),
                    byte[] b when b.Length is > 0 and <= 40 => Encoding.UTF8.GetString(b).TrimEnd('\0'),
                    string str => str,
                    _ => null,
                };
                if (value is not null) list.Add(new(name, value));
            }
            return ($@"HKEY_CURRENT_USER\Software\{company}\{product}", DateTime.MinValue, list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    // ── Windows' own settings for the game ──────────────────────────────

    /// <summary>
    /// What Windows itself is set to for a game and for games in general: its graphics preference, fullscreen
    /// optimisations, Game Mode, hardware-accelerated GPU scheduling, and the main screen's resolution and refresh rate.
    /// English, for the AI. Lines only for what could be read.
    /// </summary>
    public static List<string> WindowsFor(string? exePath)
    {
        var lines = new List<string>();
        try
        {
            if (exePath is { Length: > 0 })
            {
                using (var gpu = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    lines.Add(gpu?.GetValue(exePath) is string pref && Regex.Match(pref, @"GpuPreference=(\d)") is { Success: true } m
                        ? "Windows graphics preference for this game: " + (m.Groups[1].Value switch { "1" => "Power saving", "2" => "High performance", _ => "Let Windows decide" })
                        : "Windows graphics preference for this game: not set (Let Windows decide)");
                using var layers = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
                string flags = layers?.GetValue(exePath) as string ?? "";
                lines.Add("Fullscreen optimisations for this game: " + (flags.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase) ? "turned off in its Compatibility settings" : "on (Windows' default)"));
                if (flags.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase)) lines.Add("It is set to run as administrator.");
            }
            using (var bar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar"))
                lines.Add("Game Mode: " + (bar?.GetValue("AutoGameModeEnabled") is int g && g == 0 ? "off" : "on"));
            using (var drv = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"))
                if (drv?.GetValue("HwSchMode") is int hw) lines.Add("Hardware-accelerated GPU scheduling: " + (hw == 2 ? "on" : "off"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        if (MainScreen() is { } screen) lines.Add($"Main screen now: {screen.W} × {screen.H} at {screen.Hz} Hz.");
        return lines;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    /// <summary>The main screen's refresh rate now (Hz), or null where Windows doesn't say.</summary>
    public static int? ScreenHz() => MainScreen() is { Hz: > 1 } s ? s.Hz : null;

    private static (int W, int H, int Hz)? MainScreen()
    {
        try
        {
            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            return EnumDisplaySettings(null, -1, ref mode) && mode.PelsWidth > 0 ? (mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// How the game is set, for the AI: the main settings in plain words, every graphics setting as its file names it,
    /// and Windows' own settings for it. The file's path is said without the person's folder.
    /// </summary>
    public static string Text(GameSettingsRead read, string? exePath)
    {
        var sb = new StringBuilder();
        if (!read.Found)
            sb.AppendLine("HOW IT'S SET: RigSight couldn't find the game's saved settings on this PC (some games keep them online or in a form that can't be read). Don't guess its settings; ask what they're set to if it matters.");
        else
        {
            sb.AppendLine("HOW IT'S SET (the game's own saved settings, read just now; they're what it used the last time it was played"
                + (read.Saved is { } saved && saved > DateTime.MinValue.AddYears(1) ? $" and were last saved {saved:d MMM yyyy h:mm tt}" : "") + "):");
            if (read.Facts.Count > 0) sb.AppendLine("  " + string.Join("; ", read.Facts.Select(f => $"{f.Label} {f.Value}")) + ".");
            foreach (var f in read.Files)
                sb.AppendLine($"  Every graphics setting in {Shown(f.Path)} (the file's names, not always the menu's: say each change by its name in the game's menu): "
                    + string.Join(", ", f.Settings.Select(x => $"{x.Key}={x.Value}")));
        }
        var windows = WindowsFor(exePath);
        if (windows.Count > 0) sb.AppendLine("WINDOWS' OWN SETTINGS FOR GAMES: " + string.Join(" ", windows.Select(w => w.TrimEnd('.') + ".")));
        sb.AppendLine("RigSight only reads these; it never changes a game's settings. Advice says what to change in the game's own menu.");
        return sb.ToString();
    }

    /// <summary>A path said without the person's own folder: "~\AppData\Local\VALORANT\…".</summary>
    public static string Shown(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ? "~" + path[profile.Length..] : path;
    }
}

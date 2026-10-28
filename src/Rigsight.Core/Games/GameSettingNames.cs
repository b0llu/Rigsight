using System.Globalization;
using System.Text.RegularExpressions;

namespace Rigsight.Core.Games;

/// <summary>
/// A graphics setting by a name made from the game's own key: "Shadow distance" · "200", in its group. <see cref="Recommended"/>
/// is NVIDIA's pick for this PC when the NVIDIA App is on it and covers the setting.
/// </summary>
public sealed record GameSettingRow(string Group, string Label, string Value, string? Recommended = null, IReadOnlyList<string>? Words = null)
{
    /// <summary>
    /// NVIDIA's pick is something else ("Off" against "SMAA"; "Full-screen" and "Fullscreen" are the same). Only said when
    /// both say it the same way: both words or both numbers as the game saved them ("2" where NVIDIA says "Ultra" may well be
    /// the same choice), and for a word RigSight made itself (<see cref="Words"/>: Unreal's level 3 is "Epic", which
    /// REMATCH's menu calls "Ultra"), only when NVIDIA's word is one of the same set.
    /// </summary>
    public bool Differs => Recommended is { } rec && !GameSettingNames.Same(rec, Value)
        && (Words is { } words ? words.Any(w => GameSettingNames.Same(w, rec)) : GameSettingNames.Comparable(rec, Value));
}

/// <summary>
/// Every graphics setting of a game, read from the game's own files when its page opens and named from the game's keys
/// ("r_softparticles 1" → "Soft particles" · "On"; the person's ask, 2026-10-27: the NVIDIA App showed Risk of Rain 2's 15
/// settings where RigSight named 4). No list of games is kept: a value the game saves as a number stays that number,
/// and the AI says what it means when asked.
/// </summary>
public static class GameSettingNames
{
    // English, translated where shown (the language tool lists them from here).
    public static readonly string Screen = Lang.Key("Screen"), Quality = GameSettings.Quality, Shadows = Lang.Key("Shadows"),
        Effects = Lang.Key("Effects"), Gameplay = Lang.Key("Gameplay that costs frames");

    private static readonly string[] Order = [Screen, Quality, Shadows, Effects, Gameplay];

    /// <summary>The settings in plain words, by group.</summary>
    internal static List<GameSettingRow> Rows(IReadOnlyList<GameSetting> settings, IReadOnlyList<GameSettingFact> facts) =>
        [.. AutoRows(settings, facts).OrderBy(r => Array.IndexOf(Order, r.Group) is var i && i < 0 ? Order.Length : i)];

    // ── Named from their keys ───────────────────────────────────────────

    /// <summary>Keys the main settings already say (the screen size, Unity's screen keys): not repeated as rows.</summary>
    private static readonly Regex Said = new(@"^screenmanager|^unity\.|(^|[._])(res(olution)?_?(size)?_?[xy]|res[xy]|(screen_?|resolution_?|window_?)?(width|height))$", RegexOptions.IgnoreCase);

    /// <summary>
    /// What a game keeps for itself beside its settings: Unreal's last screen it recommended or confirmed, the window size it
    /// wants, its benchmark results and file version (REMATCH's "LastRecommendedScreenHeight=-1", "DesiredScreenWidth=1280").
    /// </summary>
    private static readonly Regex Bookkeeping = new(@"(^|\.)(LastRecommended|LastConfirmed|LastUser|LastCPU|LastGPU|LastBenchmark|Desired|bUseDesired|PreferredFullscreenMode|WindowPos|Version)",
        RegexOptions.IgnoreCase);

    private static List<GameSettingRow> AutoRows(IReadOnlyList<GameSetting> settings, IReadOnlyList<GameSettingFact> facts)
    {
        bool sizeSaid = facts.Any(f => f.Label == GameSettings.Resolution);
        var rows = new List<GameSettingRow>();
        foreach (var s in settings)
        {
            if (sizeSaid && Said.IsMatch(s.Key)) continue;
            if (Bookkeeping.IsMatch(s.Key)) continue;
            string label = Label(s.Key);
            if (label.Length < 2 || rows.Any(r => r.Label.Equals(label, StringComparison.OrdinalIgnoreCase))) continue;
            var (value, words) = AutoValue(s.Key, label, s.Value);
            rows.Add(new(GroupOf(label), label, value, Words: words));
        }
        return rows;
    }

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aa"] = "anti-aliasing", ["ao"] = "ambient occlusion", ["ssao"] = "SSAO", ["hbao"] = "HBAO", ["ssr"] = "screen-space reflections",
        ["lod"] = "LOD", ["dof"] = "depth of field", ["fov"] = "field of view", ["aniso"] = "anisotropic filtering", ["vsync"] = "VSync",
        ["fps"] = "FPS", ["hdr"] = "HDR", ["dlss"] = "DLSS", ["fsr"] = "FSR", ["xess"] = "XeSS", ["rt"] = "ray tracing", ["res"] = "resolution",
        ["tex"] = "texture", ["qual"] = "quality", ["fx"] = "effects", ["msaa"] = "MSAA", ["taa"] = "TAA", ["fxaa"] = "FXAA", ["smaa"] = "SMAA",
        ["gpu"] = "GPU", ["cpu"] = "CPU", ["vram"] = "VRAM", ["hz"] = "Hz", ["nvidia"] = "NVIDIA", ["amd"] = "AMD", ["sg"] = "", ["mip"] = "mip", ["vfx"] = "VFX",
    };

    /// <summary>"r_softparticles" → "Soft particles"; "ShadowQuality" → "Shadow quality"; "bUseVSync" → "Use VSync".</summary>
    internal static string Label(string key)
    {
        string leaf = key[(key.LastIndexOf('.') + 1)..];
        leaf = Regex.Replace(leaf, @"_h\d{5,}$", "");
        leaf = Regex.Replace(leaf, @"^(r|pp|mat|cl|gfx|setting|cvar|m|g)_", "", RegexOptions.IgnoreCase);
        leaf = Regex.Replace(leaf, @"^b(?=[A-Z])", "");
        // Names written with a capital inside the word, said as one word ("bUseVSync" is "Use VSync").
        leaf = Regex.Replace(leaf, "VSync|VRAM|SSAO|HDR", m => m.Value.ToLowerInvariant() is var w && w == "vsync" ? "_vsync_" : "_" + w + "_", RegexOptions.IgnoreCase);
        // Words from camelCase, underscores, dashes and spaces; capitals runs like "SSAO" stay one word.
        var parts = Regex.Matches(Regex.Replace(leaf, @"[_\-\s]+", " "), @"[A-Z]{2,}(?=[A-Z][a-z]|\b|\d)|[A-Z]?[a-z]+|[A-Z]+|\d+")
            .Select(m => m.Value).ToList();
        var words = parts.SelectMany(Unglue).Select(p => Words.TryGetValue(p, out var w) ? w : p.Length > 1 && p.All(char.IsUpper) ? p : p.ToLowerInvariant())
            .Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "";
        string text = string.Join(' ', words);
        return char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }

    /// <summary>Words games glue together in their keys ("softparticles", "motionblur", "foliagewind", "vfxbudget").</summary>
    private static readonly string[] Vocabulary =
    [
        "ambient", "anisotropic", "anti", "aliasing", "bias", "blur", "bloom", "budget", "cascades", "cascade", "chromatic", "aberration", "cloud", "clouds",
        "contrast", "corpses", "corpse", "cost", "count", "debris", "decal", "decals", "depth", "detail", "distance", "distortion", "draw", "dynamic", "effects",
        "field", "filter", "filtering", "fog", "foliage", "frame", "frames", "grain", "grass", "lens", "flare", "level", "light", "lights", "lighting", "limit",
        "max", "mesh", "min", "mode", "motion", "occlusion", "outline", "particle", "particles", "physics", "post", "process", "quality", "rate", "ray",
        "reflection", "reflections", "refresh", "render", "resolution", "scale", "screen", "shader", "shaders", "shadow", "shadows", "sharpen", "sharpness",
        "soft", "space", "terrain", "texture", "textures", "tracing", "tree", "trees", "view", "volumetric", "vfx", "vignette", "water", "wind", "window",
    ];

    /// <summary>
    /// A lower-case word of 8 letters or more split into graphics words when it's wholly made of them ("softparticles" →
    /// "soft", "particles"); any other word as it is.
    /// </summary>
    private static IEnumerable<string> Unglue(string word)
    {
        if (word.Length < 8 || !word.All(char.IsLower)) return [word];
        // Splits[i]: the words that make up word[..i], or null when none do; longer words first.
        var splits = new List<string>?[word.Length + 1];
        splits[0] = [];
        for (int i = 0; i < word.Length; i++)
        {
            if (splits[i] is not { } before) continue;
            foreach (var v in Vocabulary)
                if (i + v.Length <= word.Length && string.CompareOrdinal(word, i, v, 0, v.Length) == 0 && (splits[i + v.Length] is null || splits[i + v.Length]!.Count > before.Count + 1))
                    splits[i + v.Length] = [.. before, v];
        }
        return splits[word.Length] is { Count: > 1 } found ? found : [word];
    }

    private static readonly Regex CountLike = new(@"cascade|count|max|min|distance|scale|level|quality|limit|size|budget|bias|sample|radius|index|width|height|fps|number|amount|lod|preset|mode|percent|frame|refresh|gamma|brightness|contrast|sharp|fov|field of view|detail|type|resolution",
        RegexOptions.IgnoreCase);

    private static readonly string[] Switches = [GameSettings.On, GameSettings.Off];

    /// <summary>The value as a menu would say it, with the set of words it was made from when RigSight made it (null: as the game saved it).</summary>
    private static (string Value, IReadOnlyList<string>? Words) AutoValue(string key, string label, string raw)
    {
        // Unreal's scalability groups: 0 to 4 are Low to Cinematic in every Unreal game's menu (not sg.ResolutionQuality, a percentage).
        if (key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase) && key.EndsWith("Quality", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("sg.ResolutionQuality", StringComparison.OrdinalIgnoreCase) && int.TryParse(raw, out int lvl) && lvl is >= 0 and <= 4) return (GameSettings.UnrealLevels[lvl], GameSettings.UnrealLevels);
        string v = raw.Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return (GameSettings.On, Switches);
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return (GameSettings.Off, Switches);
        if (v is "0" or "1" && !CountLike.IsMatch(label)) return (v == "1" ? GameSettings.On : GameSettings.Off, Switches);
        // A frame limit of 0 is none ("FrameRateLimit=0.000000").
        if (Regex.IsMatch(label, @"frame ?rate limit|fps (max|limit|cap)|max fps|frame limit|frame cap", RegexOptions.IgnoreCase) && Num(v) is double cap && cap <= 0) return (GameSettings.NoLimit, [GameSettings.NoLimit]);
        // A screen size, maybe with its refresh rate ("2560x1440x144").
        if (Regex.Match(v, @"^(\d{3,5})\s*[x×]\s*(\d{3,5})(\s*[x×@]\s*\d{2,3})?$") is { Success: true } size) return ($"{size.Groups[1].Value} × {size.Groups[2].Value}", null);
        // A word saved in CamelCase, as a menu would say it: "OutOfSight" → "Out of sight", "HardOnly" → "Hard only".
        if (Regex.IsMatch(v, "^[A-Z][a-z]+([A-Z][a-z]+)+$")) return (Regex.Replace(v, "(?<=[a-z])([A-Z])", m => " " + m.Value.ToLowerInvariant()), null);
        return (Tidy(v), null);
    }

    /// <summary>"30.00361" → "30"; "0.700000" → "0.7"; words as they are.</summary>
    private static string Tidy(string raw) =>
        Num(raw) is double n && raw.Contains('.') ? n.ToString(Math.Abs(n) >= 10 ? "0" : "0.##", CultureInfo.InvariantCulture) : raw;

    private static string GroupOf(string label) => label.ToLowerInvariant() switch
    {
        var l when Regex.IsMatch(l, "shadow") => Shadows,
        var l when Regex.IsMatch(l, "distortion|blur|bloom|grain|aberration|vignette|flare") => Effects,
        var l when Regex.IsMatch(l, @"resolution|window|display|vsync|fps|frame|refresh|screen|monitor|fullscreen|borderless|upscal|dlss|fsr|xess|hdr|gamma|brightness|latency|reflex") => Screen,
        var l when Regex.IsMatch(l, @"texture|anti-aliasing|msaa|taa|fxaa|smaa|anisotropic|lod|quality|preset|detail|mesh|draw|view distance|tessellation|filter|streaming") => Quality,
        var l when Regex.IsMatch(l, @"corpse|ragdoll|gib|debris|gore|crowd|physics") => Gameplay,
        _ => Effects,
    };

    // ── Shared ──────────────────────────────────────────────────────────

    internal static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>
    /// A setting's name as a key to hold against another's: letters only, its words in order of the alphabet ("Corpses max"
    /// and NVIDIA's "Max Corpses" are the same), and the usual other names of a setting as one (SSAO is ambient occlusion).
    /// </summary>
    internal static string NameKey(string label)
    {
        string l = Regex.Replace(label.ToLowerInvariant(), @"\(.*?\)", " ");
        l = Regex.Replace(l, @"\b(ssao|hbao\+?|ao)\b", "ambient occlusion");
        l = Regex.Replace(l, @"\banti-?aliasing\b|\baa\b", "antialiasing");
        var words = Regex.Matches(l, "[a-z0-9]+").Select(m => m.Value.TrimEnd('s')).Where(w => w.Length > 0).OrderBy(w => w, StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    /// <summary>Whether two values can be held against each other: both numbers, or both words.</summary>
    public static bool Comparable(string a, string b) => (Num(a) is null) == (Num(b) is null);

    /// <summary>Two values that say the same choice: letters and digits only, any case ("Full-screen" and "Fullscreen"), or the same number.</summary>
    public static bool Same(string a, string b) =>
        GameSettings.Norm(a) == GameSettings.Norm(b) || (Num(a) is double x && Num(b) is double y && Math.Abs(x - y) < 1e-6);

    /// <summary>A setting's name inside a sentence: "anti-aliasing", but "LOD bias" and "SSAO" keep their capitals.</summary>
    public static string InSentence(string label) =>
        label.Length > 1 && char.IsUpper(label[0]) && char.IsLower(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;
}

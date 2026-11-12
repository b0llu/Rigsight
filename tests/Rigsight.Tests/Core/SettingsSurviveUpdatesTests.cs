using System.Text.Json.Nodes;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>
/// An update never changes a setting someone already set (the user's rule, 2026-11-08: "none of the settings user has
/// already set should change after any update ... even if we are touching the same area ... unless we are removing a
/// specific setting"). Fixtures/settings holds a settings file as each release saves it, with every setting moved away
/// from its default; each is loaded with today's code and must come back value for value.
/// <para>
/// A release that adds settings adds its own file: a copy of the newest one, named for that release, with the new
/// settings set to something other than their defaults. The files already there are never edited: they are what
/// people's PCs have. A setting taken out for good goes in <see cref="Removed"/>, with why.
/// </para>
/// </summary>
public sealed class SettingsSurviveUpdatesTests
{
    /// <summary>Settings taken out for good: their path ("Alerts.CardSeconds") and why. Only these may be missing after loading.</summary>
    private static readonly Dictionary<string, string> Removed = new()
    {
        ["WidgetFade"] = "the fade under the pointer was taken out at the user's word (2026-11-12): widgets stay solid, and hover boxes need them readable",
        ["Widgets[].Locked"] = "lock in place (click-through) was taken out at the user\'s word (2026-11-12): every widget answers the pointer, for hover and scrolling; a widget saved locked comes back movable",
    };

    /// <summary>Parts of the file that aren't someone's settings, so they may change: the path and why.</summary>
    private static readonly Dictionary<string, string> NotSettings = new()
    {
        ["SettingsVersion"] = "the file's own version, raised by each migration",
    };

    /// <summary>
    /// Settings the newest file may leave at their default, and why: for the rest, a value equal to the default couldn't
    /// tell "kept" from "reset".
    /// </summary>
    private static readonly Dictionary<string, string> MayBeDefault = new()
    {
        ["SettingsVersion"] = "not a setting",
        ["TrayGraphs"] = "retired in 1.2 (read once into TrayLooks), always saved empty",
        ["HistoryFile"] = "the history file check's own state, not a setting (still compared on loading)",
    };

    /// <summary>A path in <see cref="MayBeDefault"/>, or inside one.</summary>
    private static bool MayBe(string path) =>
        MayBeDefault.Keys.Any(p => path == p || path.StartsWith(p + ".", StringComparison.Ordinal) || path.StartsWith(p + "[", StringComparison.Ordinal));

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings");

    /// <summary>The saved files, oldest release first.</summary>
    private static List<string> Files() =>
        [.. Directory.GetFiles(Folder, "*.json").OrderBy(f => Version.Parse(Path.GetFileNameWithoutExtension(f)))];

    public static TheoryData<string> Releases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Files()) data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    private static JsonNode Saved(string release) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, release + ".json")))!;

    /// <summary>The file as today's RigSight reads it and saves it again.</summary>
    private static JsonNode Updated(string release) =>
        JsonNode.Parse(SettingsStore.Serialize(SettingsStore.Deserialize(File.ReadAllText(Path.Combine(Folder, release + ".json")))))!;

    private static JsonNode Defaults => JsonNode.Parse(SettingsStore.Serialize(new RigsightSettings()))!;

    [Fact]
    public void There_is_a_saved_file_from_a_release() => Assert.NotEmpty(Files());

    [Theory]
    [MemberData(nameof(Releases))]
    public void A_settings_file_from_an_earlier_release_keeps_every_setting(string release)
    {
        var changed = new List<string>();
        Compare(Saved(release), Updated(release), "", changed);
        Assert.True(changed.Count == 0,
            $"Loading the settings saved by {release} changed what someone had set:\n  " + string.Join("\n  ", changed)
            + "\nAn update keeps every setting as it was. Only a setting removed for good may go (add it to Removed, with why).");
    }

    [Fact]
    public void The_newest_saved_file_has_every_setting()
    {
        string newest = Path.GetFileNameWithoutExtension(Files()[^1]);
        var missing = new List<string>();
        Covers(Defaults, Saved(newest), "", missing);
        Assert.True(missing.Count == 0,
            $"These settings aren't in any saved file, so nothing checks that an update keeps them:\n  " + string.Join("\n  ", missing)
            + $"\nCopy Fixtures/settings/{newest}.json to a file named for the coming release, and set these to something other than their defaults there.");
    }

    [Fact]
    public void The_newest_saved_file_moves_every_setting_away_from_its_default()
    {
        string newest = Path.GetFileNameWithoutExtension(Files()[^1]);
        var same = new List<string>();
        SameAsDefault(Defaults, Saved(newest), "", same);
        Assert.True(same.Count == 0,
            $"In Fixtures/settings/{newest}.json these are at their defaults, so a reset to the default would go unnoticed:\n  "
            + string.Join("\n  ", same) + "\nGive them another value there (in a new file for a coming release), or add them to MayBeDefault with why.");
    }

    /// <summary>A path taken out for good; a list item's place doesn't matter ("Widgets[3].Locked" is "Widgets[].Locked").</summary>
    private static bool IsRemoved(string path) => Removed.ContainsKey(System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[]"));

    /// <summary>Everything in <paramref name="before"/> must be in <paramref name="after"/> with the same value; what's new in after is fine.</summary>
    private static void Compare(JsonNode? before, JsonNode? after, string path, List<string> changed)
    {
        if (IsRemoved(path) || NotSettings.ContainsKey(path)) return;
        switch (before)
        {
            case JsonObject b:
                if (after is not JsonObject a) { changed.Add($"{Show(path)}: was an object, now {Text(after)}"); return; }
                foreach (var (key, value) in b)
                {
                    string at = Join(path, key);
                    if (IsRemoved(at) || NotSettings.ContainsKey(at)) continue;
                    if (!a.TryGetPropertyValue(key, out var now)) changed.Add($"{at}: gone (was {Text(value)})");
                    else Compare(value, now, at, changed);
                }
                return;
            case JsonArray list when after is JsonArray now && now.Count == list.Count:
                for (int i = 0; i < list.Count; i++) Compare(list[i], now[i], $"{path}[{i}]", changed);
                return;
            default:
                if (!Same(before, after)) changed.Add($"{Show(path)}: was {Text(before)}, now {Text(after)}");
                return;
        }
    }

    /// <summary>
    /// Every key the settings have today is in the saved file (inside lists of things, in at least one of them). Lists
    /// that are empty by default can't say what their items hold, so those items aren't checked.
    /// </summary>
    private static void Covers(JsonNode? defaults, JsonNode? saved, string path, List<string> missing)
    {
        switch (defaults)
        {
            case JsonObject d:
                foreach (var (key, value) in d)
                {
                    string at = Join(path, key);
                    if (saved is not JsonObject s || !s.TryGetPropertyValue(key, out var there)) missing.Add(at);
                    else if (value is JsonObject or JsonArray) Covers(value, there, at, missing);
                }
                return;
            case JsonArray d when d.Count > 0 && d[0] is JsonObject item:
                var items = (saved as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                foreach (var (key, _) in item)
                    if (!items.Any(i => i.ContainsKey(key))) missing.Add($"{path}[].{key}");
                return;
        }
    }

    /// <summary>Each setting outside lists that the saved file leaves at its default.</summary>
    private static void SameAsDefault(JsonNode? defaults, JsonNode? saved, string path, List<string> same)
    {
        if (defaults is JsonObject d && saved is JsonObject s)
        {
            foreach (var (key, value) in d)
                if (s.TryGetPropertyValue(key, out var there)) SameAsDefault(value, there, Join(path, key), same);
            return;
        }
        if (Same(defaults, saved) && !MayBe(path)) same.Add(path);
    }

    /// <summary>The same value; a moment written with another offset is the same moment.</summary>
    private static bool Same(JsonNode? x, JsonNode? y)
    {
        if (JsonNode.DeepEquals(x, y)) return true;
        return x is JsonValue vx && y is JsonValue vy && vx.TryGetValue(out string? sx) && vy.TryGetValue(out string? sy)
            && sx.Contains('T') && DateTimeOffset.TryParse(sx, out var tx) && DateTimeOffset.TryParse(sy, out var ty) && tx == ty;
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    private static string Show(string path) => path.Length == 0 ? "(the whole file)" : path;

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";
}

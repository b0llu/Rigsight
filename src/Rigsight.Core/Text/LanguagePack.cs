using System.Text.Json;

namespace Rigsight.Core;

/// <summary>A language the app can be shown in, before its pack is downloaded.</summary>
/// <param name="Code">"es"; also the pack's file name (es.json) and its release asset (Rigsight-Language-es.json).</param>
/// <param name="Name">The language's own name for itself, as the picker lists it: "Español".</param>
/// <param name="SizeKB">About how big its pack is, shown before it's downloaded (a test keeps it near the real size).</param>
/// <param name="Reopens">What happens when it's picked, said in the language in use: "RigSight opens again in Spanish."</param>
public sealed record LanguageInfo(string Code, string Name, int SizeKB, string Reopens);

/// <summary>
/// One language's translation of the app: every English text (the key, see <see cref="Lang"/>) to its own words.
/// Packs are plain text, downloaded only when someone picks the language, so nothing in them can run.
/// </summary>
public sealed class LanguagePack
{
    public const string English = "en";

    /// <summary>The languages the picker offers. English is built in; the rest come as packs.</summary>
    public static IReadOnlyList<LanguageInfo> Known { get; } =
    [
        new(English, "English", 0, Lang.Key("RigSight opens again in English.")),
        new("es", "Español", 310, Lang.Key("RigSight opens again in Spanish.")),
    ];

    public string Code { get; }
    public string Name { get; }

    /// <summary>Counts up with each fix to the pack, so a newer one for the same app version can replace it.</summary>
    public int Version { get; }

    /// <summary>The app version the pack was made for ("0.30.0"). An older pack still works; newer text stays English.</summary>
    public string ForApp { get; }

    /// <summary>English text to translation, with only the entries that fit (see <see cref="Read"/>).</summary>
    public IReadOnlyDictionary<string, string> Strings { get; }

    /// <summary>Entries left out because their holes didn't match the English ones, for the log.</summary>
    public IReadOnlyList<string> Dropped { get; }

    private LanguagePack(string code, string name, int version, string forApp, Dictionary<string, string> strings, List<string> dropped)
    {
        Code = code;
        Name = name;
        Version = version;
        ForApp = forApp;
        Strings = strings;
        Dropped = dropped;
    }

    /// <summary>
    /// Reads a pack. An entry is kept only when it fills the same holes as its English text ({0}, {1} ...), each at
    /// least once and no others, so a mistake in one sentence leaves that sentence English instead of breaking the page.
    /// Throws <see cref="InvalidDataException"/> when the file isn't a pack at all.
    /// </summary>
    public static LanguagePack Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            string code = root.GetProperty("code").GetString() ?? "";
            if (code.Length is < 2 or > 16 || code == English || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                throw new InvalidDataException($"Not a language code: \"{code}\"");
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? code : code;
            int version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out int vv) ? vv : 0;
            string forApp = root.TryGetProperty("forApp", out var f) ? f.GetString() ?? "" : "";

            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            var dropped = new List<string>();
            foreach (var entry in root.GetProperty("strings").EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) { dropped.Add(entry.Name); continue; }
                string text = entry.Value.GetString()!;
                if (text.Length == 0) continue; // not translated yet: stays English
                if (Fits(entry.Name, text)) strings[entry.Name] = text;
                else dropped.Add(entry.Name);
            }
            return new LanguagePack(code, name, version, forApp, strings, dropped);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Not a language pack: " + ex.Message, ex);
        }
    }

    public static LanguagePack Load(string path) => Read(File.ReadAllText(path));

    /// <summary>Whether a translation fills exactly the English text's holes and would format without an error.</summary>
    public static bool Fits(string english, string text)
    {
        int[] holes = Lang.Holes(english);
        if (!holes.SequenceEqual(Lang.Holes(text))) return false;
        if (holes.Length == 0) return true; // used as it is, never formatted
        try
        {
            // Empty values skip each hole's format, so this checks only that the braces are well formed.
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, new object?[holes[^1] + 1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

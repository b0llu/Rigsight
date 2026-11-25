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

    /// <summary>
    /// Written before a month's name when a day comes first ("de " in Spanish: "19 de octubre"); empty for none.
    /// Every "d MMMM" date in the app follows it (.NET's genitive month names), so no date pattern needs translating.
    /// </summary>
    public string MonthPrefix { get; init; } = "";

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
    public static LanguagePack Read(string json) => Read(System.Text.Encoding.UTF8.GetBytes(json));

    /// <summary>
    /// Reads a pack from its UTF-8 bytes, one token at a time. Not through JsonDocument: it rents its buffers from .NET's
    /// shared pool, which keeps them for reuse, and that left about 2.3 MB held for good in the agent and the app after
    /// a pack was read (measured 2026-11-25: the Spanish end-to-end stage's agent was 3.5 MB over the English one; 1.6 MB
    /// of it is the translations, which have to stay).
    /// </summary>
    public static LanguagePack Read(byte[] utf8)
    {
        try
        {
            // A file saved with a byte order mark (as File.ReadAllText used to drop it).
            ReadOnlySpan<byte> bytes = utf8.AsSpan();
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidOperationException("the file isn't a JSON object");
            string? code = null, name = null, forApp = null, monthPrefix = null;
            int version = 0;
            Dictionary<string, string>? strings = null;
            var dropped = new List<string>();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string property = reader.GetString()!;
                reader.Read();
                switch (property)
                {
                    case "code": code = reader.GetString(); break;
                    case "name": name = reader.GetString(); break;
                    case "forApp": forApp = reader.GetString(); break;
                    case "monthPrefix": monthPrefix = reader.GetString(); break;
                    case "version": version = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int v) ? v : 0; break;
                    case "strings":
                        if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidOperationException("\"strings\" isn't an object");
                        strings = new Dictionary<string, string>(StringComparer.Ordinal);
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            string english = reader.GetString()!;
                            reader.Read();
                            if (reader.TokenType != JsonTokenType.String) { dropped.Add(english); reader.Skip(); continue; }
                            string text = reader.GetString()!;
                            if (text.Length == 0) continue; // not translated yet: stays English
                            if (Fits(english, text)) strings[english] = text;
                            else dropped.Add(english);
                        }
                        break;
                    default: reader.Skip(); break; // "stale", anything a newer tool adds
                }
            }
            if (code is null) throw new KeyNotFoundException("no \"code\"");
            if (code.Length is < 2 or > 16 || code == English || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                throw new InvalidDataException($"Not a language code: \"{code}\"");
            if (strings is null) throw new KeyNotFoundException("no \"strings\"");
            monthPrefix ??= "";
            if (monthPrefix.Length > 12) monthPrefix = "";
            return new LanguagePack(code, name ?? code, version, forApp ?? "", strings, dropped) { MonthPrefix = monthPrefix };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Not a language pack: " + ex.Message, ex);
        }
    }

    public static LanguagePack Load(string path) => Read(File.ReadAllBytes(path));

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

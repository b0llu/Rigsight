using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rigsight.Core;
using Rigsight.LangTool;

// Keeps the language packs (languages\<code>.json) in step with the app's English.
//
//   sync    <pack>                  every text the app shows gets an entry (new ones empty, to translate); entries whose
//                                   English is gone move to "stale" with their old translation, for reference when the
//                                   same idea was only reworded
//   check   <pack>                  lists what isn't translated, or doesn't fit; exit code 1 if anything
//   release <pack> <version> <out>  writes the release asset: translated entries only, made for <version>
//   list                            every text and where it's used
//   dump    <out.json>              the same as JSON, for translators
//
// Run from the repo root: dotnet run --project tools\Rigsight.LangTool -- sync languages\es.json

string repo = Directory.GetCurrentDirectory();
string src = Path.Combine(repo, "src");
if (!Directory.Exists(src)) return Fail("Run from the repo root (no src folder here).");

string command = args.ElementAtOrDefault(0) ?? "";
var scan = TextScanner.Scan(src);

switch (command)
{
    case "list":
        foreach (var (key, sites) in scan.Texts)
            Console.WriteLine($"{key}    [{string.Join(", ", sites.Select(s => $"{s.File}:{s.Line}"))}]");
        Console.WriteLine($"{scan.Texts.Count} texts; {scan.NotWritten.Count} lookups of a text kept elsewhere.");
        return 0;

    case "dump" when args.Length == 2:
    {
        // Every text with where it's used, for translators: [{ "en": "...", "where": ["file:line", ...] }, ...]
        var rows = new JsonArray();
        foreach (var (key, sites) in scan.Texts)
            rows.Add(new JsonObject { ["en"] = key, ["where"] = new JsonArray([.. sites.Take(4).Select(st => (JsonNode?)JsonValue.Create($"{st.File}:{st.Line}"))]) });
        File.WriteAllText(args[1], rows.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"{args[1]}: {rows.Count} texts");
        return 0;
    }

    case "sync" when args.Length == 2:
    {
        var pack = ReadPack(args[1]);
        var strings = pack["strings"]!.AsObject();
        var stale = pack["stale"]?.AsObject() ?? [];
        var fresh = new JsonObject();
        foreach (var key in scan.Texts.Keys)
        {
            string text = strings[key]?.GetValue<string>() ?? stale[key]?.GetValue<string>() ?? "";
            fresh[key] = text;
        }
        var newStale = new JsonObject();
        foreach (var (key, value) in stale)
            if (!scan.Texts.ContainsKey(key)) newStale[key] = value?.DeepClone();
        int added = 0, retired = 0;
        foreach (var (key, value) in strings)
            if (!scan.Texts.ContainsKey(key) && value?.GetValue<string>() is { Length: > 0 }) { newStale[key] = value.DeepClone(); retired++; }
        foreach (var key in scan.Texts.Keys)
            if (!strings.ContainsKey(key) && !stale.ContainsKey(key)) added++;
        pack["strings"] = fresh;
        pack["stale"] = newStale;
        WritePack(args[1], pack);
        int todo = fresh.Count(kv => kv.Value?.GetValue<string>() is not { Length: > 0 });
        Console.WriteLine($"{args[1]}: {fresh.Count} texts, {added} new, {retired} moved to stale, {todo} to translate.");
        return 0;
    }

    case "check" when args.Length == 2:
    {
        var pack = LanguagePack.Load(args[1]);
        var missing = scan.Texts.Keys.Where(k => !pack.Strings.ContainsKey(k) && !pack.Dropped.Contains(k)).ToList();
        foreach (var key in pack.Dropped)
            Console.WriteLine($"DOESN'T FIT  {key}    [{Where(key)}]");
        foreach (var key in missing)
            Console.WriteLine($"NOT DONE     {key}    [{Where(key)}]");
        // A text in the pack twice: "release" (and so the installer) can't make the pack from it (2026-10-27).
        var twice = Twice(args[1]);
        foreach (var key in twice)
            Console.WriteLine($"TWICE        {key}");
        Console.WriteLine($"{pack.Name}: {scan.Texts.Count} texts, {missing.Count} not translated, {pack.Dropped.Count} that don't fit{(twice.Count > 0 ? $", {twice.Count} in it twice" : "")}.");
        return missing.Count + pack.Dropped.Count + twice.Count == 0 ? 0 : 1;
    }

    case "release" when args.Length == 4:
    {
        var pack = ReadPack(args[1]);
        if (ReleaseVersion(args[2]) is not { } version) return Fail($"Not a version: {args[2]}");
        var output = new JsonObject
        {
            ["code"] = pack["code"]?.DeepClone(),
            ["name"] = pack["name"]?.DeepClone(),
            ["version"] = pack["version"]?.DeepClone(),
            ["forApp"] = version,
            ["monthPrefix"] = pack["monthPrefix"]?.DeepClone(),
            ["strings"] = new JsonObject(pack["strings"]!.AsObject()
                .Where(kv => scan.Texts.ContainsKey(kv.Key) && kv.Value?.GetValue<string>() is { Length: > 0 })
                .Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value!.DeepClone()))),
        };
        WritePack(args[3], output);
        var check = LanguagePack.Load(args[3]); // the app reads it the same way
        Console.WriteLine($"{args[3]}: {check.Name} for {version}, {check.Strings.Count} texts, {check.Dropped.Count} that don't fit.");
        return check.Dropped.Count == 0 ? 0 : 1;
    }

    default:
        return Fail("Usage: sync <pack> | check <pack> | release <pack> <version> <out> | list");
}

string Where(string key) =>
    scan.Texts.TryGetValue(key, out var sites) ? string.Join(", ", sites.Take(3).Select(s => $"{s.File}:{s.Line}")) : "no longer used";

static string? ReleaseVersion(string v) =>
    Version.TryParse(v.TrimStart('v'), out var parsed) ? parsed.ToString(3) : null;

/// <summary>The texts that are in a pack's strings more than once.</summary>
static List<string> Twice(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    if (!doc.RootElement.TryGetProperty("strings", out var strings)) return [];
    return [.. strings.EnumerateObject().GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key)];
}

static JsonObject ReadPack(string path)
{
    if (!File.Exists(path)) return new JsonObject { ["code"] = Path.GetFileNameWithoutExtension(path), ["name"] = "", ["version"] = 1, ["strings"] = new JsonObject() };
    return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!.AsObject();
}

static void WritePack(string path, JsonObject pack)
{
    // Readable by people: accented letters as they are, one entry a line.
    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, pack.ToJsonString(options) + "\n", new System.Text.UTF8Encoding(false));
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

using System.IO;
using Rigsight.Core.Ask;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Services;

// Asks Ask every question in a file (one a line; a line starting with ">" is a follow-up to the one before; "#" a
// comment) against a database, and prints how each was read and the whole answer. For reading many answers at once
// after a change. Reads the database read-only (safe while Rigsight is running).
//
//   Rigsight.AskAudit <questions.txt> [<rigsight.db>] [--settings <settings.json>] [--no-model] [--brief] [--fahrenheit]
//     (--brief: only how each was read and the first two lines of its answer, for skimming hundreds)
//     (no database: the installed Rigsight's, with its settings)
var arg = (string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rigsight");
var plain = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
if (arg("--settings") is { } sf) plain.Remove(sf);
string questions = plain.Count > 0 ? plain[0] : throw new Exception("Usage: Rigsight.AskAudit <questions.txt> [<rigsight.db>]");
string db = plain.Count > 1 ? plain[1] : Path.Combine(data, "rigsight.db");
string? settingsFile = arg("--settings") ?? (db == Path.Combine(data, "rigsight.db") ? Path.Combine(data, "settings.json") : null);

using var reader = RigsightDb.OpenReader(db) ?? throw new Exception($"Can't open {db}");
var settings = settingsFile is not null && File.Exists(settingsFile) ? SettingsStore.Load(settingsFile) : new RigsightSettings();
if (args.Contains("--fahrenheit")) settings.UseFahrenheit = true;
Rigsight.Core.Units.Fahrenheit = settings.UseFahrenheit;
using var embedder = new AskEmbedder();
var engine = new AskEngine(new AskRouter(args.Contains("--no-model") || !AskEmbedder.Available ? null : embedder));

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"# {db}  (model: {(engine.Router is not null && AskEmbedder.Available && !args.Contains("--no-model") ? "yes" : "no")})");
Console.WriteLine("Starters: " + string.Join(" | ", engine.Starters(reader, settings)));
AskContext? context = null;
var watch = new System.Diagnostics.Stopwatch();
foreach (string raw in File.ReadLines(questions))
{
    string line = raw.Trim();
    if (line.Length == 0 || line.StartsWith('#')) continue;
    bool follows = line.StartsWith('>');
    if (follows) line = line[1..].Trim(); else context = null;
    watch.Restart();
    var all = engine.AskAll(reader, settings, line, context);
    long ms = watch.ElapsedMilliseconds;
    foreach (var a in all)
    {
    var q = a.Query!;
    Console.WriteLine();
    Console.WriteLine($"Q{(follows ? " (follow-up)" : "")}: {line}");
    Console.WriteLine($"   [{q.Intent} {q.Score:0.00}{(q.Why ? " why" : "")}{(q.Last ? " last" : "")}{(q.Games ? " games" : "")}{(q.HowTo ? " howto" : "")}{(q.FollowsUp ? " follows" : "")}"
        + $"{(q.Period is { } p ? $" | {p.Label} {p.From:MM-dd HH:mm}..{p.To:MM-dd HH:mm}" : "")}{(q.App is not null ? $" | app={q.App}" : "")}{(q.Part != AskPart.None ? $" | part={q.Part}" : "")} | {ms} ms]  read: {a.Read}");
    if (a.Carried is not null) Console.WriteLine($"   << {a.Carried} | instead: {a.CarriedInstead}");
    if (a.AppChoices.Count > 0) Console.WriteLine("   pick: " + string.Join(" | ", a.AppChoices.Select(c => c.Name)));
    bool brief = args.Contains("--brief");
    foreach (string l in a.PlainText().Split(Environment.NewLine).Take(brief ? 2 : int.MaxValue)) Console.WriteLine("   " + l);
    if (brief) { if (a.Understood) context = a.Context; continue; }
    if (a.Links.Count > 0) Console.WriteLine("   -> " + string.Join(", ", a.Links.Select(l => $"{l.Text}{(l.Day is { } d ? $" ({d:d MMM})" : "")}")));
    if (a.FollowUps.Count > 0) Console.WriteLine("   ?? " + string.Join(" | ", a.FollowUps));
    if (a.Understood) context = a.Context;
    }
}

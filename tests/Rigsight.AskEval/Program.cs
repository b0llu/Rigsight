using System.Text;
using System.Text.Json;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;

// Asks RigSight AI a fixed set of questions, each on the plans it is about, and writes what came back: the answer as
// written, its look-ups, the buttons RigSight would show (AiActions.Resolve, as the app does) and any "[locked: …]" line.
// Run it before and after a prompt change and compare the two files.
//
//   set RIGSIGHT_DATA_DIR=<a copy of a test copy's data folder>   (so it is its own PC to the relay)
//   set RIGSIGHT_RELAY_URL=http://127.0.0.1:8787                  (wrangler dev; never the real relay)
//   Rigsight.AskEval <out.md> <free|complete|all> [question ids, comma separated]
//
// The plan itself is the relay's: give the eval PC a plan in wrangler dev's local database before each run.

if (args.Length < 2) { Console.Error.WriteLine("Rigsight.AskEval <out.md> <free|complete> [ids]"); return 2; }
if (Environment.GetEnvironmentVariable("RIGSIGHT_RELAY_URL") is not { Length: > 0 } relayUrl || relayUrl.Contains("rigsight.app"))
{
    Console.Error.WriteLine("Set RIGSIGHT_RELAY_URL to a local relay (wrangler dev); this never asks the real one.");
    return 2;
}
string outFile = args[0], plan = args[1];
var only = args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : null;

// The drive scan the app would have after Scan was pressed (E, as RigSight words it for the AI).
const string ScanOfE = """
Scan of E: finished just now: 677.6 GB in 58,083 files.
Biggest folders (with the biggest inside each):
  E:\SteamLibrary: 464.0 GB (68%), 3,120 files
    E:\SteamLibrary\steamapps\common\Call of Duty: 211.4 GB
    E:\SteamLibrary\steamapps\common\Cyberpunk 2077: 92.7 GB
  E:\Videos: 89.8 GB (13%), 1,204 files
  E:\Downloads: 56.8 GB (8%), 2,310 files
Biggest files (by kind and folder; their names aren't sent):
  38.2 GB: a .mkv file in E:\Videos
  21.8 GB: a .zip file in E:\Downloads
Nothing on RigSight's list of space that is safe to free (temporary files, caches, old downloads, the Recycle Bin) is on E:.
Folder names are the person's own: say what a folder probably is only from its name (a Steam library, videos) and never invent what is inside. Suggest deleting only what the free list names, or what can be got back (a game installed again from its launcher, uninstalled from Installed apps); never a file of their own. [do: open storage <letter>] opens this scan on the Storage page; [do: windows storage-sense] or disk-cleanup clears Windows' own files.
""";
const string DrivesNow = """
  C: 241.3 GB of 446.0 GB used (54%), health: Healthy
  E: 680.5 GB of 931.5 GB used (73%), health: Healthy
  F: 690.3 GB of 1,863.0 GB used (37%), health: Healthy
""";

// id, question, which plans it's asked on, whether E has been scanned, what to look for.
var questions = new (string Id, string Text, string Plans, bool Scanned, string Expect)[]
{
    ("ok", "Is my PC ok?", "free,complete", false, "short health answer from today; no plan talk unless needed"),
    ("hey", "hey", "free,complete", false, "small talk: no look-ups, no buttons, no next step"),
    ("temps", "Are my temperatures normal for my CPU?", "free,complete", false, "temps + web limit; no locked line"),
    ("shutoff", "Why did my PC shut off last?", "free,complete", false, "Free: says Crashes isn't in the plan once + [locked: crashes]; Complete: finding + open crashes"),
    ("fast", "how do I make my PC fast?", "complete", false, "running now; close button only if something really holds memory"),
    ("game", "Which game should I play?", "complete", false, "from what they play, not load"),
    ("energy", "How much energy did my PC use this week?", "free,complete", false, "kWh from the report"),
    ("changed", "What changed on my PC this week?", "complete", false, "changes; timeline"),
    ("driver", "Is the driver for my RTX 3080 Ti up to date?", "free,complete", false, "web; NEW: [do: app nvidia-app]"),
    ("drive", "why is my E drive full?", "free,complete", false, "Free: real reason once, no scan button, [locked: storage | …], open storage E; Complete: scan E offered"),
    ("drivescanned", "why is my E drive full?", "complete", true, "answers from the scan; no scan button; open storage E"),
    ("crashgame", "My game keeps crashing on start, what do I do?", "complete", false, "verify for a Steam game if one; else ask which"),
    ("update", "How do I check for Windows updates?", "free,complete", false, "NEW: [do: windows windows-update]"),
    ("devmgr", "Where can I see if my graphics card driver has a problem in Windows?", "free,complete", false, "NEW: [do: windows device-manager]"),
    ("fan", "How do I change my mouse lighting?", "free,complete", false, "NEW: [do: app g-hub] (installed here)"),
};

// Questions that state something false about this PC with full confidence (the person's idea, 2026-10-24, after an
// answer about an "I drive" that isn't there). Each should be checked against the records and corrected, never played along.
// The truth, from the history these run on: RTX 3080 Ti (driver 621.01), Ryzen 7 5700X3D, 16 GB, Windows 11 25H2, drives
// C D E F, CPU minute peaks ~78-83 °C, fans up to ~3,700 rpm, 3 blue screens, no Cyberpunk or Valorant ever played.
var wrong = new (string Id, string Text, string Plans, bool Scanned, string Expect)[]
{
    ("w-gpu", "My RTX 5060 is running really hot in games, why?", "free,complete", false, "corrects: it's an RTX 3080 Ti; then answers for that card"),
    ("w-cpu", "Why does my Intel i9 keep hitting 100 °C?", "free,complete", false, "corrects: Ryzen 7 5700X3D, peaks ~83 °C, never 100"),
    ("w-ram", "I have 64 GB of RAM, so why does memory keep filling up?", "free,complete", false, "corrects: 16 GB"),
    ("w-drive", "Why is my I drive full?", "free,complete", false, "says there's no I drive (C, D, E, F), no numbers for I, no scan of I"),
    ("w-valorant", "Why did Valorant crash yesterday?", "complete", false, "no Valorant on record; doesn't invent a crash"),
    ("w-bsod", "My PC blue-screened 5 times today, what's wrong?", "complete", false, "checks today: no 5 blue screens today; says what really happened"),
    ("w-temp", "Why did my CPU hit 105 °C during Dota 2?", "complete", false, "no 105 °C on record; real peak"),
    ("w-driver", "I installed NVIDIA driver 640.10 yesterday and now games stutter. Should I roll back?", "free,complete", false, "corrects: 621.01 installed 22 Oct, no 640.10"),
    ("w-fans", "Why are my fans stuck at 5000 rpm all the time?", "complete", false, "corrects: never above ~3,700 rpm"),
    ("w-win10", "Ever since I upgraded to Windows 10 my PC feels slow. Why?", "free,complete", false, "corrects: Windows 11 25H2"),
    ("w-psu", "I replaced my power supply last week, so the sudden shut-offs are fixed now, right?", "complete", false, "no PSU change on record; shut-off today at 10:02 AM, so not proven fixed"),
    ("w-crimson", "Why did Crimson Desert crash 20 times this week?", "complete", false, "not played this week (last 14 Oct); no 20 crashes"),
    ("w-cyberpunk", "How hot did my GPU get during my 10 hour Cyberpunk session yesterday?", "complete", false, "no Cyberpunk on record"),
    ("w-2gpu", "Why isn't my second graphics card being detected?", "free,complete", false, "only one graphics card on record"),
};
// Tools off the fixed list, matched by their Start menu name (2026-10-25). On this PC: NVIDIA Broadcast and GeForce NOW
// are installed but not on the list; CPU-Z is on the list; MSI Center isn't installed at all.
var apps = new (string Id, string Text, string Plans, bool Scanned, string Expect)[]
{
    ("a-broadcast", "How do I turn on noise removal for my microphone?", "complete", false, "NVIDIA Broadcast is installed (not on the list): [do: app NVIDIA Broadcast] -> Open NVIDIA Broadcast"),
    ("a-gfn", "Can I play my games in the cloud on this PC?", "complete", false, "GeForce NOW installed: Open NVIDIA GeForce NOW"),
    ("a-cpuz", "Where can I see my RAM timings?", "complete", false, "CPU-Z on the list and installed: Open CPU-Z"),
    ("a-msi", "Open MSI Center so I can change my RGB", "complete", false, "MSI Center not installed: no button, no claim it's installed"),
    ("a-fan", "How do I make my fans quieter?", "complete", false, "a fan tool: by name only if installed; else BIOS words, no made-up button"),
};
if (Environment.GetEnvironmentVariable("EVAL_SET") == "wrong") questions = wrong;
if (Environment.GetEnvironmentVariable("EVAL_SET") == "apps") questions = apps;
// EVAL_PROMPT=old asks as a released app does (the relay then sends its old prompt word for word); EVAL_REPEAT=n asks each n times,
// since one answer only hints: answers vary from run to run.
Relay.Features.UseActions2 = Environment.GetEnvironmentVariable("EVAL_PROMPT") != "old";
int repeat = int.TryParse(Environment.GetEnvironmentVariable("EVAL_REPEAT"), out int r) && r > 1 ? r : 1;
questions = [.. questions.SelectMany(q => Enumerable.Range(1, repeat).Select(i => repeat == 1 ? q : q with { Id = $"{q.Id}#{i}" }))];

var sb = new StringBuilder();
sb.AppendLine($"# RigSight AI answers, {plan} plan, {DateTime.Now:yyyy-MM-dd HH:mm}");
sb.AppendLine($"Relay {relayUrl}, app version {Relay.Version}, prompt {(Relay.Features.UseActions2 ? "actions-2 (new)" : "old")}");
sb.AppendLine();
var settings = SettingsStore.Load();
int n = 0;
foreach (var q in questions)
{
    if (!q.Plans.Split(',').Contains(plan)) continue;
    if (only is not null && !only.Contains(q.Id.Split('#')[0])) continue;
    var tools = new AiTools(settings)
    {
        PlanPages = plan == "free" ? AiPages.DefaultFor(0) : null,
        DriveScan = letter => q.Scanned && letter == "E" ? ScanOfE : null,
        DrivesNow = () => DrivesNow,
    };
    var started = DateTime.Now;
    AiReply reply;
    // The relay's own limit on questions a minute: a pause between them, and once more after a minute when it says so.
    if (n > 0) await Task.Delay(TimeSpan.FromSeconds(12));
    for (int attempt = 0; ; attempt++)
    {
        try { reply = await Relay.AskAsync([], q.Text, tools); }
        catch (Exception ex) { reply = new AiReply(null, ex.GetType().Name + ": " + ex.Message, []); }
        if (attempt >= 2 || reply.Problem?.Contains("a lot of questions") != true) break;
        Console.WriteLine($"{q.Id}: the relay asked to wait; again in a minute");
        await Task.Delay(TimeSpan.FromSeconds(65));
        started = DateTime.Now;
    }
    var took = DateTime.Now - started;
    var text = reply.Text ?? "";
    var markers = text.Split('\n').Select(l => AiActions.Marker(l)).OfType<string>().ToList();
    // As the app checks them: the plan's pages (and Settings › AI), the PC tools really on this PC.
    var planPages = plan == "free" ? AiPages.DefaultFor(0) : null;
    var buttons = AiActions.Resolve(markers, new AiActionContext(reply.LookUps, tools.LastRunning, tools.Installed, true,
        page => (planPages is null || planPages.Contains(page)) && settings.Ai.Sends(page), () => OpenableApps.Installed(StartApps.List()), StartApps.List));
    var locked = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("[locked:", StringComparison.OrdinalIgnoreCase));
    sb.AppendLine($"## {q.Id}: {q.Text}");
    sb.AppendLine($"*Expect:* {q.Expect}");
    sb.AppendLine();
    sb.AppendLine($"- Took {took.TotalSeconds:0} s · look-ups: {(reply.LookUps.Count == 0 ? "none" : string.Join(", ", reply.LookUps.Select(l => l.Tool + (l.Args.Count > 0 ? " " + l.Args.ToJsonString() : ""))))}");
    sb.AppendLine($"- Markers: {(markers.Count == 0 ? "none" : string.Join(" · ", markers.Select(m => $"[do: {m}]")))}");
    sb.AppendLine($"- Buttons shown: {(buttons.Count == 0 ? "none" : string.Join(" · ", buttons.Select(b => $"{b.Kind} \"{b.Label}\" → {b.Target}")))}");
    sb.AppendLine($"- Locked line: {locked ?? "none"}");
    if (reply.Problem is { } problem) sb.AppendLine($"- PROBLEM: {problem}");
    sb.AppendLine();
    sb.AppendLine("```text");
    sb.AppendLine(text.Trim());
    sb.AppendLine("```");
    sb.AppendLine();
    Console.WriteLine($"{q.Id}: {took.TotalSeconds:0} s, {text.Length} chars, {buttons.Count} buttons{(reply.Problem is null ? "" : ", problem: " + reply.Problem)}");
    File.WriteAllText(outFile, sb.ToString());
    n++;
}
Console.WriteLine($"{n} questions written to {outFile}");
return 0;

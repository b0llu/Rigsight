using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rigsight.Core.Ai;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Ai;

/// <summary>
/// RigSight AI asked about the person's own PC, from a copy of their history and their games' real settings files (the
/// person's ask, 2026-10-25: "run AI tests on my actual data, read it and make meaningful tests so you know what the
/// answers should be"). Two steps, both off unless asked for:
/// EVAL_DUMP=1 writes what each look-up shows for that history (real-lookups.md beside the questions), to read and write
/// the questions from; then the questions file (EVAL_QUESTIONS, JSON kept out of the repo since it holds the person's
/// own facts) is asked of a local relay (RIGSIGHT_LIVE_RELAY, started with ADMIN_DEV=1), each answer checked by its rules
/// and written out in full to real-eval.md beside it. RIGSIGHT_REAL_DB is the copy of the history (never the live file).
/// </summary>
public sealed class AiRealDataEvalTests
{
    private static readonly string? RelayUrl = Environment.GetEnvironmentVariable("RIGSIGHT_LIVE_RELAY");
    private static readonly string? Db = Environment.GetEnvironmentVariable("RIGSIGHT_REAL_DB");
    private static readonly string? Questions = Environment.GetEnvironmentVariable("EVAL_QUESTIONS");

    /// <summary>One question with what a good answer has to do, as far as a rule can say it.</summary>
    private sealed record Question(string Id, string Q, string[]? LookUps = null, string[]? Names = null, string[]? NotNames = null,
        bool Search = false, string? Note = null);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    private static AiTools Tools(string db) =>
        new(new RigsightSettings(), db) { SettingsPlaces = GameSettings.Places.ThisPc(), RivaTuner = () => Environment.GetEnvironmentVariable("EVAL_RIVATUNER") ?? "running" };

    private static string Folder => Path.GetDirectoryName(Path.GetFullPath(Questions!))!;

    [Fact]
    public void What_the_look_ups_show_for_the_real_history()
    {
        if (Environment.GetEnvironmentVariable("EVAL_DUMP") != "1" || Db is null || Questions is null) return;
        Assert.DoesNotContain(@"\Rigsight\rigsight.db", Db, StringComparison.OrdinalIgnoreCase);
        var tools = Tools(Db);
        var sb = new StringBuilder();
        void Show(string tool, JsonObject args)
        {
            sb.AppendLine($"## {tool} {args.ToJsonString()}\n\n```text\n{tools.Run(tool, args.ToJsonString()).Trim()}\n```\n");
        }
        foreach (var tool in new[] { "get_pc_info", "get_today", "get_temperatures", "get_crashes", "get_apps", "get_memory", "get_storage", "get_changes" })
            Show(tool, []);
        foreach (var game in (Environment.GetEnvironmentVariable("EVAL_GAMES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Show("get_game", new JsonObject { ["name"] = game });
        File.WriteAllText(Path.Combine(Folder, "real-lookups.md"), sb.ToString());
    }

    private static async Task Admin(string path, object body)
    {
        using var http = new HttpClient { BaseAddress = new Uri(RelayUrl!.TrimEnd('/') + "/") };
        var res = await http.PostAsJsonAsync($"admin/api/{path}", body);
        Assert.True(res.IsSuccessStatusCode, $"admin {path}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Questions_about_the_real_PC_are_answered_from_its_records()
    {
        if (RelayUrl is null || RelayUrl.Contains("rigsight.app") || Db is null || Questions is null || Environment.GetEnvironmentVariable("EVAL_DUMP") == "1") return;
        Assert.DoesNotContain(@"\Rigsight\rigsight.db", Db, StringComparison.OrdinalIgnoreCase);
        Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", RelayUrl);
        var useActions2 = Relay.Features.UseActions2;
        Relay.Features.UseActions2 = true;
        try
        {
            var questions = JsonSerializer.Deserialize<Question[]>(File.ReadAllText(Questions), Json)!;
            int repeat = int.TryParse(Environment.GetEnvironmentVariable("EVAL_REPEAT"), out int r) && r > 1 ? r : 1;
            var only = Environment.GetEnvironmentVariable("EVAL_ONLY")?.Split(',').ToHashSet();
            var (status, problem) = await Relay.StatusAsync();
            Assert.True(status is not null, problem);
            await Admin($"devices/{Relay.DeviceId}", new { action = "give_plan", plan = "complete", reason = "real data eval" });
            await Admin($"devices/{Relay.DeviceId}", new { action = "cap_override", n = 500 });

            string outPath = Path.Combine(Folder, "real-eval.md");
            var sb = new StringBuilder();
            var score = new List<(string Id, bool Pass, string Why)>();
            int asked = 0;
            foreach (var q in questions)
            {
                if (only is not null && !only.Contains(q.Id)) continue;
                for (int i = 1; i <= repeat; i++)
                {
                    if (asked++ > 0) await Task.Delay(TimeSpan.FromSeconds(8));
                    var tools = Tools(Db);
                    var started = DateTime.Now;
                    AiReply reply;
                    for (int attempt = 0; ; attempt++)
                    {
                        try { reply = await Relay.AskAsync([], q.Q, tools); }
                        catch (Exception ex) { reply = new AiReply(null, ex.GetType().Name + ": " + ex.Message, []); }
                        if (attempt >= 2 || reply.Problem?.Contains("a lot of questions") != true) break;
                        await Task.Delay(TimeSpan.FromSeconds(65));
                    }
                    string answer = reply.Text ?? "";
                    // What it was shown, again: the same look-ups give the same text (the copy doesn't change).
                    string shown = string.Join("\n", reply.LookUps.Where(l => l.Tool != "search_web").Select(l => Tools(Db).Run(l.Tool, l.Args.ToJsonString()))) + "\n" + q.Q;
                    var fails = new List<string>();
                    if (reply.Problem is { } p) fails.Add("problem: " + p);
                    foreach (var tool in q.LookUps ?? []) if (!reply.LookUps.Any(l => l.Tool == tool)) fails.Add("no " + tool);
                    if (q.Search && !reply.LookUps.Any(l => l.Tool == "search_web")) fails.Add("no web search");
                    foreach (var n in q.Names ?? []) if (!Regex.IsMatch(answer, n, RegexOptions.IgnoreCase)) fails.Add($"doesn't say {n}");
                    foreach (var n in q.NotNames ?? []) if (Regex.Match(answer, n, RegexOptions.IgnoreCase) is { Success: true } m) fails.Add($"says \"{m.Value}\"");
                    var madeUp = MadeUpNumbers(answer, shown);
                    if (madeUp.Count > 0) fails.Add("numbers not in the records: " + string.Join(", ", madeUp));
                    string id = q.Id + (repeat > 1 ? $"#{i}" : "");
                    score.Add((id, fails.Count == 0, string.Join("; ", fails)));
                    sb.AppendLine($"### {id}: {q.Q}\n*Good answer:* {q.Note}\n");
                    sb.AppendLine($"- {(fails.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", fails))}");
                    sb.AppendLine($"- Took {(DateTime.Now - started).TotalSeconds:0} s · look-ups: {string.Join(", ", reply.LookUps.Select(l => l.Tool + (l.Args.Count > 0 ? " " + l.Args.ToJsonString() : "")))}\n");
                    sb.AppendLine("```text\n" + answer.Trim() + "\n```\n");
                    File.WriteAllText(outPath, sb.ToString());
                }
            }
            sb.Insert(0, $"# RigSight AI on this PC's own history, {DateTime.Now:yyyy-MM-dd HH:mm}\n\n**{score.Count(x => x.Pass)} of {score.Count} passed the rule checks.** "
                + string.Join(" · ", score.Where(x => !x.Pass).Select(x => $"{x.Id}: {x.Why}")) + "\n\n");
            File.WriteAllText(outPath, sb.ToString());
        }
        finally
        {
            Relay.Features.UseActions2 = useActions2;
            Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", null);
        }
    }

    /// <summary>
    /// Every temperature, frame rate, percent and hour count in the answer, against the numbers it was shown (within 1).
    /// A line credited to a web source quotes a guide, not this PC.
    /// </summary>
    private static List<string> MadeUpNumbers(string answer, string shown)
    {
        var known = Regex.Matches(shown, @"\d+(\.\d+)?").Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        answer = string.Join("\n", answer.Split('\n').Where(l => !Regex.IsMatch(l, @"\[src:[^\]]*\bw\d")));
        return [.. Regex.Matches(answer, @"(\d+(?:\.\d+)?)\s*(?:fps|FPS|°C|°|%|hours?\b|hrs?\b)")
            .Select(m => m.Value)
            .Where(v => double.Parse(Regex.Match(v, @"\d+(\.\d+)?").Value, System.Globalization.CultureInfo.InvariantCulture) is var n && !known.Any(k => Math.Abs(k - n) <= 1))
            .Distinct()];
    }
}

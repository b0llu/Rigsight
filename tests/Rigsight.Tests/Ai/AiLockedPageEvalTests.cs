using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Rigsight.Core.Ai;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Ai;

/// <summary>
/// Every AI button's question asked on the Free plan, from a page Free doesn't include (the person's ask, 2026-10-26: with
/// AI buttons on every page, a person on Free presses one on a paid page). Each must still be answered from what Free
/// can read, say once in plain words which page the plan doesn't include, and end with that page's card; a question
/// Free does read must say nothing about plans. Runs only against a local relay (RIGSIGHT_LIVE_RELAY, ADMIN_DEV=1), on a
/// copy of the person's history (RIGSIGHT_REAL_DB); writes locked-eval.md beside it.
/// </summary>
public sealed class AiLockedPageEvalTests
{
    private static readonly string? RelayUrl = Environment.GetEnvironmentVariable("RIGSIGHT_LIVE_RELAY");
    private static readonly string? Db = Environment.GetEnvironmentVariable("RIGSIGHT_REAL_DB");

    // The questions as each button builds them, filled from this PC's history; null page: one Free reads.
    private static readonly (string Button, string Q, string? Locked)[] Questions =
    [
        ("Games · Ask how to make it run better", "How can I make Dota 2 run better?", AiPages.Games),
        ("Games · Why did it end?", "Why did my Risk of Rain 2 play on Sat 24 Oct at 9:40 PM end (game crashed)?", AiPages.Games),
        ("Crashes · Explain", "Explain this from my PC's records: The graphics driver was reset, Thu 22 Oct 5:25 PM", AiPages.Crashes),
        ("Timeline · Is it the driver?", "2 graphics driver resets since driver 621.01, none in the 14 days before. Did driver 621.01 cause this?", AiPages.Timeline),
        ("Memory · Ask what to do", "Discord is at 1.9 GB. It usually holds 600 MB. What should I do?", AiPages.Memory),
        ("Storage · Is it safe?", "Is it safe to delete Windows Update cleanup (3.2 GB)? Old update files Windows keeps after installing updates.", AiPages.Storage),
        ("Network · Why does it drop?", "6 drops today. Why does my connection drop, and is it the PC or the line?", AiPages.Network),
        ("Fans · Ask about this fan", "Fan #2. It spins 30% slower than last month at the same heat. Why, and what should I do?", AiPages.Fans),
        ("Processes · Ask what this is", "What is svchost.exe, and is it normal for it to use 1.2 GB of memory?", null), // Free since 0011
        ("Apps · Ask what this is", "What is Discord, and how do I use it on my PC?", AiPages.Apps),
        ("Reports · Ask about this period", "How did my PC do last week, and is anything worth a look?", AiPages.Reports),
        ("Temperatures · Why is it warmer?", "My GPU sits at 52° at rest today, usually 44–47°. Why is it warmer?", null),
        ("Home · Explain", "Your PC shut off suddenly 3 times this week. What does this mean, and what should I do?", null),
    ];

    /// <summary>A question from a page Free reads that is still mainly about one it doesn't: shut-offs are Crashes'.</summary>
    private static readonly Dictionary<string, string> CardAnyway = new() { ["Home · Explain"] = AiPages.Crashes };

    private static async Task Admin(string path, object body)
    {
        using var http = new HttpClient { BaseAddress = new Uri(RelayUrl!.TrimEnd('/') + "/") };
        var res = await http.PostAsJsonAsync($"admin/api/{path}", body);
        string text = await res.Content.ReadAsStringAsync();
        // Ending a plan the PC doesn't have is fine: it's on Free either way.
        Assert.True(res.IsSuccessStatusCode || path.Contains("devices") && text.Contains("plan"), $"admin {path}: {(int)res.StatusCode} {text}");
    }

    [Fact]
    public async Task On_Free_every_AI_buttons_question_from_a_paid_page_is_answered_and_names_the_page_once()
    {
        if (RelayUrl is null || RelayUrl.Contains("rigsight.app") || Db is null) return;
        Assert.DoesNotContain(@"\Rigsight\rigsight.db", Db, StringComparison.OrdinalIgnoreCase);
        Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", RelayUrl);
        var useActions2 = Relay.Features.UseActions2;
        Relay.Features.UseActions2 = true;
        try
        {
            var (status, problem) = await Relay.StatusAsync();
            Assert.True(status is not null, problem);
            await Admin($"devices/{Relay.DeviceId}", new { action = "end_plan", reason = "locked page eval" });
            // Free's weekly questions, enough for every question here.
            await Admin($"devices/{Relay.DeviceId}", new { action = "set_free", n = 500 });
            var free = AiPages.DefaultFor(0);

            var sb = new StringBuilder();
            var score = new List<(string Id, bool Pass, string Why)>();
            foreach (var (button, q, locked) in Questions)
            {
                if (score.Count > 0) await Task.Delay(TimeSpan.FromSeconds(8));
                var tools = new AiTools(new RigsightSettings(), Db) { SettingsPlaces = GameSettings.Places.ThisPc(), RivaTuner = () => "running", PlanPages = free };
                AiReply reply;
                // As the app sends a button's question from a page the plan doesn't include (AskViewModel.LockedAside).
                string sent = locked is null ? q : q + $"\n\n(Asked from RigSight's {AiPages.Get(locked)!.Label} page, which my plan doesn't include.)";
                try { reply = await Relay.AskAsync([], sent, tools); }
                catch (Exception ex) { reply = new AiReply(null, ex.GetType().Name + ": " + ex.Message, []); }
                string answer = reply.Text ?? "";
                var lockedLine = Regex.Match(answer, @"\[locked:\s*([a-z]+)");
                var fails = new List<string>();
                if (reply.Problem is { } p) fails.Add("problem: " + p);
                if (answer.Length < 80) fails.Add("no real answer");
                if (Regex.IsMatch(answer, @"couldn.t (look|open)|from here|with the tools I have|look-ups here|Asked from RigSight", RegexOptions.IgnoreCase)) fails.Add("sounds broken, or quotes the note");
                if (Regex.IsMatch(answer, @"\$\d|per month|Plus plan|Complete plan|Pro plan", RegexOptions.IgnoreCase)) fails.Add("sells (names a plan or price)");
                if (locked is not null)
                {
                    string label = AiPages.Get(locked)!.Label;
                    if (!lockedLine.Success) fails.Add($"no [locked: {locked}] card");
                    else if (lockedLine.Groups[1].Value != locked) fails.Add($"card for {lockedLine.Groups[1].Value}, not {locked}");
                    if (!Regex.IsMatch(answer, Regex.Escape(label), RegexOptions.IgnoreCase)) fails.Add($"never names {label}");
                }
                else if (CardAnyway.TryGetValue(button, out var anyway))
                {
                    if (lockedLine.Groups[1].Value != anyway) fails.Add($"no [locked: {anyway}] card for a question about it");
                }
                else if (lockedLine.Success || Regex.IsMatch(answer, @"\bplan\b", RegexOptions.IgnoreCase)) fails.Add("talks about plans on a page Free reads");
                score.Add((button, fails.Count == 0, string.Join("; ", fails)));
                sb.AppendLine($"### {button} ({(locked is null ? "Free reads it" : "not in Free: " + locked)})\n**Asked:** {q}\n");
                sb.AppendLine($"- {(fails.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", fails))}");
                sb.AppendLine($"- look-ups: {string.Join(", ", reply.LookUps.Select(l => l.Tool))}\n");
                sb.AppendLine("```text\n" + answer.Trim() + "\n```\n");
                File.WriteAllText(Out(), sb.ToString());
            }
            sb.Insert(0, $"# AI buttons on Free, {DateTime.Now:yyyy-MM-dd HH:mm}\n\n**{score.Count(x => x.Pass)} of {score.Count} passed.** "
                + string.Join(" · ", score.Where(x => !x.Pass).Select(x => $"{x.Id}: {x.Why}")) + "\n\n");
            File.WriteAllText(Out(), sb.ToString());
        }
        finally
        {
            Relay.Features.UseActions2 = useActions2;
            Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", null);
        }
    }

    private static string Out() => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Db!))!, "locked-eval.md");
}

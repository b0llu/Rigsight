using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rigsight.Core.Ai;

/// <summary>A model Ollama has: its name, its size on disk, and whether it can call tools (look at RigSight's records).</summary>
public sealed record OllamaModel(string Name, long Bytes, bool CanLookUp);

/// <summary>One record the helper looked at for an answer: the tool, what it asked for, and how much came back.</summary>
public sealed record AiLookUp(string Tool, JsonObject Args, int Chars);

/// <summary>A message earlier in the conversation, sent again so a follow-up question has its context.</summary>
public sealed record AiTurn(bool FromUser, string Text);

public static class AiTurns
{
    /// <summary>Messages sent again at most: the first question and its answer (what the talk is about), then the latest.</summary>
    public const int MaxSent = 12;

    /// <summary>
    /// What of a long conversation goes with the next question, so it doesn't grow without end: the first exchange and
    /// the most recent ones, each exchange whole.
    /// </summary>
    public static IReadOnlyList<AiTurn> Recent(IReadOnlyList<AiTurn> all)
    {
        if (all.Count <= MaxSent) return all;
        int start = all.Count - (MaxSent - 2);
        // Begin the recent ones on a question, never on an answer cut from its question.
        while (start < all.Count && !all[start].FromUser) start++;
        return [.. all.Take(2), .. all.Skip(start)];
    }
}

/// <summary>
/// What the helper is doing now, for the line under a question: a real stage (getting the model ready, reading a record,
/// putting the findings together), never a scripted one. <paramref name="Note"/> is a second, fainter line.
/// </summary>
public sealed record AiStep(string Text, string? Note = null, bool Loading = false);

/// <summary>What came back for a question: the answer's text, or why there is none, and what it looked at.</summary>
public sealed record AiReply(string? Text, string? Problem, IReadOnlyList<AiLookUp> LookUps, string? Model = null);

/// <summary>
/// Ollama on this PC (Settings › Ask RigSight → Ollama on my PC): lists its models and answers questions, looking at RigSight's
/// records through <see cref="AiTools"/> when the model can call tools. Everything stays on the PC. Uses Ollama's own
/// API (not its OpenAI-style one) so the context can be set big enough for a report.
/// </summary>
public static class Ollama
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Room for the question, a few look-ups and the answer; a report alone is several thousand words.</summary>
    public const int ContextTokens = 32768;

    /// <summary>A look-up loop stops here; a model still asking for records after this many rounds won't answer.</summary>
    public const int MaxRounds = 8;

    /// <summary>"localhost:11434/" → "http://localhost:11434": a scheme added, the trailing slash dropped.</summary>
    public static string Normalise(string address)
    {
        var a = address.Trim().TrimEnd('/');
        return a.Length == 0 || a.Contains("://") ? a : "http://" + a;
    }

    /// <summary>The models Ollama at <paramref name="address"/> has; null when it doesn't answer.</summary>
    public static async Task<IReadOnlyList<OllamaModel>?> ModelsAsync(string address, CancellationToken ct = default)
    {
        var root = Normalise(address);
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var tags = JsonNode.Parse(await Http.GetStringAsync(root + "/api/tags", quick.Token));
            var list = new List<OllamaModel>();
            foreach (var m in tags?["models"] as JsonArray ?? [])
            {
                string name = (string?)m?["name"] ?? "";
                if (name.Length == 0) continue;
                // /api/show says what the model can do; "tools" means it can ask RigSight for records.
                bool tools = false;
                try
                {
                    using var res = await Http.PostAsync(root + "/api/show", Json(new JsonObject { ["model"] = name }), quick.Token);
                    var show = JsonNode.Parse(await res.Content.ReadAsStringAsync(quick.Token));
                    tools = (show?["capabilities"] as JsonArray)?.Any(c => (string?)c == "tools") == true;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException) { Log.Write("ollama", $"Couldn't read what {name} can do: {ex.Message}"); }
                list.Add(new OllamaModel(name, (long?)m?["size"] ?? 0, tools));
            }
            Log.Write("ollama", $"{root} has {list.Count} models: {string.Join(", ", list.Select(m => m.Name + (m.CanLookUp ? "" : " (no tools)")))}");
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Write("ollama", $"No answer from {root}: expected its model list, got {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Asks <paramref name="model"/> the question with the conversation so far, answering the look-ups it asks for
    /// until it writes its answer. A problem (Ollama not running, the model gone, too many look-ups) comes back in
    /// <see cref="AiReply.Problem"/> in words for the person.
    /// </summary>
    public static async Task<AiReply> AskAsync(string address, string model, bool canLookUp, IReadOnlyList<AiTurn> earlier,
        string question, AiTools tools, Action<AiStep>? step = null, CancellationToken ct = default)
    {
        var root = Normalise(address);
        var started = DateTime.Now;
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = AiPrompt.System(canLookUp, withSearch: false, started) } };
        foreach (var turn in AiTurns.Recent(earlier)) messages.Add(new JsonObject { ["role"] = turn.FromUser ? "user" : "assistant", ["content"] = turn.Text });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
        var lookUps = new List<AiLookUp>();
        var definitions = canLookUp ? tools.Definitions() : null;
        Log.Write("ollama", $"Asking {model} at {root} ({(canLookUp ? $"{definitions!.Count} look-ups allowed" : "no look-ups")}, {AiTurns.Recent(earlier).Count} of {earlier.Count} earlier messages)");
        // A model not in memory yet takes a while to load onto the graphics card: say so, so the wait makes sense.
        step?.Invoke(await IsLoadedAsync(root, model, ct)
            ? new AiStep(Lang.T("Connecting the dots…"))
            : new AiStep(Lang.T($"Getting {model} ready…"), Lang.T("The first answer can take a little longer while it loads."), Loading: true));
        for (int round = 0; round < MaxRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonNode.Parse(messages.ToJsonString()),
                ["stream"] = false,
                ["options"] = new JsonObject { ["num_ctx"] = ContextTokens },
            };
            if (definitions is { Count: > 0 }) body["tools"] = JsonNode.Parse(definitions.ToJsonString());
            string text;
            int status;
            try
            {
                using var res = await Http.PostAsync(root + "/api/chat", Json(body), ct);
                status = (int)res.StatusCode;
                text = await res.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException)
            {
                Log.Write("ollama", $"No answer from {root} for {model}: {ex.Message}");
                return new AiReply(null, Lang.T($"Ollama isn't answering at {root}. Check that it's running, then ask again."), lookUps);
            }
            JsonNode? json = null;
            try { json = JsonNode.Parse(text); } catch (JsonException) { }
            if (status is < 200 or >= 300 || json?["error"] is not null)
            {
                string error = (string?)json?["error"] ?? text[..Math.Min(300, text.Length)];
                Log.Write("ollama", $"{model} round {round}: expected an answer, got HTTP {status}: {error}");
                return new AiReply(null, status == 404
                    ? Lang.T($"Ollama doesn't have {model} any more. Pick another model in Settings › Ask RigSight.")
                    : Lang.T($"Ollama couldn't answer: {error}"), lookUps);
            }
            var msg = json?["message"];
            if (msg?["tool_calls"] is JsonArray { Count: > 0 } calls)
            {
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = (string?)msg["content"] ?? "", ["tool_calls"] = JsonNode.Parse(calls.ToJsonString()) });
                foreach (var call in calls)
                {
                    string name = (string?)call?["function"]?["name"] ?? "";
                    var args = call?["function"]?["arguments"] as JsonObject ?? [];
                    step?.Invoke(new AiStep(Reviewing(name, args)));
                    string result = await Task.Run(() => tools.Run(name, args.ToJsonString()), ct);
                    lookUps.Add(new AiLookUp(name, (JsonObject)args.DeepClone(), result.Length));
                    Log.Write("ollama", $"{model} looked at {name} {args.ToJsonString()}: {result.Length} chars");
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = result });
                }
                step?.Invoke(new AiStep(Lang.T("Putting the findings together…")));
                continue;
            }
            string answer = ((string?)msg?["content"] ?? "").Trim();
            Log.Write("ollama", $"{model} answered in {(DateTime.Now - started).TotalSeconds:0} s after {lookUps.Count} look-ups, {(long?)json?["prompt_eval_count"]} tokens in, {(long?)json?["eval_count"]} out, {answer.Length} chars");
            return answer.Length == 0
                ? new AiReply(null, Lang.T($"{model} gave an empty answer. Ask again, or pick another model in Settings › Ask RigSight."), lookUps)
                : new AiReply(answer, null, lookUps, model);
        }
        Log.Write("ollama", $"{model} was still asking for records after {MaxRounds} rounds");
        return new AiReply(null, Lang.T($"{model} kept asking for records without answering. Try a bigger model in Settings › Ask RigSight."), lookUps);
    }

    /// <summary>Whether Ollama has the model in memory already (/api/ps); unknown counts as not.</summary>
    private static async Task<bool> IsLoadedAsync(string root, string model, CancellationToken ct)
    {
        try
        {
            var ps = JsonNode.Parse(await Http.GetStringAsync(root + "/api/ps", ct));
            return (ps?["models"] as JsonArray)?.Any(m => (string?)m?["name"] == model) == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>What the Ask window says while a look-up runs.</summary>
    private static string Reviewing(string tool, JsonObject args) => tool switch
    {
        "get_report" => Lang.T("Reviewing your report…"),
        "get_crashes" => Lang.T("Reviewing your crashes…"),
        "get_changes" => Lang.T("Reviewing what changed…"),
        "get_pc_info" => Lang.T("Reviewing your PC's parts…"),
        "list_games" => Lang.T("Reviewing your games…"),
        "get_moment" => Lang.T("Reviewing that moment…"),
        "get_temperatures" => Lang.T("Reviewing your temperatures…"),
        "get_fans" => Lang.T("Reviewing your fans…"),
        "get_apps" => Lang.T("Reviewing your apps…"),
        "get_memory" => Lang.T("Reviewing your memory…"),
        "get_storage" => Lang.T("Reviewing your drives…"),
        "get_network" => Lang.T("Reviewing your internet use…"),
        "get_on_times" => Lang.T("Reviewing when your PC was on…"),
        "get_game" when (string?)args["name"] is { Length: > 0 } game => Lang.T($"Reviewing {game}…"),
        _ => Lang.T("Reviewing your PC history…"),
    };

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rigsight.Core.Ai;

/// <summary>A model Ollama has: its name, its size on disk, and whether it can call tools (look at RigSight's records).</summary>
public sealed record OllamaModel(string Name, long Bytes, bool CanLookUp);

/// <summary>One record the helper looked at for an answer: the tool, what it asked for, and how much came back.</summary>
/// <param name="Web">A web search's numbered results (the relay's search only), for the paragraphs' sources.</param>
public sealed record AiLookUp(string Tool, JsonObject Args, int Chars, IReadOnlyList<WebSource>? Web = null);

/// <summary>A message earlier in the conversation, sent again so a follow-up question has its context.</summary>
/// <summary>One message of a conversation as sent again; an answer keeps the records it was written from.</summary>
public sealed record AiTurn(bool FromUser, string Text, IReadOnlyList<AiLookUp>? LookUps = null);

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

    /// <summary>
    /// A message as the model reads it again. An answer ends with the records it was written from: without them, asked
    /// "why did you say that?", a model saw no look-ups and said it had made the numbers up (gpt-6-luna, 2026-10-21).
    /// </summary>
    public static JsonObject Message(AiTurn turn)
    {
        string text = turn.Text;
        if (!turn.FromUser && turn.LookUps is { Count: > 0 } looked)
            text += "\n\n(Records you looked at for this answer: " + string.Join("; ", looked.Select(l => l.Args.Count == 0 ? l.Tool
                : l.Tool + " " + string.Join(", ", l.Args.Select(a => $"{a.Key} {a.Value}")))) + ".)";
        return new JsonObject { ["role"] = turn.FromUser ? "user" : "assistant", ["content"] = text };
    }
}

/// <summary>
/// What the helper is doing now, for the line under a question: a real stage (getting the model ready, reading a record,
/// putting the findings together), never a scripted one. <paramref name="Note"/> is a second, fainter line.
/// </summary>
public sealed record AiStep(string Text, string? Note = null, bool Loading = false);

/// <summary>What came back for a question: the answer's text, or why there is none, what it looked at, and a quiet note for under the answer (web search failed).</summary>
public sealed record AiReply(string? Text, string? Problem, IReadOnlyList<AiLookUp> LookUps, string? Model = null, string? Note = null, AiLimit? Limit = null);

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

    /// <summary>
    /// True when the address is this PC (localhost, 127.x, ::1), so nothing sent to it leaves the PC. Any other address
    /// (another PC at home, a rented server) is somewhere else, and RigSight says so before the first question.
    /// </summary>
    public static bool IsOnThisPc(string address)
    {
        if (!Uri.TryCreate(Normalise(address), UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The address's computer for people to read: "192.168.1.20" or "gpu-box", without http:// and the port.</summary>
    public static string Host(string address) => Uri.TryCreate(Normalise(address), UriKind.Absolute, out var uri) ? uri.Host : address.Trim();

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
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = AiPrompt.Ollama(canLookUp, started, AiInstructions.Custom(Settings.AiMode.Ollama)) } };
        foreach (var turn in AiTurns.Recent(earlier)) messages.Add(AiTurns.Message(turn));
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
                // Ollama on another computer needs the network; on this PC it doesn't.
                if (!IsOnThisPc(root) && await Internet.ExplainsAsync(ex)) return new AiReply(null, Internet.NoInternet(), lookUps);
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

    /// <summary>
    /// What the Ask window says while a look-up runs, pulsing (no spinner, the person's word 2026-10-23): what it reads and
    /// for which days, "Reading your crashes for 16 Oct – 22 Oct…", so the wait says what's going on.
    /// </summary>
    public static string Reviewing(string tool, JsonObject args)
    {
        string what = tool switch
        {
            "search_web" when (string?)args["query"] is { Length: > 0 } q => Lang.T($"Searching the web for \"{(q.Length > 48 ? q[..47].TrimEnd() + "…" : q)}\""),
            "search_web" => Lang.T("Searching the web"),
            "get_report" => Lang.T("Reading your report"),
            "get_crashes" => Lang.T("Reading your crashes"),
            "get_changes" => Lang.T("Reading what changed"),
            "get_pc_info" => Lang.T("Reading your PC's parts"),
            "list_games" => Lang.T("Reading your games"),
            "get_moment" => Lang.T("Reading that moment"),
            "get_temperatures" => Lang.T("Reading your temperatures"),
            "get_fans" => Lang.T("Reading your fans"),
            "get_apps" => Lang.T("Reading your apps"),
            "get_memory" => Lang.T("Reading your memory"),
            "get_storage" => Lang.T("Reading your drives"),
            "get_drive_scan" when (string?)args["drive"] is { Length: > 0 } d => Lang.T($"Reading the scan of {d.Trim().TrimEnd(':').ToUpperInvariant()}:"),
            "get_network" => Lang.T("Reading your internet use"),
            "get_on_times" => Lang.T("Reading when your PC was on"),
            "get_today" => Lang.T("Reading today so far"),
            "get_sensors_now" => Lang.T("Reading every sensor now"),
            "get_running_now" => Lang.T("Reading what's running now"),
            "get_rewind" => Lang.T("Reading your rewind"),
            "get_dashboard" => Lang.T("Reading your dashboards"),
            "get_game" when (string?)args["name"] is { Length: > 0 } game => Lang.T($"Reading {game}"),
            _ => Lang.T("Reading your PC history"),
        };
        string range = AiSources.Range(new AiLookUp(tool, args, 0));
        return range.Length > 0 ? Lang.T($"{what} for {range}…") : what + "…";
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}

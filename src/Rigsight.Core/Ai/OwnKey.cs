using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ai;

/// <summary>A model a service lists for the person's key, with about what a question costs when the service publishes its prices.</summary>
public sealed record ServiceModel(string Id, double? CentsPerQuestion, bool Tested);

/// <summary>
/// The person's own API key, kept in ai-key.bin beside the settings and locked to their Windows account by Windows itself
/// (DPAPI), so it never sits in settings.json in plain text and another account on the PC can't read it.
/// </summary>
public static class KeyStore
{
    public static string File => Path.Combine(RigsightPaths.DataDir, "ai-key.bin");

    public static void Save(string key)
    {
        Directory.CreateDirectory(RigsightPaths.DataDir);
        System.IO.File.WriteAllBytes(File, Protect(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>The saved key; null when none is saved or it can't be read (another account, a copied file).</summary>
    public static string? Load()
    {
        try { return System.IO.File.Exists(File) ? Encoding.UTF8.GetString(Unprotect(System.IO.File.ReadAllBytes(File))) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Write("own-key", $"The saved key couldn't be read: {ex.Message}");
            return null;
        }
    }

    public sealed class CryptographicException(string message) : Exception(message);

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int NoPrompt = 0x1;

    internal static byte[] Protect(byte[] data) => Run(data, (ref Blob i, ref Blob o) => CryptProtectData(ref i, "RigSight AI key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoPrompt, ref o));
    internal static byte[] Unprotect(byte[] data) => Run(data, (ref Blob i, ref Blob o) => CryptUnprotectData(ref i, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoPrompt, ref o));

    private delegate bool Crypt(ref Blob input, ref Blob output);

    private static byte[] Run(byte[] data, Crypt crypt)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            var output = new Blob();
            if (!crypt(ref input, ref output)) throw new CryptographicException($"Windows refused it (error {Marshal.GetLastWin32Error()})");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}

/// <summary>
/// My own key (Settings › Ask RigSight): any service that speaks OpenAI's chat API, OpenRouter first. Lists the models the
/// key can use and answers questions with the same look-ups and instructions as Ollama. No web search: that comes with
/// RigSight AI (the user's ruling, 2026-10-19), except on a test copy with a search key (<see cref="WebSearch"/>).
/// </summary>
public static class OwnKey
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(4) };

    /// <summary>Room for the answer and the model's own thinking first: Haiku 5.5 came back empty at 3000 (the model test, 2026-10-21).</summary>
    public const int MaxAnswerTokens = 8000;

    /// <summary>A look-up loop stops here, as Ollama's does.</summary>
    public const int MaxRounds = 8;

    /// <summary>Models answered and judged in RigSight's own model test, shown first in the list.</summary>
    public static readonly IReadOnlySet<string> Tested = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "anthropic/claude-haiku-5.5", "anthropic/claude-sonnet-5.5", "google/gemini-3.8-flash", "openai/gpt-6-luna",
        "deepseek/deepseek-v4.1-flash", "qwen/qwen3.8-flash",
    };

    /// <summary>Where each service answers in OpenAI's format; Custom uses the address typed in.</summary>
    public static string Root(string service, string custom) => service switch
    {
        "OpenRouter" => "https://openrouter.ai/api/v1",
        "OpenAI" => "https://api.openai.com/v1",
        "Anthropic" => "https://api.anthropic.com/v1",
        "Google Gemini" => "https://generativelanguage.googleapis.com/v1beta/openai",
        "DeepSeek" => "https://api.deepseek.com/v1",
        "xAI" => "https://api.x.ai/v1",
        "Mistral" => "https://api.mistral.ai/v1",
        "Groq" => "https://api.groq.com/openai/v1",
        _ => custom.Trim().TrimEnd('/'),
    };

    private static bool IsOpenRouter(string root) => root.StartsWith("https://openrouter.ai/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks the key and lists the models it can use (on OpenRouter only those that can call tools, since a model that
    /// can't look at the records can't answer about this PC). The models, or why there are none, in words for the person.
    /// </summary>
    public static async Task<(IReadOnlyList<ServiceModel>? Models, string? Problem)> ModelsAsync(string service, string root, string key, CancellationToken ct = default)
    {
        if (root.Length == 0) return (null, Lang.T("Type the service's address first."));
        try
        {
            // OpenRouter lists its models to anyone, so the key is checked on its own first.
            if (IsOpenRouter(root))
            {
                using var check = Request(HttpMethod.Get, root + "/key", key);
                using var keyRes = await Http.SendAsync(check, ct);
                if (!keyRes.IsSuccessStatusCode) return (null, await ProblemAsync(service, keyRes, ct, inSettings: true));
            }
            using var req = Request(HttpMethod.Get, root + "/models", key);
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return (null, await ProblemAsync(service, res, ct, inSettings: true));
            var list = new List<ServiceModel>();
            foreach (var m in JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"] as JsonArray ?? [])
            {
                string id = (string?)m?["id"] ?? "";
                if (id.Length == 0) continue;
                if (m?["supported_parameters"] is JsonArray can && !can.Any(p => (string?)p == "tools")) continue;
                list.Add(new ServiceModel(id, Cents(m?["pricing"]), Tested.Contains(id)));
            }
            Log.Write("own-key", $"{service} key works: {list.Count} models it can use, {list.Count(m => m.Tested)} of them tested with RigSight");
            return list.Count == 0 ? (null, Lang.T($"{service} accepted the key but listed no models that can look at RigSight's records.")) : (list, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Write("own-key", $"No answer from {service} at {root}: expected its model list, got {ex.GetType().Name}: {ex.Message}");
            if (await Internet.ExplainsAsync(ex)) return (null, Internet.NoInternet(inSettings: true));
            return (null, Lang.T($"{service} isn't answering. Check your internet connection, then press Test again."));
        }
    }

    /// <summary>
    /// About what one question costs at the service's prices, from what questions took in the model test: about 25,000
    /// tokens read (the instructions, look-ups and their records, sent again each round) and 1,500 written.
    /// </summary>
    private static double? Cents(JsonNode? pricing)
    {
        if (!double.TryParse((string?)pricing?["prompt"], NumberStyles.Float, CultureInfo.InvariantCulture, out var inPrice)
            || !double.TryParse((string?)pricing?["completion"], NumberStyles.Float, CultureInfo.InvariantCulture, out var outPrice)) return null;
        return (25_000 * inPrice + 1_500 * outPrice) * 100;
    }

    /// <summary>
    /// Asks the model the question with the conversation so far, answering the look-ups it asks for until it writes its
    /// answer, as <see cref="Ollama.AskAsync"/> does. The answer streams in: <paramref name="writing"/> gets the text so far as
    /// it grows with the look-ups made (so RigSight's numbers can be drawn as their markers come), and null when the words
    /// turn out to be a lead-in to more look-ups. A problem comes back in
    /// <see cref="AiReply.Problem"/> in words for the person. Always own key's own instructions, with no web search.
    /// </summary>
    public static async Task<AiReply> AskAsync(string service, string root, string key, string model, IReadOnlyList<AiTurn> earlier,
        string question, AiTools tools, Action<AiStep>? step = null, Action<string?, IReadOnlyList<AiLookUp>>? writing = null, CancellationToken ct = default)
    {
        var started = DateTime.Now;
        // Offline: said at once, rather than after waiting for the service to time out.
        if (Internet.IsOff())
        {
            Log.Write("own-key", "Not asked: Windows has no network connection");
            return new AiReply(null, Internet.NoInternet(), []);
        }
        string system = AiPrompt.OwnKey(started, AiInstructions.Custom(AiMode.OwnKey), tools.Settings.Ai.OwnKeyPreferences);
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var turn in AiTurns.Recent(earlier)) messages.Add(AiTurns.Message(turn));
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
        var lookUps = new List<AiLookUp>();
        var definitions = tools.Definitions();
        long tokensIn = 0, tokensOut = 0;
        Log.Write("own-key", $"Asking {model} on {service} ({definitions.Count} look-ups allowed, {AiTurns.Recent(earlier).Count} of {earlier.Count} earlier messages)");
        step?.Invoke(new AiStep(""));
        for (int round = 0; round < MaxRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonNode.Parse(messages.ToJsonString()),
                ["tools"] = JsonNode.Parse(definitions.ToJsonString()),
                ["stream"] = true,
            };
            // OpenRouter: room for thinking first, and only providers that don't keep what is sent.
            if (IsOpenRouter(root))
            {
                body["max_tokens"] = MaxAnswerTokens;
                body["provider"] = new JsonObject { ["data_collection"] = "deny" };
                body["stream_options"] = new JsonObject { ["include_usage"] = true };
            }
            Streamed got;
            var asked = DateTime.Now;
            try
            {
                using var req = Request(HttpMethod.Post, root + "/chat/completions", key);
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode)
                {
                    var problem = await ProblemAsync(service, res, ct);
                    Log.Write("own-key", $"{model} round {round}: expected an answer, got HTTP {(int)res.StatusCode}");
                    return new AiReply(null, problem, lookUps);
                }
                got = await ReadStreamAsync(res, writing is null ? null : text => writing(text, lookUps), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Log.Write("own-key", $"No answer from {service} for {model}: {ex.GetType().Name}: {ex.Message}");
                if (await Internet.ExplainsAsync(ex)) return new AiReply(null, Internet.NoInternet(), lookUps);
                return new AiReply(null, Lang.T($"{service} isn't answering. Check your internet connection, then ask again."), lookUps);
            }
            tokensIn += got.TokensIn;
            tokensOut += got.TokensOut;
            if (got.Error is { } error)
            {
                Log.Write("own-key", $"{model} round {round}: expected an answer, got: {error}");
                return new AiReply(null, Lang.T($"{service} couldn't answer: {error}"), lookUps);
            }
            Log.Write("own-key", $"{model} round {round}: first words after {(got.FirstWords is { } f ? $"{(f - asked).TotalSeconds:0.0} s" : "none")}, done after {(DateTime.Now - asked).TotalSeconds:0.0} s");
            if (got.Calls.Count > 0)
            {
                // Words before look-ups are a lead-in ("Let me check…"), not the answer: they come off the screen.
                if (got.Text.Length > 0) writing?.Invoke(null, lookUps);
                var calls = new JsonArray([.. got.Calls.Select(c => (JsonNode)c)]);
                // The words themselves don't go back either: the person never saw them, and a model handed its own draft
                // "corrected one thing I said earlier" in the answer (Haiku, 2026-10-21).
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = JsonNode.Parse(calls.ToJsonString()) });
                foreach (var call in calls)
                {
                    string name = (string?)call?["function"]?["name"] ?? "";
                    string argsJson = (string?)call?["function"]?["arguments"] ?? "{}";
                    JsonObject args;
                    try { args = JsonNode.Parse(argsJson.Length == 0 ? "{}" : argsJson) as JsonObject ?? []; } catch (JsonException) { args = []; }
                    step?.Invoke(new AiStep(Ollama.Reviewing(name, args)));
                    string result = await Task.Run(() => tools.Run(name, args.ToJsonString()), ct);
                    lookUps.Add(new AiLookUp(name, args, result.Length));
                    Log.Write("own-key", $"{model} looked at {name} {args.ToJsonString()}: {result.Length} chars");
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = (string?)call?["id"] ?? "", ["content"] = result });
                }
                step?.Invoke(new AiStep(""));
                continue;
            }
            string answer = got.Text.Trim();
            string finish = got.Finish;
            Log.Write("own-key", $"{model} answered in {(DateTime.Now - started).TotalSeconds:0} s after {lookUps.Count} look-ups, {tokensIn} tokens in, {tokensOut} out, {answer.Length} chars, finished: {finish}");
            if (answer.Length == 0)
                return new AiReply(null, finish == "length"
                    ? Lang.T($"{model} ran out of room before it could answer. Ask again, or ask a narrower question.")
                    : Lang.T($"{model} gave an empty answer. Ask again, or pick another model in Settings › Ask RigSight."), lookUps);
            return new AiReply(answer, null, lookUps, model);
        }
        Log.Write("own-key", $"{model} was still asking for records after {MaxRounds} rounds");
        return new AiReply(null, Lang.T($"{model} kept asking for records without answering. Ask again, or pick another model in Settings › Ask RigSight."), lookUps);
    }

    /// <summary>How long a streamed answer may say nothing at all before it counts as stopped; a slow model thinking hard
    /// on someone's own key can be quiet a long while before its first words.</summary>
    internal static TimeSpan QuietLimit { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A text from a chunk, or null when it's missing or not text (one odd chunk never breaks an answer).</summary>
    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>A number from a chunk, whole or not, or null.</summary>
    internal static long? Num(JsonNode? node) =>
        node is not JsonValue v ? null
        : v.TryGetValue(out long l) ? l
        : v.TryGetValue(out double d) && double.IsFinite(d) && Math.Abs(d) < long.MaxValue ? (long)d
        : null;

    /// <summary>One round's reply put back together from the stream: its words, its look-ups, why it stopped.</summary>
    /// <remarks><paramref name="CutOff"/>: the app stopped it (silence, a dropped connection), not the service: whether
    /// the service counted it isn't known.</remarks>
    internal sealed record Streamed(string Text, List<JsonObject> Calls, string Finish, long TokensIn, long TokensOut, string? Error, DateTime? FirstWords, bool CutOff = false);

    /// <summary>
    /// Reads a streamed reply (server-sent events, "data: {...}" lines ending with "data: [DONE]"), handing on the words so
    /// far as they come and putting each look-up back together from its pieces (they arrive by index, the arguments in parts).
    /// </summary>
    internal static async Task<Streamed> ReadStreamAsync(HttpResponseMessage res, Action<string?>? writing, CancellationToken ct, TimeSpan? quiet = null)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        string finish = "";
        long tokensIn = 0, tokensOut = 0;
        DateTime? first = null;
        bool done = false;
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            // A service that says nothing at all for this long (not even a keep-alive) has stopped; waiting on would
            // leave "Thinking…" up for ever, since the request's own timeout ends once the first bytes have come.
            string? line;
            using (var silence = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                silence.CancelAfter(quiet ?? QuietLimit);
                try { line = await reader.ReadLineAsync(silence.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new(text.ToString(), [], finish, tokensIn, tokensOut, $"nothing came for {(quiet ?? QuietLimit).TotalSeconds:0} s", first, CutOff: true);
                }
            }
            if (line is null) break;
            // Blank lines end an event; ": ..." lines are keep-alive comments (OpenRouter's "processing").
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim();
            if (data == "[DONE]") { done = true; break; }
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); } catch (JsonException) { continue; }
            if (chunk is not JsonObject) continue;
            if (chunk["error"] is { } err) return new(text.ToString(), [], finish, tokensIn, tokensOut, Str(err["message"]) ?? err.ToJsonString(), first);
            if (chunk["usage"] is JsonObject usage)
            {
                tokensIn = Num(usage["prompt_tokens"]) ?? tokensIn;
                tokensOut = Num(usage["completion_tokens"]) ?? tokensOut;
            }
            // The last chunk of some services is usage alone, with no choices ("choices": []).
            if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
            if (Str(choice["finish_reason"]) is { Length: > 0 } why) finish = why;
            var delta = choice["delta"] as JsonObject;
            if (Str(delta?["content"]) is { Length: > 0 } words)
            {
                first ??= DateTime.Now;
                text.Append(words);
                if (calls.Count == 0) writing?.Invoke(text.ToString());
            }
            foreach (var piece in delta?["tool_calls"] as JsonArray ?? [])
            {
                if (piece is not JsonObject) continue;
                int index = (int?)Num(piece["index"]) ?? calls.Count;
                var (id, name, args) = calls.TryGetValue(index, out var had) ? had : ("", "", new StringBuilder());
                if (Str(piece["id"]) is { Length: > 0 } newId) id = newId;
                if (Str(piece["function"]?["name"]) is { Length: > 0 } newName) name = newName;
                args.Append(Str(piece["function"]?["arguments"]) ?? "");
                calls[index] = (id, name, args);
            }
        }
        // Ended with neither [DONE] nor a reason to stop: the connection dropped, and what came is only part of it.
        if (!done && finish.Length == 0 && (text.Length > 0 || calls.Count > 0))
            return new(text.ToString(), [], finish, tokensIn, tokensOut, "the answer was cut off before its end", first, CutOff: true);
        var list = calls.Values.Select(c => new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.Length == 0 ? "{}" : c.Args.ToString() },
        }).ToList();
        return new(text.ToString(), list, finish, tokensIn, tokensOut, null, first);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string key)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (url.StartsWith("https://openrouter.ai/", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Add("HTTP-Referer", "https://rigsight.app");
            req.Headers.Add("X-Title", "RigSight");
        }
        return req;
    }

    /// <summary>
    /// What a refused request means for the person: a wrong key, no credit left, too busy, or the service's own words.
    /// <paramref name="inSettings"/>: said under the key in Settings (after Test), so it doesn't send them to Settings.
    /// </summary>
    private static async Task<string> ProblemAsync(string service, HttpResponseMessage res, CancellationToken ct, bool inSettings = false)
    {
        string text = await res.Content.ReadAsStringAsync(ct);
        string? said = null;
        try { said = (string?)JsonNode.Parse(text)?["error"]?["message"]; } catch (JsonException) { }
        Log.Write("own-key", $"{service} refused: HTTP {(int)res.StatusCode}: {said ?? text[..Math.Min(300, text.Length)]}");
        return res.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => inSettings
                ? Lang.T($"{service} didn't accept this key. Check that it was copied in full, then press Test again.")
                : Lang.T($"{service} didn't accept the API key. Check it in Settings › Ask RigSight."),
            HttpStatusCode.PaymentRequired => inSettings
                ? Lang.T($"Your {service} account is out of credit. Add some on {service}'s site, then press Test again.")
                : Lang.T($"Your {service} account is out of credit. Add some on {service}'s site, then ask again."),
            HttpStatusCode.TooManyRequests => inSettings
                ? Lang.T($"{service} is busy right now. Press Test again in a moment.")
                : Lang.T($"{service} is busy right now. Ask again in a moment."),
            _ => Lang.T($"{service} couldn't answer: {said ?? ((int)res.StatusCode).ToString(CultureInfo.InvariantCulture)}"),
        };
    }
}

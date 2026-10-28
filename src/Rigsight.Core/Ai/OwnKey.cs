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
/// <remarks>
/// The facts beyond the id come from the service's own list (no ratings of our own: the user's ruling, 2026-10-27, so the
/// model table shows only real facts). <see cref="Tier"/> marks the three suggested ones ("Best answers", "Balanced",
/// "Cheapest", English keys for Lang.T); <see cref="Tested"/> stays for older callers and means the same as a tier.
/// </remarks>
public sealed record ServiceModel(string Id, double? CentsPerQuestion, bool Tested)
{
    /// <summary>The name to show: the service's display name (OpenRouter's without its "Maker: " start), else the id.</summary>
    public string Name { get; init; } = Id;
    /// <summary>Who made it ("Anthropic", "Google"), when the service says or the id makes it plain.</summary>
    public string? Maker { get; init; }
    /// <summary>How much it reads at once, in tokens, when the service says.</summary>
    public int? ContextTokens { get; init; }
    /// <summary>"Best answers", "Balanced" or "Cheapest" for the three suggested models; null for the rest.</summary>
    public string? Tier { get; init; }
    /// <summary>When the service says it came out (Anthropic's created_at, OpenAI's created), in UTC; null when it doesn't say.</summary>
    public DateTime? Released { get; init; }
}

/// <summary>
/// The person's own API key in use, kept with the others in ai-keys.bin (<see cref="SavedKeys"/>) and locked to their
/// Windows account by Windows itself (DPAPI), so it never sits in settings.json in plain text and another account on the
/// PC can't read it. Before saved keys there was one key in ai-key.bin; it moves into the list the first time it's read.
/// </summary>
public static class KeyStore
{
    /// <summary>The old one-key file (before 2026-10-27), read once to move it into the saved keys.</summary>
    public static string File => Path.Combine(SavedKeys.Folder, "ai-key.bin");

    /// <summary>A key that works: the one in use gets it, or it is saved as a new key for the current service.</summary>
    public static void Save(string key) => SavedKeys.SaveSecret(key);

    /// <summary>The key in use; null when none is saved or it can't be read (another account, a copied file).</summary>
    public static string? Load() => SavedKeys.Active()?.Secret is { Length: > 0 } secret ? secret : LoadOld();

    /// <summary>The old file's key, only while it hasn't moved (it couldn't be written, say).</summary>
    internal static string? LoadOld()
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

    /// <summary>
    /// Told on the last round, when no more look-ups are allowed: an answer from what was found instead of "kept asking
    /// for records" (the person's call, 2026-10-27), saying what it would still check, as the first Ask next question.
    /// The relay tells RigSight AI the same.
    /// </summary>
    public const string LastRound = "This is your last step for this question: you can't look anything else up now. Write the full answer from what you've already found. If there is something you'd still want to look at, add one plain sentence just before the Next step line saying what you can check next (for example \"I can also look at your fan speeds during those shut-offs.\"), and make that the first Ask next question, worded as their question (\"Check my fan speeds during those shut-offs\").";

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
    public static Task<(IReadOnlyList<ServiceModel>? Models, string? Problem)> ModelsAsync(string service, string root, string key, CancellationToken ct = default)
        => ModelsAsync(service, root, key, filterTools: true, ct);

    /// <param name="filterTools">Off for a server of the person's own: those don't say what their models can do, so
    /// every model is listed and the one picked is tried instead (<see cref="ProbeToolsAsync"/>).</param>
    internal static async Task<(IReadOnlyList<ServiceModel>? Models, string? Problem)> ModelsAsync(string service, string root, string key, bool filterTools, CancellationToken ct = default)
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
            var list = Parse(JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"] as JsonArray ?? [], root, filterTools);
            Log.Write("own-key", $"{service} key works: {list.Count} models it can use, {list.Count(m => m.Tier is not null)} of them suggested ({string.Join(", ", list.Where(m => m.Tier is not null).Select(m => $"{m.Tier}: {m.Id}"))})");
            return list.Count == 0 ? (null, Lang.T($"{service} accepted the key but listed no models that can look at RigSight's records.")) : (list, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Write("own-key", $"No answer from {service} at {root}: expected its model list, got {ex.GetType().Name}: {ex.Message}");
            if (await Internet.ExplainsAsync(ex)) return (null, Internet.NoInternet(inSettings: true));
            return (null, Lang.T($"{service} isn't answering. Check your internet connection, then press Connect again."));
        }
    }


    /// <summary>OpenRouter's three suggested models by id; shown only when the key lists them.</summary>
    private static readonly (string Id, string Tier)[] OpenRouterTiers =
        [("anthropic/claude-sonnet-5.5", "Best answers"), ("google/gemini-3.8-flash", "Balanced"), ("deepseek/deepseek-v4.1-flash", "Cheapest")];

    /// <summary>
    /// A service's model list as RigSight shows it: each model's name, maker, context and cost from what the service says,
    /// and the three suggested ones marked. Only models that can use look-ups, where the service says which can.
    /// </summary>
    internal static List<ServiceModel> Parse(JsonArray data, string root, bool filterTools = true)
    {
        bool anthropic = root.StartsWith("https://api.anthropic.com/", StringComparison.OrdinalIgnoreCase);
        var list = new List<(ServiceModel Model, DateTime? Created)>();
        foreach (var m in data)
        {
            string id = Str(m?["id"]) ?? "";
            if (id.Length == 0) continue;
            if (filterTools && m?["supported_parameters"] is JsonArray can && !can.Any(p => Str(p) == "tools")) continue;
            string shown = Str(m?["display_name"]) ?? Str(m?["name"]) ?? id;
            string? maker = null;
            // OpenRouter names them "Anthropic: Claude Sonnet 5.5": the part before the colon is the maker.
            int colon = shown.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && Str(m?["display_name"]) is null) { maker = shown[..colon]; shown = shown[(colon + 2)..]; }
            maker ??= anthropic ? "Anthropic" : id.Contains('/') ? MakerFrom(id[..id.IndexOf('/')]) : MakerFrom(Str(m?["owned_by"]));
            long? context = Num(m?["context_length"]) ?? Num(m?["max_input_tokens"]) ?? Num(m?["max_model_len"]) ?? Num(m?["context_window"]);
            DateTime? created = DateTime.TryParse(Str(m?["created_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at) ? at
                : Num(m?["created"]) is long unix && unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : null;
            list.Add((new ServiceModel(id, Cents(m?["pricing"]), false)
            {
                Name = shown,
                Maker = maker,
                ContextTokens = context is > 0 and <= int.MaxValue ? (int)context : null,
                Released = created,
            }, created));
        }
        var tiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (IsOpenRouter(root))
            foreach (var (id, tier) in OpenRouterTiers)
                if (list.Any(x => x.Model.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) tiers[id] = tier;
        if (anthropic)
            // Anthropic lists the newest first; by its date when it gives one. The newest of each line is its pick.
            foreach (var (line, tier) in new[] { ("claude-opus-", "Best answers"), ("claude-sonnet-", "Balanced"), ("claude-haiku-", "Cheapest") })
            {
                var newest = list.Select((x, i) => (x.Model, x.Created, Order: i))
                    .Where(x => x.Model.Id.StartsWith(line, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Created ?? DateTime.MinValue).ThenBy(x => x.Order).FirstOrDefault();
                if (newest.Model is not null) tiers[newest.Model.Id] = tier;
            }
        return [.. list.Select(x => tiers.TryGetValue(x.Model.Id, out var t) ? x.Model with { Tested = true, Tier = t } : x.Model)];
    }

    /// <summary>A maker's usual name from an id's start or a service's owned_by ("meta-llama" is Meta); null for "system" and such.</summary>
    private static string? MakerFrom(string? word) => (word ?? "").ToLowerInvariant() switch
    {
        "" or "system" or "user" or "organization-owner" or "library" or "llamacpp" or "vllm" => null,
        "openai" or "openai-internal" => "OpenAI",
        "anthropic" => "Anthropic",
        "google" or "google-deepmind" => "Google",
        "meta-llama" or "meta" => "Meta",
        "mistralai" or "mistral" => "Mistral",
        "deepseek" or "deepseek-ai" => "DeepSeek",
        "x-ai" or "xai" => "xAI",
        "qwen" => "Qwen",
        "moonshotai" => "Moonshot",
        "cohere" => "Cohere",
        "microsoft" => "Microsoft",
        "nvidia" => "NVIDIA",
        string w => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.Replace('-', ' ')),
    };

    /// <summary>What connecting found: where it answered, its models or why none, and what the person should know.</summary>
    /// <param name="Root">Where questions go: the service's address, or the server's with /v1 when it answered there.</param>
    /// <param name="AddedV1">The address answered only with /v1 on its end; <paramref name="Root"/> has it.</param>
    /// <param name="NoModelList">The server answers but doesn't list its models: the person types the model's name.</param>
    /// <param name="IsOllama">Port 11434 is Ollama, which has its own setting; nothing was asked.</param>
    /// <param name="NotPrivate">http:// to a computer outside the person's own network: the key and records would travel unprotected.</param>
    public sealed record ConnectResult(string Root, IReadOnlyList<ServiceModel>? Models, string? Problem, bool AddedV1 = false, bool NoModelList = false, bool IsOllama = false, bool NotPrivate = false);

    /// <summary>
    /// Connects a pasted key or address: a named service lists its models for the key; an address (a server of their own)
    /// is asked for its models at /models, and at /v1/models when that doesn't answer, since most such servers only
    /// answer there (LiteLLM's "Not Found" is a missing /v1). The key is optional for an address.
    /// </summary>
    public static async Task<ConnectResult> ConnectAsync(string service, string key, string address, CancellationToken ct = default)
    {
        key = (key ?? "").Trim();
        address = (address ?? "").Trim();
        if (address.Length == 0 && service != "Custom")
        {
            string root = Root(service, "");
            var (models, problem) = await ModelsAsync(service, root, key, ct);
            return new ConnectResult(root, models, problem);
        }
        if (address.Length == 0) return new ConnectResult("", null, Lang.T("Type the server's address first."));
        string bare = address.TrimEnd('/');
        if (!bare.Contains("://", StringComparison.Ordinal)) bare = "http://" + bare;
        if (!Uri.TryCreate(bare, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new ConnectResult(bare, null, Lang.T($"{address} isn't an address RigSight can use. It looks like http://192.168.1.20:8000/v1."));
        if (uri.Port == 11434)
        {
            Log.Write("own-key", $"{uri.Host}:11434 is Ollama's port: not connected as a server, Ollama has its own setting");
            return new ConnectResult(bare, null, null, IsOllama: true);
        }
        bool notPrivate = uri.Scheme == "http" && !IsOwnNetwork(uri.Host);
        string where = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
        string nothing = Lang.T($"Nothing answered at {address}. Check the server is running and the address is right; these usually end in /v1.");
        string refused = Lang.T("The server didn't accept this key. Check that it was copied in full.");
        var first = await ListAsync(where, key, ct);
        if (first.Models is not null) return new ConnectResult(where, first.Models, null, NotPrivate: notPrivate);
        if (first.Code is null)
            return new ConnectResult(where, null, first.Failed is { } why && await Internet.ExplainsAsync(why) ? Internet.NoInternet(inSettings: true) : nothing, NotPrivate: notPrivate);
        if (first.Code is 401 or 403) return new ConnectResult(where, null, refused, NotPrivate: notPrivate);
        if (!where.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            var withV1 = await ListAsync(where + "/v1", key, ct);
            if (withV1.Models is not null)
            {
                Log.Write("own-key", $"{where} answered only with /v1 on the end: saved as {where}/v1");
                return new ConnectResult(where + "/v1", withV1.Models, null, AddedV1: true, NotPrivate: notPrivate);
            }
            if (withV1.Code is 401 or 403) return new ConnectResult(where + "/v1", null, refused, NotPrivate: notPrivate);
            if (withV1.Code == 404 && first.Code == 404)
            {
                Log.Write("own-key", $"{where} answers but lists no models at /models or /v1/models: the person types the model's name");
                return new ConnectResult(where + "/v1", [], null, NoModelList: true, NotPrivate: notPrivate);
            }
        }
        else if (first.Code == 404)
        {
            Log.Write("own-key", $"{where} answers but lists no models at /models: the person types the model's name");
            return new ConnectResult(where, [], null, NoModelList: true, NotPrivate: notPrivate);
        }
        return new ConnectResult(where, null, Lang.T($"{address} answered, but not as an AI server does (HTTP {first.Code.Value.ToString(CultureInfo.InvariantCulture)})."), NotPrivate: notPrivate);
    }

    /// <summary>One try at a server's model list: its models, or the HTTP code it gave, or what stopped the request.</summary>
    private static async Task<(List<ServiceModel>? Models, int? Code, Exception? Failed)> ListAsync(string root, string key, CancellationToken ct)
    {
        try
        {
            using var req = Request(HttpMethod.Get, root + "/models", key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var res = await Http.SendAsync(req, timeout.Token);
            string body = await res.Content.ReadAsStringAsync(timeout.Token);
            if (!res.IsSuccessStatusCode)
            {
                Log.Write("own-key", $"Expected a model list at {root}/models, got HTTP {(int)res.StatusCode}");
                return (null, (int)res.StatusCode, null);
            }
            JsonArray? data = null;
            try { data = JsonNode.Parse(body)?["data"] as JsonArray; } catch (JsonException) { }
            if (data is null)
            {
                // A web page or anything else at that path: as good as no list there.
                Log.Write("own-key", $"Expected a model list at {root}/models, got {body.Length} characters without one");
                return (null, 404, null);
            }
            var list = Parse(data, root, filterTools: false);
            Log.Write("own-key", $"{root} lists {list.Count} models");
            return (list, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Write("own-key", $"Expected a model list at {root}/models, got {ex.GetType().Name}: {ex.Message}");
            return (null, null, ex);
        }
    }

    /// <summary>The person's own computer or network: localhost, 127.x, 10.x, 192.168.x, 172.16-31.x, 169.254.x or a .local name.</summary>
    internal static bool IsOwnNetwork(string host)
    {
        host = host.Trim('[', ']').ToLowerInvariant();
        if (host is "localhost" or "::1" || host.EndsWith(".local", StringComparison.Ordinal)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254));
    }

    /// <summary>
    /// Whether a model on a server of the person's own can use look-ups: there's no standard way to ask a server (vLLM, for
    /// one, only calls tools when started with --enable-auto-tool-choice), so it is asked one tiny question with one harmless
    /// tool. True when it calls the tool, false when it answers in words, null when it couldn't be asked.
    /// </summary>
    public static async Task<bool?> ProbeToolsAsync(string root, string key, string model, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 60,
            ["stream"] = false,
            ["tool_choice"] = "auto",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "What time is it? Use the get_time tool to find out." }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "get_time",
                    ["description"] = "The current time on this PC.",
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                },
            }),
        };
        try
        {
            using var req = Request(HttpMethod.Post, root.TrimEnd('/') + "/chat/completions", key);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var res = await Http.SendAsync(req, timeout.Token);
            string text = await res.Content.ReadAsStringAsync(timeout.Token);
            if (!res.IsSuccessStatusCode)
            {
                Log.Write("own-key", $"Look-up check of {model} at {root}: expected an answer, got HTTP {(int)res.StatusCode}: {text[..Math.Min(200, text.Length)]}");
                return null;
            }
            var message = JsonNode.Parse(text)?["choices"]?[0]?["message"];
            bool calls = message?["tool_calls"] is JsonArray { Count: > 0 };
            Log.Write("own-key", calls
                ? $"Look-up check of {model} at {root}: it called the tool, so it can read the records"
                : $"Look-up check of {model} at {root}: expected a tool call, got words instead ({(Str(message?["content"]) ?? "").Length} characters), so it can't read the records");
            return calls;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Write("own-key", $"Look-up check of {model} at {root}: expected an answer, got {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>For a test: OpenRouter's address, pointed at a fake server.</summary>
    internal static readonly AsyncLocal<string?> TestOpenRouter = new();

    /// <summary>
    /// An OpenRouter key's credit left and this month's spend, in dollars, from its /credits and /key: both shown on the
    /// key's row. Each null when it can't be read (a key without the right, no answer).
    /// </summary>
    public static async Task<(double? CreditLeft, double? MonthSpent)> CreditAsync(string key, CancellationToken ct = default)
    {
        string root = TestOpenRouter.Value ?? "https://openrouter.ai/api/v1";
        double? left = null, month = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using (var req = Request(HttpMethod.Get, root + "/credits", key))
            using (var res = await Http.SendAsync(req, timeout.Token))
                if (res.IsSuccessStatusCode && JsonNode.Parse(await res.Content.ReadAsStringAsync(timeout.Token))?["data"] is { } d
                    && Dollars(d["total_credits"]) is double total && Dollars(d["total_usage"]) is double used)
                    left = Math.Max(0, total - used);
            using (var req = Request(HttpMethod.Get, root + "/key", key))
            using (var res = await Http.SendAsync(req, timeout.Token))
                if (res.IsSuccessStatusCode && JsonNode.Parse(await res.Content.ReadAsStringAsync(timeout.Token))?["data"] is { } d)
                    month = Dollars(d["usage_monthly"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Write("own-key", $"Expected OpenRouter's credit and spend, got {ex.GetType().Name}: {ex.Message}");
        }
        Log.Write("own-key", $"OpenRouter credit: {(left is double l ? $"${l:0.00} left" : "unknown")}, this month {(month is double mo ? $"${mo:0.00}" : "unknown")}");
        return (left, month);
    }

    private static double? Dollars(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue(out double d) && double.IsFinite(d) ? d
        : v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null
        : null;

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
        // The apps an answer can open: the prompt's [do: app] button needs to know which are on this PC.
        tools.OpenableAppsLookUp = true;
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
            bool last = round == MaxRounds - 1;
            var sending = (JsonArray)JsonNode.Parse(messages.ToJsonString())!;
            if (last) sending.Add(new JsonObject { ["role"] = "system", ["content"] = LastRound });
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = sending,
                ["tools"] = JsonNode.Parse(definitions.ToJsonString()),
                ["stream"] = true,
            };
            if (last) body["tool_choice"] = "none";
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
                return new AiReply(null, Lang.T($"{service} couldn't answer ({error})"), lookUps);
            }
            Log.Write("own-key", $"{model} round {round}: first words after {(got.FirstWords is { } f ? $"{(f - asked).TotalSeconds:0.0} s" : "none")}, done after {(DateTime.Now - asked).TotalSeconds:0.0} s");
            if (got.Calls.Count > 0 && IsAnswer(got.Text, lookUps.Count))
                Log.Write("own-key", $"{model} round {round}: wrote an answer ({got.Text.Trim().Length} chars) and asked for {string.Join(", ", got.Calls.Select(c => (string?)c["function"]?["name"]))} too: kept the answer, the look-ups not run");
            else if (got.Calls.Count > 0)
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
    /// <summary>
    /// Words already written in a round that also asks for look-ups: an answer (lines of it, or a paragraph), not a
    /// lead-in ("Let me check your temperatures."). Shown as they came, an answer stays the answer: dropping it for the
    /// look-ups showed one answer, took it away, waited and wrote another (the person's report, 2026-10-24; the model had
    /// written its answer, then asked again for temperatures it already had).
    /// <para>
    /// Only once something was looked up in this question (a first reply's words are a lead-in), only a real paragraph (160
    /// letters, or lines adding up to 80), and never words that end as a lead-in does (":" or "…"): a two-line "Let me check
    /// your temperatures and crashes." must not become the whole answer.
    /// </para>
    /// </summary>
    internal static bool IsAnswer(string text, int lookedUp)
    {
        string t = text.Trim();
        if (lookedUp == 0 || t.EndsWith(':') || t.EndsWith('…') || t.EndsWith("...", StringComparison.Ordinal)) return false;
        return t.Length >= 160 || (t.Contains('\n') && t.Length >= 80);
    }

    internal sealed record Streamed(string Text, List<JsonObject> Calls, string Finish, long TokensIn, long TokensOut, string? Error, DateTime? FirstWords, bool CutOff = false);

    /// <summary>
    /// Reads a streamed reply (server-sent events, "data: {...}" lines ending with "data: [DONE]"), handing on the words so
    /// far as they come and putting each look-up back together from its pieces (they arrive by index, the arguments in parts).
    /// </summary>
    /// <param name="relayStep">The relay's own word on what it's doing ("writing": thinking ran out, so it asks again without), never the model's.</param>
    internal static async Task<Streamed> ReadStreamAsync(HttpResponseMessage res, Action<string?>? writing, CancellationToken ct, TimeSpan? quiet = null, Action<string>? relayStep = null)
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
            if (Str(chunk["rigsight"]?["step"]) is { Length: > 0 } said) { relayStep?.Invoke(said); continue; }
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

    internal static HttpRequestMessage Request(HttpMethod method, string url, string key)
    {
        var req = new HttpRequestMessage(method, url);
        // A server of their own may need no key: then no header at all, rather than "Bearer " with nothing.
        if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        // Anthropic wants its version on every request and the key as x-api-key: without the version, even its model
        // list answered 400 "anthropic-version: header is required" (a real key, 2026-10-27), so no Anthropic key worked.
        if (url.StartsWith("https://api.anthropic.com/", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Add("anthropic-version", "2023-06-01");
            if (key.Length > 0) req.Headers.Add("x-api-key", key);
        }
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
                ? Lang.T($"{service} didn't accept this key. Check that it was copied in full, then press Connect again.")
                : Lang.T($"{service} didn't accept the API key. Check it in Settings › Ask RigSight."),
            HttpStatusCode.PaymentRequired => inSettings
                ? Lang.T($"Your {service} account is out of credit. Add some on {service}'s site, then press Connect again.")
                : Lang.T($"Your {service} account is out of credit. Add some on {service}'s site, then ask again."),
            HttpStatusCode.TooManyRequests => inSettings
                ? Lang.T($"{service} is busy right now. Press Connect again in a moment.")
                : Lang.T($"{service} is busy right now. Ask again in a moment."),
            _ => Lang.T($"{service} couldn't answer ({said ?? ((int)res.StatusCode).ToString(CultureInfo.InvariantCulture)})"),
        };
    }
}

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Rigsight.Core.Ai;

/// <summary>A pack RigSight AI sells, from the relay, with its checkout link (which carries this PC's id).</summary>
public sealed record RelayPack(int Questions, string Price, string Url);

/// <summary>What the relay says about this PC: questions left in all, the free ones, and its licence if it has one.</summary>
public sealed record RelayStatus(int QuestionsLeft, int FreeLeft, string? Key, int LicenceQuestions, string? LicenceStatus, IReadOnlyList<RelayPack> Packs)
{
    public bool HasLicence => Key is not null;

    /// <summary>The plan this PC is on now (Free when it has none); null from a relay from before plans.</summary>
    public RelayPlan? Plan { get; init; }

    /// <summary>The plans to choose from, Free first, each with the pages it opens and its checkout link.</summary>
    public IReadOnlyList<RelayPlanOffer> Plans { get; init; } = [];

    /// <summary>Questions given from the admin site that are still running, newest first; none from an older relay.</summary>
    public IReadOnlyList<RelayGift> Gifts { get; init; } = [];

    /// <summary>The free trial: offered, running or ended; null when none is offered and this PC never had one.</summary>
    public RelayTrial? Trial { get; init; }
}

/// <summary>
/// RigSight AI, through the relay (api.rigsight.app, a private repo of its own). The app holds no key: it sends each
/// round of the conversation with the look-ups it can run; the relay adds RigSight AI's instructions and web search,
/// asks the model, and streams the reply back. Look-ups still run here, on the PC; web searches the model asks for go
/// to the relay (/v1/search), which has the search key. Questions are counted there, once per question, and given
/// back when something fails on its side; the relay's errors come in four kinds and are put in words here.
/// </summary>
public static partial class Relay
{
    /// <summary>What this app can show, sent with each question, so the relay sends the rules for it and old apps keep theirs.</summary>
    public static class Features
    {
        /// <summary>
        /// The buttons of 2026-10-24: "[do: app …]" and the new Windows screens, no button for a page the plan or Settings › AI
        /// keeps from the answers, and "[locked: page | what they'd get]" drawn as a card.
        /// </summary>
        public const string Actions2 = "actions-2";

        /// <summary>Off only in the answer comparison (tests/Rigsight.AskEval), to ask as a released app does: the old prompt, no apps look-up.</summary>
        public static bool UseActions2 { get; set; } = true;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>How long a streamed answer may say nothing at all (not even a keep-alive) before the app gives up on it.</summary>
    internal static TimeSpan QuietLimit { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The relay. A test copy can point at another: relay-url.txt in its data folder (e.g. wrangler dev's), or
    /// RIGSIGHT_RELAY_URL (the tests, so they never reach the real one).
    /// </summary>
    public static string Root
    {
        get
        {
            if (TestRoot.Value is { } forTest) return forTest;
            if (RigsightPaths.IsTestInstance)
            {
                if (Environment.GetEnvironmentVariable("RIGSIGHT_RELAY_URL") is { Length: > 0 } fromTests) return fromTests.TrimEnd('/');
                var file = Path.Combine(RigsightPaths.DataDir, "relay-url.txt");
                try { if (File.Exists(file) && File.ReadAllText(file).Trim().TrimEnd('/') is { Length: > 0 } url) return url; }
                catch (IOException) { }
            }
            return "https://api.rigsight.app";
        }
    }

    /// <summary>Tests: a relay for the calls made in this async flow only, so tests running side by side never share one.</summary>
    internal static readonly AsyncLocal<string?> TestRoot = new();

    public static string Version { get; } = typeof(Relay).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// This PC, as the relay knows it: a SHA-256 of Windows' own id for this install (MachineGuid) and the Windows account
    /// using it, so it stays the same when RigSight is reinstalled and says nothing about the person. Without one, a random
    /// id kept in the data folder.
    /// </summary>
    /// <remarks>
    /// The account is part of it since 1.0.0: each account keeps its own secret (ai-secret.bin, locked to that account), so
    /// with one id for the whole PC only the first account to ask was let in; every other one was told its sign-in changed
    /// (a family PC, or a work PC set up for everyone). Each account is its own "PC" to the relay now: its own free
    /// questions, trial and plan, and no one else on the PC sees its licence or can cancel its plan.
    /// </remarks>
    public static string DeviceId => _deviceId ??= MakeDeviceId();
    private static string? _deviceId;

    private static string MakeDeviceId()
    {
        string? machine = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machine = key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        if (string.IsNullOrWhiteSpace(machine))
        {
            var file = Path.Combine(RigsightPaths.DataDir, "device-id.txt");
            try
            {
                if (File.Exists(file)) machine = File.ReadAllText(file).Trim();
                if (string.IsNullOrWhiteSpace(machine)) { machine = Guid.NewGuid().ToString(); Directory.CreateDirectory(RigsightPaths.DataDir); File.WriteAllText(file, machine); }
            }
            catch (IOException) { machine = Guid.NewGuid().ToString(); }
        }
        string account = "";
        try { account = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? ""; }
        catch (System.Security.SecurityException) { }
        // A test copy is its own PC to the relay, so its tests never spend the real install's free questions.
        string salt = RigsightPaths.IsTestInstance ? "rigsight-test:" + RigsightPaths.InstanceSuffix + ":" : "rigsight:";
        return IdFor(salt, machine, account);
    }

    /// <summary>The id for this install of Windows and this account on it (the account's SID; none when it can't be read).</summary>
    internal static string IdFor(string salt, string machine, string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + machine + (account.Length > 0 ? ":" + account : "")))).ToLowerInvariant();

    // ── Questions left and the licence ───────────────────────────────────
    public static async Task<(RelayStatus? Status, string? Problem)> StatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await Http.SendAsync(Request(HttpMethod.Get, "/v1/status"), ct);
            string text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (null, Problem(text, (int)res.StatusCode, inSettings: true));
            if (ReadStatus(text) is { } status) return (status, null);
            Log.Write("relay", $"Expected this PC's questions from {Root}, got HTTP {(int)res.StatusCode} {Short(text)}");
            return (null, Lang.T("Couldn't check your questions right now. Try again in a minute."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException && !ct.IsCancellationRequested)
        {
            Log.Write("relay", $"Expected this PC's questions from {Root}, got {ex.GetType().Name}: {ex.Message}");
            return (null, await Unreachable(ex, checking: true));
        }
    }

    /// <summary>A key typed in Settings; the problem says why it can't be used here ("in use on another PC").</summary>
    public static async Task<(RelayStatus? Status, string? Problem, string? Reason)> ActivateAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var req = Request(HttpMethod.Post, "/v1/activate");
            req.Content = new StringContent(new JsonObject { ["key"] = key.Trim() }.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct);
            string text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                Log.Write("relay", $"Activating a key: expected it to work here, got HTTP {(int)res.StatusCode} {text}");
                return (null, Problem(text, (int)res.StatusCode, activating: true, inSettings: true), Reason(text));
            }
            if (ReadStatus(text) is not { } status)
            {
                Log.Write("relay", $"Activating a key: expected this PC's licence, got HTTP {(int)res.StatusCode} {Short(text)}");
                return (null, Lang.T("RigSight AI isn't answering, so the key couldn't be checked. Try again in a minute."), null);
            }
            Log.Write("relay", "Licence activated on this PC");
            return (status, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException && !ct.IsCancellationRequested)
        {
            Log.Write("relay", $"Activating a key at {Root}: {ex.GetType().Name}: {ex.Message}");
            return (null, await Unreachable(ex, inSettings: true), null);
        }
    }

    /// <summary>The relay's standing for this PC, or null when the reply isn't one (a page, nonsense, the wrong shape).</summary>
    internal static RelayStatus? ReadStatus(string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); } catch (JsonException) { return null; }
        if (node is not JsonObject s || OwnKey.Num(s["questions_left"]) is not { } left) return null;
        var licence = s["licence"] as JsonObject;
        return new RelayStatus(
            (int)Math.Clamp(left, 0, int.MaxValue),
            (int)Math.Clamp(OwnKey.Num(s["free_left"]) ?? 0, 0, int.MaxValue),
            Text(licence?["key"]),
            (int)Math.Clamp(OwnKey.Num(licence?["questions_left"]) ?? 0, 0, int.MaxValue),
            Text(licence?["status"]),
            [.. (s["packs"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => new RelayPack((int)Math.Clamp(OwnKey.Num(p["questions"]) ?? 0, 0, int.MaxValue), Text(p["price"]) ?? "", Text(p["url"]) ?? ""))
                .Where(p => p.Questions > 0)])
        {
            Plan = s["plan"] is JsonObject plan ? ReadPlan(plan) : null,
            Plans = [.. (s["plans"] as JsonArray ?? []).OfType<JsonObject>().Select(ReadOffer).OfType<RelayPlanOffer>()],
            Gifts = [.. (s["gifts"] as JsonArray ?? []).OfType<JsonObject>().Select(ReadGift).OfType<RelayGift>()],
            Trial = ReadTrial(s["trial"]),
        };
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>A reply for the log: the start of it, on one line.</summary>
    private static string Short(string text) => text.Length <= 200 ? text.ReplaceLineEndings(" ") : text[..200].ReplaceLineEndings(" ") + "…";

    // ── Asking ───────────────────────────────────────────────────────────
    /// <summary>
    /// Asks RigSight AI as <see cref="OwnKey.AskAsync"/> asks a service: round after round, the look-ups run here,
    /// the answer streaming in through <paramref name="writing"/>. One question id for all its rounds, so it's counted once.
    /// </summary>
    public static async Task<AiReply> AskAsync(IReadOnlyList<AiTurn> earlier, string question, AiTools tools, Action<AiStep>? step = null,
        Action<string?, IReadOnlyList<AiLookUp>>? writing = null, CancellationToken ct = default, Action<string>? asking = null)
    {
        var started = DateTime.Now;
        if (Internet.IsOff())
        {
            Log.Write("relay", "Not asked: Windows has no network connection");
            return new AiReply(null, Internet.NoInternet(), []);
        }
        string id = Guid.NewGuid().ToString("N");
        asking?.Invoke(id);
        var messages = new JsonArray();
        foreach (var turn in AiTurns.Recent(earlier)) messages.Add(AiTurns.Message(turn));
        int questionAt = messages.Count;
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
        var lookUps = new List<AiLookUp>();
        // The apps an answer can open (Ollama and own key look them up too since their prompts took RigSight AI's buttons, 2026-10-27).
        tools.OpenableAppsLookUp = Features.UseActions2;
        var definitions = tools.Definitions();
        bool searchFailed = false;
        Log.Write("relay", $"Asking RigSight AI at {Root} (question {id[..8]}, {definitions.Count} kinds of look-up open on the plan, at most {MaxRounds} rounds, {AiTurns.Recent(earlier).Count} of {earlier.Count} earlier messages)");
        step?.Invoke(new AiStep(""));
        for (int round = 0; round < MaxRounds; round++)
        {
            // Under the size guard (AiMemory.SafeRequest): only huge pastes or many big look-ups come near it.
            var sending = (JsonArray)JsonNode.Parse(messages.ToJsonString())!;
            if (AiMemory.Fit(sending, questionAt, definitions.ToJsonString().Length + 300) is > 0 and var shortened)
                Log.Write("relay", $"Round {round}: {shortened} long earlier message{(shortened == 1 ? "" : "s")} shortened in this request to keep it under {AiMemory.SafeRequest:N0} characters");
            var body = new JsonObject
            {
                ["question_id"] = id,
                ["now"] = started.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                ["messages"] = sending,
                ["tools"] = JsonNode.Parse(definitions.ToJsonString()),
                // What this app reads in an answer beyond the words: a "Title:" line names the conversation.
                ["features"] = Features.UseActions2 ? new JsonArray("title", Features.Actions2) : new JsonArray("title"),
            };
            OwnKey.Streamed got;
            var asked = DateTime.Now;
            try
            {
                var req = Request(HttpMethod.Post, "/v1/ask");
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode)
                {
                    string text = await res.Content.ReadAsStringAsync(ct);
                    Log.Write("relay", $"Round {round}: expected an answer, got HTTP {(int)res.StatusCode} {text}");
                    // Grown too big or too dear after its first round (2026-10-27: the app said "wasn't counted" for a
                    // refused fifth round): the relay counted it, and gives no question back for that.
                    if (round > 0 && TooBig(text))
                        return new AiReply(null, Lang.T("This answer grew too big to finish, so RigSight AI stopped it. Try asking about one part at a time."), lookUps);
                    return new AiReply(null, Problem(text, (int)res.StatusCode), lookUps, Limit: ReadLimit(text));
                }
                // A round that thought until its limit is asked again without thinking (2026-10-27): said, so the long
                // wait reads as progress.
                got = await OwnKey.ReadStreamAsync(res, writing is null ? null : t => writing(t, lookUps), ct, QuietLimit, said =>
                {
                    Log.Write("relay", $"Round {round}: thinking ran out before any words, so the relay asked again without it");
                    if (said == "writing") step?.Invoke(new AiStep(Lang.T("Writing your answer…")));
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException or UriFormatException && !ct.IsCancellationRequested)
            {
                Log.Write("relay", $"No answer from {Root} in round {round}: {ex.GetType().Name}: {ex.Message}");
                return new AiReply(null, await Unreachable(ex), lookUps);
            }
            if (got.Error is not null)
            {
                // Stopped here (silence, a dropped connection): the relay may have counted it, so no "wasn't counted".
                if (got.CutOff)
                {
                    Log.Write("relay", $"Round {round}: expected the answer to its end, got: {got.Error} ({got.Text.Length} chars came)");
                    return new AiReply(null, Lang.T("The answer stopped before it finished. Ask again in a minute."), lookUps);
                }
                Log.Write("relay", $"Round {round}: the answer broke off ({got.Error}); the relay gives the question back");
                return new AiReply(null, Lang.T("RigSight AI couldn't answer this time. Your question wasn't counted. Ask again in a minute."), lookUps);
            }
            Log.Write("relay", $"Round {round}: first words after {(got.FirstWords is { } f ? $"{(f - asked).TotalSeconds:0.0} s" : "none")}, done after {(DateTime.Now - asked).TotalSeconds:0.0} s");
            // An answer already written stays the answer, even when the model also asked to look something up again.
            if (got.Calls.Count > 0 && OwnKey.IsAnswer(got.Text, lookUps.Count))
                Log.Write("relay", $"Round {round}: wrote an answer ({got.Text.Trim().Length} chars) and asked for {string.Join(", ", got.Calls.Select(c => (string?)c["function"]?["name"]))} too: kept the answer, the look-ups not run");
            else if (got.Calls.Count > 0)
            {
                if (got.Text.Length > 0) writing?.Invoke(null, lookUps);
                var calls = new JsonArray([.. got.Calls.Select(c => (JsonNode)c)]);
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = JsonNode.Parse(calls.ToJsonString()) });
                foreach (var call in calls)
                {
                    string name = (string?)call?["function"]?["name"] ?? "";
                    string argsJson = (string?)call?["function"]?["arguments"] ?? "{}";
                    JsonObject args;
                    try { args = JsonNode.Parse(argsJson.Length == 0 ? "{}" : argsJson) as JsonObject ?? []; } catch (JsonException) { args = []; }
                    step?.Invoke(new AiStep(Ollama.Reviewing(name, args)));
                    string result;
                    if (name == "search_web")
                    {
                        var (found, failed) = await SearchAsync(id, (string?)args["query"] ?? "", ct);
                        result = found;
                        searchFailed |= failed;
                    }
                    else result = await Task.Run(() => tools.Run(name, args.ToJsonString()), ct);
                    lookUps.Add(new AiLookUp(name, args, result.Length, name == "search_web" ? AiSources.ParseWeb(result) : null));
                    Log.Write("relay", $"Looked at {name} {args.ToJsonString()}: {result.Length} chars");
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = (string?)call?["id"] ?? "", ["content"] = result });
                }
                step?.Invoke(new AiStep(""));
                continue;
            }
            string answer = got.Text.Trim();
            Log.Write("relay", $"RigSight AI answered in {(DateTime.Now - started).TotalSeconds:0} s after {lookUps.Count} look-ups, {answer.Length} chars, finished: {got.Finish}{(searchFailed ? ", web search failed" : "")}");
            if (answer.Length == 0) return new AiReply(null, Lang.T("RigSight AI couldn't answer this time. Your question wasn't counted. Ask again in a minute."), lookUps);
            return new AiReply(answer, null, lookUps, Note: searchFailed ? Lang.T("Web search wasn't working, so this answer is from your records only.") : null);
        }
        // The relay counted it (its rounds were used), so no "wasn't counted".
        Log.Write("relay", $"Still looking things up after {MaxRounds} rounds");
        return new AiReply(null, Lang.T("RigSight AI couldn't finish this answer. Ask again, or ask about one part of it."), lookUps);
    }

    /// <summary>A web search the model asked for, run by the relay; a failure is one line the model reads, and the answer goes on.</summary>
    private static async Task<(string Text, bool Failed)> SearchAsync(string questionId, string query, CancellationToken ct)
    {
        try
        {
            var req = Request(HttpMethod.Post, "/v1/search");
            req.Content = new StringContent(new JsonObject { ["question_id"] = questionId, ["query"] = query }.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct);
            var node = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            return ((string?)node?["text"] ?? "Error: web search isn't answering right now.", (bool?)node?["failed"] ?? !res.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            Log.Write("relay", $"Searching \"{query}\" failed: {ex.GetType().Name}: {ex.Message}");
            return ("Error: web search isn't answering right now.", true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, Root + path);
        req.Headers.Add("X-RigSight-Device", DeviceId);
        req.Headers.Add("X-RigSight-Version", Version);
        // The PC's own clock, so the day's questions start again at its midnight; and its secret, so knowing this PC's id
        // isn't enough to ask, read its licence or change its plan (see Secret).
        req.Headers.Add("X-RigSight-Now", (Clock?.Invoke() ?? DateTime.Now).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        req.Headers.Add("X-RigSight-Secret", Secret);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("RigSight", Version));
        return req;
    }

    // ── The relay's four kinds of error, in words ────────────────────────
    private static string? Reason(string text)
    {
        try { return (string?)JsonNode.Parse(text)?["reason"]; } catch (JsonException) { return null; }
    }

    /// <summary>What a refusal from the relay means for the person (the mock-up of 2026-10-21).</summary>
    /// <param name="inSettings">Shown in Settings › Ask RigSight, beside Email support (questions left, Activate): no
    /// "from Settings › Ask RigSight" there, it's the page they're on.</param>
    /// <summary>
    /// More rounds than the relay allows (its MAX_ROUNDS, 8): the relay ends a question by having its last round answer
    /// without look-ups, so the relay decides, and a higher limit there needs no app update (2026-10-27).
    /// </summary>
    private const int MaxRounds = 12;

    /// <summary>The relay refused a round for its size or the question's budget ("detail": "too long").</summary>
    internal static bool TooBig(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject e && (string?)e["detail"] == "too long" && (string?)e["reason"] is "bad_request" or "stopped"; }
        catch (JsonException) { return false; }
    }

    public static string Problem(string text, int status, bool activating = false, bool inSettings = false)
    {
        string? kind = null, reason = null;
        try { var e = JsonNode.Parse(text); kind = (string?)e?["error"]; reason = (string?)e?["reason"]; } catch (JsonException) { }
        return (kind, reason) switch
        {
            ("out_of_questions", "week") => Lang.T("That was your last free question this week. They're back on Monday."),
            ("out_of_questions", "day") => Lang.T("That's all your questions for today. More at midnight."),
            ("out_of_questions", "paused") => Lang.T("Free questions are paused for now. A plan keeps answering."),
            ("licence", "has_plan") => Lang.T("This PC already has a plan, so another key can't be used here. Cancel the plan first, or contact support."),
            ("licence", "wrong_secret") => Lang.T("This PC's RigSight AI sign-in changed (often after a reinstall). Contact support with your key."),
            ("unavailable", "too_fast") => Lang.T("That's a lot of questions at once. Wait a minute, then ask again. Your question wasn't counted."),
            ("unavailable", "billing") => Lang.T("Your plan couldn't be changed right now. Nothing was charged. Try again in a minute."),
            ("out_of_questions", "free") => Lang.T("This PC has used its free questions. See plans for more, or use your API key or Ollama."),
            ("out_of_questions", _) => Lang.T("You're out of questions, so this one wasn't sent. See plans in Settings › Ask RigSight."),
            ("licence", "not_found") => Lang.T("That licence key wasn't found. Check it against your receipt email."),
            ("licence", "other_pc") => activating
                ? Lang.T("This key is in use on another PC. A licence works on one PC.")
                : Lang.T("Your licence is now in use on another PC, so this PC can't ask with it."),
            ("licence", "refunded") => Lang.T("This licence was refunded, so it can't ask questions any more. See plans to start a new one."),
            ("licence", _) when inSettings => Lang.T("Your licence is turned off, so this PC can't ask questions. Contact support with your key."),
            ("licence", _) => Lang.T("This licence is turned off. Contact support from Settings › Ask RigSight."),
            ("update_needed", _) => Lang.T("RigSight AI needs the latest RigSight. Update, then ask again."),
            // Past the relay's limits for one question (rounds, size): it was counted, so no "wasn't counted".
            ("unavailable", "stopped") => Lang.T("RigSight AI couldn't finish this answer. Ask again, or ask about one part of it."),
            ("unavailable", "busy") => Lang.T("RigSight AI is busy right now. Your question wasn't counted. Ask again in a moment."),
            _ when status >= 500 || kind == "unavailable" => Lang.T("RigSight AI couldn't answer this time. Your question wasn't counted. Ask again in a minute."),
            _ => Lang.T($"RigSight AI couldn't answer this time (error {status}). Ask again in a minute."),
        };
    }

    /// <summary>No reply from the relay at all: offline, or the relay is down.</summary>
    /// <param name="checking">Asking for questions left (no question was asked, so none to count).</param>
    private static async Task<string> Unreachable(Exception ex, bool inSettings = false, bool checking = false) =>
        // Settings' RigSight AI card has no Test button: "try again", not Own key's "press Test again".
        await Internet.ExplainsAsync(ex) ? (inSettings || checking ? Lang.T("Your PC isn't connected to the internet. Connect, then try again.") : Internet.NoInternet())
        : inSettings ? Lang.T("RigSight AI isn't answering, so the key couldn't be checked. Try again in a minute.")
        : checking ? Lang.T("Couldn't check your questions right now. Try again in a minute.")
        : Lang.T("RigSight AI isn't answering right now. Your question wasn't counted. Try again in a minute.");
}

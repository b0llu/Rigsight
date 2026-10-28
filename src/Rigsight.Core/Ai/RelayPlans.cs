using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Rigsight.Core.Ai;

/// <summary>The card a plan is paid with, as Lemon Squeezy reports it (brand and last four digits only).</summary>
public sealed record RelayCard(string Brand, string Last4)
{
    public string Text => $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Brand)} " + Lang.T($"ending {Last4}");
}

/// <summary>The newest paid payment for the plan (a refunded one never counts), for the plan box's "Last payment".</summary>
public sealed record RelayPayment(int AmountCents, string Currency, DateTime At)
{
    // "$5" like the plan's own price beside it, "$4.17" when there are cents (a switch's part-month payment).
    public string Amount => Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "$" + Number : Number + " " + Currency.ToUpperInvariant();
    private string Number => (AmountCents / 100m).ToString(AmountCents % 100 == 0 ? "0" : "0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// The plan this PC is on, as the relay counts it. <paramref name="Source"/>: free, subscription (bought, managed in the
/// app) or gift (given by support, no payments). <paramref name="State"/>: active, cancelled (works until EndsAt), past_due
/// (a payment failed, still works while Lemon Squeezy retries), paused, expired.
/// </summary>
public sealed record RelayPlan(string Id, string Name, string Source, string State, int Used, int Cap, string Period, DateTime? ResetsAt,
    DateTime? RenewsAt, DateTime? EndsAt, string Price, RelayCard? Card, IReadOnlyList<string> Pages, bool CanManage)
{
    public bool IsFree => Id == "free";
    public int Left => Math.Max(0, Cap - Used);
    public bool Weekly => Period == "week";
    public bool Ending => State == "cancelled";
    public bool PastDue => State == "past_due";
    public bool Gift => Source == "gift";
    /// <summary>The free trial (2026-10-28): the plan for a few days from the first question, ending at <see cref="EndsAt"/>.</summary>
    public bool IsTrial => Source == "trial";
    public RelayPayment? LastPayment { get; init; }

    /// <summary>Paid once a year (the relay's "billing", 2026-10-28; <see cref="Price"/> is then the year's price). False for monthly, gifts and an older relay.</summary>
    public bool Yearly { get; init; }

    /// <summary>
    /// Questions given from the admin site, kept until used: taken once the plan's day or the free week is used up
    /// (the person's rule, 2026-10-25). Not in <see cref="Cap"/> or <see cref="Left"/>. 0 from an older relay.
    /// </summary>
    public int Given { get; init; }
    /// <summary>What can be asked now: the plan's day or week left, and the given ones.</summary>
    public int AllLeft => Left + Given;
}

/// <summary>A plan to choose from: its price, questions, the pages it opens and the checkout link (carrying this PC's id).</summary>
public sealed record RelayPlanOffer(string Id, string Name, string Price, int PriceCents, int Cap, string Period, IReadOnlyList<string> Pages, string Url)
{
    /// <summary>Its answers check the web (the relay's "search", 2026-10-27: Essentials and Complete, not Free; an older relay gave it to every plan).</summary>
    public bool Search { get; init; } = true;
    public bool IsFree => Id == "free";
    public bool Weekly => Period == "week";

    /// <summary>The same plan bought for a year (2026-10-28), its own checkout; null when the relay has none for it.</summary>
    public RelayYearly? Yearly { get; init; }
}

/// <summary>A plan's yearly option: the year's price and its checkout link (carrying this PC's id).</summary>
public sealed record RelayYearly(string Price, int PriceCents, string Url)
{
    /// <summary>What it comes to a month, to the cent ($49.99 a year: $4.17).</summary>
    public int MonthCents => (int)Math.Round(PriceCents / 12m, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The free trial of a plan (the relay's "trial", 2026-10-28): offered (the first question starts it; <paramref name="Days"/>
/// long), running (until <paramref name="Ends"/>), or ended. <paramref name="Plan"/>: the plan it gives (complete).
/// </summary>
public sealed record RelayTrial(string State, string Plan, int Days, int Cap, DateTime? Started, DateTime? Ends)
{
    public bool Offered => State == "offered";
    public bool Running => State == "running";
    public bool Ended => State == "ended";
}

/// <summary>Questions given from the admin site (kept until used), with the message written to them: told once in the chat.</summary>
public sealed record RelayGift(long Id, int N, string? Note);

/// <summary>Why a question wasn't sent: the free week's or the plan's day used up, and when it starts again.</summary>
public sealed record AiLimit(string Kind, string? Plan, DateTime? ResetsAt);

public static partial class Relay
{
    /// <summary>Tests: the PC's clock as the relay is told it (null: the real one).</summary>
    internal static Func<DateTime>? Clock { get; set; }

    /// <summary>
    /// This PC's secret for the relay: 32 random bytes made once, kept in ai-secret.bin locked to this Windows account
    /// (DPAPI). The relay keeps the first one a PC sends and refuses calls without it, so someone who learned this PC's id
    /// can't ask on its plan, read its licence key, or cancel or change its plan.
    /// </summary>
    internal static string Secret => _secret ??= LoadSecret();
    private static string? _secret;

    private static string SecretFile => Path.Combine(RigsightPaths.DataDir, "ai-secret.bin");

    private static string LoadSecret()
    {
        try
        {
            if (File.Exists(SecretFile) && Encoding.ASCII.GetString(KeyStore.Unprotect(File.ReadAllBytes(SecretFile))) is { Length: 64 } saved) return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyStore.CryptographicException)
        {
            // A secret that can't be read (another account's copy, a damaged file) is made again; the relay then says the
            // sign-in changed, and support can let this PC in again.
            Log.Write("relay", $"The saved RigSight AI secret couldn't be read, so a new one was made: {ex.Message}");
        }
        string made = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        try
        {
            Directory.CreateDirectory(RigsightPaths.DataDir);
            File.WriteAllBytes(SecretFile, KeyStore.Protect(Encoding.ASCII.GetBytes(made)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyStore.CryptographicException)
        {
            Log.Write("relay", $"The RigSight AI secret couldn't be saved ({ex.Message}); this run uses it from memory");
        }
        return made;
    }

    private static RelayPlan? ReadPlan(JsonObject p)
    {
        if (Text(p["id"]) is not { Length: > 0 } id) return null;
        var card = p["card"] as JsonObject;
        return new RelayPlan(id, Text(p["name"]) ?? id, Text(p["source"]) ?? "free", Text(p["state"]) ?? "active",
            (int)Math.Clamp(OwnKey.Num(p["used"]) ?? 0, 0, int.MaxValue), (int)Math.Clamp(OwnKey.Num(p["cap"]) ?? 0, 0, int.MaxValue),
            Text(p["period"]) ?? "day", LocalTime(p["resets_at"]), Moment(p["renews_at"]), Moment(p["ends_at"]), Text(p["price_label"]) ?? "",
            card is not null && Text(card["last4"]) is { Length: > 0 } last4 ? new RelayCard(Text(card["brand"]) ?? "card", last4) : null,
            Pages(p["pages"]), (bool?)(p["can_manage"] as JsonValue) ?? false)
        {
            Given = (int)Math.Clamp(OwnKey.Num(p["given"]) ?? 0, 0, 1_000_000),
            Yearly = Text(p["billing"]) == "year",
            LastPayment = p["last_payment"] is JsonObject last && OwnKey.Num(last["amount_cents"]) is { } cents && Moment(last["at"]) is { } at
                ? new RelayPayment((int)Math.Clamp(cents, 0, int.MaxValue), Text(last["currency"]) ?? "USD", at) : null,
        };
    }

    private static RelayPlanOffer? ReadOffer(JsonObject p) =>
        Text(p["id"]) is not { Length: > 0 } id ? null
        : new RelayPlanOffer(id, Text(p["name"]) ?? id, Text(p["price_label"]) ?? "", (int)Math.Clamp(OwnKey.Num(p["price_cents"]) ?? 0, 0, int.MaxValue),
            (int)Math.Clamp(OwnKey.Num(p["cap"]) ?? 0, 0, int.MaxValue), Text(p["period"]) ?? "day", Pages(p["pages"]), Text(p["url"]) ?? "")
        {
            Search = (bool?)(p["search"] as JsonValue) ?? true,
            Yearly = p["yearly"] is JsonObject y && OwnKey.Num(y["price_cents"]) is { } cents && cents > 0 && Text(y["url"]) is { Length: > 0 } yUrl
                ? new RelayYearly(Text(y["price_label"]) ?? "", (int)Math.Min(cents, int.MaxValue), yUrl) : null,
        };

    private static IReadOnlyList<string> Pages(JsonNode? node) =>
        [.. (node as JsonArray ?? []).Select(Text).OfType<string>().Where(id => AiPages.Get(id) is not null)];

    private static RelayTrial? ReadTrial(JsonNode? node) =>
        node is JsonObject t && Text(t["state"]) is { Length: > 0 } state
            ? new RelayTrial(state, Text(t["plan"]) ?? "complete", (int)Math.Clamp(OwnKey.Num(t["days"]) ?? 0, 0, 365),
                (int)Math.Clamp(OwnKey.Num(t["cap"]) ?? 0, 0, int.MaxValue), Moment(t["started"]), Moment(t["ends_at"]))
            : null;

    private static RelayGift? ReadGift(JsonObject g) =>
        OwnKey.Num(g["id"]) is { } id && OwnKey.Num(g["n"]) is { } n && n > 0
            ? new RelayGift((long)id, (int)Math.Min(n, 100_000), Text(g["note"]) is { Length: > 0 } note ? note.Trim() : null)
            : null;

    /// <summary>A time the relay gives in the PC's own clock ("2026-10-24T00:00:00", no zone).</summary>
    private static DateTime? LocalTime(JsonNode? node) =>
        DateTime.TryParseExact(Text(node), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>A moment the relay gives with its zone (Lemon Squeezy's renewal dates), in the PC's time.</summary>
    private static DateTime? Moment(JsonNode? node) =>
        DateTimeOffset.TryParse(Text(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.LocalDateTime : null;

    internal static AiLimit? ReadLimit(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject e || Text(e["error"]) != "out_of_questions") return null;
            return new AiLimit(Text(e["reason"]) ?? "", Text(e["plan"]), LocalTime(e["resets_at"]));
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // ── Plan actions (cancel, keep, switch, card and receipts), all through the relay ──────────────
    public static Task<(RelayStatus? Status, string? Problem)> CancelPlanAsync(string? reason, CancellationToken ct = default) =>
        PlanAction("/v1/plan/cancel", new JsonObject { ["reason"] = reason }, "Cancelling the plan", ct);

    public static Task<(RelayStatus? Status, string? Problem)> KeepPlanAsync(CancellationToken ct = default) =>
        PlanAction("/v1/plan/resume", [], "Keeping the plan", ct);

    public static Task<(RelayStatus? Status, string? Problem)> SwitchPlanAsync(string plan, CancellationToken ct = default) =>
        PlanAction("/v1/plan/switch", new JsonObject { ["plan"] = plan }, $"Switching to {plan}", ct);

    /// <summary>The same plan paid monthly or yearly (2026-10-28): to yearly now, to monthly from the renewal.</summary>
    public static Task<(RelayStatus? Status, string? Problem)> SetBillingAsync(bool yearly, CancellationToken ct = default) =>
        PlanAction("/v1/plan/billing", new JsonObject { ["billing"] = yearly ? "year" : "month" }, yearly ? "Paying yearly" : "Paying monthly", ct);

    private static async Task<(RelayStatus?, string?)> PlanAction(string path, JsonObject body, string what, CancellationToken ct)
    {
        try
        {
            var req = Request(HttpMethod.Post, path);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct);
            string text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode && ReadStatus(text) is { } status)
            {
                Log.Write("relay", $"{what}: done ({status.Plan?.Name} {status.Plan?.State}, ends {status.Plan?.EndsAt:yyyy-MM-dd}, renews {status.Plan?.RenewsAt:yyyy-MM-dd})");
                return (status, null);
            }
            Log.Write("relay", $"{what}: expected the plan back, got HTTP {(int)res.StatusCode} {Short(text)}");
            return (null, res.IsSuccessStatusCode ? Lang.T("Your plan couldn't be changed right now. Nothing was charged. Try again in a minute.") : Problem(text, (int)res.StatusCode, inSettings: true));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException && !ct.IsCancellationRequested)
        {
            Log.Write("relay", $"{what} at {Root}: {ex.GetType().Name}: {ex.Message}");
            return (null, await Internet.ExplainsAsync(ex) ? Lang.T("Your PC isn't connected to the internet. Connect, then try again.")
                : Lang.T("Your plan couldn't be changed right now. Nothing was charged. Try again in a minute."));
        }
    }

    /// <summary>Lemon Squeezy's own page to change the card (card) or see receipts (receipts), made fresh by the relay.</summary>
    public static async Task<(string? Url, string? Problem)> BillingPageAsync(string which, CancellationToken ct = default)
    {
        try
        {
            using var res = await Http.SendAsync(Request(HttpMethod.Get, "/v1/plan/portal?for=" + which), ct);
            string text = await res.Content.ReadAsStringAsync(ct);
            string? url = null;
            try { url = Text(JsonNode.Parse(text)?["url"]); } catch (System.Text.Json.JsonException) { }
            if (res.IsSuccessStatusCode && url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) return (url, null);
            Log.Write("relay", $"Billing page ({which}): expected a link, got HTTP {(int)res.StatusCode} {Short(text)}");
            return (null, Lang.T("Lemon Squeezy's page couldn't be opened right now. Try again in a minute."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException && !ct.IsCancellationRequested)
        {
            Log.Write("relay", $"Billing page ({which}) at {Root}: {ex.GetType().Name}: {ex.Message}");
            return (null, Lang.T("Lemon Squeezy's page couldn't be opened right now. Try again in a minute."));
        }
    }

    /// <summary>
    /// The person pressed Stop: the relay gives the question back (it decides; past its daily allowance, or once the answer
    /// was already written out, it keeps it). Returns whether it did; false when it couldn't be told.
    /// </summary>
    public static async Task<bool> StopAsync(string questionId)
    {
        try
        {
            var req = Request(HttpMethod.Post, "/v1/stop");
            req.Content = new StringContent(new JsonObject { ["question_id"] = questionId }.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var res = await Http.SendAsync(req, cts.Token);
            string text = await res.Content.ReadAsStringAsync(cts.Token);
            bool back = res.IsSuccessStatusCode && (bool?)(JsonNode.Parse(text)?["refunded"] as JsonValue) == true;
            Log.Write("relay", $"Stopped question {questionId[..Math.Min(8, questionId.Length)]}: {(back ? "given back" : $"kept (HTTP {(int)res.StatusCode} {Short(text)})")}");
            return back;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or System.Text.Json.JsonException)
        {
            Log.Write("relay", $"Telling the relay a question was stopped: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Helpful or Not helpful under an answer, with why (wrong_numbers, didnt_answer, too_long, other) and their own words:
    /// kept by the relay with the question, for the admin site. A second one for the same question replaces the first.
    /// </summary>
    public static async Task<bool> FeedbackAsync(string questionId, bool helpful, string? reason, string? words)
    {
        try
        {
            var body = new JsonObject { ["question_id"] = questionId, ["helpful"] = helpful };
            if (!helpful && reason is { Length: > 0 }) body["reason"] = reason;
            if (!helpful && words?.Trim() is { Length: > 0 } w) body["words"] = w.Length > 500 ? w[..500] : w;
            var req = Request(HttpMethod.Post, "/v1/feedback");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var res = await Http.SendAsync(req, cts.Token);
            string text = await res.Content.ReadAsStringAsync(cts.Token);
            Log.Write("relay", $"Feedback on {questionId[..Math.Min(8, questionId.Length)]} ({(helpful ? "helpful" : $"not helpful, {reason ?? "no reason"}")}): {(res.IsSuccessStatusCode ? "kept" : $"HTTP {(int)res.StatusCode} {Short(text)}")}");
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            Log.Write("relay", $"Sending feedback: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

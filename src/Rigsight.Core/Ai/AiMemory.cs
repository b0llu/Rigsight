using System.Globalization;

namespace Rigsight.Core.Ai;

/// <summary>
/// A long chat's memory for RigSight AI (2026-10-26, the approved mock-up "Long chats and big pastes"): once a chat is
/// too long to send whole, its older part is summarised by the relay and the summary goes with the latest messages,
/// instead of the middle being left out unsaid. The person's chat on screen never changes; only what is sent does.
/// </summary>
public static partial class AiMemory
{
    /// <summary>The latest messages always sent as they were written (two questions and their answers).</summary>
    public const int KeepRecent = 4;

    /// <summary>
    /// The latest messages may hold two pastes of up to 100,000 characters each; past this many characters together,
    /// only the latest question and its answer are kept as written, so a request still fits the relay's 280,000.
    /// </summary>
    public const int BigRecent = 120_000;

    /// <summary>How many of the latest messages stay as they were written: <see cref="KeepRecent"/>, or 2 when those are huge.</summary>
    public static int Keep(IReadOnlyList<AiTurn> sent) =>
        sent.TakeLast(KeepRecent).Sum(t => AiTurns.Message(t)["content"]!.ToString().Length) > BigRecent ? 2 : KeepRecent;

    /// <summary>
    /// The size guard for one request (the person's call, 2026-10-27), under the 100,000-token line past which every token
    /// of the request costs five times as much (a year-long comparison sent 110,000 and that one step cost 7¢). The relay
    /// has the same guard (its fit.ts: an app can be changed) and allows about 180,000 characters of conversation; this
    /// keeps within it, so what's cut is cut here, where the log says so. Nothing is ever refused for it: <see cref="Fit"/>
    /// shortens the oldest material and says so in place, and the model can look again for a shorter stretch.
    /// </summary>
    public const int SafeRequest = 180_000;

    /// <summary>
    /// The messages of one request made to fit <paramref name="limit"/> characters (with <paramref name="otherChars"/> for
    /// the rest of the request): only when two huge pastes and big look-ups together would pass it. The longest messages
    /// before the question being asked are shortened first, each to its start and a note of how much was left out; the
    /// question itself is never touched, and this answer's own look-ups only when that isn't enough. Returns how many
    /// were shortened.
    /// </summary>
    public static int Fit(System.Text.Json.Nodes.JsonArray messages, int questionAt, int otherChars, int limit = SafeRequest)
    {
        int Size() => messages.ToJsonString().Length + otherChars;
        int cut = 0;
        while (Size() > limit)
        {
            var longest = messages.Take(questionAt).OfType<System.Text.Json.Nodes.JsonObject>()
                .Where(m => m["content"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? t) && t.Length > ShortenedTo + 200)
                .OrderByDescending(m => m["content"]!.ToString().Length).FirstOrDefault();
            if (longest is null) break;
            string text = longest["content"]!.ToString();
            longest["content"] = text[..ShortenedTo] + $"\n… ({text.Length - ShortenedTo:N0} more characters of this message left out here, so the question fits)";
            cut++;
        }
        // Still too big: this answer's own look-ups, oldest first, never the latest round's (2026-10-27: "this month against
        // every month this year" made 19 look-ups and its fifth round was refused for size). A last resort, said in place,
        // so the model can look again for a shorter stretch; the relay sends such a look-up again in full, never "read it there".
        int lastAsked = messages.Select((m, i) => (m, i)).LastOrDefault(x => x.m?["role"]?.ToString() == "assistant").i;
        foreach (var m in messages.Skip(questionAt + 1).Take(Math.Max(0, lastAsked - questionAt - 1)).OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (Size() <= limit) break;
            if (m["role"]?.ToString() != "tool" || m["content"] is not System.Text.Json.Nodes.JsonValue v || !v.TryGetValue(out string? text) || text.Length <= ShortenedTo + 200) continue;
            m["content"] = text[..ShortenedTo] + $"\n… ({text.Length - ShortenedTo:N0} more characters of this result {LookUpCut}; look it up again for a shorter stretch if you need them)";
            cut++;
        }
        return cut;
    }

    /// <summary>The words that mark a look-up result shortened by <see cref="Fit"/>; the relay knows them.</summary>
    public const string LookUpCut = "left out here, so the request fits";

    /// <summary>How much of a shortened message is kept: its start, where a pasted log or report says what it is.</summary>
    public const int ShortenedTo = 4000;

    /// <summary>
    /// How long the unsummarised part may get, in characters, before it is summarised: about 15,000 tokens, a quarter of
    /// a round's room. RIGSIGHT_COMPACT_AT changes it (a test copy shows a summary after a short chat with it).
    /// </summary>
    public static int CompactAt =>
        int.TryParse(Environment.GetEnvironmentVariable("RIGSIGHT_COMPACT_AT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int at) && at > 0 ? at : 60_000;

    /// <summary>
    /// A summary as kept: without the answers' own marks the model may have carried over ("[src: crashes]", "[do: …]",
    /// "[w2]") or their "Ask next:" and "Title:" lines, which mean something only under an answer (the first live summary,
    /// 2026-10-26, kept them).
    /// </summary>
    public static string Clean(string summary)
    {
        var lines = summary.Replace("\r", "").Split('\n')
            .Select(l => Marks().Replace(l, "").TrimEnd())
            .Select(l => SpaceBeforeStop().Replace(l, "$1"))
            .Where(l => l.Trim().Length > 0 && !l.TrimStart('-', ' ', '*', '•').StartsWith("Ask next:", StringComparison.OrdinalIgnoreCase)
                && !l.TrimStart('-', ' ', '*', '•').StartsWith("Title:", StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", lines).Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\[(?:(?:src|do|show|locked)\s*:[^\]]*|w\d+(?:\s+w\d+)*)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Marks();
    [System.Text.RegularExpressions.GeneratedRegex(@"\s+([.,;:])")]
    private static partial System.Text.RegularExpressions.Regex SpaceBeforeStop();

    /// <summary>The words that put the summary in front of the first message kept, so the chat still goes question, answer.</summary>
    public const string Lead = "(What was said earlier in this conversation, summarised by RigSight:)";

    /// <summary>
    /// Whether the chat needs (a new) summary after its latest answer: more messages since the last one than are ever
    /// sent whole (<see cref="AiTurns.MaxSent"/>), or more characters than <see cref="CompactAt"/>, with something older
    /// than the latest <see cref="KeepRecent"/> to summarise.
    /// </summary>
    public static bool Needs(IReadOnlyList<AiTurn> sent, int covered, string? summary)
    {
        var open = sent.Skip(covered).ToList();
        if (open.Count <= Keep(sent)) return false;
        int chars = (summary?.Length ?? 0) + open.Sum(t => AiTurns.Message(t)["content"]!.ToString().Length);
        return open.Count > AiTurns.MaxSent || chars > CompactAt;
    }

    /// <summary>
    /// What goes with the next question: the whole chat while it has no summary (cut as always, <see cref="AiTurns.Recent"/>),
    /// else the messages after the summary with the summary in front of the first of them.
    /// </summary>
    public static IReadOnlyList<AiTurn> History(IReadOnlyList<AiTurn> sent, int covered, string? summary)
    {
        if (summary is not { Length: > 0 } || covered <= 0 || covered > sent.Count) return sent;
        var kept = sent.Skip(covered).ToList();
        if (kept.Count == 0 || !kept[0].FromUser) return sent;
        kept[0] = kept[0] with { Text = $"{Lead}\n{summary}\n\n{kept[0].Text}" };
        return kept;
    }
}

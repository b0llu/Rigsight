using System.Text;
using System.Text.Json.Nodes;

namespace Rigsight.Core.Ai;

public static partial class Relay
{
    /// <summary>
    /// The summary of <paramref name="turns"/> (with the summary they follow, if any), from the relay: not a question,
    /// nothing is taken from the person's questions. Null when it couldn't be made; the chat is then sent as before.
    /// </summary>
    public static async Task<string?> SummariseAsync(IReadOnlyList<AiTurn> turns, string? earlier, CancellationToken ct = default)
    {
        var messages = new JsonArray();
        if (earlier is { Length: > 0 }) messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"{AiMemory.Lead}\n{earlier}" });
        foreach (var t in turns) messages.Add(AiTurns.Message(t));
        int chars = messages.Sum(m => m!["content"]!.ToString().Length);
        try
        {
            var req = Request(HttpMethod.Post, "/v1/summary");
            req.Content = new StringContent(new JsonObject { ["messages"] = messages }.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var res = await Http.SendAsync(req, cts.Token);
            string text = await res.Content.ReadAsStringAsync(cts.Token);
            string? summary = res.IsSuccessStatusCode ? (string?)JsonNode.Parse(text)?["summary"] : null;
            Log.Write("relay", summary is { Length: > 0 }
                ? $"Summarised {turns.Count} messages ({chars:N0} chars{(earlier is null ? "" : ", with the summary before")}) into {summary.Length:N0} chars"
                : $"Summary not made for {turns.Count} messages ({chars:N0} chars): HTTP {(int)res.StatusCode} {text[..Math.Min(200, text.Length)]}");
            return summary is { Length: > 0 } && AiMemory.Clean(summary) is { Length: > 0 } clean ? clean : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or System.Text.Json.JsonException)
        {
            Log.Write("relay", $"Summary not made for {turns.Count} messages ({chars:N0} chars): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

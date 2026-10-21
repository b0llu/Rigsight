using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rigsight.Core.Ai;

/// <summary>
/// Web search for the helper (Parallel Search): normal temperatures for a part, the newest driver, known game problems.
/// It comes with RigSight AI, whose relay will search on RigSight's side; own key and Ollama don't get it (the user's
/// ruling, 2026-10-19). Until the relay is built, only a test copy searches, with a key in search-key.txt in its data
/// folder, so the model test runs with search the way RigSight AI will.
/// </summary>
public static class WebSearch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The test copy's search key; null everywhere else.</summary>
    public static string? TestKey
    {
        get
        {
            if (!RigsightPaths.IsTestInstance) return null;
            var file = Path.Combine(RigsightPaths.DataDir, "search-key.txt");
            try { return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } key ? key : null; }
            catch (IOException) { return null; }
        }
    }

    public static JsonObject Definition() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "search_web",
            ["description"] = "Search the web for facts about PC parts and software: normal temperatures for a CPU or graphics card, known driver or game problems, fixes. Never put personal details in the query.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("query"),
            },
        },
    };

    /// <summary>The top results with their excerpts, as the model reads them; a failure says so in one line.</summary>
    public static async Task<string> SearchAsync(string key, string query, CancellationToken ct = default)
    {
        if (query.Trim().Length == 0) return "Error: the search was empty.";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.parallel.ai/v1/search")
            {
                Content = JsonContent.Create(new { objective = query, search_queries = new[] { query }, mode = "fast" }),
            };
            req.Headers.Add("x-api-key", key);
            using var res = await Http.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                Log.Write("search", $"Searching \"{query}\": expected results, got HTTP {(int)res.StatusCode}");
                return $"Error: web search failed ({(int)res.StatusCode}).";
            }
            var sb = new StringBuilder();
            foreach (var r in (JsonNode.Parse(body)?["results"] as JsonArray ?? []).Take(5))
            {
                sb.AppendLine($"{r?["title"]} ({r?["url"]})");
                string excerpts = string.Join(" … ", (r?["excerpts"] as JsonArray ?? []).Select(e => (string?)e));
                sb.AppendLine("  " + (excerpts.Length > 1200 ? excerpts[..1200] + "…" : excerpts));
            }
            Log.Write("search", $"Searched \"{query}\": {sb.Length} chars of results");
            return sb.Length == 0 ? "No results." : sb.ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Write("search", $"Searching \"{query}\" failed: {ex.GetType().Name}: {ex.Message}");
            return "Error: web search isn't answering right now.";
        }
    }
}

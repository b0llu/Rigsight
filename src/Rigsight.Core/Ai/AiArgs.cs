using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rigsight.Core.Ai;

/// <summary>
/// A look-up's argument as text, whatever the model sent: text as it is, a number or true/false as written, anything else
/// (a list, an object) none. A cast to string throws on a number or a list, and nothing caught it, so the question stopped
/// after the model's first round and the relay gave it back 15 minutes later (the likeliest cause of a get_dashboard
/// question that never got its second round, 2026-10-31).
/// </summary>
public static class AiArgs
{
    public static string? Text(JsonNode? node) => node is not JsonValue v ? null
        : v.TryGetValue(out string? s) ? s
        : v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.ToJsonString()
        : null;
}

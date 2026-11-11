using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rigsight.Core.Settings;

/// <summary>
/// One change made on a newer copy, value by value (a three-way merge of the JSON): the app's change not yet sent made
/// on the agent's copy that came meanwhile, and an Undo in the widget editor made on the widget as it's saved now. What
/// the other side changed meanwhile stays.
/// </summary>
public static class SettingsMerge
{
    /// <summary>
    /// <paramref name="now"/> with the change from <paramref name="from"/> to <paramref name="to"/> made on it: each value
    /// the change set is <paramref name="to"/>'s, every other stays <paramref name="now"/>'s (where both changed the same
    /// value, the change wins). Lists of things with an identifier (widgets, dashboards, readings) go by it: one the change
    /// added or took away is added or taken away, one added on the other side stays. Other lists of the same length go
    /// item by item; one the change made longer or shorter is taken whole.
    /// </summary>
    public static JsonNode? Merge(JsonNode? from, JsonNode? to, JsonNode? now)
    {
        if (JsonNode.DeepEquals(from, to)) return now?.DeepClone();
        if (JsonNode.DeepEquals(from, now) || JsonNode.DeepEquals(to, now)) return to?.DeepClone();
        if (from is JsonObject f && to is JsonObject t && now is JsonObject n)
        {
            var merged = new JsonObject();
            foreach (string key in t.Select(p => p.Key).Union(n.Select(p => p.Key)))
                if (t.ContainsKey(key) || f.ContainsKey(key) == false)
                    merged[key] = Merge(f[key], t[key], n[key]);
            return merged;
        }
        if (from is JsonArray fa && to is JsonArray ta && now is JsonArray na)
        {
            if (ById(fa) is { } fi && ById(ta) is { } ti && ById(na) is { } ni)
            {
                var merged = new List<JsonNode?>();
                foreach (var (id, item) in ti)
                {
                    if (!fi.TryGetValue(id, out var was)) merged.Add(item.DeepClone()); // added by the change
                    else if (ni.TryGetValue(id, out var there)) merged.Add(Merge(was, item, there));
                    else if (!JsonNode.DeepEquals(was, item)) merged.Add(item.DeepClone()); // taken away there, changed here: kept
                }
                // Added on the other side: where it is there.
                int i = 0;
                foreach (var (id, item) in ni)
                {
                    if (!fi.ContainsKey(id) && !ti.ContainsKey(id)) merged.Insert(Math.Min(i, merged.Count), item.DeepClone());
                    i++;
                }
                return new JsonArray([.. merged]);
            }
            if (fa.Count == ta.Count && fa.Count == na.Count)
                return new JsonArray([.. ta.Select((_, k) => Merge(fa[k], ta[k], na[k]))]);
        }
        return to?.DeepClone();
    }

    /// <summary>A list's items by their "Id", in order (an empty list too); null when it isn't a list of things each with an identifier of its own.</summary>
    private static List<(string Id, JsonNode Item)>? ById(JsonArray list)
    {
        var items = new List<(string, JsonNode)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in list)
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue("Id", out var idNode) || idNode is not JsonValue v
                || !v.TryGetValue(out string? id) || string.IsNullOrEmpty(id) || !seen.Add(id)) return null;
            items.Add((id, o));
        }
        return items;
    }

    private static bool TryGetValue(this List<(string Id, JsonNode Item)> list, string id, out JsonNode item)
    {
        foreach (var (key, value) in list)
            if (key == id) { item = value; return true; }
        item = null!;
        return false;
    }

    private static bool ContainsKey(this List<(string Id, JsonNode Item)> list, string id) => list.Any(x => x.Id == id);

    /// <summary>
    /// <paramref name="now"/> with the change from <paramref name="from"/> to <paramref name="to"/> made on it (see
    /// <see cref="Merge(JsonNode?, JsonNode?, JsonNode?)"/>), read back as settings without repairs: the change may be half
    /// typed (a name with a space at its end), and the copy that's sent is repaired anyway.
    /// </summary>
    /// <remarks>Each copy as <see cref="SettingsStore.Serialize"/> writes it.</remarks>
    public static RigsightSettings Merge(string from, string to, string now) =>
        Merge(JsonNode.Parse(from), JsonNode.Parse(to), JsonNode.Parse(now))?.Deserialize(SettingsJson.Default.RigsightSettings) ?? SettingsStore.Deserialize(now);
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rigsight.Core.Ai;

/// <summary>
/// One saved message: a question, an answer (the model's own text, the records it looked at, and RigSight's numbers as
/// they were drawn then, by marker, so an old answer shows what it showed), or a line saying no answer came.
/// </summary>
public sealed record SavedMessage(string Kind, string Text, string? Model = null, IReadOnlyList<SavedLookUp>? LookUps = null,
    IReadOnlyDictionary<string, IReadOnlyList<AiBlock>>? Drawn = null)
{
    public const string Question = "question", Answer = "answer", Problem = "problem", Stopped = "stopped";
    /// <summary>What a button under an answer did ("Closed Python at 10:42 PM"), and a Check again ("1729615200 10 Fans").</summary>
    public const string Done = "done", Check = "check";
    /// <summary>The summary of a long chat's older part, where it starts.</summary>
    public const string Summary = "summary";
}

public sealed record SavedLookUp(string Tool, string Args, int Chars, IReadOnlyList<WebSource>? Web = null)
{
    public AiLookUp ToLookUp() => new(Tool, JsonNode.Parse(Args) as JsonObject ?? [], Chars, Web);
    public static SavedLookUp From(AiLookUp l) => new(l.Tool, l.Args.ToJsonString(), l.Chars, l.Web);
}

/// <summary>A conversation as kept between runs: its title, when it started, what it was opened from, pinned or archived.</summary>
public sealed record SavedConversation(string Id, string Title, DateTime When, string? LookingAt, bool Pinned, bool Archived,
    IReadOnlyList<SavedMessage> Messages);

/// <summary>
/// Ask RigSight's conversations, kept in conversations.json beside the settings: per Windows account, on this PC only
/// (nothing here is sent anywhere). Written whole each time, through a temporary file, so a power cut leaves the old
/// file or the new one, never half of one.
/// </summary>
public static class AiConversations
{
    private sealed record FileShape(int V, IReadOnlyList<SavedConversation> Conversations);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    private static readonly object WriteLock = new();

    public static string File => Path.Combine(RigsightPaths.DataDir, "conversations.json");

    public static List<SavedConversation> Load(string? path = null)
    {
        path ??= File;
        try
        {
            if (!System.IO.File.Exists(path)) return [];
            var shape = JsonSerializer.Deserialize<FileShape>(System.IO.File.ReadAllText(path), Options);
            return [.. shape?.Conversations ?? []];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable: kept aside (not overwritten by the next save) and started again, said in the log.
            Log.Write("ask", $"Conversations couldn't be read ({ex.Message}); kept as {Path.GetFileName(path)}.bad, starting with none");
            try { System.IO.File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
            return [];
        }
    }

    public static void Save(IReadOnlyList<SavedConversation> conversations, string? path = null)
    {
        path ??= File;
        string json = JsonSerializer.Serialize(new FileShape(1, conversations), Options);
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".tmp";
                System.IO.File.WriteAllText(temp, json);
                System.IO.File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write("ask", $"Conversations couldn't be saved: {ex.Message}");
            }
        }
    }
}

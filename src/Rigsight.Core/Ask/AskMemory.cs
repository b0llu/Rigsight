using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rigsight.Core.Ask;

/// <summary>
/// What Ask has picked up from this user, kept in a file on this PC and nowhere else: the wordings they taught it
/// (see <see cref="AskRouter.Learn"/>), and the questions it had no answer for, so they can be seen and added one day.
/// </summary>
public sealed class AskMemory
{
    public List<AskLearned> Learned { get; set; } = [];

    /// <summary>Questions nothing could be made of, newest last (the same one once).</summary>
    public List<string> Unanswered { get; set; } = [];

    /// <summary>Names the user gave apps ("vsc" for Visual Studio Code): what they typed, lower case, to the app's exe.</summary>
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const int MaxUnanswered = 100;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string File => Path.Combine(RigsightPaths.DataDir, "ask.json");

    public static AskMemory Load(string? path = null)
    {
        try
        {
            path ??= File;
            if (System.IO.File.Exists(path)) return JsonSerializer.Deserialize<AskMemory>(System.IO.File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("ask", ex);
        }
        return new();
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= File;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Error("ask", ex);
        }
    }

    public void AddUnanswered(string question)
    {
        question = question.Trim();
        if (question.Length is < 3 or > 200) return;
        Unanswered.RemoveAll(q => string.Equals(q, question, StringComparison.OrdinalIgnoreCase));
        Unanswered.Add(question);
        if (Unanswered.Count > MaxUnanswered) Unanswered.RemoveRange(0, Unanswered.Count - MaxUnanswered);
    }
}

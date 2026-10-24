using System.Text.Json;

namespace Rigsight.Core.Ai;

/// <summary>
/// Which new-chat starters were shown, on how many days, and when one was last picked, by topic (the question with its
/// figures taken out, so "Why did my GPU fan stop for 12 min?" and "…for 40 min?" are one). Kept on this PC only, beside
/// the chats; a question shown day after day and never picked gives way to another, one just asked rests a few days.
/// </summary>
public sealed class AiStarterLog
{
    public sealed record Seen(int Days, DateTime LastShown, DateTime? Picked);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _lock = new();
    private readonly string _path;
    private Dictionary<string, Seen>? _topics;

    public AiStarterLog(string? path = null) => _path = path ?? Path.Combine(RigsightPaths.DataDir, "ask-starters.json");

    /// <summary>"Why did my GPU fan stop for #?": the topic a question is about, the same whatever its figures.</summary>
    public static string TopicOf(string question) =>
        System.Text.RegularExpressions.Regex.Replace(question.Trim().ToLowerInvariant(), @"\d[\d.,:]*", "#");

    public Seen? Get(string topic)
    {
        lock (_lock) return Topics().GetValueOrDefault(topic);
    }

    /// <summary>These were on screen: one more day each, counted once a day however often a new chat opens.</summary>
    public void Shown(IEnumerable<string> topics, DateTime now)
    {
        lock (_lock)
        {
            var all = Topics();
            bool changed = false;
            foreach (var t in topics)
            {
                var was = all.GetValueOrDefault(t);
                if (was is not null && was.LastShown.Date == now.Date) continue;
                all[t] = new Seen((was?.Days ?? 0) + 1, now, was?.Picked);
                changed = true;
            }
            if (changed) Save(all, now);
        }
    }

    /// <summary>Picked: its count starts again, and it rests a few days.</summary>
    public void Picked(string topic, DateTime now)
    {
        lock (_lock)
        {
            var all = Topics();
            all[topic] = new Seen(0, now, now);
            Save(all, now);
        }
    }

    private Dictionary<string, Seen> Topics()
    {
        if (_topics is not null) return _topics;
        try
        {
            _topics = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Seen>>(File.ReadAllText(_path), Options) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Write("ask", $"The starters' record couldn't be read ({ex.Message}); starting it again");
            _topics = [];
        }
        return _topics;
    }

    private void Save(Dictionary<string, Seen> all, DateTime now)
    {
        // Topics not shown for a month are dropped: the file stays a few lines.
        foreach (var old in all.Where(p => now - p.Value.LastShown > TimeSpan.FromDays(30)).Select(p => p.Key).ToList()) all.Remove(old);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(all, Options));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("ask", $"The starters' record couldn't be saved: {ex.Message}"); }
    }
}

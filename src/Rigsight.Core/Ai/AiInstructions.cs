using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ai;

/// <summary>
/// The person's own instructions for Ollama or their own key, changed in Settings › Ask RigSight. One file each beside the
/// settings (ai-instructions-ollama.json, ai-instructions-own-key.json), holding the text and which version of RigSight's
/// own text it was changed from, so after an update that improves RigSight's text Settings can say so and offer Reset.
/// No file means RigSight's own text. RigSight AI has none: its prompt lives on the relay.
/// </summary>
public static class AiInstructions
{
    public static string Default(AiMode mode) => mode switch
    {
        AiMode.Ollama => AiPrompt.OllamaInstructions,
        AiMode.OwnKey => AiPrompt.OwnKeyInstructions,
        _ => throw new ArgumentException($"{mode} has no instructions to change", nameof(mode)),
    };

    /// <summary>Their own text, or null when they use RigSight's (or the file can't be read).</summary>
    public static string? Custom(AiMode mode) => Read(mode)?.Text;

    /// <summary>What Settings shows in the box: their text, or RigSight's.</summary>
    public static string Current(AiMode mode) => Custom(mode) ?? Default(mode);

    /// <summary>True when they changed the text and RigSight's own text has changed since.</summary>
    public static bool DefaultChangedSince(AiMode mode) => Read(mode) is { } saved && saved.BasedOn != Version(Default(mode));

    /// <summary>When they saved their own text; null when they use RigSight's.</summary>
    public static DateTime? SavedOn(AiMode mode) => Read(mode)?.SavedOn;

    /// <summary>"Keep mine" after RigSight's text changed: their text stays, and the note doesn't come back for this version.</summary>
    public static void Keep(AiMode mode)
    {
        if (Read(mode) is not { } saved) return;
        Write(mode, saved with { BasedOn = Version(Default(mode)) });
        Log.Write("ai-instructions", $"Own instructions for {mode} kept over RigSight's new ones");
    }

    /// <summary>Saves their text. Saving RigSight's text unchanged, or nothing, is the same as Reset.</summary>
    public static void Save(AiMode mode, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Same(text, Default(mode))) { Reset(mode); return; }
        Write(mode, new Saved(text.Trim(), Version(Default(mode)), DateTime.Now));
        Log.Write("ai-instructions", $"Own instructions saved for {mode} ({text.Trim().Length} chars)");
    }

    public static void Reset(AiMode mode)
    {
        if (!File.Exists(FileOf(mode))) return;
        File.Delete(FileOf(mode));
        Log.Write("ai-instructions", $"{mode} back to RigSight's instructions");
    }

    private sealed record Saved(string Text, string BasedOn, DateTime SavedOn);

    private static void Write(AiMode mode, Saved saved)
    {
        Directory.CreateDirectory(RigsightPaths.DataDir);
        File.WriteAllText(FileOf(mode), JsonSerializer.Serialize(saved));
    }

    private static Saved? Read(AiMode mode)
    {
        try
        {
            if (!File.Exists(FileOf(mode))) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FileOf(mode)));
            return string.IsNullOrWhiteSpace(saved?.Text) ? null : saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("ai-instructions", $"Own instructions for {mode} couldn't be read, using RigSight's: {ex.Message}");
            return null;
        }
    }

    private static string FileOf(AiMode mode) =>
        Path.Combine(RigsightPaths.DataDir, mode == AiMode.Ollama ? "ai-instructions-ollama.json" : "ai-instructions-own-key.json");

    private static bool Same(string a, string b) => Normal(a) == Normal(b);

    private static string Normal(string s) => s.Replace("\r\n", "\n").Trim();

    private static string Version(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normal(text))))[..12];
}

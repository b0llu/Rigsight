namespace Rigsight.Core.Ai;

/// <summary>
/// Until RigSight AI launches, the whole AI helper (RigSight AI, own key, Ollama) is hidden: no Ask button, no Explain
/// buttons, no Settings tab, and nothing ever reaches the relay, Ollama or a service. A PC given the preview has this
/// file in its data folder, made by running RigSight once with <c>--ai-preview on</c> (removed with <c>--ai-preview off</c>).
/// The relay is what really keeps the preview closed (no free questions for new PCs until launch): this only hides it.
/// A test copy has it on (its tests and the AI test copy use the helper), unless RIGSIGHT_AI_PREVIEW=0.
/// </summary>
public static class AiPreview
{
    private static string FilePath => Path.Combine(RigsightPaths.DataDir, "ai-preview.on");

    /// <summary>Read once when the app starts; switching needs a restart.</summary>
    public static bool IsOn => _isOn ??= Decide(RigsightPaths.IsTestInstance, File.Exists(FilePath), Environment.GetEnvironmentVariable("RIGSIGHT_AI_PREVIEW"));
    private static bool? _isOn;

    internal static bool Decide(bool testInstance, bool fileThere, string? env) =>
        env?.Trim() is "0" ? false : env?.Trim() is "1" || fileThere || testInstance;

    /// <summary>The command line's word: true for on, false for off, null for anything else.</summary>
    public static bool? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "yes" => true,
        "off" or "0" or "false" or "no" => false,
        _ => null,
    };

    /// <summary>Makes or removes the file. Throws IOException or UnauthorizedAccessException when it can't.</summary>
    public static void Set(bool on)
    {
        if (on)
        {
            Directory.CreateDirectory(RigsightPaths.DataDir);
            File.WriteAllText(FilePath, $"RigSight AI preview turned on {DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}");
        }
        else if (File.Exists(FilePath)) File.Delete(FilePath);
        Log.Write("ai-preview", on ? $"AI preview turned on for this PC ({FilePath})" : "AI preview turned off for this PC");
    }
}

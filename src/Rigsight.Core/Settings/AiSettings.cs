namespace Rigsight.Core.Settings;

/// <summary>How the AI helper answers: not at all, through RigSight's own questions, the person's own key, or Ollama.</summary>
public enum AiMode { Off, RigSight, OwnKey, Ollama }

/// <summary>Where the Ask button sits: the top bar beside the window's buttons, or the sidebar.</summary>
public enum AskPlace { TopBar, Sidebar }

/// <summary>
/// The AI helper (Settings › Ask RigSight): how it answers, what it may send, where its button is and how long conversations
/// stay. On from the start with RigSight AI and its free questions to try (the user's word, 2026-10-20); Off hides
/// every Ask and Explain button.
/// </summary>
public sealed class AiSettings
{
    public AiMode Mode { get; set; } = AiMode.RigSight;
    public AskPlace Place { get; set; } = AskPlace.TopBar;

    /// <summary>The keys that open the Ask window, as WPF writes them ("Ctrl+Shift+A"); empty while none are set.</summary>
    public string Shortcut { get; set; } = "";

    /// <summary>How long conversations are kept, in days; 0 keeps them until they are cleared (like KeepHistoryDays).</summary>
    public int KeepConversationDays { get; set; } = 30;

    /// <summary>The warning about what the helper sends was closed with its close icon: Settings › Ask RigSight no longer opens with it.</summary>
    public bool SendingNoteClosed { get; set; }

    /// <summary>The box about web search (on your own key or Ollama) was closed.</summary>
    public bool WebSearchNoteClosed { get; set; }

    // Your own key: the service, and the model picked from the ones it lists for that key. The key itself is kept
    // apart from settings, protected by Windows (never in settings.json).
    public string Service { get; set; } = "OpenRouter";
    public string Model { get; set; } = "";
    /// <summary>The address of a service that speaks OpenAI's API, for "Custom".</summary>
    public string CustomAddress { get; set; } = "";

    // Ollama on my PC.
    public string OllamaAddress { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "";

    // What it may send: everything by default, for the best answers.
    public bool ShareCrashes { get; set; } = true;
    public bool ShareHeat { get; set; } = true;
    public bool ShareApps { get; set; } = true;
    public bool ShareChanges { get; set; } = true;
    public bool ShareMemory { get; set; } = true;
    public bool ShareNetwork { get; set; } = true;
    public bool ShareParts { get; set; } = true;
}

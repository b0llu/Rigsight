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

    /// <summary>The ways of answering whose first-question window (what is sent, and where) has been read and closed with Start asking.</summary>
    public List<AiMode> FirstQuestionSeen { get; set; } = [];

    // Your own key: the service, and the model picked from the ones it lists for that key. The key itself is kept
    // apart from settings, protected by Windows (never in settings.json).
    public string Service { get; set; } = "OpenRouter";
    public string Model { get; set; } = "";
    /// <summary>The address of a service that speaks OpenAI's API, for "Custom".</summary>
    public string CustomAddress { get; set; } = "";

    // Ollama on my PC.
    public string OllamaAddress { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "";

    /// <summary>How answers should be (length, tone, language, about them), for Ollama and for their own key apart.</summary>
    public AiPreferences OllamaPreferences { get; set; } = new();
    public AiPreferences OwnKeyPreferences { get; set; } = new();
    public AiPreferences PreferencesFor(AiMode mode) => mode == AiMode.Ollama ? OllamaPreferences : OwnKeyPreferences;

    /// <summary>
    /// The pages (AiPages ids) answers may not use, switched off in Settings › AI; null until it was first set, when the old
    /// switches by kind of record below still say what's off (AiPages.FromOldSwitches).
    /// </summary>
    public List<string>? PagesOff { get; set; }

    /// <summary>Whether answers may use this page's records (every page is on by default, for the best answers).</summary>
    public bool Sends(string page) => !(PagesOff ?? [.. Ai.AiPages.FromOldSwitches(this)]).Contains(page);

    public void SetSends(string page, bool on)
    {
        var off = new HashSet<string>(PagesOff ?? [.. Ai.AiPages.FromOldSwitches(this)]);
        if (on) off.Remove(page); else off.Add(page);
        PagesOff = [.. Ai.AiPages.All.Select(p => p.Id).Where(off.Contains)];
    }

    // What it sent before pages (until 2026-10-23), by kind of record: read once into PagesOff, then no longer used.
    public bool ShareCrashes { get; set; } = true;
    public bool ShareHeat { get; set; } = true;
    public bool ShareApps { get; set; } = true;
    public bool ShareChanges { get; set; } = true;
    public bool ShareMemory { get; set; } = true;
    public bool ShareNetwork { get; set; } = true;
    public bool ShareParts { get; set; } = true;
    /// <summary>When the PC was on and someone was using it (the report's times, get_on_times).</summary>
    public bool ShareUsage { get; set; } = true;
}

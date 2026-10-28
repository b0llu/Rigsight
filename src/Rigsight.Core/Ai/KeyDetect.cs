using System.Text.RegularExpressions;

namespace Rigsight.Core.Ai;

/// <summary>
/// What was pasted into the one box of Settings › Ask RigSight (the user's "just paste" choice, 2026-10-27): a server's
/// address, or a key whose start says which service made it. Checked against the services' published formats
/// (2026-10-27): OpenRouter "sk-or-v1-", Anthropic "sk-ant-", OpenAI "sk-proj-", "sk-svcacct-", "sk-admin-" or an old
/// "sk-" key, every one carrying "T3BlbkFJ"; Google "AIza"; xAI "xai-"; Groq "gsk_". DeepSeek's keys are a plain "sk-"
/// with no marker and Mistral's have no known start, so those are never guessed: the person picks, and the key goes
/// only to the service they pick.
/// </summary>
public static partial class KeyDetect
{
    /// <summary>An address rather than a key: http:// or https://, or a host with a port ("localhost:1234", "192.168.1.20:8000").</summary>
    public static bool IsAddress(string text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Contains(' ')) return false;
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        return HostPort().IsMatch(t);
    }

    /// <summary>
    /// The service a key's start points to, by the names <see cref="OwnKey.Root"/> uses; null for an address or a key
    /// nobody's start matches. Any "sk-ant-" is Anthropic, not only "sk-ant-api03-": a real key began "sk-ant-usr-".
    /// </summary>
    public static string? Service(string text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || IsAddress(t)) return null;
        if (t.StartsWith("sk-or-", StringComparison.Ordinal)) return "OpenRouter";
        if (t.StartsWith("sk-ant-", StringComparison.Ordinal)) return "Anthropic";
        if (t.StartsWith("AIza", StringComparison.Ordinal)) return "Google Gemini";
        if (t.StartsWith("xai-", StringComparison.Ordinal)) return "xAI";
        if (t.StartsWith("gsk_", StringComparison.Ordinal)) return "Groq";
        if (IsOpenAi(t)) return "OpenAI";
        return null;
    }

    /// <summary>OpenAI's own key kinds, or any "sk-" key with the marker every OpenAI key carries.</summary>
    private static bool IsOpenAi(string t) =>
        t.StartsWith("sk-proj-", StringComparison.Ordinal) || t.StartsWith("sk-svcacct-", StringComparison.Ordinal)
        || t.StartsWith("sk-admin-", StringComparison.Ordinal)
        || (t.StartsWith("sk-", StringComparison.Ordinal) && t.Contains("T3BlbkFJ", StringComparison.Ordinal));

    /// <summary>A plain "sk-" key with no OpenAI marker: DeepSeek's, or another service's that starts the same way.</summary>
    public static bool IsPlainSk(string text)
    {
        string t = (text ?? "").Trim();
        return t.StartsWith("sk-", StringComparison.Ordinal) && Service(t) is null && !IsAddress(t);
    }

    [GeneratedRegex(@"^(localhost|[a-z0-9.-]+\.[a-z0-9-]+|\d{1,3}(\.\d{1,3}){3}|\[[0-9a-f:]+\]):\d{1,5}(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPort();
}

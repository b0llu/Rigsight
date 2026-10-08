namespace Rigsight.Core;

/// <summary>
/// What a page's search box matches: every word typed must be somewhere in what the row says (its name, its version,
/// the file it crashed in), in any order and whatever the capitals. Plain matching of what was typed, nothing guessed.
/// </summary>
public static class TextMatch
{
    /// <summary>The words of what was typed (none: nothing is being searched for).</summary>
    public static string[] Words(string? typed) => (typed ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether each of <paramref name="words"/> is in at least one of <paramref name="texts"/>.</summary>
    public static bool Has(string[] words, params string?[] texts)
    {
        foreach (string word in words)
        {
            bool found = false;
            foreach (string? text in texts)
                if (text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            if (!found) return false;
        }
        return true;
    }
}

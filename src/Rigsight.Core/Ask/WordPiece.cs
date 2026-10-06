using System.Globalization;
using System.Text;

namespace Rigsight.Core.Ask;

/// <summary>
/// The tokenizer the embedding model was trained with (BERT, uncased): lower case, accents off, punctuation apart, then
/// each word as the longest pieces the vocabulary has ("overheating" = "over", "##hea", "##ting").
/// </summary>
public sealed class WordPiece
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _unk, _cls, _sep;
    public const int MaxTokens = 64;

    public WordPiece(IEnumerable<string> vocabLines)
    {
        _vocab = new Dictionary<string, int>(32000, StringComparer.Ordinal);
        int i = 0;
        foreach (var line in vocabLines) _vocab[line.TrimEnd('\r', '\n')] = i++;
        _unk = _vocab["[UNK]"];
        _cls = _vocab["[CLS]"];
        _sep = _vocab["[SEP]"];
    }

    /// <summary>Whether the vocabulary has this as a whole word.</summary>
    public bool Contains(string word) => _vocab.ContainsKey(word);

    /// <summary>The token ids of a text, with the start and end marks, cut at <see cref="MaxTokens"/>.</summary>
    public long[] Encode(string text)
    {
        var ids = new List<long>(24) { _cls };
        foreach (var word in Words(text))
        {
            if (ids.Count >= MaxTokens - 1) break;
            Pieces(word, ids);
        }
        if (ids.Count > MaxTokens - 1) ids.RemoveRange(MaxTokens - 1, ids.Count - (MaxTokens - 1));
        ids.Add(_sep);
        return [.. ids];
    }

    private static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark || ch == 0 || ch == 0xFFFD || (char.IsControl(ch) && !char.IsWhiteSpace(ch))) continue;
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else if (IsPunctuation(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return ch.ToString();
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    // BERT counts every ASCII symbol as punctuation ($, ^, ` too), besides Unicode's own.
    private static bool IsPunctuation(char ch) =>
        (ch >= 33 && ch <= 47) || (ch >= 58 && ch <= 64) || (ch >= 91 && ch <= 96) || (ch >= 123 && ch <= 126) || char.IsPunctuation(ch) || char.IsSymbol(ch);

    private void Pieces(string word, List<long> ids)
    {
        if (word.Length > 100) { ids.Add(_unk); return; }
        int start = 0, first = ids.Count;
        while (start < word.Length)
        {
            int end = word.Length, id = -1;
            for (; end > start; end--)
            {
                string piece = start == 0 ? word[..end] : "##" + word[start..end];
                if (_vocab.TryGetValue(piece, out id)) break;
                id = -1;
            }
            if (id < 0)
            {
                ids.RemoveRange(first, ids.Count - first);
                ids.Add(_unk);
                return;
            }
            ids.Add(id);
            start = end;
        }
    }
}

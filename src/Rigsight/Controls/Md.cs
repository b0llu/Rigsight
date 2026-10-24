using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// The few Markdown marks an answer's text may carry, drawn as text (the person's call, 2026-10-24: the AI marks how its
/// words look and the app only draws it, instead of the app guessing which line is bold): **bold**, *italic* and `code`.
/// A [words](address) link shows its words only, never something to press: a page the AI read could slip one in, and
/// only a checked source pill opens a web page. Nothing else: no headings, pictures or HTML. A mark left open (an answer still arriving, or a
/// slip) never shows its stars. <c>&lt;Span ctl:Md.Text="{Binding Text}" /&gt;</c> fills the span.
/// </summary>
public static class Md
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Md), new PropertyMetadata(null, OnText));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string? value) => o.SetValue(TextProperty, value);

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static void OnText(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Span span) return;
        span.Inlines.Clear();
        foreach (var piece in Parse(e.NewValue as string ?? ""))
        {
            Inline inline = new Run(piece.Text);
            if (piece.Code) { inline.FontFamily = Mono; inline.FontSize = 12.5; }
            if (piece.Bold) inline.FontWeight = FontWeights.SemiBold;
            if (piece.Italic) inline.FontStyle = FontStyles.Italic;
            span.Inlines.Add(inline);
        }
    }

    /// <summary>One run of text and how it looks.</summary>
    public readonly record struct Piece(string Text, bool Bold = false, bool Italic = false, bool Code = false);

    /// <summary>The text cut into runs by its marks, in order.</summary>
    public static List<Piece> Parse(string text)
    {
        var pieces = new List<Piece>();
        Walk(text, false, false, pieces);
        return pieces;
    }

    /// <summary>The words alone (copying an answer, a step's text): the marks taken out, a link's words kept.</summary>
    public static string Plain(string text) => string.Concat(Parse(text).Select(p => p.Text));

    private static void Walk(string s, bool bold, bool italic, List<Piece> into)
    {
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { into.Add(new Piece(plain.ToString(), bold, italic)); plain.Clear(); } }
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            // `code`: as it is, nothing inside read as a mark.
            if (c == '`')
            {
                int end = s.IndexOf('`', i + 1);
                if (end > i + 1) { Flush(); into.Add(new Piece(s[(i + 1)..end], bold, italic, Code: true)); i = end + 1; continue; }
                i++; // a lone backtick: dropped
                continue;
            }
            // [words](address): the words only (see the summary).
            if (c == '[' && LinkAt(s, i) is var (words, _, after))
            {
                Flush();
                into.Add(new Piece(words, bold, italic));
                i = after;
                continue;
            }
            // **bold** or __bold__.
            if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
            {
                string mark = new(c, 2);
                int end = s.IndexOf(mark, i + 2, StringComparison.Ordinal);
                if (end > i + 2) { Flush(); Walk(s[(i + 2)..end], true, italic, into); i = end + 2; continue; }
                i += 2; // left open (still arriving): the stars never show
                continue;
            }
            // *italic* or _italic_: only around words ("5 * 3" and file_names stay as they are).
            if ((c == '*' || c == '_') && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
            {
                int end = s.IndexOf(c, i + 1);
                if (end > i + 1 && !char.IsWhiteSpace(s[end - 1]) && (end + 1 >= s.Length || !char.IsLetterOrDigit(s[end + 1])))
                {
                    Flush(); Walk(s[(i + 1)..end], bold, true, into); i = end + 1; continue;
                }
            }
            plain.Append(c);
            i++;
        }
        Flush();
    }

    private static (string Words, string Url, int After)? LinkAt(string s, int at)
    {
        int close = s.IndexOf("](", at + 1, StringComparison.Ordinal);
        if (close < 0) return null;
        int end = s.IndexOf(')', close + 2);
        if (end < 0 || s.IndexOf('\n', at, end - at) >= 0) return null;
        return (s[(at + 1)..close], s[(close + 2)..end].Trim(), end + 1);
    }
}

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
/// A drive scan's file by its label, "[file C1]", shows as that file's real name from the scan kept on this PC (the name
/// never went to the AI, 2026-10-27): a small chip that opens the Storage page; a label with no such file, plain words.
/// </summary>
public static partial class Md
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Md), new PropertyMetadata(null, OnText));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string? value) => o.SetValue(TextProperty, value);

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    /// <summary>A scan's file by drive, number and the scan's code (DriveScans.FileAt), set by the app.</summary>
    public static Func<string, int, string, (string Name, string Path, string Folder, long Size)?>? FileOf { get; set; }

    /// <summary>Opens the Storage page on a drive (a file's chip pressed), set by the app.</summary>
    public static Action<string>? OpenDrive { get; set; }

    /// <summary>A file an answer names, found on this PC: its drive, name, where it is and how big.</summary>
    public sealed record FileRef(string Drive, string Name, string Folder, long Size);

    private static void OnText(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Span span) return;
        span.Inlines.Clear();
        foreach (var piece in Parse(e.NewValue as string ?? ""))
        {
            if (piece.File is { } file) { span.Inlines.Add(Chip(file)); continue; }
            Inline inline = new Run(piece.Text);
            if (piece.Code) { inline.FontFamily = Mono; inline.FontSize = 12.5; }
            if (piece.Bold) inline.FontWeight = FontWeights.SemiBold;
            if (piece.Italic) inline.FontStyle = FontStyles.Italic;
            span.Inlines.Add(inline);
        }
    }

    /// <summary>One run of text and how it looks (or a file named on this PC).</summary>
    public readonly record struct Piece(string Text, bool Bold = false, bool Italic = false, bool Code = false, FileRef? File = null);

    /// <summary>A file's name as a chip in the line: the file kind's mark, the name, its place on hover; pressed, Storage opens.</summary>
    private static Inline Chip(FileRef f)
    {
        var good = (Brush)Application.Current.FindResource("GoodBrush");
        var name = new System.Windows.Controls.TextBlock { Text = f.Name, FontFamily = Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 };
        var mark = new InkCentered { Text = "\uE8A5", Font = "Segoe Fluent Icons, Segoe MDL2 Assets", FontSize = 11, Width = 11, Height = 16, Fill = good, Margin = new Thickness(0, 0, 5, 0) };
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(mark);
        row.Children.Add(name);
        var chip = new System.Windows.Controls.Border
        {
            Child = row, CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 3, 6, 0), Margin = new Thickness(1, 3, 1, -2),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, ((SolidColorBrush)good).Color.R, ((SolidColorBrush)good).Color.G, ((SolidColorBrush)good).Color.B)),
            Background = new SolidColorBrush(Color.FromArgb(0x1C, ((SolidColorBrush)good).Color.R, ((SolidColorBrush)good).Color.G, ((SolidColorBrush)good).Color.B)),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Core.Lang.T($"{Core.Units.Bytes(f.Size)} in {f.Folder}") + "\n" + Core.Lang.T("Named on this PC · click to open it in Storage"),
        };
        System.Windows.Automation.AutomationProperties.SetName(chip, f.Name);
        chip.MouseLeftButtonUp += (_, e) => { OpenDrive?.Invoke(f.Drive); e.Handled = true; };
        return new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center };
    }

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
            // [file C1 k3x]: a drive scan's file, named here from the scan on this PC (see the summary); from an earlier scan
            // (rescanned since), or without its scan's code, plain words, never another file's name.
            if (c == '[' && FileLabel().Match(s, i) is { Success: true } label && label.Index == i)
            {
                Flush();
                string drive = label.Groups[1].Value.ToUpperInvariant(), code = label.Groups[3].Value;
                int n = int.Parse(label.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                var found = code.Length == 0 ? null : FileOf?.Invoke(drive, n, code);
                if (found is { } f && System.IO.File.Exists(f.Path))
                    into.Add(new Piece(f.Name, bold, italic, File: new FileRef(drive, f.Name, f.Folder, f.Size)));
                else into.Add(new Piece(found is not null ? Core.Lang.T("a file no longer there") : code.Length > 0 ? Core.Lang.T("a file from an older scan") : Core.Lang.T("a file"), bold, italic));
                i += label.Length;
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

    [System.Text.RegularExpressions.GeneratedRegex(@"\[file ([A-Za-z])(\d{1,2})(?: ([0-9a-z]{3}))?\]")]
    private static partial System.Text.RegularExpressions.Regex FileLabel();

    private static (string Words, string Url, int After)? LinkAt(string s, int at)
    {
        int close = s.IndexOf("](", at + 1, StringComparison.Ordinal);
        if (close < 0) return null;
        int end = s.IndexOf(')', close + 2);
        if (end < 0 || s.IndexOf('\n', at, end - at) >= 0) return null;
        return (s[(at + 1)..close], s[(close + 2)..end].Trim(), end + 1);
    }
}

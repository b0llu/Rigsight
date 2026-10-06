using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Rigsight.Controls;

/// <summary>
/// Text with **strong** words for a TextBlock: <c>ctl:RichText.Text="{Binding Lead}"</c> shows the marked words in
/// medium weight and the full text colour, and the rest as it is (Ask's answers mark the verdict and the numbers that way).
/// </summary>
public static class RichText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(RichText), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var (text, strong) in Parts(e.NewValue as string ?? ""))
        {
            var run = new Run(text);
            if (strong)
            {
                // Strong words stand out by weight and by the full text colour, whatever softer colour the rest is in.
                run.FontWeight = FontWeights.Medium;
                run.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            }
            block.Inlines.Add(run);
        }
    }

    /// <summary>The text cut at its ** marks: every second piece is a strong one. A mark left open is shown as text.</summary>
    public static IEnumerable<(string Text, bool Strong)> Parts(string text)
    {
        var pieces = text.Split("**");
        if (pieces.Length % 2 == 0)
        {
            yield return (text, false);
            yield break;
        }
        for (int i = 0; i < pieces.Length; i++)
            if (pieces[i].Length > 0) yield return (pieces[i], i % 2 == 1);
    }
}

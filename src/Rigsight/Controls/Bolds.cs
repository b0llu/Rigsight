using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Rigsight.Controls;

/// <summary>
/// A grey line whose figures stand out (1.23.0, "Where it stuttered"): the parts marked **like this** are drawn semibold in
/// the text's own colour, the rest in the block's (muted) one. <c>&lt;TextBlock ctl:Bolds.Text="{Binding Text}" /&gt;</c>.
/// Only the marks: the words come from RigSight's own code, never from outside.
/// </summary>
public static class Bolds
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Bolds), new PropertyMetadata(null, OnText));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string? value) => o.SetValue(TextProperty, value);

    private static void OnText(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var (text, bold) in Pieces(e.NewValue as string ?? ""))
        {
            var run = new Run(text);
            if (bold)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            }
            block.Inlines.Add(run);
        }
    }

    /// <summary>The text in pieces, each bold or not; a mark left open stays plain and shows no stars.</summary>
    internal static List<(string Text, bool Bold)> Pieces(string text)
    {
        var pieces = new List<(string, bool)>();
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            // An odd piece is inside a pair of marks, unless it's the last and the pair never closed.
            bool bold = i % 2 == 1 && i < parts.Length - 1;
            pieces.Add((parts[i], bold));
        }
        return pieces;
    }
}

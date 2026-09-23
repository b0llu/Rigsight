using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// <c>ctl:TrimmedText.ToolTip="True"</c> on a TextBlock: cuts long text with "…" and shows the full text
/// on hover, but only when it was actually cut.
/// </summary>
public static class TrimmedText
{
    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
        "ToolTip", typeof(bool), typeof(TrimmedText), new PropertyMetadata(false, OnChanged));

    public static bool GetToolTip(DependencyObject o) => (bool)o.GetValue(ToolTipProperty);
    public static void SetToolTip(DependencyObject o, bool value) => o.SetValue(ToolTipProperty, value);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock tb || e.NewValue is not true) return;
        tb.TextTrimming = TextTrimming.CharacterEllipsis;
        // A placeholder so WPF raises ToolTipOpening; the real text is filled in (or the tip cancelled) there.
        tb.ToolTip = "";
        tb.ToolTipOpening += (_, args) =>
        {
            if (IsTrimmed(tb)) tb.ToolTip = tb.Text;
            else args.Handled = true;
        };
    }

    private static bool IsTrimmed(TextBlock tb)
    {
        var text = new FormattedText(tb.Text, CultureInfo.CurrentCulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        return text.WidthIncludingTrailingWhitespace > tb.ActualWidth + 0.5;
    }
}

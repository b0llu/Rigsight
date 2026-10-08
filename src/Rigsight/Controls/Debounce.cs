using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Rigsight.Controls;

/// <summary>
/// A search box that waits for a pause in the typing before it searches:
/// <c>ctl:Debounce.Text="{Binding Search}"</c> in place of <c>Text=</c>. Filtering a long list on every letter does the
/// work several times over for a word that's still being typed (the Timeline rebuilt its days at each key). What's
/// typed reaches the page a moment after the last key, and <see cref="IsPendingProperty"/> is on meanwhile, which
/// the search box's style shows as a turning arc. Clearing the box is passed on at once.
/// </summary>
public static class Debounce
{
    /// <summary>How long after the last key the search is done.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(250);

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Debounce),
        new FrameworkPropertyMetadata(Unset, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    // Not a text anyone can type: the page's first value, empty or not, is then always a change, which is where the box
    // is wired up.
    private const string Unset = "￿";

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);

    private static readonly DependencyPropertyKey IsPendingKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsPending", typeof(bool), typeof(Debounce), new PropertyMetadata(false));

    /// <summary>Something was typed that hasn't been searched for yet.</summary>
    public static readonly DependencyProperty IsPendingProperty = IsPendingKey.DependencyProperty;

    public static bool GetIsPending(DependencyObject o) => (bool)o.GetValue(IsPendingProperty);

    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached(
        "Timer", typeof(DispatcherTimer), typeof(Debounce));

    // The page's own value changed (it was cleared, or the box is new): the box shows it, with nothing to wait for.
    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        if (box.GetValue(TimerProperty) is null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, box.Dispatcher) { Interval = Wait };
            timer.Tick += (_, _) => Pass(box);
            box.SetValue(TimerProperty, timer);
            box.TextChanged += (_, _) => Typed(box);
            box.Unloaded += (_, _) => timer.Stop();
        }
        string text = e.NewValue as string ?? "";
        if (box.Text != text) box.Text = text;
    }

    private static void Typed(TextBox box)
    {
        var timer = (DispatcherTimer)box.GetValue(TimerProperty);
        timer.Stop();
        if (box.Text == GetText(box))
        {
            box.SetValue(IsPendingKey, false);
            return;
        }
        if (box.Text.Length == 0)
        {
            Pass(box);
            return;
        }
        box.SetValue(IsPendingKey, true);
        timer.Start();
    }

    private static void Pass(TextBox box)
    {
        ((DispatcherTimer)box.GetValue(TimerProperty)).Stop();
        SetText(box, box.Text);
        box.SetValue(IsPendingKey, false);
    }
}

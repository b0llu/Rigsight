using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Rigsight.Controls;

/// <summary>
/// A page's tab that is built the first time it shows, then kept: <see cref="Body"/> is laid out only once
/// <see cref="Shown"/> turns true. Settings built all nine of its tabs on each first visit, 0.5-0.8 s (2026-11-05).
/// Hidden until told otherwise: a plain ContentControl with its Visibility bound was laid out (and built) in the page's
/// first layout, before its binding said which tab was picked.
/// </summary>
public sealed class LazyTab : ContentControl
{
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(bool), typeof(LazyTab), new PropertyMetadata(false, (d, _) => ((LazyTab)d).Update()));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(DataTemplate), typeof(LazyTab), new PropertyMetadata(null, (d, _) => ((LazyTab)d).Update()));

    /// <summary>Whether the tab shows now (the page's own "Is…" property).</summary>
    public bool Shown
    {
        get => (bool)GetValue(ShownProperty);
        set => SetValue(ShownProperty, value);
    }

    /// <summary>What the tab holds, with the page's own view model as its data.</summary>
    public DataTemplate? Body
    {
        get => (DataTemplate?)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public LazyTab()
    {
        Focusable = false;
        IsTabStop = false;
        Visibility = Visibility.Collapsed;
        SetBinding(ContentProperty, new Binding());
    }

    private void Update()
    {
        Visibility = Shown ? Visibility.Visible : Visibility.Collapsed;
        if (Shown && ContentTemplate is null && Body is not null) ContentTemplate = Body;
    }
}

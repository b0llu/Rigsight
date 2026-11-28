using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// A list of rows of several kinds (All sensors: a card's top, its sensors, its foot, a device's card) whose rows are
/// recycled as it scrolls (1.23.0): a row that leaves the screen is handed the next item instead of being built new.
/// WPF keeps one pool of rows for every kind, so a row that held a sensor can be handed a card's top. Before it is,
/// it lets go of the sensor, so the sensor row's bindings never look at the card's top while its own look is chosen
/// (WPF chooses it by the item's type, at the next layout); a row handed an item of the same kind keeps its look and
/// only takes the new item.
/// </summary>
public sealed class RowList : ItemsControl
{
    protected override DependencyObject GetContainerForItemOverride() => new Row();

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ContentPresenter { Content: { } held } row && held.GetType() != item?.GetType())
            row.Content = null;
        base.PrepareContainerForItemOverride(element, item);
    }

    /// <summary>
    /// A row that only takes an item its look was built for. WPF hands a recycled row its next item (as its DataContext)
    /// before <see cref="PrepareContainerForItemOverride"/> runs, so a card's top could be bound to a sensor until its look
    /// changed, and every binding it has said so (seen in the page tests, 2026-11-28). Until the look for the new kind is
    /// chosen, the row holds no item at all.
    /// </summary>
    private sealed class Row : ContentPresenter
    {
        private Type? _builtFor;

        static Row() => DataContextProperty.OverrideMetadata(typeof(Row),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, null, (d, v) =>
                v is not null && ((Row)d)._builtFor is { } kind && !kind.IsInstanceOfType(v) ? null : v));

        protected override void OnTemplateChanged(DataTemplate oldTemplate, DataTemplate newTemplate)
        {
            base.OnTemplateChanged(oldTemplate, newTemplate);
            _builtFor = newTemplate?.DataType as Type;
            CoerceValue(DataContextProperty);
        }
    }
}

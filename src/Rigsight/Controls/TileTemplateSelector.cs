using System.Windows;
using System.Windows.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Controls;

/// <summary>
/// Picks a custom-page tile's template from resources: "Tile.&lt;kind&gt;" for what's inside a tile, or,
/// with <see cref="Chrome"/> set, the frame around it ("Tile.Frame", or "Tile.Placeholder" while dragging).
/// </summary>
public sealed class TileTemplateSelector : DataTemplateSelector
{
    public bool Chrome { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not TileViewModel tile || container is not FrameworkElement element) return null;
        string key = Chrome ? tile.IsPlaceholder ? "Tile.Placeholder" : "Tile.Frame" : "Tile." + tile.Kind;
        return element.TryFindResource(key) as DataTemplate ?? element.TryFindResource("Tile.Unknown") as DataTemplate;
    }
}

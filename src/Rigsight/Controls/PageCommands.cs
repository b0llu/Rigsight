using System.Windows.Input;

namespace Rigsight.Controls;

/// <summary>
/// Commands a page's header raises for the window to carry out. Routed up to the window (which binds them), so a page
/// needs nothing of the window's and a page shown on its own (in a test) simply has nothing to do.
/// </summary>
public static class PageCommands
{
    /// <summary>The page's Export button: the window opens that page's own Export box.</summary>
    public static readonly RoutedCommand Export = new(nameof(Export), typeof(PageCommands));
}

using System.Windows;

namespace Rigsight.Controls;

/// <summary>
/// The sidebar shows icons only (its edge handle, or Ctrl+B): set once on the sidebar and inherited by
/// everything in it, so each item's template drops its words and centres its icon, and its name becomes its hover.
/// </summary>
public static class SidebarLook
{
    public static readonly DependencyProperty NarrowProperty = DependencyProperty.RegisterAttached(
        "Narrow", typeof(bool), typeof(SidebarLook), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetNarrow(DependencyObject d) => (bool)d.GetValue(NarrowProperty);
    public static void SetNarrow(DependencyObject d, bool value) => d.SetValue(NarrowProperty, value);
}

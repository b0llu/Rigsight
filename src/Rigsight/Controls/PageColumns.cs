using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// Two columns on a page that has the room, one when it hasn't (2026-11-06: Overlay and Taskbar got the page width every
/// page has, the user: "the only 2 pages which have their own page width"). The left column is the page's main list;
/// the right one sits beside it, or under it in a narrow window. Optionally the right one follows the scroll, so what
/// it shows (Overlay's position preview) stays in view while the left one scrolls past.
/// </summary>
public static class PageColumns
{
    /// <summary>The gap between the two columns, and between cards stacked in one: the same as Home's.</summary>
    public const double Gap = 22;

    /// <summary>
    /// Puts <paramref name="right"/> beside <paramref name="left"/> (column 2 of <paramref name="grid"/>, <paramref name="rightWidth"/>
    /// wide) while the grid is at least <paramref name="wideFrom"/> wide, else under it (row 1, the full width). Returns
    /// whether it's two columns.
    /// </summary>
    /// <param name="stackedGap">The space over the right column when it's under the left one.</param>
    public static bool Arrange(Grid grid, FrameworkElement left, FrameworkElement right, double rightWidth, double wideFrom, double stackedGap = Gap)
    {
        bool wide = grid.ActualWidth >= wideFrom;
        grid.ColumnDefinitions[1].Width = new GridLength(wide ? Gap : 0);
        grid.ColumnDefinitions[2].Width = new GridLength(wide ? rightWidth : 0);
        Grid.SetRow(right, wide ? 0 : 1);
        Grid.SetColumn(right, wide ? 2 : 0);
        Grid.SetColumnSpan(right, wide ? 1 : 3);
        right.Margin = new Thickness(0, wide ? 0 : stackedGap, 0, 0);
        Grid.SetColumnSpan(left, wide ? 1 : 3);
        return wide;
    }

    /// <summary>
    /// The right column follows the scroll while two columns are shown: it starts where the columns start, moves down as
    /// the page scrolls, and stops where the left column ends, so it never runs past the page's content.
    /// </summary>
    public static void Follow(ScrollViewer scroll, Grid grid, FrameworkElement left, FrameworkElement right, TranslateTransform shift, bool wide)
    {
        if (!wide || scroll.Content is not Visual content || !grid.IsDescendantOf(content))
        {
            shift.Y = 0;
            return;
        }
        const double top = 16; // the space it keeps from the top of the page as it follows
        double columnsTop = grid.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
        double room = Math.Max(0, left.ActualHeight - right.ActualHeight);
        shift.Y = Math.Clamp(scroll.VerticalOffset + top - columnsTop, 0, room);
    }
}

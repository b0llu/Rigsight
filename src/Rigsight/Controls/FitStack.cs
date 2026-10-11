using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// A stack of rows that shows as many as fit the height it is given, and no part of one more (Home's recap card, which
/// stands beside a taller card: it fills to that card's height instead of ending halfway down). Each row has a
/// <see cref="RankProperty"/>: when not all fit, the lower ranks stay. The rows marked
/// <see cref="EssentialProperty"/> are the height it asks for; the rest only come in when there is room to spare.
/// </summary>
public sealed class FitStack : Panel
{
    public static readonly DependencyProperty RankProperty = DependencyProperty.RegisterAttached(
        "Rank", typeof(int), typeof(FitStack), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static int GetRank(DependencyObject o) => (int)o.GetValue(RankProperty);
    public static void SetRank(DependencyObject o, int value) => o.SetValue(RankProperty, value);

    public static readonly DependencyProperty EssentialProperty = DependencyProperty.RegisterAttached(
        "Essential", typeof(bool), typeof(FitStack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static bool GetEssential(DependencyObject o) => (bool)o.GetValue(EssentialProperty);
    public static void SetEssential(DependencyObject o, bool value) => o.SetValue(EssentialProperty, value);

    protected override Size MeasureOverride(Size available)
    {
        double height = 0, width = 0;
        var each = new Size(available.Width, double.PositiveInfinity);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(each);
            width = Math.Max(width, child.DesiredSize.Width);
            if (GetEssential(child)) height += child.DesiredSize.Height;
        }
        return new Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), height);
    }

    /// <summary>The rows that fit <paramref name="height"/>, lowest ranks first (given as heights and ranks; for tests).</summary>
    internal static bool[] Fit(IReadOnlyList<(double Height, int Rank)> rows, double height)
    {
        var shown = new bool[rows.Count];
        double used = 0;
        foreach (int i in Enumerable.Range(0, rows.Count).OrderBy(i => rows[i].Rank).ThenBy(i => i))
        {
            // A row that doesn't fit ends it: a later, shorter one would jump the queue.
            if (used + rows[i].Height > height + 0.5) break;
            shown[i] = true;
            used += rows[i].Height;
        }
        return shown;
    }

    protected override Size ArrangeOverride(Size final)
    {
        var rows = InternalChildren.Cast<UIElement>().Select(c => (c.DesiredSize.Height, GetRank(c))).ToList();
        var shown = Fit(rows, final.Height);
        double y = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            // A row left out is given no room and isn't drawn or hit: it is still there for the next height.
            child.Arrange(shown[i] ? new Rect(0, y, final.Width, rows[i].Height) : new Rect(0, 0, 0, 0));
            child.Opacity = shown[i] ? 1 : 0;
            child.IsHitTestVisible = shown[i];
            if (shown[i]) y += rows[i].Height;
        }
        return final;
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// Tiles of one size in as many columns as fit, each at least <see cref="MinTileWidth"/> wide, the row's width shared
/// out between them; the rest wrap onto more rows (the Temperatures page's board sensors and drives: three on one PC,
/// a dozen on another). No ragged right edge, and no tile alone stretched across the card.
/// </summary>
public sealed class TileWrap : Panel
{
    public static readonly DependencyProperty MinTileWidthProperty = DependencyProperty.Register(nameof(MinTileWidth), typeof(double), typeof(TileWrap),
        new FrameworkPropertyMetadata(170.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(TileWrap),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinTileWidth { get => (double)GetValue(MinTileWidthProperty); set => SetValue(MinTileWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    /// <summary>The most columns: a row of a few tiles keeps their size instead of each growing to a third of the card.</summary>
    public const int MaxColumns = 8;

    /// <summary>How many columns a width takes, and how wide each tile is then.</summary>
    internal (int Columns, double Width) Fit(double width, int count)
    {
        if (count == 0 || double.IsInfinity(width)) return (Math.Max(1, count), MinTileWidth);
        int fit = Math.Clamp((int)((width + Gap) / (MinTileWidth + Gap)), 1, MaxColumns);
        // Fewer tiles than fit: they keep the width a full row's would have (at least four across), not the whole card.
        int columns = Math.Max(Math.Min(fit, count), Math.Min(fit, 4));
        return (columns, Math.Max(1, (width - Gap * (columns - 1)) / columns));
    }

    protected override Size MeasureOverride(Size available)
    {
        var (columns, tile) = Fit(available.Width, InternalChildren.Count);
        double height = 0, row = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Measure(new Size(tile, double.PositiveInfinity));
            row = Math.Max(row, InternalChildren[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == InternalChildren.Count - 1)
            {
                height += row + (height > 0 ? Gap : 0);
                row = 0;
            }
        }
        return new Size(double.IsInfinity(available.Width) ? columns * tile + Gap * (columns - 1) : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (columns, tile) = Fit(final.Width, InternalChildren.Count);
        double y = 0;
        for (int start = 0; start < InternalChildren.Count; start += columns)
        {
            int end = Math.Min(start + columns, InternalChildren.Count);
            double row = 0;
            for (int i = start; i < end; i++) row = Math.Max(row, InternalChildren[i].DesiredSize.Height);
            for (int i = start; i < end; i++) InternalChildren[i].Arrange(new Rect((i - start) * (tile + Gap), y, tile, row));
            y += row + Gap;
        }
        return final;
    }
}

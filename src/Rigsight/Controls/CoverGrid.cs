using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// The Games library's covers: as many columns as fit at <see cref="MinItemWidth"/> or more, every column the same
/// width so each row fills the width with no gap left at its end, with <see cref="Gap"/> between columns and
/// <see cref="RowGap"/> between rows.
/// </summary>
public sealed class CoverGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(CoverGrid), new FrameworkPropertyMetadata(132.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(CoverGrid), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(CoverGrid), new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

    /// <summary>The height of one row of covers, as last laid out (the library box shows two and a half of them).</summary>
    public double RowHeight { get; private set; }

    private (int Columns, double Width) Columns(double available)
    {
        if (double.IsInfinity(available) || available <= 0) available = MinItemWidth * 6 + Gap * 5;
        int cols = Math.Max(1, (int)Math.Floor((available + Gap) / (MinItemWidth + Gap)));
        return (cols, (available - Gap * (cols - 1)) / cols);
    }

    protected override Size MeasureOverride(Size available)
    {
        var (cols, width) = Columns(available.Width);
        double rowH = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            rowH = Math.Max(rowH, child.DesiredSize.Height);
        }
        RowHeight = rowH;
        int rows = (InternalChildren.Count + cols - 1) / cols;
        double w = double.IsInfinity(available.Width) ? cols * width + (cols - 1) * Gap : available.Width;
        return new Size(w, rows == 0 ? 0 : rows * rowH + (rows - 1) * RowGap);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (cols, width) = Columns(final.Width);
        double rowH = RowHeight;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int r = i / cols, c = i % cols;
            InternalChildren[i].Arrange(new Rect(c * (width + Gap), r * (rowH + RowGap), width, rowH));
        }
        return final;
    }
}
